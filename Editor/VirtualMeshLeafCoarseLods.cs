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

namespace Unity.VirtualMesh.Editor
{
    [Serializable]
    public sealed class LeafCoarseLodSettings
    {
        public int[] Submeshes;
        public int MaxLevels = 3, MinimumTriangles = 256;
        public float MaximumReductionFactor = 8;
        public float RelativeError = .005f, ErrorGrowth = 2, MaximumRelativeError = .32f;
        public float MaximumStepExpansion = 1.4f, MaximumTotalExpansion = 3;
        public float DonationDistanceFraction = .03f, MinimumNormalAlignment = .3f;
        // Caller must fingerprint every real material variant and validation settings.
        public string ValidationIdentity;
        // Runs synchronously immediately before publication, after all worker/yield work.
        // Recheck every source material/texture snapshot used by the validator here.
        public Action ValidateSourceUnchanged;
        public IProgress<string> Progress;
        // Commit a prepared, revalidated material contract in the same synchronous
        // publication segment. A missing contract never permits new leaf levels.
        public Action<VirtualMeshModelAsset> PublishMaterialContract;
    }

    [Serializable]
    public sealed class LeafCoarseLodLevelResult
    {
        public int Submesh, Level, Triangles, Clusters;
        public float Error, MaxProjectedPixels, MaxVertexDisplacement, MaximumExpansion;
        public double UnassignedArea, DonatedArea;
        public bool Accepted;
        public string Reason;
        public object ValidationReport;
    }

    [Serializable]
    public sealed class LeafCoarseLodBuildResult
    {
        public bool Updated, Reused;
        public string SettingsHash;
        public int AddedLevels, AddedVertices, AddedClusters;
        public long OldRootTriangles, NewRootTriangles;
        public Bounds ExpandedBounds;
        public LeafCoarseLodLevelResult[] Levels;
    }

    /// <summary>Appends genuine alpha-clipped mesh cluster layers. Preserves all source prefixes and
    /// attributes; only new-layer positions are dilated. A caller-supplied validation of EVERY
    /// source material variant is mandatory. No asset is changed before validation completes.</summary>
    public static class VirtualMeshLeafCoarseLods
    {
        public const int AlgorithmVersion = 1;
        const uint VertexStride = 72;
        const int MaxVertices = 120, MaxTriangles = 64;
        public delegate Task<LeafCoverageValidation> CoverageValidator(VirtualMeshModelAsset source,
            int submesh, int level, VirtualMeshModelVertex[] vertices, uint[] indices,
            Bounds expandedBounds, CancellationToken cancellationToken);

        sealed class Candidate
        {
            public Vertex[] Vertices;
            public VirtualMeshModelVertex[] ModelVertices;
            public uint[] Indices;
            public uint[][] Meshlets;
            public float Error, Spacing, Gate;
            public LeafCoarseLodLevelResult Result;
        }
        sealed class SlotBuild
        {
            public int Slot;
            public List<int> Roots;
            public int RootTriangles;
            public float Error, Spacing;
            public readonly List<Candidate> Candidates = new List<Candidate>();
        }
        sealed class Prepared
        {
            public Bounds Bounds;
            public readonly List<SlotBuild> Slots = new List<SlotBuild>();
        }
        sealed class Published
        {
            public VirtualMeshModelVertex[] Vertices;
            public uint[] Indices;
            public VirtualMeshModelCluster[] Clusters;
            public VirtualMeshModelClusterCullingData[] Culling;
            public VirtualMeshModelLodLevel[] Rows;
        }
        readonly struct PositionKey : IEquatable<PositionKey>
        {
            readonly int x, y, z;
            public PositionKey(Vector3 p) {
                x = p.x == 0 ? 0 : BitConverter.SingleToInt32Bits(p.x);
                y = p.y == 0 ? 0 : BitConverter.SingleToInt32Bits(p.y);
                z = p.z == 0 ? 0 : BitConverter.SingleToInt32Bits(p.z);
            }
            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PositionKey p && Equals(p);
            public override int GetHashCode() { unchecked { return ((x * 397) ^ y) * 397 ^ z; } }
        }
        sealed class Components
        {
            public int[] Labels;
            public double[] Area;
            public Vector3[] Center, Normal;
            public bool[] Eligible;
        }
        sealed class EdgeComparer : IEqualityComparer<ulong>
        {
            public static readonly EdgeComparer Instance = new EdgeComparer();
            public bool Equals(ulong a, ulong b) => a == b;
            public int GetHashCode(ulong edge)
            {
                // The default ulong hash is high^low. Sequential mesh edge IDs
                // then collide almost universally (i,i+1), making a large bush
                // quadratic to analyze. Mix both full IDs before truncation.
                unchecked { edge ^= edge >> 33; edge *= 0xff51afd7ed558ccdUL;
                    edge ^= edge >> 33; edge *= 0xc4ceb9fe1a85ec53UL; edge ^= edge >> 33;
                    return (int)edge; }
            }
        }

