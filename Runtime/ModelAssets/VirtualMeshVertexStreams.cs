using System;
using System.Runtime.InteropServices;
#if !VM_VERTEX_STREAMS_CPU_ONLY
using UnityEngine;
#endif

namespace Unity.VirtualMesh.Runtime
{
    // Uint CPU fields preserve every source float bit, including signed zero and
    // NaN payloads. HLSL reads the same raw bytes as float fields.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct VertexHot24
    {
        public uint PositionX, PositionY, PositionZ, Uv0X, Uv0Y, ColorR;
        public const int Stride = 24;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct VertexNormal28
    {
        public uint NormalX, NormalY, NormalZ, TangentX, TangentY, TangentZ, TangentW;
        public const int Stride = 28;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct VertexCold20
    {
        public uint Uv1X, Uv1Y, ColorG, ColorB, ColorA;
        public const int Stride = 20;
    }

    internal static class VirtualMeshVertexStreamPacking
    {
        internal const int ChunkVertices = 4096;
        internal const int SourceStride = 72;
        internal const int ScratchBytes = ChunkVertices * SourceStride;

        internal static void Split(ReadOnlySpan<uint> sourceWords, int count,
            Span<VertexHot24> hot, Span<VertexNormal28> normal, Span<VertexCold20> cold)
        {
            if (count < 0 || sourceWords.Length < checked(count * 18) || hot.Length < count || normal.Length < count || cold.Length < count)
                throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                int s = i * 18;
                hot[i] = new VertexHot24 {
                    PositionX = sourceWords[s], PositionY = sourceWords[s + 1], PositionZ = sourceWords[s + 2],
                    Uv0X = sourceWords[s + 10], Uv0Y = sourceWords[s + 11], ColorR = sourceWords[s + 14]
                };
                normal[i] = new VertexNormal28 {
                    NormalX = sourceWords[s + 3], NormalY = sourceWords[s + 4], NormalZ = sourceWords[s + 5],
                    TangentX = sourceWords[s + 6], TangentY = sourceWords[s + 7], TangentZ = sourceWords[s + 8], TangentW = sourceWords[s + 9]
                };
                cold[i] = new VertexCold20 {
                    Uv1X = sourceWords[s + 12], Uv1Y = sourceWords[s + 13],
                    ColorG = sourceWords[s + 15], ColorB = sourceWords[s + 16], ColorA = sourceWords[s + 17]
                };
            }
        }
    }

#if !VM_VERTEX_STREAMS_CPU_ONLY
    /// <summary>Bounded, reusable CPU staging; never holds another model-sized copy.</summary>
    internal sealed class VirtualMeshVertexStreamScratch
    {
        readonly VertexHot24[] hot = new VertexHot24[VirtualMeshVertexStreamPacking.ChunkVertices];
        readonly VertexNormal28[] normal = new VertexNormal28[VirtualMeshVertexStreamPacking.ChunkVertices];
        readonly VertexCold20[] cold = new VertexCold20[VirtualMeshVertexStreamPacking.ChunkVertices];

        internal void Upload(VirtualMeshGraphRuntime.Atlas atlas, VirtualMeshModelVertex[] source,
            int sourceElement, int destinationElement, int count)
        {
            var words = MemoryMarshal.Cast<VirtualMeshModelVertex, uint>(source.AsSpan(sourceElement, count));
            VirtualMeshVertexStreamPacking.Split(words, count, hot, normal, cold);
            atlas.VertexPositionUvColor.SetData(hot, 0, destinationElement, count);
            atlas.VertexNormalTangent.SetData(normal, 0, destinationElement, count);
            atlas.VertexCold.SetData(cold, 0, destinationElement, count);
            // The caller advances the cursor only after all three calls succeed.
        }
    }

    // Optional cold-upload accelerator. All uploads and dispatches execute on
    // the graphics queue; this class never enables async compute execution.
    internal sealed class VirtualMeshVertexGpuScratch : IDisposable
    {
        internal const int Capacity = (2 * 1024 * 1024) / VirtualMeshModelVertex.Stride;
        internal const long Bytes = (long)Capacity * VirtualMeshModelVertex.Stride;
        readonly GraphicsBuffer source;
        readonly ComputeShader shader;
        readonly int kernel;
        readonly UnityEngine.Rendering.CommandBuffer commands;
        UnityEngine.Rendering.GraphicsFence lastFence;
        bool submitted, recorded;
        internal long SubmittedBytes { get; private set; }
        internal long SubmittedChunks { get; private set; }
        internal long PendingBytes { get; private set; }
        internal long RecordTicks { get; private set; }
        internal bool CanDispose
        {
            get
            {
                if (!submitted) return true;
                // An execution/query failure cannot prove that queued GPU work
                // stopped using these buffers. Retain its retirement budget.
                try { return lastFence.passed; }
                catch { return false; }
            }
        }
        internal long InFlightBytes => CanDispose ? 0 : lastSubmissionBytes;
        long lastSubmissionBytes;

        internal VirtualMeshVertexGpuScratch()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsGraphicsFence || !SystemInfo.supportsAsyncCompute)
                throw new NotSupportedException("GPU vertex transpose requires compute and supported graphics fences; this backend must support AsyncQueueSynchronisation fence polling.");
            var resource = Resources.Load<ComputeShader>("VirtualMeshVertexTranspose");
            if (resource == null) throw new InvalidOperationException("GPU vertex transpose compute shader is unavailable.");
            try
            {
                shader = UnityEngine.Object.Instantiate(resource);
                shader.hideFlags = HideFlags.HideAndDontSave;
                kernel = shader.FindKernel("Transpose");
                source = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, VirtualMeshModelVertex.Stride);
                commands = new UnityEngine.Rendering.CommandBuffer { name = "VirtualMesh.Graph.VertexTranspose" };
            }
            catch { Dispose(); throw; }
        }

        internal void Record(VirtualMeshGraphRuntime.Atlas atlas, VirtualMeshModelVertex[] data,
            int sourceElement, int destinationElement, int count)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Recording the upload alongside its dispatch is essential:
                // immediate SetData could overwrite scratch before prior dispatches.
                commands.SetBufferData(source, data, sourceElement, 0, count);
                commands.SetComputeIntParam(shader, "_Count", count);
                commands.SetComputeIntParam(shader, "_Destination", destinationElement);
                commands.SetComputeBufferParam(shader, kernel, "_Source", source);
                commands.SetComputeBufferParam(shader, kernel, "_Hot", atlas.VertexPositionUvColor);
                commands.SetComputeBufferParam(shader, kernel, "_Normal", atlas.VertexNormalTangent);
                commands.SetComputeBufferParam(shader, kernel, "_Cold", atlas.VertexCold);
                commands.DispatchCompute(shader, kernel, (count + 127) / 128, 1, 1);
                recorded = true;
                PendingBytes += (long)count * VirtualMeshModelVertex.Stride;
                SubmittedChunks++;
            }
            finally { RecordTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
        }

        internal void Submit(UnityEngine.Rendering.ScriptableRenderContext context)
        {
            if (!recorded) return;
            var fence = commands.CreateGraphicsFence(UnityEngine.Rendering.GraphicsFenceType.AsyncQueueSynchronisation,
                UnityEngine.Rendering.SynchronisationStageFlags.AllGPUOperations);
            // Preserve ownership before execution: a native call can fail after
            // queueing work, so its exception must never permit immediate disposal.
            lastFence = fence; submitted = true;
            lastSubmissionBytes = PendingBytes;
            // Submit private uploads directly on the graphics queue, independent
            // of a later context.Submit (URP may skip every camera). Ready/native
            // registration follows this call; this does not mean the GPU is done.
            // No async compute execution is enabled. Keep the context parameter
            // for caller compatibility; upload commands do not consume its state.
            Graphics.ExecuteCommandBuffer(commands);
            SubmittedBytes += PendingBytes;
            commands.Clear(); recorded = false; PendingBytes = 0;
        }

        internal void DiscardRecorded()
        { commands?.Clear(); recorded = false; PendingBytes = 0; }

        public void Dispose()
        {
            commands?.Release(); source?.Dispose();
            if (shader != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(shader);
                else UnityEngine.Object.DestroyImmediate(shader);
            }
        }
    }

    // Keep pending transfer resources alive across layout changes. A new atlas
    // may not consume this reserved GPU budget until the prior fence passes.
    internal static class VirtualMeshVertexGpuRetirement
    {
        sealed class Item { internal long Bytes; internal Func<bool> Ready; internal Action Dispose; }
        static readonly System.Collections.Generic.List<Item> items = new System.Collections.Generic.List<Item>();
        internal static long Bytes { get; private set; }
        static bool installed;
        internal static void Retire(long bytes, Func<bool> ready, Action dispose)
        {
            if (ready()) { dispose(); return; }
            items.Add(new Item { Bytes = bytes, Ready = ready, Dispose = dispose }); Bytes += bytes;
            if (installed) return;
            installed = true;
            UnityEngine.Rendering.RenderPipelineManager.beginContextRendering += OnContext;
            Application.quitting += Shutdown;
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.update += Pump;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            #endif
        }
        static void OnContext(UnityEngine.Rendering.ScriptableRenderContext context,
            System.Collections.Generic.List<Camera> cameras) => Pump();
        internal static void Pump()
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i]; if (!item.Ready()) continue;
                item.Dispose(); Bytes -= item.Bytes; items.RemoveAt(i);
            }
            if (items.Count == 0) Uninstall();
        }
        static void Uninstall()
        {
            if (!installed) return;
            installed = false;
            UnityEngine.Rendering.RenderPipelineManager.beginContextRendering -= OnContext;
            Application.quitting -= Shutdown;
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= Pump;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            #endif
        }
        static void Shutdown()
        {
            // Engine teardown/reload follows Unity's existing resource-disposal
            // contract; never spin-wait on a context that may not yet be submitted.
            foreach (var item in items) item.Dispose();
            items.Clear(); Bytes = 0; Uninstall();
        }
    }

    /// <summary>Valid inactive SRVs for the uniform layout branch; never a second atlas.</summary>
    internal static class VirtualMeshVertexBindings
    {
        internal const long RequiredBytes = 72 + 24 + 28 + 20;
        internal static GraphicsBuffer Legacy { get; private set; }
        internal static GraphicsBuffer PositionUvColor { get; private set; }
        internal static GraphicsBuffer NormalTangent { get; private set; }
        internal static GraphicsBuffer Cold { get; private set; }
        internal static long Bytes => Legacy != null ? RequiredBytes : 0;

        internal static void Ensure()
        {
            if (Legacy != null) return;
            if (Marshal.SizeOf<VirtualMeshModelVertex>() != VirtualMeshModelVertex.Stride ||
                Marshal.SizeOf<VertexHot24>() != VertexHot24.Stride || Marshal.SizeOf<VertexNormal28>() != VertexNormal28.Stride ||
                Marshal.SizeOf<VertexCold20>() != VertexCold20.Stride ||
                Marshal.OffsetOf<VirtualMeshModelVertex>(nameof(VirtualMeshModelVertex.normal)).ToInt32() != 12 ||
                Marshal.OffsetOf<VirtualMeshModelVertex>(nameof(VirtualMeshModelVertex.tangent)).ToInt32() != 24 ||
                Marshal.OffsetOf<VirtualMeshModelVertex>(nameof(VirtualMeshModelVertex.uv0)).ToInt32() != 40 ||
                Marshal.OffsetOf<VirtualMeshModelVertex>(nameof(VirtualMeshModelVertex.uv1)).ToInt32() != 48 ||
                Marshal.OffsetOf<VirtualMeshModelVertex>(nameof(VirtualMeshModelVertex.color)).ToInt32() != 56)
                throw new InvalidOperationException("Virtual Mesh vertex stream layout does not match the 72-byte source contract.");
            try
            {
                Legacy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VirtualMeshModelVertex.Stride);
                PositionUvColor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VertexHot24.Stride);
                NormalTangent = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VertexNormal28.Stride);
                Cold = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VertexCold20.Stride);
                Legacy.SetData(new VirtualMeshModelVertex[1]);
                PositionUvColor.SetData(new VertexHot24[1]);
                NormalTangent.SetData(new VertexNormal28[1]);
                Cold.SetData(new VertexCold20[1]);
            }
            catch { Release(); throw; }
        }

        internal static bool DiagnosticDynamicLayoutVariant; // Off by default; atlas layout is unchanged.

        // Renderer-owned local keywords. Call after every material property/keyword sync.
        internal static void SetLayoutKeyword(Material material, bool split, bool legacy = false)
        {
            const string splitKeyword = "VM_SPLIT_VERTEX_STREAMS";
            const string dynamicKeyword = "VM_DYNAMIC_VERTEX_STREAMS";
            bool wantDynamic = !legacy && DiagnosticDynamicLayoutVariant;
            bool wantSplit = !legacy && !wantDynamic && split;
            if (material.IsKeywordEnabled(dynamicKeyword) != wantDynamic)
            { if (wantDynamic) material.EnableKeyword(dynamicKeyword); else material.DisableKeyword(dynamicKeyword); }
            if (material.IsKeywordEnabled(splitKeyword) != wantSplit)
            { if (wantSplit) material.EnableKeyword(splitKeyword); else material.DisableKeyword(splitKeyword); }
        }

        internal static void BindAtlas(MaterialPropertyBlock properties, VirtualMeshGraphRuntime.Atlas atlas)
        {
            Ensure();
            properties.SetInt("_VMGeometryAtlasSplit", atlas.SplitVertexStreams ? 1 : 0);
            properties.SetBuffer("_VMVertices", atlas.SplitVertexStreams ? Legacy : atlas.Vertices);
            properties.SetBuffer("_VMGeometryAtlasPositionUvColor", atlas.SplitVertexStreams ? atlas.VertexPositionUvColor : PositionUvColor);
            properties.SetBuffer("_VMGeometryAtlasNormalTangent", atlas.SplitVertexStreams ? atlas.VertexNormalTangent : NormalTangent);
            properties.SetBuffer("_VMGeometryAtlasCold", atlas.SplitVertexStreams ? atlas.VertexCold : Cold);
        }

        internal static void BindLegacy(MaterialPropertyBlock properties, GraphicsBuffer vertices)
        {
            Ensure();
            properties.SetInt("_VMGeometryAtlasSplit", 0);
            properties.SetBuffer("_VMVertices", vertices);
            properties.SetBuffer("_VMGeometryAtlasPositionUvColor", PositionUvColor);
            properties.SetBuffer("_VMGeometryAtlasNormalTangent", NormalTangent);
            properties.SetBuffer("_VMGeometryAtlasCold", Cold);
        }

        internal static void Release()
        {
            Legacy?.Dispose(); PositionUvColor?.Dispose(); NormalTangent?.Dispose(); Cold?.Dispose();
            Legacy = PositionUvColor = NormalTangent = Cold = null;
        }
    }
#endif
}
