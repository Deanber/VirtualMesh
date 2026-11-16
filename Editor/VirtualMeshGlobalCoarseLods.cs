using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEngine;
using static MeshOptimizer.MeshOptimizerNative;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>Explicit opt-in for already verified opaque material slots. No material is inferred
    /// from a model asset: alpha cards need a separate coverage-preserving LOD policy.</summary>
    [Serializable]
    public sealed class GlobalCoarseLodSettings
    {
        public int[] Submeshes;
        public int MaxLevels = 16;
        public int MaxAdditionalLevels { get => MaxLevels; set => MaxLevels = value; }
        public int TargetTriangleCountPerSubmesh = 256;
        public float RelativeError = .005f;
        public float ErrorGrowth = 2f;
        public float MaxRelativeError = .25f;
        public float ColorWeight = .8f;
        public float MinimumReduction = .02f;
        public float DefaultPixelError = 1f;
        public float DefaultTargetTrianglePixels = 2f;
    }

    [Serializable]
    public sealed class GlobalCoarseLodSubmeshResult
    {
        public int Submesh, AddedLevels, OldRootClusters, NewRootClusters;
        // Removed from newly accepted simplified levels, not the original index prefix.
        public int RemovedDegenerateTriangles;
        public long OldRootTriangles, NewRootTriangles;
        public long RemainingTriangles;
        public int TargetTriangleCount;
        public bool TargetReached;
        public string StopReason;
        public VirtualMeshModelLodLevel[] Levels;
    }

    [Serializable]
    public sealed class GlobalCoarseLodBuildResult
    {
        public bool Updated, Reused;
        public int AlgorithmVersion, AddedLevels, OldClusterCount, NewClusterCount;
        public int RemovedDegenerateTriangles;
        public long OldRootTriangles, NewRootTriangles;
        public bool AllTargetsReached;
        public string SettingsHash;
        public GlobalCoarseLodSubmeshResult[] Submeshes;
    }

    /// <summary>Appends global LOD chains to published geometry without FBX import or native Player code.</summary>
    public static class VirtualMeshGlobalCoarseLods
    {
        // Included in the settings/cache identity. Never accept an older algorithm's output as current.
        public const int AlgorithmVersion = 3;
        const uint VertexStride = 72;
        const int MaxVertices = 120, MaxTriangles = 64;

        readonly struct PositionKey : IEquatable<PositionKey>
        {
            readonly int x, y, z;
            public PositionKey(Vector3 value)
            {
                // Signed zero is the same geometric position. Other finite floats
                // remain bit-exact; nearby, disconnected surfaces are not welded.
                x = value.x == 0 ? 0 : BitConverter.SingleToInt32Bits(value.x);
                y = value.y == 0 ? 0 : BitConverter.SingleToInt32Bits(value.y);
                z = value.z == 0 ? 0 : BitConverter.SingleToInt32Bits(value.z);
            }
            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((x * 397) ^ y) * 397 ^ z; }
            }
        }

        sealed class BuiltData
        {
            public uint[] Indices;
            public VirtualMeshModelCluster[] Clusters;
            public VirtualMeshModelClusterCullingData[] Culling;
            public VirtualMeshModelLodLevel[] Levels;
            public GlobalCoarseLodBuildResult Result;
        }

        /// <summary>Call from the editor thread. CPU/native work runs in a worker; only the final
        /// publication touches the supplied asset. Defaults to not saving, suitable for a model clone.</summary>
        public static async Task<GlobalCoarseLodBuildResult> BuildAsync(VirtualMeshModelAsset model,
            GlobalCoarseLodSettings settings = null, bool saveAsset = false,
            CancellationToken cancellationToken = default)
        {
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("Start a global coarse LOD build on the Unity editor thread.");
            cancellationToken.ThrowIfCancellationRequested();
            if (model == null || !model.IsValid) throw new ArgumentException("A valid published model is required.", nameof(model));
            var options = Snapshot(settings ?? new GlobalCoarseLodSettings(), model.SubMeshCount);
            string hash = SettingsHash(options);
            if (model.CoarseLodVersion != 0)
            {
                if (model.CoarseLodVersion != AlgorithmVersion || model.CoarseLodSettingsHash != hash)
                    throw new InvalidOperationException("This model already has a different global LOD build. Rebuild its original geometry before changing the global LOD settings.");
                return DescribeReused(model, hash, options);
            }
            int revision = model.Revision, submeshCount = model.SubMeshCount;
            var vertices = model.Vertices;
            var indices = model.Indices;
            var clusters = model.Clusters;
            var bounds = model.Bounds;
            var source = model.SourceMesh;
            string sourceHash = model.SourceHash;
            float sourceError = model.SimplificationError;
            var built = await Task.Run(() => BuildData(vertices, indices, clusters, submeshCount, bounds,
                options, hash, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (model == null || model.Revision != revision || model.CoarseLodVersion != 0 ||
                !ReferenceEquals(vertices, model.Vertices) || !ReferenceEquals(indices, model.Indices) ||
                !ReferenceEquals(clusters, model.Clusters))
                throw new InvalidOperationException("The model changed during its global LOD build. The computed result was not published.");
            // All geometry and derived arrays have been validated before changing the object.
            if (built.Result.Updated)
            {
                model.SetData(source, sourceError, sourceHash, vertices, built.Indices, built.Clusters, submeshCount, bounds);
                model.SetCullingData(built.Culling);
            }
            model.SetCoarseLodMetadata(AlgorithmVersion, hash, clusters.Length, built.Levels,
                options.DefaultPixelError, options.DefaultTargetTrianglePixels);
            EditorUtility.SetDirty(model);
            if (saveAsset && EditorUtility.IsPersistent(model)) AssetDatabase.SaveAssetIfDirty(model);
            return built.Result;
        }

        static GlobalCoarseLodSettings Snapshot(GlobalCoarseLodSettings source, int submeshCount)
        {
            var slots = (source.Submeshes ?? Enumerable.Range(0, submeshCount)).Distinct().OrderBy(i => i).ToArray();
            if (slots.Length == 0 || slots.Any(i => i < 0 || i >= submeshCount) ||
                source.MaxLevels < 1 || source.MaxLevels > 16 || source.TargetTriangleCountPerSubmesh < 1 ||
                !Finite(source.RelativeError) || source.RelativeError <= 0 || source.RelativeError > 1 ||
                !Finite(source.MaxRelativeError) || source.MaxRelativeError < source.RelativeError || source.MaxRelativeError > 1 ||
                !Finite(source.ErrorGrowth) || source.ErrorGrowth < 1 || source.ErrorGrowth > 4 ||
                !Finite(source.ColorWeight) || source.ColorWeight <= 0 || source.ColorWeight > 10 ||
                !Finite(source.MinimumReduction) || source.MinimumReduction < 0 || source.MinimumReduction >= .5f ||
                !Finite(source.DefaultPixelError) || source.DefaultPixelError <= 0 ||
                !Finite(source.DefaultTargetTrianglePixels) || source.DefaultTargetTrianglePixels < 0)
                throw new ArgumentException("Invalid global coarse LOD settings.");
            return new GlobalCoarseLodSettings { Submeshes = slots, MaxLevels = source.MaxLevels,
                TargetTriangleCountPerSubmesh = source.TargetTriangleCountPerSubmesh, MaxRelativeError = source.MaxRelativeError,
                RelativeError = source.RelativeError, ErrorGrowth = source.ErrorGrowth, ColorWeight = source.ColorWeight,
                MinimumReduction = source.MinimumReduction, DefaultPixelError = source.DefaultPixelError,
                DefaultTargetTrianglePixels = source.DefaultTargetTrianglePixels };
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static string SettingsHash(GlobalCoarseLodSettings settings)
        {
            // Float bit patterns and version form a culture-independent algorithm cache key.
            string key = AlgorithmVersion + ":" + settings.MaxLevels + ":" + settings.TargetTriangleCountPerSubmesh + ":" +
                string.Join(",", new[] { settings.RelativeError, settings.ErrorGrowth, settings.ColorWeight,
                    settings.MinimumReduction, settings.DefaultPixelError, settings.DefaultTargetTrianglePixels, settings.MaxRelativeError }
                    .Select(value => BitConverter.SingleToInt32Bits(value).ToString("x8"))) + ":" +
                string.Join(",", settings.Submeshes);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
        }

        static GlobalCoarseLodBuildResult DescribeReused(VirtualMeshModelAsset model, string hash, GlobalCoarseLodSettings options)
        {
            var rows = model.CoarseLodLevels.GroupBy(level => level.Submesh).Select(group =>
            {
                var ordered = group.OrderBy(level => level.Level).ToArray();
                return new GlobalCoarseLodSubmeshResult { Submesh = group.Key, AddedLevels = ordered.Length - 1,
                    OldRootClusters = ordered[0].ClusterCount, NewRootClusters = ordered[ordered.Length - 1].ClusterCount,
                    OldRootTriangles = ordered[0].TriangleCount, NewRootTriangles = ordered[ordered.Length - 1].TriangleCount,
                    RemainingTriangles = ordered[ordered.Length - 1].TriangleCount,
                    TargetTriangleCount = options.TargetTriangleCountPerSubmesh,
                    TargetReached = ordered[ordered.Length - 1].TriangleCount <= options.TargetTriangleCountPerSubmesh,
                    StopReason = "Reused", Levels = ordered };
            }).ToArray();
            return new GlobalCoarseLodBuildResult { Reused = true, AlgorithmVersion = AlgorithmVersion,
                SettingsHash = hash, AddedLevels = rows.Sum(row => row.AddedLevels),
                OldClusterCount = model.CoarseLodBaseClusterCount, NewClusterCount = model.Clusters.Length,
                OldRootTriangles = rows.Sum(row => row.OldRootTriangles), NewRootTriangles = rows.Sum(row => row.NewRootTriangles),
                AllTargetsReached = rows.All(row => row.TargetReached), Submeshes = rows };
        }

        static BuiltData BuildData(VirtualMeshModelVertex[] vertices, uint[] sourceIndices,
            VirtualMeshModelCluster[] sourceClusters, int submeshCount, Bounds bounds,
            GlobalCoarseLodSettings options, string hash, CancellationToken cancellationToken)
        {
            var changed = (VirtualMeshModelCluster[])sourceClusters.Clone();
            var appendIndices = new List<uint>();
            var appendClusters = new List<VirtualMeshModelCluster>();
            var levels = new List<VirtualMeshModelLodLevel>();
            var rows = new List<GlobalCoarseLodSubmeshResult>();
            Vector3 center = bounds.center, extents = bounds.extents;
            float radius = (float)Math.Sqrt((double)extents.x * extents.x + (double)extents.y * extents.y + (double)extents.z * extents.z);
            if (!Finite(radius) || !Finite(center.x) || !Finite(center.y) || !Finite(center.z))
                throw new InvalidDataException("The model has invalid bounds.");
            // Include every original submesh, including slots excluded from this build:
            // selected material borders must remain joined to their unchanged neighbors.
            var materialBoundaries = FindMaterialBoundaryPositions(vertices, sourceIndices,
                sourceClusters, submeshCount, cancellationToken);
            foreach (int slot in options.Submeshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootIds = new List<int>();
                double originalArea = 0;
                int rootIndexCount = 0;
                float accumulatedError = 0, spacing = 0;
                for (int i = 0; i < sourceClusters.Length; i++)
                {
                    var cluster = sourceClusters[i];
                    if (cluster.submesh != slot) continue;
                    if (cluster.type == 0 || cluster.type == 1) originalArea += Area(vertices, sourceIndices, cluster);
                    if (cluster.type != 0 && cluster.type != 3) continue;
                    rootIds.Add(i); rootIndexCount = checked(rootIndexCount + (int)cluster.indexCount);
                    accumulatedError = Math.Max(accumulatedError, cluster.error);
                    spacing = Math.Max(spacing, cluster.triangleSpacing);
                }
                if (rootIds.Count == 0) continue;
                if (originalArea == 0) foreach (int id in rootIds) originalArea += Area(vertices, sourceIndices, sourceClusters[id]);
                var row = new GlobalCoarseLodSubmeshResult { Submesh = slot, OldRootClusters = rootIds.Count,
                    NewRootClusters = rootIds.Count, OldRootTriangles = rootIndexCount / 3, NewRootTriangles = rootIndexCount / 3,
                    TargetTriangleCount = options.TargetTriangleCountPerSubmesh, StopReason = "Safety level limit" };
                int tableStart = levels.Count;
                levels.Add(Level(slot, 0, -1, rootIds.Count, rootIndexCount / 3, accumulatedError, spacing, options));
                WeldRoots(vertices, sourceIndices, sourceClusters, rootIds, rootIndexCount,
                    out var workingVertices, out var workingIndices, out var originalVertexIds, cancellationToken);
                var vertexLocks = BuildMaterialBoundaryLocks(vertices, originalVertexIds, materialBoundaries);
                float scale = PositionScale(workingVertices);
                if (!Finite(scale)) throw new InvalidDataException("The root surface has an invalid position scale.");
                int previousCount = rootIds.Count, previousAddedStart = -1;
                for (int attempt = 0; attempt < options.MaxLevels; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (workingIndices.Length / 3 <= options.TargetTriangleCountPerSubmesh)
                    { row.StopReason = "Triangle budget reached"; break; }
                    if (workingIndices.Length <= 3 || scale <= 0) { row.StopReason = "Minimal or degenerate surface"; break; }
                    float relativeError = (float)Math.Min(options.MaxRelativeError, options.RelativeError * Math.Pow(options.ErrorGrowth, attempt));
                    bool canIncreaseError = options.ErrorGrowth > 1 && relativeError < options.MaxRelativeError;
                    var simplified = new uint[workingIndices.Length];
                    float error;
                    int newCount = Simplify(workingIndices, workingVertices, vertexLocks, simplified,
                        checked((uint)(Math.Max(options.TargetTriangleCountPerSubmesh, workingIndices.Length / 6) * 3)),
                        relativeError, options.ColorWeight, out error);
                    if (!Finite(error) || error < 0) throw new InvalidDataException("Simplifier returned an invalid error.");
                    if (newCount <= 0 || newCount > workingIndices.Length || newCount % 3 != 0)
                    { row.StopReason = "Topology or attribute/error limit"; if (canIncreaseError) continue; break; }
                    int nativeCount = newCount;
                    newCount = FilterDegenerateTriangles(simplified, newCount, workingVertices);
                    // A collapsed surface must not become an empty LOD that drops all geometry.
                    if (newCount == 0) { row.StopReason = "No nondegenerate simplified surface"; break; }
                    if (newCount >= workingIndices.Length)
                    { row.StopReason = "Topology or attribute/error limit"; if (canIncreaseError) continue; break; }
                    Array.Resize(ref simplified, newCount);
                    if (newCount / 3 > options.TargetTriangleCountPerSubmesh &&
                        (double)(workingIndices.Length - newCount) / workingIndices.Length < options.MinimumReduction)
                    { row.StopReason = "Reduction below useful minimum"; if (canIncreaseError) continue; break; }
                    var meshlets = Meshlets(simplified, workingVertices);
                    if (meshlets.Length > previousCount)
                    { row.StopReason = "Cluster count increased"; if (canIncreaseError) continue; break; }
                    float nextError = RoundUp((double)accumulatedError + error * (double)scale);
                    float nextSpacing = Math.Max(spacing, (float)Math.Sqrt(originalArea / (newCount / 3)));
                    if (!Finite(nextError) || !Finite(nextSpacing) || nextError < accumulatedError || nextSpacing < spacing)
                        throw new InvalidDataException("Global LOD transitions must be finite and monotone.");
                    if (previousAddedStart < 0)
                    {
                        foreach (int id in rootIds)
                        {
                            var old = changed[id]; old.type = old.type == 0 ? 1u : 2u;
                            old.parentError = nextError; old.parentTriangleSpacing = nextSpacing; changed[id] = old;
                        }
                        // All descendants must use the same distance metric as their new global parent.
                        for (int i = 0; i < changed.Length; i++) if (changed[i].submesh == slot)
                        { var old = changed[i]; old.center = center; old.radius = radius; changed[i] = old; }
                    }
                    else
                        for (int i = previousAddedStart; i < appendClusters.Count; i++)
                        { var old = appendClusters[i]; old.type = 2; old.parentError = nextError; old.parentTriangleSpacing = nextSpacing; appendClusters[i] = old; }
                    var previousTable = levels[levels.Count - 1];
                    previousTable.ParentError = nextError; previousTable.ParentTriangleSpacing = nextSpacing;
                    levels[levels.Count - 1] = previousTable;
                    previousAddedStart = appendClusters.Count;
                    foreach (var meshlet in meshlets)
                    {
                        uint start = checked((uint)(sourceIndices.Length + appendIndices.Count));
                        foreach (uint vertex in meshlet) appendIndices.Add(originalVertexIds[vertex]);
                        appendClusters.Add(new VirtualMeshModelCluster { center = center, radius = radius,
                            indexStart = start, indexCount = (uint)meshlet.Length, submesh = (uint)slot, type = 3,
                            error = nextError, triangleSpacing = nextSpacing });
                    }
                    row.AddedLevels++;
                    row.RemovedDegenerateTriangles += (nativeCount - newCount) / 3;
                    levels.Add(Level(slot, row.AddedLevels, sourceClusters.Length + previousAddedStart,
                        meshlets.Length, newCount / 3, nextError, nextSpacing, options));
                    accumulatedError = nextError; spacing = nextSpacing; previousCount = meshlets.Length;
                    workingIndices = simplified; row.NewRootClusters = meshlets.Length; row.NewRootTriangles = newCount / 3;
                    if (newCount / 3 <= options.TargetTriangleCountPerSubmesh) { row.StopReason = "Triangle budget reached"; break; }
                }
                row.RemainingTriangles = row.NewRootTriangles;
                row.TargetReached = row.NewRootTriangles <= options.TargetTriangleCountPerSubmesh;
                row.Levels = levels.Skip(tableStart).ToArray(); rows.Add(row);
            }
            var result = new GlobalCoarseLodBuildResult { AlgorithmVersion = AlgorithmVersion, SettingsHash = hash,
                Updated = appendClusters.Count != 0, AddedLevels = rows.Sum(row => row.AddedLevels),
                OldClusterCount = sourceClusters.Length, NewClusterCount = checked(sourceClusters.Length + appendClusters.Count),
                OldRootTriangles = rows.Sum(row => row.OldRootTriangles), NewRootTriangles = rows.Sum(row => row.NewRootTriangles),
                RemovedDegenerateTriangles = rows.Sum(row => row.RemovedDegenerateTriangles),
                AllTargetsReached = rows.All(row => row.TargetReached), Submeshes = rows.ToArray() };
            var data = new BuiltData { Result = result, Levels = levels.ToArray(), Indices = sourceIndices, Clusters = sourceClusters };
            if (!result.Updated) return data;
            data.Indices = new uint[checked(sourceIndices.Length + appendIndices.Count)];
            Array.Copy(sourceIndices, data.Indices, sourceIndices.Length); appendIndices.CopyTo(data.Indices, sourceIndices.Length);
            data.Clusters = new VirtualMeshModelCluster[result.NewClusterCount];
            Array.Copy(changed, data.Clusters, changed.Length); appendClusters.CopyTo(data.Clusters, changed.Length);
            cancellationToken.ThrowIfCancellationRequested();
            data.Culling = VirtualMeshModelCullingData.Build(vertices, data.Indices, data.Clusters, cancellationToken);
            return data;
        }

        static VirtualMeshModelLodLevel Level(int slot, int level, int first, int count, int triangles,
            float error, float spacing, GlobalCoarseLodSettings options) => new VirtualMeshModelLodLevel
        { Submesh = slot, Level = level, FirstCluster = first, ClusterCount = count, TriangleCount = triangles,
            Error = error, TriangleSpacing = spacing, TransitionUnitsPerPixel = VirtualMeshModelCluster.TransitionThreshold(
                error, spacing, options.DefaultPixelError, options.DefaultTargetTrianglePixels) };

        static double Area(VirtualMeshModelVertex[] vertices, uint[] indices, VirtualMeshModelCluster cluster)
        {
            double result = 0;
            for (uint i = cluster.indexStart; i < cluster.indexStart + cluster.indexCount; i += 3)
            {
                var a = vertices[indices[i]].position; var b = vertices[indices[i + 1]].position; var c = vertices[indices[i + 2]].position;
                double x1 = (double)b.x - a.x, y1 = (double)b.y - a.y, z1 = (double)b.z - a.z;
                double x2 = (double)c.x - a.x, y2 = (double)c.y - a.y, z2 = (double)c.z - a.z;
                double x = y1 * z2 - z1 * y2, y = z1 * x2 - x1 * z2, z = x1 * y2 - y1 * x2;
                result += .5 * Math.Sqrt(x * x + y * y + z * z);
            }
            if (double.IsNaN(result) || double.IsInfinity(result)) throw new InvalidDataException("Invalid model triangle positions.");
            return result;
        }

        static float PositionScale(Vertex[] vertices)
        {
            var min = vertices[0].position; var max = min;
            foreach (var vertex in vertices) { min = Vector3.Min(min, vertex.position); max = Vector3.Max(max, vertex.position); }
            var size = max - min; return Math.Max(size.x, Math.Max(size.y, size.z));
        }

        // Attribute seams can retain different indices at the same position, and
        // simplification may leave collinear triangles. Remove only exact geometric
        // zeroes: an absolute epsilon would also erase valid small/thin geometry.
        // This edits only the new native result, never the published source prefix.
        static int FilterDegenerateTriangles(uint[] indices, int indexCount, Vertex[] vertices)
        {
            if (indexCount < 0 || indexCount > indices.Length || indexCount % 3 != 0)
                throw new InvalidDataException("Invalid simplified triangle range.");
            int write = 0;
            for (int read = 0; read < indexCount; read += 3)
            {
                uint ia = indices[read], ib = indices[read + 1], ic = indices[read + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    throw new InvalidDataException("A simplified index exceeds its vertex buffer.");
                if (ia == ib || ib == ic || ic == ia) continue;
                var a = vertices[ia].position; var b = vertices[ib].position; var c = vertices[ic].position;
                if (!Finite(a.x) || !Finite(a.y) || !Finite(a.z) || !Finite(b.x) || !Finite(b.y) ||
                    !Finite(b.z) || !Finite(c.x) || !Finite(c.y) || !Finite(c.z))
                    throw new InvalidDataException("A simplified triangle has a non-finite position.");
                double x1 = (double)b.x - a.x, y1 = (double)b.y - a.y, z1 = (double)b.z - a.z;
                double x2 = (double)c.x - a.x, y2 = (double)c.y - a.y, z2 = (double)c.z - a.z;
                double x = y1 * z2 - z1 * y2, y = z1 * x2 - x1 * z2, z = x1 * y2 - y1 * x2;
                if (x == 0 && y == 0 && z == 0) continue;
                indices[write++] = ia; indices[write++] = ib; indices[write++] = ic;
            }
            return write;
        }

        static float RoundUp(double value)
        {
            float result = (float)value;
            if (Finite(result) && result < value)
                result = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(result) + 1);
            return result;
        }

        static HashSet<PositionKey> FindMaterialBoundaryPositions(VirtualMeshModelVertex[] vertices,
            uint[] indices, VirtualMeshModelCluster[] clusters, int submeshCount, CancellationToken cancellationToken)
        {
            var firstSlot = new Dictionary<PositionKey, uint>();
            var shared = new HashSet<PositionKey>();
            foreach (var cluster in clusters)
            {
                if (cluster.type != 0 && cluster.type != 3) continue;
                cancellationToken.ThrowIfCancellationRequested();
                ulong end = (ulong)cluster.indexStart + cluster.indexCount;
                if (cluster.submesh >= submeshCount || end > (ulong)indices.Length || cluster.indexCount % 3 != 0)
                    throw new InvalidDataException("Invalid root surface while identifying material boundaries.");
                for (ulong i = cluster.indexStart; i < end; i++)
                {
                    uint index = indices[(int)i];
                    if (index >= vertices.Length) throw new InvalidDataException("A root index exceeds the model vertex buffer.");
                    var position = vertices[index].position;
                    if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z))
                        throw new InvalidDataException("A root vertex has a non-finite position.");
                    var key = new PositionKey(position);
                    if (firstSlot.TryGetValue(key, out uint slot))
                    {
                        if (slot != cluster.submesh) shared.Add(key);
                    }
                    else firstSlot.Add(key, cluster.submesh);
                }
            }
            return shared;
        }

        static byte[] BuildMaterialBoundaryLocks(VirtualMeshModelVertex[] sourceVertices,
            uint[] originalVertexIds, HashSet<PositionKey> materialBoundaries)
        {
            var locks = new byte[originalVertexIds.Length];
            for (int i = 0; i < locks.Length; i++)
                if (materialBoundaries.Contains(new PositionKey(sourceVertices[originalVertexIds[i]].position)))
                    locks[i] = 1;
            return locks;
        }

        static unsafe void WeldRoots(VirtualMeshModelVertex[] sourceVertices, uint[] sourceIndices,
            VirtualMeshModelCluster[] clusters, List<int> roots, int indexCount,
            out Vertex[] vertices, out uint[] indices, out uint[] originalVertexIds, CancellationToken cancellationToken)
        {
            var inputVertices = new List<Vertex>(); var sourceIds = new List<uint>();
            var sourceToInput = new int[sourceVertices.Length]; Array.Fill(sourceToInput, -1);
            var inputIndices = new uint[indexCount]; int offset = 0;
            foreach (int id in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cluster = clusters[id];
                for (uint i = cluster.indexStart; i < cluster.indexStart + cluster.indexCount; i++)
                {
                    uint original = sourceIndices[i]; int mapped = sourceToInput[original];
                    if (mapped < 0)
                    {
                        var v = sourceVertices[original]; mapped = inputVertices.Count; sourceToInput[original] = mapped;
                        inputVertices.Add(new Vertex { position = v.position, tangent = v.tangent, normal = v.normal,
                            color = v.color, uv0 = v.uv0, uv1 = v.uv1 }); sourceIds.Add(original);
                    }
                    inputIndices[offset++] = (uint)mapped;
                }
            }
            var input = inputVertices.ToArray(); var remap = new uint[input.Length];
            uint unique;
            fixed (Vertex* v = input) fixed (uint* ix = inputIndices) fixed (uint* map = remap)
            {
                // Reject malformed attributes before entering native code. Exact welding never merges
                // different normal/tangent/UV/color bits, including distinct authored material masks.
                for (int i = 0; i < input.Length * 18L; i++)
                    if (!Finite(((float*)v)[i])) throw new InvalidDataException("A root vertex has non-finite attributes.");
                unique = meshopt_generateVertexRemap(map, ix, (uint)inputIndices.Length, v, (uint)input.Length, VertexStride);
                vertices = new Vertex[unique]; indices = new uint[inputIndices.Length];
                fixed (Vertex* output = vertices) fixed (uint* outputIndices = indices)
                { meshopt_remapVertexBuffer(output, v, (uint)input.Length, VertexStride, map);
                    meshopt_remapIndexBuffer(outputIndices, ix, (uint)inputIndices.Length, map); }
            }
            originalVertexIds = new uint[unique]; Array.Fill(originalVertexIds, uint.MaxValue);
            for (int i = 0; i < remap.Length; i++) if (originalVertexIds[remap[i]] == uint.MaxValue) originalVertexIds[remap[i]] = sourceIds[i];
        }

        static unsafe int Simplify(uint[] indices, Vertex[] vertices, byte[] vertexLocks, uint[] output, uint target,
            float relativeError, float colorWeight, out float error)
        {
            if (vertexLocks.Length != vertices.Length)
                throw new ArgumentException("The material boundary lock count must match the compact vertex buffer.", nameof(vertexLocks));
            var weights = new[] { .5f, .5f, .5f, .8f, .8f, .8f, .8f,
                colorWeight, colorWeight, colorWeight, colorWeight, .5f, .5f, .2f, .2f };
            float resultError = 0;
            fixed (uint* dst = output) fixed (uint* src = indices) fixed (Vertex* v = vertices) fixed (float* w = weights)
            fixed (byte* locks = vertexLocks)
            {
                int result = checked((int)meshopt_simplifyWithAttributes(dst, src, (uint)indices.Length, v,
                    (uint)vertices.Length, VertexStride, &v[0].tangent, VertexStride, w, 15, locks, target,
                    relativeError, 0, &resultError)); // Only shared material positions are explicitly locked.
                error = resultError; return result;
            }
        }

        static uint[][] Meshlets(uint[] indices, Vertex[] vertices)
        {
            MeshOperations.SpatialSortTriangles(indices, vertices, VertexStride);
            uint capacity = MeshOperations.BuildMeshletsBound((uint)indices.Length, MaxVertices, MaxTriangles);
            var meshlets = new Meshlet[capacity]; var meshletVertices = new uint[capacity * MaxVertices];
            var triangles = new byte[capacity * MaxTriangles * 3];
            uint count = MeshOperations.BuildMeshlets(meshlets, meshletVertices, triangles, indices, vertices,
                VertexStride, MaxVertices, MaxTriangles, 0);
            var result = new uint[count][];
            for (int i = 0; i < count; i++)
            {
                var meshlet = meshlets[i]; var cluster = new uint[meshlet.triangleCount * 3];
                for (int j = 0; j < cluster.Length; j++) cluster[j] = meshletVertices[meshlet.vertexOffset + triangles[meshlet.triangleOffset + j]];
                result[i] = cluster;
            }
            return result;
        }
    }
}