        public static async Task<LeafCoarseLodBuildResult> BuildAsync(VirtualMeshModelAsset model,
            LeafCoarseLodSettings settings, CoverageValidator validator, bool saveAsset = false,
            CancellationToken cancellationToken = default)
        {
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("Start a leaf LOD build on the Unity editor thread.");
            cancellationToken.ThrowIfCancellationRequested();
            if (model == null || !model.IsValid) throw new ArgumentException("A valid source model is required.");
            if (validator == null) throw new ArgumentNullException(nameof(validator), "Real alpha coverage validation is mandatory.");
            var options = Snapshot(settings, model.SubMeshCount);
            options.ValidateSourceUnchanged();
            string hash = SettingsHash(options, model.SourceHash, model.CoarseLodSettingsHash);
            if (model.LeafLodVersion != 0)
            {
                if (model.LeafLodVersion != AlgorithmVersion || model.LeafLodSettingsHash != hash)
                    throw new InvalidOperationException("Rebuild the original geometry before changing leaf LOD settings or material validation identity.");
                if (model.LeafMaterialContract == null || !model.LeafMaterialContract.IsValid) {
                    options.PublishMaterialContract(model); EditorUtility.SetDirty(model);
                    if (saveAsset && EditorUtility.IsPersistent(model)) AssetDatabase.SaveAssetIfDirty(model);
                }
                return new LeafCoarseLodBuildResult { Reused = true, SettingsHash = hash, ExpandedBounds = model.Bounds,
                    AddedLevels = model.LeafLodLevels.Count(r => r.Level > 0),
                    OldRootTriangles = model.LeafLodLevels.Where(r => r.Level == 0).Sum(r => (long)r.TriangleCount),
                    NewRootTriangles = model.LeafLodLevels.GroupBy(r => r.Submesh).Sum(g => (long)g.OrderBy(r => r.Level).Last().TriangleCount),
                    Levels = Array.Empty<LeafCoarseLodLevelResult>() };
            }
            foreach (var row in model.CoarseLodLevels)
                if (row.Level > 0 && options.Submeshes.Contains(row.Submesh))
                    throw new InvalidOperationException("An opaque coarse chain already occupies a requested leaf material slot.");
            int revision = model.Revision, slots = model.SubMeshCount;
            var vertices = model.Vertices; var indices = model.Indices; var clusters = model.Clusters;
            bool hadCulling = model.HasCullingData; var originalCulling = model.CullingData;
            var bounds = model.Bounds; var sourceMesh = model.SourceMesh;
            string sourceHash = model.SourceHash; float sourceError = model.SimplificationError;
            int opaqueVersion = model.CoarseLodVersion, opaqueBase = model.CoarseLodBaseClusterCount;
            string opaqueHash = model.CoarseLodSettingsHash; var opaqueRows = model.CoarseLodLevels;
            float pixelError = model.DefaultPixelError, trianglePixels = model.DefaultTargetTrianglePixels;
            int policyVersion = model.PrebuildPolicyVersion; string policyHash = model.PrebuildPolicyHash;
            var policies = model.PrebuildPolicies; var contexts = model.PrebuildPolicyContexts;
            var prepared = await Task.Run(() => Prepare(vertices, indices, clusters, slots, bounds, options, cancellationToken), cancellationToken);
            foreach (var slot in prepared.Slots)
            {
                float priorError = slot.Error, priorGate = float.PositiveInfinity;
                foreach (var candidate in slot.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var validation = await validator(model, slot.Slot, candidate.Result.Level, candidate.ModelVertices,
                        candidate.Indices, prepared.Bounds, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    candidate.Result.ValidationReport = validation?.Report;
                    bool valid = validation != null && validation.Accepted && Finite(validation.MaxProjectedPixels) &&
                        validation.MaxProjectedPixels > 0 && Finite(validation.CoverageErrorWorld) && validation.CoverageErrorWorld >= 0;
                    if (!valid) { candidate.Result.Reason = validation?.Reason ?? "Coverage validator returned no valid acceptance."; break; }
                    candidate.Gate = Math.Min(priorGate, validation.MaxProjectedPixels);
                    // Density may relax error by 2x: include twice the measured coverage error.
                    candidate.Error = Math.Max(priorError, Math.Max(candidate.Error, RoundUp(2d * validation.CoverageErrorWorld)));
                    candidate.Error = Math.Max(.000001f, candidate.Error);
                    if (!Finite(candidate.Error)) throw new InvalidDataException("Leaf LOD error overflow.");
                    candidate.Result.Accepted = true; candidate.Result.Reason = validation.Reason;
                    candidate.Result.Error = candidate.Error; candidate.Result.MaxProjectedPixels = candidate.Gate;
                    priorGate = candidate.Gate; priorError = candidate.Error;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new LeafCoarseLodBuildResult { SettingsHash = hash, ExpandedBounds = prepared.Bounds,
                Levels = prepared.Slots.SelectMany(s => s.Candidates.Select(c => c.Result)).ToArray(),
                OldRootTriangles = prepared.Slots.Sum(s => (long)s.RootTriangles),
                NewRootTriangles = prepared.Slots.Sum(s => (long)(s.Candidates.LastOrDefault(c => c.Result.Accepted)?.Indices.Length / 3 ?? s.RootTriangles)) };
            result.AddedLevels = result.Levels.Count(r => r.Accepted);
            if (result.AddedLevels == 0) return result;
            var published = await Task.Run(() => Assemble(vertices, indices, clusters, prepared, pixelError, trianglePixels, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (model == null || model.Revision != revision || model.LeafLodVersion != 0 ||
                !ReferenceEquals(vertices, model.Vertices) || !ReferenceEquals(indices, model.Indices) || !ReferenceEquals(clusters, model.Clusters))
                throw new InvalidOperationException("Source geometry changed during leaf validation; nothing was published.");
            options.ValidateSourceUnchanged();
            try
            {
                model.SetData(sourceMesh, sourceError, sourceHash, published.Vertices, published.Indices, published.Clusters, slots, prepared.Bounds);
                model.SetCullingData(published.Culling);
                if (opaqueVersion > 0) model.SetCoarseLodMetadata(opaqueVersion, opaqueHash, opaqueBase, opaqueRows, pixelError, trianglePixels);
                if (policyVersion > 0) model.SetPrebuildPolicyMetadata(policyVersion, policyHash, policies, contexts);
                model.SetLeafLodMetadata(AlgorithmVersion, hash, vertices.Length, indices.Length, clusters.Length, published.Rows);
                options.PublishMaterialContract(model);
                if (model.LeafMaterialContract == null || !model.LeafMaterialContract.IsValid)
                    throw new InvalidOperationException("The leaf hierarchy has no valid material contract.");
            }
            catch
            {
                // A publishing callback can fail (for example while adding frozen
                // materials as subassets). Restore all original geometry/metadata.
                // Revision deliberately advances so every GPU cache drops a possible
                // reference to the abandoned arrays. No partially built hierarchy is saved.
                model.SetData(sourceMesh, sourceError, sourceHash, vertices, indices, clusters, slots, bounds);
                if (hadCulling) model.SetCullingData(originalCulling);
                if (opaqueVersion > 0) model.SetCoarseLodMetadata(opaqueVersion, opaqueHash, opaqueBase, opaqueRows, pixelError, trianglePixels);
                if (policyVersion > 0) model.SetPrebuildPolicyMetadata(policyVersion, policyHash, policies, contexts);
                EditorUtility.SetDirty(model);
                throw;
            }
            EditorUtility.SetDirty(model);
            if (saveAsset && EditorUtility.IsPersistent(model)) AssetDatabase.SaveAssetIfDirty(model);
            result.Updated = true; result.AddedVertices = published.Vertices.Length - vertices.Length;
            result.AddedClusters = published.Clusters.Length - clusters.Length;
            return result;
        }

        static LeafCoarseLodSettings Snapshot(LeafCoarseLodSettings source, int slots)
        {
            if (source == null || source.Submeshes == null || source.Submeshes.Length == 0 ||
                source.Submeshes.Any(s => s < 0 || s >= slots) || string.IsNullOrEmpty(source.ValidationIdentity) ||
                source.ValidateSourceUnchanged == null ||
                source.PublishMaterialContract == null ||
                source.MaxLevels < 1 || source.MaxLevels > 16 || source.MinimumTriangles < 1 ||
                !Finite(source.MaximumReductionFactor) || source.MaximumReductionFactor < 2 || source.MaximumReductionFactor > 8 ||
                !Finite(source.RelativeError) || source.RelativeError <= 0 ||
                !Finite(source.MaximumRelativeError) || source.MaximumRelativeError < source.RelativeError || source.MaximumRelativeError > 1 ||
                !Finite(source.ErrorGrowth) || source.ErrorGrowth < 1 || source.ErrorGrowth > 4 ||
                !Finite(source.MaximumStepExpansion) || source.MaximumStepExpansion < 1 || source.MaximumStepExpansion > 1.5f ||
                !Finite(source.MaximumTotalExpansion) || source.MaximumTotalExpansion < 1 || source.MaximumTotalExpansion > 3 ||
                !Finite(source.DonationDistanceFraction) || source.DonationDistanceFraction <= 0 || source.DonationDistanceFraction > .1f ||
                !Finite(source.MinimumNormalAlignment) || source.MinimumNormalAlignment < 0 || source.MinimumNormalAlignment > 1)
                throw new ArgumentException("Invalid leaf LOD settings; explicit slots and material validation identity are required.");
            return new LeafCoarseLodSettings { Submeshes = source.Submeshes.Distinct().OrderBy(i => i).ToArray(),
                MaxLevels = source.MaxLevels, MinimumTriangles = source.MinimumTriangles, MaximumReductionFactor = source.MaximumReductionFactor,
                RelativeError = source.RelativeError, ErrorGrowth = source.ErrorGrowth, MaximumRelativeError = source.MaximumRelativeError,
                MaximumStepExpansion = source.MaximumStepExpansion, MaximumTotalExpansion = source.MaximumTotalExpansion,
                DonationDistanceFraction = source.DonationDistanceFraction, MinimumNormalAlignment = source.MinimumNormalAlignment,
                ValidationIdentity = source.ValidationIdentity, ValidateSourceUnchanged = source.ValidateSourceUnchanged,
                PublishMaterialContract = source.PublishMaterialContract, Progress = source.Progress };
        }

        static string SettingsHash(LeafCoarseLodSettings s, string sourceHash, string opaqueHash)
        {
            string text = AlgorithmVersion + ":" + sourceHash + ":" + opaqueHash + ":" + s.ValidationIdentity + ":" +
                string.Join(",", s.Submeshes) + ":" + s.MaxLevels + ":" + s.MinimumTriangles + ":" +
                string.Join(",", new[] { s.MaximumReductionFactor, s.RelativeError, s.ErrorGrowth, s.MaximumRelativeError,
                    s.MaximumStepExpansion, s.MaximumTotalExpansion, s.DonationDistanceFraction, s.MinimumNormalAlignment }
                    .Select(v => BitConverter.SingleToInt32Bits(v).ToString("x8")));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        static Prepared Prepare(VirtualMeshModelVertex[] vertices, uint[] indices, VirtualMeshModelCluster[] clusters,
            int submeshCount, Bounds bounds, LeafCoarseLodSettings options, CancellationToken token)
        {
            var output = new Prepared { Bounds = bounds };
            var boundaries = FindMaterialBoundaryPositions(vertices, indices, clusters, submeshCount, token);
            Vector3 extents = bounds.extents, center = bounds.center;
            foreach (int slotId in options.Submeshes)
            {
                token.ThrowIfCancellationRequested();
                var slot = new SlotBuild { Slot = slotId, Roots = new List<int>() };
                var finestIndices = new List<uint>(); double originalArea = 0;
                for (int i = 0; i < clusters.Length; i++)
                {
                    var c = clusters[i]; if (c.submesh != slotId) continue;
                    if (c.type == 0 || c.type == 1) {
                        for (uint j = c.indexStart; j < c.indexStart + c.indexCount; j++) finestIndices.Add(indices[j]);
                        originalArea += Area(vertices, indices, c);
                    }
                    if (c.type == 0 || c.type == 3) { slot.Roots.Add(i); slot.RootTriangles += (int)c.indexCount / 3; }
                    slot.Error = Math.Max(slot.Error, Math.Max(c.error, c.parentError));
                    slot.Spacing = Math.Max(slot.Spacing, Math.Max(c.triangleSpacing, c.parentTriangleSpacing));
                }
                if (slot.Roots.Count == 0 || finestIndices.Count == 0) continue;
                var original = new Vertex[vertices.Length];
                for (int i = 0; i < vertices.Length; i++) original[i] = Native(vertices[i]);
                options.Progress?.Report("Slot " + slotId + ": analyzing original leaf components");
                var sourceComponents = AnalyzeComponents(original, finestIndices.ToArray(), boundaries, true, token);
                options.Progress?.Report("Slot " + slotId + ": welding root attributes");
                WeldRoots(vertices, indices, clusters, slot.Roots, slot.RootTriangles * 3,
                    out var working, out var current, out var originalIds, token);
                var locks = BuildMaterialBoundaryLocks(vertices, originalIds, boundaries);
                var labels = new int[working.Length]; var cumulative = new float[working.Length];
                for (int i = 0; i < working.Length; i++) {
                    labels[i] = sourceComponents.Labels[originalIds[i]]; cumulative[i] = 1;
                    if (labels[i] < 0) throw new InvalidDataException("A root vertex is absent from its original finest leaf component.");
                }
                int minimum = Math.Max(options.MinimumTriangles, (int)Math.Ceiling(slot.RootTriangles / (double)options.MaximumReductionFactor));
                float error = slot.Error, spacing = slot.Spacing; int previousClusters = slot.Roots.Count;
                float donationDistance = PositionScale(working) * options.DonationDistanceFraction;
                for (int attempt = 0; attempt < options.MaxLevels && current.Length / 3 > minimum; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    float relative = (float)Math.Min(options.MaximumRelativeError, options.RelativeError * Math.Pow(options.ErrorGrowth, attempt));
                    var simplified = new uint[current.Length];
                    float nativeError;
                    options.Progress?.Report("Slot " + slotId + ", attempt " + (attempt + 1) + ": simplifying " + current.Length / 3 + " triangles");
                    int count = Simplify(current, working, locks, simplified, checked((uint)(Math.Max(minimum, current.Length / 6) * 3)), relative, .8f, out nativeError);
                    if (!Finite(nativeError) || nativeError < 0 || count < 0 || count > simplified.Length || count % 3 != 0)
                        throw new InvalidDataException("Invalid native leaf simplification result.");
                    count = FilterDegenerateTriangles(simplified, count, working);
                    if (count < 3 || count >= current.Length * .98) continue;
                    Array.Resize(ref simplified, count);
                    float nativeScale = PositionScale(working);
                    var nextVertices = (Vertex[])working.Clone(); var nextCumulative = (float[])cumulative.Clone();
                    var row = new LeafCoarseLodLevelResult { Submesh = slotId, Level = slot.Candidates.Count + 1, Triangles = count / 3 };
                    options.Progress?.Report("Slot " + slotId + ", level " + row.Level + ": redistributing open leaf area");
                    Redistribute(nextVertices, simplified, labels, sourceComponents, nextCumulative, boundaries,
                        donationDistance, options, row, token);
                    error = RoundUp((double)error + nativeError * (double)nativeScale + row.MaxVertexDisplacement);
                    spacing = Math.Max(spacing, RoundUp(Math.Sqrt(originalArea / (count / 3))));
                    if (!Finite(error) || !Finite(spacing)) throw new InvalidDataException("Leaf simplification error overflow.");
                    Compact(nextVertices, simplified, out var compactVertices, out var compactIndices);
                    options.Progress?.Report("Slot " + slotId + ", level " + row.Level + ": building " + count / 3 + " triangle clusters");
                    var meshlets = Meshlets((uint[])compactIndices.Clone(), compactVertices);
                    if (meshlets.Length > previousClusters) break;
                    row.Clusters = meshlets.Length; row.Error = error;
                    var candidate = new Candidate { Vertices = compactVertices, ModelVertices = compactVertices.Select(Model).ToArray(),
                        Indices = compactIndices, Meshlets = meshlets, Error = error, Spacing = spacing, Result = row };
                    slot.Candidates.Add(candidate);
                    foreach (var v in compactVertices) {
                        extents = Vector3.Max(extents, new Vector3(RoundUp(Math.Abs((double)v.position.x - center.x)),
                            RoundUp(Math.Abs((double)v.position.y - center.y)), RoundUp(Math.Abs((double)v.position.z - center.z))));
                    }
                    working = nextVertices; cumulative = nextCumulative; current = simplified; previousClusters = meshlets.Length;
                }
                output.Slots.Add(slot);
            }
            // Preserve the original pivot/center: source materials use it for per-instance variation.
            output.Bounds = new Bounds(center, extents * 2);
            return output;
        }

        static Published Assemble(VirtualMeshModelVertex[] sourceVertices, uint[] sourceIndices,
            VirtualMeshModelCluster[] sourceClusters, Prepared prepared, float pixelError, float trianglePixels, CancellationToken token)
        {
            var vertices = new List<VirtualMeshModelVertex>(sourceVertices); var indices = new List<uint>(sourceIndices);
            var clusters = new List<VirtualMeshModelCluster>(sourceClusters); var rows = new List<VirtualMeshModelLodLevel>();
            var e = prepared.Bounds.extents; float radius = RoundUp(Math.Sqrt((double)e.x * e.x + (double)e.y * e.y + (double)e.z * e.z));
            foreach (var slot in prepared.Slots)
            {
                var accepted = slot.Candidates.Where(c => c.Result.Accepted).ToArray(); if (accepted.Length == 0) continue;
                token.ThrowIfCancellationRequested();
                var previous = slot.Roots.ToArray();
                for (int i = 0; i < sourceClusters.Length; i++) if (clusters[i].submesh == slot.Slot) {
                    var c = clusters[i]; c.center = prepared.Bounds.center; c.radius = radius; clusters[i] = c;
                }
                rows.Add(new VirtualMeshModelLodLevel { Submesh = slot.Slot, FirstCluster = -1, ClusterCount = slot.Roots.Count,
                    TriangleCount = slot.RootTriangles, Error = slot.Error, TriangleSpacing = slot.Spacing,
                    TransitionUnitsPerPixel = VirtualMeshModelCluster.TransitionThreshold(slot.Error, slot.Spacing, pixelError, trianglePixels) });
                foreach (var candidate in accepted)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (int id in previous) {
                        var c = clusters[id]; c.type = c.type == 0 ? 1u : 2u;
                        c.parentError = candidate.Error; c.parentTriangleSpacing = candidate.Spacing; clusters[id] = c;
                    }
                    var prevRow = rows[rows.Count - 1]; prevRow.ParentError = candidate.Error; prevRow.ParentTriangleSpacing = candidate.Spacing;
                    rows[rows.Count - 1] = prevRow;
                    int vertexBase = vertices.Count, clusterBase = clusters.Count;
                    vertices.AddRange(candidate.ModelVertices);
                    foreach (var meshlet in candidate.Meshlets) {
                        int start = indices.Count; foreach (uint id in meshlet) indices.Add(checked((uint)vertexBase + id));
                        clusters.Add(new VirtualMeshModelCluster { center = prepared.Bounds.center, radius = radius, submesh = (uint)slot.Slot,
                            type = 3, error = candidate.Error, triangleSpacing = candidate.Spacing, indexStart = (uint)start, indexCount = (uint)meshlet.Length });
                    }
                    rows.Add(new VirtualMeshModelLodLevel { Submesh = slot.Slot, Level = candidate.Result.Level, FirstCluster = clusterBase,
                        ClusterCount = candidate.Meshlets.Length, TriangleCount = candidate.Indices.Length / 3, Error = candidate.Error,
                        TriangleSpacing = candidate.Spacing, MaxProjectedPixels = candidate.Gate,
                        TransitionUnitsPerPixel = VirtualMeshModelCluster.TransitionThreshold(candidate.Error, candidate.Spacing, pixelError, trianglePixels) });
                    previous = Enumerable.Range(clusterBase, candidate.Meshlets.Length).ToArray();
                }
            }
            var output = new Published { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Clusters = clusters.ToArray(), Rows = rows.ToArray() };
            output.Culling = VirtualMeshModelCullingData.Build(output.Vertices, output.Indices, output.Clusters, token);
            return output;
        }

        static Vertex Native(VirtualMeshModelVertex v) => new Vertex { position = v.position, tangent = v.tangent,
            normal = v.normal, color = v.color, uv0 = v.uv0, uv1 = v.uv1 };
        static VirtualMeshModelVertex Model(Vertex v) => new VirtualMeshModelVertex { position = v.position, tangent = v.tangent,
            normal = v.normal, color = v.color, uv0 = v.uv0, uv1 = v.uv1 };
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static void Compact(Vertex[] source, uint[] input, out Vertex[] vertices, out uint[] indices)
        {
            var map = new int[source.Length]; Array.Fill(map, -1); var compact = new List<Vertex>();
            indices = new uint[input.Length];
            for (int i = 0; i < input.Length; i++) {
                uint id = input[i]; if (map[id] < 0) { map[id] = compact.Count; compact.Add(source[id]); }
                indices[i] = (uint)map[id];
            }
            vertices = compact.ToArray();
        }

        static int Find(int[] parent, int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        static void Union(int[] parent, int a, int b) { a = Find(parent, a); b = Find(parent, b); if (a != b) parent[b] = a; }
        static ulong Edge(int a, int b) => a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
        static double TriangleArea(Vector3 a, Vector3 b, Vector3 c) {
            double ax = (double)b.x - a.x, ay = (double)b.y - a.y, az = (double)b.z - a.z;
            double bx = (double)c.x - a.x, by = (double)c.y - a.y, bz = (double)c.z - a.z;
            double x = ay * bz - az * by, y = az * bx - ax * bz, z = ax * by - ay * bx;
            return .5 * Math.Sqrt(x * x + y * y + z * z);
        }

        static Components AnalyzeComponents(Vertex[] vertices, uint[] indices, HashSet<PositionKey> boundaries,
            bool determineOpenLeaf, CancellationToken token)
        {
            var nodeAtPosition = new Dictionary<PositionKey, int>(); var nodes = new int[vertices.Length]; Array.Fill(nodes, -1);
            foreach (uint id in indices) if (nodes[id] < 0) {
                var key = new PositionKey(vertices[id].position);
                if (!nodeAtPosition.TryGetValue(key, out int node)) { node = nodeAtPosition.Count; nodeAtPosition.Add(key, node); }
                nodes[id] = node;
            }
            var parent = Enumerable.Range(0, nodeAtPosition.Count).ToArray();
            for (int i = 0; i < indices.Length; i += 3) { Union(parent, nodes[indices[i]], nodes[indices[i + 1]]); Union(parent, nodes[indices[i]], nodes[indices[i + 2]]); }
            var rootToLabel = new Dictionary<int, int>(); var labels = new int[vertices.Length]; Array.Fill(labels, -1);
            for (int i = 0; i < labels.Length; i++) if (nodes[i] >= 0) {
                int root = Find(parent, nodes[i]); if (!rootToLabel.TryGetValue(root, out int label)) { label = rootToLabel.Count; rootToLabel.Add(root, label); }
                labels[i] = label;
            }
            int count = rootToLabel.Count;
            var area = new double[count]; var centers = new double[count, 3]; var normals = new double[count, 3];
            var blocked = new bool[count]; var edgeCounts = new Dictionary<ulong, int>(EdgeComparer.Instance);
            if (determineOpenLeaf) for (int i = 0; i < indices.Length; i += 3)
                for (int j = 0; j < 3; j++) { ulong edge = Edge(nodes[indices[i + j]], nodes[indices[i + (j + 1) % 3]]); edgeCounts.TryGetValue(edge, out int n); edgeCounts[edge] = n + 1; }
            var boundaryArea = new double[count];
            for (int i = 0; i < indices.Length; i += 3)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                uint ia = indices[i], ib = indices[i + 1], ic = indices[i + 2]; int label = labels[ia];
                var a = vertices[ia]; var b = vertices[ib]; var c = vertices[ic];
                double value = TriangleArea(a.position, b.position, c.position);
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("Nonfinite leaf triangle.");
                area[label] += value;
                for (int d = 0; d < 3; d++) { centers[label, d] += value * ((double)a.position[d] + b.position[d] + c.position[d]) / 3;
                    normals[label, d] += value * ((double)a.normal[d] + b.normal[d] + c.normal[d]) / 3; }
                blocked[label] |= boundaries.Contains(new PositionKey(a.position)) || boundaries.Contains(new PositionKey(b.position)) || boundaries.Contains(new PositionKey(c.position));
                if (determineOpenLeaf && (edgeCounts[Edge(nodes[ia], nodes[ib])] == 1 || edgeCounts[Edge(nodes[ib], nodes[ic])] == 1 || edgeCounts[Edge(nodes[ic], nodes[ia])] == 1))
                    boundaryArea[label] += value;
            }
            var result = new Components { Labels = labels, Area = area, Center = new Vector3[count], Normal = new Vector3[count], Eligible = new bool[count] };
            for (int i = 0; i < count; i++) {
                double length = Math.Sqrt(normals[i, 0] * normals[i, 0] + normals[i, 1] * normals[i, 1] + normals[i, 2] * normals[i, 2]);
                if (area[i] > 0) result.Center[i] = new Vector3((float)(centers[i, 0] / area[i]), (float)(centers[i, 1] / area[i]), (float)(centers[i, 2] / area[i]));
                if (length > 0) result.Normal[i] = new Vector3((float)(normals[i, 0] / length), (float)(normals[i, 1] / length), (float)(normals[i, 2] / length));
                result.Eligible[i] = !blocked[i] && length > 0 && area[i] > 0 && (!determineOpenLeaf || boundaryArea[i] >= .5 * area[i]);
            }
            return result;
        }

        static void Redistribute(Vertex[] vertices, uint[] indices, int[] sourceLabels, Components source,
            float[] cumulative, HashSet<PositionKey> boundaries, float maxDistance, LeafCoarseLodSettings options,
            LeafCoarseLodLevelResult report, CancellationToken token)
        {
            if (maxDistance <= 0 || !Finite(maxDistance)) return;
            var current = AnalyzeComponents(vertices, indices, boundaries, false, token);
            int count = current.Area.Length; var sourceId = new int[count]; Array.Fill(sourceId, -1);
            var used = new bool[vertices.Length]; foreach (uint id in indices) used[id] = true;
            var maximumExpansion = new float[count]; var perSource = new double[source.Area.Length];
            for (int i = 0; i < vertices.Length; i++) if (used[i]) {
                int label = current.Labels[i], original = sourceLabels[i];
                if (sourceId[label] >= 0 && sourceId[label] != original)
                    throw new InvalidDataException("Simplification connected originally disconnected leaves.");
                sourceId[label] = original; maximumExpansion[label] = Math.Max(maximumExpansion[label], cumulative[i]);
            }
            for (int i = 0; i < count; i++) { perSource[sourceId[i]] += current.Area[i]; current.Eligible[i] &= source.Eligible[sourceId[i]]; }
            var donation = new double[count];
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)Math.Floor(p.x / maxDistance), (int)Math.Floor(p.y / maxDistance), (int)Math.Floor(p.z / maxDistance));
            for (int i = 0; i < count; i++) if (current.Eligible[i]) {
                var cell = Cell(current.Center[i]); if (!grid.TryGetValue(cell, out var items)) grid.Add(cell, items = new List<int>()); items.Add(i);
            }
            var nearest = new int[3]; var scores = new double[3];
            for (int donor = 0; donor < source.Area.Length; donor++)
            {
                if ((donor & 1023) == 0) token.ThrowIfCancellationRequested();
                if (!source.Eligible[donor] || source.Area[donor] <= 0 || perSource[donor] > 0) continue;
                Array.Fill(nearest, -1); Array.Fill(scores, double.PositiveInfinity);
                var (x, y, z) = Cell(source.Center[donor]);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((x + dx, y + dy, z + dz), out var items)) foreach (int target in items) {
                        double distance = (current.Center[target] - source.Center[donor]).sqrMagnitude;
                        double alignment = Math.Abs(Vector3.Dot(current.Normal[target], source.Normal[donor]));
                        if (distance > maxDistance * (double)maxDistance || alignment < options.MinimumNormalAlignment) continue;
                        double score = distance / Math.Max(.05, alignment * alignment);
                        for (int k = 0; k < 3; k++) if (score < scores[k]) {
                            for (int j = 2; j > k; j--) { scores[j] = scores[j - 1]; nearest[j] = nearest[j - 1]; }
                            scores[k] = score; nearest[k] = target; break;
                        }
                    }
                if (nearest[0] < 0) { report.UnassignedArea += source.Area[donor]; continue; }
                double total = 0; for (int k = 0; k < 3; k++) if (nearest[k] >= 0) total += 1 / Math.Max(1e-8, scores[k]);
                for (int k = 0; k < 3; k++) if (nearest[k] >= 0) donation[nearest[k]] += source.Area[donor] / Math.Max(1e-8, scores[k]) / total;
                report.DonatedArea += source.Area[donor];
            }
            var scale = new float[count];
            for (int i = 0; i < count; i++) {
                double own = current.Area[i] * source.Area[sourceId[i]] / Math.Max(1e-30, perSource[sourceId[i]]);
                double desired = Math.Sqrt(Math.Max(1, (own + donation[i]) / Math.Max(1e-30, current.Area[i])));
                scale[i] = current.Eligible[i] ? (float)Math.Max(1, Math.Min(desired,
                    Math.Min(options.MaximumStepExpansion, options.MaximumTotalExpansion / Math.Max(1, maximumExpansion[i])))) : 1;
            }
            for (int i = 0; i < vertices.Length; i++) if (used[i]) {
                int label = current.Labels[i]; var delta = (vertices[i].position - current.Center[label]) * (scale[label] - 1);
                vertices[i].position += delta; cumulative[i] *= scale[label];
                report.MaxVertexDisplacement = Math.Max(report.MaxVertexDisplacement, delta.magnitude);
                report.MaximumExpansion = Math.Max(report.MaximumExpansion, cumulative[i]);
            }
        }
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
                if (cluster.type != 0 && cluster.type != 1 && cluster.type != 3) continue;
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
            var weights = new[] { .5f, .5f, .5f, 0f, .8f, .8f, .8f,
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

        static unsafe uint[][] Meshlets(uint[] indices, Vertex[] vertices)
        {
            fixed (uint* ix = indices) fixed (Vertex* v = vertices)
                NativeApi.SpatialSort(ix, ix, Size(indices.Length), v, Size(vertices.Length), Size(VertexStride));
            uint capacity = checked((uint)NativeApi.MeshletBound(Size(indices.Length), Size(MaxVertices), Size(MaxTriangles)).ToUInt64());
            var meshlets = new Meshlet[capacity]; var meshletVertices = new uint[capacity * MaxVertices];
            var triangles = new byte[capacity * MaxTriangles * 3];
            uint count;
            fixed (Meshlet* m = meshlets) fixed (uint* mv = meshletVertices) fixed (byte* t = triangles)
            fixed (uint* ix = indices) fixed (Vertex* v = vertices)
            {
                count = checked((uint)NativeApi.BuildMeshlets(m, mv, t, ix, Size(indices.Length), v, Size(vertices.Length),
                    Size(VertexStride), Size(MaxVertices), Size(MaxTriangles), 0).ToUInt64());
                for (int i = 0; i < count; i++) NativeApi.OptimizeMeshlet(mv + m[i].vertexOffset, t + m[i].triangleOffset,
                    Size(m[i].triangleCount), Size(m[i].vertexCount));
            }
            var result = new uint[count][];
            for (int i = 0; i < count; i++)
            {
                var meshlet = meshlets[i]; var cluster = new uint[meshlet.triangleCount * 3];
                for (int j = 0; j < cluster.Length; j++) cluster[j] = meshletVertices[meshlet.vertexOffset + triangles[meshlet.triangleOffset + j]];
                result[i] = cluster;
            }
            return result;
        }

        // Native meshoptimizer uses size_t for all counts/strides. Preserve its
        // 64-bit ABI on both Mono and CoreCLR; uint stack arguments are not size_t.
        static class NativeApi
        {
            const string Dll = "meshoptimizer.dll";
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_generateVertexRemap")]
            internal static extern unsafe UIntPtr Remap(uint* dst, uint* indices, UIntPtr count, void* vertices, UIntPtr verticesCount, UIntPtr stride);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_remapVertexBuffer")]
            internal static extern unsafe void RemapVertices(void* dst, void* src, UIntPtr count, UIntPtr stride, uint* remap);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_remapIndexBuffer")]
            internal static extern unsafe void RemapIndices(uint* dst, uint* src, UIntPtr count, uint* remap);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyWithAttributes")]
            internal static extern unsafe UIntPtr Simplify(uint* dst, uint* indices, UIntPtr count, void* vertices, UIntPtr vertexCount, UIntPtr stride,
                void* attributes, UIntPtr attributeStride, float* weights, UIntPtr attributeCount, byte* locks, UIntPtr target, float error, uint options, float* resultError);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_spatialSortTriangles")]
            internal static extern unsafe void SpatialSort(uint* dst, uint* src, UIntPtr count, void* vertices, UIntPtr vertexCount, UIntPtr stride);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsBound")]
            internal static extern UIntPtr MeshletBound(UIntPtr count, UIntPtr maxVertices, UIntPtr maxTriangles);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshlets")]
            internal static extern unsafe UIntPtr BuildMeshlets(Meshlet* dst, uint* meshletVertices, byte* triangles, uint* indices, UIntPtr count,
                void* vertices, UIntPtr vertexCount, UIntPtr stride, UIntPtr maxVertices, UIntPtr maxTriangles, float coneWeight);
            [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "meshopt_optimizeMeshlet")]
            internal static extern unsafe void OptimizeMeshlet(uint* vertices, byte* triangles, UIntPtr triangleCount, UIntPtr vertexCount);
        }
        static UIntPtr Size(uint value) => new UIntPtr(value);
        static UIntPtr Size(int value) => new UIntPtr(checked((uint)value));
        static unsafe uint meshopt_generateVertexRemap(uint* dst, uint* indices, uint count, void* vertices, uint vertexCount, uint stride) =>
            checked((uint)NativeApi.Remap(dst, indices, Size(count), vertices, Size(vertexCount), Size(stride)).ToUInt64());
        static unsafe void meshopt_remapVertexBuffer(void* dst, void* src, uint count, uint stride, uint* remap) =>
            NativeApi.RemapVertices(dst, src, Size(count), Size(stride), remap);
        static unsafe void meshopt_remapIndexBuffer(uint* dst, uint* src, uint count, uint* remap) =>
            NativeApi.RemapIndices(dst, src, Size(count), remap);
        static unsafe uint meshopt_simplifyWithAttributes(uint* dst, uint* src, uint count, void* vertices, uint vertexCount, uint stride,
            void* attributes, uint attributeStride, float* weights, uint attributeCount, byte* locks, uint target, float error, uint options, float* resultError) =>
            checked((uint)NativeApi.Simplify(dst, src, Size(count), vertices, Size(vertexCount), Size(stride),
                attributes, Size(attributeStride), weights, Size(attributeCount), locks, Size(target), error, options, resultError).ToUInt64());

    }
}
