using System;
using UnityEngine.Rendering.Universal;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Preserves renderer data serialized with the retired scene-bake workflow.</summary>
    [Obsolete("Scene-level Virtual Mesh rendering has been removed. Use VirtualMeshModelRenderFeature instead.")]
    [SupportedOnRenderer()]
    public sealed class VirtualMeshRenderFeature : ScriptableRendererFeature
    {
        public override void Create()
        {
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
        }
    }
}
