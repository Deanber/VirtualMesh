#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// Editor-session native geometry copy-carrier ownership. No disk cache or GPU pointers.
    /// Mesh buffers are Raw storage only; Graph copies back into ordinary Structured buffers for shader use.
    /// Graph owns the manifest, readiness, submission retirement and borrowed buffer wrappers.
    /// </summary>
    internal sealed class VirtualMeshEditorGeometryCache : ScriptableSingleton<VirtualMeshEditorGeometryCache>
    {
        [Serializable]
        private sealed class NativeRecord
        {
            public Mesh Mesh;
            public int Count, Stride;
            public string Label;
        }

        // Empty Payload means allocation/upload is incomplete and cannot be attached as ready.
        // Publish only after every immutable range is uploaded and all submission fences pass.
        [SerializeField] public string Payload;
        [SerializeField] public string Identity;
        [SerializeField] public bool AllowPartialRelease; // Import only; caller proves each copy complete before release.
        [SerializeField] public UnityEngine.Object[] Dependencies = Array.Empty<UnityEngine.Object>();
        [SerializeField] private List<NativeRecord> records = new List<NativeRecord>();

        public static bool IsSupported => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 &&
            SystemInfo.supportsAsyncGPUReadback && SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute;
        public int BufferCount => records?.Count ?? 0;
        public long NativeBytes
        {
            get
            {
                long value = 0;
                if (records != null) foreach (var record in records) if (record != null && record.Mesh != null) value = checked(value + (long)record.Count * record.Stride);
                return value;
            }
        }

        // No FilePath, Save or OnDisable cleanup. Serialized Mesh/Object references survive
        // assembly reload; returned GraphicsBuffer wrappers do not participate in serialization.
        [InitializeOnLoadMethod]
        private static void InstallExitCleanup()
        {
            EditorApplication.quitting -= ClearOnEditorExit;
            EditorApplication.quitting += ClearOnEditorExit;
        }

        private static void ClearOnEditorExit()
        {
            // Do not instantiate an unused singleton during shutdown.
            foreach (var owner in Resources.FindObjectsOfTypeAll<VirtualMeshEditorGeometryCache>())
                if (owner != null) owner.Clear();
        }

        /// <summary>
        /// Starts a new allocation epoch. Caller must first detach/retire all previous GPU users.
        /// Never call while the old cache or a partially allocated epoch has submitted work in flight.
        /// </summary>
        public void BeginAllocation(string identity, UnityEngine.Object[] dependencies)
        {
            if (!IsSupported) throw new NotSupportedException("Native raw-copy Mesh cache requires the validated Direct3D12 backend.");
            Clear();
            hideFlags = HideFlags.HideAndDontSave;
            Identity = string.IsNullOrEmpty(identity) ? Guid.NewGuid().ToString("N") : identity;
            name = "Virtual Mesh Geometry Cache " + Identity;
            Dependencies = dependencies == null ? Array.Empty<UnityEngine.Object>() : (UnityEngine.Object[])dependencies.Clone();
        }

        /// <summary>
        /// Allocates a Raw+Copy storage buffer owned by a hidden native Mesh. Never bind it as a Structured SRV/UAV. The caller owns the
        /// returned wrapper and must Dispose it at detach/reload; doing so does not destroy Mesh.
        /// All allocation must finish before recording any upload/dispatch into this epoch.
        /// </summary>
        public GraphicsBuffer AllocateBuffer(int count, int stride, string label)
        {
            return AllocateBuffer(count, stride, label, out _);
        }

        public GraphicsBuffer AllocateBuffer(int count, int stride, string label, out Mesh nativeOwner)
        {
            nativeOwner = null;
            // Invalid API use must not clear a previously published cache.
            RequireAllocation(count, stride);
            Mesh mesh = null;
            GraphicsBuffer wrapper = null;
            try
            {
                mesh = new Mesh { name = "VM Cache " + label, hideFlags = HideFlags.HideAndDontSave };
                mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
                mesh.SetVertexBufferParams(count, Attributes(stride));
                // No giant managed zero-fill array is created. The immutable payload is filled
                // by Graph's normal bit-preserving uploader before Payload may be published.
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
                mesh.UploadMeshData(true); // Upload/release native CPU mesh storage first.
                RecordMesh(mesh, count, stride, label);
                wrapper = mesh.GetVertexBuffer(0); // Validation wrapper is already disposed.
                ValidateWrapper(wrapper, count, stride, label);
                nativeOwner = mesh;
                return wrapper;
            }
            catch
            {
                wrapper?.Dispose();
                if (mesh != null && !Contains(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
                // Allocation is a transaction: do not retain a half-allocated native cache.
                // This is safe under the allocation-before-any-submission contract above.
                Clear();
                throw;
            }
        }

        /// <summary>
        /// Takes ownership of a fully configured, non-asset, single-stream Mesh after validation.
        /// On failure the caller still owns this Mesh. Use AllocateBuffer for normal construction.
        /// </summary>
        public int RecordMesh(Mesh mesh, int count, int stride, string label)
        {
            RequireAllocation(count, stride);
            if (mesh == null || EditorUtility.IsPersistent(mesh)) throw new ArgumentException("Only a newly created non-asset Mesh can be owned by the session cache.", nameof(mesh));
            if (Contains(mesh)) throw new InvalidOperationException("Mesh is already owned by this cache.");
            if (mesh.isReadable || mesh.vertexCount != count || mesh.vertexBufferCount != 1 || mesh.GetVertexBufferStride(0) != stride)
                throw new InvalidOperationException("Mesh must already be uploaded, CPU copy discarded, and match the declared single-stream layout.");
            using (var check = mesh.GetVertexBuffer(0)) ValidateWrapper(check, count, stride, label);
            mesh.hideFlags = HideFlags.HideAndDontSave;
            records ??= new List<NativeRecord>();
            records.Add(new NativeRecord { Mesh = mesh, Count = count, Stride = stride, Label = label });
            return records.Count - 1;
        }

        private bool Contains(Mesh mesh)
        {
            if (records != null) foreach (var record in records) if (record != null && record.Mesh == mesh) return true;
            return false;
        }

        private void RequireAllocation(int count, int stride)
        {
            if (!IsSupported) throw new NotSupportedException("This session copy-carrier backend has only been validated for Direct3D12.");
            if (string.IsNullOrEmpty(Identity)) throw new InvalidOperationException("Call BeginAllocation before allocating native geometry.");
            if (!string.IsNullOrEmpty(Payload)) throw new InvalidOperationException("A published cache is immutable; start a new allocation epoch before changing it.");
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (stride <= 0 || stride % 4 != 0 || stride > 128) throw new ArgumentOutOfRangeException(nameof(stride), "This carrier supports up to eight float4 TexCoord attributes.");
            _ = checked((long)count * stride);
        }

        private static VertexAttributeDescriptor[] Attributes(int stride)
        {
            var result = new List<VertexAttributeDescriptor>();
            for (int words = stride / 4, attribute = 0; words > 0; attribute++)
            {
                int width = Math.Min(4, words);
                result.Add(new VertexAttributeDescriptor((VertexAttribute)((int)VertexAttribute.TexCoord0 + attribute), VertexAttributeFormat.Float32, width, 0));
                words -= width;
            }
            return result.ToArray();
        }

        private static void ValidateWrapper(GraphicsBuffer buffer, int count, int stride, string label)
        {
            if (buffer == null || !buffer.IsValid()) throw new InvalidOperationException("Missing native buffer: " + label);
            const GraphicsBuffer.Target required = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            if (buffer.count != count || buffer.stride != stride || (buffer.target & required) != required || (buffer.target & GraphicsBuffer.Target.Structured) != 0)
                throw new InvalidOperationException($"Native buffer contract changed: {label}; count={buffer.count}, stride={buffer.stride}, target={buffer.target}");
        }

        /// <summary>
        /// Validates native owners/layout only. It cannot validate payload bytes, source versions,
        /// outstanding fences or device-loss preservation; Graph must validate the manifest too.
        /// </summary>
        public bool ValidateNative(out string reason)
        {
            reason = null;
            if (!IsSupported) { reason = "Backend is not Direct3D12."; return false; }
            if (records == null || records.Count == 0) { reason = "No native geometry owners."; return false; }
            try
            {
                foreach (var record in records)
                {
                    if (record == null) throw new InvalidOperationException("Native cache is partially imported/released.");
                    if (record.Mesh == null || record.Mesh.isReadable || record.Mesh.vertexCount != record.Count ||
                        record.Mesh.vertexBufferCount != 1 || record.Mesh.GetVertexBufferStride(0) != record.Stride)
                        throw new InvalidOperationException("Native Mesh owner/layout changed: " + record.Label);
                    using (var wrapper = record.Mesh.GetVertexBuffer(0)) ValidateWrapper(wrapper, record.Count, record.Stride, record.Label);
                }
                return true;
            }
            catch (Exception error) { reason = error.Message; return false; }
        }

        /// <summary>Fresh copy-only wrapper for one retained owner; supports sequential import.</summary>
        public GraphicsBuffer TakeBuffer(int index)
        {
            if (!IsSupported) throw new NotSupportedException("Copy carrier backend is not supported.");
            if (index < 0 || index >= BufferCount) throw new ArgumentOutOfRangeException(nameof(index));
            var record = records[index];
            if (record == null || record.Mesh == null) throw new InvalidOperationException("Native buffer was already released or lost.");
            var wrapper = record.Mesh.GetVertexBuffer(0);
            try { ValidateWrapper(wrapper, record.Count, record.Stride, record.Label); return wrapper; }
            catch { wrapper?.Dispose(); throw; }
        }

        /// <summary>
        /// Consumes one carrier after import. The caller must complete its Graphics.CopyBuffer
        /// (supported fence or ordered readback) and dispose its wrapper before this call.
        /// Index positions remain stable; ValidateNative deliberately rejects any partial cache.
        /// </summary>
        public void ReleaseNativeBuffer(int index)
        {
            if (!AllowPartialRelease) throw new InvalidOperationException("Enable AllowPartialRelease only inside a validated import transaction.");
            if (index < 0 || index >= BufferCount) throw new ArgumentOutOfRangeException(nameof(index));
            var record = records[index];
            if (record == null) return;
            if (record.Mesh != null) UnityEngine.Object.DestroyImmediate(record.Mesh);
            records[index] = null;
        }

        /// <summary>Returns fresh wrappers in original allocation order. Caller owns every wrapper.</summary>
        public GraphicsBuffer[] AcquireAll()
        {
            if (!ValidateNative(out string reason)) throw new InvalidOperationException(reason);
            var result = new GraphicsBuffer[records.Count];
            try
            {
                for (int index = 0; index < result.Length; index++)
                {
                    var record = records[index];
                    result[index] = record.Mesh.GetVertexBuffer(0);
                    ValidateWrapper(result[index], record.Count, record.Stride, record.Label);
                }
                return result;
            }
            catch { Detach(result); throw; }
        }

        /// <summary>Releases borrowed wrappers only; all native Mesh owners and metadata remain.</summary>
        public static void Detach(GraphicsBuffer[] wrappers)
        {
            if (wrappers == null) return;
            foreach (var buffer in wrappers) buffer?.Dispose();
        }

        /// <summary>Caller must first retire/detach all GPU users. Never use for routine Play detach.</summary>
        public void Clear()
        {
            Payload = null; Identity = null; AllowPartialRelease = false; Dependencies = Array.Empty<UnityEngine.Object>();
            if (records == null) { records = new List<NativeRecord>(); return; }
            foreach (var record in records) if (record != null && record.Mesh != null) UnityEngine.Object.DestroyImmediate(record.Mesh);
            records.Clear();
        }
    }
}
#endif
