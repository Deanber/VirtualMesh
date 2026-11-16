#ifndef VIRTUAL_MESH_IMPOSTOR_RECONSTRUCTION_INCLUDED
#define VIRTUAL_MESH_IMPOSTOR_RECONSTRUCTION_INCLUDED

// Shared by color, depth, motion, shadow and the standalone GPU reference test.
// Coverage uses an exact point load. Continuous, fully covered neighborhoods may
// reconstruct their height by coherent filtering; empty texels never contribute.
struct VMImpostorView { float4 rightExtent, upExtent, directionDepth; };
struct VMImpostorHit
{
    float3 positionOS, normalOS;
    float2 uv;
    float3 albedo;
    float roughness;
};
StructuredBuffer<VMImpostorView> _VMImpostorViews;
Texture2DArray<float4> _VMImpostorAlbedoCoverage;
Texture2DArray<float4> _VMImpostorNormalRoughness;
Texture2DArray<float> _VMImpostorDepth;
uint _VMImpostorViewCount;
float4 _VMImpostorBoundsCenter, _VMImpostorBoundsExtents;
float4 _VMImpostorTextureSize;

uint VMImpostorSelectView(float3 observerOS)
{
    uint selected = 0; float best = -3.402823466e+38;
    for (uint i = 0; i < _VMImpostorViewCount; i++)
    {
        float score = dot(observerOS, _VMImpostorViews[i].directionDepth.xyz);
        if (score > best) { best = score; selected = i; }
    }
    return selected;
}

bool VMImpostorSample(float2 uv, uint view, out float depth, out float4 color, out float4 normal)
{
    depth = 0; color = 0; normal = 0;
    // The slab entry can lie exactly on the capture rectangle's inclusive edge.
    // Sample its edge texel; rejecting uv==1 would also reject later valid interior hits.
    if (any(uv < -1e-6) || any(uv > 1.000001) || !all(isfinite(uv))) return false;
    uv = saturate(uv);
    int2 pixel = min(int2(floor(uv * _VMImpostorTextureSize.xy)), int2(_VMImpostorTextureSize.xy)-1);
    color = _VMImpostorAlbedoCoverage.Load(int4(pixel, view, 0));
    // Zero depth may be a real hit at the capture's far plane. Only coverage determines validity.
    if (color.a < .5 || !all(isfinite(color))) return false;
    depth = _VMImpostorDepth.Load(int4(pixel, view, 0));
    normal = _VMImpostorNormalRoughness.Load(int4(pixel, view, 0));
    if (!isfinite(depth) || depth < 0 || depth > 1 || !all(isfinite(normal))) return false;
    // A point-constant heightfield can oscillate across a texel boundary even on
    // a perfectly continuous oblique plane. Interpolate only a demonstrated local
    // surface: all four texels covered, similar normals, and no large depth jump.
    float2 texel = uv * _VMImpostorTextureSize.xy - .5;
    int2 lo = int2(floor(texel)), hi = lo + 1;
    if (all(lo >= 0) && all(hi < int2(_VMImpostorTextureSize.xy)))
    {
        float4 c00 = _VMImpostorAlbedoCoverage.Load(int4(lo,view,0));
        float4 c10 = _VMImpostorAlbedoCoverage.Load(int4(hi.x,lo.y,view,0));
        float4 c01 = _VMImpostorAlbedoCoverage.Load(int4(lo.x,hi.y,view,0));
        float4 c11 = _VMImpostorAlbedoCoverage.Load(int4(hi,view,0));
        if (min(min(c00.a,c10.a),min(c01.a,c11.a)) >= .5)
        {
            float4 depths = float4(_VMImpostorDepth.Load(int4(lo,view,0)),_VMImpostorDepth.Load(int4(hi.x,lo.y,view,0)),
                _VMImpostorDepth.Load(int4(lo.x,hi.y,view,0)),_VMImpostorDepth.Load(int4(hi,view,0)));
            float4 n00 = _VMImpostorNormalRoughness.Load(int4(lo,view,0));
            float4 n10 = _VMImpostorNormalRoughness.Load(int4(hi.x,lo.y,view,0));
            float4 n01 = _VMImpostorNormalRoughness.Load(int4(lo.x,hi.y,view,0));
            float4 n11 = _VMImpostorNormalRoughness.Load(int4(hi,view,0));
            VMImpostorView basis = _VMImpostorViews[view];
            float gapLimit = 4 * max(basis.rightExtent.w * _VMImpostorTextureSize.z,
                basis.upExtent.w * _VMImpostorTextureSize.w) / basis.directionDepth.w;
            float gap = max(max(depths.x,depths.y),max(depths.z,depths.w))-min(min(depths.x,depths.y),min(depths.z,depths.w));
            float3 baseNormal = normal.xyz * 2 - 1;
            bool consistent = dot(baseNormal,n00.xyz*2-1)>.5 && dot(baseNormal,n10.xyz*2-1)>.5 &&
                dot(baseNormal,n01.xyz*2-1)>.5 && dot(baseNormal,n11.xyz*2-1)>.5;
            if (all(isfinite(depths)) && all(depths>=0) && all(depths<=1) && gap <= gapLimit && consistent)
            {
                float2 f = frac(texel);
                depth = lerp(lerp(depths.x,depths.y,f.x),lerp(depths.z,depths.w,f.x),f.y);
                color = lerp(lerp(c00,c10,f.x),lerp(c01,c11,f.x),f.y);
                normal = lerp(lerp(n00,n10,f.x),lerp(n01,n11,f.x),f.y);
            }
        }
    }
    return true;
}

