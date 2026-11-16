# Reusable model assets

Bake a static source model to publish its model-space geometry clusters and a reusable prefab. Each scene instance uses the same published model data.

## Build and place a model

1. Select an FBX, another imported static model, a prefab, or a saved Mesh asset in the Project window.
2. Choose **Assets > Virtual Mesh > Bake Model Clusters**. The first bake also labels the source for automatic updates.
3. The completion message links to the generated prefab. Drag that prefab into a scene.
4. Duplicate, move, rotate, or scale the prefab normally. All instances of a model share its geometry buffers. Instances may use different material references.

The generated prefab contains a `VirtualMeshInstance` next to each supported static mesh. Its `Model` references the published cluster asset. `Materials` and `SourceRenderer` retain the original rendering data; `RenderMaterials` contains generated material assets that retain the necessary shader variants in Player builds. `PixelError` controls the projected LOD tolerance: smaller values retain more detail. `TargetTrianglePixels` defaults to 2 and enables density adjustment; set it to 0 to retain the ordinary error-only rule. `ShadowLodMultiplier` defaults to 1; higher values permit coarser shadow geometry without changing visible geometry. `PreviewInEditMode` controls editing-time rendering.

The source FBX, prefab, materials, and user scenes are not overwritten. The generated prefab preserves the source hierarchy and transforms. For an authored `LODGroup`, the generated copy uses the first nonempty LOD as the cluster source, disables renderers found only in lower LODs, and removes that copy's `LODGroup`. Its lower detail levels come from the generated cluster hierarchy, rather than displaying the original lower-LOD renderers at the same time. The source's LOD setup is unchanged.

Unsupported-input fallback uses the retained highest-detail source renderer. It preserves the original mesh and materials when the cluster renderer cannot handle them, but does not recreate the source `LODGroup`'s distance switching in the generated prefab. The original FBX/prefab remains available when authored LOD switching is required. GPU residency fallback has a different shadow policy, described below.

Reimporting or moving a labeled source queues another bake. Changes to external source `.mat` dependencies also refresh the affected labeled models' generated materials so Player builds retain their updated shader variants. Select **Disable Automatic Cluster Bake** to remove the `VirtualMeshPrebuild` label and keep the current published assets. An in-progress bake finishes. Generated output is excluded from the import queue.

## Set up the URP renderer

1. Open the Universal Renderer Data asset assigned to the camera, and set **Rendering Path** to **Forward+**, **Deferred**, or **Deferred+**.
2. Use **Add Renderer Feature** to add and enable **Virtual Mesh Model Render Feature** (`VirtualMeshModelRenderFeature`).
3. Disable MSAA on the pipeline asset. Unity 6.3 uses Render Graph by default. If the project previously enabled Compatibility Mode, turn it off and remove `URP_COMPATIBILITY_MODE` from the active platform's **Player > Scripting Define Symbols**. Use a full camera viewport. If a camera uses a Render Texture, set its anti-aliasing to one sample.
4. For the verified Windows configuration, put **Direct3D 12** first in the Player graphics API list and restart the Editor. Use a device that supports compute shaders and GPU instancing.

The feature is selected per camera. Verify that the camera is using the renderer that contains it. The full Render Graph path requires a non-stereo camera and an unscaled hardware viewport.

## Output and lifetime

| Location | Purpose |
|---|---|
| `Assets/VirtualMeshModels/Meshes/<source-guid>-<mesh-local-id>.asset` | Published binary model geometry, cluster hierarchy and bounds. It is a normal Player dependency. |
| `Assets/VirtualMeshModels/Materials/<material-guid>-<local-id>.mat` | Material assets using the model renderer shader. These retain required shader variants during a build. |
| `Assets/VirtualMeshModels/Prefabs/<source-guid>/<name>.prefab` | Reusable hierarchy with model instances and original renderer fallbacks. |
| `Library/VirtualMeshModelClusters/v3/.../*.clusters` | Optional rebuild cache. It is not read by the model runtime and can be regenerated. |

Rebuilding updates existing model assets in place and preserves their GUIDs. An unchanged source hash, format version, and simplification error reuse the already published asset without incrementing its revision or uploading geometry again. A changed model receives a new revision, which invalidates its runtime geometry buffers.

