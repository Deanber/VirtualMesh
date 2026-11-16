# Implementation

## Model publication

`VirtualMeshModelPrebuilder` queues opted-in source assets after Unity imports them. `VirtualMeshModelAssetBuilder` publishes a model asset per source mesh and a prefab per source asset. The model stores vertices in model space, index data, material slots, cluster hierarchy, bounds, and culling metadata. The prefab associates model assets and generated render materials with `VirtualMeshInstance` components and retains their source renderers.

`ModelClusterBuilder` groups triangles into clusters, simplifies group geometry, and records the relationship between neighboring levels. `ModelClusterCache` stores model-local build data in Library for reuse. Published assets live in Assets and are sufficient for runtime geometry. Eligible opaque material slots can receive additional coarse levels. Coverage-sensitive leaf levels require their own material and coverage validation.

## GPU residency and drawing

`VirtualMeshRuntime` tracks active instances and validates the model, material and source renderer state. `VirtualMeshGraphRuntime` admits models under a GPU buffer budget and uploads their data into a shared atlas. Model offsets and per-instance transforms let several instances refer to the same geometry. Per-camera work lists supply each material batch with candidate clusters and indirect draw arguments.

A resident supported instance uses cluster color and cluster shadows. While model upload is queued or rejected, the source renderer can supply color with its high-detail shadows temporarily suppressed. Unsupported instance data retains its source rendering behavior. See [Model assets](model-assets.md#gpu-residency-and-budget) for the budget and diagnostic counters.

## URP Render Graph passes

`VirtualMeshModelRenderFeature` first selects a cluster level and applies bounds and safe cone tests. Previous-frame hierarchical depth predicts visibility. Early survivors write depth, then a current depth pyramid retests deferred clusters. Recovered geometry writes depth before final color shading. Ordinary opaque scene depth participates in this process. Camera cuts and projection or target changes invalidate history.

Color, depth, normals and motion vectors use the selected cluster geometry. A separate light-visible list feeds URP shadow maps, so an off-screen object can still cast into the camera's view. The shader resources include the model culling compute shader, depth pyramid compute shader and material adapters. Shadow behavior and supported camera configurations are detailed in [Model assets](model-assets.md).
## Preparation lifecycle

Instances register in Edit Mode as well as Play. Geometry preparation still advances from real render contexts; the Editor update helper requests Scene repaints rather than preparing independently. Model-set/revision changes invalidate the shared atlas. The Editor-session cache avoids repeated CPU geometry upload for matching complete data, but uses GPU byte copies at domain transitions and does not survive an Editor restart.

Independent preparation scheduling, directly consumable imported metadata and stable incremental model allocations are future work. The cache does not resolve these architectural limits.
