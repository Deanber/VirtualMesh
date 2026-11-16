using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.VirtualMesh.Runtime;
using Object = UnityEngine.Object;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>Creates immutable material subassets only after validated leaf geometry
    /// is ready to publish. CRCs remain process-local comparisons, never asset data.</summary>
    [InitializeOnLoad]
    public static class VirtualMeshLeafMaterialContractBuilder
    {
        static readonly Dictionary<Object, string> fingerprints = new Dictionary<Object, string>();
        static VirtualMeshLeafMaterialContractBuilder()
        {
            VirtualMeshLeafMaterialContract.EditorDependencyFingerprintProvider = DependencyFingerprint;
            EditorApplication.projectChanged += ClearDependencyCache;
        }
        public static void ClearDependencyCache() => fingerprints.Clear();
        static string DependencyFingerprint(Object asset)
        {
            if (asset == null) return null;
            if (fingerprints.TryGetValue(asset, out string value)) return value;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            // GUID/hash are asset identities, independent of an absolute project
            // folder. Built-in resources are explicitly scoped to the engine version.
            value = string.IsNullOrEmpty(guid) ? "builtin:" + Application.unityVersion + ":" + path + ":" + asset.name :
                guid + ":" + AssetDatabase.GetAssetDependencyHash(path).ToString();
            fingerprints.Add(asset, value); return value;
        }
        public sealed class Prepared : IDisposable
        {
            internal VirtualMeshModelAsset Owner;
            internal Material[][] Sources;
            internal Material[][] Frozen;
            internal bool[] Slots;
            internal VirtualMeshLeafMaterialContract.Dependency[] Dependencies;
            internal VirtualMeshLeafMaterialContract.TextureState[] Textures;
            internal int MipmapLimit;
            internal bool Committed, Disposed;
            /// <summary>Safe after the model's staged geometry publication: only
            /// source materials/textures are compared, not the old model Revision.</summary>
            public void ValidateSourceUnchanged()
            {
                if (Disposed || Committed) throw new InvalidOperationException("Leaf material preparation is no longer pending.");
                if (QualitySettings.globalTextureMipmapLimit != MipmapLimit) throw new InvalidOperationException("Mip quality changed during leaf validation.");
                for (int variant = 0; variant < Sources.Length; variant++) for (int slot = 0; slot < Slots.Length; slot++) if (Slots[slot]) {
                    var source = Sources[variant][slot]; var frozen = Frozen[variant][slot];
                    if (source == null || frozen == null || source.ComputeCRC() != frozen.ComputeCRC())
                        throw new InvalidOperationException("A source material changed during leaf coverage validation.");
                }
                foreach (var state in Textures) if (state.Texture == null || state.Texture is RenderTexture || state.Texture.updateCount != 0 ||
                    state.Texture.filterMode != state.Filter || state.Texture.wrapModeU != state.WrapU || state.Texture.wrapModeV != state.WrapV || state.Texture.wrapModeW != state.WrapW ||
                    state.Texture.anisoLevel != state.Aniso || state.Texture.mipMapBias != state.MipBias)
                    throw new InvalidOperationException("A source texture changed or is dynamic; leaf material publication was canceled.");
                foreach (var dependency in Dependencies) if (DependencyFingerprint(dependency.Asset) != dependency.Fingerprint)
                    throw new InvalidOperationException("A source shader/texture asset dependency changed during leaf validation.");
            }
            public void Dispose()
            {
                if (Disposed) return; Disposed = true;
                if (Frozen == null) return;
                foreach (var variant in Frozen) foreach (var material in variant) if (material != null && !Committed) Object.DestroyImmediate(material);
            }
        }
        public static Prepared Prepare(VirtualMeshModelAsset model, Material[] sources, IEnumerable<int> candidateSlots) =>
            Prepare(model, new[] { sources }, candidateSlots);
        public static Prepared Prepare(VirtualMeshModelAsset model, IEnumerable<Material[]> materialVariants, IEnumerable<int> candidateSlots)
        {
            if (model == null || !model.IsValid || materialVariants == null || candidateSlots == null) throw new ArgumentException("A model, complete material variants and validated slot set are required.");
            var sources = materialVariants.Select(v => v == null ? null : (Material[])v.Clone()).ToArray();
            if (sources.Length == 0 || sources.Any(v => v == null || v.Length != model.SubMeshCount)) throw new ArgumentException("Every material variant must have exactly the model's material slot count.");
            var slots = new bool[model.SubMeshCount]; foreach (int slot in candidateSlots) {
                if (slot < 0 || slot >= slots.Length) throw new ArgumentOutOfRangeException(nameof(candidateSlots)); slots[slot] = true; }
            if (!slots.Any(v => v)) throw new ArgumentException("At least one candidate leaf slot is required.");
            var result = new Prepared { Owner = model, Sources = sources, Frozen = sources.Select(v => new Material[v.Length]).ToArray(), Slots = slots,
                MipmapLimit = QualitySettings.globalTextureMipmapLimit };
            try
            {
                var dependencies = new Dictionary<Object, string>(); var textures = new Dictionary<Texture, VirtualMeshLeafMaterialContract.TextureState>();
                for (int variant = 0; variant < sources.Length; variant++) for (int slot = 0; slot < slots.Length; slot++) if (slots[slot]) {
                    var source = sources[variant][slot]; if (source == null || source.shader == null) throw new ArgumentException("A candidate leaf material is missing.");
                    var frozen = new Material(source) { name = source.name + " (Validated Leaf v" + variant + " s" + slot + ")" }; result.Frozen[variant][slot] = frozen;
                    if (source.ComputeCRC() != frozen.ComputeCRC()) throw new InvalidOperationException("Cloning a source material changed its render properties.");
                    AddDependency(source.shader);
                    foreach (int id in source.GetTexturePropertyNameIDs()) {
                        var texture = source.GetTexture(id); if (texture == null) continue;
                        if (texture is RenderTexture || texture.updateCount != 0) throw new InvalidOperationException("Leaf coverage cannot publish a dynamic or runtime-updated texture.");
                        AddDependency(texture); if (!textures.ContainsKey(texture)) textures.Add(texture, VirtualMeshLeafMaterialContract.TextureState.Capture(texture));
                    }
                }
                result.Dependencies = dependencies.Select(pair => new VirtualMeshLeafMaterialContract.Dependency { Asset = pair.Key, Fingerprint = pair.Value }).ToArray();
                result.Textures = textures.Values.ToArray(); result.ValidateSourceUnchanged(); return result;
                void AddDependency(Object asset) {
                    string hash = DependencyFingerprint(asset); if (string.IsNullOrEmpty(hash)) throw new InvalidOperationException("Validated leaf shader/texture dependencies must be saved/imported assets.");
                    dependencies[asset] = hash;
                }
            }
            catch { result.Dispose(); throw; }
        }
        /// <summary>Call synchronously after SetLeafLodMetadata and before the caller's
        /// SaveAssets. On failure the geometry remains ineligible until a valid contract
        /// is published. No original renderer fallback is requested by this API.</summary>
        public static void Commit(VirtualMeshModelAsset model, Prepared prepared)
        {
            if (model == null || prepared == null || model != prepared.Owner || model.LeafLodVersion <= 0) throw new ArgumentException("Prepared materials must belong to the model's completed leaf publication.");
            prepared.ValidateSourceUnchanged();
            var slots = new bool[model.SubMeshCount];
            foreach (var row in model.LeafLodLevels) if (row.Level > 0) slots[row.Submesh] = true;
            if (!slots.Any(v => v)) throw new InvalidOperationException("No added leaf geometry requires a material contract.");
            for (int i = 0; i < slots.Length; i++) if (slots[i] && !prepared.Slots[i]) throw new InvalidOperationException("An added leaf slot has no validated frozen material.");
            var variants = prepared.Frozen.Select(v => new VirtualMeshLeafMaterialContract.Variant { Materials = v.Select((m, i) => slots[i] ? m : null).ToArray() }).ToArray();
            var contract = VirtualMeshLeafMaterialContract.Create(slots, variants, prepared.Dependencies, prepared.Textures, prepared.MipmapLimit);
            var added = new List<Material>();
            try
            {
                if (AssetDatabase.Contains(model)) foreach (var material in contract.FrozenMaterials) { AssetDatabase.AddObjectToAsset(material, model); added.Add(material); }
                // No await or worker boundary between final source check and publish.
                prepared.ValidateSourceUnchanged(); model.SetLeafMaterialContract(contract); EditorUtility.SetDirty(model); prepared.Committed = true;
                // Candidate slots without a successful added layer are not retained.
                foreach (var variant in prepared.Frozen) for (int i = 0; i < variant.Length; i++) if (!slots[i] && variant[i] != null) { Object.DestroyImmediate(variant[i]); variant[i] = null; }
            }
            catch
            {
                foreach (var material in added) if (material != null) Object.DestroyImmediate(material, true);
                throw;
            }
        }
    }
    sealed class VirtualMeshLeafMaterialContractImports : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) =>
            VirtualMeshLeafMaterialContractBuilder.ClearDependencyCache();
    }
}
