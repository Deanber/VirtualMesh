using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Unity.VirtualMesh.Editor
{
    [Serializable]
    public sealed class VirtualMeshImpostorBuildSettings
    {
        public int AzimuthViews = 8;
        public float[] Elevations = { -60f, 0f, 60f };
        public int Resolution = 256;
        public float BoundsPadding = .02f;
        public float MaxScreenHeight = .15f;
    }

    [Serializable]
    public sealed class VirtualMeshImpostorBuildResult
    {
        public bool Reused;
        public string AssetPath, BakeHash, CoverageSnapshot;
        public int SourceTriangles, SourceVertices, ViewCount, Resolution;
        public VirtualMeshModelImpostorAsset Asset;
    }

    /// <summary>Explicit editor-thread GPU capture. Source leaf geometry (types0/1) from every
    /// material slot is drawn together. Original assets/materials are never changed.
    /// Known Source Hornbeam/Foliage shaders supply their real VMImpostorBake pass.</summary>
    public static class VirtualMeshModelImpostorBuilder
    {
        public const int AlgorithmVersion = 2;
        public const string CapturePassName = "VMImpostorBake";
        const string BaseCoverageSnapshot = VirtualMeshModelImpostorAsset.CoveragePolicy;

        public static bool IsSupportedMaterial(Material material) => material != null && material.shader != null &&
            (material.shader.name == "ElectricDreams/Source Hornbeam" || material.shader.name == "ElectricDreams/Source Foliage") &&
            material.FindPass(CapturePassName) >= 0;

        /// <summary>Automatic content-addressed publication when assetPath is null. Call only from
        /// Unity's editor synchronization context; do not launch a second Editor for this build.</summary>
        public static async Task<VirtualMeshImpostorBuildResult> BuildAsync(VirtualMeshModelAsset model,
            Material[] sourceMaterials, VirtualMeshImpostorBuildSettings settings = null,
            string assetPath = null, bool saveAsset = true, CancellationToken cancellationToken = default)
        {
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("Start impostor capture on the Unity editor thread.");
            cancellationToken.ThrowIfCancellationRequested();
            var options = Snapshot(settings ?? new VirtualMeshImpostorBuildSettings());
            ValidateSources(model, sourceMaterials);
            var materials = (Material[])sourceMaterials.Clone();
            int revision = model.Revision;
            string identity = ComputeBakeHash(model, materials, options);
            if (string.IsNullOrEmpty(assetPath))
            {
                string modelGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model));
                if (string.IsNullOrEmpty(modelGuid)) throw new ArgumentException("Publish the source model before building its impostor.");
                assetPath = "Assets/VirtualMeshModels/Impostors/" + modelGuid + "/" + identity + ".asset";
            }
            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
                assetPath.Contains("../") || assetPath.Contains("/./"))
                throw new ArgumentException("Impostor assets must use a project-relative Assets/.../*.asset path.");
            var existing = AssetDatabase.LoadAssetAtPath<VirtualMeshModelImpostorAsset>(assetPath);
            if (existing != null && existing.BakeHash == identity && existing.MatchesSource(model, materials))
            {
                RegisterVariant(model, existing, saveAsset);
                return new VirtualMeshImpostorBuildResult { Reused = true, Asset = existing, AssetPath = assetPath,
                    BakeHash = identity, ViewCount = existing.ViewCount, Resolution = options.Resolution, CoverageSnapshot = BaseCoverageSnapshot };
            }
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                throw new InvalidOperationException("An unrelated or stale asset already occupies the impostor path. Use the automatic content-addressed path.");
            if (!SystemInfo.supports2DArrayTextures || SystemInfo.supportedRenderTargetCount < 3 ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render))
                throw new NotSupportedException("Impostor capture requires texture arrays and three MRTs with RGBA8/RFloat support.");

            Mesh mesh = null;
            Material[] clones = new Material[materials.Length];
            RenderTexture colorTarget = null, normalTarget = null, depthTarget = null, zTarget = null;
            Texture2DArray colorArray = null, normalArray = null, depthArray = null;
            Texture2D colorReadback = null, normalReadback = null, depthReadback = null;
            VirtualMeshModelImpostorAsset asset = null;
            bool published = false, createdAsset = false;
            var oldActive = RenderTexture.active;
            bool oldAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            int CenterId = Shader.PropertyToID("_VMImpostorCenter"), DirectionId = Shader.PropertyToID("_VMImpostorDirectionDepth"),
                CameraPositionId = Shader.PropertyToID("_WorldSpaceCameraPos"), AlphaToMaskId = Shader.PropertyToID("_AlphaToMaskAvailable");
            Vector4 oldCenter = Shader.GetGlobalVector(CenterId), oldDirection = Shader.GetGlobalVector(DirectionId), oldCamera = Shader.GetGlobalVector(CameraPositionId);
            float oldAlphaToMask = Shader.GetGlobalFloat(AlphaToMaskId);
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                mesh = BuildSourceLeafMesh(model, cancellationToken, out int sourceTriangles);
                for (int slot = 0; slot < clones.Length; slot++)
                {
                    clones[slot] = new Material(materials[slot]) { name = materials[slot].name + " (Impostor Capture)", hideFlags = HideFlags.HideAndDontSave };
                    // Use the least-dense endpoint of the source distance-opacity range. This avoids
                    // silently increasing coverage when FarOpacityStrength is below1. It is still
                    // a fixed directional snapshot, not a certified conservative HiZ occluder.
                    if (clones[slot].HasProperty("_DistanceOpacity") && clones[slot].GetFloat("_DistanceOpacity") > .5f &&
                        (!clones[slot].HasProperty("_SourceBranch") || clones[slot].GetFloat("_SourceBranch") < .5f))
                    {
                        float strength = clones[slot].HasProperty("_FarOpacityStrength") ? clones[slot].GetFloat("_FarOpacityStrength") : 1;
                        float intensity = clones[slot].GetFloat("_OpacityIntensity");
                        if (!Finite(strength) || !Finite(intensity)) throw new InvalidDataException("Source opacity parameters must be finite.");
                        clones[slot].SetFloat("_OpacityIntensity", intensity * Mathf.Min(1, Mathf.Max(0, strength)));
                    }
                    SetIfPresent(clones[slot], "_WindEnabled", 0);
                    SetIfPresent(clones[slot], "_WindAmplitude", 0);
                    SetIfPresent(clones[slot], "_DistanceOpacity", 0);
                    // Never persist a pending/error placeholder variant into the baked arrays.
                    ShaderUtil.CompilePass(clones[slot], clones[slot].FindPass(CapturePassName), true);
                }
                var views = BuildViews(model.Bounds, options);
                colorArray = NewArray(options.Resolution, views.Length, TextureFormat.RGBA32, "AlbedoCoverage");
                normalArray = NewArray(options.Resolution, views.Length, TextureFormat.RGBA32, "ObjectNormalRoughness");
                depthArray = NewArray(options.Resolution, views.Length, TextureFormat.RFloat, "LocalDepth");
                colorTarget = NewTarget(options.Resolution, GraphicsFormat.R8G8B8A8_UNorm, "Impostor Albedo Coverage");
                normalTarget = NewTarget(options.Resolution, GraphicsFormat.R8G8B8A8_UNorm, "Impostor Object Normal Roughness");
                depthTarget = NewTarget(options.Resolution, GraphicsFormat.R32_SFloat, "Impostor Local Depth");
                zTarget = new RenderTexture(options.Resolution, options.Resolution, 32, RenderTextureFormat.Depth)
                    { name = "Impostor Capture Z", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
                if (!zTarget.Create()) throw new InvalidOperationException("Failed to allocate impostor depth attachment.");
                colorReadback = NewReadback(options.Resolution, TextureFormat.RGBA32);
                normalReadback = NewReadback(options.Resolution, TextureFormat.RGBA32);
                depthReadback = NewReadback(options.Resolution, TextureFormat.RFloat);
                var targets = new RenderTargetIdentifier[] { colorTarget, normalTarget, depthTarget };
                for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var view = views[viewIndex];
                    BuildMatrices(model.Bounds.center, view, out var viewMatrix, out var projection, out var eye);
                    using (var commands = new CommandBuffer { name = "VirtualMesh Impostor " + viewIndex })
                    {
                        commands.SetRenderTarget(targets, zTarget);
                        commands.SetViewport(new Rect(0, 0, options.Resolution, options.Resolution));
                        // These command APIs take Unity's canonical depth/projection conventions;
                        // the graphics backend performs reversed-Z and GPU projection conversion.
                        commands.ClearRenderTarget(true, true, Color.clear, 1f);
                        commands.SetViewProjectionMatrices(viewMatrix, projection);
                        commands.SetGlobalVector(CenterId, model.Bounds.center);
                        commands.SetGlobalVector(DirectionId, view.DirectionDepth);
                        commands.SetGlobalVector(CameraPositionId, eye);
                        // ShaderLab AlphaToMask Off does not reset URP's availability global.
                        // Single-sample captures require actual cutoff/discard, not an A2C alpha
                        // that this MRT pass subsequently replaces with binary hit coverage.
                        commands.SetGlobalFloat(AlphaToMaskId, 0);
                        for (int slot = 0; slot < clones.Length; slot++)
                            commands.DrawMesh(mesh, Matrix4x4.identity, clones[slot], slot, clones[slot].FindPass(CapturePassName));
                        Graphics.ExecuteCommandBuffer(commands);
                    }
                    ReadLayer(colorTarget, colorReadback, colorArray, viewIndex);
                    ReadLayer(normalTarget, normalReadback, normalArray, viewIndex);
                    ReadLayer(depthTarget, depthReadback, depthArray, viewIndex);
                    // Permit editor input/cancellation between captures. The continuation stays
                    // on Unity's synchronization context; all engine APIs remain on that thread.
                    await Task.Yield();
                }
                colorArray.Apply(false, false); normalArray.Apply(false, false); depthArray.Apply(false, false);
                cancellationToken.ThrowIfCancellationRequested();
                if (model == null || model.Revision != revision || ComputeBakeHash(model, materials, options) != identity)
                    throw new InvalidOperationException("Source geometry, materials or capture settings changed during impostor capture.");
                asset = ScriptableObject.CreateInstance<VirtualMeshModelImpostorAsset>();
                asset.name = model.name + " (Whole Model Impostor)";
                asset.SetData(model, materials, identity, views, colorArray, normalArray, depthArray, options.MaxScreenHeight, 0);
                EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
                AssetDatabase.CreateAsset(asset, assetPath); createdAsset = true;
                AssetDatabase.AddObjectToAsset(colorArray, asset);
                AssetDatabase.AddObjectToAsset(normalArray, asset);
                AssetDatabase.AddObjectToAsset(depthArray, asset);
                EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset);
                published = true;
                RegisterVariant(model, asset, saveAsset);
                return new VirtualMeshImpostorBuildResult { Asset = asset, AssetPath = assetPath, BakeHash = identity,
                    SourceTriangles = sourceTriangles, SourceVertices = mesh.vertexCount, ViewCount = views.Length,
                    Resolution = options.Resolution, CoverageSnapshot = BaseCoverageSnapshot };
            }
            catch
            {
                if (createdAsset && !published) AssetDatabase.DeleteAsset(assetPath);
                throw;
            }
            finally
            {
                RenderTexture.active = oldActive;
                ShaderUtil.allowAsyncCompilation = oldAsyncCompilation;
                Shader.SetGlobalVector(CenterId, oldCenter); Shader.SetGlobalVector(DirectionId, oldDirection); Shader.SetGlobalVector(CameraPositionId, oldCamera);
                Shader.SetGlobalFloat(AlphaToMaskId, oldAlphaToMask);
                Destroy(mesh); foreach (var material in clones) Destroy(material);
                Release(colorTarget); Release(normalTarget); Release(depthTarget); Release(zTarget);
                Destroy(colorReadback); Destroy(normalReadback); Destroy(depthReadback);
                if (!published) { Destroy(colorArray); Destroy(normalArray); Destroy(depthArray); Destroy(asset); }
            }
        }

        public static VirtualMeshImpostorView[] BuildViews(Bounds bounds, VirtualMeshImpostorBuildSettings settings)
        {
            var options = Snapshot(settings ?? new VirtualMeshImpostorBuildSettings());
            var extent = bounds.extents;
            var center = bounds.center;
            if (!Finite(center.x) || !Finite(center.y) || !Finite(center.z) || !Finite(extent.x) || !Finite(extent.y) || !Finite(extent.z) || extent.x < 0 || extent.y < 0 || extent.z < 0 || extent.sqrMagnitude <= 0)
                throw new ArgumentException("Impostor source bounds must be finite and nonempty.");
            var output = new VirtualMeshImpostorView[options.AzimuthViews * options.Elevations.Length];
            float padding = Mathf.Max(.00001f, extent.magnitude * options.BoundsPadding);
            int index = 0;
            foreach (float elevation in options.Elevations)
                for (int azimuth = 0; azimuth < options.AzimuthViews; azimuth++)
                {
                    float yaw = azimuth * (Mathf.PI * 2 / options.AzimuthViews), pitch = elevation * Mathf.Deg2Rad;
                    var direction = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
                    var right = Vector3.Cross(Vector3.up, direction).normalized;
                    var up = Vector3.Cross(direction, right).normalized;
                    output[index++] = new VirtualMeshImpostorView {
                        RightExtent = new Vector4(right.x, right.y, right.z, ProjectExtent(extent, right) + padding),
                        UpExtent = new Vector4(up.x, up.y, up.z, ProjectExtent(extent, up) + padding),
                        DirectionDepth = new Vector4(direction.x, direction.y, direction.z, ProjectExtent(extent, direction) + padding) };
                }
            return output;
        }

        static void BuildMatrices(Vector3 center, VirtualMeshImpostorView basis, out Matrix4x4 view, out Matrix4x4 projection, out Vector3 eye)
        {
            const float near = .01f;
            Vector3 right = basis.RightExtent, up = basis.UpExtent, direction = basis.DirectionDepth;
            eye = center + direction * (basis.DirectionDepth.w + near);
            view = Matrix4x4.identity;
            view.SetRow(0, new Vector4(right.x, right.y, right.z, -Vector3.Dot(right, eye)));
            view.SetRow(1, new Vector4(up.x, up.y, up.z, -Vector3.Dot(up, eye)));
            view.SetRow(2, new Vector4(direction.x, direction.y, direction.z, -Vector3.Dot(direction, eye)));
            projection = Matrix4x4.Ortho(-basis.RightExtent.w, basis.RightExtent.w, -basis.UpExtent.w,
                basis.UpExtent.w, near, basis.DirectionDepth.w * 2 + near);
        }

        static Mesh BuildSourceLeafMesh(VirtualMeshModelAsset model, CancellationToken cancellation, out int triangles)
        {
            var source = model.Vertices; var sourceIndices = model.Indices;
            var remap = new int[source.Length]; Array.Fill(remap, -1);
            var compact = new List<VirtualMeshModelVertex>();
            var slots = new List<int>[model.SubMeshCount]; for (int i = 0; i < slots.Length; i++) slots[i] = new List<int>();
            long count = 0;
            foreach (var cluster in model.Clusters)
            {
                if (cluster.type != 0 && cluster.type != 1) continue;
                cancellation.ThrowIfCancellationRequested();
                ulong end = (ulong)cluster.indexStart + cluster.indexCount;
                if (cluster.submesh >= slots.Length || end > (ulong)sourceIndices.Length || cluster.indexCount % 3 != 0)
                    throw new InvalidDataException("Invalid source leaf range while building impostor capture geometry.");
                var slot = slots[cluster.submesh];
                for (ulong i = cluster.indexStart; i < end; i++)
                {
                    uint original = sourceIndices[(int)i];
                    if (original >= source.Length) throw new InvalidDataException("Source leaf index exceeds the vertex buffer.");
                    int mapped = remap[original];
                    if (mapped < 0) { mapped = compact.Count; compact.Add(source[original]); remap[original] = mapped; }
                    slot.Add(mapped); count++;
                }
            }
            if (count == 0 || count / 3 > int.MaxValue) throw new InvalidDataException("No valid original leaf geometry is available for capture.");
            triangles = checked((int)(count / 3));
            var positions = new Vector3[compact.Count]; var normals = new Vector3[compact.Count]; var tangents = new Vector4[compact.Count];
            var colors = new Color[compact.Count]; var uv = new Vector2[compact.Count]; var uv1 = new Vector2[compact.Count];
            for (int i = 0; i < compact.Count; i++)
            {
                var v = compact[i]; positions[i] = v.position; normals[i] = v.normal; tangents[i] = v.tangent;
                colors[i] = v.color; uv[i] = v.uv0; uv1[i] = v.uv1;
            }
            var mesh = new Mesh { name = model.name + " (Original Leaf Capture)", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = positions; mesh.normals = normals; mesh.tangents = tangents; mesh.colors = colors; mesh.uv = uv; mesh.uv2 = uv1;
            mesh.subMeshCount = slots.Length;
            for (int i = 0; i < slots.Length; i++) mesh.SetIndices(slots[i], MeshTopology.Triangles, i, false);
            mesh.bounds = model.Bounds;
            return mesh;
        }

        static VirtualMeshImpostorBuildSettings Snapshot(VirtualMeshImpostorBuildSettings source)
        {
            if (source.AzimuthViews < 4 || source.AzimuthViews > 32 || source.Elevations == null || source.Elevations.Length < 1 || source.Elevations.Length > 8 ||
                source.Resolution < 32 || source.Resolution > 1024 || (source.Resolution & (source.Resolution - 1)) != 0 ||
                !Finite(source.BoundsPadding) || source.BoundsPadding < 0 || source.BoundsPadding > .25f ||
                !Finite(source.MaxScreenHeight) || source.MaxScreenHeight <= 0 || source.MaxScreenHeight > 1)
                throw new ArgumentException("Invalid impostor capture settings.");
            foreach (float angle in source.Elevations) if (!Finite(angle) || angle <= -89 || angle >= 89) throw new ArgumentException("Capture elevations must lie strictly between -89 and89 degrees.");
            return new VirtualMeshImpostorBuildSettings { AzimuthViews = source.AzimuthViews, Elevations = (float[])source.Elevations.Clone(),
                Resolution = source.Resolution, BoundsPadding = source.BoundsPadding, MaxScreenHeight = source.MaxScreenHeight };
        }

        static void ValidateSources(VirtualMeshModelAsset model, Material[] materials)
        {
            if (model == null || !model.IsValid || materials == null || materials.Length != model.SubMeshCount)
                throw new ArgumentException("A valid model and exactly one source material per submesh are required.");
            for (int slot = 0; slot < materials.Length; slot++)
                if (!IsSupportedMaterial(materials[slot]))
                    throw new NotSupportedException("Material slot " + slot + " must use Source Hornbeam/Foliage with a VMImpostorBake pass. Capture was not replaced with an opaque fallback.");
        }

        static string ComputeBakeHash(VirtualMeshModelAsset model, Material[] materials, VirtualMeshImpostorBuildSettings options)
        {
            var text = new StringBuilder();
            text.Append(AlgorithmVersion).Append('|').Append(BaseCoverageSnapshot).Append('|').Append(model.SourceHash).Append('|').Append(model.Revision);
            text.Append('|').Append(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)));
            text.Append('|').Append(options.AzimuthViews).Append('|').Append(options.Resolution);
            void Add(float value) => text.Append('|').Append(BitConverter.SingleToInt32Bits(value).ToString("x8"));
            Add(options.BoundsPadding); Add(options.MaxScreenHeight); foreach (float angle in options.Elevations) Add(angle);
            foreach (var material in materials)
            {
                string path = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("Impostor source materials must be persistent assets.");
                text.Append('|').Append(GlobalObjectId.GetGlobalObjectIdSlow(material).ToString()).Append('|').Append(AssetDatabase.GetAssetDependencyHash(path));
                text.Append('|').Append(material.ComputeCRC().ToString("x8"));
                string shaderPath = AssetDatabase.GetAssetPath(material.shader);
                text.Append('|').Append(material.shader.name).Append('|').Append(AssetDatabase.GetAssetDependencyHash(shaderPath));
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        static float ProjectExtent(Vector3 e, Vector3 axis) => Mathf.Abs(axis.x) * e.x + Mathf.Abs(axis.y) * e.y + Mathf.Abs(axis.z) * e.z;
        static void RegisterVariant(VirtualMeshModelAsset model, VirtualMeshModelImpostorAsset asset, bool saveAsset)
        {
            var previous = model.ImpostorVariants;
            var variants = new List<VirtualMeshModelImpostorAsset>();
            bool replaced = false;
            foreach (var existing in previous)
            {
                if (existing == null || existing.SourceModel != model) continue;
                bool sameMaterials = existing.SourceMaterials != null && existing.SourceMaterials.Length == asset.SourceMaterials.Length;
                if (sameMaterials)
                    for (int i = 0; i < existing.SourceMaterials.Length; i++)
                        if (existing.SourceMaterials[i] != asset.SourceMaterials[i]) { sameMaterials = false; break; }
                if (!sameMaterials) variants.Add(existing);
                else if (!replaced) { variants.Add(asset); replaced = true; }
            }
            if (!replaced) variants.Add(asset);
            // Keep variant order stable, so repeated content-addressed builds do
            // not increment metadata revisions or rewrite a large model asset.
            bool unchanged = variants.Count == previous.Length;
            for (int i = 0; unchanged && i < previous.Length; i++)
                unchanged = ReferenceEquals(variants[i], previous[i]);
            if (unchanged) return;
            model.SetImpostorVariants(variants.ToArray()); EditorUtility.SetDirty(model);
            if (saveAsset && EditorUtility.IsPersistent(model)) AssetDatabase.SaveAssetIfDirty(model);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void SetIfPresent(Material material, string name, float value) { if (material.HasProperty(name)) material.SetFloat(name, value); }
        static Texture2DArray NewArray(int resolution, int layers, TextureFormat format, string name) =>
            new Texture2DArray(resolution, resolution, layers, format, false, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        static Texture2D NewReadback(int resolution, TextureFormat format) => new Texture2D(resolution, resolution, format, false, true) { hideFlags = HideFlags.HideAndDontSave };
        static RenderTexture NewTarget(int resolution, GraphicsFormat format, string name)
        {
            var descriptor = new RenderTextureDescriptor(resolution, resolution) { graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1,
                volumeDepth = 1, dimension = TextureDimension.Tex2D, useMipMap = false, autoGenerateMips = false };
            var target = new RenderTexture(descriptor) { name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (!target.Create()) { Destroy(target); throw new InvalidOperationException("Failed to allocate " + name); }
            return target;
        }
        static void ReadLayer(RenderTexture source, Texture2D readback, Texture2DArray destination, int layer)
        {
            RenderTexture.active = source;
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            destination.SetPixelData(readback.GetRawTextureData<byte>(), 0, layer);
        }
        static void EnsureFolder(string path)
        {
            string[] segments = path.Split('/'); string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }
        static void Release(RenderTexture texture) { if (texture != null) { texture.Release(); Destroy(texture); } }
        static void Destroy(Object value) { if (value != null) Object.DestroyImmediate(value); }
    }
}
