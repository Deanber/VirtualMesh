using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Serialized material references and asset dependencies for empirically
    /// validated leaf coverage. Material CRCs are recomputed, never serialized.</summary>
    [Serializable]
    public sealed class VirtualMeshLeafMaterialContract
    {
        public const int CurrentVersion = 1;
        [Serializable] public sealed class Variant { public Material[] Materials; }
        [Serializable] public struct Dependency { public Object Asset; public string Fingerprint; }
        [Serializable] public struct TextureState
        {
            public Texture Texture;
            public FilterMode Filter;
            public TextureWrapMode WrapU, WrapV, WrapW;
            public int Aniso;
            public float MipBias;
            public static TextureState Capture(Texture texture) => new TextureState { Texture = texture, Filter = texture.filterMode,
                WrapU = texture.wrapModeU, WrapV = texture.wrapModeV, WrapW = texture.wrapModeW, Aniso = texture.anisoLevel, MipBias = texture.mipMapBias };
            internal bool Matches() => Texture != null && !(Texture is RenderTexture) && Texture.updateCount == 0 &&
                Texture.filterMode == Filter && Texture.wrapModeU == WrapU && Texture.wrapModeV == WrapV && Texture.wrapModeW == WrapW &&
                Texture.anisoLevel == Aniso && Texture.mipMapBias == MipBias;
        }
        [SerializeField] int version;
        [SerializeField] int mipmapLimit;
        [SerializeField] bool[] validatedSlots = Array.Empty<bool>();
        [SerializeField] Variant[] variants = Array.Empty<Variant>();
        [SerializeField] Dependency[] dependencies = Array.Empty<Dependency>();
        [SerializeField] TextureState[] textures = Array.Empty<TextureState>();

        // Installed by the editor assembly. The player uses immutable imported
        // assets plus runtime texture-update/sampler checks, with no AssetDatabase.
        public static Func<Object, string> EditorDependencyFingerprintProvider;

        /// <summary>Clear once per synchronous render context. CRCs are shared
        /// across all model instances and all frozen variants in that context.</summary>
        public sealed class EvaluationContext
        {
            readonly Dictionary<Material, uint> crcs = new Dictionary<Material, uint>();
            readonly Dictionary<Object, string> fingerprints = new Dictionary<Object, string>();
            internal readonly Dictionary<VirtualMeshLeafMaterialContract, string> DependencyErrors = new Dictionary<VirtualMeshLeafMaterialContract, string>();
            public int MaterialCrcComputations { get; private set; }
            public void Clear() { crcs.Clear(); fingerprints.Clear(); DependencyErrors.Clear(); MaterialCrcComputations = 0; }
            internal uint Crc(Material material)
            {
                if (!crcs.TryGetValue(material, out uint value)) { value = unchecked((uint)material.ComputeCRC()); crcs.Add(material, value); MaterialCrcComputations++; }
                return value;
            }
            internal string Fingerprint(Object asset)
            {
                if (!fingerprints.TryGetValue(asset, out string value)) { value = EditorDependencyFingerprintProvider?.Invoke(asset); fingerprints.Add(asset, value); }
                return value;
            }
        }
        public bool IsValid
        {
            get
            {
                if (version != CurrentVersion || validatedSlots == null || validatedSlots.Length == 0 || variants == null || variants.Length == 0 || dependencies == null || textures == null) return false;
                bool any = false; foreach (bool slot in validatedSlots) any |= slot; if (!any) return false;
                foreach (var variant in variants) {
                    if (variant?.Materials == null || variant.Materials.Length != validatedSlots.Length) return false;
                    for (int i = 0; i < validatedSlots.Length; i++) if (validatedSlots[i] && variant.Materials[i] == null) return false;
                }
                foreach (var dependency in dependencies) if (dependency.Asset == null || string.IsNullOrEmpty(dependency.Fingerprint)) return false;
                foreach (var state in textures) if (state.Texture == null) return false;
                return true;
            }
        }
        public int VariantCount => variants?.Length ?? 0;
        public int SlotCount => validatedSlots?.Length ?? 0;
        public bool ValidatesSlot(int slot) => validatedSlots != null && slot >= 0 && slot < validatedSlots.Length && validatedSlots[slot];
        public Material[] FrozenMaterials
        {
            get { var result = new List<Material>(); if (variants != null) foreach (var variant in variants) if (variant?.Materials != null)
                foreach (var material in variant.Materials) if (material != null && !result.Contains(material)) result.Add(material); return result.ToArray(); }
        }
        /// <summary>Publication API. Only publish snapshots after every retained
        /// variant/slot has passed the source alpha-coverage validation.</summary>
        public static VirtualMeshLeafMaterialContract Create(bool[] slots, Variant[] materialVariants, Dependency[] assetDependencies,
            TextureState[] textureStates, int capturedMipmapLimit)
        {
            if (slots == null || materialVariants == null || assetDependencies == null || textureStates == null) throw new ArgumentNullException();
            var result = new VirtualMeshLeafMaterialContract { version = CurrentVersion, mipmapLimit = capturedMipmapLimit,
                validatedSlots = (bool[])slots.Clone(), variants = new Variant[materialVariants.Length],
                dependencies = (Dependency[])assetDependencies.Clone(), textures = (TextureState[])textureStates.Clone() };
            for (int i = 0; i < materialVariants.Length; i++) result.variants[i] = new Variant { Materials = materialVariants[i]?.Materials == null ? null : (Material[])materialVariants[i].Materials.Clone() };
            if (!result.IsValid) throw new ArgumentException("Invalid leaf material coverage contract."); return result;
        }
        public bool Matches(Material[] currentMaterials, out string reason) => Matches(currentMaterials, null, out reason);
        public bool Matches(Material[] currentMaterials, EvaluationContext context, out string reason)
        {
            reason = null;
            if (currentMaterials == null || validatedSlots == null || currentMaterials.Length != validatedSlots.Length) { reason = "Leaf material slot count differs from the validated variants."; return false; }
            if (context == null || !context.DependencyErrors.TryGetValue(this, out reason)) {
                reason = CheckDependencies(context); if (context != null) context.DependencyErrors[this] = reason;
            }
            if (reason != null) return false;
            for (int i = 0; i < currentMaterials.Length; i++) if (validatedSlots[i] && currentMaterials[i] == null) { reason = "A validated leaf material slot is empty."; return false; }
            foreach (var variant in variants) {
                bool match = true;
                for (int i = 0; i < validatedSlots.Length; i++) if (validatedSlots[i]) {
                    uint current = context != null ? context.Crc(currentMaterials[i]) : unchecked((uint)currentMaterials[i].ComputeCRC());
                    uint frozen = context != null ? context.Crc(variant.Materials[i]) : unchecked((uint)variant.Materials[i].ComputeCRC());
                    if (current != frozen) { match = false; break; }
                }
                if (match) return true;
            }
            reason = "The current leaf material array does not match any validated frozen variant."; return false;
        }
        string CheckDependencies(EvaluationContext context)
        {
            // Structural/Unity-object checks are also context-cached. Thousands of
            // instances must not repeatedly traverse the same texture dependencies.
            if (!IsValid) return "Leaf material coverage contract is missing or invalid.";
            if (QualitySettings.globalTextureMipmapLimit != mipmapLimit) return "Texture mip quality differs from leaf coverage validation.";
            foreach (var state in textures) if (!state.Matches()) return "A validated texture changed sampling state, was updated at runtime, or is dynamic.";
#if UNITY_EDITOR
            if (EditorDependencyFingerprintProvider == null) return "Editor leaf dependency verification is unavailable.";
            foreach (var dependency in dependencies) {
                string current = context != null ? context.Fingerprint(dependency.Asset) : EditorDependencyFingerprintProvider(dependency.Asset);
                if (!string.Equals(current, dependency.Fingerprint, StringComparison.Ordinal)) return "A validated shader or texture asset dependency changed.";
            }
#endif
            return null;
        }
    }
}
