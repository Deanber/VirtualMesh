using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>CPU residency and draw data for the Render Graph model renderer.</summary>
    internal static partial class VirtualMeshGraphRuntime
    {
        // Installed by the renderer feature. The predicate must identify the camera's actual
        // renderer before URP culls native shadow casters, not only during AddRenderPasses.
        internal static Func<Camera, bool> CameraUsesGraph;
        internal static Func<Camera, VirtualMeshLodBudgetSettings> CameraLodBudget;
        internal static Func<Camera, float> CameraImpostorMaxHeight;

        [Serializable]
        internal struct CounterStatistics
        {
            public long Early, Deferred, Recovered, Final, ConeOrProjectionRejected, FrustumRejected, CurrentOccluded, LODSelected;
            public long FrustumRejectedWorkGroups;
            public long TruncatedClusters;
            internal void Add(uint[] values)
            {
                Early += values[0]; Deferred += values[1]; Recovered += values[2]; Final += values[3];
                ConeOrProjectionRejected += values[4]; FrustumRejected += values[5]; CurrentOccluded += values[6]; LODSelected += values[7];
                FrustumRejectedWorkGroups += values[8];
                TruncatedClusters += values[9];
            }
        }

        [Serializable]
        internal struct CameraStatistics
        {
            public string CameraName;
            public int Generation, SampleGeneration, OldestSampleGeneration, NewestSampleGeneration;
            public int ColorBatches, ShadowBatches, PendingSamples, CompletedSamples, ExpectedSamples;
            public bool CompleteSample;
            public CounterStatistics Color, Shadow;
            public long InstanceDataUploadBytes, ShadowInstanceDataUploadBytes, ModelIdUploadBytes, PreviousTransformUploadBytes;
            public long CameraLodDataUploadBytes, ShadowLodDataUploadBytes;
            public long RequestedBudget, EstimatedBudgetTriangles, ShadowRequestedBudget, ShadowEstimatedBudgetTriangles;
            public int ProfiledInstances, UnmetInstances, UnprofiledInstances, ShadowProfiledInstances, ShadowUnmetInstances;
            public float MaximumBudgetScale, MaximumShadowBudgetScale;
            public int CoverageLimitedInstances, ShadowCoverageLimitedInstances;
            public int MaterialCoverageLimitedInstances, ShadowMaterialCoverageLimitedInstances;
            public int LodSelectionCacheHits, LodSelectionCacheMisses;
            public int ProxyColorInstances, ProxyShadowInstances, ProxyVisibleInstances, ProxyUnavailableInstances;
            public long ProxyColorTriangles, ProxyShadowTriangles;
            public long ProxyTextureBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ModelData
        {
            public uint VertexOffset, IndexOffset, ClusterOffset, ClusterCount;
            public Vector4 BoundsCenter;
            public const int Stride = 32;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WorkItem
        {
            public uint ClusterStart, Count, InstanceId, ModelId, BoundsIndex;
            public const int Stride = 20;
        }

        private const int ClustersPerWorkItem = 64;
        internal const int VisibilityCounterCount = 10;

        [StructLayout(LayoutKind.Sequential)]
        private struct WorkBounds
        {
            public Vector4 CenterRadius, Extents;
            public const int Stride = 32;
        }

        internal sealed class Atlas
        {
            public GraphicsBuffer Vertices, Indices, Clusters, CullingData, ModelTable, WorkBounds;
            public GraphicsBuffer VertexPositionUvColor, VertexNormalTangent, VertexCold;
            public bool SplitVertexStreams;
            public int VertexCount;
            public long VertexPayloadBytes;
            internal VirtualMeshVertexStreamScratch VertexUploadScratch;
            internal VirtualMeshVertexGpuScratch GpuVertexUpload;
#if UNITY_EDITOR
            internal VirtualMeshEditorGeometryCache EditorOwner;
            internal string[] EditorDependencyHashes;
            internal string EditorTransposeHash;
            internal List<string> EditorBufferNames;
            internal CacheCopyTransfer EditorCopyTransfer;
            internal GraphicsBuffer[] EditorCopyCarriers;
#endif
            public int Revision;
            public long Bytes;
            public bool HasCullingData;
        }

        /// <summary>Borrowed immutable draw snapshot, valid until the next rendering context.</summary>
        internal sealed class Batch
        {
            public readonly GraphicsBuffer WorkItems, Instances, InstanceModelIds, LodParameters;
            public readonly GraphicsBuffer Visible, Deferred;
            public readonly GraphicsBuffer Arguments, EarlyArguments, RecoveredArguments;
            public readonly GraphicsBuffer Counters, RetestDispatchArguments;
            public readonly Material Material;
            public readonly MaterialPropertyBlock Properties;
            public readonly Bounds Bounds;
            public readonly int WorkCount, Capacity, Cull, Layer;
            public readonly uint RenderingLayers;
            public readonly ulong SceneMask;
            public readonly long CandidateCount, WorkUploadBytes;
            public readonly float WindPadding;
            public readonly bool Shadow, ReceiveShadows;
            public readonly Vector4[] ShadowReceiverPlanes, ShadowLightDirections, ShadowLightPositionsRanges;
            public readonly int ShadowLightCount;
            internal readonly BatchState Owner;

            internal Batch(BatchState owner, GraphicsBuffer instances, GraphicsBuffer modelIds, GraphicsBuffer lodParameters, Bounds bounds,
                long candidateCount, float windPadding, Vector4[] receiverPlanes, Vector4[] lightDirections,
                Vector4[] lightPositionsRanges, int lightCount)
            {
                Owner = owner;
                WorkItems = owner.WorkItems; WorkCount = owner.WorkCount;
                WorkUploadBytes = owner.WorkUploadBytes;
                Instances = instances; InstanceModelIds = modelIds; LodParameters = lodParameters;
                Visible = owner.Visible; Deferred = owner.Deferred;
                Arguments = owner.Arguments; EarlyArguments = owner.EarlyArguments; RecoveredArguments = owner.RecoveredArguments;
                Counters = owner.Counters;
                RetestDispatchArguments = owner.RetestDispatchArguments;
                Material = owner.Shared.Material; Properties = owner.Properties;
                Bounds = bounds; CandidateCount = candidateCount; Capacity = owner.Capacity;
                Cull = owner.Key.Cull; Layer = owner.Key.Layer; RenderingLayers = owner.Key.RenderingLayers;
                SceneMask = owner.Key.SceneMask; Shadow = owner.Key.Shadow; ReceiveShadows = owner.Key.ReceiveShadows;
                WindPadding = windPadding;
                ShadowReceiverPlanes = receiverPlanes; ShadowLightDirections = lightDirections;
                ShadowLightPositionsRanges = lightPositionsRanges; ShadowLightCount = lightCount;
            }
        }

        internal sealed class Frame
        {
            public readonly Camera Camera;
            public readonly int Generation, HistoryRevision;
            public readonly Atlas Atlas;
            public readonly GraphicsBuffer Instances, ShadowInstances, InstanceModelIds, PreviousObjectToWorld;
            public readonly GraphicsBuffer ColorLodParameters, ShadowLodParameters;
            public readonly Batch[] Colors, Shadows;
            public readonly VirtualMeshImpostorRuntime.Batch[] ProxyColors, ProxyShadows;
            public readonly Vector4[] FrustumPlanes;
            public readonly Vector4 CameraPosition, CameraForward, LodParams;
            public readonly bool UsesRenderGraph;
            public Batch[] ColorBatches => Colors;
            public Batch[] ShadowBatches => Shadows;

            internal Frame(Camera camera, int generation, Atlas atlas, GraphicsBuffer instances,
                GraphicsBuffer modelIds, GraphicsBuffer previousMatrices, GraphicsBuffer colorLodParameters,
                GraphicsBuffer shadowLodParameters, int historyRevision,
                Batch[] colors, Batch[] shadows, bool usesGraph,
                VirtualMeshImpostorRuntime.Batch[] proxyColors, VirtualMeshImpostorRuntime.Batch[] proxyShadows)
            {
                Camera = camera; Generation = generation; Atlas = atlas; HistoryRevision = historyRevision;
                Instances = ShadowInstances = instances; InstanceModelIds = modelIds; PreviousObjectToWorld = previousMatrices;
                ColorLodParameters = colorLodParameters; ShadowLodParameters = shadowLodParameters;
                Colors = colors; Shadows = shadows; UsesRenderGraph = usesGraph;
                ProxyColors = proxyColors; ProxyShadows = proxyShadows;
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                FrustumPlanes = new Vector4[6];
                for (int i = 0; i < 6; i++) FrustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
                CameraPosition = camera.transform.position; CameraForward = camera.transform.forward;
                LodParams = new Vector4(camera.pixelHeight * Mathf.Abs(camera.projectionMatrix.m11) * 0.5f,
                    camera.orthographic ? 1f : 0f, Mathf.Max(0.001f, camera.nearClipPlane), 0f);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct InstanceData
        {
            public Matrix4x4 ObjectToWorld, WorldToObject;
            public Vector4 Parameters;
            public const int Stride = 144;
        }

        private const int LodParameterStride = 2 * sizeof(float);
        private const int InstanceBufferStride = InstanceData.Stride + sizeof(uint);

        internal readonly struct BatchKey : IEquatable<BatchKey>
        {
            public readonly Material Source, Template;
            public readonly int Cull, Layer;
            public readonly uint RenderingLayers;
            public readonly ulong SceneMask;
            public readonly bool Shadow, ReceiveShadows;
            public BatchKey(VirtualMeshInstance instance, int submesh, bool shadow)
            {
                Source = instance.Materials[submesh];
                Template = instance.RenderMaterials != null && submesh < instance.RenderMaterials.Length ? instance.RenderMaterials[submesh] : null;
                Cull = shadow && instance.SourceShadowMode == ShadowCastingMode.TwoSided ? 0 : materialValidation.Get(Source).ShadowCull;
                Layer = instance.RenderTransform.gameObject.layer;
                RenderingLayers = instance.SourceRenderer != null ? instance.SourceRenderer.renderingLayerMask : 1u;
                SceneMask = instance.RenderTransform.gameObject.sceneCullingMask;
                Shadow = shadow; ReceiveShadows = instance.SourceRenderer == null || instance.SourceRenderer.receiveShadows;
            }
            public BatchKey(Active item, int submesh, bool shadow)
            {
                Source = item.SourceMaterials[submesh];
                Template = item.Templates != null && submesh < item.Templates.Length ? item.Templates[submesh] : null;
                Cull = shadow && item.ShadowMode == ShadowCastingMode.TwoSided ? 0 : materialValidation.Get(Source).ShadowCull;
                Layer = item.Layer; RenderingLayers = item.RenderingLayers; SceneMask = item.SceneMask;
                Shadow = shadow; ReceiveShadows = item.ReceiveShadows;
            }
            public bool Equals(BatchKey other) => Source == other.Source && Template == other.Template && Cull == other.Cull &&
                Layer == other.Layer && RenderingLayers == other.RenderingLayers && SceneMask == other.SceneMask &&
                Shadow == other.Shadow && ReceiveShadows == other.ReceiveShadows;
            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Source.GetInstanceID(); hash = hash * 397 ^ (Template != null ? Template.GetInstanceID() : 0);
                    hash = hash * 397 ^ Cull; hash = hash * 397 ^ Layer; hash = hash * 397 ^ (int)RenderingLayers;
                    hash = hash * 397 ^ SceneMask.GetHashCode(); hash = hash * 397 ^ (Shadow ? 1 : 0);
                    return hash * 397 ^ (ReceiveShadows ? 1 : 0);
                }
            }
        }

        internal readonly struct MaterialKey : IEquatable<MaterialKey>
        {
            public readonly Material Source, Template;
            public readonly int Cull;
            public readonly bool ReceiveShadows;
            public MaterialKey(BatchKey key) { Source = key.Source; Template = key.Template; Cull = key.Cull; ReceiveShadows = key.ReceiveShadows; }
            public bool Equals(MaterialKey other) => Source == other.Source && Template == other.Template && Cull == other.Cull && ReceiveShadows == other.ReceiveShadows;
            public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (((Source.GetInstanceID() * 397 ^ (Template != null ? Template.GetInstanceID() : 0)) * 397 ^ Cull) * 397) ^ (ReceiveShadows ? 1 : 0); }
            }
        }

        internal sealed class SharedMaterial
        {
            public readonly MaterialKey Key;
            public readonly Material Material;
            public int References, Generation = -1;
            public SharedMaterial(MaterialKey key)
            {
                Key = key;
                var shader = (key.Template != null ? key.Template : key.Source).shader;
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("Virtual Mesh graph shader is unavailable for '" + key.Source.name + "'.");
                Material = key.Template != null ? new Material(key.Template) : new Material(shader);
                Material.name = key.Source.name + " (Virtual Mesh Graph)"; Material.hideFlags = HideFlags.HideAndDontSave;
            }
            public void Synchronize(int generation)
            {
                if (Generation == generation) return;
                using var scope = materialMarker.Auto();
                // A material/template may change shader without changing its
                // object identity (and therefore without changing the batch key).
                // CopyPropertiesFromMaterial does not copy the shader itself.
                var shader = (Key.Template != null ? Key.Template : Key.Source).shader;
                if (shader == null || !shader.isSupported)
                    throw new InvalidOperationException("Virtual Mesh graph shader is unavailable for '" + Key.Source.name + "'.");
                if (Material.shader != shader) Material.shader = shader;
                Material.CopyPropertiesFromMaterial(VirtualMeshMaterialSupport.GetPropertyMaterial(Key.Source, Key.Template));
                Material.enableInstancing = true;
                if (Material.HasProperty("_VMUnlit")) Material.SetFloat("_VMUnlit", Key.Source.shader.name == "Universal Render Pipeline/Unlit" ? 1f :
                    Key.Source.HasProperty("_VMUnlit") ? Key.Source.GetFloat("_VMUnlit") : 0f);
                if (Material.HasProperty("_VMShadowCull")) Material.SetFloat("_VMShadowCull", Key.Cull);
                Generation = generation;
            }
        }

        internal sealed class BatchState : IDisposable
        {
            private readonly struct WorkRange
            {
                public readonly uint ClusterStart, InstanceId, ModelId, BoundsStart;
                public readonly int Count;

                public WorkRange(uint clusterStart, int count, uint instanceId, uint modelId, uint boundsStart)
                {
                    ClusterStart = clusterStart;
                    Count = count;
                    InstanceId = instanceId;
                    ModelId = modelId;
                    BoundsStart = boundsStart;
                }

                public bool Matches(WorkRange other) => ClusterStart == other.ClusterStart &&
                    Count == other.Count && InstanceId == other.InstanceId && ModelId == other.ModelId && BoundsStart == other.BoundsStart;
            }

            public readonly BatchKey Key;
            public readonly SharedMaterial Shared;
            public readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            private readonly List<WorkRange> workRanges = new List<WorkRange>();
            private readonly List<WorkRange> uploadedRanges = new List<WorkRange>();
            private bool workChanged;
            public GraphicsBuffer WorkItems, Visible, Deferred, Arguments, EarlyArguments, RecoveredArguments, Counters, RetestDispatchArguments;
            public Bounds Bounds;
            public int Capacity, WorkCapacity, SubmittedGeneration;
            public int WorkCount { get; private set; }
            public long WorkUploadBytes { get; private set; }
            public long CandidateCount, SelectedCapacity, VisibleCount;
            public bool Disposed, ReadbackPending, ReadbackScheduled, HasBounds;
            public double LastUsed, LastReadback;
            public readonly uint[] LastCounters = new uint[VisibilityCounterCount];
            public int ScheduledSampleGeneration, LastSampleGeneration = -1;
            public BatchState(BatchKey key)
            {
                Key = key;
                var materialKey = new MaterialKey(key);
                if (!materials.TryGetValue(materialKey, out var shared)) { shared = new SharedMaterial(materialKey); materials.Add(materialKey, shared); }
                Shared = shared; Shared.References++;
            }
            public void Reset()
            {
                workRanges.Clear();
                workChanged = false;
                WorkCount = 0;
                WorkUploadBytes = 0;
                CandidateCount = SelectedCapacity = 0;
                HasBounds = false;
            }

            public void AddWork(uint clusterStart, int count, uint instanceId, uint modelId, uint boundsStart)
            {
                var range = new WorkRange(clusterStart, count, instanceId, modelId, boundsStart);
                int index = workRanges.Count;
                if (!workChanged && (index >= uploadedRanges.Count || !range.Matches(uploadedRanges[index])))
                    workChanged = true;
                workRanges.Add(range);
                WorkCount = checked(WorkCount + 1 + (count - 1) / ClustersPerWorkItem);
            }

            public long RequiredBytes => RequiredBufferBytes(Key.Shadow, checked((int)SelectedCapacity), WorkCount);
            public long Bytes => (long)WorkCapacity * WorkItem.Stride + (long)Capacity * 8 * (Key.Shadow ? 1 : 2) +
                (Arguments != null ? GraphicsBuffer.IndirectDrawArgs.size * (Key.Shadow ? 1 : 3) +
                    (Key.Shadow ? 0 : 12) + VisibilityCounterCount * sizeof(uint) : 0);
            public void Allocate()
            {
                int capacity = Math.Max(1, checked((int)SelectedCapacity));
                if (Visible == null || Capacity < capacity)
                {
                    Visible?.Dispose(); Deferred?.Dispose();
                    Visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 8);
                    if (!Key.Shadow) Deferred = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 8);
                    Capacity = capacity;
                }
                bool replaceWorkBuffer = WorkItems == null || WorkCapacity < WorkCount;
                if (replaceWorkBuffer)
                {
                    WorkItems?.Dispose(); WorkCapacity = Math.Max(1, WorkCount);
                    WorkItems = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WorkCapacity, WorkItem.Stride);
                }
                if (Arguments == null)
                {
                    Arguments = CreateDrawArguments();
                    Counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VisibilityCounterCount, sizeof(uint));
                    if (!Key.Shadow)
                    {
                        EarlyArguments = CreateDrawArguments(); RecoveredArguments = CreateDrawArguments();
                        RetestDispatchArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, 4);
                        RetestDispatchArguments.SetData(new uint[] { 0, 1, 1 });
                    }
                }
                if (replaceWorkBuffer || workChanged || workRanges.Count != uploadedRanges.Count)
                    UploadWork();
            }

            private void UploadWork()
            {
                // The GPU buffer holds only immutable dispatch addresses. Transforms, visibility
                // and selected capacity are still rebuilt independently for the current camera.
                using var scope = workItemsMarker.Auto();
                workItemScratch.Clear();
                if (workItemScratch.Capacity < WorkCount) workItemScratch.Capacity = WorkCount;
                foreach (var range in workRanges)
                {
                    int remaining = range.Count;
                    uint start = range.ClusterStart;
                    uint bounds = range.BoundsStart;
                    while (remaining > 0)
                    {
                        int count = Math.Min(ClustersPerWorkItem, remaining);
                        workItemScratch.Add(new WorkItem
                        {
                            ClusterStart = start,
                            Count = (uint)count,
                            InstanceId = range.InstanceId,
                            ModelId = range.ModelId,
                            BoundsIndex = bounds++
                        });
                        start += (uint)count;
                        remaining -= count;
                    }
                }
                using (uploadMarker.Auto()) WorkItems.SetData(workItemScratch);
                WorkUploadBytes = (long)WorkCount * WorkItem.Stride;
                uploadedRanges.Clear();
                uploadedRanges.AddRange(workRanges);
                workChanged = false;
            }
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                WorkItems?.Dispose(); Visible?.Dispose(); Deferred?.Dispose();
                Arguments?.Dispose(); EarlyArguments?.Dispose(); RecoveredArguments?.Dispose(); Counters?.Dispose();
                RetestDispatchArguments?.Dispose();
                if (--Shared.References == 0) { materials.Remove(Shared.Key); Destroy(Shared.Material); }
            }
        }

        private sealed class Entry
        {
            public VirtualMeshModelAsset Model;
            public int Revision, CullingRevision, Id;
            public ModelData Data;
            public int[] SubmeshStarts, SubmeshCounts;
            public uint[] SubmeshWorkBounds;
            public uint WorkBoundsOffset;
            public int WorkBoundsCount;
            public bool Ready;
            public int UploadStream, UploadElement;
            public VirtualMeshModelCluster[] UploadClusters;
            public VirtualMeshModelClusterCullingData[] UploadCones;
            public WorkBounds[] UploadBounds;
        }

        private readonly struct GroupBoundsKey : IEquatable<GroupBoundsKey>
        {
            private readonly int x, y, z, radius;
            public GroupBoundsKey(VirtualMeshModelCluster cluster)
            {
                x = BitConverter.SingleToInt32Bits(cluster.center.x); y = BitConverter.SingleToInt32Bits(cluster.center.y);
                z = BitConverter.SingleToInt32Bits(cluster.center.z); radius = BitConverter.SingleToInt32Bits(cluster.radius);
            }
            public bool Equals(GroupBoundsKey other) => x == other.x && y == other.y && z == other.z && radius == other.radius;
            public override bool Equals(object obj) => obj is GroupBoundsKey other && Equals(other);
            public override int GetHashCode() { unchecked { return ((x * 397 ^ y) * 397 ^ z) * 397 ^ radius; } }
        }

        private readonly struct CapacityInterval : IEquatable<CapacityInterval>
        {
            public readonly uint Type;
            public readonly float Error, ParentError, Spacing, ParentSpacing;
            public CapacityInterval(VirtualMeshModelCluster cluster)
            { Type = cluster.type; Error = cluster.error; ParentError = cluster.parentError; Spacing = cluster.triangleSpacing; ParentSpacing = cluster.parentTriangleSpacing; }
            public bool Equals(CapacityInterval other) => Type == other.Type && Error.Equals(other.Error) && ParentError.Equals(other.ParentError) &&
                Spacing.Equals(other.Spacing) && ParentSpacing.Equals(other.ParentSpacing);
            public override bool Equals(object obj) => obj is CapacityInterval other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((((int)Type * 397 ^ Error.GetHashCode()) * 397 ^ ParentError.GetHashCode()) * 397 ^ Spacing.GetHashCode()) * 397 ^ ParentSpacing.GetHashCode(); }
            }
        }

        private readonly struct CapacityParameters : IEquatable<CapacityParameters>
        {
            public readonly int Submesh, PixelError, TargetPixels;
            public CapacityParameters(int submesh, float pixelError, float target)
            { Submesh = submesh; PixelError = BitConverter.SingleToInt32Bits(pixelError); TargetPixels = BitConverter.SingleToInt32Bits(target); }
            public bool Equals(CapacityParameters other) => Submesh == other.Submesh && PixelError == other.PixelError && TargetPixels == other.TargetPixels;
            public override bool Equals(object obj) => obj is CapacityParameters other && Equals(other);
            public override int GetHashCode() { unchecked { return (Submesh * 397 ^ PixelError) * 397 ^ TargetPixels; } }
        }

        private readonly struct CapacityEvent
        {
            public readonly float Threshold;
            public readonly int Delta;
            public CapacityEvent(float threshold, int delta) { Threshold = threshold; Delta = delta; }
        }

        private sealed class CapacitySubmesh
        {
            public int FullCount;
            public bool Invalid;
            public readonly Dictionary<GroupBoundsKey, Dictionary<CapacityInterval, int>> Groups = new Dictionary<GroupBoundsKey, Dictionary<CapacityInterval, int>>();
        }

        private sealed class CapacityModel
        {
            public readonly int Revision;
            public readonly CapacitySubmesh[] Submeshes;
            public readonly Dictionary<CapacityParameters, int> Results = new Dictionary<CapacityParameters, int>();
            public CapacityModel(VirtualMeshModelAsset model)
            {
                Revision = model.Revision; Submeshes = new CapacitySubmesh[model.SubMeshCount];
                for (int i = 0; i < Submeshes.Length; i++) Submeshes[i] = new CapacitySubmesh();
                foreach (var cluster in model.Clusters)
                {
                    if (cluster.submesh >= Submeshes.Length) continue;
                    var submesh = Submeshes[cluster.submesh]; submesh.FullCount++;
                    if (!float.IsFinite(cluster.center.x) || !float.IsFinite(cluster.center.y) || !float.IsFinite(cluster.center.z) ||
                        !float.IsFinite(cluster.radius) || cluster.radius < 0 || cluster.type > 3 ||
                        cluster.type != 0 && (!float.IsFinite(cluster.error) || !float.IsFinite(cluster.parentError) ||
                        !float.IsFinite(cluster.triangleSpacing) || !float.IsFinite(cluster.parentTriangleSpacing) ||
                        cluster.error < 0 || cluster.parentError < 0 || cluster.triangleSpacing < 0 || cluster.parentTriangleSpacing < 0))
                    { submesh.Invalid = true; continue; }
                    if (submesh.Invalid) continue;
                    var key = new GroupBoundsKey(cluster);
                    if (!submesh.Groups.TryGetValue(key, out var levels)) { levels = new Dictionary<CapacityInterval, int>(); submesh.Groups.Add(key, levels); }
                    var interval = new CapacityInterval(cluster);
                    levels.TryGetValue(interval, out int count); levels[interval] = count + 1;
                }
            }
        }

        internal sealed class TransformSnapshot
        {
            private Matrix4x4 matrix;
            private Bounds localBounds;
            private bool hasMatrix, hasBounds;
            private Matrix4x4 inverse;
            private float stretch;
            private Bounds worldBounds;

            public float Determinant { get; private set; }

            public void UpdateMatrix(in Matrix4x4 current)
            {
                if (hasMatrix && matrix.Equals(current)) return;
                matrix = current;
                Determinant = current.determinant;
                hasMatrix = true;
                hasBounds = false;
            }

            public void GetGeometry(Bounds bounds, out Matrix4x4 worldToObject, out float maximumStretch, out Bounds transformedBounds)
            {
                if (!hasBounds || !localBounds.Equals(bounds))
                {
                    inverse = matrix.inverse;
                    stretch = VirtualMeshRuntime.MaximumStretch(matrix);
                    worldBounds = VirtualMeshRuntime.TransformBounds(bounds, matrix);
                    localBounds = bounds;
                    hasBounds = true;
                }
                worldToObject = inverse;
                maximumStretch = stretch;
                transformedBounds = worldBounds;
            }
        }

        internal readonly struct Active
        {
            public readonly VirtualMeshInstance Instance;
            public readonly VirtualMeshModelAsset Model;
            public readonly Material[] SourceMaterials, Templates;
            public readonly GameObject GameObject;
            public readonly int Layer;
            public readonly uint RenderingLayers;
            public readonly ulong SceneMask;
            public readonly ShadowCastingMode ShadowMode;
            public readonly bool ReceiveShadows;
            public readonly Bounds Bounds;
            internal readonly InstanceData Data;
            public readonly Vector2 ColorLod, ShadowLod;
            public readonly float WindPadding, ShadowMultiplier;
            public Active(VirtualMeshInstance instance, GameObject renderObject, MeshRenderer renderer,
                in Matrix4x4 matrix, TransformSnapshot snapshot, float windPadding, ResidentInstance resident)
            {
                Instance = instance;
                Model = instance.Model;
                SourceMaterials = resident == null ? instance.Materials : resident.SourceMaterials;
                Templates = resident == null ? instance.RenderMaterials : resident.Templates;
                GameObject = renderObject; Layer = renderObject.layer; SceneMask = renderObject.sceneCullingMask;
                RenderingLayers = renderer != null ? renderer.renderingLayerMask : 1u;
                ReceiveShadows = renderer == null || renderer.receiveShadows;
                ShadowMode = instance.GetSourceShadowMode(renderer);
                float pixelError = float.IsFinite(instance.PixelError) ? Mathf.Max(.01f, instance.PixelError) : 1;
                float targetPixels = float.IsFinite(instance.TargetTrianglePixels) ? Mathf.Max(0, instance.TargetTrianglePixels) : 2;
                snapshot.GetGeometry(instance.Model.Bounds, out var inverse, out float stretch, out var bounds);
                Data = new InstanceData { ObjectToWorld = matrix, WorldToObject = inverse,
                    Parameters = new Vector4(stretch, pixelError, snapshot.Determinant < 0 ? -1 : 1, targetPixels) };
                float multiplier = float.IsFinite(instance.ShadowLodMultiplier) ? Mathf.Max(0.25f, instance.ShadowLodMultiplier) : 1f;
                ShadowMultiplier = multiplier;
                ColorLod = new Vector2(pixelError, targetPixels);
                ShadowLod = new Vector2((float)Math.Min(float.MaxValue * .25, (double)pixelError * multiplier), targetPixels);
                bounds.Expand(windPadding * 2); Bounds = bounds; WindPadding = windPadding;
            }
        }

        internal sealed class ResidentInstance
        {
            public int Index = -1;
            public bool Ready, VisibilityDirty;
            public VirtualMeshModelAsset Model;
            public MeshRenderer SourceRenderer;
            public Material[] SourceMaterials, Templates;
            public readonly HashSet<Material> Dependencies = new HashSet<Material>();
        }

        private struct LodSelectionMemo
        {
            private VirtualMeshLodBudget.Profile profile;
            private VirtualMeshLodBudget.Selection selection;
            private int budget, units, maximumUnits, projectedPixels, pixelError, targetPixels;
            private bool budgetEnabled;

            public VirtualMeshLodBudget.Selection Evaluate(VirtualMeshLodBudget.Profile currentProfile, int currentBudget,
                float currentUnits, float currentMaximumUnits, float currentProjectedPixels, float currentPixelError,
                float currentTargetPixels, bool currentBudgetEnabled, ref int hits, ref int misses)
            {
                int unitBits = BitConverter.SingleToInt32Bits(currentUnits);
                int errorBits = BitConverter.SingleToInt32Bits(currentPixelError);
                int targetBits = BitConverter.SingleToInt32Bits(currentTargetPixels);
                // Eligibility is the only part of the evaluator that reads these two values.
                int maximumBits = currentProfile.HasEligibility ? BitConverter.SingleToInt32Bits(currentMaximumUnits) : 0;
                int projectedBits = currentProfile.HasEligibility ? BitConverter.SingleToInt32Bits(currentProjectedPixels) : 0;
                if (ReferenceEquals(profile, currentProfile) && budget == currentBudget && units == unitBits &&
                    maximumUnits == maximumBits && projectedPixels == projectedBits && pixelError == errorBits &&
                    targetPixels == targetBits && budgetEnabled == currentBudgetEnabled)
                {
                    hits++;
                    return selection;
                }

                misses++;
                selection = VirtualMeshLodBudget.EvaluateEligibleProfile(currentProfile, currentBudget, currentUnits,
                    currentMaximumUnits, currentProjectedPixels, currentPixelError, currentTargetPixels, currentBudgetEnabled);
                profile = currentProfile;
                budget = currentBudget;
                units = unitBits;
                maximumUnits = maximumBits;
                projectedPixels = projectedBits;
                pixelError = errorBits;
                targetPixels = targetBits;
                budgetEnabled = currentBudgetEnabled;
                return selection;
            }
        }

        private sealed class CameraState
        {
            public Camera Camera;
            public readonly Dictionary<BatchKey, BatchState> Batches = new Dictionary<BatchKey, BatchState>();
            public readonly VirtualMeshImpostorRuntime.CameraLists Proxies = new VirtualMeshImpostorRuntime.CameraLists();
            public Frame Frame;
            public double LastUsed;
            public GraphicsBuffer PreviousObjectToWorld;
            public GraphicsBuffer ColorLodParameters, ShadowLodParameters;
            public Vector2[] ColorLodData = Array.Empty<Vector2>(), ShadowLodData = Array.Empty<Vector2>();
            public Vector2[] UploadedColorLodData = Array.Empty<Vector2>(), UploadedShadowLodData = Array.Empty<Vector2>();
            public int UploadedColorLodCount, UploadedShadowLodCount, LodCapacity;
            public long InstanceDataUploadBytes, ModelIdUploadBytes, CameraLodDataUploadBytes, ShadowLodDataUploadBytes;
            public long RequestedBudget, EstimatedBudgetTriangles, ShadowRequestedBudget, ShadowEstimatedBudgetTriangles;
            public int ProfiledInstances, UnmetInstances, UnprofiledInstances, ShadowProfiledInstances, ShadowUnmetInstances;
            public float MaximumBudgetScale = 1, MaximumShadowBudgetScale = 1;
            public int CoverageLimitedInstances, ShadowCoverageLimitedInstances;
            public int MaterialCoverageLimitedInstances, ShadowMaterialCoverageLimitedInstances;
            public LodSelectionMemo[] ColorLodSelections = Array.Empty<LodSelectionMemo>();
            public LodSelectionMemo[] ShadowLodSelections = Array.Empty<LodSelectionMemo>();
            public int LodSelectionCacheHits, LodSelectionCacheMisses;
            public int PreviousCapacity, PreparedGeneration, CommittedGeneration, HistoryRevision, LastAtlasRevision = -1;
            public readonly Dictionary<VirtualMeshInstance, Matrix4x4> PreviousMatrices = new Dictionary<VirtualMeshInstance, Matrix4x4>();
            public readonly List<VirtualMeshInstance> PreviousOrder = new List<VirtualMeshInstance>();
            public VirtualMeshInstance[] PendingOrder = Array.Empty<VirtualMeshInstance>();
            public Matrix4x4[] PendingMatrices = Array.Empty<Matrix4x4>();
            public Matrix4x4[] PreviousData = Array.Empty<Matrix4x4>(), UploadedPreviousData = Array.Empty<Matrix4x4>();
            public int PendingCount, UploadedPreviousCount;
            public bool PendingHistoryChanged;
            public long PreviousTransformUploadBytes;
            public long PreviousBytes => (long)PreviousCapacity * 64;
            public long LodBytes => (long)LodCapacity * LodParameterStride * 2;
            public void Dispose()
            {
                foreach (var batch in Batches.Values) batch.Dispose(); Batches.Clear(); Frame = null;
                Proxies.Dispose();
                ColorLodSelections = ShadowLodSelections = Array.Empty<LodSelectionMemo>();
                PreviousObjectToWorld?.Dispose(); PreviousObjectToWorld = null; PreviousCapacity = 0;
                ColorLodParameters?.Dispose(); ShadowLodParameters?.Dispose(); ColorLodParameters = ShadowLodParameters = null; LodCapacity = 0;
                ColorLodData = ShadowLodData = Array.Empty<Vector2>();
                UploadedColorLodData = UploadedShadowLodData = Array.Empty<Vector2>();
                UploadedColorLodCount = UploadedShadowLodCount = 0;
                PreviousMatrices.Clear(); PreviousOrder.Clear();
                PendingOrder = Array.Empty<VirtualMeshInstance>();
                PendingMatrices = PreviousData = UploadedPreviousData = Array.Empty<Matrix4x4>();
            }
        }

        private static readonly ProfilerMarker validateMarker = new ProfilerMarker("VirtualMesh.Graph.Validate");
        private static readonly ProfilerMarker validateActiveStateMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.ActiveState");
        private static readonly ProfilerMarker validateTransformMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.Transform");
        private static readonly ProfilerMarker validateBuildActiveMarker = new ProfilerMarker("VirtualMesh.Graph.Validate.BuildActive");
        private static readonly ProfilerMarker sourceVisibilityMarker = new ProfilerMarker("VirtualMesh.Graph.SourceVisibility");
        private static readonly VirtualMeshRuntime.MaterialValidationContext materialValidation = new VirtualMeshRuntime.MaterialValidationContext();
        private static readonly VirtualMeshLeafMaterialContract.EvaluationContext leafMaterialValidation = new VirtualMeshLeafMaterialContract.EvaluationContext();
        private static readonly ProfilerMarker prepareMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare");
        private static readonly ProfilerMarker prepareInstanceDataMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.InstanceData");
        private static readonly ProfilerMarker prepareCameraMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.Camera");
        private static readonly ProfilerMarker prepareVisibilityMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.Visibility");
        private static readonly ProfilerMarker prepareLodMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.LOD");
        private static readonly ProfilerMarker prepareAppendMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.AppendWork");
        private static readonly ProfilerMarker prepareCapacityMarker = new ProfilerMarker("VirtualMesh.Graph.Prepare.AppendWork.Capacity");
        private static readonly ProfilerMarker prepareFrameMarker = new ProfilerMarker("VirtualMesh.Graph.PrepareFrame");
        private static readonly ProfilerMarker prepareFrameMotionMarker = new ProfilerMarker("VirtualMesh.Graph.PrepareFrame.MotionHistory");
        private static readonly ProfilerMarker prepareFrameBatchMarker = new ProfilerMarker("VirtualMesh.Graph.PrepareFrame.Batches");
        private static readonly ProfilerMarker uploadMarker = new ProfilerMarker("VirtualMesh.Graph.Upload");
        private static readonly ProfilerMarker workItemsMarker = new ProfilerMarker("VirtualMesh.Graph.WorkItems");
        private static readonly List<WorkItem> workItemScratch = new List<WorkItem>();
        private static readonly ProfilerMarker materialMarker = new ProfilerMarker("VirtualMesh.Graph.MaterialSync");
        private static readonly Dictionary<MaterialKey, SharedMaterial> materials = new Dictionary<MaterialKey, SharedMaterial>();
        private static readonly Dictionary<VirtualMeshModelAsset, Entry> entries = new Dictionary<VirtualMeshModelAsset, Entry>();
        private static readonly Dictionary<VirtualMeshModelAsset, CapacityModel> capacityModels = new Dictionary<VirtualMeshModelAsset, CapacityModel>();
        private static readonly Dictionary<int, CameraState> cameras = new Dictionary<int, CameraState>();
        private static readonly List<Active> active = new List<Active>();
        private static readonly Dictionary<VirtualMeshInstance, TransformSnapshot> transforms = new Dictionary<VirtualMeshInstance, TransformSnapshot>();
        private static readonly Dictionary<VirtualMeshInstance, ResidentInstance> residentInstances = new();
        private static readonly HashSet<VirtualMeshInstance> polledInstances = new();
        private static readonly HashSet<VirtualMeshInstance> changedInstances = new();
        private static readonly Dictionary<VirtualMeshModelAsset, int> residentModels = new();
        private static readonly Dictionary<VirtualMeshModelAsset, HashSet<VirtualMeshInstance>> modelUsers = new();
        private static readonly Dictionary<Material, HashSet<VirtualMeshInstance>> materialUsers = new();
        private static readonly Dictionary<VirtualMeshInstance, string> residentErrors = new();
        private static bool needsRegistrationSnapshot = true;
        // Resident slots keep their IDs when edited in place. The polled tail
        // is rebuilt independently to preserve immediate legacy mutation behavior.
        private static int residentActiveCount, readyResidentCount;

        internal static void ForgetTransform(VirtualMeshInstance instance) => transforms.Remove(instance);

        internal static void InvalidateSharedMaterial(Material material)
        {
            if (material != null && materialUsers.TryGetValue(material, out var users))
                foreach (var instance in users) instance.InvalidateRenderState();
        }

        internal static void InvalidateModel(VirtualMeshModelAsset model)
        {
            if (modelUsers.TryGetValue(model, out var users))
                foreach (var instance in users) instance.InvalidateRenderState();
        }

