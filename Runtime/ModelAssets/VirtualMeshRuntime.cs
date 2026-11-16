using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// Model geometry is uploaded once and shared across instances and cameras. Only transforms and
    /// visible cluster/instance pairs are camera-specific. This path does not read scene-baked pages.
    /// </summary>
    public static class VirtualMeshRuntime
    {
        [Serializable]
        public struct RuntimeStatistics
        {
            public int ActiveInstances, SharedModels, DrawBatches, ShadowDrawBatches;
            public int QueuedModels, BudgetRejectedModels, UploadedModelsThisContext;
            public int ChangeDrivenValidatedInstances, PolledInstances;
            public long AtlasUploadBytesThisContext;
            public bool PreparationCacheHit;
            public long PreparationCacheBytes;
            public bool AtlasUploadDeferredForHostMemory;
            public long GeometryBytes, CandidateClusters, VisibleClusters, ShadowCandidateClusters, VisibleShadowClusters;
            public long GpuBufferBytes, GpuBudgetBytes;
            public bool RequestedSplitVertexStreams, SplitVertexStreams;
            public int AtlasVertexCount;
            public long VertexPayloadBytes, VertexBindingBytes;
            public string VertexLayoutFailure;
            public string LastError, Path;
        }
        public static RuntimeStatistics Statistics { get; private set; }
        /// <summary>Budget for Virtual Mesh geometry and draw buffers only; excludes textures, source meshes and other engine allocations. Zero selects min(40% device memory, 3072 MiB).</summary>
        public static int GpuBudgetMiB { get; set; }
        /// <summary>Effective process-wide atlas layout. An explicit assignment overrides loaded-scene preferences until ClearVertexStreamOverrides or an exact override-state restore. Call on the main thread.</summary>
        public static bool UseSplitVertexStreams
        {
            get => SplitVertexStreamsOverride ?? sceneSplitVertexStreams;
            set => SplitVertexStreamsOverride = value;
        }
        /// <summary>Effective split-atlas upload strategy. A true request uses GPU transpose when supported; CPU fallback preserves the same vertex data. An explicit assignment overrides scene preferences.</summary>
        public static bool UseGpuVertexTranspose
        {
            get => GpuVertexTransposeOverride ?? sceneGpuVertexTranspose;
            set => GpuVertexTransposeOverride = value;
        }
        /// <summary>Null follows loaded-scene preferences; false and true are explicit process-wide overrides.</summary>
        public static bool? SplitVertexStreamsOverride { get; set; }
        /// <summary>Null follows loaded-scene preferences; false and true are explicit process-wide overrides.</summary>
        public static bool? GpuVertexTransposeOverride { get; set; }
        public readonly struct VertexStreamOverrideState
        {
            public readonly bool? SplitVertexStreams, GpuVertexTranspose;
            internal VertexStreamOverrideState(bool? split, bool? gpu) { SplitVertexStreams = split; GpuVertexTranspose = gpu; }
        }
        public static VertexStreamOverrideState CaptureVertexStreamOverrides() =>
            new VertexStreamOverrideState(SplitVertexStreamsOverride, GpuVertexTransposeOverride);
        /// <summary>Restore the nullable requests, including automatic scene policy. Saving/restoring effective bools is not equivalent.</summary>
        public static void RestoreVertexStreamOverrides(VertexStreamOverrideState state)
        { SplitVertexStreamsOverride = state.SplitVertexStreams; GpuVertexTransposeOverride = state.GpuVertexTranspose; }
        public static void ClearVertexStreamOverrides()
        { SplitVertexStreamsOverride = null; GpuVertexTransposeOverride = null; }
        /// <summary>Limits completed model uploads per rendering context. Large models are uploaded over multiple contexts. Valid range is one to two.</summary>
        public static int MaxModelUploadsPerContext { get; set; } = 1;
        private const long MiB = 1024L * 1024L;
        private static readonly ProfilerMarker validateMarker = new ProfilerMarker("VirtualMesh.Validate");
        private static readonly ProfilerMarker validateModelMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.Model");
        private static readonly ProfilerMarker validateRendererMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.Renderer");
        private static readonly ProfilerMarker validateComponentsMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.Components");
        private static readonly ProfilerMarker validateMaterialsMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.Materials");
        private static readonly ProfilerMarker prepareMarker = new ProfilerMarker("VirtualMesh.Prepare");
        private static readonly ProfilerMarker resolveMarker = new ProfilerMarker("VirtualMesh.Resolve");
        private static readonly ProfilerMarker submitMarker = new ProfilerMarker("VirtualMesh.Submit");
        private static readonly ProfilerMarker uploadMarker = new ProfilerMarker("VirtualMesh.Upload");
        private static readonly ProfilerMarker computeMarker = new ProfilerMarker("VirtualMesh.Compute");
        private static readonly ProfilerMarker drawMarker = new ProfilerMarker("VirtualMesh.Draw");
        private static readonly ProfilerMarker materialMarker = new ProfilerMarker("VirtualMesh.MaterialSync");
        private static readonly ProfilerMarker readbackMarker = new ProfilerMarker("VirtualMesh.Readback");

        [StructLayout(LayoutKind.Sequential)]
        private struct InstanceData
        {
            public Matrix4x4 objectToWorld, worldToObject;
            public Vector4 parameters;
            public const int Stride = 144;
        }

        private readonly struct ActiveInstance
        {
            public readonly VirtualMeshInstance Instance;
            public readonly InstanceData Data;
            public readonly Bounds Bounds;
            public readonly ShadowCastingMode ShadowMode;
            public readonly float ShadowLodMultiplier;

            public ActiveInstance(VirtualMeshInstance instance, Matrix4x4 matrix, float determinant)
            {
                Instance = instance;
                Data = new InstanceData { objectToWorld = matrix, worldToObject = matrix.inverse,
                    parameters = new Vector4(MaximumStretch(matrix), Mathf.Max(0.01f, instance.PixelError),
                        determinant < 0 ? -1f : 1f, Mathf.Max(0f, instance.TargetTrianglePixels)) };
                var bounds = TransformBounds(instance.Model.Bounds, matrix);
                float windPadding = 0f;
                foreach (var material in instance.Materials) windPadding = Mathf.Max(windPadding, GetWindPadding(material));
                bounds.Expand(windPadding * 2f);
                Bounds = bounds;
                ShadowMode = instance.SourceShadowMode;
                ShadowLodMultiplier = float.IsFinite(instance.ShadowLodMultiplier) ? Mathf.Max(0.25f, instance.ShadowLodMultiplier) : 1f;
            }
        }

        private sealed class Geometry : IDisposable
        {
            public readonly VirtualMeshModelAsset Model;
            public readonly int Revision;
            public readonly GraphicsBuffer Vertices, Indices, Clusters;
            public readonly int[] SubmeshOffsets, SubmeshCounts;
            public readonly long Bytes;
            public Geometry(VirtualMeshModelAsset model)
            {
                Model = model;
                Revision = model.Revision;
                SubmeshOffsets = new int[model.SubMeshCount];
                SubmeshCounts = new int[model.SubMeshCount];
                var uploadClusters = model.Clusters;
                bool sorted = true;
                uint previousSubmesh = 0;
                foreach (var cluster in uploadClusters)
                {
                    SubmeshCounts[cluster.submesh]++;
                    if (cluster.submesh < previousSubmesh) sorted = false;
                    previousSubmesh = cluster.submesh;
                }
                for (int i = 1; i < SubmeshOffsets.Length; i++)
                    SubmeshOffsets[i] = SubmeshOffsets[i - 1] + SubmeshCounts[i - 1];
                // Published assets are already grouped. Handle externally supplied data without
                // changing its serialized order or the vertex/index addresses stored in clusters.
                if (!sorted)
                {
                    uploadClusters = new VirtualMeshModelCluster[model.Clusters.Length];
                    var nextOffsets = (int[])SubmeshOffsets.Clone();
                    foreach (var cluster in model.Clusters)
                        uploadClusters[nextOffsets[cluster.submesh]++] = cluster;
                }
                try
                {
                    using var scope = uploadMarker.Auto();
                    Vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, model.Vertices.Length, VirtualMeshModelVertex.Stride);
                    Indices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, model.Indices.Length, sizeof(uint));
                    Clusters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, model.Clusters.Length, VirtualMeshModelCluster.Stride);
                    Vertices.SetData(model.Vertices); Indices.SetData(model.Indices); Clusters.SetData(uploadClusters);
                    Bytes = model.GeometryBytes;
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); Clusters?.Dispose(); }
        }

        private enum Residency { Dormant, Queued, Ready, BudgetRejected }

        private sealed class ModelDemand
        {
            public readonly VirtualMeshModelAsset Model;
            public readonly int Revision;
            public readonly int[] SubmeshCounts;
            public bool Wanted, ColorVisible, RejectionWarned, BufferLimitExceeded;
            public float DistanceSquared;
            public double LastNeeded;
            public long BatchBytes;
            public Residency State;
            public ModelDemand(VirtualMeshModelAsset model)
            {
                Model = model; Revision = model.Revision;
                SubmeshCounts = new int[model.SubMeshCount];
                foreach (var cluster in model.Clusters) SubmeshCounts[cluster.submesh]++;
            }
            public void ResetDemand()
            {
                Wanted = ColorVisible = BufferLimitExceeded = false; DistanceSquared = float.PositiveInfinity; BatchBytes = 0;
                State = Residency.Dormant;
            }
        }

        private readonly struct ShadowLight
        {
            public readonly LightType Type;
            public readonly Vector3 Position, Direction;
            public readonly float RangeSquared;
            public readonly int CullingMask;
            public ShadowLight(Light light)
            {
                Type = light.type; Position = light.transform.position; Direction = light.transform.forward;
                RangeSquared = light.range * light.range; CullingMask = light.cullingMask;
            }
        }

        private readonly struct BatchKey : IEquatable<BatchKey>
        {
            public readonly VirtualMeshModelAsset Model;
            public readonly Material Material;
            public readonly Material RenderMaterial;
            public readonly int Submesh, Layer;
            public readonly uint RenderingLayers;
            public readonly ulong SceneMask;
            public readonly bool ReceiveShadows;
            public BatchKey(VirtualMeshInstance instance, Material material, int submesh)
            {
                Model = instance.Model; Material = material; Submesh = submesh;
                RenderMaterial = instance.RenderMaterials != null && submesh < instance.RenderMaterials.Length ? instance.RenderMaterials[submesh] : null;
                Layer = instance.RenderTransform.gameObject.layer;
                SceneMask = instance.RenderTransform.gameObject.sceneCullingMask;
                RenderingLayers = instance.SourceRenderer != null ? instance.SourceRenderer.renderingLayerMask : 1;
                ReceiveShadows = instance.SourceRenderer == null || instance.SourceRenderer.receiveShadows;
            }
            public bool Equals(BatchKey other) => Model == other.Model && Material == other.Material && RenderMaterial == other.RenderMaterial &&
                Submesh == other.Submesh && Layer == other.Layer && SceneMask == other.SceneMask && RenderingLayers == other.RenderingLayers && ReceiveShadows == other.ReceiveShadows;
            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { int hash = Model.GetInstanceID(); hash = hash * 397 ^ Material.GetInstanceID();
                    hash = hash * 397 ^ (RenderMaterial != null ? RenderMaterial.GetInstanceID() : 0);
                    hash = hash * 397 ^ SceneMask.GetHashCode();
                    hash = hash * 397 ^ Submesh; hash = hash * 397 ^ Layer; hash = hash * 397 ^ (int)RenderingLayers;
                    return hash * 397 ^ (ReceiveShadows ? 1 : 0); }
            }
        }

        private readonly struct ShadowBatchKey : IEquatable<ShadowBatchKey>
        {
            private const float CellSize = 64f;
            public readonly BatchKey Draw;
            public readonly Vector3Int Cell;
            public readonly int Cull;

            public ShadowBatchKey(BatchKey draw, ActiveInstance instance)
            {
                Draw = draw;
                Vector3 center = instance.Bounds.center / CellSize;
                Cell = new Vector3Int(Mathf.FloorToInt(center.x), Mathf.FloorToInt(center.y), Mathf.FloorToInt(center.z));
                Cull = instance.ShadowMode == ShadowCastingMode.TwoSided ? 0 : VirtualMeshMaterialSupport.GetShadowCull(draw.Material);
            }

            public bool Equals(ShadowBatchKey other) => Draw.Equals(other.Draw) && Cell == other.Cell && Cull == other.Cull;
            public override bool Equals(object obj) => obj is ShadowBatchKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (Draw.GetHashCode() * 397 ^ Cell.GetHashCode()) * 397 ^ Cull; }
            }
        }

        private readonly struct MaterialKey : IEquatable<MaterialKey>
        {
            public readonly Material Source, Template;
            public readonly Shader Shader;
            public readonly int ShadowCull;
            public MaterialKey(Material source, Material template, Shader shader, int shadowCull)
            { Source = source; Template = template; Shader = shader; ShadowCull = shadowCull; }
            public bool Equals(MaterialKey other) => Source == other.Source && Template == other.Template &&
                Shader == other.Shader && ShadowCull == other.ShadowCull;
            public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Source.GetInstanceID();
                    hash = hash * 397 ^ (Template != null ? Template.GetInstanceID() : 0);
                    return (hash * 397 ^ Shader.GetInstanceID()) * 397 ^ ShadowCull;
                }
            }
        }

        private sealed class SharedMaterial
        {
            public readonly MaterialKey Key;
            public readonly Material Material;
            public int References;
            private int synchronizedGeneration = -1;
            public SharedMaterial(MaterialKey key)
            {
                Key = key;
                Material = key.Template != null ? new Material(key.Template) : new Material(key.Shader);
                Material.name = key.Source.name + " (Virtual Mesh)";
                Material.hideFlags = HideFlags.HideAndDontSave;
            }
            public void Synchronize()
            {
                if (synchronizedGeneration == contextGeneration) return;
                using var scope = materialMarker.Auto();
                // This also copies runtime property changes, keywords, queue and GI flags.
                // Synchronize once per context rather than once per model/submesh/cell/camera.
                Material.CopyPropertiesFromMaterial(VirtualMeshMaterialSupport.GetPropertyMaterial(Key.Source, Key.Template));
                // True legacy per-model geometry always uses the 72-byte AoS layout.
                VirtualMeshVertexBindings.SetLayoutKeyword(Material, false, legacy: true);
                Material.enableInstancing = true;
                if (Material.HasProperty("_VMUnlit"))
                    Material.SetFloat("_VMUnlit", Key.Source.shader.name == "Universal Render Pipeline/Unlit" ? 1f :
                        Key.Source.HasProperty("_VMUnlit") ? Key.Source.GetFloat("_VMUnlit") : 0f);
                if (Material.HasProperty("_VMShadowCull")) Material.SetFloat("_VMShadowCull", Key.ShadowCull);
                synchronizedGeneration = contextGeneration;
            }
        }

        private static SharedMaterial AcquireMaterial(Material source, Material template, int shadowCull)
        {
            var shader = (template != null ? template : source).shader;
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("The generated Virtual Mesh shader is unavailable for '" + source.name + "'.");
            var key = new MaterialKey(source, template, shader, shadowCull);
            if (!materialPool.TryGetValue(key, out var shared))
            {
                shared = new SharedMaterial(key);
                materialPool.Add(key, shared);
            }
            shared.References++;
            return shared;
        }

        private static void ReleaseMaterial(SharedMaterial shared)
        {
            if (--shared.References != 0) return;
            materialPool.Remove(shared.Key);
            DestroyResource(shared.Material);
        }

        private sealed class Batch : IDisposable
        {
            public readonly List<int> Members = new List<int>();
            public readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            private readonly SharedMaterial sharedMaterial;
            public Material Material => sharedMaterial.Material;
            public GraphicsBuffer Instances, Visible, Arguments, CountReadback;
            public InstanceData[] InstanceArray = Array.Empty<InstanceData>();
            public int GeometryRevision;
            public int SubmittedGeneration;
            public bool Shadow;
            public long VisibleCount;
            public bool ReadbackPending, Disposed;
            public double LastReadback, LastUsed;
            private int visibleCapacity;
            public long BufferBytes => (Instances != null ? (long)InstanceArray.Length * InstanceData.Stride : 0) +
                (Visible != null ? (long)visibleCapacity * sizeof(uint) * 2 : 0) +
                (Arguments != null ? GraphicsBuffer.IndirectDrawArgs.size : 0) + (CountReadback != null ? sizeof(uint) : 0);

            public long RequiredBufferBytes(int clusterCount)
            {
                if (Members.Count == 0) return BufferBytes;
                return (long)Mathf.Max(InstanceArray.Length, Mathf.NextPowerOfTwo(Members.Count)) * InstanceData.Stride +
                    Math.Max(visibleCapacity, (long)clusterCount * Members.Count) * sizeof(uint) * 2 +
                    GraphicsBuffer.IndirectDrawArgs.size + sizeof(uint);
            }

            public Batch(Material source, Material renderMaterial, int shadowCull = 2)
            {
                sharedMaterial = AcquireMaterial(source, renderMaterial, shadowCull);
            }

            public void Prepare(BatchKey key, Geometry geometry)
            {
                sharedMaterial.Synchronize();

                int required = Members.Count;
                if (InstanceArray.Length < required)
                {
                    Instances?.Dispose();
                    InstanceArray = new InstanceData[Mathf.NextPowerOfTwo(required)];
                    Instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, InstanceArray.Length, InstanceData.Stride);
                }
                long outputCount = (long)geometry.SubmeshCounts[key.Submesh] * required;
                if (outputCount > int.MaxValue / 8) throw new InvalidOperationException("The visible cluster buffer is too large. Split the instance group.");
                int capacity = Mathf.Max(1, (int)outputCount);
                if (Visible == null || visibleCapacity < capacity || GeometryRevision != geometry.Revision)
                {
                    Visible?.Dispose();
                    Visible = new GraphicsBuffer(GraphicsBuffer.Target.Append, capacity, sizeof(uint) * 2);
                    visibleCapacity = capacity;
                }
                if (Arguments == null)
                {
                    Arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
                    Arguments.SetData(new[] { new GraphicsBuffer.IndirectDrawArgs { vertexCountPerInstance = 192, instanceCount = 0, startVertex = 0, startInstance = 0 } });
                }
                if (CountReadback == null) CountReadback = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 1, sizeof(uint));
                GeometryRevision = geometry.Revision;
                VirtualMeshVertexBindings.BindLegacy(Properties, geometry.Vertices);
                Properties.SetBuffer("_VMIndices", geometry.Indices);
                Properties.SetBuffer("_VMClusters", geometry.Clusters);
                Properties.SetBuffer("_VMInstances", Instances);
                Properties.SetBuffer("_VMVisibleClusters", Visible);
                Properties.SetVector("_VMModelBoundsCenter", geometry.Model.Bounds.center);
                Properties.SetInt("_VMUseGeometryAtlas", 0);
                Properties.SetInt("_VMGraphDraw", 0);
                Properties.SetFloat("_VMReceiveShadows", key.ReceiveShadows && !key.Material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF") &&
                    (!key.Material.HasProperty("_ReceiveShadows") || key.Material.GetFloat("_ReceiveShadows") > 0.5f) ? 1 : 0);
            }

            public void ReadVisibleCount()
            {
                if (!SystemInfo.supportsAsyncGPUReadback || ReadbackPending || Time.realtimeSinceStartupAsDouble - LastReadback < 0.5) return;
                using var scope = readbackMarker.Auto();
                ReadbackPending = true;
                LastReadback = Time.realtimeSinceStartupAsDouble;
                // Native shadow rendering can consume or rewrite its draw arguments. Read the
                // append counter through our own buffer, before handing the draw to Unity.
                GraphicsBuffer.CopyCount(Visible, CountReadback, 0);
                AsyncGPUReadback.Request(CountReadback, request =>
                {
                    if (Disposed) return;
                    ReadbackPending = false;
                    if (!request.hasError)
                    {
                        long previous = VisibleCount;
                        VisibleCount = request.GetData<uint>()[0];
                        // Editor render requests may stop after one context. Keep the published
                        // snapshot current when its asynchronous count completes afterwards.
                        if (!renderingContext && SubmittedGeneration == contextGeneration)
                        {
                            var statistics = Statistics;
                            if (Shadow) statistics.VisibleShadowClusters += VisibleCount - previous;
                            else statistics.VisibleClusters += VisibleCount - previous;
                            Statistics = statistics;
                        }
                    }
                });
            }

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Instances?.Dispose(); Visible?.Dispose(); Arguments?.Dispose(); CountReadback?.Dispose();
                ReleaseMaterial(sharedMaterial);
            }
        }

        private sealed class CameraState : IDisposable
        {
            public Camera Camera;
            public double LastUsed;
            public int PreparedGeneration;
            public readonly Dictionary<BatchKey, Batch> Batches = new Dictionary<BatchKey, Batch>();
            public readonly Dictionary<ShadowBatchKey, Batch> ShadowBatches = new Dictionary<ShadowBatchKey, Batch>();
            public void ClearShadowBatches()
            {
                foreach (var batch in ShadowBatches.Values) batch.Dispose();
                ShadowBatches.Clear();
            }
            public void Dispose()
            {
                foreach (var batch in Batches.Values) batch.Dispose();
                Batches.Clear(); ClearShadowBatches();
            }
        }

        private static readonly HashSet<VirtualMeshInstance> instances = new HashSet<VirtualMeshInstance>();
        private static readonly Dictionary<VirtualMeshModelAsset, Geometry> geometryCache = new Dictionary<VirtualMeshModelAsset, Geometry>();
        private static readonly Dictionary<int, CameraState> cameraStates = new Dictionary<int, CameraState>();
        private static readonly Dictionary<MaterialKey, SharedMaterial> materialPool = new Dictionary<MaterialKey, SharedMaterial>();
        private static readonly Dictionary<VirtualMeshModelAsset, ModelDemand> modelDemands = new Dictionary<VirtualMeshModelAsset, ModelDemand>();
        private static readonly List<ModelDemand> uploadOrder = new List<ModelDemand>();
        private static readonly HashSet<VirtualMeshModelAsset> admittedModels = new HashSet<VirtualMeshModelAsset>();
        private static readonly List<CameraState> preparedCameras = new List<CameraState>();
        private static readonly List<ShadowLight> shadowLights = new List<ShadowLight>();
        private static readonly Vector3[] frustumCorners = new Vector3[4];
        private const int MaxShadowCullLights = 16;
        private static readonly Plane[] shadowReceiverPlanes = new Plane[6];
        private static readonly Vector4[] shadowReceiverVectors = new Vector4[6];
        private static readonly Vector4[] shadowLightDirections = new Vector4[MaxShadowCullLights];
        private static readonly Vector4[] shadowLightPositionsRanges = new Vector4[MaxShadowCullLights];
        private static Camera shadowCullCamera;
        private static int shadowCullLayer;
        private static bool shadowCullUploaded;
        private static readonly List<ActiveInstance> active = new List<ActiveInstance>();
        private static readonly HashSet<VirtualMeshModelAsset> usedModels = new HashSet<VirtualMeshModelAsset>();
        private static readonly List<VirtualMeshModelAsset> staleModels = new List<VirtualMeshModelAsset>();
        private static readonly List<int> staleCameras = new List<int>();
        private static readonly List<BatchKey> staleBatches = new List<BatchKey>();
        private static readonly List<ShadowBatchKey> staleShadowBatches = new List<ShadowBatchKey>();
        private static readonly Plane[] frustum = new Plane[6];
        private static readonly Vector4[] frustumVectors = new Vector4[6];
        private static ComputeShader cullShader;
        private static Shader renderShader;
        private static int kernel, contextGeneration;
        private static uint threads;
        private static bool subscribed, renderingContext, graphActive;
        private static readonly HashSet<int> scriptScenePolicies = new();
        private static readonly Dictionary<int, int> sceneSettingsCounts = new();

        private readonly struct SceneVertexStreamRequest
        {
            internal readonly int SceneHandle;
            internal readonly bool Split, Gpu;
            internal SceneVertexStreamRequest(int sceneHandle, bool split, bool gpu)
            { SceneHandle = sceneHandle; Split = split; Gpu = split && gpu; }
        }
        // Keyed by component, not camera or scene: duplicate scene settings are
        // deterministic OR requests, while validation keeps its existing counts.
        private static readonly Dictionary<int, SceneVertexStreamRequest> sceneVertexStreamRequests = new();
        private static readonly List<int> staleVertexStreamRequests = new();
        private static bool sceneSplitVertexStreams, sceneGpuVertexTranspose, vertexStreamSceneRequestsDirty = true;
        internal static object VertexStreamPolicyDiagnostics => new {
            SceneRequestCount = sceneVertexStreamRequests.Count,
            SceneSplitVertexStreams = sceneSplitVertexStreams, SceneGpuVertexTranspose = sceneGpuVertexTranspose,
            SplitVertexStreamsOverride, GpuVertexTransposeOverride,
            EffectiveSplitVertexStreams = UseSplitVertexStreams, EffectiveGpuVertexTranspose = UseGpuVertexTranspose,
            SceneRequestsDirty = vertexStreamSceneRequestsDirty };

        static VirtualMeshRuntime()
        {
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.sceneLoaded += OnVertexStreamSceneLoaded;
        }
        private static void OnVertexStreamSceneLoaded(Scene scene, LoadSceneMode mode) => vertexStreamSceneRequestsDirty = true;
        internal static void SetSceneVertexStreamRequest(VirtualMeshSceneSettings owner)
        {
            if (owner == null) return;
            var scene = owner.gameObject.scene;
            int id = owner.GetInstanceID();
            if (!owner.isActiveAndEnabled || !scene.IsValid() || !scene.isLoaded || !owner.RequestSplitVertexStreams)
                sceneVertexStreamRequests.Remove(id);
            else
                sceneVertexStreamRequests[id] = new SceneVertexStreamRequest(scene.handle,
                    owner.RequestSplitVertexStreams, owner.RequestGpuVertexTranspose);
            RefreshVertexStreamScenePolicy();
        }
        internal static void RemoveSceneVertexStreamRequest(VirtualMeshSceneSettings owner)
        {
            if (owner != null && sceneVertexStreamRequests.Remove(owner.GetInstanceID())) RefreshVertexStreamScenePolicy();
        }
        private static void RefreshVertexStreamScenePolicy()
        {
            sceneSplitVertexStreams = sceneGpuVertexTranspose = false;
            foreach (var request in sceneVertexStreamRequests.Values)
            { sceneSplitVertexStreams |= request.Split; sceneGpuVertexTranspose |= request.Gpu; }
        }
        private static void RebuildVertexStreamSceneRequests()
        {
            sceneVertexStreamRequests.Clear();
            foreach (var settings in UnityEngine.Object.FindObjectsByType<VirtualMeshSceneSettings>(FindObjectsSortMode.None))
                SetSceneVertexStreamRequest(settings);
            RefreshVertexStreamScenePolicy();
            vertexStreamSceneRequestsDirty = false;
        }

        /// <summary>Sets the default validation policy for VM instances in a loaded scene. Call on the main thread.</summary>
        public static void SetSceneChangeDrivenValidation(Scene scene, bool enabled)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            bool wasEnabled = SceneUsesChangeDrivenValidation(scene);
            if (enabled) scriptScenePolicies.Add(scene.handle);
            else scriptScenePolicies.Remove(scene.handle);
            NotifyScenePolicyChange(scene, wasEnabled);
        }

        internal static bool SceneUsesChangeDrivenValidation(VirtualMeshInstance instance) =>
            SceneUsesChangeDrivenValidation(instance.gameObject.scene);

        private static bool SceneUsesChangeDrivenValidation(Scene scene) =>
            scriptScenePolicies.Contains(scene.handle) || sceneSettingsCounts.ContainsKey(scene.handle);

        internal static void ChangeSceneSettingsCount(Scene scene, int delta)
        {
            bool wasEnabled = SceneUsesChangeDrivenValidation(scene);
            sceneSettingsCounts.TryGetValue(scene.handle, out int count);
            count += delta;
            if (count <= 0) sceneSettingsCounts.Remove(scene.handle);
            else sceneSettingsCounts[scene.handle] = count;
            NotifyScenePolicyChange(scene, wasEnabled);
        }

        private static void NotifyScenePolicyChange(Scene scene, bool wasEnabled)
        {
            if (wasEnabled == SceneUsesChangeDrivenValidation(scene)) return;
            foreach (var instance in instances)
                if (instance != null && instance.ValidationMode == VirtualMeshValidationMode.SceneDefault &&
                    instance.gameObject.scene == scene)
                    instance.InvalidateRenderState();
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            scriptScenePolicies.Remove(scene.handle);
            sceneSettingsCounts.Remove(scene.handle);
            staleVertexStreamRequests.Clear();
            foreach (var pair in sceneVertexStreamRequests)
                if (pair.Value.SceneHandle == scene.handle) staleVertexStreamRequests.Add(pair.Key);
            foreach (int id in staleVertexStreamRequests) sceneVertexStreamRequests.Remove(id);
            staleVertexStreamRequests.Clear(); RefreshVertexStreamScenePolicy();
        }

        internal static void Register(VirtualMeshInstance instance)
        {
            if (instance == null || !instance.gameObject.scene.IsValid()) return;
            if (!instance.gameObject.scene.isLoaded)
            {
#if UNITY_EDITOR
                ScheduleEditorRegistration();
#endif
                return;
            }
            if (instances.Add(instance))
            {
                instance.SetRegisteredForValidation(true);
                instance.InvalidateRenderState();
#if UNITY_EDITOR
                RequestEditorPreviewRefresh();
#endif
            }
            if (subscribed) return;
            RenderPipelineManager.beginContextRendering += Render;
            Application.quitting += ReleaseAll;
            subscribed = true;
        }

        internal static void Unregister(VirtualMeshInstance instance)
        {
            if (instances.Remove(instance))
            {
                instance.SetRegisteredForValidation(false);
                instance.InvalidateRenderState();
            }
            VirtualMeshGraphRuntime.ForgetTransform(instance);
            if (instances.Count == 0) ReleaseAll();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ReleaseAll();
            scriptScenePolicies.Clear();
            sceneVertexStreamRequests.Clear(); RefreshVertexStreamScenePolicy();
            vertexStreamSceneRequestsDirty = true;
            // Explicit overrides survive when domain reload is disabled, as the
            // original process-wide bool API did. Test scopes restore nullable state.
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterLoadedInstances()
        {
            // Rebuild idempotently even when component registration fields survived
            // domain-reload-disabled Play. Do not disturb validation registration.
            RebuildVertexStreamSceneRequests();
            foreach (var instance in UnityEngine.Object.FindObjectsByType<VirtualMeshInstance>(FindObjectsSortMode.None))
                if (instance.isActiveAndEnabled) Register(instance);
        }

#if UNITY_EDITOR
        private const string EditorPreviewPausedKey = "Unity.VirtualMesh.PreviewPaused";
        private const string EditorPreviewPausedMenu = "Tools/Virtual Mesh/Pause Edit Mode Preview";
        private static bool editorPreviewPaused;
        private static bool editorRegistrationQueued, editorRegistrationRunning;
        private static bool editorStreamingRequested, editorStreamingActive, editorStreamingSuspended;
        private static double editorStreamingNextRepaint, editorStreamingLastProgress;
        private static long editorStreamingAtlasBytes;
        private static int editorStreamingQueuedModels, editorStreamingSharedModels, editorStreamingPrepared, editorStreamingUploaded;
        private static VirtualMeshIndexedVisibility editorStreamingPreparation;
        private static readonly Dictionary<MeshRenderer, bool> pausedPreviewSources = new Dictionary<MeshRenderer, bool>();
        private static readonly HashSet<MeshRenderer> currentPreviewSources = new HashSet<MeshRenderer>();
        private static readonly List<MeshRenderer> stalePreviewSources = new List<MeshRenderer>();

        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallEditorCleanup()
        {
            editorPreviewPaused = UnityEditor.SessionState.GetBool(EditorPreviewPausedKey, false);
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
            UnityEditor.EditorApplication.quitting -= ReleaseAll;
            UnityEditor.EditorApplication.quitting += ReleaseAll;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= RestoreBeforeSceneSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += RestoreBeforeSceneSave;
            UnityEditor.SceneManagement.PrefabStage.prefabSaving -= RestoreBeforePrefabSave;
            UnityEditor.SceneManagement.PrefabStage.prefabSaving += RestoreBeforePrefabSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnEditorSceneOpened;
            UnityEditor.EditorApplication.update -= UpdateEditorStreaming;
            UnityEditor.EditorApplication.update += UpdateEditorStreaming;
            ScheduleEditorRegistration();
        }

        private static void OnEditorSceneOpened(Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode)
        { vertexStreamSceneRequestsDirty = true; ScheduleEditorRegistration(); }

        private static void ScheduleEditorRegistration()
        {
            if (editorRegistrationQueued || editorRegistrationRunning || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            editorRegistrationQueued = true;
            UnityEditor.EditorApplication.delayCall += CompleteEditorRegistration;
        }

        private static void CompleteEditorRegistration()
        {
            editorRegistrationQueued = false;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
            {
                ScheduleEditorRegistration();
                return;
            }
            // One scan after reload/scene-open; OnEnable can run before scene.isLoaded becomes true.
            editorRegistrationRunning = true;
            try { RegisterLoadedInstances(); }
            finally { editorRegistrationRunning = false; }
            RequestEditorPreviewRefresh();
        }

        private static void RequestEditorPreviewRefresh()
        {
            if (Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode || editorPreviewPaused) return;
            editorStreamingRequested = true;
            editorStreamingSuspended = false;
            editorStreamingActive = false;
        }

        private static void StopEditorStreaming()
        {
            editorStreamingRequested = editorStreamingActive = editorStreamingSuspended = false;
            editorStreamingNextRepaint = 0;
            editorStreamingPreparation = null;
        }

        private static void UpdateEditorStreaming()
        {
            // This callback never renders or searches for objects itself. Play mode keeps its own cadence.
            if (Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode || editorPreviewPaused ||
                UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating || renderingContext ||
                !subscribed || UnityEditor.SceneView.sceneViews.Count == 0) return;

            var preparation = VirtualMeshGraphRuntime.IndexedVisibilityPreparation;
            bool indexedPending = preparation != null && preparation.Bytes > 0 && !preparation.Ready && preparation.Failure == null;
            bool pending = Statistics.QueuedModels > 0 || indexedPending;
            if (!pending && !editorStreamingRequested)
            {
                editorStreamingActive = editorStreamingSuspended = false;
                editorStreamingPreparation = null;
                return;
            }

            double now = UnityEditor.EditorApplication.timeSinceStartup;
            long atlasBytes = VirtualMeshGraphRuntime.AtlasUploadProgressBytes;
            int prepared = indexedPending ? preparation.PreparedClusters : 0;
            int uploaded = indexedPending ? preparation.UploadedClusters : 0;
            bool progress = !editorStreamingActive || editorStreamingAtlasBytes != atlasBytes ||
                editorStreamingQueuedModels != Statistics.QueuedModels || editorStreamingSharedModels != Statistics.SharedModels ||
                !ReferenceEquals(editorStreamingPreparation, preparation) ||
                editorStreamingPrepared != prepared || editorStreamingUploaded != uploaded;
            if (progress)
            {
                editorStreamingActive = true;
                editorStreamingSuspended = false;
                editorStreamingLastProgress = now;
                editorStreamingAtlasBytes = atlasBytes;
                editorStreamingQueuedModels = Statistics.QueuedModels;
                editorStreamingSharedModels = Statistics.SharedModels;
                editorStreamingPreparation = preparation;
                editorStreamingPrepared = prepared;
                editorStreamingUploaded = uploaded;
            }
            // No-progress timeout prevents hidden views, errors or memory pressure from spinning forever.
            // A later ordinary render that advances work, or an explicit refresh, resumes the pump.
            if (!editorStreamingRequested && (editorStreamingSuspended || now - editorStreamingLastProgress >= 15))
            {
                editorStreamingSuspended = true;
                return;
            }
            if (now < editorStreamingNextRepaint) return;
            editorStreamingRequested = false;
            editorStreamingNextRepaint = now + (Statistics.AtlasUploadDeferredForHostMemory ? 1.0 : 1.0 / 30.0);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
        }

        [UnityEditor.MenuItem(EditorPreviewPausedMenu)]
        private static void ToggleEditorPreviewPause() => SetEditorPreviewPaused(!editorPreviewPaused);

        [UnityEditor.MenuItem(EditorPreviewPausedMenu, true)]
        private static bool ValidateEditorPreviewPause()
        {
            UnityEditor.Menu.SetChecked(EditorPreviewPausedMenu, editorPreviewPaused);
            return !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode;
        }

        public static bool IsEditorPreviewPaused => editorPreviewPaused;

        /// <summary>Temporarily hides Edit Mode VM previews and their original meshes for this Editor session.</summary>
        public static void SetEditorPreviewPaused(bool paused)
        {
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode || editorPreviewPaused == paused) return;
            ReleaseResources();
            editorPreviewPaused = paused;
            UnityEditor.SessionState.SetBool(EditorPreviewPausedKey, paused);
            if (paused) HidePausedPreviewSources();
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
        }

        private static void HidePausedPreviewSources()
        {
            currentPreviewSources.Clear();
            foreach (var instance in instances)
            {
                if (instance == null || !instance.isActiveAndEnabled || !instance.PreviewInEditMode) continue;
                var source = instance.SourceRenderer;
                if (source == null || !currentPreviewSources.Add(source)) continue;
                if (!pausedPreviewSources.ContainsKey(source)) pausedPreviewSources.Add(source, source.forceRenderingOff);
                if (!source.forceRenderingOff) source.forceRenderingOff = true;
            }

            // Reconcile changes made while paused, including reassigned source
            // renderers. Keep one original flag per renderer shared by instances.
            stalePreviewSources.Clear();
            foreach (var source in pausedPreviewSources.Keys)
                if (source == null || !currentPreviewSources.Contains(source)) stalePreviewSources.Add(source);
            foreach (var source in stalePreviewSources)
            {
                if (source != null) source.forceRenderingOff = pausedPreviewSources[source];
                pausedPreviewSources.Remove(source);
            }
        }

        private static void RestorePausedPreviewSources()
        {
            foreach (var pair in pausedPreviewSources)
                if (pair.Key != null) pair.Key.forceRenderingOff = pair.Value;
            pausedPreviewSources.Clear();
            currentPreviewSources.Clear();
            stalePreviewSources.Clear();
        }

        private static void RestoreBeforeSceneSave(UnityEngine.SceneManagement.Scene scene, string path)
        {
            RestorePausedPreviewSources();
            foreach (var instance in instances)
            {
                if (instance == null || instance.gameObject.scene != scene) continue;
                instance.RestoreSource();
                if (instance.UseChangeDrivenValidation) instance.InvalidateRenderState();
            }
        }

        private static void RestoreBeforePrefabSave(GameObject root)
        {
            if (root != null) RestoreBeforeSceneSave(root.scene, null);
        }

        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingEditMode || state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                ReleaseResources();
            else if (state == UnityEditor.PlayModeStateChange.EnteredEditMode)
                ScheduleEditorRegistration();
            else if (state == UnityEditor.PlayModeStateChange.EnteredPlayMode)
                RegisterLoadedInstances();
        }
