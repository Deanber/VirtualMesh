#ifndef VIRTUAL_MESH_MODEL_FETCH
#define VIRTUAL_MESH_MODEL_FETCH
#include "VirtualMeshModelGeometry.hlsl"

// RenderPrimitivesIndirect has no object matrix: hand world-space attributes
// to URP's normal vertex paths, which consequently use identity object matrices.
VMVertex VMFetchVertex(uint vertexId, uint instanceId)
{
    uint instanceIndex;
    VMVertex v = VMFetchLocalVertex(vertexId, instanceId, instanceIndex);
    VMInstance instance = _VMInstances[instanceIndex];
    v.position = mul(instance.objectToWorld, float4(v.position, 1)).xyz;
    v.normal = SafeNormalize(mul(v.normal, (float3x3)instance.worldToObject));
    v.tangent.xyz = SafeNormalize(mul((float3x3)instance.objectToWorld, v.tangent.xyz));
    v.tangent.w *= instance.parameters.z < 0 ? -1 : 1;
    return v;
}
#endif
