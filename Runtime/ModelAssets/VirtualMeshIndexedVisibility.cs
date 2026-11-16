using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Optional atlas remapping and one scratch allocation shared by sequential visibility draws.</summary>
    internal sealed class VirtualMeshIndexedVisibility : IDisposable
    {
        internal const int IndicesPerCluster = 192;
        internal const int CanonicalWordsPerCluster = IndicesPerCluster / 4;
        internal const int MaterialSlotCount = 64;
        internal const int DiagnosticsWordCount = 64 * 2 * 4;
        internal const long AuxiliaryBytes = ((5 + 4 + 3) * MaterialSlotCount + DiagnosticsWordCount) * sizeof(uint);
        private const int UploadClusterCount = 4096;
        private const int UploadChunkBytes = UploadClusterCount * CanonicalWordsPerCluster * sizeof(uint);
        private CanonicalPreparation preparation;
        private int preparedClustersSnapshot, lastUploadGeneration = int.MinValue;
        private string failure;
        private bool disposed;

        public GraphicsBuffer CanonicalCorners { get; private set; }
        public GraphicsBuffer Indices { get; private set; }
        public GraphicsBuffer IndexedArguments { get; private set; }
        public GraphicsBuffer FallbackArguments { get; private set; }
        public GraphicsBuffer DispatchArguments { get; private set; }
        public GraphicsBuffer Diagnostics { get; private set; }
        public int CapacityClusters { get; private set; }
        public int AtlasRevision { get; private set; }
        public long Bytes { get; private set; }
        public int TotalClusters { get; private set; }
        public int PreparedClusters => preparation == null ? preparedClustersSnapshot : Volatile.Read(ref preparation.PreparedClusters);
        public int UploadedClusters { get; private set; }
        public string Failure => failure ?? (preparation == null ? null : Volatile.Read(ref preparation.Failure));
        public bool Ready => !disposed && Failure == null && UploadedClusters == TotalClusters &&
            preparation != null && Volatile.Read(ref preparation.Completed);
        public int QueuedChunks => preparation?.Chunks.Count ?? 0;

        // Snapshots contain only managed arrays. No Unity object or graphics resource enters the worker.
        internal readonly struct CanonicalSource
        {
            internal readonly uint[] Indices;
            internal readonly VirtualMeshModelCluster[] Clusters;
            internal readonly int[] SubmeshStarts;
            internal readonly int AtlasClusterOffset;
            internal CanonicalSource(uint[] indices, VirtualMeshModelCluster[] clusters, int[] submeshStarts, int atlasClusterOffset)
            {
                Indices = indices; Clusters = clusters; SubmeshStarts = (int[])submeshStarts.Clone();
                AtlasClusterOffset = atlasClusterOffset;
            }
        }

        private readonly struct CanonicalChunk
        {
            internal readonly uint[] Words;
            internal readonly int ClusterOffset, ClusterCount;
            internal CanonicalChunk(uint[] words, int clusterOffset, int clusterCount)
            { Words = words; ClusterOffset = clusterOffset; ClusterCount = clusterCount; }
        }

        private sealed class CanonicalPreparation
        {
            internal readonly BlockingCollection<CanonicalChunk> Chunks = new BlockingCollection<CanonicalChunk>(2);
            // Reserve before renting/generating: at most two chunk arrays, including in-flight uploads.
            internal readonly SemaphoreSlim Slots = new SemaphoreSlim(2, 2);
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task Worker = Task.CompletedTask;
            internal int PreparedClusters;
            internal bool Completed;
            internal string Failure;

            internal void Return(CanonicalChunk chunk)
            {
                ArrayPool<uint>.Shared.Return(chunk.Words);
                Slots.Release();
            }

            internal void CancelAndRelease()
            {
                Cancellation.Cancel();
                // Never wait for a producer on the rendering thread. Cleanup only touches managed state.
                Worker.ContinueWith(_ =>
                {
                    while (Chunks.TryTake(out var chunk)) Return(chunk);
                    Chunks.Dispose(); Slots.Dispose(); Cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        internal static VirtualMeshIndexedVisibility Create(int clusterCount, int capacityClusters, int atlasRevision)
        {
            if (clusterCount <= 0 || capacityClusters <= 0) throw new ArgumentOutOfRangeException();
            var resources = new VirtualMeshIndexedVisibility();
            try
            {
                resources.CanonicalCorners = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    checked(clusterCount * CanonicalWordsPerCluster), sizeof(uint));
                resources.Indices = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw,
                    checked(capacityClusters * IndicesPerCluster), sizeof(uint));
                resources.IndexedArguments = CreateArguments(5 * MaterialSlotCount);
                resources.FallbackArguments = CreateArguments(4 * MaterialSlotCount);
                resources.DispatchArguments = CreateArguments(3 * MaterialSlotCount);
                resources.Diagnostics = new GraphicsBuffer(GraphicsBuffer.Target.Structured, DiagnosticsWordCount, sizeof(uint));
                resources.Diagnostics.SetData(new uint[DiagnosticsWordCount]);
                resources.CapacityClusters = capacityClusters;
                resources.AtlasRevision = atlasRevision;
                resources.TotalClusters = clusterCount;
                resources.Bytes = (long)clusterCount * CanonicalWordsPerCluster * sizeof(uint) +
                    (long)capacityClusters * IndicesPerCluster * sizeof(uint) + AuxiliaryBytes;
                return resources;
            }
            catch
            {
                resources.Dispose();
                throw;
            }
        }

        private static GraphicsBuffer CreateArguments(int wordCount)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                wordCount, sizeof(uint));
            try { buffer.SetData(new uint[wordCount]); }
            catch { buffer.Dispose(); throw; }
            return buffer;
        }

        internal void BeginCanonicalPreparation(List<CanonicalSource> sources)
        {
            if (disposed || preparation != null) throw new InvalidOperationException("Canonical preparation already started or disposed.");
            var snapshot = sources.ToArray();
            var state = new CanonicalPreparation();
            int totalClusters = TotalClusters;
            preparation = state;
            state.Worker = Task.Run(() => PrepareCanonical(snapshot, state, totalClusters));
        }

        private static void PrepareCanonical(CanonicalSource[] sources, CanonicalPreparation state, int totalClusters)
        {
            var cancellation = state.Cancellation.Token;
            try
            {
                var firstCorners = new Dictionary<uint, byte>(IndicesPerCluster);
                Array.Sort(sources, (a, b) => a.AtlasClusterOffset.CompareTo(b.AtlasClusterOffset));
                int expectedOffset = 0;
                foreach (var source in sources)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (source.AtlasClusterOffset != expectedOffset)
                        throw new ArgumentException("Canonical source offsets do not cover the atlas contiguously.");
                    expectedOffset = checked(expectedOffset + source.Clusters.Length);
                    var order = BuildStableClusterOrder(source.Clusters, source.SubmeshStarts, cancellation);
                    for (int first = 0; first < order.Length; first += UploadClusterCount)
                    {
                        state.Slots.Wait(cancellation);
                        uint[] words = null;
                        bool queued = false;
                        try
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int count = Math.Min(UploadClusterCount, order.Length - first);
                            words = ArrayPool<uint>.Shared.Rent(UploadClusterCount * CanonicalWordsPerCluster);
                            FillCanonicalCorners(source.Indices, source.Clusters, order, first, count, words, firstCorners, cancellation);
                            cancellation.ThrowIfCancellationRequested();
                            Interlocked.Add(ref state.PreparedClusters, count);
                            state.Chunks.Add(new CanonicalChunk(words, checked(source.AtlasClusterOffset + first), count), cancellation);
                            queued = true; // Ownership passes to the upload queue.
                        }
                        finally
                        {
                            if (!queued)
                            {
                                if (words != null) ArrayPool<uint>.Shared.Return(words);
                                state.Slots.Release();
                            }
                        }
                    }
                }
                if (expectedOffset != totalClusters)
                    throw new ArgumentException("Canonical source count does not match the atlas.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) { Volatile.Write(ref state.Failure, exception.Message); }
            finally { state.Chunks.CompleteAdding(); Volatile.Write(ref state.Completed, true); }
        }

        // SetData cannot be interrupted; 2 ms is a scheduling budget and 4 MiB is a hard byte cap.
        internal void PumpCanonicalUploads(int generation)
        {
            if (disposed || preparation == null || lastUploadGeneration == generation || Ready) return;
            lastUploadGeneration = generation;
            if (Failure != null) return;
            var state = preparation;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2 / 1000;
            int bytes = 0;
            try
            {
                while (bytes + UploadChunkBytes <= 4 * 1024 * 1024 && Stopwatch.GetTimestamp() < deadline &&
                    state.Chunks.TryTake(out var chunk))
                {
                    try
                    {
                        int words = chunk.ClusterCount * CanonicalWordsPerCluster;
                        CanonicalCorners.SetData(chunk.Words, 0, checked(chunk.ClusterOffset * CanonicalWordsPerCluster), words);
                        UploadedClusters += chunk.ClusterCount;
                        bytes += words * sizeof(uint);
                    }
                    finally { state.Return(chunk); }
                }
                if (state.Chunks.IsCompleted && UploadedClusters != TotalClusters && Failure == null)
                    failure = "Canonical preparation did not cover the complete atlas.";
            }
            catch (Exception exception) { failure = exception.Message; }
        }

        // Match PrepareClusterUpload's stable submesh grouping, including legacy unsorted assets.
        internal static int[] BuildStableClusterOrder(VirtualMeshModelCluster[] clusters, int[] submeshStarts,
            CancellationToken cancellation = default)
        {
            var next = (int[])submeshStarts.Clone();
            var counts = new int[next.Length];
            for (int i = 0; i < clusters.Length; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var cluster = clusters[i];
                if (cluster.submesh >= counts.Length) throw new ArgumentException("Invalid cluster material slot.");
                counts[cluster.submesh]++;
            }
            int expected = 0;
            for (int slot = 0; slot < counts.Length; slot++)
            {
                if (next[slot] != expected) throw new ArgumentException("Cluster offsets do not match the atlas.");
                expected = checked(expected + counts[slot]);
            }
            var order = new int[clusters.Length];
            for (int i = 0; i < clusters.Length; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                order[next[clusters[i].submesh]++] = i;
            }
            return order;
        }

        // Padding maps to source corner zero; mirror compensation belongs to index generation.
        internal static void FillCanonicalCorners(uint[] indices, VirtualMeshModelCluster[] clusters,
            int[] order, int first, int count, uint[] words, Dictionary<uint, byte> firstCorners,
            CancellationToken cancellation = default)
        {
            if (first < 0 || count < 0 || first > order.Length - count ||
                words.Length < (long)count * CanonicalWordsPerCluster)
                throw new ArgumentOutOfRangeException();
            Array.Clear(words, 0, count * CanonicalWordsPerCluster);
            for (int local = 0; local < count; local++)
            {
                if ((local & 63) == 0) cancellation.ThrowIfCancellationRequested();
                var cluster = clusters[order[first + local]];
                if (cluster.indexCount == 0 || cluster.indexCount > IndicesPerCluster || cluster.indexCount % 3 != 0 ||
                    (ulong)cluster.indexStart + cluster.indexCount > (ulong)indices.Length)
                    throw new ArgumentException("Invalid cluster index range.");
                firstCorners.Clear();
                for (int corner = 0; corner < cluster.indexCount; corner++)
                {
                    uint vertex = indices[(int)cluster.indexStart + corner];
                    if (!firstCorners.TryGetValue(vertex, out byte canonical))
                    {
                        canonical = (byte)corner;
                        firstCorners.Add(vertex, canonical);
                    }
                    int word = local * CanonicalWordsPerCluster + corner / 4;
                    words[word] |= (uint)canonical << ((corner & 3) * 8);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (preparation != null)
            {
                preparedClustersSnapshot = Volatile.Read(ref preparation.PreparedClusters);
                failure ??= Volatile.Read(ref preparation.Failure);
                preparation.CancelAndRelease();
                preparation = null;
            }
            CanonicalCorners?.Dispose(); CanonicalCorners = null;
            Indices?.Dispose(); Indices = null;
            IndexedArguments?.Dispose(); IndexedArguments = null;
            FallbackArguments?.Dispose(); FallbackArguments = null;
            DispatchArguments?.Dispose(); DispatchArguments = null;
            Diagnostics?.Dispose(); Diagnostics = null;
            Bytes = 0;
            CapacityClusters = 0;
        }
    }
}
