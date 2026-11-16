using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Orthographic capture basis in model space. Direction points toward the observer.
    /// Depth stores dot(position-center,Direction)/(2*halfDepth)+.5; coverage zero means no hit.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct VirtualMeshImpostorView
    {
        public Vector4 RightExtent;
        public Vector4 UpExtent;
        public Vector4 DirectionDepth;
        public const int Stride = 48;

        public Vector3 Reconstruct(Vector3 center, Vector2 uv, float depth)
        {
            return center + (Vector3)RightExtent * ((uv.x - .5f) * 2 * RightExtent.w) +
                (Vector3)UpExtent * ((uv.y - .5f) * 2 * UpExtent.w) +
                (Vector3)DirectionDepth * ((depth - .5f) * 2 * DirectionDepth.w);
        }
    }

    /// <summary>A whole-model far representation, including every original material slot.
    /// This is a distinct representation, not a synthetic cluster parent in an existing submesh.
    /// Proxy depth is excluded from current/history HiZ until conservative occluder coverage exists.</summary>
    [PreferBinarySerialization]
    public sealed class VirtualMeshModelImpostorAsset : ScriptableObject
    {
        public const int CurrentVersion = 1;
        public const string CoveragePolicy = "MinimumDistanceCoverage_NoWind_v1";
        [SerializeField] int version;
        [SerializeField] VirtualMeshModelAsset sourceModel;
        [SerializeField] int sourceGeometryRevision;
        [SerializeField] string sourceHash, bakeHash;
        [SerializeField] Material[] sourceMaterials = Array.Empty<Material>();
        [SerializeField] uint[] sourceMaterialCrcs = Array.Empty<uint>();
        [SerializeField] Bounds bounds;
        [SerializeField] VirtualMeshImpostorView[] views = Array.Empty<VirtualMeshImpostorView>();
        [SerializeField] Texture2DArray albedoCoverage, normalRoughness, depth;
        [SerializeField] float maxScreenHeight = .15f;
        // Version1 captures base coverage with distance opacity and wind disabled on clones.
        [SerializeField] float captureOpacityDistance;

        public VirtualMeshModelAsset SourceModel => sourceModel;
        public int SourceGeometryRevision => sourceGeometryRevision;
        public string SourceHash => sourceHash;
        public string BakeHash => bakeHash;
        public Material[] SourceMaterials => sourceMaterials;
        public uint[] SourceMaterialCrcs => sourceMaterialCrcs;
        public string CaptureCoveragePolicy => CoveragePolicy;
        public Bounds Bounds => bounds;
        public VirtualMeshImpostorView[] Views => views;
        public Texture2DArray AlbedoCoverage => albedoCoverage;
        public Texture2DArray NormalRoughness => normalRoughness;
        public Texture2DArray Depth => depth;
        public float MaxScreenHeight => maxScreenHeight;
        public float CaptureOpacityDistance => captureOpacityDistance;
        public int ViewCount => views?.Length ?? 0;
        public int ProxyTriangleCount => 2;
        public bool CanOcclude => false;
        public bool IsWholeModel => true;
        public bool IsValid => version == CurrentVersion && sourceModel != null && sourceModel.IsValid &&
            sourceModel.Revision == sourceGeometryRevision && sourceModel.SourceHash == sourceHash &&
            sourceMaterials != null && sourceMaterials.Length == sourceModel.SubMeshCount &&
            sourceMaterialCrcs != null && sourceMaterialCrcs.Length == sourceMaterials.Length &&
            views != null && views.Length > 0 && albedoCoverage != null && normalRoughness != null && depth != null &&
            albedoCoverage.depth == views.Length && normalRoughness.depth == views.Length && depth.depth == views.Length &&
            albedoCoverage.width == albedoCoverage.height && normalRoughness.width == albedoCoverage.width &&
            normalRoughness.height == albedoCoverage.height && depth.width == albedoCoverage.width && depth.height == albedoCoverage.height;

        public bool MatchesSource(VirtualMeshModelAsset model, Material[] materials, Func<Material, uint> crcProvider = null)
        {
            if (!IsValid || model != sourceModel || materials == null || materials.Length != sourceMaterials.Length) return false;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] == null || materials[i] != sourceMaterials[i] ||
                    (crcProvider != null ? crcProvider(materials[i]) : unchecked((uint)materials[i].ComputeCRC())) != sourceMaterialCrcs[i]) return false;
            return true;
        }

        /// <summary>Use camera observer direction for color/depth/motion, light observer direction for shadows.
        /// GPU selection must use the same maximum-dot rule and first-index tie break.</summary>
        public int SelectView(Vector3 observerDirectionOS)
        {
            if (views == null || views.Length == 0) return -1;
            float best = float.NegativeInfinity; int selected = 0;
            for (int i = 0; i < views.Length; i++)
            {
                float score = Vector3.Dot(observerDirectionOS, (Vector3)views[i].DirectionDepth);
                if (score > best) { best = score; selected = i; }
            }
            return selected;
        }

        public void SetData(VirtualMeshModelAsset model, Material[] materials, string identity,
            VirtualMeshImpostorView[] captureViews, Texture2DArray color, Texture2DArray normals,
            Texture2DArray depths, float screenHeight, float opacityDistance)
        {
            if (model == null || !model.IsValid || materials == null || materials.Length != model.SubMeshCount ||
                string.IsNullOrEmpty(identity) || captureViews == null || captureViews.Length == 0 ||
                color == null || normals == null || depths == null || color.depth != captureViews.Length ||
                normals.depth != captureViews.Length || depths.depth != captureViews.Length ||
                color.width != color.height || normals.width != color.width || normals.height != color.height ||
                depths.width != color.width || depths.height != color.height || !Finite(screenHeight) ||
                screenHeight <= 0 || screenHeight > 1 || !Finite(opacityDistance) || opacityDistance < 0)
                throw new ArgumentException("Invalid whole-model impostor data.");
            foreach (var material in materials) if (material == null) throw new ArgumentException("Every source material slot must be supplied.");
            foreach (var view in captureViews)
            {
                if (!ValidBasis(view.RightExtent) || !ValidBasis(view.UpExtent) || !ValidBasis(view.DirectionDepth) ||
                    Math.Abs(Vector3.Dot((Vector3)view.RightExtent, (Vector3)view.UpExtent)) > .001f ||
                    Math.Abs(Vector3.Dot((Vector3)view.RightExtent, (Vector3)view.DirectionDepth)) > .001f ||
                    Math.Abs(Vector3.Dot((Vector3)view.UpExtent, (Vector3)view.DirectionDepth)) > .001f)
                    throw new ArgumentException("Impostor capture basis must be finite, orthonormal and have positive extents.");
            }
            sourceModel = model; sourceGeometryRevision = model.Revision; sourceHash = model.SourceHash;
            sourceMaterials = (Material[])materials.Clone(); bakeHash = identity; bounds = model.Bounds;
            sourceMaterialCrcs = new uint[materials.Length];
            for (int i = 0; i < materials.Length; i++) sourceMaterialCrcs[i] = unchecked((uint)materials[i].ComputeCRC());
            views = (VirtualMeshImpostorView[])captureViews.Clone(); albedoCoverage = color;
            normalRoughness = normals; depth = depths; maxScreenHeight = screenHeight;
            captureOpacityDistance = opacityDistance; version = CurrentVersion;
        }

        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        static bool ValidBasis(Vector4 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w) &&
            v.w > 0 && Math.Abs(v.x * v.x + v.y * v.y + v.z * v.z - 1) < .001f;
    }
}
