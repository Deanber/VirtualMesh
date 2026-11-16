#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System.Runtime.InteropServices;
#endif

namespace Unity.VirtualMesh.Runtime
{
    internal static class VirtualMeshHostMemory
    {
        internal static bool HasUploadHeadroom(long bytesRequired)
        {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            if (bytesRequired <= 0) return true;

            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            // AvailableCommit is the remaining commit the process can request.
            return !GlobalMemoryStatusEx(ref status) || status.AvailableCommit >= (ulong)bytesRequired;
#else
            return true;
#endif
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhysical, AvailablePhysical, CommitLimit, AvailableCommit;
            public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
#endif
    }
}
