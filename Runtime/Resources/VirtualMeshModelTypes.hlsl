#ifndef VIRTUAL_MESH_MODEL_TYPES
#define VIRTUAL_MESH_MODEL_TYPES
struct VMVertex
{
    float3 position;
    float3 normal;
    float4 tangent;
    float2 uv0;
    float2 uv1;
    float4 color;
};
struct VMCluster
{
    float3 center;
    float radius;
    uint indexStart;
    uint indexCount;
    uint submesh;
    uint type;
    float error;
    float parentError;
    float triangleSpacing;
    float parentTriangleSpacing;
};
struct VMInstance
{
    float4x4 objectToWorld;
    float4x4 worldToObject;
    float4 parameters; // conservative stretch, pixel error, determinant sign, target triangle pixels (0 disables)
};
#endif
