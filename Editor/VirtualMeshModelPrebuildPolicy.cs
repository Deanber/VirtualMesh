using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.VirtualMesh.Runtime;
using UnityEngine;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>Conservative material classification for automatic global LOD publication.
    /// Known foliage requires a separate coverage-preserving policy, even without alpha clipping.</summary>
    public static class VirtualMeshModelPrebuildPolicy
    {
        public const int Version = 2;

        public static VirtualMeshModelLodPolicySlot Classify(Material material, int submesh = 0)
        {
            var result = new VirtualMeshModelLodPolicySlot { Submesh = submesh,
                Policy = VirtualMeshModelLodPolicy.PendingUnknown, Reason = "Missing or unsupported material" };
            if (material == null || material.shader == null) return result;
            string shader = material.shader.name;
            bool hornbeam = shader == "ElectricDreams/Source Hornbeam" || shader == "Virtual Mesh/ElectricDreams Hornbeam";
            // SourceBranch is an explicit resolved UE material branch, not a material name heuristic.
            // Decoration and Leaves remain coverage-sensitive; only an explicitly opaque Trunk branch qualifies.
            bool opaqueTrunk = hornbeam && material.HasProperty("_SourceBranch") && material.GetFloat("_SourceBranch") == 1 &&
                material.HasProperty("_OpacitySource") && material.GetFloat("_OpacitySource") == 0 &&
                material.HasProperty("_UseBaseAlpha") && material.GetFloat("_UseBaseAlpha") == 0;
            if (shader == "ElectricDreams/Source Foliage" || shader == "Virtual Mesh/ElectricDreams Foliage" || hornbeam && !opaqueTrunk)
            {
                result.Policy = VirtualMeshModelLodPolicy.PendingCoverage;
                result.Reason = "Foliage needs a coverage-preserving LOD policy";
                return result;
            }
            bool known = shader == "Universal Render Pipeline/Lit" || shader == "Universal Render Pipeline/Unlit" ||
                shader == "Virtual Mesh/Model Lit" || shader == "ElectricDreams/Source Surface" ||
                shader == "Virtual Mesh/ElectricDreams Surface" || opaqueTrunk;
            if (!known) return result;
            bool alpha = material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") != 0 ||
                material.HasProperty("_Surface") && material.GetFloat("_Surface") != 0;
            string renderType = material.GetTag("RenderType", false, "");
            // Hornbeam shares a fixed AlphaTest queue/Cutout tag across all source branches;
            // its explicit opaque Trunk branch returns full coverage when AlphaClip is off.
            bool coverageQueue = opaqueTrunk ? material.renderQueue > 2500 : material.renderQueue >= 2450;
            if (alpha || coverageQueue || !opaqueTrunk && renderType == "TransparentCutout" || renderType == "Transparent")
            {
                result.Policy = VirtualMeshModelLodPolicy.PendingCoverage;
                result.Reason = "Alpha-tested or transparent material needs coverage validation";
                return result;
            }
            result.Policy = VirtualMeshModelLodPolicy.Opaque;
            result.Reason = "All known renderer uses are supported opaque materials";
            return result;
        }

        /// <summary>Uses Unity's extra-material rule: materials beyond the slot count also draw the
        /// final submesh. A missing slot is unknown, never inferred from a neighboring material.</summary>
        public static VirtualMeshModelLodPolicySlot[] Evaluate(int submeshCount, IEnumerable<Material[]> rendererMaterials)
        {
            if (submeshCount <= 0) throw new ArgumentOutOfRangeException(nameof(submeshCount));
            var uses = rendererMaterials?.ToArray() ?? Array.Empty<Material[]>();
            var result = new VirtualMeshModelLodPolicySlot[submeshCount];
            for (int slot = 0; slot < submeshCount; slot++)
            {
                result[slot] = new VirtualMeshModelLodPolicySlot { Submesh = slot,
                    Policy = VirtualMeshModelLodPolicy.Opaque, Reason = "All known renderer uses are supported opaque materials" };
                if (uses.Length == 0) result[slot] = Classify(null, slot);
                foreach (var materials in uses)
                {
                    result[slot] = Intersect(result[slot], Classify(materials != null && slot < materials.Length ? materials[slot] : null, slot));
                    if (slot == submeshCount - 1 && materials != null)
                        for (int extra = submeshCount; extra < materials.Length; extra++)
                            result[slot] = Intersect(result[slot], Classify(materials[extra], slot));
                }
            }
            return result;
        }

        static VirtualMeshModelLodPolicySlot Intersect(VirtualMeshModelLodPolicySlot a, VirtualMeshModelLodPolicySlot b)
        {
            // Unknown is the most conservative result; coverage takes precedence over opaque.
            if (a.Policy == VirtualMeshModelLodPolicy.PendingUnknown) return a;
            if (b.Policy == VirtualMeshModelLodPolicy.PendingUnknown) return b;
            return b.Policy == VirtualMeshModelLodPolicy.PendingCoverage ? b : a;
        }

        public static VirtualMeshModelLodPolicyContext[] ReplaceContext(
            VirtualMeshModelLodPolicyContext[] previous, string sourceId, VirtualMeshModelLodPolicySlot[] slots)
        {
            if (string.IsNullOrEmpty(sourceId) || slots == null || slots.Length == 0)
                throw new ArgumentException("A material usage context and slots are required.");
            var contexts = new List<VirtualMeshModelLodPolicyContext>();
            foreach (var old in previous ?? Array.Empty<VirtualMeshModelLodPolicyContext>())
                if (old != null && old.SourceId != sourceId && !string.IsNullOrEmpty(old.SourceId))
                    contexts.Add(new VirtualMeshModelLodPolicyContext { SourceId = old.SourceId,
                        Slots = old.Slots != null && old.Slots.Length == slots.Length ? old.Slots :
                            Enumerable.Range(0, slots.Length).Select(slot => Classify(null, slot)).ToArray() });
            contexts.Add(new VirtualMeshModelLodPolicyContext { SourceId = sourceId, Slots = slots });
            return contexts.OrderBy(context => context.SourceId, StringComparer.Ordinal).ToArray();
        }

        public static VirtualMeshModelLodPolicySlot[] IntersectContexts(VirtualMeshModelLodPolicyContext[] contexts)
        {
            if (contexts == null || contexts.Length == 0 || contexts[0]?.Slots == null)
                throw new ArgumentException("At least one material usage context is required.");
            var result = (VirtualMeshModelLodPolicySlot[])contexts[0].Slots.Clone();
            foreach (var context in contexts.Skip(1))
            {
                if (context?.Slots == null || context.Slots.Length != result.Length)
                    throw new ArgumentException("All material usage contexts must have the same slot count.");
                for (int slot = 0; slot < result.Length; slot++) result[slot] = Intersect(result[slot], context.Slots[slot]);
            }
            return result;
        }

        /// <summary>Copies caller settings and limits their requested slots to the safe intersection.</summary>
        public static GlobalCoarseLodSettings CreateSettings(GlobalCoarseLodSettings settings,
            VirtualMeshModelLodPolicySlot[] policies)
        {
            var s = settings ?? new GlobalCoarseLodSettings();
            if (policies == null || policies.Length == 0) throw new ArgumentException("Missing slot policies.");
            if (s.Submeshes != null && s.Submeshes.Any(slot => slot < 0 || slot >= policies.Length))
                throw new ArgumentException("A requested submesh is outside the model.");
            var copy = new GlobalCoarseLodSettings { Submeshes = policies.Where(p => p.Policy == VirtualMeshModelLodPolicy.Opaque &&
                    (s.Submeshes == null || Array.IndexOf(s.Submeshes, p.Submesh) >= 0)).Select(p => p.Submesh).ToArray(),
                MaxLevels = s.MaxLevels, TargetTriangleCountPerSubmesh = s.TargetTriangleCountPerSubmesh,
                RelativeError = s.RelativeError, ErrorGrowth = s.ErrorGrowth, MaxRelativeError = s.MaxRelativeError,
                ColorWeight = s.ColorWeight, MinimumReduction = s.MinimumReduction,
                DefaultPixelError = s.DefaultPixelError, DefaultTargetTrianglePixels = s.DefaultTargetTrianglePixels };
            if (copy.MaxLevels < 1 || copy.MaxLevels > 16 || copy.TargetTriangleCountPerSubmesh < 1 ||
                !float.IsFinite(copy.RelativeError) || copy.RelativeError <= 0 || copy.RelativeError > 1 ||
                !float.IsFinite(copy.MaxRelativeError) || copy.MaxRelativeError < copy.RelativeError || copy.MaxRelativeError > 1 ||
                !float.IsFinite(copy.ErrorGrowth) || copy.ErrorGrowth < 1 || copy.ErrorGrowth > 4 ||
                !float.IsFinite(copy.ColorWeight) || copy.ColorWeight <= 0 || copy.ColorWeight > 10 ||
                !float.IsFinite(copy.MinimumReduction) || copy.MinimumReduction < 0 || copy.MinimumReduction >= .5f ||
                !float.IsFinite(copy.DefaultPixelError) || copy.DefaultPixelError <= 0 ||
                !float.IsFinite(copy.DefaultTargetTrianglePixels) || copy.DefaultTargetTrianglePixels < 0)
                throw new ArgumentException("Invalid automatic coarse LOD settings.");
            return copy;
        }

        public static string GetBuildHash(GlobalCoarseLodSettings settings, VirtualMeshModelLodPolicySlot[] policies)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Version); writer.Write(VirtualMeshGlobalCoarseLods.AlgorithmVersion);
                    writer.Write(settings.MaxLevels); writer.Write(settings.TargetTriangleCountPerSubmesh);
                    writer.Write(settings.RelativeError); writer.Write(settings.ErrorGrowth); writer.Write(settings.MaxRelativeError);
                    writer.Write(settings.ColorWeight); writer.Write(settings.MinimumReduction);
                    writer.Write(settings.DefaultPixelError); writer.Write(settings.DefaultTargetTrianglePixels);
                    foreach (var p in policies) { writer.Write(p.Submesh); writer.Write((int)p.Policy); }
                    writer.Write(-1);
                    foreach (int slot in settings.Submeshes) writer.Write(slot);
                }
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
