#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Runtime
{
    internal static partial class VirtualMeshGraphRuntime
    {
        // Native Mesh carriers preserve GPU bytes across managed reload, but must
        // NEVER be bound as StructuredBuffer: GetVertexBuffer forces Raw on D3D12.
        // At Play boundaries, copy bytes between carriers and ordinary Structured
        // buffers, retiring one source at a time. Rendering keeps the original ABI.
        private const string GeometryCacheEnabledKey = "Unity.VirtualMesh.GeometryCache.Enabled";
        private const int GeometryCacheVersion = 1;
        // Editor transitions may borrow at most 128 MiB beyond the steady VM budget.
        // Admission still uses planningBudget. Bound the VM allocation against
        // reported device capacity and check host headroom for native Mesh staging.
        private const long CacheTransferHeadroomBytes = 128L * 1024 * 1024;
        private static bool geometryCacheAllocationFailed, geometryCacheHit;
        private static string geometryCacheStatus = "Not checked";
        private static long geometryCacheReusedBytes, geometryCacheCopyBytes;
        private static double geometryCacheCopyMilliseconds;
        private static double geometryCacheExportMilliseconds;
        private static long geometryCacheDriverPeakBytes;
        internal sealed class CacheCopyTransfer : IDisposable
        {
            private GraphicsFence fence;
            private CommandBuffer commands;
            private bool submitted;
            internal bool CanDispose {
                get { if (!submitted) return true; try { return fence.passed; } catch { return false; } }
            }
            internal void Copy(GraphicsBuffer source, GraphicsBuffer destination)
            {
                if (!CanDispose) throw new InvalidOperationException("Previous geometry cache copy is still in flight");
                Dispose();
                commands = new CommandBuffer { name = "Virtual Mesh Play geometry cache copy" };
                commands.CopyBuffer(source, destination);
                fence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                submitted = true; // Ownership precedes the native submission, including exceptional returns.
                Graphics.ExecuteCommandBuffer(commands);
                geometryCacheDriverPeakBytes = Math.Max(geometryCacheDriverPeakBytes, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver());
                var completion = AsyncGPUReadback.Request(destination, 4, 0);
                completion.WaitForCompletion();
                if (completion.hasError) throw new InvalidOperationException("Geometry cache copy completion failed");
                // Ordered readback completion proves the preceding copy has finished.
                submitted = false; Dispose();
            }
            public void Dispose() { commands?.Release(); commands = null; }
        }
        private static bool GeometryCacheEnabled => SessionState.GetBool(GeometryCacheEnabledKey, true);

        internal static object PreparationCacheDiagnostics => new {
            Enabled = GeometryCacheEnabled, Supported = VirtualMeshEditorGeometryCache.IsSupported,
            Hit = geometryCacheHit, Status = geometryCacheStatus,
            NativeBytes = VirtualMeshEditorGeometryCache.instance.NativeBytes,
            ReusedBytes = geometryCacheReusedBytes, GpuCopyBytes = geometryCacheCopyBytes,
            CopyMilliseconds = geometryCacheCopyMilliseconds,
            ExportMilliseconds = geometryCacheExportMilliseconds, DriverPeakBytes = geometryCacheDriverPeakBytes,
            TransferHeadroomLimitBytes = CacheTransferHeadroomBytes,
            LastMiss = SessionState.GetString("Unity.VirtualMesh.GeometryCache.LastMiss", ""),
            Identity = VirtualMeshEditorGeometryCache.instance.Identity,
            UploadBytes = AtlasUploadProgressBytes
        };

        [Serializable]
        private sealed class CachedEntry
        {
            public int Revision, CullingRevision, Id, VertexCount, IndexCount, ClusterCount, SubmeshCount;
            public uint VertexOffset, IndexOffset, ClusterOffset, WorkBoundsOffset;
            public Vector4 BoundsCenter;
            public int[] SubmeshStarts, SubmeshCounts;
            public uint[] SubmeshWorkBounds;
            public int WorkBoundsCount;
            public string DependencyHash;
        }

        [Serializable]
        private sealed class CachedLayout
        {
            public int Version, VertexCount;
            public long Budget, Bytes, VertexPayloadBytes;
            public double ExportMilliseconds;
            public bool Split, RequestedSplit, RequestedGpuTranspose, HasCullingData;
            public string RuntimeAssembly, Device, TransposeShaderHash;
            public string[] BufferNames;
            public CachedEntry[] Entries;
        }

        private static string CacheDevice => SystemInfo.graphicsDeviceType + ":" + SystemInfo.graphicsDeviceVendorID +
            ":" + SystemInfo.graphicsDeviceID + ":" + SystemInfo.graphicsDeviceVersion;
        private static string RuntimeAssemblyId => typeof(VirtualMeshGraphRuntime).Assembly.ManifestModule.ModuleVersionId.ToString();
        private static bool FitsCacheTransfer(long bytes, long largest)
        {
            if (bytes + largest + VirtualMeshVertexBindings.RequiredBytes > planningBudget + CacheTransferHeadroomBytes) return false;
            long deviceBytes = (long)SystemInfo.graphicsMemorySize * 1024 * 1024;
            // The driver allocation counter also retains pools/deferred releases;
            // it is measured below, not treated as an OS free-VRAM counter.
            return deviceBytes > 0 && bytes + largest + 256L * 1024 * 1024 <= deviceBytes &&
                VirtualMeshHostMemory.HasUploadHeadroom(largest + AtlasHostReserveBytes);
        }
        private static string DependencyHash(UnityEngine.Object model)
        {
            string path = AssetDatabase.GetAssetPath(model);
            return string.IsNullOrEmpty(path) ? "" : AssetDatabase.GetAssetDependencyHash(path).ToString();
        }

        private static void BeginGeometryCacheAllocation()
        {
            if (!GeometryCacheEnabled || geometryCacheAllocationFailed || !VirtualMeshEditorGeometryCache.IsSupported) return;
            var owner = VirtualMeshEditorGeometryCache.instance;
            var dependencies = new UnityEngine.Object[plannedModels.Count];
            var hashes = new string[plannedModels.Count];
            for (int i = 0; i < dependencies.Length; i++)
            { dependencies[i] = plannedModels[i]; hashes[i] = DependencyHash(plannedModels[i]); }
            owner.BeginAllocation(Guid.NewGuid().ToString("N"), dependencies);
            atlas.EditorOwner = owner;
            atlas.EditorDependencyHashes = hashes;
            atlas.EditorTransposeHash = DependencyHash(Resources.Load<ComputeShader>("VirtualMeshVertexTranspose"));
            atlas.EditorBufferNames = new List<string>();
            geometryCacheHit = false;
            geometryCacheReusedBytes = geometryCacheCopyBytes = 0;
            geometryCacheCopyMilliseconds = geometryCacheExportMilliseconds = 0;
            geometryCacheStatus = "Cold preparation";
        }

        private static bool RetainPreparedGeometry(Atlas value)
        {
            if (!GeometryCacheEnabled || value.EditorOwner == null || rejectedModels.Count != 0 ||
                entries.Count == 0 || entries.Count != modelSet.Count || PendingAtlasUploadCount() != 0 ||
                (value.GpuVertexUpload != null && (value.GpuVertexUpload.PendingBytes != 0 || !value.GpuVertexUpload.CanDispose))) return false;
            try
            {
                if (value.EditorTransposeHash != DependencyHash(Resources.Load<ComputeShader>("VirtualMeshVertexTranspose"))) return false;
                var snapshot = new CachedLayout {
                    Version = GeometryCacheVersion, VertexCount = value.VertexCount, Budget = planningBudget,
                    Bytes = value.Bytes - (value.GpuVertexUpload != null ? VirtualMeshVertexGpuScratch.Bytes : 0),
                    VertexPayloadBytes = value.VertexPayloadBytes, Split = value.SplitVertexStreams,
                    RequestedSplit = planningSplitVertexStreams, RequestedGpuTranspose = planningGpuVertexTranspose,
                    HasCullingData = value.HasCullingData, RuntimeAssembly = RuntimeAssemblyId, Device = CacheDevice,
                    TransposeShaderHash = value.EditorTransposeHash,
                    BufferNames = value.EditorBufferNames.ToArray(), Entries = new CachedEntry[plannedModels.Count]
                };
                for (int i = 0; i < plannedModels.Count; i++)
                {
                    var model = plannedModels[i]; var e = entries[model];
                    if (e.Revision != model.Revision || e.CullingRevision != model.CullingRevision ||
                        value.EditorDependencyHashes[i] != DependencyHash(model)) return false;
                    snapshot.Entries[i] = new CachedEntry {
                        Revision = e.Revision, CullingRevision = e.CullingRevision, Id = e.Id,
                        VertexCount = model.Vertices.Length, IndexCount = model.Indices.Length,
                        ClusterCount = model.Clusters.Length, SubmeshCount = model.SubMeshCount,
                        VertexOffset = e.Data.VertexOffset, IndexOffset = e.Data.IndexOffset, ClusterOffset = e.Data.ClusterOffset,
                        BoundsCenter = e.Data.BoundsCenter, SubmeshStarts = e.SubmeshStarts, SubmeshCounts = e.SubmeshCounts,
                        SubmeshWorkBounds = e.SubmeshWorkBounds, WorkBoundsOffset = e.WorkBoundsOffset,
                        WorkBoundsCount = e.WorkBoundsCount, DependencyHash = value.EditorDependencyHashes[i]
                    };
                }
                var sources = new GraphicsBuffer[snapshot.BufferNames.Length];
                long largest = 0;
                for (int i = 0; i < sources.Length; i++)
                { sources[i] = GeometryBuffer(value, snapshot.BufferNames[i]); largest = Math.Max(largest, (long)sources[i].count * sources[i].stride); }
                if (!FitsCacheTransfer(snapshot.Bytes, largest))
                { geometryCacheStatus = "Cache transfer peak exceeds geometry budget"; return false; }
                // Camera and instance resources have already been detached by ReleaseAtlas.
                // Only one extra geometry buffer exists during each ownership transfer.
                value.EditorOwner.BeginAllocation(Guid.NewGuid().ToString("N"), plannedModels.ToArray());
                value.EditorCopyTransfer = new CacheCopyTransfer();
                value.EditorCopyCarriers = new GraphicsBuffer[sources.Length];
                geometryCacheDriverPeakBytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < sources.Length; i++)
                {
                    var carrier = value.EditorCopyCarriers[i] = value.EditorOwner.AllocateBuffer(sources[i].count, sources[i].stride, snapshot.BufferNames[i]);
                    value.EditorCopyTransfer.Copy(sources[i], carrier);
                    carrier.Dispose(); value.EditorCopyCarriers[i] = null;
                    sources[i].Dispose();
                    SetGeometryBuffer(value, snapshot.BufferNames[i], null);
                }
                geometryCacheCopyMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                geometryCacheCopyBytes = snapshot.Bytes;
                snapshot.ExportMilliseconds = geometryCacheExportMilliseconds = geometryCacheCopyMilliseconds;
                // Publish only after every ordered GPU copy has completed.
                value.EditorOwner.Payload = JsonUtility.ToJson(snapshot);
                geometryCacheStatus = "Retained complete geometry";
                return true;
            }
            catch (Exception e) { geometryCacheStatus = "Cache retention skipped: " + e.Message; return false; }
        }

        private static GraphicsBuffer GeometryBuffer(Atlas value, string name)
        {
            switch (name)
            {
                case "Vertices": return value.Vertices;
                case "VertexPositionUvColor": return value.VertexPositionUvColor;
                case "VertexNormalTangent": return value.VertexNormalTangent;
                case "VertexCold": return value.VertexCold;
                case "Indices": return value.Indices;
                case "Clusters": return value.Clusters;
                case "CullingData": return value.CullingData;
                case "ModelTable": return value.ModelTable;
                case "WorkBounds": return value.WorkBounds;
                default: throw new InvalidOperationException("Unknown geometry buffer " + name);
            }
        }

        private static void SetGeometryBuffer(Atlas value, string name, GraphicsBuffer buffer)
        {
            switch (name)
            {
                case "Vertices": value.Vertices = buffer; break;
                case "VertexPositionUvColor": value.VertexPositionUvColor = buffer; break;
                case "VertexNormalTangent": value.VertexNormalTangent = buffer; break;
                case "VertexCold": value.VertexCold = buffer; break;
                case "Indices": value.Indices = buffer; break;
                case "Clusters": value.Clusters = buffer; break;
                case "CullingData": value.CullingData = buffer; break;
                case "ModelTable": value.ModelTable = buffer; break;
                case "WorkBounds": value.WorkBounds = buffer; break;
                default: throw new InvalidOperationException("Unknown geometry buffer " + name);
            }
        }

        private static bool TryRestorePreparedGeometry()
        {
            geometryCacheHit = false;
            if (VirtualMeshVertexGpuRetirement.Bytes != 0) return false;
            var owner = VirtualMeshEditorGeometryCache.instance;
            if (!GeometryCacheEnabled || !VirtualMeshEditorGeometryCache.IsSupported)
            { owner.Clear(); return false; }
            if (string.IsNullOrEmpty(owner.Payload)) return false;
            GraphicsBuffer[] buffers = null;
            GraphicsBuffer[] carriers = null;
            var transfer = new CacheCopyTransfer();
            bool adopted = false;
            try
            {
                var data = JsonUtility.FromJson<CachedLayout>(owner.Payload);
                if (data == null || data.Version != GeometryCacheVersion || data.RuntimeAssembly != RuntimeAssemblyId ||
                    data.Device != CacheDevice || data.TransposeShaderHash != DependencyHash(Resources.Load<ComputeShader>("VirtualMeshVertexTranspose")) ||
                    data.Budget != planningBudget || data.RequestedSplit != planningSplitVertexStreams ||
                    data.RequestedGpuTranspose != planningGpuVertexTranspose || data.Split != planningSplitVertexStreams ||
                    data.Entries == null || data.Entries.Length != modelSet.Count || owner.Dependencies.Length != modelSet.Count ||
                    data.BufferNames == null || data.BufferNames.Length != owner.BufferCount)
                    throw new InvalidOperationException("Geometry cache layout, code or budget changed: " +
                        $"version={data?.Version}/{GeometryCacheVersion}, assembly={data?.RuntimeAssembly}/{RuntimeAssemblyId}, " +
                        $"device={data?.Device}/{CacheDevice}, budget={data?.Budget}/{planningBudget}, " +
                        $"split={data?.RequestedSplit}/{planningSplitVertexStreams}, gpu={data?.RequestedGpuTranspose}/{planningGpuVertexTranspose}, " +
                        $"models={data?.Entries?.Length}/{modelSet.Count}, dependencies={owner.Dependencies?.Length}, buffers={data?.BufferNames?.Length}/{owner.BufferCount}");
                var restoredEntries = new Dictionary<VirtualMeshModelAsset, Entry>();
                var restoredModels = new List<VirtualMeshModelAsset>();
                var hashes = new string[data.Entries.Length];
                for (int i = 0; i < data.Entries.Length; i++)
                {
                    var model = owner.Dependencies[i] as VirtualMeshModelAsset; var e = data.Entries[i];
                    if (model == null || !modelSet.Contains(model) || !model.IsValid || e.Id != i ||
                        e.Revision != model.Revision || e.CullingRevision != model.CullingRevision ||
                        e.VertexCount != model.Vertices.Length || e.IndexCount != model.Indices.Length ||
                        e.ClusterCount != model.Clusters.Length || e.SubmeshCount != model.SubMeshCount ||
                        e.DependencyHash != DependencyHash(model) || e.SubmeshCounts.Length != model.SubMeshCount ||
                        e.SubmeshStarts.Length != model.SubMeshCount || e.SubmeshWorkBounds.Length != model.SubMeshCount)
                        throw new InvalidOperationException("Geometry cache model revision or dependency changed");
                    restoredModels.Add(model); hashes[i] = e.DependencyHash;
                    restoredEntries.Add(model, new Entry {
                        Model = model, Id = e.Id, Revision = e.Revision, CullingRevision = e.CullingRevision, Ready = true,
                        UploadStream = 5, SubmeshStarts = e.SubmeshStarts, SubmeshCounts = e.SubmeshCounts,
                        SubmeshWorkBounds = e.SubmeshWorkBounds, WorkBoundsOffset = e.WorkBoundsOffset, WorkBoundsCount = e.WorkBoundsCount,
                        Data = new ModelData { VertexOffset = e.VertexOffset, IndexOffset = e.IndexOffset, ClusterOffset = e.ClusterOffset,
                            ClusterCount = (uint)e.ClusterCount, BoundsCenter = e.BoundsCenter }
                    });
                }
                if (!owner.ValidateNative(out string reason)) throw new InvalidOperationException(reason);
                long largest = 0;
                // Validate the entire manifest before consuming even the first carrier.
                carriers = owner.AcquireAll();
                foreach (var carrier in carriers) largest = Math.Max(largest, (long)carrier.count * carrier.stride);
                if (!FitsCacheTransfer(data.Bytes, largest))
                    throw new InvalidOperationException("Geometry cache transfer peak exceeds budget: " +
                        $"base={data.Bytes}, largest={largest}, budget={planningBudget}, driver={UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()}, deviceMB={SystemInfo.graphicsMemorySize}");
                owner.Payload = null; // Consumption is a transaction; a partial move is never reusable.
                owner.AllowPartialRelease = true;
                buffers = new GraphicsBuffer[owner.BufferCount];
                geometryCacheDriverPeakBytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < buffers.Length; i++)
                {
                    var carrier = carriers[i];
                    buffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Structured |
                        GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination, carrier.count, carrier.stride);
                    transfer.Copy(carrier, buffers[i]);
                    carrier.Dispose(); carriers[i] = null;
                    owner.ReleaseNativeBuffer(i);
                }
                owner.Payload = null;
                geometryCacheCopyMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                geometryCacheCopyBytes = geometryCacheReusedBytes = data.Bytes;
                geometryCacheExportMilliseconds = data.ExportMilliseconds;
                var restored = new Atlas { Revision = ++atlasRevision, Bytes = data.Bytes, VertexCount = data.VertexCount,
                    VertexPayloadBytes = data.VertexPayloadBytes, HasCullingData = data.HasCullingData,
                    SplitVertexStreams = data.Split, EditorOwner = owner, EditorDependencyHashes = hashes, EditorTransposeHash = data.TransposeShaderHash,
                    EditorBufferNames = new List<string>(data.BufferNames) };
                for (int i = 0; i < buffers.Length; i++)
                {
                    switch (data.BufferNames[i])
                    {
                        case "Vertices": restored.Vertices = buffers[i]; break;
                        case "VertexPositionUvColor": restored.VertexPositionUvColor = buffers[i]; break;
                        case "VertexNormalTangent": restored.VertexNormalTangent = buffers[i]; break;
                        case "VertexCold": restored.VertexCold = buffers[i]; break;
                        case "Indices": restored.Indices = buffers[i]; break;
                        case "Clusters": restored.Clusters = buffers[i]; break;
                        case "CullingData": restored.CullingData = buffers[i]; break;
                        case "ModelTable": restored.ModelTable = buffers[i]; break;
                        case "WorkBounds": restored.WorkBounds = buffers[i]; break;
                        default: throw new InvalidOperationException("Unknown cached buffer");
                    }
                }
                if (restored.Indices == null || restored.Clusters == null || restored.CullingData == null ||
                    restored.ModelTable == null || restored.WorkBounds == null ||
                    (data.Split ? restored.VertexPositionUvColor == null || restored.VertexNormalTangent == null || restored.VertexCold == null : restored.Vertices == null))
                    throw new InvalidOperationException("Incomplete cached buffer set");
                VirtualMeshVertexBindings.Ensure();
                entries.Clear(); foreach (var pair in restoredEntries) entries.Add(pair.Key, pair.Value);
                plannedModels.Clear(); plannedModels.AddRange(restoredModels);
                atlas = restored; adopted = true; geometryCacheHit = true;
                geometryCacheStatus = "Hit: prepared geometry restored by GPU copy; CPU upload skipped";
                return true;
            }
            catch (Exception e) {
                geometryCacheStatus = "Miss: " + e.Message;
                SessionState.SetString("Unity.VirtualMesh.GeometryCache.LastMiss", geometryCacheStatus);
                return false;
            }
            finally
            {
                if (!adopted)
                {
                    // A submission can throw after queueing a copy. Keep both sides
                    // alive until this copy's fence, independently of the upload fence.
                    var failedBuffers = buffers; var failedCarriers = carriers;
                    long retiringBytes = owner.NativeBytes;
                    if (buffers != null) foreach (var buffer in buffers) if (buffer != null) retiringBytes += (long)buffer.count * buffer.stride;
                    VirtualMeshVertexGpuRetirement.Retire(retiringBytes, () => transfer.CanDispose, () => {
                        VirtualMeshEditorGeometryCache.Detach(failedBuffers);
                        VirtualMeshEditorGeometryCache.Detach(failedCarriers);
                        transfer.Dispose(); owner.Clear();
                    });
                }
                else { VirtualMeshEditorGeometryCache.Detach(carriers); transfer.Dispose(); }
            }
        }

        internal static void InvalidatePreparedGeometryCache()
        {
            ReleaseAtlas();
            var owner = VirtualMeshEditorGeometryCache.instance;
            owner.Payload = null;
            if (VirtualMeshVertexGpuRetirement.Bytes == 0) owner.Clear();
            geometryCacheHit = false; geometryCacheStatus = "Explicitly invalidated";
            geometryCacheReusedBytes = geometryCacheCopyBytes = 0; geometryCacheCopyMilliseconds = 0;
        }
    }
}
#endif
