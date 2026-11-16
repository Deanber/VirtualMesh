using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>Publishes model-local clusters and a reusable prefab. No scene bake is required.</summary>
    public static class VirtualMeshModelAssetBuilder
    {
        public const string OutputRoot = "Assets/VirtualMeshModels";
        public const float DefaultSimplificationError = 0.005f;
        private static readonly SemaphoreSlim s_PublicationGate = new SemaphoreSlim(1, 1);

        [Serializable]
        public sealed class BuildResult
        {
            public string SourcePath;
            public string PrefabPath;
            public string[] ModelPaths;
            public int BuiltMeshes;
            public int ReusedMeshes;
            public int InstanceCount;
            public int CoarseUpdatedMeshes, CoarseReusedMeshes, PendingSubmeshes, UnmetTargetSubmeshes;
            public int ProxyBuiltVariants, ProxyReusedVariants, PendingProxyVariants;
            public ModelPrebuildResult[] ModelResults;
        }

        [Serializable]
        public sealed class ModelPrebuildResult
        {
            [NonSerialized] internal VirtualMeshModelAsset Model;
            public string ModelPath;
            public bool GeometryUpdated, Reused, BaseCacheReused;
            public VirtualMeshModelLodPolicySlot[] Policies;
            public GlobalCoarseLodBuildResult Coarse;
            public int PendingSubmeshes, UnmetTargetSubmeshes;
            public int ProxyBuiltVariants, ProxyReusedVariants, PendingProxyVariants;
            public string[] ProxyPaths = Array.Empty<string>(), ProxyPendingReasons = Array.Empty<string>();
        }

        [Serializable]
        public sealed class ValidationResult
        {
            public int MeshCount;
            public int GroupCount;
            public int LodSamplesChecked;
            public int ClusterCount;
            public long LeafIndexCount;
        }

        [Serializable]
        public sealed class PrefabValidationResult
        {
            public int InstanceCount;
            public int UniqueModelCount;
            public int PreservedSourceRendererCount;
            public int PublishedMaterialCount;
        }

        [Serializable]
        public sealed class CullingBuildResult
        {
            public int Total, Updated, Reused, Failed;
            public long MetadataBytes;
            public readonly List<string> Errors = new List<string>();
        }

        [Serializable]
        public sealed class CullingBuildProgress
        {
            public string AssetPath;
            public int Completed, Total;
            public bool Updated;
        }

        /// <summary>Derives only optional culling metadata. Geometry, cache and asset GUID stay unchanged.</summary>
        public static bool EnsureCullingData(VirtualMeshModelAsset model, bool saveAsset = true)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!model.EnsureCullingData()) return false;
            EditorUtility.SetDirty(model);
            if (saveAsset && EditorUtility.IsPersistent(model)) AssetDatabase.SaveAssetIfDirty(model);
            return true;
        }

        /// <summary>
        /// Migrates existing published assets, without importing FBXs or simplifying meshes.
        /// Pass scene instances' distinct model paths for a scene-only batch, or null for
        /// every published model. CPU derivation runs off the editor thread; publication
        /// and progress resume on the calling Unity synchronization context.
        /// </summary>
        public static async Task<CullingBuildResult> BuildPublishedCullingDataAsync(
            IEnumerable<string> modelPaths = null, Action<CullingBuildProgress> progress = null,
            CancellationToken cancellationToken = default, bool unloadNewlyLoadedAssets = true)
        {
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("Start the culling-data batch on the Unity editor thread.");
            string[] paths = (modelPaths ?? AssetDatabase.FindAssets("t:VirtualMeshModelAsset", new[] { OutputRoot })
                .Select(AssetDatabase.GUIDToAssetPath)).Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal).ToArray();
            var alreadyLoaded = new HashSet<int>(Resources.FindObjectsOfTypeAll<VirtualMeshModelAsset>()
                .Select(model => model.GetInstanceID()));
            var result = new CullingBuildResult { Total = paths.Length };
            for (int index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = paths[index];
                VirtualMeshModelAsset model = null;
                bool updated = false;
                try
                {
                    model = AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(path);
                    if (model == null || !model.IsValid) throw new InvalidDataException("Missing or invalid model asset.");
                    if (model.HasCullingData) result.Reused++;
                    else
                    {
                        int revision = model.Revision;
                        var vertices = model.Vertices;
                        var indices = model.Indices;
                        var clusters = model.Clusters;
                        var data = await Task.Run(() => VirtualMeshModelCullingData.Build(vertices, indices, clusters,
                            cancellationToken), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (model == null || model.Revision != revision || !ReferenceEquals(vertices, model.Vertices) ||
                            !ReferenceEquals(indices, model.Indices) || !ReferenceEquals(clusters, model.Clusters))
                            throw new InvalidOperationException("Geometry changed while deriving its culling data; retry this model.");
                        // Another caller may already have published the same derived data.
                        if (model.HasCullingData) result.Reused++;
                        else
                        {
                            model.SetCullingData(data);
                            EditorUtility.SetDirty(model);
                            AssetDatabase.SaveAssetIfDirty(model);
                            result.Updated++;
                            updated = true;
                        }
                    }
                    result.MetadataBytes += model.CullingDataBytes;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    result.Failed++;
                    result.Errors.Add(path + ": " + exception.Message);
                }
                finally
                {
                    // Avoid keeping all 389 geometry arrays resident during an all-model
                    // migration. Models already referenced by loaded scenes stay loaded.
                    if (unloadNewlyLoadedAssets && model != null && !alreadyLoaded.Contains(model.GetInstanceID()) &&
                        !EditorUtility.IsDirty(model) && !Selection.Contains(model) &&
                        !Resources.FindObjectsOfTypeAll<VirtualMeshInstance>().Any(instance => instance.Model == model))
                        Resources.UnloadAsset(model);
                }
                progress?.Invoke(new CullingBuildProgress { AssetPath = path, Completed = index + 1,
                    Total = paths.Length, Updated = updated });
                await Task.Yield();
            }
            return result;
        }

        public static bool IsGeneratedAsset(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   (path.Equals(OutputRoot, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(OutputRoot + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The stable asset path is shared by every prefab that references the same source mesh.</summary>
        public static string GetModelAssetPath(Mesh mesh)
        {
            if (mesh == null || !EditorUtility.IsPersistent(mesh) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
                throw new ArgumentException("Select a persistent model or mesh asset.", nameof(mesh));
            return $"{OutputRoot}/Meshes/{guid}-{localId:x16}.asset";
        }

        public static VirtualMeshModelAsset BuildMeshAsset(Mesh mesh,
            float simplificationError = DefaultSimplificationError)
        {
            if (!s_PublicationGate.Wait(0))
                throw new InvalidOperationException("A model publication is running. Retry this synchronous mesh build after it completes.");
            try { return BuildMeshAssetInternal(mesh, simplificationError, out _); }
            finally { s_PublicationGate.Release(); }
        }

        /// <summary>Opt-in coarse levels for verified opaque model/material slots. This never imports
        /// an FBX or changes a scene. Pass an instantiated model and saveAsset=false for isolated QA.</summary>
        public static Task<GlobalCoarseLodBuildResult> BuildGlobalCoarseLodsAsync(VirtualMeshModelAsset model,
            GlobalCoarseLodSettings settings = null, bool saveAsset = false,
            CancellationToken cancellationToken = default) =>
            VirtualMeshGlobalCoarseLods.BuildAsync(model, settings, saveAsset, cancellationToken);

        /// <summary>Checks that a generated prefab retains its original mesh/material fallback and asset references.</summary>
        public static PrefabValidationResult ValidatePrefab(string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new ArgumentException("The prefab does not exist.", nameof(prefabPath));
            var models = new HashSet<VirtualMeshModelAsset>();
            var materials = new HashSet<Material>();
            var result = new PrefabValidationResult();
            foreach (var instance in prefab.GetComponentsInChildren<VirtualMeshInstance>(true))
            {
                if (instance.Model == null || !instance.Model.IsValid || !EditorUtility.IsPersistent(instance.Model))
                    throw new InvalidDataException("A prefab instance has a missing or invalid model asset.");
                var renderer = instance.SourceRenderer;
                var filter = instance.GetComponent<MeshFilter>();
                if (renderer == null || filter == null || filter.sharedMesh != instance.Model.SourceMesh)
                    throw new InvalidDataException("The source mesh/renderer fallback was not preserved.");
                if (!renderer.sharedMaterials.SequenceEqual(instance.Materials))
                    throw new InvalidDataException("The source material fallback differs from the recorded materials.");
                foreach (var material in instance.RenderMaterials)
                    if (material != null)
                    {
                        if (!EditorUtility.IsPersistent(material))
                            throw new InvalidDataException("A rendering material is not a persistent asset.");
                        materials.Add(material);
                    }
                models.Add(instance.Model);
                result.InstanceCount++;
                result.PreservedSourceRendererCount++;
            }
            if (result.InstanceCount == 0) throw new InvalidDataException("The prefab contains no Virtual Mesh instances.");
            result.UniqueModelCount = models.Count;
            result.PublishedMaterialCount = materials.Count;
            return result;
        }

        /// <summary>
        /// Builds a prefab from a model/prefab/mesh asset. Existing generated assets retain their GUIDs.
        /// The caller may instantiate the returned PrefabPath in any scene that uses the model renderer feature.
        /// </summary>
        public static BuildResult BuildAsset(string sourcePath,
            float simplificationError = DefaultSimplificationError)
        {
            // Preserve the synchronous API for existing callers. Automatic coarse publication is
            // asynchronous; synchronously blocking its Unity continuation would deadlock the editor.
            if (!s_PublicationGate.Wait(0))
                throw new InvalidOperationException("A model publication is running. Await BuildAssetAsync to queue this export.");
            try { return BuildAssetCoreAsync(sourcePath, simplificationError, false, null, default, false).GetAwaiter().GetResult(); }
            finally { s_PublicationGate.Release(); }
        }

        /// <summary>Preferred FBX/prefab prebuild entry. Serializes publications, examines every
        /// renderer use of each mesh, and automatically appends safe opaque global LOD levels.
        /// Experimental impostor texture capture is opt-in; ordinary cluster exports create none.</summary>
        public static async Task<BuildResult> BuildAssetAsync(string sourcePath,
            float simplificationError = DefaultSimplificationError, GlobalCoarseLodSettings coarseSettings = null,
            CancellationToken cancellationToken = default, bool generateImpostors = false)
        {
            RequireEditorContext();
            await s_PublicationGate.WaitAsync(cancellationToken);
            try { return await BuildAssetCoreAsync(sourcePath, simplificationError, true, coarseSettings, cancellationToken, generateImpostors); }
            finally { s_PublicationGate.Release(); }
        }

        /// <summary>Upgrade a published model using all known renderer material arrays (for example,
        /// scene overrides). The source Mesh is used only to restore the original v3 cache when needed.
        /// Keep usageContext stable across retries; other known contexts remain conservatively intersected.</summary>
        public static async Task<ModelPrebuildResult> BuildPublishedModelAsync(VirtualMeshModelAsset model,
            IEnumerable<Material[]> rendererMaterials, string usageContext,
            GlobalCoarseLodSettings coarseSettings = null, CancellationToken cancellationToken = default, bool saveAsset = true)
        {
            RequireEditorContext();
            if (model == null || !model.IsValid || model.SourceMesh == null || !EditorUtility.IsPersistent(model) ||
                AssetDatabase.GetAssetPath(model) != GetModelAssetPath(model.SourceMesh))
                throw new ArgumentException("A published model with its original persistent source mesh is required.", nameof(model));
            var uses = rendererMaterials?.Select(materials => materials == null ? null : (Material[])materials.Clone()).ToArray();
            if (string.IsNullOrEmpty(usageContext)) throw new ArgumentException("A stable material usage context is required.", nameof(usageContext));
            await s_PublicationGate.WaitAsync(cancellationToken);
            try { return await BuildMeshWithPolicyAsync(model.SourceMesh, model.SimplificationError, uses,
                usageContext, coarseSettings, cancellationToken, saveAsset); }
            finally { s_PublicationGate.Release(); }
        }

        private static void RequireEditorContext()
        {
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("Start model publication on the Unity editor thread.");
        }

        private static async Task<BuildResult> BuildAssetCoreAsync(string sourcePath, float simplificationError,
            bool buildCoarse, GlobalCoarseLodSettings coarseSettings, CancellationToken cancellationToken, bool generateImpostors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateError(simplificationError);
            if (string.IsNullOrEmpty(sourcePath) ||
                !sourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                IsGeneratedAsset(sourcePath))
                throw new ArgumentException("Choose a source model, prefab or mesh under Assets, outside the generated VirtualMeshModels folder.", nameof(sourcePath));

            var source = AssetDatabase.LoadMainAssetAtPath(sourcePath);
            if (!(source is GameObject) && !(source is Mesh))
                throw new ArgumentException("The selected asset is not a model, prefab or mesh.", nameof(sourcePath));

            string guid = AssetDatabase.AssetPathToGUID(sourcePath);
            string folder = $"{OutputRoot}/Prefabs/{guid}";
            EnsureFolder(folder);
            string[] existingPrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            string prefabPath = existingPrefabs.Length == 1
                ? AssetDatabase.GUIDToAssetPath(existingPrefabs[0])
                : $"{folder}/{SanitizeName(source.name)}.prefab";
            var result = new BuildResult { SourcePath = sourcePath, PrefabPath = prefabPath };
            var modelPaths = new HashSet<string>();
            var models = new Dictionary<Mesh, VirtualMeshModelAsset>();
            var modelResults = new List<ModelPrebuildResult>();
            var renderMaterials = new Dictionary<Material, Material>();
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            bool originalActive = true;
            try
            {
                if (source is GameObject sourceRoot)
                {
                    root = (GameObject)PrefabUtility.InstantiatePrefab(sourceRoot, previewScene);
                    if (root == null)
                    {
                        root = Object.Instantiate(sourceRoot);
                        SceneManager.MoveGameObjectToScene(root, previewScene);
                    }
                    if (PrefabUtility.IsPartOfPrefabInstance(root))
                        PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    root.name = sourceRoot.name;
                }
                else
                {
                    root = new GameObject(source.name);
                    SceneManager.MoveGameObjectToScene(root, previewScene);
                    root.AddComponent<MeshFilter>().sharedMesh = (Mesh)source;
                    root.AddComponent<MeshRenderer>().sharedMaterials =
                        Enumerable.Repeat(GetDefaultMaterial(), ((Mesh)source).subMeshCount).ToArray();
                }

                originalActive = root.activeSelf;
                root.SetActive(false); // No transient preview instances register while native work is awaited.
                // The model's own cluster hierarchy replaces authored LOD levels in the generated copy.
                // Keep the highest available level; never submit all source LODs on top of each other.
                // Include inactive/lower-LOD renderers in material safety analysis before removing
                // the generated prefab's LODGroup. A shared mesh must be safe in every known use.
                var materialUses = new Dictionary<Mesh, List<Material[]>>();
                foreach (var sourceFilter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();
                    var sourceMesh = sourceFilter.sharedMesh;
                    if (sourceMesh == null || sourceRenderer == null) continue;
                    if (!materialUses.TryGetValue(sourceMesh, out var uses))
                        materialUses.Add(sourceMesh, uses = new List<Material[]>());
                    uses.Add(sourceRenderer.sharedMaterials);
                }
                var excludedRenderers = RemoveLowerLods(root);
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var renderer = filter.GetComponent<MeshRenderer>();
                    var mesh = filter.sharedMesh;
                    if (renderer == null || mesh == null || excludedRenderers.Contains(renderer) || !HasTriangles(mesh))
                        continue;

                    if (!models.TryGetValue(mesh, out var model))
                    {
                        bool reused;
                        if (buildCoarse)
                        {
                            var built = await BuildMeshWithPolicyAsync(mesh, simplificationError, materialUses[mesh].ToArray(),
                                "asset:" + guid, coarseSettings, cancellationToken, saveAsset: false);
                            model = built.Model;
                            try { if (generateImpostors) await BuildMaterialImpostorsAsync(model, materialUses[mesh], built, cancellationToken); }
                            finally { if (model != null && EditorUtility.IsDirty(model)) AssetDatabase.SaveAssetIfDirty(model); }
                            reused = built.Reused || built.BaseCacheReused;
                            modelResults.Add(built);
                            if (built.Coarse?.Updated == true) result.CoarseUpdatedMeshes++;
                            if (built.Coarse?.Reused == true) result.CoarseReusedMeshes++;
                            result.PendingSubmeshes += built.PendingSubmeshes;
                            result.UnmetTargetSubmeshes += built.UnmetTargetSubmeshes;
                            result.ProxyBuiltVariants += built.ProxyBuiltVariants;
                            result.ProxyReusedVariants += built.ProxyReusedVariants;
                            result.PendingProxyVariants += built.PendingProxyVariants;
                        }
                        else
                        {
                            var policies = VirtualMeshModelPrebuildPolicy.Evaluate(mesh.subMeshCount, materialUses[mesh]);
                            var built = BuildMeshWithPolicySynchronous(mesh, simplificationError, policies, "asset:" + guid);
                            model = built.Model;
                            reused = built.Reused || built.BaseCacheReused;
                            result.PendingSubmeshes += built.PendingSubmeshes;
                            modelResults.Add(built);
                        }
                        models.Add(mesh, model);
                        modelPaths.Add(AssetDatabase.GetAssetPath(model));
                        if (reused) result.ReusedMeshes++;
                        else result.BuiltMeshes++;
                    }

                    var instance = filter.GetComponent<VirtualMeshInstance>();
                    if (instance == null) instance = filter.gameObject.AddComponent<VirtualMeshInstance>();
                    instance.enabled = false;
                    instance.Model = model;
                    instance.Materials = renderer.sharedMaterials;
                    instance.RenderMaterials = new Material[instance.Materials.Length];
                    for (int slot = 0; slot < instance.Materials.Length; slot++)
                    {
                        Material material = instance.Materials[slot];
                        if (material == null) continue;
                        if (!renderMaterials.TryGetValue(material, out var converted))
                        {
                            converted = BuildRenderMaterial(material);
                            renderMaterials.Add(material, converted);
                        }
                        instance.RenderMaterials[slot] = converted;
                    }
                    instance.SourceRenderer = renderer;
                    instance.PixelError = model.DefaultPixelError;
                    instance.TargetTrianglePixels = model.DefaultTargetTrianglePixels;
                    instance.PreviewInEditMode = true;
                    instance.enabled = renderer.enabled;
                    result.InstanceCount++;
                }

                if (result.InstanceCount == 0)
                    throw new InvalidOperationException("No static triangle meshes were found. Skinned meshes are not supported by the model cluster renderer.");

                root.SetActive(originalActive);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                if (!success) throw new IOException($"Could not save Virtual Mesh prefab '{prefabPath}'.");
                result.ModelPaths = modelPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
                result.ModelResults = modelResults.ToArray();
                return result;
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        private static ModelPrebuildResult BuildMeshWithPolicySynchronous(Mesh mesh, float simplificationError,
            VirtualMeshModelLodPolicySlot[] usagePolicies, string usageContext)
        {
            string path = GetModelAssetPath(mesh);
            var previous = AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(path);
            int revision = previous != null ? previous.Revision : 0;
            var contexts = VirtualMeshModelPrebuildPolicy.ReplaceContext(previous?.PrebuildPolicyContexts, usageContext, usagePolicies);
            var policies = VirtualMeshModelPrebuildPolicy.IntersectContexts(contexts);
            var options = VirtualMeshModelPrebuildPolicy.CreateSettings(null, policies);
            string hash = VirtualMeshModelPrebuildPolicy.GetBuildHash(options, policies);
            bool forceBase = previous != null && previous.CoarseLodVersion != 0 &&
                (previous.PrebuildPolicyHash != hash || previous.CoarseLodVersion != VirtualMeshGlobalCoarseLods.AlgorithmVersion);
            // Synchronous callers cannot wait on a Unity continuation. Still enforce material
            // safety: remove a now-ineligible coarse chain, then let the async entry extend it.
            var model = BuildMeshAssetInternal(mesh, simplificationError, out bool cacheReused, forceBase);
            if (model.PrebuildPolicyHash != hash || !SamePolicyContexts(model.PrebuildPolicyContexts, contexts))
            {
                model.SetPrebuildPolicyMetadata(VirtualMeshModelPrebuildPolicy.Version, hash, policies, contexts);
                EditorUtility.SetDirty(model);
                AssetDatabase.SaveAssetIfDirty(model);
            }
            return new ModelPrebuildResult { Model = model, ModelPath = path, Policies = policies,
                GeometryUpdated = model.Revision != revision, Reused = model == previous && model.Revision == revision,
                BaseCacheReused = cacheReused, PendingSubmeshes = policies.Count(p => p.Policy != VirtualMeshModelLodPolicy.Opaque) };
        }

        private static async Task BuildMaterialImpostorsAsync(VirtualMeshModelAsset model, IEnumerable<Material[]> rendererMaterials,
            ModelPrebuildResult result, CancellationToken cancellationToken)
        {
            var unique = new List<Material[]>();
            // Keep already registered scene material variants current after a source geometry
            // revision. Their arrays describe known uses even while the old captures are stale.
            var knownVariants = (model.ImpostorVariants ?? Array.Empty<VirtualMeshModelImpostorAsset>())
                .Where(asset => asset != null && asset.SourceModel == model).Select(asset => asset.SourceMaterials);
            foreach (var materials in rendererMaterials.Concat(knownVariants))
            {
                var snapshot = materials == null ? Array.Empty<Material>() : (Material[])materials.Clone();
                if (!unique.Any(previous => previous.SequenceEqual(snapshot))) unique.Add(snapshot);
            }
            var paths = new List<string>(); var pending = new List<string>();
            foreach (var materials in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var policies = VirtualMeshModelPrebuildPolicy.Evaluate(model.SubMeshCount, new[] { materials });
                // Ordinary opaque geometry already has the global triangle hierarchy. Only
                // coverage-sensitive/unknown material variants need this additional representation.
                if (policies.All(policy => policy.Policy == VirtualMeshModelLodPolicy.Opaque)) continue;
                string description = string.Join(", ", materials.Select(material => material != null ? material.name : "<missing>"));
                string reason = null;
                if (materials.Length != model.SubMeshCount) reason = "Requires exactly one material per submesh";
                else if (materials.Any(material => !VirtualMeshModelImpostorBuilder.IsSupportedMaterial(material)))
                    reason = "Source materials do not all expose a supported alpha-aware capture pass";
                else if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    reason = "GPU capture is unavailable in a no-graphics editor";
                if (reason == null)
                {
                    try
                    {
                        // This method remains inside the publication gate. The GPU baker never
                        // calls back into model publication, so there is no recursive gate wait.
                        var baked = await VirtualMeshModelImpostorBuilder.BuildAsync(model, materials,
                            new VirtualMeshImpostorBuildSettings(), saveAsset: false, cancellationToken: cancellationToken);
                        paths.Add(baked.AssetPath);
                        if (baked.Reused) result.ProxyReusedVariants++; else result.ProxyBuiltVariants++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { reason = exception.Message; }
                }
                if (reason != null) pending.Add(description + ": " + reason);
            }
            result.ProxyPaths = paths.ToArray(); result.ProxyPendingReasons = pending.ToArray();
            result.PendingProxyVariants = pending.Count;
        }

        private static async Task<ModelPrebuildResult> BuildMeshWithPolicyAsync(Mesh mesh, float simplificationError,
            Material[][] usageMaterials, string usageContext,
            GlobalCoarseLodSettings coarseSettings, CancellationToken cancellationToken, bool saveAsset = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateError(simplificationError);
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            // Classify after taking the publication gate: material properties can change while
            // another model is being built. Retain references for a second safety check at commit.
            var usagePolicies = VirtualMeshModelPrebuildPolicy.Evaluate(mesh.subMeshCount, usageMaterials);
            if (mesh == null || usagePolicies == null || usagePolicies.Length != mesh.subMeshCount)
                throw new ArgumentException("Material policies must cover every source mesh submesh.");
            string path = GetModelAssetPath(mesh);
            string sourcePath = AssetDatabase.GetAssetPath(mesh);
            string sourceHash = AssetDatabase.GetAssetDependencyHash(sourcePath).ToString();
            var published = AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(path);
            var contexts = VirtualMeshModelPrebuildPolicy.ReplaceContext(published?.PrebuildPolicyContexts,
                usageContext, usagePolicies);
            var policies = VirtualMeshModelPrebuildPolicy.IntersectContexts(contexts);
            var options = VirtualMeshModelPrebuildPolicy.CreateSettings(coarseSettings, policies);
            string policyHash = VirtualMeshModelPrebuildPolicy.GetBuildHash(options, policies);
            var result = new ModelPrebuildResult { Model = published, ModelPath = path, Policies = policies,
                PendingSubmeshes = policies.Count(p => p.Policy != VirtualMeshModelLodPolicy.Opaque) };
            bool current = published != null && published.IsValid && !EditorUtility.IsDirty(mesh) &&
                published.SourceMesh == mesh && published.SourceHash == sourceHash &&
                published.SimplificationError == simplificationError;
            bool sameBuild = current && published.PrebuildPolicyVersion == VirtualMeshModelPrebuildPolicy.Version &&
                published.PrebuildPolicyHash == policyHash && (options.Submeshes.Length == 0 ? published.CoarseLodVersion == 0 :
                    published.CoarseLodVersion == VirtualMeshGlobalCoarseLods.AlgorithmVersion);
            if (sameBuild || current && published.CoarseLodVersion == 0 && options.Submeshes.Length == 0)
            {
                if (options.Submeshes.Length > 0)
                    result.Coarse = await VirtualMeshGlobalCoarseLods.BuildAsync(published, options, false, cancellationToken);
                EnsureCullingData(published, false);
                if (!sameBuild || !SamePolicyContexts(published.PrebuildPolicyContexts, contexts))
                {
                    published.SetPrebuildPolicyMetadata(VirtualMeshModelPrebuildPolicy.Version, policyHash, policies, contexts);
                    EditorUtility.SetDirty(published);
                }
                if (saveAsset && EditorUtility.IsDirty(published)) AssetDatabase.SaveAssetIfDirty(published);
                result.Reused = true;
                result.UnmetTargetSubmeshes = result.Coarse?.Submeshes.Count(s => !s.TargetReached) ?? 0;
                return result;
            }

            int originalRevision = published != null ? published.Revision : 0;
            string originalPolicyHash = published != null ? published.PrebuildPolicyHash : null;
            var originalContexts = published != null ? published.PrebuildPolicyContexts : null;
            var stage = ScriptableObject.CreateInstance<VirtualMeshModelAsset>();
            bool copiedOriginalBase = current && published.CoarseLodVersion == 0;
            try
            {
                // Never append a second coarse chain. A changed policy/settings/algorithm starts
                // from the unchanged v3 Library cache, even if only one material slot changed.
                // Work on a transient asset so cancellation/failure leaves live geometry intact.
                if (copiedOriginalBase) CopyGeometry(published, stage);
                else BuildMeshAssetInternal(mesh, simplificationError, out result.BaseCacheReused, true, stage);
                if (options.Submeshes.Length > 0)
                    result.Coarse = await VirtualMeshGlobalCoarseLods.BuildAsync(stage, options, false, cancellationToken);
                stage.SetPrebuildPolicyMetadata(VirtualMeshModelPrebuildPolicy.Version, policyHash, policies, contexts);
                cancellationToken.ThrowIfCancellationRequested();
                var currentUsage = VirtualMeshModelPrebuildPolicy.Evaluate(mesh.subMeshCount, usageMaterials);
                if (!usagePolicies.Select(p => p.Policy).SequenceEqual(currentUsage.Select(p => p.Policy)))
                    throw new InvalidOperationException("A source material changed its LOD safety policy during prebuild; retry this export.");
                if (AssetDatabase.GetAssetDependencyHash(sourcePath).ToString() != stage.SourceHash ||
                    published != null && published.Revision != originalRevision ||
                    published != null && (published.PrebuildPolicyHash != originalPolicyHash ||
                        !ReferenceEquals(published.PrebuildPolicyContexts, originalContexts)) ||
                    AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(path) != published)
                    throw new InvalidOperationException("The source or published model changed during prebuild; retry this export.");

                bool create = published == null;
                if (create)
                {
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                        throw new InvalidOperationException("A different asset already occupies the model output path.");
                    published = ScriptableObject.CreateInstance<VirtualMeshModelAsset>();
                    published.name = mesh.name + " Clusters";
                }
                try
                {
                    result.GeometryUpdated = !copiedOriginalBase || result.Coarse?.Updated == true;
                    if (result.GeometryUpdated) CopyGeometry(stage, published);
                    if (stage.CoarseLodVersion != 0)
                        published.SetCoarseLodMetadata(stage.CoarseLodVersion, stage.CoarseLodSettingsHash,
                            stage.CoarseLodBaseClusterCount, stage.CoarseLodLevels, stage.DefaultPixelError, stage.DefaultTargetTrianglePixels);
                    published.SetPrebuildPolicyMetadata(VirtualMeshModelPrebuildPolicy.Version, policyHash, policies, contexts);
                    if (create) { EnsureFolder(OutputRoot + "/Meshes"); AssetDatabase.CreateAsset(published, path); }
                    EditorUtility.SetDirty(published);
                    if (saveAsset) AssetDatabase.SaveAssetIfDirty(published);
                    result.Model = published;
                    result.UnmetTargetSubmeshes = result.Coarse?.Submeshes.Count(s => !s.TargetReached) ?? 0;
                    return result;
                }
                catch
                {
                    if (create && published != null && !EditorUtility.IsPersistent(published)) Object.DestroyImmediate(published);
                    throw;
                }
            }
            finally { Object.DestroyImmediate(stage); }
        }

        private static void CopyGeometry(VirtualMeshModelAsset source, VirtualMeshModelAsset destination)
        {
            destination.SetData(source.SourceMesh, source.SimplificationError, source.SourceHash, source.Vertices,
                source.Indices, source.Clusters, source.SubMeshCount, source.Bounds);
            if (source.HasCullingData) destination.SetCullingData(source.CullingData);
        }

        private static bool SamePolicyContexts(VirtualMeshModelLodPolicyContext[] a, VirtualMeshModelLodPolicyContext[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null || a[i].SourceId != b[i].SourceId || a[i].Slots == null || a[i].Slots.Length != b[i].Slots.Length) return false;
                for (int slot = 0; slot < a[i].Slots.Length; slot++)
                    if (a[i].Slots[slot].Policy != b[i].Slots[slot].Policy || a[i].Slots[slot].Reason != b[i].Slots[slot].Reason) return false;
            }
            return true;
        }

        private static VirtualMeshModelAsset BuildMeshAssetInternal(Mesh mesh, float simplificationError, out bool reused,
            bool forceBase = false, VirtualMeshModelAsset destination = null)
        {
            ValidateError(simplificationError);
            if (mesh == null || !HasTriangles(mesh))
                throw new ArgumentException("The source mesh has no triangles.", nameof(mesh));

            string modelPath = GetModelAssetPath(mesh);
            string sourcePath = AssetDatabase.GetAssetPath(mesh);
            string sourceHash = AssetDatabase.GetAssetDependencyHash(sourcePath).ToString();
            var published = AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(modelPath);
            if (!forceBase && destination == null && published != null && published.IsValid && !EditorUtility.IsDirty(mesh) && published.SourceMesh == mesh &&
                published.SourceHash == sourceHash && published.SimplificationError == simplificationError)
            {
                reused = true;
                EnsureCullingData(published);
                return published;
            }
            reused = ModelClusterCache.TryLoad(mesh, simplificationError, out var source, out var key);
            if (!reused)
            {
                var submeshes = new ModelSubmeshClusters[mesh.subMeshCount];
                foreach (string _ in ModelClusterBuilder.Build(mesh, simplificationError, submeshes)) { }
                source = new ModelClusters(submeshes);
                // A missing/unwritable Library cache must not prevent publishing a valid model asset.
                if (!ModelClusterCache.Save(key, source))
                    Debug.LogWarning($"[Virtual Mesh] Could not save the optional Library cache for '{mesh.name}'. The publishable model asset will still be generated.", mesh);
            }

            if (AssetDatabase.GetAssetDependencyHash(sourcePath).ToString() != sourceHash)
                throw new InvalidOperationException($"'{sourcePath}' changed while its clusters were being built. Build the model again.");

            int vertexCount = 0, indexCount = 0, clusterCount = 0;
            checked
            {
                foreach (var submesh in source.Submeshes)
                    foreach (var group in submesh.Groups)
                    {
                        vertexCount += group.Vertices.Length;
                        indexCount += group.Indices.Length;
                        clusterCount += group.Clusters.Length;
                    }
            }

            var vertices = new VirtualMeshModelVertex[vertexCount];
            var indices = new uint[indexCount];
            var clusters = new VirtualMeshModelCluster[clusterCount];
            int vertexOffset = 0, indexOffset = 0, clusterOffset = 0;
            var totalBounds = new Bounds();
            bool haveBounds = false;
            for (int slot = 0; slot < source.Submeshes.Length; slot++)
            {
                foreach (var group in source.Submeshes[slot].Groups)
                {
                    ValidateGroup(group, null);
                    var levelSpacing = GetLevelTriangleSpacing(group);
                    var groupBounds = new Bounds(group.Vertices[0].position, Vector3.zero);
                    for (int i = 0; i < group.Vertices.Length; i++)
                    {
                        var vertex = group.Vertices[i];
                        groupBounds.Encapsulate(vertex.position);
                        vertices[vertexOffset + i] = new VirtualMeshModelVertex
                        {
                            position = vertex.position,
                            normal = vertex.normal,
                            tangent = vertex.tangent,
                            uv0 = vertex.uv0,
                            uv1 = vertex.uv1,
                            color = vertex.color
                        };
                    }
                    if (haveBounds) totalBounds.Encapsulate(groupBounds);
                    else { totalBounds = groupBounds; haveBounds = true; }
                    for (int i = 0; i < group.Indices.Length; i++)
                        indices[indexOffset + i] = checked((uint)vertexOffset + group.Indices[i]);

                    uint nextIndex = (uint)indexOffset;
                    foreach (var cluster in group.Clusters)
                    {
                        clusters[clusterOffset++] = new VirtualMeshModelCluster
                        {
                            center = groupBounds.center,
                            radius = groupBounds.extents.magnitude,
                            indexStart = nextIndex,
                            indexCount = (uint)cluster.IndexCount,
                            submesh = (uint)slot,
                            type = cluster.Type,
                            error = cluster.Error,
                            parentError = cluster.ParentError,
                            triangleSpacing = levelSpacing[cluster.Level],
                            parentTriangleSpacing = cluster.Type == 1 || cluster.Type == 2
                                ? levelSpacing[cluster.Level + 1] : 0f
                        };
                        nextIndex += (uint)cluster.IndexCount;
                    }
                    vertexOffset += group.Vertices.Length;
                    indexOffset += group.Indices.Length;
                }
            }

            EnsureFolder(OutputRoot + "/Meshes");
            var asset = destination != null ? destination : AssetDatabase.LoadAssetAtPath<VirtualMeshModelAsset>(modelPath);
            bool create = asset == null;
            if (create && AssetDatabase.LoadMainAssetAtPath(modelPath) != null)
                throw new InvalidOperationException($"A different asset already exists at '{modelPath}'.");
            if (create) asset = ScriptableObject.CreateInstance<VirtualMeshModelAsset>();
            try
            {
                // A geometry rebuild is not evidence that other exported material uses disappeared.
                var knownContexts = destination == null && asset.SourceMesh == mesh ? asset.PrebuildPolicyContexts : null;
                int knownPolicyVersion = asset.PrebuildPolicyVersion;
                string knownPolicyHash = asset.PrebuildPolicyHash;
                asset.name = mesh.name + " Clusters";
                asset.SetData(mesh, simplificationError, sourceHash, vertices, indices, clusters, mesh.subMeshCount, totalBounds);
                asset.EnsureCullingData();
                if (knownContexts != null && knownContexts.Length > 0 && knownPolicyVersion > 0)
                {
                    bool layoutChanged = knownContexts.Any(context => context == null || context.Slots == null ||
                        context.Slots.Length != mesh.subMeshCount);
                    if (layoutChanged)
                    {
                        // A changed submesh layout invalidates the old slot mapping, not the
                        // existence of other exports. Keep them unknown until each is reevaluated.
                        knownContexts = knownContexts.Where(context => context != null && !string.IsNullOrEmpty(context.SourceId))
                            .Select(context => new VirtualMeshModelLodPolicyContext { SourceId = context.SourceId,
                                Slots = context.Slots != null && context.Slots.Length == mesh.subMeshCount ? context.Slots :
                                    VirtualMeshModelPrebuildPolicy.Evaluate(mesh.subMeshCount, null) }).ToArray();
                    }
                    if (knownContexts.Length > 0)
                    {
                        var retainedPolicies = VirtualMeshModelPrebuildPolicy.IntersectContexts(knownContexts);
                        if (layoutChanged)
                        {
                            knownPolicyVersion = VirtualMeshModelPrebuildPolicy.Version;
                            knownPolicyHash = VirtualMeshModelPrebuildPolicy.GetBuildHash(
                                VirtualMeshModelPrebuildPolicy.CreateSettings(null, retainedPolicies), retainedPolicies);
                        }
                        asset.SetPrebuildPolicyMetadata(knownPolicyVersion, knownPolicyHash, retainedPolicies, knownContexts);
                    }
                }
                if (destination == null)
                {
                    if (create) AssetDatabase.CreateAsset(asset, modelPath);
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                }
                return asset;
            }
            catch
            {
                if (create && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
                throw;
            }
        }

        private static float[] GetLevelTriangleSpacing(ModelClusterGroup group)
        {
            var triangleCounts = new int[9];
            double originalArea = 0;
            int offset = 0;
            foreach (var cluster in group.Clusters)
            {
                if (cluster.Level < 0 || cluster.Level >= triangleCounts.Length)
                    throw new InvalidDataException("Invalid cluster LOD level. Rebuild the model cache.");
                triangleCounts[cluster.Level] += cluster.IndexCount / 3;
                if (cluster.Level == 0)
                    for (int i = offset; i < offset + cluster.IndexCount; i += 3)
                    {
                        Vector3 a = group.Vertices[group.Indices[i]].position;
                        Vector3 b = group.Vertices[group.Indices[i + 1]].position;
                        Vector3 c = group.Vertices[group.Indices[i + 2]].position;
                        originalArea += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                    }
                offset += cluster.IndexCount;
            }
            var spacing = new float[triangleCounts.Length];
            for (int level = 0; level < spacing.Length; level++)
            {
                if (triangleCounts[level] == 0) continue;
                spacing[level] = Mathf.Sqrt((float)(originalArea / triangleCounts[level]));
                if (!float.IsFinite(spacing[level]) || level > 0 &&
                    (triangleCounts[level - 1] == 0 || triangleCounts[level] >= triangleCounts[level - 1]))
                    throw new InvalidDataException("Invalid density LOD hierarchy.");
            }
            return spacing;
        }

        private static HashSet<Renderer> RemoveLowerLods(GameObject root)
        {
            var excluded = new HashSet<Renderer>();
            foreach (var lodGroup in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lodGroup.GetLODs();
                var keep = new HashSet<Renderer>();
                foreach (var lod in lods)
                {
                    foreach (var renderer in lod.renderers)
                        if (renderer != null) keep.Add(renderer);
                    if (keep.Count > 0) break;
                }
                foreach (var lod in lods)
                    foreach (var renderer in lod.renderers)
                        if (renderer != null && !keep.Contains(renderer))
                        {
                            renderer.enabled = false;
                            excluded.Add(renderer);
                            var instance = renderer.GetComponent<VirtualMeshInstance>();
                            if (instance != null) Object.DestroyImmediate(instance);
                        }
                Object.DestroyImmediate(lodGroup);
            }
            return excluded;
        }

        private static Material GetDefaultMaterial()
        {
            const string path = OutputRoot + "/Default Material.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit is required to create a default mesh material.");
            EnsureFolder(OutputRoot);
            material = new Material(shader) { name = "Virtual Mesh Default" };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>
        /// Verifies source-cache LOD coverage at every transition and compares original triangle counts.
        /// Does not open, save or alter any user scene. Throws on a malformed hierarchy.
        /// </summary>
        public static ValidationResult ValidateSourceClusters(Mesh mesh,
            float simplificationError = DefaultSimplificationError)
        {
            BuildMeshAsset(mesh, simplificationError);
            if (!ModelClusterCache.TryLoad(mesh, simplificationError, out var source, out _))
            {
                var submeshes = new ModelSubmeshClusters[mesh.subMeshCount];
                foreach (string _ in ModelClusterBuilder.Build(mesh, simplificationError, submeshes)) { }
                source = new ModelClusters(submeshes);
            }

            var result = new ValidationResult { MeshCount = 1 };
            foreach (var submesh in source.Submeshes)
            {
                long leafIndices = 0;
                foreach (var group in submesh.Groups)
                {
                    ValidateGroup(group, result);
                    for (int i = group.Clusters.Length - group.LeafClusterCount; i < group.Clusters.Length; i++)
                        leafIndices += group.Clusters[i].IndexCount;
                }
                if (leafIndices != submesh.InputIndexCount)
                    throw new InvalidDataException($"Cluster leaves contain {leafIndices} indices, expected {submesh.InputIndexCount} for '{mesh.name}'.");
                result.LeafIndexCount += leafIndices;
            }
            return result;
        }

        private static void ValidateGroup(ModelClusterGroup group, ValidationResult result)
        {
            if (group.Vertices.Length == 0 || group.Clusters.Length == 0 || group.LeafClusterCount <= 0 ||
                group.LeafClusterCount > group.Clusters.Length)
                throw new InvalidDataException("Invalid model cluster group.");
            var thresholds = new SortedSet<float> { 0f };
            foreach (var cluster in group.Clusters)
            {
                if (cluster.Type > 3 || cluster.IndexCount <= 0 || cluster.IndexCount % 3 != 0 ||
                    cluster.IndexCount > ModelClusterBuilder.ClusterTriangleCount * 3 ||
                    !float.IsFinite(cluster.Error) || !float.IsFinite(cluster.ParentError) ||
                    cluster.Error < 0f || cluster.ParentError < 0f || cluster.Level < 0 || cluster.Level > 8)
                    throw new InvalidDataException("Invalid cluster hierarchy metadata.");
                thresholds.Add(cluster.Error);
                thresholds.Add(cluster.ParentError);
            }
            var samples = new List<float>(thresholds);
            var boundaries = thresholds.ToArray();
            for (int i = 1; i < boundaries.Length; i++)
                samples.Add(boundaries[i - 1] + (boundaries[i] - boundaries[i - 1]) * 0.5f);
            samples.Add(boundaries[boundaries.Length - 1] + Mathf.Max(1f, boundaries[boundaries.Length - 1]));
            foreach (float tolerance in samples)
            {
                int selected = 0;
                int selectedLevel = -1;
                foreach (var cluster in group.Clusters)
                {
                    bool visible = cluster.Type == 0 ||
                                   (cluster.Type == 1 && tolerance < cluster.ParentError) ||
                                   (cluster.Type == 2 && cluster.Error <= tolerance && tolerance < cluster.ParentError) ||
                                   (cluster.Type == 3 && cluster.Error <= tolerance);
                    if (!visible) continue;
                    if (selected > 0 && selectedLevel != cluster.Level)
                        throw new InvalidDataException("Two LOD levels overlap at a cluster-group transition.");
                    selected++;
                    selectedLevel = cluster.Level;
                }
                if (selected == 0)
                    throw new InvalidDataException("The cluster hierarchy leaves a gap at an LOD transition.");
                if (selected != group.Clusters.Count(cluster => cluster.Level == selectedLevel))
                    throw new InvalidDataException("Only part of an LOD level was selected.");
            }
            if (result != null)
            {
                result.GroupCount++;
                result.ClusterCount += group.Clusters.Length;
                result.LodSamplesChecked += samples.Count;
            }
        }

        /// <summary>Creates a referenced material asset so Player builds retain the required shader variants.</summary>
        public static Material BuildRenderMaterial(Material source)
        {
            if (source == null || source.shader == null) return null;
            if (source.renderQueue > 2500 || source.HasProperty("_Surface") && source.GetFloat("_Surface") > 0.5f)
                return null;
            if (!string.IsNullOrEmpty(source.GetTag(VirtualMeshMaterialSupport.ShadingTag, false, string.Empty)))
                return source;
            bool unlit = source.shader.name == "Universal Render Pipeline/Unlit";
            string shaderName = VirtualMeshMaterialSupport.GetRenderShaderName(source.shader.name);
            if (shaderName == null) return null;

            var shader = Shader.Find(shaderName);
            if (shader == null && shaderName == "Virtual Mesh/Model Lit") shader = Resources.Load<Shader>("VirtualMeshModelLit");
            if (shader == null)
                throw new InvalidOperationException("The model shader '" + shaderName + "' is unavailable. Wait for shader import to complete before building a model.");
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId))
                throw new ArgumentException("Source materials must be saved assets.", nameof(source));

            string folder = OutputRoot + "/Materials";
            EnsureFolder(folder);
            string path = $"{folder}/{guid}-{localId:x16}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = material == null;
            if (create && AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new InvalidOperationException($"A different asset already exists at '{path}'.");
            if (create) material = new Material(shader);
            else material.shader = shader;
            material.name = source.name + " (Virtual Mesh)";
            material.CopyPropertiesFromMaterial(source);
            material.shaderKeywords = source.shaderKeywords;
            if (material.HasProperty("_VMUnlit")) material.SetFloat("_VMUnlit", unlit ? 1f : 0f);
            if (material.HasProperty("_VMShadowCull")) material.SetFloat("_VMShadowCull", VirtualMeshMaterialSupport.GetShadowCull(source));
            material.enableInstancing = true;
            if (create) AssetDatabase.CreateAsset(material, path);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private static bool HasTriangles(Mesh mesh)
        {
            for (int slot = 0; slot < mesh.subMeshCount; slot++)
                if (mesh.GetTopology(slot) == MeshTopology.Triangles && mesh.GetIndexCount(slot) >= 3)
                    return true;
            return false;
        }

        private static void ValidateError(float error)
        {
            if (float.IsNaN(error) || float.IsInfinity(error) || error <= 0f)
                throw new ArgumentOutOfRangeException(nameof(error), "Simplification error must be finite and positive.");
        }

        private static string SanitizeName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrEmpty(name) ? "Model" : name;
        }

        private static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string parent = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string child = parent + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(child)) AssetDatabase.CreateFolder(parent, parts[i]);
                parent = child;
            }
        }
    }
}