The model and generated material assets are automatically included when referenced by a scene or a prefab included in the Player build. Add the generated folder to source control along with the source models. Unlike the Library cache, these published assets must be available to the build.

## Runtime behavior

The path uses `VirtualMeshModelRenderFeature` with URP Render Graph in Forward+, Deferred, or Deferred+. VM shades into camera color through its own forward pass, including on a deferred renderer. Forward+ and Deferred+ provide clustered additional lights. Standard Deferred requires URP per-object light indices for additional lights, which VM indirect batches do not provide. Use a clustered mode for VM objects that need additional point or spot lighting.

Geometry is stored in model coordinates and uploaded into a shared atlas. Material batches share the atlas across models and instances. Stable work lists, instance slots, model IDs, and previous transform data stay on the GPU. An instance update transfers the affected ranges. Game View and Scene View keep separate visibility and history buffers. Under the GPU budget, the runtime can evict idle camera buffers while retaining the shared atlas. Geometry is admitted as whole models according to camera and shadow demand. Cluster-page disk streaming is not implemented.

The graph selects LOD before depth rendering, then applies tight cluster bounds, backface-cone rejection where safe, and previous-frame Hi-Z prediction. Early survivors write depth; a current-frame depth pyramid retests deferred clusters using GPU indirect dispatch. Recovered clusters add depth before final shading. Ordinary opaque scene depth seeds the process. Camera cuts, projection changes and target changes invalidate history. Two-sided and wind-deformed foliage bypass unsafe cone rejection. Depth, normals, color and motion use the selected geometry; motion tests against the current active depth attachment. Each camera owns its visibility and history resources.

Unsupported multisampling, XR or actual scaled-resolution configurations retain the native fallback path. Camera stacks disable impostors while retaining the supported cluster path. This is not a software rasterizer or a complete UE Nanite/VSM implementation.

## Automatic coarse levels and global budgets

Open **Tools > Virtual Mesh > Rendering Settings** and select a Universal Renderer with a VM feature. The panel exposes its screen-height LOD curve and global budget multiplier, culling, impostor, shading and motion settings. Changes apply to all VM instances rendered by cameras using that Renderer. Click **Save Renderer Settings** to persist the values on the Renderer asset. These controls do not require rebaking the model.

The model prebuild adds whole-model coarse levels for material slots verified to be opaque. It preserves material boundaries and UV/vertex data, and attempts to reach 256 triangles per eligible slot. The builder reports when topology or boundaries prevent that target; it does not report success merely because it reached the last level. Unknown or alpha-clipped material uses prevent unsafe opaque simplification. Shared model publication intersects all registered material-use contexts, and restores the original cached hierarchy before rebuilding under a changed policy.

`VirtualMeshModelRenderFeature.lodBudget` controls the per-instance request. Defaults are 50,000 triangles at projected screen height 0.5, 5,000 at 0.15, and 500 at 0.03, smoothly interpolated between thresholds. `BudgetMultiplier` scales these requests globally. These are requests constrained by the available model hierarchy, not hard triangle caps. A per-model density/level table selects a common tolerance scale; color and depth use the same selection. Shadow budgets also respect `ShadowLodMultiplier`. Runtime counters distinguish profiled, unprofiled and unmet requests.

## Optional far-foliage depth impostors

`enableImpostors` is opt-in and defaults to false. The experimental `BuildAssetAsync(..., generateImpostors: true)` option generates whole-model impostor assets for supported ElectricDreams Hornbeam/Foliage material variants when graphics capture is available. The default is false; ordinary model prebuild and the FBX batch queue only build clusters. Each actual full material-array variant is captured separately; unsupported or unavailable capture is reported as pending and retains cluster rendering. The default capture has 24 directions at 256 square pixels. Assets live at `Assets/VirtualMeshModels/Impostors/<model-guid>/<bake-hash>.asset` and are referenced by their model. Geometry/material changes invalidate incompatible captures. `VirtualMeshInstance.Impostor` can explicitly override the model's registered variants.

