using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Explicit adapters; unrecognized shaders retain their source rendering path.</summary>
    public static class VirtualMeshMaterialSupport
    {
        public const string ShadingTag = "VirtualMeshShading";
        public const string SourceShaderTag = "VirtualMeshSourceShader";

        internal static string GetShadingFamily(Material material) =>
            material != null ? material.GetTag(ShadingTag, false, string.Empty) : string.Empty;

        internal static bool HasRenderPasses(Material material) =>
            material.FindPass("ForwardLit") >= 0 && material.FindPass("DepthOnly") >= 0 &&
            material.FindPass("DepthNormals") >= 0 && material.FindPass("ShadowCaster") >= 0;

        internal static Material GetPropertyMaterial(Material source, Material template) =>
            template != null && template.GetTag(SourceShaderTag, false, string.Empty) == source.shader.name ? template : source;

        public static string GetRenderShaderName(string sourceShader)
        {
            switch (sourceShader)
            {
                case "Universal Render Pipeline/Lit":
                case "Universal Render Pipeline/Unlit":
                case "Virtual Mesh/Model Lit": return "Virtual Mesh/Model Lit";
                case "ElectricDreams/Source Foliage":
                case "Virtual Mesh/ElectricDreams Foliage": return "Virtual Mesh/ElectricDreams Foliage";
                case "ElectricDreams/Source Hornbeam":
                case "Virtual Mesh/ElectricDreams Hornbeam": return "Virtual Mesh/ElectricDreams Hornbeam";
                case "ElectricDreams/Source Surface":
                case "Virtual Mesh/ElectricDreams Surface": return "Virtual Mesh/ElectricDreams Surface";
                default: return null;
            }
        }

        public static int GetShadowCull(Material source)
        {
            if (source != null && source.HasProperty("_Cull")) return Mathf.Clamp(Mathf.RoundToInt(source.GetFloat("_Cull")), 0, 2);
            string name = source != null && source.shader != null ? source.shader.name : null;
            return name == "ElectricDreams/Source Foliage" || name == "ElectricDreams/Source Hornbeam" ||
                name == "Virtual Mesh/ElectricDreams Foliage" || name == "Virtual Mesh/ElectricDreams Hornbeam" ? 0 : 2;
        }
    }
}
