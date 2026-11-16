using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Model-specific density/geometry levels. Level zero describes the old root surface;
    /// FirstCluster=-1 means that surface is not a contiguous, uniformly selectable global level.</summary>
    [Serializable]
    public struct VirtualMeshModelLodLevel
    {
        public int Submesh, Level, FirstCluster, ClusterCount, TriangleCount;
        public float Error, TriangleSpacing, ParentError, ParentTriangleSpacing;
        // Zero is unrestricted. A positive value is a validated upper bound on
        // projected model diameter in pixels for this coverage-sensitive level.
        public float MaxProjectedPixels;
        // Reference threshold in model units per screen pixel, using this model's defaults.
        public float TransitionUnitsPerPixel;
    }

    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct VirtualMeshModelVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector4 tangent;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector4 color;
        public const int Stride = 72;
    }

    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct VirtualMeshModelCluster
    {
        // All siblings use their GROUP bounds, including every LOD. This makes
        // their error threshold identical and avoids holes at LOD transitions.
        public Vector3 center;
        public float radius;
        public uint indexStart;
        public uint indexCount;
        public uint submesh;
        public uint type;
        public float error;
        public float parentError;
        // sqrt(original group surface area / triangles in this whole LOD level).
        // Every cluster in a level shares these values; parent spacing is copied verbatim.
        public float triangleSpacing;
        public float parentTriangleSpacing;
        public const int Stride = 48;

        public bool IsSelected(float tolerance)
        {
            return type == 0 || (type == 1 && tolerance < parentError) ||
                (type == 2 && error <= tolerance && tolerance < parentError) ||
                (type == 3 && error <= tolerance);
        }

        public static float TransitionThreshold(float error, float spacing, float pixelError, float targetTrianglePixels)
        {
            float regular = error / Mathf.Max(0.000001f, pixelError);
            if (targetTrianglePixels <= 0 || spacing <= 0) return regular;
            // Density may relax geometric error by at most 2x. It cannot discard a group.
            return Mathf.Min(regular, Mathf.Max(regular * 0.5f, spacing / targetTrianglePixels));
        }

        public bool IsSelected(float modelUnitsPerPixel, float pixelError, float targetTrianglePixels)
        {
            float current = TransitionThreshold(error, triangleSpacing, pixelError, targetTrianglePixels);
            float parent = TransitionThreshold(parentError, parentTriangleSpacing, pixelError, targetTrianglePixels);
            return type == 0 || (type == 1 && modelUnitsPerPixel < parent) ||
                (type == 2 && current <= modelUnitsPerPixel && modelUnitsPerPixel < parent) ||
                (type == 3 && current <= modelUnitsPerPixel);
        }
    }

    /// <summary>
    /// Publishable, model-local geometry. Scene instances never modify or duplicate
    /// these arrays. Unlike Library caches this asset is a normal player dependency.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class VirtualMeshModelAsset : ScriptableObject
    {
        public const int CurrentFormatVersion = 3;
        [SerializeField] private int formatVersion;
        [SerializeField] private int revision;
        [SerializeField] private Mesh sourceMesh;
        [SerializeField] private float simplificationError;
        [SerializeField] private string sourceHash;
        [SerializeField] private Bounds bounds;
        [SerializeField] private int subMeshCount;
        [SerializeField] private VirtualMeshModelVertex[] vertices = Array.Empty<VirtualMeshModelVertex>();
        [SerializeField] private uint[] indices = Array.Empty<uint>();
        [SerializeField] private VirtualMeshModelCluster[] clusters = Array.Empty<VirtualMeshModelCluster>();
        // Optional derived metadata: older format-3 geometry remains valid without it.
        [SerializeField] private VirtualMeshModelClusterCullingData[] cullingData = Array.Empty<VirtualMeshModelClusterCullingData>();
        [SerializeField] private int cullingDataVersion;
        [SerializeField] private int cullingGeometryRevision;
        [SerializeField] private int cullingRevision;
        // Optional opt-in hierarchy extension. Geometry format/cluster stride remain version 3 / 48 bytes.
        [SerializeField] private int coarseLodVersion, coarseLodGeometryRevision, coarseLodBaseClusterCount;
        [SerializeField] private string coarseLodSettingsHash;
        [SerializeField] private VirtualMeshModelLodLevel[] coarseLodLevels = Array.Empty<VirtualMeshModelLodLevel>();
        // Coverage-sensitive mesh layers are independent of the opaque extension cache.
        [SerializeField] private int leafLodVersion, leafLodGeometryRevision;
        [SerializeField] private int leafLodBaseVertexCount, leafLodBaseIndexCount, leafLodBaseClusterCount;
        [SerializeField] private string leafLodSettingsHash;
        [SerializeField] private VirtualMeshModelLodLevel[] leafLodLevels = Array.Empty<VirtualMeshModelLodLevel>();
        [SerializeField] private VirtualMeshLeafMaterialContract leafMaterialContract;
        [SerializeField] private float defaultPixelError = 1f, defaultTargetTrianglePixels = 2f;
        [SerializeField] private int prebuildPolicyVersion, prebuildPolicyGeometryRevision;
        [SerializeField] private string prebuildPolicyHash;
        [SerializeField] private VirtualMeshModelLodPolicySlot[] prebuildPolicies = Array.Empty<VirtualMeshModelLodPolicySlot>();
        [SerializeField] private VirtualMeshModelLodPolicyContext[] prebuildPolicyContexts = Array.Empty<VirtualMeshModelLodPolicyContext>();
        [SerializeField] private VirtualMeshModelImpostorAsset[] impostorVariants = Array.Empty<VirtualMeshModelImpostorAsset>();
        [SerializeField] private int impostorRevision;

        public VirtualMeshModelVertex[] Vertices => vertices;
        public uint[] Indices => indices;
        public VirtualMeshModelCluster[] Clusters => clusters;
        public VirtualMeshModelClusterCullingData[] CullingData => cullingData;
        public int CullingDataVersion => cullingDataVersion;
        public int CullingRevision => cullingRevision;
        public int CoarseLodVersion => coarseLodGeometryRevision == revision ? coarseLodVersion : 0;
        public string CoarseLodSettingsHash => CoarseLodVersion != 0 ? coarseLodSettingsHash : null;
        public int CoarseLodBaseClusterCount => CoarseLodVersion != 0 ? coarseLodBaseClusterCount : 0;
        public VirtualMeshModelLodLevel[] CoarseLodLevels => coarseLodLevels;
        public int LeafLodVersion => leafLodGeometryRevision == revision ? leafLodVersion : 0;
        public string LeafLodSettingsHash => LeafLodVersion != 0 ? leafLodSettingsHash : null;
        public VirtualMeshModelLodLevel[] LeafLodLevels => leafLodLevels;
        public int LeafLodBaseVertexCount => LeafLodVersion != 0 ? leafLodBaseVertexCount : 0;
        public int LeafLodBaseIndexCount => LeafLodVersion != 0 ? leafLodBaseIndexCount : 0;
        public int LeafLodBaseClusterCount => LeafLodVersion != 0 ? leafLodBaseClusterCount : 0;
        public VirtualMeshLeafMaterialContract LeafMaterialContract => leafMaterialContract;
        public float DefaultPixelError => defaultPixelError;
        public float DefaultTargetTrianglePixels => defaultTargetTrianglePixels;
        public int PrebuildPolicyVersion => prebuildPolicyGeometryRevision == revision ? prebuildPolicyVersion : 0;
        public string PrebuildPolicyHash => PrebuildPolicyVersion != 0 ? prebuildPolicyHash : null;
        public VirtualMeshModelLodPolicySlot[] PrebuildPolicies => prebuildPolicies;
        public VirtualMeshModelLodPolicyContext[] PrebuildPolicyContexts => prebuildPolicyContexts;
        public VirtualMeshModelImpostorAsset[] ImpostorVariants => impostorVariants;
        public int ImpostorRevision => impostorRevision;
        public bool HasCullingData => IsValid && cullingDataVersion == VirtualMeshModelCullingData.CurrentVersion &&
            cullingGeometryRevision == revision && cullingData != null && cullingData.Length == clusters.Length;
        public long CullingDataBytes => HasCullingData ? (long)cullingData.Length * VirtualMeshModelClusterCullingData.Stride : 0;
        public Bounds Bounds => bounds;
        public Mesh SourceMesh => sourceMesh;
        public int SubMeshCount => subMeshCount;
        public int Revision => revision;
        public int FormatVersion => formatVersion;
        public string SourceHash => sourceHash;
        public float SimplificationError => simplificationError;
        public long GeometryBytes => (long)vertices.Length * VirtualMeshModelVertex.Stride +
            (long)indices.Length * sizeof(uint) + (long)clusters.Length * VirtualMeshModelCluster.Stride;
        public bool IsValid => formatVersion == CurrentFormatVersion && subMeshCount > 0 &&
            vertices != null && vertices.Length > 0 && indices != null && indices.Length > 0 &&
            clusters != null && clusters.Length > 0;

        /// <summary>Marks a completed optional build (including a topology-limited no-op). This is
        /// separate from geometry format so old format-3 assets remain loadable.</summary>
        public void SetCoarseLodMetadata(int algorithmVersion, string settingsHash, int baseClusterCount,
            VirtualMeshModelLodLevel[] levels, float pixelError, float targetTrianglePixels)
        {
            if (!IsValid || algorithmVersion <= 0 || string.IsNullOrEmpty(settingsHash) ||
                baseClusterCount <= 0 || baseClusterCount > clusters.Length || levels == null ||
                !float.IsFinite(pixelError) || pixelError <= 0 ||
                !float.IsFinite(targetTrianglePixels) || targetTrianglePixels < 0)
                throw new ArgumentException("Invalid coarse LOD metadata.");
            foreach (var level in levels)
                if (level.Submesh < 0 || level.Submesh >= subMeshCount || level.Level < 0 ||
                    level.FirstCluster < -1 || level.ClusterCount <= 0 || level.TriangleCount <= 0 ||
                    level.FirstCluster >= 0 && (long)level.FirstCluster + level.ClusterCount > clusters.Length ||
                    !float.IsFinite(level.Error) || level.Error < 0 ||
                    !float.IsFinite(level.TriangleSpacing) || level.TriangleSpacing < 0 ||
                    !float.IsFinite(level.ParentError) || level.ParentError < 0 ||
                    !float.IsFinite(level.ParentTriangleSpacing) || level.ParentTriangleSpacing < 0 ||
                    !float.IsFinite(level.MaxProjectedPixels) || level.MaxProjectedPixels < 0 ||
                    level.MaxProjectedPixels > 0 && level.Error <= 0 ||
                    !float.IsFinite(level.TransitionUnitsPerPixel) || level.TransitionUnitsPerPixel < 0)
                    throw new ArgumentException("Invalid coarse LOD level table.");
            coarseLodVersion = algorithmVersion;
            coarseLodGeometryRevision = revision;
            coarseLodSettingsHash = settingsHash;
            coarseLodBaseClusterCount = baseClusterCount;
            coarseLodLevels = levels;
            defaultPixelError = pixelError;
            defaultTargetTrianglePixels = targetTrianglePixels;
        }

        /// <summary>Records separately validated alpha-clipped mesh layers. Source geometry prefixes
        /// are retained, and every added layer must have a positive coverage eligibility limit.</summary>
        public void SetLeafLodMetadata(int algorithmVersion, string settingsHash, int baseVertexCount,
            int baseIndexCount, int baseClusterCount, VirtualMeshModelLodLevel[] levels)
        {
            if (!IsValid || algorithmVersion <= 0 || string.IsNullOrEmpty(settingsHash) || levels == null ||
                baseVertexCount <= 0 || baseVertexCount > vertices.Length || baseIndexCount <= 0 ||
                baseIndexCount > indices.Length || baseClusterCount <= 0 || baseClusterCount > clusters.Length)
                throw new ArgumentException("Invalid leaf LOD metadata.");
            foreach (var row in levels)
                if (row.Submesh < 0 || row.Submesh >= subMeshCount || row.Level < 0 || row.FirstCluster < -1 ||
                    row.ClusterCount <= 0 || row.TriangleCount <= 0 ||
                    row.FirstCluster >= 0 && (long)row.FirstCluster + row.ClusterCount > clusters.Length ||
                    !float.IsFinite(row.Error) || row.Error < 0 || !float.IsFinite(row.TriangleSpacing) || row.TriangleSpacing < 0 ||
                    !float.IsFinite(row.ParentError) || row.ParentError < 0 ||
                    !float.IsFinite(row.ParentTriangleSpacing) || row.ParentTriangleSpacing < 0 ||
                    !float.IsFinite(row.TransitionUnitsPerPixel) || row.TransitionUnitsPerPixel < 0 ||
                    !float.IsFinite(row.MaxProjectedPixels) || row.MaxProjectedPixels < 0 ||
                    row.Level > 0 && (row.MaxProjectedPixels <= 0 || row.Error <= 0 || row.FirstCluster < baseClusterCount))
                    throw new ArgumentException("Invalid leaf LOD level table.");
            leafLodVersion = algorithmVersion; leafLodGeometryRevision = revision; leafLodSettingsHash = settingsHash;
            leafLodBaseVertexCount = baseVertexCount; leafLodBaseIndexCount = baseIndexCount;
            leafLodBaseClusterCount = baseClusterCount; leafLodLevels = levels;
            leafMaterialContract = null;
        }

        public void SetLeafMaterialContract(VirtualMeshLeafMaterialContract contract)
        {
            if (contract != null) {
                if (!contract.IsValid || LeafLodVersion == 0 || contract.SlotCount != subMeshCount)
                    throw new ArgumentException("A leaf material contract requires a matching valid published leaf hierarchy.");
                foreach (var row in leafLodLevels)
                    if (row.Level > 0 && !contract.ValidatesSlot(row.Submesh))
                        throw new ArgumentException("Every added leaf material slot must have a validated material snapshot.");
            }
            leafMaterialContract = contract;
        }

        /// <summary>Registers whole-model material variants without invalidating the geometry or
        /// freshly captured source revision. Stale variants remain unusable through MatchesSource.</summary>
        public void SetImpostorVariants(VirtualMeshModelImpostorAsset[] variants)
        {
            if (variants == null) throw new ArgumentNullException(nameof(variants));
            foreach (var variant in variants)
                if (variant == null || variant.SourceModel != this)
                    throw new ArgumentException("Every impostor variant must reference this model.");
            impostorVariants = (VirtualMeshModelImpostorAsset[])variants.Clone();
            impostorRevision = unchecked(impostorRevision + 1);
        }

        /// <summary>Records material safety decisions without changing the geometry revision.</summary>
        public void SetPrebuildPolicyMetadata(int version, string hash, VirtualMeshModelLodPolicySlot[] policies,
            VirtualMeshModelLodPolicyContext[] contexts)
        {
            if (!IsValid || version <= 0 || string.IsNullOrEmpty(hash) || policies == null ||
                policies.Length != subMeshCount || contexts == null || contexts.Length == 0)
                throw new ArgumentException("Invalid model prebuild policy metadata.");
            for (int i = 0; i < policies.Length; i++)
                if (policies[i].Submesh != i || (int)policies[i].Policy < 0 || (int)policies[i].Policy > 2)
                    throw new ArgumentException("Invalid material slot policy.");
            foreach (var context in contexts)
                if (context == null || string.IsNullOrEmpty(context.SourceId) || context.Slots == null ||
                    context.Slots.Length != subMeshCount)
                    throw new ArgumentException("Invalid material usage context.");
            prebuildPolicyVersion = version;
            prebuildPolicyGeometryRevision = revision;
            prebuildPolicyHash = hash;
            prebuildPolicies = policies;
            prebuildPolicyContexts = contexts;
        }

        /// <summary>Publishes derived data without changing geometry or its Revision.</summary>
        public void SetCullingData(VirtualMeshModelClusterCullingData[] data)
        {
            if (!IsValid || data == null || data.Length != clusters.Length)
                throw new ArgumentException("Culling data must match every cluster in a valid model.", nameof(data));
            foreach (var item in data)
                if (!VirtualMeshModelCullingData.IsValid(item)) throw new ArgumentException("Invalid cluster culling data.", nameof(data));
            cullingData = data;
            cullingDataVersion = VirtualMeshModelCullingData.CurrentVersion;
            cullingGeometryRevision = revision;
            cullingRevision = unchecked(cullingRevision + 1);
        }

        /// <summary>One-time managed derivation. Returns false when already current.</summary>
        public bool EnsureCullingData()
        {
            if (HasCullingData) return false;
            if (!IsValid) throw new InvalidOperationException("Build valid model geometry before deriving culling data.");
            SetCullingData(VirtualMeshModelCullingData.Build(vertices, indices, clusters));
            return true;
        }

        public void SetData(Mesh sourceMesh, float simplificationError, string sourceHash,
            VirtualMeshModelVertex[] vertices, uint[] indices, VirtualMeshModelCluster[] clusters,
            int subMeshCount, Bounds bounds)
        {
            if (vertices == null || indices == null || clusters == null || subMeshCount <= 0)
                throw new ArgumentException("A model needs vertices, indices, clusters and material slots.");
            if (vertices.Length == 0 || indices.Length == 0 || clusters.Length == 0)
                throw new ArgumentException("The model contains no triangle clusters.");
            foreach (uint index in indices)
                if (index >= vertices.Length) throw new ArgumentException("Model index is outside its vertex buffer.");
            foreach (var cluster in clusters)
            {
                if (cluster.indexCount == 0 || cluster.indexCount > 192 || cluster.indexCount % 3 != 0 ||
                    (ulong)cluster.indexStart + cluster.indexCount > (ulong)indices.Length ||
                    cluster.submesh >= subMeshCount || cluster.type > 3 ||
                    !float.IsFinite(cluster.radius) || cluster.radius < 0 ||
                    !float.IsFinite(cluster.error) || cluster.error < 0 ||
                    !float.IsFinite(cluster.parentError) || cluster.parentError < 0 ||
                    !float.IsFinite(cluster.triangleSpacing) || cluster.triangleSpacing < 0 ||
                    !float.IsFinite(cluster.parentTriangleSpacing) || cluster.parentTriangleSpacing < 0)
                    throw new ArgumentException("Invalid model cluster metadata.");
            }
            this.sourceMesh = sourceMesh;
            this.simplificationError = simplificationError;
            this.sourceHash = sourceHash;
            this.vertices = vertices;
            this.indices = indices;
            this.clusters = clusters;
            this.subMeshCount = subMeshCount;
            this.bounds = bounds;
            formatVersion = CurrentFormatVersion;
            revision = unchecked(revision + 1);
            cullingData = Array.Empty<VirtualMeshModelClusterCullingData>();
            cullingDataVersion = 0;
            cullingGeometryRevision = 0;
            cullingRevision = unchecked(cullingRevision + 1);
            coarseLodVersion = coarseLodGeometryRevision = coarseLodBaseClusterCount = 0;
            coarseLodSettingsHash = null;
            coarseLodLevels = Array.Empty<VirtualMeshModelLodLevel>();
            leafLodVersion = leafLodGeometryRevision = leafLodBaseVertexCount = leafLodBaseIndexCount = leafLodBaseClusterCount = 0;
            leafLodSettingsHash = null; leafLodLevels = Array.Empty<VirtualMeshModelLodLevel>();
            leafMaterialContract = null;
            prebuildPolicyVersion = prebuildPolicyGeometryRevision = 0;
            prebuildPolicyHash = null;
            prebuildPolicies = Array.Empty<VirtualMeshModelLodPolicySlot>();
            prebuildPolicyContexts = Array.Empty<VirtualMeshModelLodPolicyContext>();
            VirtualMeshGraphRuntime.InvalidateModel(this);
        }
    }
}
