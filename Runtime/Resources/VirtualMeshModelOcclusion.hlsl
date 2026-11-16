#ifndef VIRTUAL_MESH_MODEL_OCCLUSION_INCLUDED
#define VIRTUAL_MESH_MODEL_OCCLUSION_INCLUDED

// The caller binds one camera's matching depth/VP pair. Previous-frame depth is
// prediction only: rejected candidates must be saved and retested against the
// current first-pass depth before they may be discarded for this frame.
Texture2D<float> _VMHiZTexture;
float4x4 _VMHiZViewProjection; // jittered GPU VP that produced this depth texture
float4 _VMHiZNdcToUV;         // uv = ndc.xy * xy + zw; native texture Y flip is explicit
float2 _VMHiZClipToDepth;     // deviceDepth = clip.z / clip.w * x + y
float4 _VMHiZNearPlaneWS;     // normalized plane, positive toward visible space
int2 _VMHiZSize;
int _VMHiZMipCount;
int _VMHiZReversedZ;
int _VMHiZValid;              // zero for camera cuts, resize, invalid history or unsupported projections
float _VMHiZDepthBias;        // device-depth safety margin; recommended 1e-5
float _VMHiZPixelPadding;     // base-level pixel expansion; recommended at least one pixel

float VMHiZClearDepth()
{
    return _VMHiZReversedZ != 0 ? 0.0 : 1.0;
}

float VMHiZFarthestDepth(float a, float b)
{
    return _VMHiZReversedZ != 0 ? min(a, b) : max(a, b);
}

float VMHiZSanitizeDepth(float depth)
{
    // Unknown depth must not become an occluder, including NaN, infinity or an
    // unexpected unnormalized depth source.
    return isfinite(depth) && depth >= 0.0 && depth <= 1.0 ? depth : VMHiZClearDepth();
}

bool VMHiZSphereOccluded(float3 centerWS, float radiusWS)
{
    if (_VMHiZValid == 0 || _VMHiZMipCount <= 0 || any(_VMHiZSize <= 0) ||
        !all(isfinite(centerWS)) || !isfinite(radiusWS) || radiusWS < 0.0)
        return false;

    float planeLength = length(_VMHiZNearPlaneWS.xyz);
    if (!isfinite(planeLength) || abs(planeLength - 1.0) > 0.01)
        return false;
    float radius = radiusWS + max(0.0001, radiusWS * 0.00001);
    if (dot(_VMHiZNearPlaneWS, float4(centerWS, 1.0)) <= radius)
        return false;

    float2 uvMin = float2(1.0, 1.0), uvMax = float2(0.0, 0.0);
    float nearestDepth = _VMHiZReversedZ != 0 ? 0.0 : 1.0;
    // A world-axis box encloses the sphere. With positive clip.w, extrema of
    // its perspective projection occur at vertices. Projecting all 8 corners
    // also supports orthographic, asymmetric and jittered projections without
    // assuming a particular camera axis or FOV.
    [unroll] for (uint corner = 0; corner < 8; corner++)
    {
        float3 sign = float3((corner & 1u) != 0u ? 1.0 : -1.0,
                            (corner & 2u) != 0u ? 1.0 : -1.0,
                            (corner & 4u) != 0u ? 1.0 : -1.0);
        float4 clip = mul(_VMHiZViewProjection, float4(centerWS + sign * radius, 1.0));
        if (!all(isfinite(clip)) || clip.w <= 0.000001)
            return false;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * _VMHiZNdcToUV.xy + _VMHiZNdcToUV.zw;
        float depth = ndc.z * _VMHiZClipToDepth.x + _VMHiZClipToDepth.y;
        if (!all(isfinite(uv)) || !isfinite(depth) || depth <= 0.0 || depth >= 1.0)
            return false;
        uvMin = min(uvMin, uv);
        uvMax = max(uvMax, uv);
        nearestDepth = _VMHiZReversedZ != 0 ? max(nearestDepth, depth) : min(nearestDepth, depth);
    }

    float paddingPixels = isfinite(_VMHiZPixelPadding) ? max(1.0, _VMHiZPixelPadding) : 1.0;
    float2 paddingUV = paddingPixels / float2(_VMHiZSize);
    uvMin -= paddingUV;
    uvMax += paddingUV;
    // Do not clamp partly off-screen boxes: unknown pixels beyond the texture
    // are not evidence of occlusion. Frustum culling is a separate decision.
    if (any(uvMin < 0.0) || any(uvMax >= 1.0) || any(uvMin > uvMax))
        return false;

    int mip = 0;
    int2 mipSize = _VMHiZSize;
    int2 lo = int2(floor(uvMin * float2(mipSize)));
    int2 hi = int2(floor(uvMax * float2(mipSize)));
#if defined(VM_HIZ_FINE_4X4)
    const int maxTexelSpan = 3; // Four contiguous texels per axis, not four corners.
#else
    const int maxTexelSpan = 1;
#endif
    // Sampling four corner texels is safe only when those four texels cover
    // the entire rectangle. ceil(log2(rectSize)) alone misses alignment cases.
    [loop] while (any(hi - lo > maxTexelSpan) && mip + 1 < _VMHiZMipCount)
    {
        mip++;
        mipSize = max(int2(1, 1), _VMHiZSize >> mip);
        lo = int2(floor(uvMin * float2(mipSize)));
        hi = int2(floor(uvMax * float2(mipSize)));
    }
    if (any(hi - lo > maxTexelSpan) || any(lo < 0) || any(hi >= mipSize))
        return false;

#if defined(VM_HIZ_FINE_4X4)
    // Clamp extra taps on short axes to the last covered texel. Every texel
    // in [lo, hi] is loaded; no interior sample can be skipped. Repeated taps
    // do not change min/max. Keep unknown depth's conservative clear value.
    float farthest = _VMHiZReversedZ != 0 ? 1.0 : 0.0;
    [unroll] for (int y = 0; y < 4; y++)
    {
        [unroll] for (int x = 0; x < 4; x++)
        {
            int2 samplePixel = min(lo + int2(x, y), hi);
            farthest = VMHiZFarthestDepth(farthest,
                VMHiZSanitizeDepth(_VMHiZTexture.Load(int3(samplePixel, mip))));
        }
    }
#else
    float farthest = VMHiZSanitizeDepth(_VMHiZTexture.Load(int3(lo, mip)));
    farthest = VMHiZFarthestDepth(farthest, VMHiZSanitizeDepth(_VMHiZTexture.Load(int3(hi.x, lo.y, mip))));
    farthest = VMHiZFarthestDepth(farthest, VMHiZSanitizeDepth(_VMHiZTexture.Load(int3(lo.x, hi.y, mip))));
    farthest = VMHiZFarthestDepth(farthest, VMHiZSanitizeDepth(_VMHiZTexture.Load(int3(hi, mip))));
#endif
    float bias = isfinite(_VMHiZDepthBias) ? max(0.0000001, _VMHiZDepthBias) : 0.00001;
    // Strict separation prevents self-occlusion. Clear-depth texels naturally
    // fail this comparison and therefore cannot hide an object.
    return _VMHiZReversedZ != 0 ? nearestDepth < farthest - bias : nearestDepth > farthest + bias;
}

#endif