bool VMImpostorSlab(float origin, float direction, float extent, inout float nearT, inout float farT)
{
    if (abs(direction) < 1e-8) return abs(origin) <= extent;
    float a = (-extent - origin) / direction, b = (extent - origin) / direction;
    nearT = max(nearT, min(a, b)); farT = min(farT, max(a, b));
    return nearT <= farT;
}

bool VMImpostorSolve(float3 origin, float3 direction, VMImpostorView view, uint viewIndex,
    float nearT, float farT, float seed, out float hitT, out float2 hitUV, out float4 color, out float4 normal)
{
    hitT = seed; hitUV = 0; color = 0; normal = 0;
    float depthTolerance = max(1e-6, view.directionDepth.w * _VMImpostorTextureSize.z * .5);
    [loop] for (uint iteration = 0; iteration < 10; iteration++)
    {
        float3 rayPoint = origin + direction * hitT;
        float2 uv = float2(rayPoint.x / (2 * view.rightExtent.w), rayPoint.y / (2 * view.upExtent.w)) + .5;
        float captured;
        if (!VMImpostorSample(uv, viewIndex, captured, color, normal)) return false;
        float height = (captured - .5) * 2 * view.directionDepth.w;
        float nextT = (height - origin.z) / direction.z;
        float roundoff = max(1e-5, (farT - nearT) * 1e-5);
        if (!isfinite(nextT) || nextT < nearT-roundoff || nextT > farT+roundoff) return false;
        nextT = clamp(nextT,nearT,farT);
        if (abs(rayPoint.z - height) <= depthTolerance)
        {
            // Confirm the corrected ray point stays on the same valid heightfield surface.
            float3 corrected = origin + direction * nextT;
            hitUV = float2(corrected.x / (2 * view.rightExtent.w), corrected.y / (2 * view.upExtent.w)) + .5;
            if (!VMImpostorSample(hitUV, viewIndex, captured, color, normal)) return false;
            if (abs(corrected.z - (captured - .5) * 2 * view.directionDepth.w) > depthTolerance) return false;
            hitT = nextT; return true;
        }
        hitT = nextT;
    }
    // Discontinuous/steep surfaces may fail fixed-point iteration. Unknown rays stay empty;
    // this representation never supplies HiZ occluder coverage.
    return false;
}

bool VMImpostorIntersect(float3 rayOriginOS, float3 rayDirectionOS, uint viewIndex, out VMImpostorHit hit)
{
    hit = (VMImpostorHit)0;
    if (viewIndex >= _VMImpostorViewCount || !all(isfinite(rayOriginOS)) || !all(isfinite(rayDirectionOS))) return false;
    float lengthSq = dot(rayDirectionOS, rayDirectionOS);
    if (lengthSq < 1e-12) return false;
    rayDirectionOS *= rsqrt(lengthSq);
    VMImpostorView view = _VMImpostorViews[viewIndex];
    float3 relative = rayOriginOS - _VMImpostorBoundsCenter.xyz;
    float3 origin = float3(dot(relative, view.rightExtent.xyz), dot(relative, view.upExtent.xyz), dot(relative, view.directionDepth.xyz));
    float3 direction = float3(dot(rayDirectionOS, view.rightExtent.xyz), dot(rayDirectionOS, view.upExtent.xyz), dot(rayDirectionOS, view.directionDepth.xyz));
    if (abs(direction.z) < .0001) return false;
    float nearT = 0, farT = 3.402823466e+38;
    if (!VMImpostorSlab(origin.x, direction.x, view.rightExtent.w, nearT, farT) ||
        !VMImpostorSlab(origin.y, direction.y, view.upExtent.w, nearT, farT) ||
        !VMImpostorSlab(origin.z, direction.z, view.directionDepth.w, nearT, farT)) return false;
    float t; float2 uv; float4 color, normal;
    bool found = VMImpostorSolve(origin, direction, view, viewIndex, nearT, farT, nearT, t, uv, color, normal);
    if (!found)
    {
        float middle = clamp(-origin.z / direction.z, nearT, farT);
        found = VMImpostorSolve(origin, direction, view, viewIndex, nearT, farT, middle, t, uv, color, normal);
    }
    if (!found && direction.z > 0)
        found = VMImpostorSolve(origin, direction, view, viewIndex, nearT, farT, farT, t, uv, color, normal);
    if (!found) return false;
    float3 n = normal.xyz * 2 - 1;
    if (dot(n, n) < 1e-8) return false;
    hit.positionOS = rayOriginOS + rayDirectionOS * t;
    hit.normalOS = normalize(n); hit.uv = uv; hit.albedo = color.rgb; hit.roughness = saturate(normal.a);
    return true;
}
#endif
