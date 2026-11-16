#ifndef VIRTUAL_MESH_MODEL_VISIBILITY_INCLUDED
#define VIRTUAL_MESH_MODEL_VISIBILITY_INCLUDED

#include "VirtualMeshModelGeometry.hlsl"

// A visibility phase owns normals only until the next geometry phase. In
// particular, old Early IDs must not overwrite a later equal-depth fallback.
uint VMVisibilityPhaseBits()
{
    return _VMUseRecoveredRange != 0 ? 64u : 0u;
}

bool VMVisibilityMatchesNormalPhase(uint4 visibility)
{
    // The normal resolve draw sets the existing phase property to 0 or 1.
    // Reusing it keeps the implicit constant-buffer layout of old passes intact.
    return ((visibility.z >> 6) & 1u) == (_VMUseRecoveredRange != 0 ? 1u : 0u);
}

// A separate D32 attachment stores material ownership, not scene depth. All
// admitted IDs (1..64) are exact binary fractions; zero denotes no resolve owner.
// Use the same encoding when producing the attachment and drawing resolve tiles
// so fixed-function Equal rejects other materials before fragment shading.
float VMVisibilityMaterialDepth(uint batchId)
{
    return batchId * (1.0 / 128.0);
}

uint2 VMVisibilityGetDrawCluster(uint instanceID)
{
    uint visibleIndex = instanceID;
    if (_VMUseRecoveredRange != 0)
        visibleIndex += _VMVisibleRangeCounters[0];
    return _VMVisibleClusters[visibleIndex];
}

VMVertex VMVisibilityFetchVertex(uint clusterId, uint instanceId, uint corner)
{
    VMCluster cluster = _VMClusters[clusterId];
    VMInstance instance = _VMInstances[instanceId];
    VMModelData model = VMGetModelData(instanceId);
    // Match VMFetchLocalVertex's mirrored winding and degenerate padding.
    if (instance.parameters.z < 0)
        corner = (corner / 3) * 3 + (corner % 3 == 0 ? 0 : 3 - corner % 3);
    if (corner >= cluster.indexCount)
        corner = 0;
    uint index = _VMIndices[model.offsets.y + cluster.indexStart + corner];
    return VMReadVertex(model.offsets.x + index);
}

uint2 VMVisibilityGetIndexedDrawCluster(uint vertexID)
{
    // The index builder encodes an absolute slot, including the recovered range.
    return _VMVisibleClusters[vertexID >> 8];
}

VMVertex VMVisibilityFetchIndexedVertex(uint clusterId, uint instanceId, uint canonicalCorner)
{
    VMCluster cluster = _VMClusters[clusterId];
    VMModelData model = VMGetModelData(instanceId);
    // Indices name the first original corner using this vertex. The builder
    // swaps mirrored triangle corners once and maps padding to corner zero;
    // applying the ordinary fetch's winding swap here would swap it twice.
    uint index = _VMIndices[model.offsets.y + cluster.indexStart + canonicalCorner];
    return VMReadVertex(model.offsets.x + index);
}

bool VMVisibilityNormalizeWeights(float3 edge, out float3 weights)
{
    weights = 0;
    // Scale before summation. A fixed epsilon would discard valid thin triangles
    // near the camera or at the edge of the pixel grid.
    float largest = max(abs(edge.x), max(abs(edge.y), abs(edge.z)));
    if (!(largest > 0) || !isfinite(largest))
        return false;
    float3 scaled = edge / largest;
    float total = scaled.x + scaled.y + scaled.z;
    if (total == 0 || !isfinite(total))
        return false;
    weights = scaled / total;
    return all(isfinite(weights));
}

bool VMVisibilityGetBarycentrics(float2 pixel, float2 viewportSize, float4 clip0, float4 clip1, float4 clip2,
    out float3 weights, out float3 weightsDx, out float3 weightsDy)
{
    weights = weightsDx = weightsDy = 0;
    if (any(viewportSize <= 0))
        return false;
    float2 ndcStep = 2 / viewportSize;
    float2 ndc = pixel * ndcStep - 1;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
    ndcStep.y = -ndcStep.y;
#endif

    // Homogeneous triangle interpolation avoids dividing a corner by W. It also
    // handles a visible triangle clipped against the near plane by the rasterizer.
    float3 clipW = float3(clip0.w, clip1.w, clip2.w);
    float3 x = float3(clip0.x, clip1.x, clip2.x) - ndc.x * clipW;
    float3 y = float3(clip0.y, clip1.y, clip2.y) - ndc.y * clipW;
    if (!VMVisibilityNormalizeWeights(cross(x, y), weights))
        return false;

    // Evaluate the same primitive at its other quad lane, even when a fullscreen
    // resolve quad spans multiple primitive IDs. Keep gradients in +X/+Y order.
    uint2 lane = (uint2)pixel & 1u;
    float2 neighbor = 1.0 - 2.0 * float2(lane);
    float3 next;
    if (VMVisibilityNormalizeWeights(cross(x - (ndcStep.x * neighbor.x) * clipW, y), next))
        weightsDx = (next - weights) * neighbor.x;
    if (VMVisibilityNormalizeWeights(cross(x, y - (ndcStep.y * neighbor.y) * clipW), next))
        weightsDy = (next - weights) * neighbor.y;
    return true;
}

#endif
