#ifndef ELECTRIC_DREAMS_VIRTUAL_MESH_INCLUDED
#define ELECTRIC_DREAMS_VIRTUAL_MESH_INCLUDED

// Include after URP Core/Lighting. Material functions continue to consume LOCAL
// vertices; this explicit instance context replaces only their object transforms.
// URP view, projection, lighting and shadow matrices remain untouched.
#include "Packages/com.unity.virtualmesh/Runtime/Resources/VirtualMeshModelGeometry.hlsl"
#include "Packages/com.unity.virtualmesh/Runtime/Resources/VirtualMeshModelMotion.hlsl"

// Supplied once per model batch, in mesh-local space. Renderer bounds center is
// not generally the transform origin or an individual cluster's bounds center.
static VMInstance edVMInstance;
static VMModelData edVMModel;
static float edVMTime;

void EDVMSetupInstance(uint instanceIndex)
{
    edVMInstance = _VMInstances[instanceIndex];
    edVMModel = VMGetModelData(instanceIndex);
    edVMTime = _VMGraphDraw != 0 ? _VMCurrentTime : _Time.y;
}

// Used only while evaluating the previous vertex position, never for normal
// transforms or current-frame alpha. The previous inverse matrix is unnecessary.
void EDVMSetupPreviousInstance(uint instanceIndex)
{
    EDVMSetupInstance(instanceIndex);
    edVMInstance.objectToWorld = _VMPreviousObjectToWorld[instanceIndex];
    edVMTime = _VMPreviousTime;
}

float EDVMGetTime() { return edVMTime; }

VMVertex EDVMFetchLocalVertex(uint vertexID, uint instanceID, out uint instanceIndex)
{
    VMVertex vertex = VMFetchLocalVertex(vertexID, instanceID, instanceIndex);
    EDVMSetupInstance(instanceIndex);
    // Preserve UV0, UV1 and all four vertex-color channels. In particular,
    // Hornbeam uses alpha for health/season and authored light-influence masks.
    return vertex;
}

float4x4 EDVMGetObjectToWorldMatrix()
{
    return edVMInstance.objectToWorld;
}

float EDVMGetOddNegativeScale()
{
    return edVMInstance.parameters.z < 0 ? -1.0 : 1.0;
}

float3 EDVMTransformObjectToWorld(float3 positionOS)
{
    return mul(edVMInstance.objectToWorld, float4(positionOS, 1.0)).xyz;
}

float4 EDVMTransformObjectToHClip(float3 positionOS)
{
    return TransformWorldToHClip(EDVMTransformObjectToWorld(positionOS));
}

float3 EDVMTransformObjectToWorldNormal(float3 normalOS)
{
    // Inverse transpose also handles nonuniform scale and parent shear.
    return SafeNormalize(mul(normalOS, (float3x3)edVMInstance.worldToObject));
}

VertexNormalInputs EDVMGetVertexNormalInputs(float3 normalOS, float4 tangentOS)
{
    VertexNormalInputs result;
    result.normalWS = EDVMTransformObjectToWorldNormal(normalOS);
    result.tangentWS = SafeNormalize(mul((float3x3)edVMInstance.objectToWorld, tangentOS.xyz));
    result.bitangentWS = cross(result.normalWS, result.tangentWS) * (tangentOS.w * EDVMGetOddNegativeScale());
    return result;
}

float3 EDVMRendererBoundsCenter()
{
    return EDVMTransformObjectToWorld(edVMModel.boundsCenter.xyz);
}

#endif
