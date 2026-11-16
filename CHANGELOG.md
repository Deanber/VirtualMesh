# Changelog
All notable changes to the com.unity.virtualmesh package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0-preview.2] - 2026-11-14

### Packaging

- Standalone UPM Git source export; `.meta` files intentionally omitted and assembly references changed from GUIDs to names.
- Windows x64 bake DLL placed under `Editor/x86_64`; a scoped importer reapplies Editor-only Windows x64 settings when local metadata is generated. Original license retained and meshoptimizer notice included.
- Optional shader-only ElectricDreams adapter sample; no forest assets, scenes, caches or measurement logs.

### Included development changes

- Two-stage Hi-Z and optional conservative 4x4 Hi-Z; MaterialDepth/resolve for supported adapter shaders.
- Scene motion depth-dependency correction, optional split vertex streams and GPU transpose upload.
- Editor-session preparation cache across Play transitions, preserving normal domain reload.

Geometry is still whole-model atlas residency. Cold preparation, scene-wide invalidation and independent Editor residency remain unfinished. Older sections describe historical releases, not the current support matrix.


### Changed

- **Assets > Virtual Mesh > Bake Model Clusters** and the model Inspector's **Bake Source Model** button use the same reusable model publication queue.
- The package guide now covers model import, bake, prefab placement and the model Render Graph feature.

### Removed

- Retired the scene-level baker, editor setup and page-streaming implementation. The old manager and renderer feature types remain as inert serialization placeholders for historical assets.

## [0.4.0-preview.1] - 2026-11-12 (local model-asset edition)

### Added

- Independent GPU cluster shadow batches using URP shadow-caster passes, alpha clipping, mirrored transforms and two-sided casting. Supported resident models no longer submit the original high-detail mesh for shadows.
- Shadow LOD with the same hierarchy and density rule as color rendering, using camera distance and a per-instance `ShadowLodMultiplier`. Separate caster selection retains off-screen objects that can shadow the camera's receiver region.
- Conservative GPU group-sphere culling against the shadow receiver frustum after LOD selection, reducing directional shadow work while retaining off-screen casters whose projection can reach visible receivers.
- A configurable GPU buffer budget, model-demand prioritization, short unused-resource retention and a limit of one model upload per rendering context by default. Accounting includes geometry and draw buffers, but excludes textures, source meshes and other engine allocations.
- Queue/budget diagnostics and source-color fallback with high-detail source shadows disabled while model GPU data is unavailable.
- Dedicated ElectricDreams Foliage, Hornbeam and Surface material adapters in this project's `Assets/ElectricDreamsPcg/Shaders/VirtualMesh` folder, plus a resumable FBX batch and an Undo-able forest integration tool.

### Changed

- Model asset format 3 and model cache v3 preserve full vertex RGBA, including Hornbeam's vertex-alpha inputs. Rebuild older generated assets; published asset GUIDs remain stable.
- Runtime statistics include shadow candidates/batches, queued and rejected model counts, and GPU buffer usage/budget.

This edition uses URP's existing shadow maps and camera-distance shadow LOD. It does not implement virtual shadow maps, per-light shadow-texel LOD, cluster-page disk streaming or Hi-Z occlusion in the model-asset path. See [model assets](Documentation~/model-assets.md) for validation scope and operating limits.

## [0.3.0-preview.2] - 2026-11-12

### Added

- Density-aware cluster LOD using average triangle surface spacing per group/level, with a two-times geometric pixel-error limit and a per-instance switch.
- Model asset format 2 and model cache v2, with explicit hierarchy levels and shared density thresholds.

### Fixed

- Accept successful zero-error simplification instead of discarding its coarser level.
- Dispatch only each material slot's cluster range; cache instance transforms once per render context and initialize indirect arguments once.
- Cull off-screen model instances before color dispatch while preserving independent shadow submission. Retain empty batches briefly to avoid allocation spikes when turning the camera.
- Use camera-space depth for conservative screen-error estimation at the edge of the view.

## [0.3.0-preview.1] - 2026-11-12 (local model-asset edition)

### Added

- Publishable binary model cluster assets and reusable prefabs, without a scene bake or camera manager.
- Shared model GPU buffers, per-instance transforms/materials, per-camera GPU frustum culling and cluster LOD selection.
- Non-indexed indirect rendering on Direct3D 12, including mirrored/nonuniform transforms, URP Lit/Unlit, alpha clipping and source-mesh shadows.
- Stable asset references, dependency-aware rebuilds, persistent material variants, model statistics and LOD coverage validation.
- Source-renderer fallback and explicit diagnostics for unsupported inputs; resource cleanup and editor preview support.

The new path currently keeps whole models resident. Legacy scene-page streaming and two-pass Hi-Z occlusion remain in the original scene-baked path. See [model assets](Documentation~/model-assets.md) for the supported workflow and its limits.

## [0.2.0-preview] - 2025-12-12

### Added

- Added third party notice and updated license links
- Exposed simplification error target in baker
- Added internal draw pass to prevent opaque pass dependency

### Changed

- Changed StreamingAsset IO code to improve security
- Slightly refactored baker UI
- Moved settings flags from render feature to runtime component
- Updated target engine version to 6000.3.0f1

### Fixed

- Fixed Shader graph shader compilation errors caused by wrong include paths
- Fixed selected LOD for in-frustum shadow casters
- Fixed depth pyramid blit warning about unused invalid target
- Improved manager data allocation timing

## [0.1.0-preview] - 2025-10-07

- Initial Release