The runtime uses a two-triangle representation when a valid variant exists, the model cannot meet its requested budget, and its projected screen height is at most both the asset limit and `impostorMaxScreenHeight` (default 0.15). Replacing a model removes its original cluster work for that selected color/shadow path. Nearby models retain their cluster hierarchy. Alpha clipping discards holes, surviving fragments reconstruct surface depth, and color/depth/normals/motion/shadows share the coverage. There is no transparent blending. Shadow views follow the light observer and use URP's bias.

Captures freeze wind and use the minimum configured distance-opacity coverage. Finite direction sampling and a single captured front depth per view can lose disoccluded detail; inspect actual assets before enabling them broadly. Proxies are tested against opaque Hi-Z but intentionally do not contribute to current or historical Hi-Z, so they cannot yet provide conservative foliage occlusion for hidden rocks. Their depth still participates in ordinary depth testing. The full cluster atlas remains resident for near views. Proxy textures are separate from the VM buffer budget.

Every LOD in a cluster group uses the same bounds and projected tolerance. A standalone cluster is always selected, leaf clusters are selected below their parent error, intermediate clusters between their own and parent errors, and root clusters at or above their own error. This keeps adjacent hierarchy levels from overlapping or leaving holes during a transition. Nonuniform and mirrored transforms are handled at instance rendering time.

Density adjustment measures `sqrt(original group triangle surface area / triangle count at this level)`. It permits a coarser level when this average triangle size projects below `TargetTrianglePixels`, provided its geometric error remains within **twice `PixelError`**. Increasing the density target requests more aggressive simplification, but cannot exceed that error limit or the coarsest level available in the asset. Setting the target to 0 disables this adjustment. This is a surface-density estimate, not an alpha-texture coverage test, occlusion test, global triangle budget, or frame-rate guarantee. Small foliage cards and overlapping instances may still be expensive.

Density thresholds are shared by an entire group/level and copied to the adjacent level's parent boundary. Explicit level identifiers distinguish equal-error levels. Successful zero-error reductions are retained and can be selected immediately; the original local hierarchy keeps its boundary locks. Additional opaque whole-model levels explicitly lock shared cross-material positions while allowing eligible open boundaries to reduce. Current model/cache format 3 also preserves full vertex RGBA. Format-1 and format-2 model assets must be rebuilt through the same prebuild command; stable asset GUIDs are preserved. Old v1/v2 cache files are ignored.

Before the per-cluster color pass, model-instance bounds are tested against the camera frustum. Each material slot dispatches only its own cluster range. Shadows use a separate candidate list and indirect submission, so a color-frustum rejection does not automatically remove a visible shadow.

The ordinary source renderer is retained for fallback and editable material/casting settings. Supported resident instances hide that renderer and use cluster geometry for both color and shadows. Disabling/removing an instance, leaving the supported rendering path, or releasing its runtime resources restores the source renderer's previous visibility and any temporarily suppressed shadow setting.

## Cluster shadows

Color and shadow passes share model geometry but have separate instance/visible-cluster buffers. Shadow draws use `ShadowCastingMode.ShadowsOnly` and the adapter's `ShadowCaster` pass. URP owns the directional cascades, point/spot shadow maps, bias and lighting. The original high-detail mesh is not submitted for shadows by the supported cluster path.

Shadow LOD uses camera-to-cluster radial distance and the same hierarchy/density selection as color, with the pixel-error tolerance multiplied by `ShadowLodMultiplier`. The default 1 applies the same error budget, but color and shadow selections can differ because color uses view-space depth and shadows retain off-screen casters. Camera-distance shadow LOD remains stable when rotating in place. It is not selected independently from each light's shadow-map texel footprint, and it is not a virtual shadow map implementation.

Directional caster selection first conservatively tests whether an instance can project along the light direction into the camera's shadow-distance receiver bounds. After LOD selection, the GPU performs a tighter group-sphere test against the receiver frustum: it clips a ray along each directional light's direction against the six planes expanded by the group radius. It retains a group when its projected volume can reach the receiver region, including off-screen casters. Shared group bounds and wind padding keep this test conservative. Point and spot lights use range-sphere tests rather than approximating their shadow cones. When the per-batch light list exceeds 16 lights, the additional GPU test is disabled rather than omitting lights.

