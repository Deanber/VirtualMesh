using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Unity.VirtualMesh.Editor
{
    internal sealed class VirtualMeshModelPrebuilder : AssetPostprocessor
    {
        private const string k_AutoPrebuildLabel = "VirtualMeshPrebuild";
        private const string k_BakeMenu = "Assets/Virtual Mesh/Bake Model Clusters";
        private const string k_DisableMenu = "Assets/Virtual Mesh/Disable Automatic Cluster Bake";

        private static readonly HashSet<string> s_PendingAssets = new HashSet<string>();
        private static readonly CancellationTokenSource s_Shutdown = new CancellationTokenSource();
        private static bool s_Running;

        static VirtualMeshModelPrebuilder()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => s_Shutdown.Cancel();
            EditorApplication.quitting += () => s_Shutdown.Cancel();
        }

        [MenuItem(k_BakeMenu, false, 2000)]
        private static void BakeSelectedAssets()
        {
            foreach (string guid in Selection.assetGUIDs)
                BakeSourceAsset(AssetDatabase.GUIDToAssetPath(guid));
        }

        internal static void BakeSourceAsset(string path)
        {
            if (!IsSupportedAsset(path))
                return;

            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return;

            var labels = AssetDatabase.GetLabels(asset);
            if (!labels.Contains(k_AutoPrebuildLabel))
                AssetDatabase.SetLabels(asset, labels.Append(k_AutoPrebuildLabel).ToArray());

            QueuePrebuild(AssetDatabase.AssetPathToGUID(path));
        }

        [MenuItem(k_BakeMenu, true)]
        private static bool CanBakeSelectedAssets()
        {
            return Selection.assetGUIDs.Any(guid => IsSupportedAsset(AssetDatabase.GUIDToAssetPath(guid)));
        }

        [MenuItem(k_DisableMenu, false, 2001)]
        private static void DisableAutomaticBake()
        {
            foreach (string guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsSupportedAsset(path) || !HasPrebuildLabel(path))
                    continue;

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                    continue;

                var labels = AssetDatabase.GetLabels(asset);
                AssetDatabase.SetLabels(asset, labels.Where(label => label != k_AutoPrebuildLabel).ToArray());
                s_PendingAssets.Remove(guid);
            }
        }

        [MenuItem(k_DisableMenu, true)]
        private static bool CanDisableAutomaticBake()
        {
            foreach (string guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsSupportedAsset(path) && HasPrebuildLabel(path))
                    return true;
            }

            return false;
        }

        private static bool IsProjectAsset(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedAsset(string path)
        {
            if (!IsProjectAsset(path) || VirtualMeshModelAssetBuilder.IsGeneratedAsset(path))
                return false;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type == typeof(GameObject) || type == typeof(Mesh);
        }

        private static bool HasPrebuildLabel(string path)
        {
            return AssetDatabase.GetLabels(AssetDatabase.GUIDFromAssetPath(path)).Contains(k_AutoPrebuildLabel);
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var changedSourceMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in importedAssets.Concat(movedAssets))
            {
                if (!IsProjectAsset(path) || VirtualMeshModelAssetBuilder.IsGeneratedAsset(path))
                    continue;
                if (HasPrebuildLabel(path) && IsSupportedAsset(path))
                    QueuePrebuild(AssetDatabase.AssetPathToGUID(path));
                if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    changedSourceMaterials.Add(path);
            }

            // A material can change keywords without causing its FBX to reimport. Refresh the
            // published materials too, so the Player build sees the same variants as the Editor.
            // Generated material imports never enter this branch or trigger a rebuild loop.
            if (changedSourceMaterials.Count == 0) return;
            foreach (string guid in AssetDatabase.FindAssets("l:" + k_AutoPrebuildLabel))
            {
                string sourcePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsSupportedAsset(sourcePath)) continue;
                if (AssetDatabase.GetDependencies(sourcePath, true).Any(changedSourceMaterials.Contains))
                    QueuePrebuild(guid);
            }
        }

        private static void QueuePrebuild(string guid)
        {
            if (!s_PendingAssets.Add(guid))
                return;

            if (s_Running) return;
            EditorApplication.delayCall -= PrebuildPendingAssets;
            EditorApplication.delayCall += PrebuildPendingAssets;
        }

        private static async void PrebuildPendingAssets()
        {
            if (s_Running || s_Shutdown.IsCancellationRequested) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += PrebuildPendingAssets;
                return;
            }

            s_Running = true;
            int built = 0;
            int reused = 0;
            int instances = 0;
            int coarse = 0, pendingSlots = 0, unmet = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // A source imported again while a worker is running remains queued for another
                // pass. The builder also serializes explicit API callers with this queue.
                while (s_PendingAssets.Count > 0 && !s_Shutdown.IsCancellationRequested)
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating) break;
                    string guid = s_PendingAssets.First();
                    s_PendingAssets.Remove(guid);
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!IsSupportedAsset(path) || !HasPrebuildLabel(path))
                        continue;
                    try
                    {
                        var result = await VirtualMeshModelAssetBuilder.BuildAssetAsync(path, cancellationToken: s_Shutdown.Token);
                        built += result.BuiltMeshes;
                        reused += result.ReusedMeshes;
                        instances += result.InstanceCount;
                        coarse += result.CoarseUpdatedMeshes;
                        pendingSlots += result.PendingSubmeshes;
                        unmet += result.UnmetTargetSubmeshes;
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
                        Debug.Log($"[Virtual Mesh] Published '{result.PrefabPath}'. Global coarse LOD updated on {result.CoarseUpdatedMeshes} meshes; {result.PendingSubmeshes} slots retain their base cluster hierarchy, {result.UnmetTargetSubmeshes} slots stopped above the triangle target. Drag this prefab into a scene with the Virtual Mesh renderer feature.", prefab);
                    }
                    catch (OperationCanceledException) when (s_Shutdown.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        Debug.LogError($"[Virtual Mesh] Could not publish model clusters for '{path}'.\n{exception}",
                            AssetDatabase.LoadMainAssetAtPath(path));
                    }
                    await System.Threading.Tasks.Task.Yield();
                }
            }
            finally
            {
                s_Running = false;
                if (s_PendingAssets.Count > 0 && !s_Shutdown.IsCancellationRequested)
                {
                    EditorApplication.delayCall -= PrebuildPendingAssets;
                    EditorApplication.delayCall += PrebuildPendingAssets;
                }
            }
            if (built + reused > 0)
                Debug.Log($"[Virtual Mesh] Model Bake finished in {timer.Elapsed.TotalSeconds:F2}s. Built {built} meshes, reused {reused}, published {instances} prefab instances; {coarse} coarse upgrades, {pendingSlots} base-only slots, {unmet} unmet triangle targets.");
        }
    }
}
