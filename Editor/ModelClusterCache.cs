using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Unity.VirtualMesh.Editor
{
    internal static class ModelClusterCache
    {
        // Bump when the local cluster builder or this binary format changes.
        private const int FormatVersion = 3;
        private const uint Magic = 0x434D5655;
        private const int BufferSize = 64 * 1024;
        private const int DependencyHashSize = 32;
        private const int PackedVertexLimit = 1 << 10;
        private const int ClusterByteSize = 2 * sizeof(int) + sizeof(uint) + 2 * sizeof(float);

        internal readonly struct Key
        {
            internal readonly Mesh Mesh;
            internal readonly string Path;
            internal readonly Hash128 DependencyHash;

            internal Key(Mesh mesh, string path, Hash128 dependencyHash)
            {
                Mesh = mesh;
                Path = path;
                DependencyHash = dependencyHash;
            }
        }

        internal static bool TryLoad(Mesh mesh, float simplificationTargetError, out ModelClusters clusters, out Key key)
        {
            clusters = null;
            key = default;
            if (mesh == null || !EditorUtility.IsPersistent(mesh) || EditorUtility.IsDirty(mesh) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
                return false;

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath))
                return false;

            var dependencyHash = AssetDatabase.GetAssetDependencyHash(assetPath);
            int errorBits = BitConverter.ToInt32(BitConverter.GetBytes(simplificationTargetError), 0);
            string cachePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library",
                "VirtualMeshModelClusters", $"v{FormatVersion}", guid, $"{localId:x16}-{errorBits:x8}.clusters"));
            key = new Key(mesh, cachePath, dependencyHash);

            if (!File.Exists(cachePath))
                return false;

            try
            {
                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    BufferSize, FileOptions.SequentialScan);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion ||
                    Encoding.ASCII.GetString(reader.ReadBytes(DependencyHashSize)) != dependencyHash.ToString())
                    return false;

                int submeshCount = ReadCount(reader, 2 * sizeof(int));
                if (submeshCount != mesh.subMeshCount)
                    return false;

                var buffer = new byte[BufferSize];
                var submeshes = new ModelSubmeshClusters[submeshCount];
                for (int i = 0; i < submeshes.Length; i++)
                {
                    int inputIndexCount = reader.ReadInt32();
                    if (inputIndexCount < 0 || inputIndexCount % 3 != 0)
                        return false;

                    var groups = new ModelClusterGroup[ReadCount(reader, 4 * sizeof(int))];
                    for (int j = 0; j < groups.Length; j++)
                        groups[j] = ReadGroup(reader, buffer);

                    submeshes[i] = new ModelSubmeshClusters(inputIndexCount, groups);
                }

                if (stream.Position != stream.Length)
                    return false;

                clusters = new ModelClusters(submeshes);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool Save(Key key, ModelClusters clusters)
        {
            if (key.Path == null || key.Mesh == null || EditorUtility.IsDirty(key.Mesh))
                return false;

            // The build can yield across editor updates. Only publish if its source is still current.
            string assetPath = AssetDatabase.GetAssetPath(key.Mesh);
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.GetAssetDependencyHash(assetPath) != key.DependencyHash)
                return false;

            string cachePath = key.Path;
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           BufferSize, FileOptions.SequentialScan))
                using (var writer = new BinaryWriter(stream))
                {
                    var buffer = new byte[BufferSize];
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(Encoding.ASCII.GetBytes(key.DependencyHash.ToString()));
                    writer.Write(clusters.Submeshes.Length);
                    foreach (var submesh in clusters.Submeshes)
                    {
                        writer.Write(submesh.InputIndexCount);
                        writer.Write(submesh.Groups.Length);
                        foreach (var group in submesh.Groups)
                        {
                            writer.Write(group.LeafClusterCount);
                            WriteArray(writer, group.Vertices, buffer);
                            WriteArray(writer, group.Indices, buffer);
                            writer.Write(group.Clusters.Length);
                            foreach (var cluster in group.Clusters)
                            {
                                writer.Write(cluster.IndexCount);
                                writer.Write(cluster.Type);
                                writer.Write(cluster.Error);
                                writer.Write(cluster.ParentError);
                                writer.Write(cluster.Level);
                            }
                        }
                    }
                }

                if (File.Exists(cachePath))
                    File.Replace(temporaryPath, cachePath, null);
                else
                    File.Move(temporaryPath, cachePath);

                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The caller still has a usable result. Another writer may already have published this entry.
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupError) when (cleanupError is IOException || cleanupError is UnauthorizedAccessException)
                {
                    // A temporary file never participates in cache lookup.
                }
                return false;
            }
        }

        private static ModelClusterGroup ReadGroup(BinaryReader reader, byte[] buffer)
        {
            int leafClusterCount = reader.ReadInt32();
            var vertices = ReadArray<Vertex>(reader, buffer, PackedVertexLimit);
            var indices = ReadArray<uint>(reader, buffer);
            var clusters = new ModelCluster[ReadCount(reader, ClusterByteSize)];

            if (vertices.Length == 0 || leafClusterCount <= 0 || leafClusterCount > clusters.Length)
                throw new InvalidDataException("Invalid model cluster group size.");

            int indexOffset = 0;
            for (int i = 0; i < clusters.Length; i++)
            {
                int indexCount = reader.ReadInt32();
                uint type = reader.ReadUInt32();
                float error = reader.ReadSingle();
                float parentError = reader.ReadSingle();
                int level = reader.ReadInt32();
                if (indexCount <= 0 || indexCount % 3 != 0 || indexCount > ModelClusterBuilder.ClusterTriangleCount * 3 ||
                    indexCount > indices.Length - indexOffset || type > 3 ||
                    !IsValidError(error) || !IsValidError(parentError) || level < 0 || level > 8)
                    throw new InvalidDataException("Invalid model cluster.");

                clusters[i] = new ModelCluster(indexCount, type, error, parentError, level);
                indexOffset += indexCount;
            }

            if (indexOffset != indices.Length)
                throw new InvalidDataException("Invalid model cluster index count.");

            foreach (uint index in indices)
                if (index >= vertices.Length)
                    throw new InvalidDataException("Invalid model cluster vertex index.");

            return new ModelClusterGroup(vertices, indices, clusters, leafClusterCount);
        }

        private static bool IsValidError(float error)
        {
            return error >= 0 && !float.IsInfinity(error);
        }

        private static int ReadCount(BinaryReader reader, int minimumBytesPerItem, int maxCount = int.MaxValue)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maxCount ||
                (long)count * minimumBytesPerItem > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid model cluster data length.");

            return count;
        }

        private static unsafe T[] ReadArray<T>(BinaryReader reader, byte[] buffer, int maxCount = int.MaxValue) where T : unmanaged
        {
            var values = new T[ReadCount(reader, sizeof(T), maxCount)];
            long byteCount = (long)values.Length * sizeof(T);
            fixed (T* valuesPtr = values)
            fixed (byte* bufferPtr = buffer)
            {
                for (long offset = 0; offset < byteCount;)
                {
                    int bytes = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, byteCount - offset));
                    if (bytes == 0)
                        throw new EndOfStreamException();

                    Buffer.MemoryCopy(bufferPtr, (byte*)valuesPtr + offset, byteCount - offset, bytes);
                    offset += bytes;
                }
            }
            return values;
        }

        private static unsafe void WriteArray<T>(BinaryWriter writer, T[] values, byte[] buffer) where T : unmanaged
        {
            writer.Write(values.Length);
            long byteCount = (long)values.Length * sizeof(T);
            fixed (T* valuesPtr = values)
            fixed (byte* bufferPtr = buffer)
            {
                for (long offset = 0; offset < byteCount; offset += buffer.Length)
                {
                    int bytes = (int)Math.Min(buffer.Length, byteCount - offset);
                    Buffer.MemoryCopy((byte*)valuesPtr + offset, bufferPtr, buffer.Length, bytes);
                    writer.Write(buffer, 0, bytes);
                }
            }
        }
    }
}