Shadow batches are divided into 64-unit spatial cells so Unity can cull smaller submission bounds. The light and URP subsequently perform their normal shadow culling. Alpha clipping, source culling settings, `Off`, `ShadowsOnly`, `TwoSided` and mirrored transforms are handled by the model shadow path. Dedicated ElectricDreams adapters preserve their wind deformation and expand bounds for wind.

## GPU residency and budget

`VirtualMeshRuntime.GpuBudgetMiB` controls the total allocation budget for Virtual Mesh geometry and draw buffers. Zero, the default, chooses 40% of reported device memory, clamped to 64–3072 MiB; if the device memory size is unavailable, it uses 1536 MiB. The budget includes vertex/index/cluster buffers plus per-camera color/shadow instance, visible-cluster, indirect-argument and count-readback buffers. It excludes textures, retained source meshes and other Unity GPU allocations; it is not a limit on total application VRAM.

The Graph backend collects active models, orders the initial model plan by camera distance and builds a shared atlas. Model-set or revision changes can rebuild that atlas, including unchanged models. Transform-only changes generally update instance data. This is not incremental page residency. The Graph backend uploads up to 16 MiB of geometry per render context in writes of at most 4 MiB. A large model stays queued until all its geometry streams are submitted. `MaxModelUploadsPerContext` defaults to 1 and accepts 1–2 completed models per context. On Windows the Graph backend can defer allocation or upload when the available system commit is low. Inspect `AtlasUploadBytesThisContext` and `AtlasUploadDeferredForHostMemory` in the runtime statistics. Models remain available as CPU-side asset data.

While a supported model is queued or rejected by the buffer budget, its original color renderer remains available and its high-detail source shadows are temporarily disabled. A `ShadowsOnly` source remains color-hidden. Shadow casting resumes with cluster geometry when the model is admitted. Runtime diagnostics distinguish queued uploads from a budget rejection; do not treat source-color fallback as a completed cluster conversion. Reducing demand or increasing the budget can resolve a rejection, subject to actual available device memory.

The controls are runtime static properties. Set them from project initialization when a persistent override is needed. Inspect `GpuBufferBytes`, `GpuBudgetBytes`, `QueuedModels`, `BudgetRejectedModels` and `UploadedModelsThisContext` in `VirtualMeshRuntime.Statistics` to assess residency. Visible-cluster statistics use asynchronous GPU readback and can lag rendering.

Each camera reuses its visible list for the early depth pass, the clusters recovered by current-frame Hi-Z, and material shading. GPU writes and indirect counts are capped at the allocated capacity. The camera's diagnostic sample reports `TruncatedClusters` if any clusters exceeded that capacity.

### Change-driven validation for static instances

Add **Rendering > Virtual Mesh Scene Settings** to a scene to enable change-driven validation for its **Scene Default** instances, including manually placed instances, prefabs and generated objects. An instance can select **Change Driven** or **Dynamic** explicitly to override its scene. A scene without a policy uses dynamic validation. To set a scene default from code, call `VirtualMeshRuntime.SetSceneChangeDrivenValidation(scene, enabled)`. An enabled scene settings component also enables the policy.

VM retains validated instances and their source Renderer state across render contexts. Enable, disable, scene unloading and model rebuilds update those records. Cameras still compute their own visibility.

For runtime script edits to a transform (including inherited parent changes), scene membership, layer, model assignment, material slot, or source Renderer, call `instance.InvalidateRenderState()` for each affected instance after the edit. For an in-place edit to a shared source or generated Material, call `VirtualMeshInstance.InvalidateSharedMaterial(material)` on the main thread. Use **Dynamic** for instances modified by scripts or animation that cannot report their changes. `ChangeDrivenValidatedInstances` and `PolledInstances` in `VirtualMeshRuntime.Statistics` report the work performed in the latest Graph rendering context.

Use **Tools > Virtual Mesh > Pause Edit Mode Preview** before editing and reimporting shaders in a large scene. The toggle releases VM buffers and hides its opted-in previews for the current Editor session. Toggle it again to restore the preview. Play Mode uses the configured renderer.

## VM shading and custom shaders

VM requires a `VirtualMeshShading` SubShader tag with a nonempty family name. Bake converts supported ordinary materials to tagged VM materials. Assign a tagged material directly to the instance, or use a tagged adapter in the matching **Render Materials** slot. A custom adapter can declare `VirtualMeshSourceShader` with the exact source shader name and supply its own material properties. VM restores the source Renderer if an instance has an unsupported slot.

