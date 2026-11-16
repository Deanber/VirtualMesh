using System;

namespace Unity.VirtualMesh.Runtime
{
    public enum VirtualMeshModelLodPolicy
    {
        PendingUnknown = 0,
        Opaque = 1,
        PendingCoverage = 2
    }

    [Serializable]
    public struct VirtualMeshModelLodPolicySlot
    {
        public int Submesh;
        public VirtualMeshModelLodPolicy Policy;
        public string Reason;
    }

    /// <summary>Known uses of a shared mesh. Each source export replaces only its own context;
    /// all contexts must agree before a material slot receives opaque coarse levels.</summary>
    [Serializable]
    public sealed class VirtualMeshModelLodPolicyContext
    {
        public string SourceId;
        public VirtualMeshModelLodPolicySlot[] Slots;
    }
}
