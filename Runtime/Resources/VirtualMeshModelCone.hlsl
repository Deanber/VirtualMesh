#ifndef VIRTUAL_MESH_MODEL_CONE_INCLUDED
#define VIRTUAL_MESH_MODEL_CONE_INCLUDED

struct VMCullingData
{
    float4 axisCutoff;
    float4 centerRadius;
};

// Call AFTER choosing the LOD. The caller must disable this optimization for
// two-sided materials/shadows, nonzero vertex deformation, or unknown light sets.
// observerOS = mul(worldToObject,float4(camera/light position,1)).xyz;
// forwardOS = mul((float3x3)worldToObject,camera/light forward). It is a RAY
// direction, not the surface-to-light direction. This object-space test handles
// nonuniform scaling and shear without attempting to transform a circular cone.
// Fetch already compensates negative determinants by swapping triangle winding;
// do not flip the cone again for mirrored instances.
bool VMClusterConeReject(VMCullingData data, float3 observerOS, float3 forwardOS,
    bool orthographic, uint cullMode)
{
    // Unity CullMode: Off=0, Front=1, Back=2. Unknown values are conservative.
    if (cullMode != 1 && cullMode != 2) return false;
    float cutoff = data.axisCutoff.w;
    float axisLengthSquared = dot(data.axisCutoff.xyz, data.axisCutoff.xyz);
    if (!(cutoff >= 0.0 && cutoff < 1.0 && axisLengthSquared > 0.999 &&
          axisLengthSquared < 1.001 && data.centerRadius.w >= 0.0)) return false;
    float3 axis = data.axisCutoff.xyz * rsqrt(axisLengthSquared);
    if (cullMode == 1) axis = -axis;
    float3 ray = orthographic ? forwardOS : data.centerRadius.xyz - observerOS;
    float lengthSquared = dot(ray, ray);
    if (!(lengthSquared > 1e-20 && lengthSquared < 3.402823466e+38)) return false;
    float rayLength = sqrt(lengthSquared);
    float radius = orthographic ? 0.0 : data.centerRadius.w;
    // The sphere form supports both Front and Back with the same data. Merely
    // negating an apex-based cone axis would NOT be valid for Front culling.
    float margin = 1e-5 * max(1.0, rayLength + radius);
    return dot(ray, axis) > cutoff * rayLength + radius + margin;
}

#endif