A tagged shader needs VM geometry bindings and the `ForwardLit`, `DepthOnly`, `DepthNormals` and `ShadowCaster` passes. Implement `MotionVectors` if the material needs motion vectors. **Enable Visibility Material Resolve** is experimental and uses `VMVisibility` or `VMVisibilityNormals` and `VMResolve` when the material supplies them. The raster passes write cluster and instance IDs, the primitive and batch IDs, facing, and raster depth into a full-resolution integer visibility attachment. The resolve pass shades matching pixels in 16×16 tiles using the material's own textures and lighting. Other tagged materials use their VM geometry passes.

The Hornbeam and Foliage adapters implement visibility resolve and far LOD shading. **Far Shading Max Pixels** limits the projected cluster diameter for the simpler lighting model. Set it to zero for full lighting. Participating materials can opt out via **Allow Simplified Distant VM Shading**. Custom shaders can use the same threshold or implement their own rule.

## Supported inputs and limits

- Static triangle meshes with URP Lit or URP Unlit materials, including opaque and alpha-clipped surfaces. The optional ElectricDreams sample supplies dedicated adapters for `ElectricDreams/Source Foliage`, `ElectricDreams/Source Hornbeam` and `ElectricDreams/Source Surface`.
- URP with compute-shader and instancing support. The verified Editor and Windows Mono Player configuration uses Direct3D 12. Check the target Player when deploying to a different platform or graphics API.
- Editable instance transforms and material references, multiple instances, and multiple cameras.
- Unsupported material shaders, blended transparent surfaces, skinned meshes, baked lightmap usage, per-renderer `MaterialPropertyBlock` overrides, singular transforms and stereo rendering retain the source rendering path rather than silently replacing their appearance.
- The generated shader covers the supported URP material properties; arbitrary custom shader behavior is not converted. The ElectricDreams adapters in `Samples~/ElectricDreamsAdapters/Shaders/VirtualMesh` preserve the corresponding source shaders' wind, foliage clipping, vertex-alpha inputs and surface layers. Other custom shaders need their own adapter. Import the optional sample when using these material conventions; source content, materials and textures are not included.
- This workflow creates cluster assets and prefabs. It does not change the project's graphics API, URP renderer list, build scenes or startup scene.

## Troubleshooting

