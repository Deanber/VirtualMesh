using System;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Preserves the component reference in scenes saved with the retired scene-bake workflow.</summary>
    [Obsolete("Scene-level Virtual Mesh baking has been removed. Bake a model asset and use VirtualMeshInstance instead.")]
    [AddComponentMenu("")]
    public sealed class VirtualMeshManager : MonoBehaviour
    {
    }
}