#if UNITY_EDITOR
        internal static bool HasResidentInstances => residentInstances.Count > 0;

        internal static void InvalidateEditorHierarchy(GameObject root)
        {
            if (root == null) return;
            Transform changed = root.transform;
            foreach (var pair in residentInstances)
            {
                var instance = pair.Key;
                if (instance == null) continue;
                var own = instance.transform;
                var source = instance.RenderTransform;
                var previousSource = pair.Value.SourceRenderer;
                bool sourceDestroyed = !ReferenceEquals(previousSource, null) && previousSource == null;
                if (sourceDestroyed || own == changed || own.IsChildOf(changed) || source == changed || source.IsChildOf(changed))
                    instance.InvalidateRenderState();
            }
        }

        internal static void InvalidateAllResidents()
        {
            foreach (var instance in residentInstances.Keys) instance.InvalidateRenderState();
        }
#endif

        private static void AddUser<T>(Dictionary<T, HashSet<VirtualMeshInstance>> index, T dependency,
            VirtualMeshInstance instance) where T : UnityEngine.Object
        {
            if (!index.TryGetValue(dependency, out var users))
                index.Add(dependency, users = new HashSet<VirtualMeshInstance>());
            users.Add(instance);
        }

        private static void RemoveUser<T>(Dictionary<T, HashSet<VirtualMeshInstance>> index, T dependency,
            VirtualMeshInstance instance) where T : UnityEngine.Object
        {
            var users = index[dependency];
            users.Remove(instance);
            if (users.Count == 0) index.Remove(dependency);
        }

        private static void RemoveDependencies(VirtualMeshInstance instance, ResidentInstance resident)
        {
            if (!ReferenceEquals(resident.Model, null)) RemoveUser(modelUsers, resident.Model, instance);
            foreach (var material in resident.Dependencies) RemoveUser(materialUsers, material, instance);
            resident.Dependencies.Clear();
        }

        private static void AddMaterialDependencies(VirtualMeshInstance instance, ResidentInstance resident, Material[] slots)
        {
            if (slots == null) return;
            foreach (var material in slots)
                if (material != null && resident.Dependencies.Add(material))
                    AddUser(materialUsers, material, instance);
        }

        private static void UpdateResidentModelCount(VirtualMeshModelAsset model, int change)
        {
            residentModels.TryGetValue(model, out int count);
            count += change;
            if (count == 0) residentModels.Remove(model);
            else residentModels[model] = count;
        }

        private static void RemoveResidentActive(ResidentInstance resident)
        {
            int index = resident.Index;
            if (index < 0) return;
            UpdateResidentModelCount(active[index].Model, -1);
            int last = --residentActiveCount;
            if (index != last)
            {
                active[index] = active[last];
                residentInstances[active[index].Instance].Index = index;
            }
            active.RemoveAt(last);
            resident.Index = -1;
            if (resident.Ready) readyResidentCount--;
            resident.Ready = false;
        }

        private static void UpdateActiveInstances(HashSet<VirtualMeshInstance> registered, bool isPlaying)
        {
            using var scope = validateMarker.Auto();
            if (active.Count > residentActiveCount)
                active.RemoveRange(residentActiveCount, active.Count - residentActiveCount);
            if (needsRegistrationSnapshot)
            {
                foreach (var instance in registered) changedInstances.Add(instance);
                needsRegistrationSnapshot = false;
            }
            while (VirtualMeshInstance.TryDequeueRenderStateChange(out var changed))
                changedInstances.Add(changed);

            Scene lastLoadedScene = default;
            bool hasLoadedScene = false;
            foreach (var instance in changedInstances)
            {
                bool registeredNow = instance != null && registered.Contains(instance);
                if (!registeredNow || !instance.UseChangeDrivenValidation)
                {
                    if (residentInstances.TryGetValue(instance, out var old))
                    {
                        RemoveResidentActive(old);
                        RemoveDependencies(instance, old);
                        residentInstances.Remove(instance);
                        residentErrors.Remove(instance);
                    }
                    if (registeredNow) polledInstances.Add(instance);
                    else polledInstances.Remove(instance);
                    continue;
                }

                polledInstances.Remove(instance);
                if (!residentInstances.TryGetValue(instance, out var resident))
                    residentInstances.Add(instance, resident = new ResidentInstance());
                resident.VisibilityDirty = true;
                RemoveDependencies(instance, resident);
                resident.Model = instance.Model != null ? instance.Model : null;
                resident.SourceRenderer = instance.SourceRenderer;
                if (resident.Model != null) AddUser(modelUsers, resident.Model, instance);
                resident.SourceMaterials = instance.Materials == null ? null : (Material[])instance.Materials.Clone();
                resident.Templates = instance.RenderMaterials == null ? null : (Material[])instance.RenderMaterials.Clone();
                AddMaterialDependencies(instance, resident, resident.SourceMaterials);
                AddMaterialDependencies(instance, resident, resident.Templates);

                statistics.ChangeDrivenValidatedInstances++;
                if (TryBuildActive(instance, resident, isPlaying, ref lastLoadedScene, ref hasLoadedScene, out var item, out string error))
                {
                    if (resident.Index < 0)
                    {
                        resident.Index = residentActiveCount++;
                        active.Add(item);
                        UpdateResidentModelCount(item.Model, 1);
                    }
                    else
                    {
                        var previous = active[resident.Index].Model;
                        if (previous != item.Model)
                        {
                            UpdateResidentModelCount(previous, -1);
                            UpdateResidentModelCount(item.Model, 1);
                        }
                        active[resident.Index] = item;
                    }
                    residentErrors.Remove(instance);
                }
                else
                {
                    RemoveResidentActive(resident);
                    if (error != null) residentErrors[instance] = error;
                    else residentErrors.Remove(instance);
                }
            }

            modelSet.Clear();
            foreach (var model in residentModels.Keys) modelSet.Add(model);
            foreach (var error in residentErrors.Values)
            {
                statistics.LastError = error;
                break;
            }
            foreach (var instance in polledInstances)
            {
                if (instance == null || !registered.Contains(instance)) continue;
                statistics.PolledInstances++;
                if (TryBuildActive(instance, null, isPlaying, ref lastLoadedScene, ref hasLoadedScene, out var item, out string error))
                {
                    active.Add(item);
                    modelSet.Add(item.Model);
                }
                else if (error != null) statistics.LastError = error;
            }
        }

        private static bool UpdateSourceVisibility(Active item)
        {
            var instance = item.Instance;
            bool ready = entries.TryGetValue(item.Model, out var entry) && entry.Ready;
            if (ready)
            {
                instance.SetVirtualRendering(true);
                instance.SetDiagnostic(null);
            }
            else
            {
                instance.SetResidencyFallback();
                instance.SetDiagnostic(rejectedModels.Contains(item.Model) ?
                    "Virtual Mesh graph buffer budget exceeded. Source color remains visible without high-poly shadows." :
                    "Model atlas upload is queued. Source color is temporarily retained without high-poly shadows.", false);
            }
            return ready;
        }

        private static void UpdateResidentSourceVisibility(ResidentInstance resident)
        {
            if (resident.Index < 0) return;
            bool ready = UpdateSourceVisibility(active[resident.Index]);
            if (ready != resident.Ready) readyResidentCount += ready ? 1 : -1;
            resident.Ready = ready;
            resident.VisibilityDirty = false;
        }

        private static bool TryBuildActive(VirtualMeshInstance instance, ResidentInstance resident, bool isPlaying,
            ref Scene lastLoadedScene, ref bool hasLoadedScene, out Active item, out string error)
        {
            item = default;
            error = null;
            GameObject sourceObject;
            MeshRenderer renderer;
            bool hasRenderer;
            using (ProfileDetail(validateActiveStateMarker))
            {
                sourceObject = instance.gameObject;
                var scene = sourceObject.scene;
                if (!hasLoadedScene || scene.handle != lastLoadedScene.handle)
                {
                    hasLoadedScene = scene.IsValid() && scene.isLoaded;
                    lastLoadedScene = scene;
                }
                renderer = instance.SourceRenderer;
                hasRenderer = renderer != null;
                if (!instance.isActiveAndEnabled || !hasLoadedScene || !isPlaying && !instance.PreviewInEditMode ||
                    hasRenderer && (!renderer.enabled || instance.GetSourceWasHidden(renderer)))
                {
                    instance.SetVirtualRendering(false);
                    return false;
                }
            }
            GameObject renderObject;
            Matrix4x4 matrix;
            TransformSnapshot snapshot;
            using (ProfileDetail(validateTransformMarker))
            {
                var renderTransform = hasRenderer ? renderer.transform : instance.transform;
                renderObject = hasRenderer ? renderTransform.gameObject : sourceObject;
                matrix = renderTransform.localToWorldMatrix;
                if (!transforms.TryGetValue(instance, out snapshot))
                    transforms.Add(instance, snapshot = new TransformSnapshot());
                snapshot.UpdateMatrix(in matrix);
            }
            error = VirtualMeshRuntime.ValidateForGraph(instance, renderer, snapshot.Determinant, materialValidation, out float windPadding);
            if (error != null)
            {
                instance.SetDiagnostic(error);
                instance.SetVirtualRendering(false);
                // A diagnostic listener can synchronously change scene state.
                hasLoadedScene = false;
                return false;
            }
            using (ProfileDetail(validateBuildActiveMarker))
                item = new Active(instance, renderObject, renderer, in matrix, snapshot, windPadding, resident);
            return true;
        }
        private static readonly List<VirtualMeshModelAsset> plannedModels = new List<VirtualMeshModelAsset>();
        private static readonly List<VirtualMeshModelAsset> uploadedModels = new List<VirtualMeshModelAsset>(2);
        private static readonly List<BatchKey> staleBatches = new List<BatchKey>();
        private static readonly List<int> staleCameras = new List<int>();
        private static readonly HashSet<VirtualMeshModelAsset> modelSet = new HashSet<VirtualMeshModelAsset>();
        private static readonly HashSet<VirtualMeshModelAsset> rejectedModels = new HashSet<VirtualMeshModelAsset>();
        private static readonly uint[] zeroCounters = new uint[VisibilityCounterCount];
        private static readonly SphericalHarmonicsL2[] ambientProbe = new SphericalHarmonicsL2[1];
        private static Atlas atlas;
        private static VirtualMeshIndexedVisibility indexedVisibility;
        private static VirtualMeshIndexedVisibility indexedVisibilityFailure;
        internal static VirtualMeshIndexedVisibility IndexedVisibilityPreparation => indexedVisibility ?? indexedVisibilityFailure;
        private static int indexedVisibilityFailedRevision = -1;
        private const int AtlasUploadSliceBytes = 16 * 1024 * 1024;
        private const int ExpandedGpuUploadSliceBytes = 64 * 1024 * 1024;
        // Experimental until cold-start A/B and exact image validation pass.
        internal static bool UseExpandedGpuUploadWindow;
        private static long gpuUploadWindowTicks, gpuUploadActiveStart;
        private static readonly long gpuUploadSoftBudgetTicks = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 125);
        private static bool ExpandedGpuUploadActive => UseExpandedGpuUploadWindow && atlas?.GpuVertexUpload != null;
        private static int AtlasUploadWindowLimit => ExpandedGpuUploadActive ? ExpandedGpuUploadSliceBytes : AtlasUploadSliceBytes;
        private static bool GpuUploadTimeRemaining => !ExpandedGpuUploadActive ||
            gpuUploadWindowTicks + (gpuUploadActiveStart == 0 ? 0 : System.Diagnostics.Stopwatch.GetTimestamp() - gpuUploadActiveStart) < gpuUploadSoftBudgetTicks;
        private const int AtlasUploadCallBytes = 4 * 1024 * 1024;
        private const long AtlasHostReserveBytes = 512L * 1024 * 1024;
        private const double AtlasUploadWindowSeconds = 1.0 / 60.0;
        private static double atlasUploadWindowEnds;
        private static int atlasUploadWindowBytes;
        private static long splitUploadWindowTicks;
        private static readonly long splitUploadSoftBudgetTicks = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 500);
        internal static long AtlasUploadProgressBytes { get; private set; }
        private static GraphicsBuffer instanceModelIds;
        private static uint[] modelIds = Array.Empty<uint>(), uploadedModelIds = Array.Empty<uint>();
        private static GraphicsBuffer instanceTransforms;
        private static InstanceData[] instanceData = Array.Empty<InstanceData>(), uploadedInstanceData = Array.Empty<InstanceData>();
        private static int uploadedModelIdCount, uploadedInstanceCount;
        private static int instanceCapacity, atlasRevision, generation, historyRevision;
        private static bool completionSubscribed;
        private static long planningBudget;
        // The effective policy belongs to the shared runtime, never a camera.
        // This is intentionally a property: old field-reflection experiments must
        // migrate to Capture/RestoreVertexStreamOverrides instead of pinning a bool.
        internal static bool UseGpuVertexTranspose => VirtualMeshRuntime.UseGpuVertexTranspose;
        private static bool planningGpuVertexTranspose, gpuVertexTransposeFailed;
        private static string gpuVertexTransposeFailure;
        private static readonly List<Entry> gpuCompletedUploads = new List<Entry>();
        internal static object VertexUploadDiagnostics => new {
            RequestedGpuTranspose = UseGpuVertexTranspose,
            ExpandedUploadRequested = UseExpandedGpuUploadWindow,
            ExpandedUploadActive = ExpandedGpuUploadActive,
            UploadWindowLimitBytes = AtlasUploadWindowLimit,
            UploadWindowUsedBytes = atlasUploadWindowBytes,
            UploadWindowCpuMilliseconds = gpuUploadWindowTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            ActualGpuTranspose = atlas?.GpuVertexUpload != null,
            ScratchGpuBytes = atlas?.GpuVertexUpload != null ? VirtualMeshVertexGpuScratch.Bytes : 0,
            RetiringGpuBytes = VirtualMeshVertexGpuRetirement.Bytes,
            SubmittedVertexBytes = atlas?.GpuVertexUpload?.SubmittedBytes ?? 0,
            RecordedChunks = atlas?.GpuVertexUpload?.SubmittedChunks ?? 0,
            PendingCommandPayloadBytes = atlas?.GpuVertexUpload?.PendingBytes ?? 0,
            InFlightCommandPayloadBytes = atlas?.GpuVertexUpload?.InFlightBytes ?? 0,
            RecordMilliseconds = (atlas?.GpuVertexUpload?.RecordTicks ?? 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            Failure = gpuVertexTransposeFailure };
        private static bool PlanGpuVertexTranspose => planningSplitVertexStreams && planningGpuVertexTranspose &&
            !splitVertexStreamsFailed && !gpuVertexTransposeFailed;
        private static bool planningSplitVertexStreams, splitVertexStreamsFailed;
        private static string splitVertexStreamsFailure;
        private static double nextReadmissionCheck;
        private static ComputeShader cullShader;
        private static LocalKeyword cullFineHiZKeyword;
        private static int cullKernel, shadowKernel;
        private static VirtualMeshRuntime.RuntimeStatistics statistics;

        // Subscopes can generate thousands of samples per frame. Leave this
        // off during ordinary rendering and enable it only for a profile capture.
        internal static bool ProfileDetails = false;
        internal static ProfilerMarker.AutoScope ProfileDetail(ProfilerMarker marker) =>
            ProfileDetails ? marker.Auto() : default;

        private static GraphicsBuffer CreateDrawArguments()
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                GraphicsBuffer.IndirectDrawArgs.size / sizeof(uint), sizeof(uint));
            buffer.SetData(new uint[] { 192, 0, 0, 0 });
            return buffer;
        }

        /// <summary>Borrowed shared scratch: every index build must precede its consuming draw.</summary>
        internal static VirtualMeshIndexedVisibility TryGetIndexedVisibilityResources(Frame frame, int maxScratchMiB = 128)
        {
            if (frame == null || atlas == null || !ReferenceEquals(frame.Atlas, atlas) ||
                frame.Generation != generation || !frame.UsesRenderGraph || maxScratchMiB <= 0)
                return null;

            // Do not replace buffers while another camera/pass in this context may reference them.
            // The context preparation below releases optional storage before base resources grow.
            if (indexedVisibility != null)
            {
                if (indexedVisibility.AtlasRevision != atlas.Revision) return null;
                indexedVisibility.PumpCanonicalUploads(generation);
                if (indexedVisibility.Failure != null)
                {
                    string failure = indexedVisibility.Failure;
                    indexedVisibilityFailure = indexedVisibility;
                    ReleaseIndexedVisibility();
                    indexedVisibilityFailedRevision = atlas.Revision;
                    statistics.GpuBufferBytes = ResidentBaseBufferBytes();
                    VirtualMeshRuntime.PublishStatistics(statistics);
                    Debug.LogWarning("Virtual Mesh indexed visibility preparation failed; using the original draw path. " + failure);
                    return null;
                }
                return indexedVisibility.Ready &&
                    (long)indexedVisibility.CapacityClusters * VirtualMeshIndexedVisibility.IndicesPerCluster * sizeof(uint) <=
                    (long)Math.Min(maxScratchMiB, 256) * 1024 * 1024 ? indexedVisibility : null;
            }
            if (indexedVisibilityFailedRevision == atlas.Revision) return null;

            long mapWords = (long)atlas.Clusters.count * VirtualMeshIndexedVisibility.CanonicalWordsPerCluster;
            long mapBytes = mapWords * sizeof(uint);
            long bufferLimit = SystemInfo.maxGraphicsBufferSize;
            if (mapWords > int.MaxValue || mapBytes > bufferLimit) return null;
            long baseBytes = Math.Max(ResidentBaseBufferBytes(), EstimateRequiredBytes());
            long available = statistics.GpuBudgetBytes - baseBytes - mapBytes - VirtualMeshIndexedVisibility.AuxiliaryBytes;
            long scratchLimit = Math.Min(Math.Min((long)Math.Min(maxScratchMiB, 256) * 1024 * 1024, bufferLimit), available);
            int requestedClusters = 0;
            foreach (var state in cameras.Values)
            {
                if (state.Frame == null || state.Frame.Generation != generation) continue;
                foreach (var batch in state.Frame.Colors) requestedClusters = Math.Max(requestedClusters, batch.Capacity);
            }
            int capacity = (int)Math.Min(requestedClusters,
                Math.Max(0, scratchLimit / (VirtualMeshIndexedVisibility.IndicesPerCluster * sizeof(uint))));
            if (capacity == 0 || !VirtualMeshHostMemory.HasUploadHeadroom(AtlasHostReserveBytes)) return null;

            VirtualMeshIndexedVisibility resources = null;
            try
            {
                resources = VirtualMeshIndexedVisibility.Create(atlas.Clusters.count, capacity, atlas.Revision);
                var sources = new List<VirtualMeshIndexedVisibility.CanonicalSource>(entries.Count);
                foreach (var entry in entries.Values)
                    sources.Add(new VirtualMeshIndexedVisibility.CanonicalSource(entry.Model.Indices, entry.Model.Clusters,
                        entry.SubmeshStarts, checked((int)entry.Data.ClusterOffset)));
                resources.BeginCanonicalPreparation(sources);
                indexedVisibility = resources;
                indexedVisibilityFailure = null;
                statistics.GpuBufferBytes = ResidentBaseBufferBytes() + resources.Bytes;
                VirtualMeshRuntime.PublishStatistics(statistics);
                // Preparation and bounded uploads proceed on later contexts; all current draws use fallback.
                return null;
            }
            catch (Exception exception)
            {
                resources?.Dispose();
                indexedVisibilityFailure = resources;
                indexedVisibilityFailedRevision = atlas.Revision;
                // This acceleration is optional; failure must never change model residency or LOD.
                Debug.LogWarning("Virtual Mesh indexed visibility is unavailable for this atlas; using the original draw path. " + exception.Message);
                return null;
            }
        }

        private static long ResidentBaseBufferBytes()
        {
            long bytes = (atlas?.Bytes ?? 0) + InstanceBufferBytes() + VirtualMeshVertexBindings.Bytes + VirtualMeshVertexGpuRetirement.Bytes;
            foreach (var state in cameras.Values)
            {
                bytes += state.PreviousBytes + state.LodBytes + state.Proxies.Bytes;
                foreach (var batch in state.Batches.Values) bytes += batch.Bytes;
            }
            return bytes;
        }

        private static void ReleaseIndexedVisibility()
        {
            indexedVisibility?.Dispose();
            indexedVisibility = null;
        }

        private static long RequiredBufferBytes(bool shadow, int capacity, int workCount) =>
            (long)Math.Max(1, workCount) * WorkItem.Stride + (long)Math.Max(1, capacity) * 8 * (shadow ? 1 : 2) +
            GraphicsBuffer.IndirectDrawArgs.size * (shadow ? 1 : 3) + (shadow ? 0 : 12) + VisibilityCounterCount * sizeof(uint);

        /// <summary>
        /// Conservative simultaneous LOD selection bound. Clusters with bit-identical group
        /// bounds evaluate the same projected units for one instance, even if separate authored
        /// groups happen to collide. Sweep their selection intervals rather than assuming a
        /// particular hierarchy depth or group identifier. Frustum, cone and HiZ only remove.
        /// </summary>
        internal static int GetSelectedCapacity(VirtualMeshModelAsset model, int submeshIndex, float pixelError, float targetPixels)
        {
            if (!capacityModels.TryGetValue(model, out var cached) || cached.Revision != model.Revision)
            { cached = new CapacityModel(model); capacityModels[model] = cached; }
            if (submeshIndex < 0 || submeshIndex >= cached.Submeshes.Length) return model.Clusters.Length;
            var submesh = cached.Submeshes[submeshIndex];
            if (submesh.Invalid || !float.IsFinite(pixelError) || pixelError <= 0 || !float.IsFinite(targetPixels)) return submesh.FullCount;
            var parameters = new CapacityParameters(submeshIndex, pixelError, targetPixels);
            if (cached.Results.TryGetValue(parameters, out int result)) return result;
            long capacity = 0;
            var events = new List<CapacityEvent>();
            foreach (var levels in submesh.Groups.Values)
            {
                events.Clear();
                int initial = 0;
                foreach (var pair in levels)
                {
                    var interval = pair.Key; int count = pair.Value;
                    if (interval.Type == 0) { initial += count; continue; }
                    // Projected units are nonnegative. These exact zero/identical transitions
                    // cannot select a leaf/intermediate cluster even with GPU float rounding.
                    if (interval.Type == 1 && interval.ParentError == 0) continue;
                    if (interval.Type == 2 && interval.Error == interval.ParentError &&
                        (interval.Error == 0 || interval.Spacing == interval.ParentSpacing || targetPixels <= 0 ||
                        interval.Spacing <= 0 && interval.ParentSpacing <= 0)) continue;
                    float start = interval.Type == 1 ? float.NegativeInfinity :
                        VirtualMeshModelCluster.TransitionThreshold(interval.Error, interval.Spacing, pixelError, targetPixels);
                    float end = interval.Type == 3 ? float.PositiveInfinity :
                        VirtualMeshModelCluster.TransitionThreshold(interval.ParentError, interval.ParentSpacing, pixelError, targetPixels);
                    if (float.IsNaN(start) || float.IsNaN(end) || start > end ||
                        float.IsPositiveInfinity(start) || float.IsNegativeInfinity(end)) return submesh.FullCount;
                    // Expand several adjacent representable floats for CPU/GPU threshold
                    // rounding and conservatively count both endpoints at a transition.
                    start = ExpandThreshold(start, false); end = ExpandThreshold(end, true);
                    if (float.IsNegativeInfinity(start)) initial += count; else events.Add(new CapacityEvent(start, count));
                    if (!float.IsPositiveInfinity(end)) events.Add(new CapacityEvent(end, -count));
                }
                events.Sort((a, b) =>
                {
                    int compare = a.Threshold.CompareTo(b.Threshold);
                    return compare != 0 ? compare : b.Delta.CompareTo(a.Delta); // starts before ends
                });
                int current = initial, maximum = initial;
                foreach (var item in events) { current += item.Delta; maximum = Math.Max(maximum, current); }
                capacity += maximum;
            }
            result = (int)Math.Min(submesh.FullCount, capacity);
            // Runtime quality sliders can produce unlimited distinct floats; retain bounded
            // parameter results while keeping the expensive group/level index reusable.
            if (cached.Results.Count >= 256) cached.Results.Clear();
            cached.Results.Add(parameters, result);
            return result;
        }

        private static float ExpandThreshold(float value, bool upward)
        {
            if (float.IsInfinity(value)) return value;
            for (int i = 0; i < 8; i++)
            {
                if (value == 0) value = upward ? float.Epsilon : -float.Epsilon;
                else
                {
                    int bits = BitConverter.SingleToInt32Bits(value);
                    bits += (value > 0) == upward ? 1 : -1;
                    value = BitConverter.Int32BitsToSingle(bits);
                    if (float.IsInfinity(value)) break;
                }
            }
            return value;
        }

        internal static bool IsRequested(List<Camera> cameraList)
        {
            if (CameraUsesGraph == null) return false;
            foreach (var camera in cameraList) if (camera != null && camera.stereoEnabled) return false;
            foreach (var camera in cameraList) if (camera != null && CameraUsesGraph(camera)) return true;
            // A reflection/preview-only context must keep using the existing atlas when a
            // graph-enabled camera is still alive; it must not allocate legacy geometry too.
            foreach (var state in cameras.Values) if (state.Camera != null && CameraUsesGraph(state.Camera)) return true;
            return false;
        }

        internal static bool TryGetFrame(Camera camera, out Frame frame)
        {
            frame = null;
            if (camera == null || !cameras.TryGetValue(camera.GetInstanceID(), out var state)) return false;
            frame = state.Frame;
            return frame != null && frame.Generation == generation && frame.UsesRenderGraph;
        }

        internal static bool TryGetCameraStatistics(Camera camera, out CameraStatistics result)
        {
            result = default;
            if (camera == null || !cameras.TryGetValue(camera.GetInstanceID(), out var state) || state.Frame == null) return false;
            var frame = state.Frame;
            result.CameraName = camera.name; result.Generation = frame.Generation;
            result.InstanceDataUploadBytes = state.InstanceDataUploadBytes;
            result.CameraLodDataUploadBytes = state.CameraLodDataUploadBytes;
            result.ShadowLodDataUploadBytes = state.ShadowLodDataUploadBytes;
            result.ModelIdUploadBytes = state.ModelIdUploadBytes;
            result.PreviousTransformUploadBytes = state.PreviousTransformUploadBytes;
            result.RequestedBudget = state.RequestedBudget; result.EstimatedBudgetTriangles = state.EstimatedBudgetTriangles;
            result.ShadowRequestedBudget = state.ShadowRequestedBudget; result.ShadowEstimatedBudgetTriangles = state.ShadowEstimatedBudgetTriangles;
            result.ProfiledInstances = state.ProfiledInstances; result.UnmetInstances = state.UnmetInstances;
            result.UnprofiledInstances = state.UnprofiledInstances; result.ShadowProfiledInstances = state.ShadowProfiledInstances;
            result.ShadowUnmetInstances = state.ShadowUnmetInstances;
            result.MaximumBudgetScale = state.MaximumBudgetScale; result.MaximumShadowBudgetScale = state.MaximumShadowBudgetScale;
            result.CoverageLimitedInstances = state.CoverageLimitedInstances;
            result.ShadowCoverageLimitedInstances = state.ShadowCoverageLimitedInstances;
            result.MaterialCoverageLimitedInstances = state.MaterialCoverageLimitedInstances;
            result.ShadowMaterialCoverageLimitedInstances = state.ShadowMaterialCoverageLimitedInstances;
            result.LodSelectionCacheHits = state.LodSelectionCacheHits;
            result.LodSelectionCacheMisses = state.LodSelectionCacheMisses;
            result.ProxyColorInstances = state.Proxies.ColorInstances; result.ProxyShadowInstances = state.Proxies.ShadowInstances;
            result.ProxyUnavailableInstances = state.Proxies.UnavailableInstances;
            result.ProxyTextureBytes = state.Proxies.TextureBytes;
            result.ProxyColorTriangles = (long)result.ProxyColorInstances * 2; result.ProxyShadowTriangles = (long)result.ProxyShadowInstances * 2;
            foreach (var proxy in frame.ProxyColors) result.ProxyVisibleInstances += proxy.VisibleCount;
            result.ColorBatches = frame.Colors.Length; result.ShadowBatches = frame.Shadows.Length;
            result.ExpectedSamples = result.ColorBatches + result.ShadowBatches;
            result.OldestSampleGeneration = int.MaxValue; result.NewestSampleGeneration = -1;
            AccumulateStatistics(frame.Colors, false, ref result);
            AccumulateStatistics(frame.Shadows, true, ref result);
            if (result.CompletedSamples == 0) result.OldestSampleGeneration = -1;
            result.SampleGeneration = result.CompletedSamples == result.ExpectedSamples &&
                result.OldestSampleGeneration == result.NewestSampleGeneration ? result.NewestSampleGeneration : -1;
            result.CompleteSample = result.CompletedSamples == result.ExpectedSamples && result.PendingSamples == 0;
            return true;
        }

        private static void AccumulateStatistics(Batch[] batches, bool shadow, ref CameraStatistics result)
        {
            foreach (var batch in batches)
            {
                var owner = batch.Owner;
                if (owner.ReadbackPending) result.PendingSamples++;
                if (owner.LastSampleGeneration < 0) continue;
                result.CompletedSamples++;
                result.OldestSampleGeneration = Math.Min(result.OldestSampleGeneration, owner.LastSampleGeneration);
                result.NewestSampleGeneration = Math.Max(result.NewestSampleGeneration, owner.LastSampleGeneration);
                if (shadow) result.Shadow.Add(owner.LastCounters); else result.Color.Add(owner.LastCounters);
            }
        }

        internal static void Render(ScriptableRenderContext context, List<Camera> cameraList,
            HashSet<VirtualMeshInstance> registeredInstances, int contextGeneration)
        {
            generation = contextGeneration;
            VirtualMeshVertexGpuRetirement.Pump();
            materialValidation.Clear();
            leafMaterialValidation.Clear();
            VirtualMeshImpostorRuntime.BeginContext(generation);
            statistics = new VirtualMeshRuntime.RuntimeStatistics { GpuBudgetBytes = VirtualMeshRuntime.GetGpuBudgetBytes(),
                RequestedSplitVertexStreams = VirtualMeshRuntime.UseSplitVertexStreams, Path = "Render Graph geometry atlas" };
            try
            {
                if (!completionSubscribed)
                {
                    RenderPipelineManager.endCameraRendering += EndCamera;
                    RenderPipelineManager.endContextRendering += EndContext;
                    completionSubscribed = true;
                }
                if (cullShader == null)
                {
                    var resource = Resources.Load<ComputeShader>("VirtualMeshModelGraphCull");
                    if (resource == null) throw new InvalidOperationException("Virtual Mesh Render Graph compute shader is unavailable.");
                    cullShader = UnityEngine.Object.Instantiate(resource); cullShader.hideFlags = HideFlags.HideAndDontSave;
                    cullFineHiZKeyword = new LocalKeyword(cullShader, "VM_HIZ_FINE_4X4");
                    cullKernel = cullShader.FindKernel("Select"); shadowKernel = cullShader.FindKernel("Shadow");
                }
                UpdateActiveInstances(registeredInstances, Application.isPlaying);
                VirtualMeshRuntime.CollectShadowLights();
                // Apply layout requests before recording any camera in this context.
                // The atlas and its frames retain their actual layout thereafter.
                bool transposeRequestChanged = planningGpuVertexTranspose != UseGpuVertexTranspose;
                if (transposeRequestChanged)
                {
                    planningGpuVertexTranspose = UseGpuVertexTranspose;
                    gpuVertexTransposeFailed = false; gpuVertexTransposeFailure = null;
                }
                bool vertexLayoutChanged = planningSplitVertexStreams != VirtualMeshRuntime.UseSplitVertexStreams ||
                    (VirtualMeshRuntime.UseSplitVertexStreams && transposeRequestChanged);
                if (vertexLayoutChanged)
                {
                    planningSplitVertexStreams = VirtualMeshRuntime.UseSplitVertexStreams;
                    splitVertexStreamsFailed = false; splitVertexStreamsFailure = null;
                }
                bool layoutChanged = vertexLayoutChanged || planningBudget != statistics.GpuBudgetBytes || !LayoutMatches();
                if (layoutChanged)
                {
                    ReleaseAtlas(); rejectedModels.Clear(); plannedModels.Clear();
                    planningBudget = statistics.GpuBudgetBytes;
#if UNITY_EDITOR
                    if (!TryRestorePreparedGeometry())
#endif
                    {
                        plannedModels.AddRange(modelSet);
                        plannedModels.Sort((a, b) => ModelDistance(a, cameraList).CompareTo(ModelDistance(b, cameraList)));
                        BuildEntries();
                    }
                }
                PrepareCameras(cameraList, false);
                if (!layoutChanged && rejectedModels.Count > 0 && PendingAtlasUploadCount() == 0 &&
                    Time.realtimeSinceStartupAsDouble >= nextReadmissionCheck)
                {
                    nextReadmissionCheck = Time.realtimeSinceStartupAsDouble + 2;
                    if (TryReadmitModels(cameraList)) layoutChanged = true;
                }
                long required = EstimateRequiredBytes();
                // Optional transfer scratch must not evict otherwise admitted geometry.
                if (required > statistics.GpuBudgetBytes && PlanGpuVertexTranspose)
                {
                    gpuVertexTransposeFailed = true;
                    gpuVertexTransposeFailure = "GPU transpose scratch exceeds the geometry budget; using CPU split uploads.";
                    if (atlas?.GpuVertexUpload != null)
                    { ReleaseAtlas(); layoutChanged = true; BuildEntries(); PrepareCameras(cameraList, false); }
                    required = EstimateRequiredBytes();
                }
                // Acceleration storage never participates in residency planning. Reclaim it first
                // when ordinary geometry, instance or camera allocations need that budget.
                if (indexedVisibility != null && required + indexedVisibility.Bytes > statistics.GpuBudgetBytes)
                    ReleaseIndexedVisibility();
                // Game View and Scene View may render in separate contexts. If
                // their transient lists do not fit together, evict idle views
                // before discarding the shared, already resident model atlas.
                if (required > statistics.GpuBudgetBytes && atlas != null)
                {
                    staleCameras.Clear();
                    foreach (var pair in cameras)
                        if (pair.Value.PreparedGeneration != generation) staleCameras.Add(pair.Key);
                    foreach (var key in staleCameras) { cameras[key].Dispose(); cameras.Remove(key); }
                    if (staleCameras.Count > 0) required = EstimateRequiredBytes();
                }
                while ((required > statistics.GpuBudgetBytes || !FitsBufferLimits()) && plannedModels.Count > 0)
                {
                    if (!layoutChanged || atlas != null)
                    {
                        // Dropping stale camera/batch allocations may itself satisfy the
                        // budget. Re-estimate before unnecessarily rejecting a whole model.
                        ReleaseAtlas(); layoutChanged = true;
                        BuildEntries(); PrepareCameras(cameraList, false); required = EstimateRequiredBytes();
                        if (required <= statistics.GpuBudgetBytes && FitsBufferLimits()) break;
                    }
                    var rejected = plannedModels[plannedModels.Count - 1];
                    rejectedModels.Add(rejected); plannedModels.RemoveAt(plannedModels.Count - 1);
                    BuildEntries(); PrepareCameras(cameraList, false); required = EstimateRequiredBytes();
                }
                UploadPendingModels(context);
                statistics.BudgetRejectedModels = rejectedModels.Count;
                if (rejectedModels.Count > 0 && !statistics.AtlasUploadDeferredForHostMemory)
                    statistics.LastError = "Virtual Mesh graph buffer budget exceeded; rejected source colors remain visible without high-poly shadows.";
                using (sourceVisibilityMarker.Auto())
                {
                    if (layoutChanged)
                        foreach (var resident in residentInstances.Values) UpdateResidentSourceVisibility(resident);
                    else
                    {
                        foreach (var instance in changedInstances)
                            if (residentInstances.TryGetValue(instance, out var resident) && resident.VisibilityDirty)
                                UpdateResidentSourceVisibility(resident);
                        if (statistics.UploadedModelsThisContext > 0)
                            foreach (var model in uploadedModels)
                                if (modelUsers.TryGetValue(model, out var users))
                                    foreach (var instance in users)
                                        if (residentInstances.TryGetValue(instance, out var resident) && !resident.Ready)
                                            UpdateResidentSourceVisibility(resident);
                    }
                    statistics.ActiveInstances = readyResidentCount;
                    for (int i = residentActiveCount; i < active.Count; i++)
                        if (UpdateSourceVisibility(active[i])) statistics.ActiveInstances++;
                }
                changedInstances.Clear();
                if (atlas != null)
                {
                    UploadInstanceBuffers(out long instanceUploadBytes, out long modelIdUploadBytes);
                    if (statistics.QueuedModels > 0 || statistics.UploadedModelsThisContext > 0) PrepareCameras(cameraList, true);
                    ambientProbe[0] = RenderSettings.ambientProbe;
                    foreach (var camera in cameraList)
                    {
                        if (camera == null || !cameras.TryGetValue(camera.GetInstanceID(), out var state)) continue;
                        PrepareFrame(state, instanceUploadBytes, modelIdUploadBytes);
                        bool nativeColor = !state.Frame.UsesRenderGraph;
                        if (nativeColor) statistics.Path = "Render Graph atlas + native fallback for cameras without the feature";
                        if (nativeColor)
                        {
                            var cmd = CommandBufferPool.Get("Virtual Mesh atlas camera fallback");
                            foreach (var batch in state.Frame.Colors) RecordCull(cmd, state.Frame, batch);
                            foreach (var batch in state.Frame.Shadows) RecordCull(cmd, state.Frame, batch);
                            context.ExecuteCommandBuffer(cmd); CommandBufferPool.Release(cmd);
                            foreach (var batch in state.Frame.Colors) RegisterNative(state.Frame, batch);
                        }
                        // Native casters must be registered before URP's context.Cull. Their
                        // arguments are populated later by BeforeRenderingShadows for RG cameras.
                        foreach (var batch in state.Frame.Shadows) RegisterNative(state.Frame, batch);
                        foreach (var proxy in state.Frame.ProxyShadows) proxy.RegisterShadow(state.Camera);
                    }
                    statistics.SharedModels = 0;
                    foreach (var entry in entries.Values) if (entry.Ready) statistics.SharedModels++;
                    statistics.GeometryBytes = atlas.Bytes;
#if UNITY_EDITOR
                    statistics.PreparationCacheHit = geometryCacheHit;
                    statistics.PreparationCacheBytes = geometryCacheReusedBytes;
#endif
                    statistics.SplitVertexStreams = atlas.SplitVertexStreams;
                    statistics.AtlasVertexCount = atlas.VertexCount;
                    statistics.VertexPayloadBytes = atlas.VertexPayloadBytes;
                    statistics.VertexBindingBytes = VirtualMeshVertexBindings.Bytes;
                    statistics.GpuBufferBytes = ResidentBaseBufferBytes() + (indexedVisibility?.Bytes ?? 0);
                }
                statistics.VertexLayoutFailure = splitVertexStreamsFailure;
            }
            catch (Exception exception)
            {
                if (atlas?.GpuVertexUpload != null)
                {
                    gpuVertexTransposeFailed = true; gpuVertexTransposeFailure = exception.Message;
                    statistics.QueuedModels = plannedModels.Count;
                }
                else if (atlas != null && atlas.SplitVertexStreams)
                {
                    // A partial three-stream upload must never publish a model.
                    // Retry this requested layout as AoS, without changing the
                    // admitted geometry, until the user toggles the experiment.
                    splitVertexStreamsFailed = true; splitVertexStreamsFailure = exception.Message;
                    // Keep the bounded editor pump alive for the AoS recovery,
                    // even when the Scene camera is otherwise stationary.
                    statistics.QueuedModels = plannedModels.Count;
                }
                foreach (var instance in registeredInstances)
                    if (instance != null) { instance.SetResidencyFallback(); instance.SetDiagnostic(exception.Message); }
                ReleaseAtlas();
                // Any retry starts with a fresh layout and untouched upload cursors.
                entries.Clear();
                foreach (var pair in residentInstances)
                {
                    pair.Value.Ready = false;
                    pair.Key.InvalidateRenderState();
                }
                readyResidentCount = 0;
                changedInstances.Clear();
                statistics.ActiveInstances = statistics.SharedModels = statistics.DrawBatches = statistics.ShadowDrawBatches = 0;
                statistics.GeometryBytes = statistics.GpuBufferBytes = 0;
                statistics.VertexBindingBytes = VirtualMeshVertexBindings.Bytes;
                statistics.GpuBufferBytes = statistics.VertexBindingBytes + VirtualMeshVertexGpuRetirement.Bytes;
                statistics.VertexLayoutFailure = splitVertexStreamsFailure;
                statistics.CandidateClusters = statistics.VisibleClusters = statistics.ShadowCandidateClusters = statistics.VisibleShadowClusters = 0;
                statistics.LastError = exception.Message;
            }
            statistics.GpuBufferBytes = Math.Max(statistics.GpuBufferBytes,
                VirtualMeshVertexGpuRetirement.Bytes + VirtualMeshVertexBindings.Bytes);
            VirtualMeshRuntime.PublishStatistics(statistics);
        }

        private static float ModelDistance(VirtualMeshModelAsset model, List<Camera> cameraList)
        {
            float distance = float.PositiveInfinity;
            foreach (var item in active)
                if (item.Model == model)
                    foreach (var camera in cameraList) if (camera != null) distance = Mathf.Min(distance, item.Bounds.SqrDistance(camera.transform.position));
            return distance;
        }

        private static bool LayoutMatches()
        {
            if (entries.Count + rejectedModels.Count != modelSet.Count) return false;
            foreach (var model in modelSet)
            {
                if (rejectedModels.Contains(model)) continue;
                if (!entries.TryGetValue(model, out var entry) || entry.Revision != model.Revision || entry.CullingRevision != model.CullingRevision) return false;
            }
            return true;
        }

        private static bool TryReadmitModels(List<Camera> cameraList)
        {
            // Probe the existing layout plus one rejected model without allocating GPU
            // buffers or disturbing resident entry readiness when the probe cannot fit.
            // Camera/shadow demand can shrink without changing a model revision or budget.
            var originalEntries = new Dictionary<VirtualMeshModelAsset, Entry>(entries);
            var candidates = new List<VirtualMeshModelAsset>(rejectedModels);
            var admitted = new List<VirtualMeshModelAsset>();
            candidates.Sort((a, b) => ModelDistance(a, cameraList).CompareTo(ModelDistance(b, cameraList)));
            foreach (var model in candidates)
            {
                plannedModels.Add(model);
                BuildEntries(); PrepareCameras(cameraList, false);
                bool fits = EstimateFreshLayoutBytes() <= statistics.GpuBudgetBytes && FitsBufferLimits();
                if (fits)
                {
                    admitted.Add(model);
                    continue;
                }
                plannedModels.RemoveAt(plannedModels.Count - 1);
                BuildEntries(); PrepareCameras(cameraList, false);
            }
            if (admitted.Count > 0)
            {
                foreach (var model in admitted) rejectedModels.Remove(model);
                ReleaseAtlas(); BuildEntries(); PrepareCameras(cameraList, false);
                return true;
            }
            entries.Clear();
            foreach (var pair in originalEntries) entries.Add(pair.Key, pair.Value);
            PrepareCameras(cameraList, false);
            return false;
        }

        private static void BuildEntries()
        {
            entries.Clear();
            uint vertices = 0, indices = 0, clusters = 0, workBounds = 0;
            foreach (var model in plannedModels)
            {
                var entry = new Entry { Model = model, Id = entries.Count, Revision = model.Revision, CullingRevision = model.CullingRevision,
                    Data = new ModelData { VertexOffset = vertices, IndexOffset = indices, ClusterOffset = clusters, ClusterCount = (uint)model.Clusters.Length,
                        BoundsCenter = new Vector4(model.Bounds.center.x, model.Bounds.center.y, model.Bounds.center.z, model.HasCullingData ? 1 : 0) },
                    SubmeshStarts = new int[model.SubMeshCount], SubmeshCounts = new int[model.SubMeshCount],
                    SubmeshWorkBounds = new uint[model.SubMeshCount], WorkBoundsOffset = workBounds };
                foreach (var cluster in model.Clusters) entry.SubmeshCounts[cluster.submesh]++;
                for (int i = 1; i < entry.SubmeshStarts.Length; i++) entry.SubmeshStarts[i] = entry.SubmeshStarts[i - 1] + entry.SubmeshCounts[i - 1];
                for (int i = 0; i < entry.SubmeshCounts.Length; i++)
                {
                    entry.SubmeshWorkBounds[i] = workBounds;
                    int count = entry.SubmeshCounts[i];
                    if (count != 0) workBounds = checked(workBounds + (uint)(1 + (count - 1) / ClustersPerWorkItem));
                }
                entry.WorkBoundsCount = checked((int)(workBounds - entry.WorkBoundsOffset));
                entries.Add(model, entry);
                vertices = checked(vertices + (uint)model.Vertices.Length); indices = checked(indices + (uint)model.Indices.Length);
                clusters = checked(clusters + (uint)model.Clusters.Length);
            }
        }

        private static long PlannedAtlasBytes()
        {
            long bytes = (long)entries.Count * ModelData.Stride, clusters = 0;
            bool cones = false;
            foreach (var model in plannedModels) { bytes += model.GeometryBytes; clusters += model.Clusters.Length; cones |= model.HasCullingData; }
            return bytes + (cones ? clusters : 1) * VirtualMeshModelClusterCullingData.Stride +
                (long)WorkBoundsCapacity() * WorkBounds.Stride +
                (PlanGpuVertexTranspose ? VirtualMeshVertexGpuScratch.Bytes : 0);
        }

        private static int WorkBoundsCapacity()
        {
            int count = 0;
            foreach (var entry in entries.Values) count = checked(count + entry.WorkBoundsCount);
            return Math.Max(1, count);
        }

        private static long InstanceBufferBytes() => (long)instanceCapacity * InstanceBufferStride;

        private static long EstimateRequiredBytes()
        {
            long requiredCapacity = Math.Max(1, Mathf.NextPowerOfTwo(active.Count));
            long bytes = PlannedAtlasBytes() + Math.Max(instanceCapacity, requiredCapacity) * InstanceBufferStride + VirtualMeshVertexBindings.RequiredBytes;
            foreach (var state in cameras.Values)
            {
                bytes += state.PreparedGeneration == generation ? Math.Max(state.PreviousBytes, (long)Math.Max(1, Mathf.NextPowerOfTwo(active.Count)) * 64) : state.PreviousBytes;
                bytes += state.PreparedGeneration == generation ? Math.Max(state.LodBytes, requiredCapacity * LodParameterStride * 2) : state.LodBytes;
                bool prepared = state.PreparedGeneration == generation;
                bytes += prepared ? state.Proxies.RequiredBytes : state.Proxies.Bytes;
                foreach (var batch in state.Batches.Values)
                    bytes += prepared && batch.WorkCount > 0 ? Math.Max(batch.Bytes, batch.RequiredBytes) : batch.Bytes;
            }
            return bytes;
        }

        private static long EstimateFreshLayoutBytes()
        {
            // A successful readmission rebuild releases all old buffers. Count only
            // the current context's required capacities, not retained high-water marks
            // or inactive cameras that the rebuild will discard.
            long capacity = Math.Max(1, Mathf.NextPowerOfTwo(active.Count));
            long bytes = PlannedAtlasBytes() + capacity * InstanceBufferStride + VirtualMeshVertexBindings.RequiredBytes;
            foreach (var state in cameras.Values)
            {
                if (state.PreparedGeneration != generation) continue;
                bytes += capacity * (64 + LodParameterStride * 2);
                foreach (var proxy in state.Proxies.Batches.Values)
                    if (proxy.Count > 0) bytes += proxy.RequiredBytes;
                foreach (var batch in state.Batches.Values)
                    if (batch.WorkCount > 0) bytes += batch.RequiredBytes;
            }
            return bytes;
        }

        private static bool FitsBufferLimits()
        {
            long vertices = 0, indices = 0, clusters = 0;
            foreach (var model in plannedModels) { vertices += model.Vertices.Length; indices += model.Indices.Length; clusters += model.Clusters.Length; }
            long limit = SystemInfo.maxGraphicsBufferSize;
            return vertices <= int.MaxValue && indices <= int.MaxValue && clusters <= int.MaxValue &&
                vertices * VirtualMeshModelVertex.Stride <= limit && indices * 4 <= limit && clusters * VirtualMeshModelCluster.Stride <= limit &&
                (long)WorkBoundsCapacity() * WorkBounds.Stride <= limit;
        }

        private static GraphicsBuffer AllocateGeometryBuffer(int count, int stride, string label)
        {
#if UNITY_EDITOR
            if (atlas.EditorOwner != null)
            {
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured |
                    GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination, count, stride);
                atlas.EditorBufferNames.Add(label);
                return buffer;
            }