#endif

        public static void Refresh()
        {
#if UNITY_EDITOR
            VirtualMeshGraphRuntime.InvalidatePreparedGeometryCache();
#endif
            ReleaseResources();
#if UNITY_EDITOR
            RequestEditorPreviewRefresh();
#endif
        }

        public static void ReleaseAll()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= CompleteEditorRegistration;
            editorRegistrationQueued = false;
#endif
            foreach (var instance in instances)
                if (!ReferenceEquals(instance, null)) instance.SetRegisteredForValidation(false);
            ReleaseResources();
            RenderPipelineManager.beginContextRendering -= Render;
            Application.quitting -= ReleaseAll;
            subscribed = false;
            instances.Clear();
        }

        private static void ReleaseResources()
        {
#if UNITY_EDITOR
            StopEditorStreaming();
            RestorePausedPreviewSources();
#endif
            // Restore authored renderer state before detaching GPU resources. Even a
            // resource-release failure must not leave transient suppression serialized.
            foreach (var instance in instances) if (instance != null) instance.RestoreSource();
            VirtualMeshGraphRuntime.Release(); graphActive = false;
            ReleaseLegacyResources();
            VirtualMeshVertexBindings.Release();
        }

        private static void ReleaseLegacyResources()
        {
            foreach (var instance in instances) if (instance != null) instance.RestoreSource();
            foreach (var state in cameraStates.Values) state.Dispose();
            cameraStates.Clear();
            foreach (var geometry in geometryCache.Values) geometry.Dispose();
            geometryCache.Clear();
            modelDemands.Clear(); uploadOrder.Clear(); admittedModels.Clear(); preparedCameras.Clear(); shadowLights.Clear();
            active.Clear();
            DestroyResource(cullShader); cullShader = null; renderShader = null;
            Statistics = default;
        }

        private static void DestroyResource(UnityEngine.Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(resource); else UnityEngine.Object.DestroyImmediate(resource);
        }

        private static string EnsureResources()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) return "Model instances require Universal Render Pipeline. The source renderer is retained.";
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) return "This device does not support compute shaders and instancing. The source renderer is retained.";
            if (cullShader == null)
            {
                var resource = Resources.Load<ComputeShader>("VirtualMeshModelCull");
                renderShader = Resources.Load<Shader>("VirtualMeshModelLit");
                if (resource == null || renderShader == null || !renderShader.isSupported) return "Virtual Mesh model render resources are unavailable. The source renderer is retained.";
                cullShader = UnityEngine.Object.Instantiate(resource);
                cullShader.hideFlags = HideFlags.HideAndDontSave;
                kernel = cullShader.FindKernel("Cull");
                cullShader.GetKernelThreadGroupSizes(kernel, out threads, out _, out _);
            }
            return null;
        }

        // A render context is a synchronous snapshot of the scene. Thousands of
        // instances often share a few materials; avoid crossing the native API
        // for identical shader/queue/wind checks on every one. Clear at EVERY
        // context, so runtime material edits are observed on the next render.
        internal sealed class MaterialValidationContext
        {
            internal readonly struct Snapshot
            {
                public readonly string ShaderName, ExpectedShader, Error;
                public readonly string DeclaredSourceShader;
                public readonly bool Tagged, HasVmPasses;
                public readonly float WindPadding;
                public readonly int ShadowCull;
                public Snapshot(Material material)
                {
                    var shader = material.shader;
                    ShaderName = shader != null ? shader.name : null;
                    string shadingFamily = VirtualMeshMaterialSupport.GetShadingFamily(material);
                    Tagged = shadingFamily.Length > 0;
                    HasVmPasses = Tagged && VirtualMeshMaterialSupport.HasRenderPasses(material);
                    DeclaredSourceShader = Tagged ? material.GetTag(VirtualMeshMaterialSupport.SourceShaderTag, false, string.Empty) : string.Empty;
                    ExpectedShader = Tagged ? ShaderName : VirtualMeshMaterialSupport.GetRenderShaderName(ShaderName);
                    Error = ShaderName == null ? "A model material slot is empty. The source renderer is retained." :
                        material.renderQueue > 2500 || material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f ?
                        "Transparent materials require sorted rendering. The source renderer is retained (opaque and alpha-clipped materials are supported)." : null;
                    WindPadding = GetWindPadding(material);
                    ShadowCull = VirtualMeshMaterialSupport.GetShadowCull(material);
                }
            }
            readonly Dictionary<Material, Snapshot> materials = new Dictionary<Material, Snapshot>();
            internal void Clear() => materials.Clear();
            internal Snapshot Get(Material material)
            {
                if (!materials.TryGetValue(material, out var snapshot))
                { snapshot = new Snapshot(material); materials.Add(material, snapshot); }
                return snapshot;
            }
        }

        internal static string Validate(VirtualMeshInstance instance, float determinant) => ValidateInContext(instance, determinant, null);

        internal static string ValidateInContext(VirtualMeshInstance instance, float determinant, MaterialValidationContext context) =>
            ValidateForGraph(instance, instance.SourceRenderer, determinant, context, out _);

        internal static string ValidateForGraph(VirtualMeshInstance instance, MeshRenderer renderer, float determinant,
            MaterialValidationContext context, out float windPadding)
        {
            VirtualMeshModelAsset model;
            Material[] materials, templates;
            windPadding = 0;
            bool profile = context != null && VirtualMeshGraphRuntime.ProfileDetails;
            using (profile ? validateModelMarker.Auto() : default)
            {
                model = instance.Model;
                materials = instance.Materials;
                templates = instance.RenderMaterials;
                if (model == null || !model.IsValid) return "Assign a valid prebuilt model asset. The source renderer is retained.";
                if (materials == null || materials.Length != model.SubMeshCount) return "The material slot count must match the model. The source renderer is retained.";
            }
            using (profile ? validateRendererMarker.Auto() : default)
            if (renderer != null)
            {
                int lightmapIndex = renderer.lightmapIndex;
                if (lightmapIndex >= 0 && lightmapIndex < 65534)
                    return "Baked lightmaps are not supported by model instances. The lightmapped source renderer is retained.";
                if (renderer.HasPropertyBlock())
                    return "Per-renderer MaterialPropertyBlock overrides are present. The source renderer is retained.";
            }
            using (profile ? validateComponentsMarker.Auto() : default)
            {
                if (renderer != null && (!renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh != model.SourceMesh))
                    return "The source renderer mesh differs from the prebuilt model. Rebuild or assign its matching asset.";
                if (instance.TryGetComponent<SkinnedMeshRenderer>(out _)) return "Skinned meshes are not supported by static model clusters. The source renderer is retained.";
            }
            using (profile ? validateMaterialsMarker.Auto() : default)
            for (int slot = 0; slot < materials.Length; slot++)
            {
                var material = materials[slot];
                var template = templates != null && slot < templates.Length ? templates[slot] : null;
                if (material == null) return "A model material slot is empty. The source renderer is retained.";
                var snapshot = context != null ? context.Get(material) : new MaterialValidationContext.Snapshot(material);
                if (snapshot.Error != null) return snapshot.Error;
                if (snapshot.ExpectedShader == null && template == null)
                    return "Material '" + material.name + "' uses an unsupported shader ('" + snapshot.ShaderName + "'). The source renderer is retained.";
                if (template == null && !snapshot.Tagged)
                    return "VM render material is missing for slot " + slot + ". Bake a tagged VM material or assign a tagged shader.";
                var target = template == null ? snapshot : context != null ? context.Get(template) : new MaterialValidationContext.Snapshot(template);
                if (snapshot.ExpectedShader == null && target.DeclaredSourceShader != snapshot.ShaderName)
                    return "Material '" + material.name + "' uses an unsupported shader ('" + snapshot.ShaderName + "'). The source renderer is retained.";
                if (!target.Tagged || target.ShaderName != snapshot.ExpectedShader && target.DeclaredSourceShader != snapshot.ShaderName)
                    return "A generated render material uses the wrong shader for slot " + slot + ". Rebuild the model prefab.";
                if (!target.HasVmPasses)
                    return "The tagged VM shader is missing a required pass in material slot " + slot + ". The source renderer is retained.";
                if (target.DeclaredSourceShader == snapshot.ShaderName && target.Error != null)
                    return target.Error;
                windPadding = Mathf.Max(windPadding,
                    target.DeclaredSourceShader == snapshot.ShaderName ? target.WindPadding : snapshot.WindPadding);
            }
            if (!float.IsFinite(determinant) || Mathf.Abs(determinant) < 1e-12f) return "The instance has a singular transform. The source renderer is retained.";
            return null;
        }

        private static void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            // URP can issue a begin-context callback without a camera. Such a context
            // need not reach Submit: keep the last valid state and never advance
            // resource planning, upload cursors or Ready publication into it.
            if (cameras == null || cameras.Count == 0) return;
            // Scene load/reload can precede a component's first LateUpdate. Resolve
            // dirty scene preferences once, before any atlas planning in this context.
            if (vertexStreamSceneRequestsDirty) RebuildVertexStreamSceneRequests();
