#ifndef VIRTUAL_MESH_MODEL_GEOMETRY_INCLUDED
#define VIRTUAL_MESH_MODEL_GEOMETRY_INCLUDED
#include "VirtualMeshModelTypes.hlsl"
#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
#include "UnityIndirect.cginc"

struct VMModelData
{
    uint4 offsets; // vertex, index, cluster, cluster count
    float4 boundsCenter;
};

StructuredBuffer<VMVertex> _VMVertices;
StructuredBuffer<uint> _VMIndices;
StructuredBuffer<VMCluster> _VMClusters;
StructuredBuffer<VMInstance> _VMInstances;
StructuredBuffer<uint2> _VMVisibleClusters;
StructuredBuffer<uint> _VMVisibleRangeCounters;
StructuredBuffer<VMModelData> _VMModelTable;
StructuredBuffer<uint> _VMInstanceModelIds;
int _VMUseGeometryAtlas;
int _VMGraphDraw;
int _VMUseRecoveredRange;
float4 _VMModelBoundsCenter;

#if defined(VM_SPLIT_VERTEX_STREAMS) || defined(VM_DYNAMIC_VERTEX_STREAMS)
// Runtime atlas representation only; serialized VMVertex remains 72 bytes.
struct VMVertexPositionUvColor { float3 position; float2 uv0; float colorR; }; // 24
struct VMVertexNormalTangent { float3 normal; float4 tangent; };             // 28
struct VMVertexCold { float2 uv1; float3 colorGba; };                        // 20
StructuredBuffer<VMVertexPositionUvColor> _VMGeometryAtlasPositionUvColor;
StructuredBuffer<VMVertexNormalTangent> _VMGeometryAtlasNormalTangent;
StructuredBuffer<VMVertexCold> _VMGeometryAtlasCold;
int _VMGeometryAtlasSplit;
#endif

#if defined(VM_SPLIT_VERTEX_STREAMS) || defined(VM_DYNAMIC_VERTEX_STREAMS)
VMVertex VMReadSplitVertex(uint vertexIndex)
{
    VMVertexPositionUvColor hot = _VMGeometryAtlasPositionUvColor[vertexIndex];
    VMVertexNormalTangent frame = _VMGeometryAtlasNormalTangent[vertexIndex];
    VMVertexCold cold = _VMGeometryAtlasCold[vertexIndex];
    VMVertex vertex;
    vertex.position = hot.position;
    vertex.normal = frame.normal;
    vertex.tangent = frame.tangent;
    vertex.uv0 = hot.uv0;
    vertex.uv1 = cold.uv1;
    vertex.color = float4(hot.colorR, cold.colorGba);
    return vertex;
}
#endif

VMVertex VMReadVertex(uint vertexIndex)
{
#if defined(VM_SPLIT_VERTEX_STREAMS)
    // Only actual split-atlas material clones select this variant. The legacy
    // pool is independent and always clears both layout keywords after sync.
    return VMReadSplitVertex(vertexIndex);
#else
#if defined(VM_DYNAMIC_VERTEX_STREAMS)
    // Diagnostic only: preserve the original branch for a same-atlas A/B/A.
    [branch] if (_VMUseGeometryAtlas != 0 && _VMGeometryAtlasSplit != 0)
        return VMReadSplitVertex(vertexIndex);
#endif
    return _VMVertices[vertexIndex];
#endif
}


VMModelData VMGetModelData(uint instanceIndex)
{
    VMModelData model = (VMModelData)0;
    model.boundsCenter = _VMModelBoundsCenter;
    if (_VMUseGeometryAtlas != 0) model = _VMModelTable[_VMInstanceModelIds[instanceIndex]];
    return model;
}

// Both the ordinary RenderPrimitivesIndirect submission and the RenderGraph
// DrawProceduralIndirect submission share geometry. The latter has no automatic
// unity_IndirectDrawArgs binding and must use SV IDs directly (start offsets 0).
VMVertex VMFetchLocalVertex(uint vertexID, uint instanceID, out uint instanceIndex)
{
    uint visibleIndex = instanceID;
    uint corner = vertexID;
    if (_VMGraphDraw == 0)
    {
        InitIndirectDrawArgs(0);
        visibleIndex = GetIndirectInstanceID(instanceID);
        corner = GetIndirectVertexID(vertexID);
    }
    else if (_VMUseRecoveredRange != 0)
    {
        visibleIndex += _VMVisibleRangeCounters[0];
    }
    uint2 visible = _VMVisibleClusters[visibleIndex];
    VMCluster cluster = _VMClusters[visible.x]; // Visible.x is global in atlas mode.
    instanceIndex = visible.y;
    VMInstance instance = _VMInstances[instanceIndex];
    VMModelData model = VMGetModelData(instanceIndex);
    // Compensate mirrored rasterizer winding exactly once. Every unused
    // triangle collapses to one vertex; no padded index/vertex storage is read.
    if (instance.parameters.z < 0)
        corner = (corner / 3) * 3 + (corner % 3 == 0 ? 0 : 3 - corner % 3);
    if (corner >= cluster.indexCount) corner = 0;
    uint index = _VMIndices[model.offsets.y + cluster.indexStart + corner];
    return VMReadVertex(model.offsets.x + index);
}

#endif