#endif
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        }

        private static void AllocateAtlas()
        {
#if UNITY_EDITOR
            try { AllocateAtlasCore(); }
            catch (Exception error) when (atlas?.EditorOwner != null)
            {
                // Native-owner allocation is optional. Retry the exact requested layout
                // using ordinary buffers before any geometry upload was submitted.
                DisposeAtlasBuffers(atlas); atlas = null;
                geometryCacheAllocationFailed = true;
                geometryCacheStatus = "Native cache allocation unavailable: " + error.Message;
                AllocateAtlasCore();
            }
#else
            AllocateAtlasCore();
#endif
        }

        private static void AllocateAtlasCore()
        {
            int vertexCount = 0, indexCount = 0, clusterCount = 0;
            bool cones = false;
            foreach (var model in plannedModels)
            {
                vertexCount = checked(vertexCount + model.Vertices.Length); indexCount = checked(indexCount + model.Indices.Length);
                clusterCount = checked(clusterCount + model.Clusters.Length); cones |= model.HasCullingData;
            }
            VirtualMeshVertexBindings.Ensure();
            atlas = new Atlas { Revision = ++atlasRevision, Bytes = PlannedAtlasBytes(), HasCullingData = cones,
                VertexCount = vertexCount, VertexPayloadBytes = (long)vertexCount * VirtualMeshModelVertex.Stride };
#if UNITY_EDITOR
            BeginGeometryCacheAllocation();
#endif
            if (planningSplitVertexStreams && !splitVertexStreamsFailed)
            {
                try
                {
                    atlas.VertexPositionUvColor = AllocateGeometryBuffer(vertexCount, VertexHot24.Stride, "VertexPositionUvColor");
                    atlas.VertexNormalTangent = AllocateGeometryBuffer(vertexCount, VertexNormal28.Stride, "VertexNormalTangent");
                    atlas.VertexCold = AllocateGeometryBuffer(vertexCount, VertexCold20.Stride, "VertexCold");
                    atlas.SplitVertexStreams = true;
                    if (PlanGpuVertexTranspose)
                    {
                        try { atlas.GpuVertexUpload = new VirtualMeshVertexGpuScratch(); }
                        catch (Exception exception)
                        {
                            gpuVertexTransposeFailed = true; gpuVertexTransposeFailure = exception.Message;
                            atlas.Bytes -= VirtualMeshVertexGpuScratch.Bytes;
                        }
                    }
                    if (atlas.GpuVertexUpload == null) atlas.VertexUploadScratch = new VirtualMeshVertexStreamScratch();
                }
                catch (Exception exception)
                {
#if UNITY_EDITOR
                    if (atlas.EditorOwner != null) throw;
#endif
                    atlas.VertexPositionUvColor?.Dispose(); atlas.VertexNormalTangent?.Dispose(); atlas.VertexCold?.Dispose();
                    atlas.VertexPositionUvColor = atlas.VertexNormalTangent = atlas.VertexCold = null;
                    atlas.VertexUploadScratch = null;
                    atlas.GpuVertexUpload?.Dispose(); atlas.GpuVertexUpload = null;
                    atlas.SplitVertexStreams = false;
                    splitVertexStreamsFailed = true; splitVertexStreamsFailure = exception.Message;
                    atlas.Bytes = PlannedAtlasBytes();
                    Debug.LogWarning("Virtual Mesh split vertex allocation failed; retaining the original atlas layout. " + exception.Message);
                }
            }
            if (!atlas.SplitVertexStreams)
                atlas.Vertices = AllocateGeometryBuffer(vertexCount, VirtualMeshModelVertex.Stride, "Vertices");
            atlas.Indices = AllocateGeometryBuffer(indexCount, 4, "Indices");
            atlas.Clusters = AllocateGeometryBuffer(clusterCount, VirtualMeshModelCluster.Stride, "Clusters");
            atlas.CullingData = AllocateGeometryBuffer(cones ? clusterCount : 1, VirtualMeshModelClusterCullingData.Stride, "CullingData");
            atlas.ModelTable = AllocateGeometryBuffer(entries.Count, ModelData.Stride, "ModelTable");
            atlas.WorkBounds = AllocateGeometryBuffer(WorkBoundsCapacity(), WorkBounds.Stride, "WorkBounds");
            var models = new ModelData[entries.Count];
            foreach (var entry in entries.Values) models[entry.Id] = entry.Data;
            atlas.ModelTable.SetData(models);
            if (!cones) atlas.CullingData.SetData(new[] { new VirtualMeshModelClusterCullingData { axisCutoff = new Vector4(0, 0, 0, 1) } });
        }

        private static void UploadPendingModels(ScriptableRenderContext context)
        {
            uploadedModels.Clear(); gpuCompletedUploads.Clear();
            if (entries.Count == 0) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= atlasUploadWindowEnds || now < atlasUploadWindowEnds - AtlasUploadWindowSeconds)
            {
                atlasUploadWindowEnds = now + AtlasUploadWindowSeconds;
                atlasUploadWindowBytes = 0;
                splitUploadWindowTicks = 0;
                gpuUploadWindowTicks = 0;
            }

            int budget = Math.Max(0, AtlasUploadWindowLimit - atlasUploadWindowBytes);
            // Keep at most one byte-window of recorded transfer payload in flight.
            // This bounds host command staging without relying on render queue depth.
            if (atlas?.GpuVertexUpload != null && !atlas.GpuVertexUpload.CanDispose) budget = 0;
            // Retirement consumes real GPU memory, but never lowers geometry admission.
            // Defer the next atlas until retirement: allowing overlap would also
            // require accounting for future camera/batch growth against both.
            // This only polls; it never waits for the GPU on the CPU thread.
            if (atlas == null && VirtualMeshVertexGpuRetirement.Bytes > 0)
            {
                budget = 0;
                statistics.LastError = "Waiting for the previous GPU vertex transfer to retire.";
            }
            long vertexScratch = planningSplitVertexStreams && !splitVertexStreamsFailed && !PlanGpuVertexTranspose
                ? VirtualMeshVertexStreamPacking.ScratchBytes : 0;
            if (atlas == null && budget > 0 && !VirtualMeshHostMemory.HasUploadHeadroom(PlannedAtlasBytes() + AtlasHostReserveBytes + vertexScratch))
            {
                statistics.AtlasUploadDeferredForHostMemory = true;
                budget = 0;
            }
            if (atlas == null && budget > 0)
            {
                AllocateAtlas();
                // Promote only after the actual GPU path has been created. Unsupported
                // devices and allocation fallback retain the original 16 MiB path.
                budget = Math.Max(0, AtlasUploadWindowLimit - atlasUploadWindowBytes);
            }

            int uploads = Mathf.Clamp(VirtualMeshRuntime.MaxModelUploadsPerContext, 1, 2);
            int remaining = 0;
            foreach (var model in plannedModels)
            {
                var entry = entries[model];
                if (entry.Ready) continue;
                if (atlas != null && budget > 0 && uploads > 0 && GpuUploadTimeRemaining)
                {
                    // Include the worst-case sort scratch for this model. The
                    // sorted arrays, if needed, only live until publication.
                    long scratch = entry.UploadClusters == null
                        ? (long)model.Clusters.Length * (VirtualMeshModelCluster.Stride +
                            (model.HasCullingData ? VirtualMeshModelClusterCullingData.Stride : 0))
                        : 0;
                    if (entry.UploadBounds == null)
                        scratch += (long)entry.WorkBoundsCount * WorkBounds.Stride;
                    bool hasHeadroom = VirtualMeshHostMemory.HasUploadHeadroom(AtlasHostReserveBytes + budget + scratch);
                    if (!hasHeadroom && ExpandedGpuUploadActive && budget > AtlasUploadSliceBytes)
                    {
                        budget = Math.Max(0, AtlasUploadSliceBytes - atlasUploadWindowBytes);
                        hasHeadroom = budget > 0 && VirtualMeshHostMemory.HasUploadHeadroom(AtlasHostReserveBytes + budget + scratch);
                    }
                    if (!hasHeadroom)
                    {
                        statistics.AtlasUploadDeferredForHostMemory = true;
                        budget = 0;
                    }
                    else
                    {
                        int before = budget;
                        bool timed = ExpandedGpuUploadActive;
                        if (timed) gpuUploadActiveStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool complete;
                        try { complete = UploadModel(entry, ref budget); }
                        finally
                        {
                            if (timed) gpuUploadWindowTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gpuUploadActiveStart;
                            gpuUploadActiveStart = 0;
                        }
                        atlasUploadWindowBytes += before - budget;
                        AtlasUploadProgressBytes += before - budget;
                        statistics.AtlasUploadBytesThisContext += before - budget;
                        if (complete)
                        {
                            uploads--;
                            if (atlas.GpuVertexUpload != null) gpuCompletedUploads.Add(entry);
                            else
                            {
                                uploadedModels.Add(model);
                                statistics.UploadedModelsThisContext++;
                            }
                        }
                    }
                }
                if (!entry.Ready) remaining++;
            }
            // Submit BEFORE publishing Ready or preparing native fallback/shadow
            // registration. Private uploads submit independently before the later
            // context's draws on the graphics queue; async compute is forbidden.
            atlas?.GpuVertexUpload?.Submit(context);
            foreach (var entry in gpuCompletedUploads)
            {
                entry.Ready = true; uploadedModels.Add(entry.Model);
                statistics.UploadedModelsThisContext++; remaining--;
            }
            gpuCompletedUploads.Clear();
            statistics.QueuedModels = remaining;
            if (statistics.AtlasUploadDeferredForHostMemory)
                statistics.LastError = "Virtual Mesh atlas upload is waiting for system memory headroom.";
        }

        private static int PendingAtlasUploadCount()
        {
            int count = 0;
            foreach (var entry in entries.Values)
                if (!entry.Ready) count++;
            return count;
        }

        private static bool UploadModel(Entry entry, ref int budget)
        {
            using var scope = uploadMarker.Auto();
            var model = entry.Model;
            while (budget > 0 && entry.UploadStream < 5 && GpuUploadTimeRemaining)
            {
                bool complete;
                switch (entry.UploadStream)
                {
                    case 0:
                        complete = UploadAtlasVertices(model.Vertices, (int)entry.Data.VertexOffset, ref entry.UploadElement, ref budget);
                        break;
                    case 1:
                        complete = UploadAtlasRange(atlas.Indices, model.Indices, (int)entry.Data.IndexOffset,
                            ref entry.UploadElement, ref budget);
                        break;
                    case 2:
                        if (entry.UploadClusters == null) PrepareClusterUpload(entry);
                        complete = UploadAtlasRange(atlas.Clusters, entry.UploadClusters, (int)entry.Data.ClusterOffset,
                            ref entry.UploadElement, ref budget);
                        break;
                    case 3:
                        complete = entry.UploadCones == null || UploadAtlasRange(atlas.CullingData, entry.UploadCones,
                            (int)entry.Data.ClusterOffset, ref entry.UploadElement, ref budget);
                        break;
                    default:
                        entry.UploadBounds ??= BuildWorkBounds(entry, entry.UploadClusters, entry.UploadCones);
                        complete = UploadAtlasRange(atlas.WorkBounds, entry.UploadBounds, (int)entry.WorkBoundsOffset,
                            ref entry.UploadElement, ref budget);
                        break;
                }

                if (!complete) return false;
                entry.UploadStream++;
                entry.UploadElement = 0;
            }
            if (entry.UploadStream < 5) return false;
            entry.UploadClusters = null;
            entry.UploadCones = null;
            entry.UploadBounds = null;
            // GPU completion is queued for publication only after Submit(context).
            // The upload commands must precede every draw that can observe Ready.
            if (atlas.GpuVertexUpload == null) entry.Ready = true;
            return true;
        }

        private static bool UploadAtlasRange<T>(GraphicsBuffer buffer, T[] data, int destination, ref int element, ref int budget)
            where T : struct
        {
            int stride = buffer.stride;
            int count = Math.Min(data.Length - element, Math.Min(budget, AtlasUploadCallBytes) / stride);
            while (count > 0 && GpuUploadTimeRemaining)
            {
                buffer.SetData(data, element, destination + element, count);
                element += count;
                budget -= count * stride;
                count = Math.Min(data.Length - element, Math.Min(budget, AtlasUploadCallBytes) / stride);
            }
            return element == data.Length;
        }

        private static bool UploadAtlasVertices(VirtualMeshModelVertex[] data, int destination, ref int element, ref int budget)
        {
            if (!atlas.SplitVertexStreams)
                return UploadAtlasRange(atlas.Vertices, data, destination, ref element, ref budget);
            if (atlas.GpuVertexUpload != null)
            {
                while (element < data.Length && budget >= VirtualMeshModelVertex.Stride && GpuUploadTimeRemaining)
                {
                    int count = Math.Min(data.Length - element, Math.Min(VirtualMeshVertexGpuScratch.Capacity,
                        budget / VirtualMeshModelVertex.Stride));
                    atlas.GpuVertexUpload.Record(atlas, data, element, destination + element, count);
                    element += count; budget -= count * VirtualMeshModelVertex.Stride;
                }
                return element == data.Length;
            }
            // The existing byte window is shared across cameras. Bound CPU
            // transpose/upload work in that window as well; one native SetData
            // call is not preemptible, so this is a soft latency limit.
            while (element < data.Length && splitUploadWindowTicks < splitUploadSoftBudgetTicks)
            {
                int count = Math.Min(data.Length - element, Math.Min(VirtualMeshVertexStreamPacking.ChunkVertices,
                    budget / VirtualMeshModelVertex.Stride));
                if (count <= 0) break;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    atlas.VertexUploadScratch.Upload(atlas, data, element, destination + element, count);
                    element += count;
                    budget -= count * VirtualMeshModelVertex.Stride;
                }
                finally { splitUploadWindowTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start; }
            }
            return element == data.Length;
        }

        private static void PrepareClusterUpload(Entry entry)
        {
            var model = entry.Model;
            var clusters = model.Clusters;
            var cones = model.HasCullingData ? model.CullingData : null;
            bool sorted = true;
            for (int i = 1; i < clusters.Length; i++) if (clusters[i].submesh < clusters[i - 1].submesh) { sorted = false; break; }
            if (!sorted)
            {
                var sortedClusters = new VirtualMeshModelCluster[clusters.Length];
                var sortedCones = cones != null ? new VirtualMeshModelClusterCullingData[cones.Length] : null;
                var next = (int[])entry.SubmeshStarts.Clone();
                for (int i = 0; i < clusters.Length; i++)
                {
                    int target = next[clusters[i].submesh]++;
                    sortedClusters[target] = clusters[i]; if (cones != null) sortedCones[target] = cones[i];
                }
                clusters = sortedClusters; cones = sortedCones;
            }
            entry.UploadClusters = clusters;
            entry.UploadCones = cones;
        }

        private static WorkBounds[] BuildWorkBounds(Entry entry, VirtualMeshModelCluster[] clusters, VirtualMeshModelClusterCullingData[] cones)
        {
            var groups = new WorkBounds[entry.WorkBoundsCount];
            int group = 0;
            for (int slot = 0; slot < entry.SubmeshCounts.Length; slot++)
            {
                int start = entry.SubmeshStarts[slot];
                int end = start + entry.SubmeshCounts[slot];
                for (int first = start; first < end; first += ClustersPerWorkItem)
                {
                    int last = Math.Min(end, first + ClustersPerWorkItem);
                    var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                    float radius = 0;
                    bool valid = true;
                    for (int i = first; i < last; i++)
                    {
                        Vector4 proxy = cones == null
                            ? new Vector4(clusters[i].center.x, clusters[i].center.y, clusters[i].center.z, clusters[i].radius)
                            : cones[i].centerRadius;
                        if (!float.IsFinite(proxy.x) || !float.IsFinite(proxy.y) || !float.IsFinite(proxy.z) ||
                            !float.IsFinite(proxy.w) || proxy.w < 0)
                        {
                            valid = false;
                            break;
                        }
                        var center = new Vector3(proxy.x, proxy.y, proxy.z);
                        min = Vector3.Min(min, center);
                        max = Vector3.Max(max, center);
                        radius = Mathf.Max(radius, proxy.w);
                    }
                    var midpoint = min * .5f + max * .5f;
                    var extents = Vector3.Max(max - midpoint, midpoint - min);
                    groups[group++] = valid
                        ? new WorkBounds { CenterRadius = new Vector4(midpoint.x, midpoint.y, midpoint.z, radius),
                            Extents = new Vector4(extents.x, extents.y, extents.z, 0) }
                        : new WorkBounds { CenterRadius = new Vector4(0, 0, 0, -1) };
                }
            }
            return groups;
        }

        // Compare the actual GPU payload in slot order, including floating-point
        // bits. A reused slot only needs an upload if its value changes.
        private static unsafe bool PayloadMatches<T>(T[] current, T[] previous, int count) where T : unmanaged
        {
            if (count == 0) return true;
            fixed (T* currentPtr = current, previousPtr = previous)
                return UnsafeUtility.MemCmp(currentPtr, previousPtr, (long)count * sizeof(T)) == 0;
        }

        private struct UploadRange
        {
            public int Start, End;
        }

        private static int AddUploadRange(Span<UploadRange> ranges, int rangeCount, int start, int end)
        {
            if (rangeCount > 0 && ranges[rangeCount - 1].End == start)
            {
                ranges[rangeCount - 1].End = end;
                return rangeCount;
            }
            if (rangeCount == ranges.Length) return -1;
            ranges[rangeCount] = new UploadRange { Start = start, End = end };
            return rangeCount + 1;
        }

        private static unsafe int FindDirtyRanges<T>(T[] data, T[] uploaded, int count, Span<UploadRange> ranges)
            where T : unmanaged
        {
            const int comparisonBlockSize = 32;
            int rangeCount = 0;
            fixed (T* dataPtr = data, uploadedPtr = uploaded)
            {
                for (int start = 0; start < count;)
                {
                    int blockCount = Math.Min(comparisonBlockSize, count - start);
                    int end = start + blockCount;
                    if (UnsafeUtility.MemCmp(dataPtr + start, uploadedPtr + start, (long)blockCount * sizeof(T)) != 0)
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (UnsafeUtility.MemCmp(dataPtr + i, uploadedPtr + i, sizeof(T)) == 0) continue;
                            rangeCount = AddUploadRange(ranges, rangeCount, i, i + 1);
                            if (rangeCount < 0) return rangeCount;
                        }
                    }
                    start = end;
                }
            }
            return rangeCount;
        }

        private static int UploadChangedData<T>(GraphicsBuffer buffer, T[] data, int count, ref T[] uploaded, ref int uploadedCount)
            where T : unmanaged
        {
            if (count == 0)
            {
                uploadedCount = 0;
                return 0;
            }

            int sharedCount = Math.Min(count, uploadedCount);
            bool sharedMatches = PayloadMatches(data, uploaded, sharedCount);
            if (count == sharedCount && sharedMatches)
            {
                uploadedCount = count;
                return 0;
            }

            // Sparse edits are common, but limit the number of graphics calls
            // when reordering or animation dirties many disjoint slots.
            Span<UploadRange> ranges = stackalloc UploadRange[8];
            int rangeCount = sharedMatches ? 0 : FindDirtyRanges(data, uploaded, sharedCount, ranges);
            if (rangeCount >= 0 && count > sharedCount)
                rangeCount = AddUploadRange(ranges, rangeCount, sharedCount, count);
            if (rangeCount < 0)
            {
                ranges[0] = new UploadRange { Start = 0, End = count };
                rangeCount = 1;
            }

            if (uploaded.Length < count) Array.Resize(ref uploaded, data.Length);
            int elementsUploaded = 0;
            for (int i = 0; i < rangeCount; i++)
            {
                var range = ranges[i];
                int length = range.End - range.Start;
                buffer.SetData(data, range.Start, range.Start, length);
                Array.Copy(data, range.Start, uploaded, range.Start, length);
                elementsUploaded += length;
            }
            uploadedCount = count;
            return elementsUploaded;
        }

        private static void UploadInstanceBuffers(out long instanceUploadBytes, out long modelIdUploadBytes)
        {
            using var scope = uploadMarker.Auto();
            int count = active.Count;
            int capacity = Math.Max(1, Mathf.NextPowerOfTwo(count));
            if (instanceCapacity < capacity || instanceModelIds == null)
            {
                instanceModelIds?.Dispose();
                instanceTransforms?.Dispose();
                instanceCapacity = capacity;
                instanceModelIds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(uint));
                instanceTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, InstanceData.Stride);
                uploadedModelIdCount = uploadedInstanceCount = 0;
            }
            if (modelIds.Length < count) modelIds = new uint[capacity];
            if (instanceData.Length < count) instanceData = new InstanceData[capacity];
            for (int i = 0; i < count; i++)
            {
                var item = active[i];
                modelIds[i] = entries.TryGetValue(item.Model, out var entry) ? (uint)entry.Id : 0;
                instanceData[i] = item.Data;
            }
            modelIdUploadBytes = (long)UploadChangedData(instanceModelIds, modelIds, count, ref uploadedModelIds, ref uploadedModelIdCount) * sizeof(uint);
            instanceUploadBytes = (long)UploadChangedData(instanceTransforms, instanceData, count, ref uploadedInstanceData, ref uploadedInstanceCount) * InstanceData.Stride;
        }

        private static void UploadCameraLodParameters(CameraState state, bool hasShadowWork)
        {
            using var scope = uploadMarker.Auto();
            int capacity = Math.Max(1, Mathf.NextPowerOfTwo(active.Count));
            if (state.ColorLodParameters == null || state.LodCapacity < capacity)
            {
                state.ColorLodParameters?.Dispose();
                state.ShadowLodParameters?.Dispose();
                state.LodCapacity = capacity;
                state.ColorLodParameters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, LodParameterStride);
                state.ShadowLodParameters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, LodParameterStride);
                state.UploadedColorLodCount = state.UploadedShadowLodCount = 0;
            }
            state.CameraLodDataUploadBytes = (long)UploadChangedData(state.ColorLodParameters, state.ColorLodData, active.Count,
                ref state.UploadedColorLodData, ref state.UploadedColorLodCount) * LodParameterStride;
            state.ShadowLodDataUploadBytes = hasShadowWork
                ? (long)UploadChangedData(state.ShadowLodParameters, state.ShadowLodData, active.Count,
                    ref state.UploadedShadowLodData, ref state.UploadedShadowLodCount) * LodParameterStride
                : 0;
            state.CameraLodDataUploadBytes += state.ShadowLodDataUploadBytes;
        }

        private static void UploadPreviousTransforms(CameraState state)
        {
            int count = active.Count;
            bool orderChanged = state.PreviousOrder.Count != count;
            int capacity = Math.Max(1, Mathf.NextPowerOfTwo(count));
            if (state.PreviousObjectToWorld == null || state.PreviousCapacity < capacity)
            {
                state.PreviousObjectToWorld?.Dispose(); state.PreviousCapacity = capacity;
                state.PreviousObjectToWorld = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 64);
                state.UploadedPreviousCount = 0;
                state.PreviousData = new Matrix4x4[capacity];
                Array.Resize(ref state.PendingMatrices, capacity);
                Array.Resize(ref state.PendingOrder, capacity);
            }
            for (int i = 0; i < count; i++)
            {
                var item = active[i];
                var current = item.Data.ObjectToWorld;
                bool hasPrevious = state.PreviousMatrices.TryGetValue(item.Instance, out var previous);
                state.PreviousData[i] = hasPrevious ? previous : current;
                state.PendingMatrices[i] = current;
                state.PendingOrder[i] = item.Instance;
                orderChanged |= i >= state.PreviousOrder.Count || state.PreviousOrder[i] != item.Instance;
            }
            if (count < state.PendingCount) Array.Clear(state.PendingOrder, count, state.PendingCount - count);
            state.PendingCount = count;
            state.PendingHistoryChanged = orderChanged ||
                !PayloadMatches(state.PendingMatrices, state.PreviousData, count);
            using (uploadMarker.Auto())
                state.PreviousTransformUploadBytes = (long)UploadChangedData(state.PreviousObjectToWorld, state.PreviousData,
                    count, ref state.UploadedPreviousData, ref state.UploadedPreviousCount) * 64;
            if (orderChanged || state.LastAtlasRevision != atlas.Revision) state.HistoryRevision = ++historyRevision;
            state.LastAtlasRevision = atlas.Revision;
        }

        private static void PrepareCameras(List<Camera> cameraList, bool readyOnly)
        {
            using var scope = prepareMarker.Auto();
            double now = Time.realtimeSinceStartupAsDouble;
            staleCameras.Clear();
            foreach (var pair in cameras)
            {
                if (pair.Value.Camera == null || now - pair.Value.LastUsed > 10) staleCameras.Add(pair.Key);
            }
            foreach (var id in staleCameras) { cameras[id].Dispose(); cameras.Remove(id); }
            foreach (var camera in cameraList)
            {
                if (camera == null) continue;
                if (!cameras.TryGetValue(camera.GetInstanceID(), out var state))
                { state = new CameraState { Camera = camera }; cameras.Add(camera.GetInstanceID(), state); }
                // Scene View and Game View can arrive in separate render contexts.
                // Reset only the camera whose plan is about to be rebuilt.
                foreach (var batch in state.Batches.Values) batch.Reset();
                state.Proxies.Reset();
                state.LastUsed = now;
                state.PreparedGeneration = generation;
                state.RequestedBudget = state.EstimatedBudgetTriangles = state.ShadowRequestedBudget = state.ShadowEstimatedBudgetTriangles = 0;
                state.ProfiledInstances = state.UnmetInstances = state.UnprofiledInstances = state.ShadowProfiledInstances = state.ShadowUnmetInstances = 0;
                state.MaximumBudgetScale = state.MaximumShadowBudgetScale = 1;
                state.CoverageLimitedInstances = state.ShadowCoverageLimitedInstances = 0;
                state.MaterialCoverageLimitedInstances = state.ShadowMaterialCoverageLimitedInstances = 0;
                state.LodSelectionCacheHits = state.LodSelectionCacheMisses = 0;
                using (ProfileDetail(prepareInstanceDataMarker))
                {
                    if (state.ColorLodData.Length < active.Count)
                    {
                        int capacity = Mathf.NextPowerOfTwo(active.Count);
                        state.ColorLodData = new Vector2[capacity];
                        state.ShadowLodData = new Vector2[capacity];
                    }
                    for (int i = 0; i < active.Count; i++)
                    { state.ColorLodData[i] = active[i].ColorLod; state.ShadowLodData[i] = active[i].ShadowLod; }
                }
                VirtualMeshLodBudget.BudgetCurve budgetCurve;
                float proxyHeightLimit;
                BudgetView budgetView;
                Plane[] planes;
                int cameraMask;
                ulong sceneMask;
#if UNITY_EDITOR
                bool sceneView;
#endif
                bool shadows;
                Bounds receivers;
                using (ProfileDetail(prepareCameraMarker))
                {
                    budgetCurve = new VirtualMeshLodBudget.BudgetCurve(CameraLodBudget?.Invoke(camera));
                    proxyHeightLimit = CameraImpostorMaxHeight?.Invoke(camera) ?? 0;
                    budgetView = new BudgetView(camera);
                    planes = GeometryUtility.CalculateFrustumPlanes(camera);
                    cameraMask = camera.cullingMask;
                    sceneMask = camera.overrideSceneCullingMask;
#if UNITY_EDITOR
                    sceneView = camera.cameraType == CameraType.SceneView;
#endif
                    shadows = VirtualMeshRuntime.CameraRendersShadows(camera);
                    if (state.ColorLodSelections.Length < active.Count)
                        Array.Resize(ref state.ColorLodSelections, Mathf.NextPowerOfTwo(active.Count));
                    if (shadows && state.ShadowLodSelections.Length < active.Count)
                        Array.Resize(ref state.ShadowLodSelections, Mathf.NextPowerOfTwo(active.Count));
                    receivers = shadows ? VirtualMeshRuntime.GetShadowReceiverBounds(camera) : default;
                }
                for (int instanceId = 0; instanceId < active.Count; instanceId++)
                {
                    var item = active[instanceId]; var instance = item.Instance;
                    Entry entry;
                    bool color, shadow;
                    using (ProfileDetail(prepareVisibilityMarker))
                    {
                        if (!entries.TryGetValue(item.Model, out entry) || readyOnly && !entry.Ready) continue;
                        if ((cameraMask & (1 << item.Layer)) == 0 ||
                            sceneMask != 0 && (sceneMask & item.SceneMask) == 0) continue;
#if UNITY_EDITOR
                        if (sceneView && UnityEditor.SceneVisibilityManager.instance.IsHidden(item.GameObject)) continue;
#endif
                        color = item.ShadowMode != ShadowCastingMode.ShadowsOnly && GeometryUtility.TestPlanesAABB(planes, item.Bounds);
                        shadow = shadows && item.ShadowMode != ShadowCastingMode.Off &&
                            VirtualMeshRuntime.CanCastVisibleShadow(item.Bounds, item.Layer, cameraMask, receivers);
                        if (!color && !shadow) continue;
                    }
                    bool proxyColor = false, proxyShadow = false;
                    using (ProfileDetail(prepareLodMarker))
                    {
                        var profile = VirtualMeshLodBudget.GetProfile(entry.Model);
                        if (budgetCurve.Enabled || profile.RequiresEligibility)
                            ApplyLodBudget(state, budgetView, in budgetCurve, profile, item, instanceId, color, shadow, proxyHeightLimit, out proxyColor, out proxyShadow);
                    }
                    color &= !proxyColor; shadow &= !proxyShadow;
                    if (!color && !shadow) continue;
                    using (ProfileDetail(prepareAppendMarker))
                    {
                        for (int submesh = 0; submesh < entry.SubmeshCounts.Length; submesh++)
                        {
                            int count = entry.SubmeshCounts[submesh];
                            if (count == 0) continue;
                            if (color) AppendWork(state, new BatchKey(item, submesh, false), item.Bounds, entry, instanceId, submesh);
                            if (shadow) AppendWork(state, new BatchKey(item, submesh, true), item.Bounds, entry, instanceId, submesh);
                        }
                    }
                }
                staleBatches.Clear();
                foreach (var pair in state.Batches)
                    if (pair.Value.WorkCount == 0 && (pair.Key.Shadow && !shadows || now - pair.Value.LastUsed > 2)) staleBatches.Add(pair.Key);
                foreach (var key in staleBatches) { state.Batches[key].Dispose(); state.Batches.Remove(key); }
            }
        }

        private readonly struct BudgetView
        {
            public readonly Vector3 Position, Forward;
            public readonly float ProjectionY, PixelScale, Near;
            public readonly bool Orthographic;
            public BudgetView(Camera camera)
            {
                Position = camera.transform.position; Forward = camera.transform.forward;
                ProjectionY = Mathf.Abs(camera.projectionMatrix.m11);
                PixelScale = camera.pixelHeight * ProjectionY * .5f;
                Near = Mathf.Max(.001f, camera.nearClipPlane); Orthographic = camera.orthographic;
            }
        }

        private static void ApplyLodBudget(CameraState state, BudgetView view, in VirtualMeshLodBudget.BudgetCurve budgetCurve,
            VirtualMeshLodBudget.Profile profile, Active item, int instanceId, bool color, bool shadow, float maxProxyHeight,
            out bool proxyColor, out bool proxyShadow)
        {
            proxyColor = proxyShadow = false;
            var model = item.Model;
            var center = item.Data.ObjectToWorld.MultiplyPoint3x4(model.Bounds.center);
            float scale = item.Data.Parameters.x;
            float radius = model.Bounds.extents.magnitude * scale + item.WindPadding;
            float depth = Vector3.Dot(center - view.Position, view.Forward);
            float closest = Mathf.Max(view.Near, depth - radius);
            // Conservative projected sphere height; it intentionally stays large when the camera
            // is inside/near the model. LOD thresholds below use the exact existing shader metric.
            float screenHeight = radius * view.ProjectionY / (view.Orthographic ? 1 : closest);
            int budget = budgetCurve.Evaluate(screenHeight);
            float denominator = Mathf.Max(.000001f, view.PixelScale * scale);
            bool budgetEnabled = budgetCurve.Enabled;
            // Budget checkpoints for the original local hierarchy must cover every
            // group's LOD sphere, which can extend beyond the mesh's physical bounds.
            float lodRadius = profile.LodRadius * scale + item.WindPadding;
            if (profile.RequiresEligibility)
                lodRadius += Mathf.Max(.00001f, (center.magnitude + lodRadius) * .000001f);
            float lodClosest = Mathf.Max(view.Near, depth - lodRadius);
            float projectedPixels = (float)Math.Min(float.MaxValue, 2.0 * lodRadius * view.PixelScale /
                (view.Orthographic ? 1 : lodClosest));
            bool uniformEligibility = profile.HasUniformEligibilitySphere;
            var eligibilitySphere = profile.UniformEligibilitySphere;
            // A validated coarse leaf layer only applies to the captured material
            // state. On an edit, keep the original cluster hierarchy and cap out
            // the new leaf levels; do not re-enable the high-poly source renderer.
            bool materialCoverageValid = model.LeafLodVersion == 0 || model.LeafMaterialContract != null &&
                model.LeafMaterialContract.Matches(item.SourceMaterials, leafMaterialValidation, out _);
            if (!materialCoverageValid)
            {
                projectedPixels = float.MaxValue;
                if (color) state.MaterialCoverageLimitedInstances++;
                if (shadow) state.ShadowMaterialCoverageLimitedInstances++;
            }
            bool proxyPolicy = budgetEnabled && maxProxyHeight > 0 && screenHeight <= maxProxyHeight;
            VirtualMeshModelImpostorAsset proxy = proxyPolicy ? VirtualMeshImpostorRuntime.Resolve(item.Instance) : null;
            bool proxyAvailable = proxy != null && screenHeight <= proxy.MaxScreenHeight;
            if (color)
            {
                var parameters = item.ColorLod;
                float units = (view.Orthographic ? 1 : lodClosest) / denominator;
                float maximumUnits = (view.Orthographic ? 1 : Mathf.Max(view.Near, depth + lodRadius)) / denominator;
                if (uniformEligibility && VirtualMeshLodBudget.TryUniformEligibilityUpperUnits(eligibilitySphere,
                    item.Data.ObjectToWorld, scale, view.Position, view.Forward, view.PixelScale, view.Near,
                    view.Orthographic, false, out float exactUpper))
                    maximumUnits = Mathf.Max(units, exactUpper);
                var selection = state.ColorLodSelections[instanceId].Evaluate(profile, budget, units, maximumUnits,
                    projectedPixels, parameters.x, parameters.y, budgetEnabled, ref state.LodSelectionCacheHits, ref state.LodSelectionCacheMisses);
                if (!selection.EligibilityValid)
                    throw new InvalidOperationException("Unsafe foliage LOD coverage bounds for model " + model.name);
                if (selection.EligibilityLimited) state.CoverageLimitedInstances++;
                bool needsProxy = selection.Profiled ? selection.Unmet : proxyPolicy && VirtualMeshImpostorRuntime.RootTriangles(model) > budget;
                if (proxyAvailable && needsProxy)
                {
                    proxyColor = true;
                    state.Proxies.Add(item.Instance, proxy, instanceId, false, item.Bounds, state.LastUsed);
                    state.ProfiledInstances++; state.RequestedBudget += budget; state.EstimatedBudgetTriangles += 2;
                    if (budget < 2) state.UnmetInstances++;
                }
                else if (selection.Profiled)
                {
                    state.ProfiledInstances++; if (budgetEnabled) state.RequestedBudget += selection.RequestedTriangles;
                    state.EstimatedBudgetTriangles += selection.EstimatedTriangles;
                    if (selection.Unmet) state.UnmetInstances++;
                    state.MaximumBudgetScale = Mathf.Max(state.MaximumBudgetScale, selection.Scale);
                    state.ColorLodData[instanceId] = ScaleBudget(parameters, selection.Scale);
                }
                else
                {
                    state.UnprofiledInstances++;
                    if (needsProxy)
                    { state.UnmetInstances++; state.RequestedBudget += budget; state.EstimatedBudgetTriangles += VirtualMeshImpostorRuntime.FinestTriangles(model); }
                }
                if (proxyPolicy && needsProxy && !proxyAvailable) state.Proxies.UnavailableInstances++;
            }
            if (shadow)
            {
                int shadowBudget = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Ceiling(budget / (double)item.ShadowMultiplier)));
                var parameters = item.ShadowLod;
                float shadowDistance = Vector3.Distance(center, view.Position);
                float units = (view.Orthographic ? 1 : Mathf.Max(view.Near, shadowDistance - lodRadius)) / denominator;
                float maximumUnits = (view.Orthographic ? 1 : Mathf.Max(view.Near, shadowDistance + lodRadius)) / denominator;
                if (uniformEligibility && VirtualMeshLodBudget.TryUniformEligibilityUpperUnits(eligibilitySphere,
                    item.Data.ObjectToWorld, scale, view.Position, view.Forward, view.PixelScale, view.Near,
                    view.Orthographic, true, out float exactUpper))
                    maximumUnits = Mathf.Max(units, exactUpper);
                // Shadow detail follows the same camera-screen coverage gate as color;
                // its LOD distance remains radial to retain off-screen shadow casters.
                var selection = state.ShadowLodSelections[instanceId].Evaluate(profile, shadowBudget, units, maximumUnits,
                    projectedPixels, parameters.x, parameters.y, budgetEnabled, ref state.LodSelectionCacheHits, ref state.LodSelectionCacheMisses);
                if (!selection.EligibilityValid)
                    throw new InvalidOperationException("Unsafe foliage shadow LOD coverage bounds for model " + model.name);
                if (selection.EligibilityLimited) state.ShadowCoverageLimitedInstances++;
                bool needsProxy = proxyColor || (selection.Profiled ? selection.Unmet : proxyPolicy && VirtualMeshImpostorRuntime.RootTriangles(model) > shadowBudget);
                if (proxyAvailable && needsProxy)
                {
                    proxyShadow = true;
                    state.Proxies.Add(item.Instance, proxy, instanceId, true, item.Bounds, state.LastUsed);
                    state.ShadowProfiledInstances++; state.ShadowRequestedBudget += shadowBudget; state.ShadowEstimatedBudgetTriangles += 2;
                    if (shadowBudget < 2) state.ShadowUnmetInstances++;
                }
                else if (selection.Profiled)
                {
                    state.ShadowProfiledInstances++; if (budgetEnabled) state.ShadowRequestedBudget += selection.RequestedTriangles;
                    state.ShadowEstimatedBudgetTriangles += selection.EstimatedTriangles;
                    if (selection.Unmet) state.ShadowUnmetInstances++;
                    state.MaximumShadowBudgetScale = Mathf.Max(state.MaximumShadowBudgetScale, selection.Scale);
                    state.ShadowLodData[instanceId] = ScaleBudget(parameters, selection.Scale);
                }
                else if (needsProxy)
                { state.ShadowUnmetInstances++; state.ShadowRequestedBudget += shadowBudget; state.ShadowEstimatedBudgetTriangles += VirtualMeshImpostorRuntime.FinestTriangles(model); }
            }
        }

        private static Vector2 ScaleBudget(Vector2 parameters, float scale)
        {
            parameters.x *= scale;
            if (parameters.y > 0) parameters.y *= scale;
            return parameters;
        }

        private static void AppendWork(CameraState state, BatchKey key, Bounds bounds, Entry entry, int instanceId, int submesh)
        {
            if (!state.Batches.TryGetValue(key, out var batch)) { batch = new BatchState(key); state.Batches.Add(key, batch); }
            batch.LastUsed = state.LastUsed;
            if (batch.HasBounds) batch.Bounds.Encapsulate(bounds); else { batch.Bounds = bounds; batch.HasBounds = true; }
            int count = entry.SubmeshCounts[submesh];
            batch.CandidateCount += count;
            var parameters = key.Shadow ? active[instanceId].ShadowLod : active[instanceId].ColorLod;
            // Camera budget scales both values together; all intervals scale by its reciprocal and their
            // maximum overlap is unchanged. Keep the original, cacheable capacity parameters.
            using (ProfileDetail(prepareCapacityMarker))
                batch.SelectedCapacity += GetSelectedCapacity(entry.Model, submesh, parameters.x, parameters.y);
            if (batch.SelectedCapacity > int.MaxValue / 8) throw new InvalidOperationException("A material's selected cluster list exceeds the supported buffer capacity.");
            uint start = entry.Data.ClusterOffset + (uint)entry.SubmeshStarts[submesh];
            batch.AddWork(start, count, (uint)instanceId, (uint)entry.Id, entry.SubmeshWorkBounds[submesh]);
        }

        private static void PrepareFrame(CameraState state, long instanceUploadBytes, long modelIdUploadBytes)
        {
            using var scope = ProfileDetail(prepareFrameMarker);
            state.InstanceDataUploadBytes = instanceUploadBytes;
            state.ModelIdUploadBytes = modelIdUploadBytes;
            bool hasShadowWork = state.Proxies.ShadowInstances > 0;
            if (!hasShadowWork)
            {
                foreach (var owner in state.Batches.Values)
                {
                    if (!owner.Key.Shadow || owner.WorkCount == 0) continue;
                    hasShadowWork = true;
                    break;
                }
            }
            UploadCameraLodParameters(state, hasShadowWork);
            using (ProfileDetail(prepareFrameMotionMarker)) UploadPreviousTransforms(state);
            state.Proxies.Prepare(state.Camera, instanceTransforms, instanceTransforms, state.PreviousObjectToWorld, generation, state.LastUsed);
            bool graph = CameraUsesGraph != null && CameraUsesGraph(state.Camera);
            var colors = new List<Batch>(); var shadows = new List<Batch>();
            using (ProfileDetail(prepareFrameBatchMarker))
            foreach (var owner in state.Batches.Values)
            {
                if (owner.WorkCount == 0) continue;
                owner.Allocate(); owner.Shared.Synchronize(generation); owner.SubmittedGeneration = generation;
                // Synchronize copies source keywords: apply layout after it, from the actual atlas.
                VirtualMeshVertexBindings.SetLayoutKeyword(owner.Shared.Material, atlas.SplitVertexStreams);
                var properties = owner.Properties;
                VirtualMeshVertexBindings.BindAtlas(properties, atlas); properties.SetBuffer("_VMIndices", atlas.Indices);
                properties.SetBuffer("_VMClusters", atlas.Clusters); properties.SetBuffer("_VMModelTable", atlas.ModelTable);
                properties.SetBuffer("_VMInstances", instanceTransforms);
                properties.SetBuffer("_VMInstanceModelIds", instanceModelIds);
                properties.SetBuffer("_VMPreviousObjectToWorld", state.PreviousObjectToWorld);
                properties.SetBuffer("_VMVisibleClusters", owner.Visible);
                properties.SetInt("_VMUseGeometryAtlas", 1); properties.SetInt("_VMGraphDraw", graph && !owner.Key.Shadow ? 1 : 0);
                var materialProperties = VirtualMeshMaterialSupport.GetPropertyMaterial(owner.Key.Source, owner.Key.Template);
                properties.SetFloat("_VMReceiveShadows", owner.Key.ReceiveShadows && !materialProperties.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF") &&
                    (!materialProperties.HasProperty("_ReceiveShadows") || materialProperties.GetFloat("_ReceiveShadows") > 0.5f) ? 1 : 0);
                properties.CopySHCoefficientArraysFrom(ambientProbe);
                properties.SetVector("unity_RenderingLayer", new Vector4(BitConverter.Int32BitsToSingle(unchecked((int)owner.Key.RenderingLayers)), 0, 0, 0));
                properties.SetVector("unity_LightData", new Vector4(0, 0, 1, 0));
                properties.SetVector("unity_ProbesOcclusion", Vector4.one);
                float padding = VirtualMeshRuntime.GetWindPadding(materialProperties);
                VirtualMeshRuntime.GetShadowProjectionSnapshot(state.Camera, owner.Key.Layer,
                    out var receiverPlanes, out var directions, out var positions, out int lightCount);
                var lodParameters = owner.Key.Shadow ? state.ShadowLodParameters : state.ColorLodParameters;
                var batch = new Batch(owner, instanceTransforms, instanceModelIds, lodParameters, owner.Bounds,
                    owner.CandidateCount, padding, receiverPlanes, directions, positions, lightCount);
                if (owner.Key.Shadow)
                {
                    shadows.Add(batch); statistics.ShadowDrawBatches++; statistics.ShadowCandidateClusters += owner.CandidateCount;
                    statistics.VisibleShadowClusters += owner.VisibleCount;
                }
                else
                {
                    colors.Add(batch); statistics.DrawBatches++; statistics.CandidateClusters += owner.CandidateCount;
                    statistics.VisibleClusters += owner.VisibleCount;
                }
            }
            state.Frame = new Frame(state.Camera, generation, atlas, instanceTransforms, instanceModelIds,
                state.PreviousObjectToWorld, state.ColorLodParameters, state.ShadowLodParameters, state.HistoryRevision,
                colors.ToArray(), shadows.ToArray(), graph,
                state.Proxies.Colors, state.Proxies.Shadows);
        }

        internal static void BindCullParameters(CommandBuffer cmd, ComputeShader shader, int kernel, Frame frame, Batch batch) =>
            BindCullParameters(CommandBufferHelpers.GetComputeCommandBuffer(cmd), shader, kernel, frame, batch,
                bindWorkBounds: !batch.Shadow);

        internal static void BindCullParameters(ComputeCommandBuffer cmd, ComputeShader shader, int kernel, Frame frame, Batch batch,
            bool bindWorkBounds = false, bool retest = false)
        {
            cmd.SetComputeBufferParam(shader, kernel, "_VMClusters", frame.Atlas.Clusters);
            cmd.SetComputeBufferParam(shader, kernel, "_VMCullingData", frame.Atlas.CullingData);
            cmd.SetComputeBufferParam(shader, kernel, "_VMModelTable", frame.Atlas.ModelTable);
            if (bindWorkBounds)
                cmd.SetComputeBufferParam(shader, kernel, "_VMWorkBounds", frame.Atlas.WorkBounds);
            cmd.SetComputeBufferParam(shader, kernel, "_VMInstances", batch.Instances);
            if (!retest) cmd.SetComputeBufferParam(shader, kernel, "_VMLodParameters", batch.LodParameters);
            cmd.SetComputeBufferParam(shader, kernel, "_VMInstanceModelIds", batch.InstanceModelIds);
            cmd.SetComputeBufferParam(shader, kernel, "_VMWorkItems", batch.WorkItems);
            cmd.SetComputeBufferParam(shader, kernel, "_VMVisibleClusters", batch.Visible);
            cmd.SetComputeBufferParam(shader, kernel, "_VMCounters", batch.Counters);
            if (batch.Deferred != null)
                cmd.SetComputeBufferParam(shader, kernel, retest ? "_VMDeferredInput" : "_VMDeferredClusters", batch.Deferred);
            cmd.SetComputeIntParam(shader, "_VMUseGeometryAtlas", 1);
            cmd.SetComputeIntParam(shader, "_VMWorkItemCount", batch.WorkCount);
            cmd.SetComputeIntParam(shader, "_VMListCapacity", batch.Capacity);
            cmd.SetComputeIntParam(shader, "_VMWorkItemOffset", 0);
            cmd.SetComputeIntParam(shader, "_VMCullMode", batch.Cull);
            cmd.SetComputeIntParam(shader, "_VMConeDataEnabled", frame.Atlas.HasCullingData && batch.Cull != 0 && batch.WindPadding <= 0 ? 1 : 0);
            cmd.SetComputeFloatParam(shader, "_VMWindPadding", batch.WindPadding);
            cmd.SetComputeVectorArrayParam(shader, "_VMFrustumPlanes", frame.FrustumPlanes);
            cmd.SetComputeVectorParam(shader, "_VMCameraPosition", frame.CameraPosition);
            cmd.SetComputeVectorParam(shader, "_VMCameraForward", frame.CameraForward);
            cmd.SetComputeVectorParam(shader, "_VMLodParams", frame.LodParams);
            cmd.SetComputeIntParam(shader, "_VMShadowLightCount", batch.ShadowLightCount);
            if (batch.ShadowLightCount > 0)
            {
                cmd.SetComputeVectorArrayParam(shader, "_VMShadowReceiverPlanes", batch.ShadowReceiverPlanes);
                cmd.SetComputeVectorArrayParam(shader, "_VMShadowLightDirections", batch.ShadowLightDirections);
                cmd.SetComputeVectorArrayParam(shader, "_VMShadowLightPositionsRanges", batch.ShadowLightPositionsRanges);
            }
        }

        internal static void RecordCull(CommandBuffer cmd, Frame frame, Batch batch, bool coneEnabled = true)
        {
            if (!batch.Shadow) cmd.SetComputeTextureParam(cullShader, cullKernel, "_VMHiZTexture", Texture2D.blackTexture);
            RecordCull(CommandBufferHelpers.GetComputeCommandBuffer(cmd), frame, batch, coneEnabled);
        }

        // RG callers bind a declared dummy/history HiZ TextureHandle for color dispatches.
        // The shadow kernel has no HiZ resource. No typed command buffer is unwrapped here.
        internal static void RecordCull(ComputeCommandBuffer cmd, Frame frame, Batch batch, bool coneEnabled = true)
        {
            int kernel = batch.Shadow ? shadowKernel : cullKernel;
            // This private clone serves native fallback and shadows, never camera HiZ.
            // Do not inherit a keyword state from the shared resource when cloning it.
            cmd.SetKeyword(cullShader, cullFineHiZKeyword, false);
            cmd.SetBufferData(batch.Counters, zeroCounters);
            BindCullParameters(cmd, cullShader, kernel, frame, batch, bindWorkBounds: !batch.Shadow);
            if (!batch.Shadow) cmd.SetComputeIntParam(cullShader, "_VMEnableEarlyWorkFrustum", 0);
            if (!coneEnabled) cmd.SetComputeIntParam(cullShader, "_VMConeDataEnabled", 0);
            cmd.SetComputeIntParam(cullShader, "_VMHiZValid", 0);
            for (int offset = 0; offset < batch.WorkCount; offset += 65535)
            {
                cmd.SetComputeIntParam(cullShader, "_VMWorkItemOffset", offset);
                cmd.DispatchCompute(cullShader, kernel, Math.Min(65535, batch.WorkCount - offset), 1, 1);
            }
            RecordDrawArguments(cmd, cullShader, batch, batch.Arguments, batch.Shadow ? 3 : 0);
            RecordVisibilityReadback(cmd, batch, batch.Visible);
        }

        internal static void RecordDrawArguments(ComputeCommandBuffer cmd, ComputeShader shader, Batch batch,
            GraphicsBuffer arguments, int countIndex, bool retested = false)
        {
            int kernel = shader.FindKernel(retested ? "BuildRetestedDrawArguments" : "BuildDrawArguments");
            cmd.SetComputeIntParam(shader, "_VMListCapacity", batch.Capacity);
            cmd.SetComputeBufferParam(shader, kernel, "_VMCounters", batch.Counters);
            cmd.SetComputeBufferParam(shader, kernel, "_VMDrawArguments", arguments);
            if (retested) cmd.SetComputeBufferParam(shader, kernel, "_VMRecoveredArguments", batch.RecoveredArguments);
            else cmd.SetComputeIntParam(shader, "_VMArgumentPhase", countIndex);
            cmd.DispatchCompute(shader, kernel, 1, 1, 1);
        }

        internal static void RecordVisibilityReadback(CommandBuffer cmd, Frame frame, Batch batch, GraphicsBuffer visible = null) =>
            RecordVisibilityReadback(CommandBufferHelpers.GetComputeCommandBuffer(cmd), batch, visible ?? batch.Visible);

        internal static void RecordVisibilityReadback(CommandBuffer cmd, Batch batch, GraphicsBuffer visible = null) =>
            RecordVisibilityReadback(CommandBufferHelpers.GetComputeCommandBuffer(cmd), batch, visible ?? batch.Visible);

        internal static void RecordVisibilityReadback(ComputeCommandBuffer cmd, Frame frame, Batch batch, GraphicsBuffer visible = null) =>
            RecordVisibilityReadback(cmd, batch, visible ?? batch.Visible);

        internal static void RecordVisibilityReadback(ComputeCommandBuffer cmd, Batch batch, GraphicsBuffer visible = null)
        {
            var owner = batch.Owner;
            if (owner.Disposed || owner.ReadbackPending || !SystemInfo.supportsAsyncGPUReadback ||
                Time.realtimeSinceStartupAsDouble - owner.LastReadback < 0.5) return;
            owner.ReadbackPending = true; owner.LastReadback = Time.realtimeSinceStartupAsDouble;
            owner.ReadbackScheduled = true;
            owner.ScheduledSampleGeneration = generation;
        }

        private static void CompleteCamera(CameraState state)
        {
            if (state.Frame == null || state.Frame.Generation != generation) return;
            foreach (var proxy in state.Frame.ProxyColors) proxy.Readback();
            foreach (var proxy in state.Frame.ProxyShadows) proxy.Readback();
            if (state.CommittedGeneration != generation)
            {
                if (state.PendingHistoryChanged)
                {
                    state.PreviousOrder.Clear();
                    state.PreviousMatrices.Clear();
                    for (int i = 0; i < state.PendingCount; i++)
                    {
                        state.PreviousOrder.Add(state.PendingOrder[i]);
                        state.PreviousMatrices[state.PendingOrder[i]] = state.PendingMatrices[i];
                    }
                }
                state.CommittedGeneration = generation;
            }
            foreach (var owner in state.Batches.Values)
            {
                if (owner.Disposed || !owner.ReadbackScheduled) continue;
                owner.ReadbackScheduled = false;
                // The graph has finished writing its structured counters. Read them
                // with one asynchronous request independent of native draw args.
                int sampleGeneration = owner.ScheduledSampleGeneration;
                uint sampleCapacity = (uint)owner.Capacity;
                AsyncGPUReadback.Request(owner.Counters, request =>
                {
                    if (owner.Disposed) return;
                    owner.ReadbackPending = false;
                    if (request.hasError) return;
                    long oldCount = owner.VisibleCount;
                    var values = request.GetData<uint>();
                    if (values.Length < VisibilityCounterCount) return;
                    for (int i = 0; i < VisibilityCounterCount; i++) owner.LastCounters[i] = values[i];
                    if (owner.Key.Shadow)
                    {
                        owner.LastCounters[3] = Math.Min(values[3], sampleCapacity);
                        owner.LastCounters[9] = values[3] - owner.LastCounters[3];
                    }
                    else
                    {
                        owner.LastCounters[0] = Math.Min(values[0], sampleCapacity);
                        owner.LastCounters[1] = Math.Min(values[1], sampleCapacity);
                        owner.LastCounters[2] = Math.Min(values[2], sampleCapacity - owner.LastCounters[0]);
                        owner.LastCounters[9] = (values[0] - owner.LastCounters[0]) +
                            (values[1] - owner.LastCounters[1]) + (values[2] - owner.LastCounters[2]);
                    }
                    owner.LastSampleGeneration = sampleGeneration;
                    owner.VisibleCount = owner.LastCounters[3];
                    if (owner.SubmittedGeneration != generation) return;
                    if (owner.Key.Shadow) statistics.VisibleShadowClusters += owner.VisibleCount - oldCount;
                    else statistics.VisibleClusters += owner.VisibleCount - oldCount;
                    VirtualMeshRuntime.PublishStatistics(statistics);
                });
            }
        }

        private static void EndCamera(ScriptableRenderContext context, Camera camera)
        { if (camera != null && cameras.TryGetValue(camera.GetInstanceID(), out var state)) CompleteCamera(state); }

        private static void EndContext(ScriptableRenderContext context, List<Camera> cameraList)
        { foreach (var state in cameras.Values) CompleteCamera(state); }

        private static void RegisterNative(Frame frame, Batch batch)
        {
            var parameters = new RenderParams(batch.Material)
            {
                camera = frame.Camera, worldBounds = batch.Bounds, layer = batch.Layer, renderingLayerMask = batch.RenderingLayers,
                overrideSceneCullingMask = true, sceneCullingMask = batch.SceneMask, matProps = batch.Properties,
                shadowCastingMode = batch.Shadow ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off,
                receiveShadows = !batch.Shadow && batch.ReceiveShadows,
                lightProbeUsage = batch.Shadow ? LightProbeUsage.Off : LightProbeUsage.BlendProbes,
                reflectionProbeUsage = batch.Shadow ? ReflectionProbeUsage.Off : ReflectionProbeUsage.BlendProbes,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion
            };
            Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, batch.Arguments, 1);
        }

        private static void ReleaseAtlas(bool preservePreparedGeometry = false)
        {
            atlasUploadWindowEnds = 0;
            atlasUploadWindowBytes = 0;
            splitUploadWindowTicks = 0;
            gpuUploadWindowTicks = gpuUploadActiveStart = 0;
            AtlasUploadProgressBytes = 0;
            ReleaseIndexedVisibility();
            indexedVisibilityFailure = null;
            indexedVisibilityFailedRevision = -1;
            foreach (var state in cameras.Values) state.Dispose();
            cameras.Clear();
            foreach (var shared in materials.Values) Destroy(shared.Material);
            materials.Clear();
            instanceModelIds?.Dispose(); instanceModelIds = null;
            instanceTransforms?.Dispose(); instanceTransforms = null;
            if (atlas != null)
            {
                Atlas retiring = atlas;
                bool retained = false;
#if UNITY_EDITOR
                retained = preservePreparedGeometry && RetainPreparedGeometry(retiring);
#endif
                retiring.GpuVertexUpload?.DiscardRecorded();
                if (retiring.GpuVertexUpload != null
#if UNITY_EDITOR
                    || retiring.EditorCopyTransfer != null
#endif
                    )
                    VirtualMeshVertexGpuRetirement.Retire(retiring.Bytes
#if UNITY_EDITOR
                        + (retiring.EditorOwner != null ? retiring.EditorOwner.NativeBytes : 0)
#endif
                        , () => (retiring.GpuVertexUpload == null || retiring.GpuVertexUpload.CanDispose)
#if UNITY_EDITOR
                            && (retiring.EditorCopyTransfer == null || retiring.EditorCopyTransfer.CanDispose)
#endif
                        , () => DisposeAtlasBuffers(retiring, !retained));
                else DisposeAtlasBuffers(retiring, !retained);
                atlas = null;
            }
            instanceModelIds?.Dispose(); instanceModelIds = null;
            instanceTransforms?.Dispose(); instanceTransforms = null;
            instanceCapacity = 0;
            modelIds = uploadedModelIds = Array.Empty<uint>();
            instanceData = uploadedInstanceData = Array.Empty<InstanceData>();
            uploadedModelIdCount = uploadedInstanceCount = 0;
        }

        private static void DisposeAtlasBuffers(Atlas value, bool destroyNativeOwner = true)
        {
            value.GpuVertexUpload?.Dispose(); value.GpuVertexUpload = null;
            value.Vertices?.Dispose(); value.Indices?.Dispose(); value.Clusters?.Dispose(); value.CullingData?.Dispose();
            value.VertexPositionUvColor?.Dispose(); value.VertexNormalTangent?.Dispose(); value.VertexCold?.Dispose();
            value.VertexUploadScratch = null;
            value.ModelTable?.Dispose(); value.WorkBounds?.Dispose();
#if UNITY_EDITOR
            VirtualMeshEditorGeometryCache.Detach(value.EditorCopyCarriers); value.EditorCopyCarriers = null;
            value.EditorCopyTransfer?.Dispose(); value.EditorCopyTransfer = null;
            if (destroyNativeOwner && value.EditorOwner != null) value.EditorOwner.Clear();
            value.EditorOwner = null;
#endif
        }

        internal static void Release()
        {
            workItemScratch.Clear();
            workItemScratch.Capacity = 0;
            materialValidation.Clear();
            leafMaterialValidation.Clear();
            ReleaseAtlas(preservePreparedGeometry: true); entries.Clear(); plannedModels.Clear(); uploadedModels.Clear(); rejectedModels.Clear(); active.Clear(); modelSet.Clear(); transforms.Clear();
            residentInstances.Clear(); polledInstances.Clear(); changedInstances.Clear(); residentModels.Clear();
            modelUsers.Clear(); materialUsers.Clear(); residentErrors.Clear();
            residentActiveCount = readyResidentCount = 0;
            needsRegistrationSnapshot = true;
            while (VirtualMeshInstance.TryDequeueRenderStateChange(out _)) { }
            capacityModels.Clear();
            VirtualMeshLodBudget.ClearCache();
            VirtualMeshImpostorRuntime.Release();
            nextReadmissionCheck = 0;
            splitVertexStreamsFailed = false; splitVertexStreamsFailure = null;
            gpuVertexTransposeFailed = false; gpuVertexTransposeFailure = null;
            gpuCompletedUploads.Clear();
            RenderPipelineManager.endCameraRendering -= EndCamera; RenderPipelineManager.endContextRendering -= EndContext;
            completionSubscribed = false;
            Destroy(cullShader); cullShader = null; cullFineHiZKeyword = default;
        }

        private static void Destroy(UnityEngine.Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(resource); else UnityEngine.Object.DestroyImmediate(resource);
        }
    }
}