#if UNITY_EDITOR
            if (editorPreviewPaused && !Application.isPlaying)
            {
                if (!UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) HidePausedPreviewSources();
                return;
            }
#endif
            renderingContext = true;
            var stats = new RuntimeStatistics();
            try
            {
                contextGeneration++;
                if (VirtualMeshGraphRuntime.IsRequested(cameras))
                {
                    if (!graphActive) { ReleaseLegacyResources(); graphActive = true; }
                    VirtualMeshGraphRuntime.Render(context, cameras, instances, contextGeneration);
                    renderingContext = false;
                    return;
                }
#if UNITY_EDITOR
                // A legacy-renderer switch must not hide a retained Graph allocation
                // from the legacy path's independent memory budget.
                VirtualMeshGraphRuntime.InvalidatePreparedGeometryCache();
#endif
                if (graphActive) { VirtualMeshGraphRuntime.Release(); graphActive = false; }
                stats.Path = "Legacy: no active Virtual Mesh Render Graph feature for these cameras.";
                stats.GpuBudgetBytes = GetGpuBudgetBytes();
                stats.RequestedSplitVertexStreams = UseSplitVertexStreams;
                string resourceError = EnsureResources();
                foreach (var camera in cameras)
                    if (camera != null && camera.stereoEnabled)
                    { resourceError = "Stereo rendering is not supported by model instances. The source renderer is retained."; break; }
                active.Clear(); usedModels.Clear();
                preparedCameras.Clear();
                foreach (var demand in modelDemands.Values) demand.ResetDemand();
                instances.RemoveWhere(instance => instance == null);
                using (validateMarker.Auto())
                foreach (var instance in instances)
                {
                    if (!instance.isActiveAndEnabled || !instance.gameObject.scene.IsValid() || !instance.gameObject.scene.isLoaded ||
                        !Application.isPlaying && !instance.PreviewInEditMode ||
                        instance.SourceRenderer != null && (!instance.SourceRenderer.enabled || instance.SourceWasHidden))
                    { instance.SetVirtualRendering(false); continue; }
                    var matrix = instance.RenderTransform.localToWorldMatrix;
                    float determinant = matrix.determinant;
                    string error = resourceError ?? Validate(instance, determinant);
                    if (error == null && VirtualMeshLodBudget.RequiresEligibility(instance.Model))
                        error = "Coverage-limited foliage LOD requires the Virtual Mesh Model Render Graph feature. The source renderer is retained.";
                    if (error != null) { instance.SetDiagnostic(error); instance.SetVirtualRendering(false); stats.LastError = error; continue; }
                    var model = instance.Model;
                    if (!modelDemands.TryGetValue(model, out var demand) || demand.Revision != model.Revision)
                    {
                        RemoveModelResources(model);
                        demand = new ModelDemand(model); modelDemands[model] = demand;
                    }
                    usedModels.Add(model); active.Add(new ActiveInstance(instance, matrix, determinant));
                }
                staleModels.Clear();
                foreach (var pair in modelDemands)
                    if (!usedModels.Contains(pair.Key)) staleModels.Add(pair.Key);
                foreach (var model in staleModels) { RemoveModelResources(model); modelDemands.Remove(model); }
                staleCameras.Clear();
                foreach (var pair in cameraStates)
                    if (pair.Value.Camera == null || Time.realtimeSinceStartupAsDouble - pair.Value.LastUsed > 10) staleCameras.Add(pair.Key);
                foreach (int id in staleCameras) { cameraStates[id].Dispose(); cameraStates.Remove(id); }
                CollectShadowLights();
                // Drop inactive shadow allocations even for cameras absent from this context.
                // In particular, disabling global shadows must also free their reserved budget.
                foreach (var state in cameraStates.Values)
                    if (shadowLights.Count == 0 || !CameraRendersShadows(state.Camera)) state.ClearShadowBatches();
                foreach (var camera in cameras)
                    if (camera != null) PrepareCamera(camera);
                ResolveResidency(ref stats);
                foreach (var cached in active)
                {
                    var instance = cached.Instance;
                    var demand = modelDemands[instance.Model];
                    if (demand.State == Residency.Ready)
                    {
                        instance.SetDiagnostic(null); instance.SetVirtualRendering(true); stats.ActiveInstances++;
                    }
                    else
                    {
                        // A shadow-only renderer must stay colorless when shadows are disabled
                        // or its GPU model is not resident; Off would expose its source colors.
                        if (cached.ShadowMode == ShadowCastingMode.ShadowsOnly) instance.SetVirtualRendering(true);
                        else instance.SetResidencyFallback();
                        string message = demand.State == Residency.Queued ? "Model GPU upload is queued. Source color is temporarily retained without high-poly shadows." :
                            demand.State == Residency.BudgetRejected ? "Virtual Mesh GPU buffer budget exceeded. Source color is retained without high-poly shadows; increase GpuBudgetMiB or reduce scene demand." : null;
                        instance.SetDiagnostic(message, false);
                    }
                }
                foreach (var state in preparedCameras) SubmitCamera(state, ref stats);
                stats.SharedModels = geometryCache.Count;
                foreach (var geometry in geometryCache.Values) stats.GeometryBytes += geometry.Bytes;
                stats.VertexBindingBytes = VirtualMeshVertexBindings.Bytes;
                stats.GpuBufferBytes = stats.GeometryBytes + GetBatchBufferBytes() + stats.VertexBindingBytes;
            }
            catch (Exception exception)
            {
                foreach (var instance in instances) if (instance != null) { instance.SetResidencyFallback(); instance.SetDiagnostic(exception.Message); }
                stats.LastError = exception.Message;
            }
            Statistics = stats;
            renderingContext = false;
        }

        internal static void PublishStatistics(RuntimeStatistics stats) => Statistics = stats;

        internal static long GetGpuBudgetBytes()
        {
            int automatic = SystemInfo.graphicsMemorySize > 0 ? Mathf.Min(3072, Mathf.Max(64, Mathf.FloorToInt(SystemInfo.graphicsMemorySize * 0.4f))) : 1536;
            return (long)(GpuBudgetMiB > 0 ? GpuBudgetMiB : automatic) * MiB;
        }

        private static long GetBatchBufferBytes()
        {
            long bytes = 0;
            foreach (var state in cameraStates.Values)
            {
                foreach (var batch in state.Batches.Values) bytes += batch.BufferBytes;
                foreach (var batch in state.ShadowBatches.Values) bytes += batch.BufferBytes;
            }
            return bytes;
        }

        private static void RemoveModelResources(VirtualMeshModelAsset model)
        {
            foreach (var state in cameraStates.Values)
            {
                staleBatches.Clear();
                foreach (var pair in state.Batches) if (pair.Key.Model == model) staleBatches.Add(pair.Key);
                foreach (var key in staleBatches) { state.Batches[key].Dispose(); state.Batches.Remove(key); }
                staleShadowBatches.Clear();
                foreach (var pair in state.ShadowBatches) if (pair.Key.Draw.Model == model) staleShadowBatches.Add(pair.Key);
                foreach (var key in staleShadowBatches) { state.ShadowBatches[key].Dispose(); state.ShadowBatches.Remove(key); }
            }
            if (geometryCache.TryGetValue(model, out var geometry)) { geometry.Dispose(); geometryCache.Remove(model); }
        }

        private static int CompareDemand(ModelDemand a, ModelDemand b)
        {
            if (a.ColorVisible != b.ColorVisible) return a.ColorVisible ? -1 : 1;
            // Small residency preference prevents tiny camera movements from swapping two models
            // at the budget boundary, while genuinely nearer models still take priority.
            float da = a.DistanceSquared * (geometryCache.ContainsKey(a.Model) ? 0.8f : 1f);
            float db = b.DistanceSquared * (geometryCache.ContainsKey(b.Model) ? 0.8f : 1f);
            int distance = da.CompareTo(db);
            return distance != 0 ? distance : a.Model.GetInstanceID().CompareTo(b.Model.GetInstanceID());
        }

        private static void ResolveResidency(ref RuntimeStatistics stats)
        {
            using var scope = resolveMarker.Auto();
            foreach (var state in cameraStates.Values)
            {
                bool current = state.PreparedGeneration == contextGeneration;
                foreach (var pair in state.Batches)
                {
                    var demand = modelDemands[pair.Key.Model];
                    demand.BatchBytes += current ? pair.Value.RequiredBufferBytes(demand.SubmeshCounts[pair.Key.Submesh]) : pair.Value.BufferBytes;
                    demand.BufferLimitExceeded |= current && (long)demand.SubmeshCounts[pair.Key.Submesh] * pair.Value.Members.Count > int.MaxValue / 8;
                }
                foreach (var pair in state.ShadowBatches)
                {
                    var demand = modelDemands[pair.Key.Draw.Model];
                    demand.BatchBytes += current ? pair.Value.RequiredBufferBytes(demand.SubmeshCounts[pair.Key.Draw.Submesh]) : pair.Value.BufferBytes;
                    demand.BufferLimitExceeded |= current && (long)demand.SubmeshCounts[pair.Key.Draw.Submesh] * pair.Value.Members.Count > int.MaxValue / 8;
                }
            }
            admittedModels.Clear(); uploadOrder.Clear();
            foreach (var demand in modelDemands.Values) if (demand.Wanted) uploadOrder.Add(demand);
            uploadOrder.Sort(CompareDemand);
            long reserved = VirtualMeshVertexBindings.RequiredBytes;
            foreach (var demand in uploadOrder)
            {
                long required = demand.Model.GeometryBytes + demand.BatchBytes;
                if (demand.BufferLimitExceeded || required > stats.GpuBudgetBytes - reserved)
                {
                    demand.State = Residency.BudgetRejected; stats.BudgetRejectedModels++;
                    stats.LastError = "Virtual Mesh GPU buffer budget exceeded; source color fallback has high-poly shadows disabled.";
                    if (!demand.RejectionWarned)
                    {
                        Debug.LogWarning("[Virtual Mesh] Model '" + demand.Model.name + "' was not admitted to the " +
                            (stats.GpuBudgetBytes / MiB) + " MiB Virtual Mesh buffer budget. Source color remains visible without high-poly shadows. " +
                            "The budget excludes textures, source meshes, and other engine GPU allocations.", demand.Model);
                        demand.RejectionWarned = true;
                    }
                    continue;
                }
                reserved += required; admittedModels.Add(demand.Model); demand.RejectionWarned = false;
                demand.State = Residency.Queued;
            }
            // Retain unused geometry briefly only when the needed models and all their draw
            // buffers have already been reserved. Stale camera buffers are included above.
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var pair in geometryCache)
            {
                var demand = modelDemands[pair.Key];
                if (demand.Wanted) continue;
                long required = pair.Value.Bytes + demand.BatchBytes;
                if (now - demand.LastNeeded <= 5.0 && required <= stats.GpuBudgetBytes - reserved)
                {
                    admittedModels.Add(pair.Key); reserved += required; demand.State = Residency.Ready;
                }
            }
            staleModels.Clear();
            foreach (var pair in geometryCache) if (!admittedModels.Contains(pair.Key)) staleModels.Add(pair.Key);
            foreach (var model in staleModels) RemoveModelResources(model);
            int remainingUploads = Mathf.Clamp(MaxModelUploadsPerContext, 1, 2);
            foreach (var demand in uploadOrder)
            {
                if (demand.State == Residency.BudgetRejected) continue;
                if (geometryCache.ContainsKey(demand.Model)) demand.State = Residency.Ready;
                else if (remainingUploads > 0)
                {
                    geometryCache.Add(demand.Model, new Geometry(demand.Model));
                    remainingUploads--; stats.UploadedModelsThisContext++; demand.State = Residency.Ready;
                }
                else stats.QueuedModels++;
            }
        }

        internal static void CollectShadowLights()
        {
            shadowLights.Clear();
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (QualitySettings.shadows == UnityEngine.ShadowQuality.Disable || !SystemInfo.supportsShadows ||
                pipeline == null || pipeline.shadowDistance <= 0f ||
                !pipeline.supportsMainLightShadows && !pipeline.supportsAdditionalLightShadows) return;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!light.isActiveAndEnabled || light.shadows == LightShadows.None || light.shadowStrength <= 0f || light.intensity <= 0f) continue;
                if (light.type == LightType.Directional && pipeline.supportsMainLightShadows ||
                    (light.type == LightType.Point || light.type == LightType.Spot) && pipeline.supportsAdditionalLightShadows)
                    shadowLights.Add(new ShadowLight(light));
            }
        }

        internal static bool CameraRendersShadows(Camera camera)
        {
            if (camera == null || QualitySettings.shadows == UnityEngine.ShadowQuality.Disable || !SystemInfo.supportsShadows) return false;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null || !pipeline.supportsMainLightShadows && !pipeline.supportsAdditionalLightShadows ||
                Mathf.Min(pipeline.shadowDistance, camera.farClipPlane) <= camera.nearClipPlane) return false;
            // URP does not apply UniversalAdditionalCameraData's toggle to Scene View cameras.
            // Cameras without that component retain the pipeline's shadow setting.
            return camera.cameraType == CameraType.SceneView ||
                !camera.TryGetComponent<UniversalAdditionalCameraData>(out var data) || data.renderShadows;
        }

        internal static Bounds GetShadowReceiverBounds(Camera camera)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float far = Mathf.Max(camera.nearClipPlane, Mathf.Min(camera.farClipPlane, pipeline != null ? pipeline.shadowDistance : camera.farClipPlane));
            Bounds bounds = default;
            for (int end = 0; end < 2; end++)
            {
                camera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), end == 0 ? camera.nearClipPlane : far, Camera.MonoOrStereoscopicEye.Mono, frustumCorners);
                for (int corner = 0; corner < 4; corner++)
                {
                    var point = camera.transform.TransformPoint(frustumCorners[corner]);
                    if (end == 0 && corner == 0) bounds = new Bounds(point, Vector3.zero); else bounds.Encapsulate(point);
                }
            }
            return bounds;
        }

        internal static bool CanCastVisibleShadow(Bounds caster, int layer, int cameraMask, Bounds receivers)
        {
            foreach (var light in shadowLights)
            {
                if ((light.CullingMask & (1 << layer)) == 0 || (light.CullingMask & cameraMask) == 0) continue;
                if (light.Type == LightType.Directional)
                {
                    var expanded = receivers;
                    expanded.Expand(caster.size);
                    if (expanded.Contains(caster.center) || expanded.IntersectRay(new Ray(caster.center, light.Direction), out float hit) && hit >= 0f)
                        return true;
                }
                else if (caster.SqrDistance(light.Position) <= light.RangeSquared && receivers.SqrDistance(light.Position) <= light.RangeSquared)
                    return true;
            }
            return false;
        }

        private static void PrepareCamera(Camera camera)
        {
            using var scope = prepareMarker.Auto();
            int id = camera.GetInstanceID();
            if (!cameraStates.TryGetValue(id, out var state)) { state = new CameraState { Camera = camera }; cameraStates.Add(id, state); }
            if (state.PreparedGeneration == contextGeneration) return;
            state.PreparedGeneration = contextGeneration;
            preparedCameras.Add(state);
            state.LastUsed = Time.realtimeSinceStartupAsDouble;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            bool renderShadows = shadowLights.Count != 0 && CameraRendersShadows(camera);
            Bounds shadowReceivers = renderShadows ? GetShadowReceiverBounds(camera) : default;
            foreach (var batch in state.Batches.Values) batch.Members.Clear();
            foreach (var batch in state.ShadowBatches.Values) batch.Members.Clear();
            for (int activeIndex = 0; activeIndex < active.Count; activeIndex++)
            {
                var cached = active[activeIndex];
                var instance = cached.Instance;
                if ((camera.cullingMask & (1 << instance.RenderTransform.gameObject.layer)) == 0) continue;
#if UNITY_EDITOR
                if (camera.cameraType == CameraType.SceneView && UnityEditor.SceneVisibilityManager.instance.IsHidden(instance.RenderTransform.gameObject)) continue;
#endif
                bool inFrustum = GeometryUtility.TestPlanesAABB(frustum, cached.Bounds);
                bool colorVisible = inFrustum && cached.ShadowMode != ShadowCastingMode.ShadowsOnly;
                bool shadowCandidate = renderShadows && cached.ShadowMode != ShadowCastingMode.Off &&
                    CanCastVisibleShadow(cached.Bounds, instance.RenderTransform.gameObject.layer, camera.cullingMask, shadowReceivers);
                if (!colorVisible && !shadowCandidate) continue;
                var demand = modelDemands[instance.Model];
                demand.Wanted = true; demand.ColorVisible |= colorVisible;
                demand.DistanceSquared = Mathf.Min(demand.DistanceSquared, cached.Bounds.SqrDistance(camera.transform.position));
                demand.LastNeeded = state.LastUsed;
                for (int submesh = 0; submesh < instance.Model.SubMeshCount; submesh++)
                {
                    if (demand.SubmeshCounts[submesh] == 0) continue;
                    var key = new BatchKey(instance, instance.Materials[submesh], submesh);
                    // Off-screen objects can cast visible shadows. Keep their cluster LOD list
                    // independent of the camera-frustum list, with spatially bounded draws.
                    if (shadowCandidate)
                    {
                        var shadowKey = new ShadowBatchKey(key, cached);
                        if (!state.ShadowBatches.TryGetValue(shadowKey, out var shadowBatch))
                        {
                            shadowBatch = new Batch(key.Material, key.RenderMaterial, shadowKey.Cull);
                            state.ShadowBatches.Add(shadowKey, shadowBatch);
                        }
                        shadowBatch.Members.Add(activeIndex); shadowBatch.LastUsed = state.LastUsed;
                    }
                    if (!colorVisible) continue;
                    if (!state.Batches.TryGetValue(key, out var batch))
                    {
                        // The color pass does not use this value, so use the source's normal
                        // shadow cull state to share the same material with its shadow draws.
                        batch = new Batch(key.Material, key.RenderMaterial, VirtualMeshMaterialSupport.GetShadowCull(key.Material));
                        state.Batches.Add(key, batch);
                    }
                    batch.Members.Add(activeIndex); batch.LastUsed = state.LastUsed;
                }
            }
            staleBatches.Clear();
            foreach (var pair in state.Batches)
                if (pair.Value.Members.Count == 0 && state.LastUsed - pair.Value.LastUsed > 2.0) staleBatches.Add(pair.Key);
            foreach (var key in staleBatches) { state.Batches[key].Dispose(); state.Batches.Remove(key); }
            staleShadowBatches.Clear();
            foreach (var pair in state.ShadowBatches)
                if (pair.Value.Members.Count == 0 && state.LastUsed - pair.Value.LastUsed > 2.0) staleShadowBatches.Add(pair.Key);
            foreach (var key in staleShadowBatches) { state.ShadowBatches[key].Dispose(); state.ShadowBatches.Remove(key); }
        }

        private static void SubmitCamera(CameraState state, ref RuntimeStatistics stats)
        {
            using var scope = submitMarker.Auto();
            var camera = state.Camera;
            shadowCullUploaded = false;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            for (int i = 0; i < 6; i++) frustumVectors[i] = new Vector4(frustum[i].normal.x, frustum[i].normal.y, frustum[i].normal.z, frustum[i].distance);
            cullShader.SetVectorArray("_VMFrustumPlanes", frustumVectors);
            cullShader.SetVector("_VMCameraPosition", camera.transform.position);
            cullShader.SetVector("_VMCameraForward", camera.transform.forward);
            cullShader.SetVector("_VMLodParams", new Vector4(camera.pixelHeight * Mathf.Abs(camera.projectionMatrix.m11) * 0.5f,
                camera.orthographic ? 1f : 0f, Mathf.Max(0.001f, camera.nearClipPlane), 0f));
            foreach (var pair in state.Batches)
                if (pair.Value.Members.Count != 0 && geometryCache.ContainsKey(pair.Key.Model)) SubmitBatch(camera, pair.Key, pair.Value, false, ref stats);
            foreach (var pair in state.ShadowBatches)
                if (pair.Value.Members.Count != 0 && geometryCache.ContainsKey(pair.Key.Draw.Model)) SubmitBatch(camera, pair.Key.Draw, pair.Value, true, ref stats);
        }

        private static void SubmitBatch(Camera camera, BatchKey key, Batch batch, bool shadow,
            ref RuntimeStatistics stats)
        {
            var geometry = geometryCache[key.Model];
            batch.Prepare(key, geometry);
            batch.Shadow = shadow; batch.SubmittedGeneration = contextGeneration;
            Bounds bounds = default;
            for (int i = 0; i < batch.Members.Count; i++)
            {
                var cached = active[batch.Members[i]];
                var data = cached.Data;
                if (shadow) data.parameters.y *= cached.ShadowLodMultiplier;
                batch.InstanceArray[i] = data;
                if (i == 0) bounds = cached.Bounds; else bounds.Encapsulate(cached.Bounds);
            }
            using (uploadMarker.Auto()) batch.Instances.SetData(batch.InstanceArray, 0, 0, batch.Members.Count);
            long total = (long)geometry.SubmeshCounts[key.Submesh] * batch.Members.Count;
            if (total > int.MaxValue) throw new InvalidOperationException("Too many cluster candidates in one model group.");
            using (computeMarker.Auto())
            {
                batch.Visible.SetCounterValue(0);
                cullShader.SetBuffer(kernel, "_VMClusters", geometry.Clusters);
                cullShader.SetBuffer(kernel, "_VMInstances", batch.Instances);
                cullShader.SetBuffer(kernel, "_VMVisibleClusters", batch.Visible);
                cullShader.SetInt("_VMClusterOffset", geometry.SubmeshOffsets[key.Submesh]);
                cullShader.SetInt("_VMClusterCount", geometry.SubmeshCounts[key.Submesh]);
                cullShader.SetInt("_VMInstanceCount", batch.Members.Count);
                cullShader.SetInt("_VMSubmesh", key.Submesh);
                cullShader.SetInt("_VMShadowPass", shadow ? 1 : 0);
                if (shadow) UploadShadowProjectionCull(camera, key.Layer);
                cullShader.SetFloat("_VMWindPadding", GetWindPadding(key.Material));
                int block = checked((int)threads * 65535);
                for (long offset = 0; offset < total; offset += block)
                {
                    cullShader.SetInt("_VMDispatchOffset", (int)offset);
                    int count = (int)Math.Min(block, total - offset);
                    cullShader.Dispatch(kernel, (count + (int)threads - 1) / (int)threads, 1, 1);
                }
                GraphicsBuffer.CopyCount(batch.Visible, batch.Arguments, sizeof(uint));
            }
            var renderParams = new RenderParams(batch.Material) { camera = camera, worldBounds = bounds, layer = key.Layer,
                renderingLayerMask = key.RenderingLayers, receiveShadows = !shadow && key.ReceiveShadows,
                shadowCastingMode = shadow ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off,
                overrideSceneCullingMask = true, sceneCullingMask = key.SceneMask, matProps = batch.Properties,
                lightProbeUsage = shadow ? LightProbeUsage.Off : LightProbeUsage.BlendProbes,
                reflectionProbeUsage = shadow ? ReflectionProbeUsage.Off : ReflectionProbeUsage.BlendProbes,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion };
            batch.ReadVisibleCount();
            using (drawMarker.Auto()) Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, batch.Arguments, 1);
            if (shadow)
            {
                stats.ShadowDrawBatches++; stats.ShadowCandidateClusters += total;
                stats.VisibleShadowClusters += batch.VisibleCount;
            }
            else
            {
                stats.DrawBatches++; stats.CandidateClusters += total;
                stats.VisibleClusters += batch.VisibleCount;
            }
        }

        private static void UploadShadowProjectionCull(Camera camera, int layer)
        {
            if (shadowCullUploaded && shadowCullCamera == camera && shadowCullLayer == layer) return;
            shadowCullUploaded = true; shadowCullCamera = camera; shadowCullLayer = layer;
            // Zero is also the backward-compatible default for standalone compute callers.
            cullShader.SetInt("_VMShadowLightCount", 0);
            var projection = camera.projectionMatrix;
            // An oblique near/far clip cannot safely be replaced by a camera-depth far plane.
            // Keep the existing conservative CPU AABB test for these custom projections.
            if (Mathf.Abs(projection.m20) > 0.00001f || Mathf.Abs(projection.m21) > 0.00001f) return;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float far = Mathf.Min(camera.farClipPlane, pipeline != null ? pipeline.shadowDistance : camera.farClipPlane);
            if (!float.IsFinite(far) || far <= camera.nearClipPlane) return;
            GeometryUtility.CalculateFrustumPlanes(projection * camera.worldToCameraMatrix, shadowReceiverPlanes);
            shadowReceiverPlanes[5] = new Plane(-camera.transform.forward, camera.transform.position + camera.transform.forward * far);
            for (int plane = 0; plane < 6; plane++)
            {
                var p = shadowReceiverPlanes[plane];
                if (!float.IsFinite(p.distance) || !float.IsFinite(p.normal.sqrMagnitude) || p.normal.sqrMagnitude < 0.5f) return;
                shadowReceiverVectors[plane] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }
            int count = 0;
            foreach (var light in shadowLights)
            {
                if ((light.CullingMask & (1 << layer)) == 0 || (light.CullingMask & camera.cullingMask) == 0) continue;
                // A truncated light list could remove a real caster. Disable this optimization
                // entirely if the fixed shader array cannot represent every relevant light.
                if (count == MaxShadowCullLights) return;
                shadowLightDirections[count] = new Vector4(light.Direction.x, light.Direction.y, light.Direction.z,
                    light.Type == LightType.Directional ? 0f : 1f);
                shadowLightPositionsRanges[count] = new Vector4(light.Position.x, light.Position.y, light.Position.z, Mathf.Sqrt(light.RangeSquared));
                count++;
            }
            if (count == 0) return;
            cullShader.SetVectorArray("_VMShadowReceiverPlanes", shadowReceiverVectors);
            cullShader.SetVectorArray("_VMShadowLightDirections", shadowLightDirections);
            cullShader.SetVectorArray("_VMShadowLightPositionsRanges", shadowLightPositionsRanges);
            cullShader.SetInt("_VMShadowLightCount", count);
        }

        internal static void GetShadowProjectionSnapshot(Camera camera, int layer, out Vector4[] receivers,
            out Vector4[] directions, out Vector4[] positions, out int count)
        {
            receivers = directions = positions = Array.Empty<Vector4>(); count = 0;
            var projection = camera.projectionMatrix;
            if (Mathf.Abs(projection.m20) > 0.00001f || Mathf.Abs(projection.m21) > 0.00001f) return;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float far = Mathf.Min(camera.farClipPlane, pipeline != null ? pipeline.shadowDistance : camera.farClipPlane);
            if (!float.IsFinite(far) || far <= camera.nearClipPlane) return;
            var planes = GeometryUtility.CalculateFrustumPlanes(projection * camera.worldToCameraMatrix);
            planes[5] = new Plane(-camera.transform.forward, camera.transform.position + camera.transform.forward * far);
            var receiverData = new Vector4[6];
            for (int i = 0; i < planes.Length; i++)
            {
                var plane = planes[i];
                if (!float.IsFinite(plane.distance) || !float.IsFinite(plane.normal.sqrMagnitude) || plane.normal.sqrMagnitude < 0.5f) return;
                receiverData[i] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            }
            var lightDirections = new Vector4[MaxShadowCullLights]; var lightPositions = new Vector4[MaxShadowCullLights];
            foreach (var light in shadowLights)
            {
                if ((light.CullingMask & (1 << layer)) == 0 || (light.CullingMask & camera.cullingMask) == 0) continue;
                if (count == MaxShadowCullLights) { count = 0; return; }
                lightDirections[count] = new Vector4(light.Direction.x, light.Direction.y, light.Direction.z, light.Type == LightType.Directional ? 0 : 1);
                lightPositions[count] = new Vector4(light.Position.x, light.Position.y, light.Position.z, Mathf.Sqrt(light.RangeSquared));
                count++;
            }
            if (count == 0) return;
            receivers = receiverData; directions = lightDirections; positions = lightPositions;
        }

        internal static float GetWindPadding(Material material)
        {
            if (material == null || !material.HasProperty("_WindEnabled") || material.GetFloat("_WindEnabled") <= 0f ||
                !material.HasProperty("_WindAmplitude")) return 0f;
            float amplitude = material.GetFloat("_WindAmplitude");
            return float.IsFinite(amplitude) ? Mathf.Abs(amplitude) : 0f;
        }

        internal static float MaximumStretch(Matrix4x4 m)
        {
            // The three-argument overload allocates a params array per call.
            float rows = Mathf.Max(Mathf.Abs(m.m00) + Mathf.Abs(m.m01) + Mathf.Abs(m.m02),
                Mathf.Max(Mathf.Abs(m.m10) + Mathf.Abs(m.m11) + Mathf.Abs(m.m12), Mathf.Abs(m.m20) + Mathf.Abs(m.m21) + Mathf.Abs(m.m22)));
            float columns = Mathf.Max(Mathf.Abs(m.m00) + Mathf.Abs(m.m10) + Mathf.Abs(m.m20),
                Mathf.Max(Mathf.Abs(m.m01) + Mathf.Abs(m.m11) + Mathf.Abs(m.m21), Mathf.Abs(m.m02) + Mathf.Abs(m.m12) + Mathf.Abs(m.m22)));
            return Mathf.Sqrt(rows * columns);
        }

        internal static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 x = matrix.MultiplyVector(new Vector3(bounds.extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, bounds.extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, bounds.extents.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f);
        }
    }
}