- **The generated prefab is missing.** Check the Console for the first bake error. Choose a source asset outside the generated output folder and allow imports and the bake queue to finish. The publication log links to its prefab.
- **The prefab looks like the source geometry.** Check the assigned camera renderer, Render Graph settings and the instance's `Diagnostic` field. Inspect `VirtualMeshRuntime.Statistics` to distinguish resident instances, queued models and rejected models. The Frame Debugger should show model passes such as **VM Early Depth** and **VM Shade** on the configured camera.
- **Editor Play is slow on a new machine.** Check **Code Optimization** in the Unity status bar and use **Release** when measuring. Let recompilation and model uploads settle, focus Game view, then measure normal Play with CPU/GPU Profilers and Deep Profiling off. [Unity's code optimization controls](https://docs.unity.com/en-us/engine/6000.3/manual/programming-environment/environment-and-tools/debugging-and-diagnostics/managed-code-debugging)
- **The GPU runs out of memory.** Compare the VM buffer budget with system GPU usage. Textures, original meshes, render targets and other applications also consume memory. Check the device budget before raising `GpuBudgetMiB`.

## Editor API and validation

All APIs below are in `Unity.VirtualMesh.Editor.VirtualMeshModelAssetBuilder` and run in the Editor.

```csharp
// Preferred entry: build the prefab, model/material assets and safe global coarse LODs.
var result = await VirtualMeshModelAssetBuilder.BuildAssetAsync("Assets/Models/Tree.fbx");
// result includes PrefabPath, ModelPaths, ModelResults, PendingSubmeshes and UnmetTargetSubmeshes.

// Build/update one saved Mesh independently.
VirtualMeshModelAsset model = VirtualMeshModelAssetBuilder.BuildMeshAsset(mesh);

// Validate every source cluster group's LOD boundaries and original leaf triangle count.
var lodReport = VirtualMeshModelAssetBuilder.ValidateSourceClusters(mesh);

// Confirm that the generated prefab has persistent model/material references and
// still retains matching source meshes, MeshRenderers and original materials.
var prefabReport = VirtualMeshModelAssetBuilder.ValidatePrefab(result.PrefabPath);
```

The default simplification error is `0.005f`; build/validation methods accept an explicit positive value. `GetModelAssetPath(mesh)` returns the stable asset path. `BuildRenderMaterial(material)` builds a material asset for supported source shaders using `VirtualMeshMaterialSupport` and returns null for unsupported material types.

The **Bake Model Clusters** menu and labeled-source import queue use `BuildAssetAsync`. Publications run serially, with global simplification on a worker. The generated prefab retains its source renderer and authored material references. Every renderer using the same mesh participates in the material safety intersection, including inactive and lower authored LOD renderers. Known usage contexts from other exported sources remain part of that intersection.

Automatic global levels currently cover supported opaque URP Lit/Unlit, ElectricDreams Surface, and the explicitly resolved Hornbeam `Trunk` branch with opaque source settings. Foliage remains coverage-sensitive even when `_AlphaClip` is zero. Hornbeam leaves/decoration, transparent/cutout materials, missing slots and unknown shaders remain pending in `PrebuildPolicies`; they retain the base hierarchy. These are different limits from whether a material can render through Virtual Mesh.

`GlobalCoarseLodSettings` defaults to a target of 256 triangles per eligible submesh, up to 16 additional levels, progressively increasing error tolerance. The target is a request, not a guarantee: topology and material boundaries can stop simplification earlier. `ModelResults[].Coarse.Submeshes` records the remaining triangles, added levels and stop reason. A change to policy, settings or algorithm restores the original version-3 Library cache before extending the hierarchy. It never appends a second extension to the previous coarse result. The published asset GUID remains stable. Worker failures/cancellation leave the previous published geometry intact.

The synchronous `BuildAsset` API remains available for older callers. It records the same material intersection and restores the base hierarchy if a new use makes existing coarse geometry unsafe; use the asynchronous API to generate new global levels. `BuildMeshAsset` remains a low-level geometry-only API and preserves known material usage contexts on a source rebuild.

For existing scene material overrides, pass all material arrays using the shared model to `BuildPublishedModelAsync(model, rendererMaterials, usageContext, coarseSettings, cancellationToken, saveAsset)`. Keep the context string stable when updating that source of material usage. `saveAsset: false` leaves publication dirty so a caller can save each changed model once at the end of a batch. The API does not edit scenes or reimport FBX files. The original source mesh must still be available if a base rebuild is required. Policy metadata is optional and does not change format 3 or the GPU cluster layout.

The model asset Inspector shows counts and memory size without expanding large serialized arrays. **Bake Source Model** runs the same source bake as the Project menu. **Validate LOD Coverage** checks every threshold and interval between levels. Runtime diagnostics are available on each instance and through `VirtualMeshRuntime.Statistics`, including shared model count, active instances, geometry bytes, separate color/shadow batch and cluster counts, and residency/budget counters.

## Verification

The Windows Direct3D 12 model path was exercised with Unity 6000.3.21f1 and URP 17.3.0 in the Editor and in an isolated Mono Player. Coverage includes shared instances, mirrored and nonuniform transforms, LOD selection, restoration of source renderers, and actual cluster shadows. Validate the intended render path and deployment target independently, especially when using another graphics API or Player backend.

Forest-specific FBX batching and scene-application tools from the development project are not part of this standalone package. See the root README for installation and the optional shader-only sample.

## Editor-session preparation cache

The Editor cache preserves fully prepared immutable geometry and metadata across ordinary Play/domain transitions on supported backends. Native byte carriers bridge the reload; rendering still uses ordinary Structured buffers and restoring requires GPU copies. Instance/camera state is rebuilt. A new Editor process or a changed model/code/layout requires cold preparation. The cache does not implement independent Editor preparation, partial model replacement or disk page streaming. See the root README for the current scope.

The export omits `.meta` files intentionally. Existing assets from another checkout may need references repaired or regeneration because source GUIDs are not retained. The supplied native bake library is Windows x64 only.
