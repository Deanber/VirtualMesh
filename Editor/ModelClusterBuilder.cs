using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Editor
{
    internal sealed class ModelClusters
    {
        internal readonly ModelSubmeshClusters[] Submeshes;
        internal ModelClusters(ModelSubmeshClusters[] submeshes)
        {
            Submeshes = submeshes;
        }
    }

    internal sealed class ModelSubmeshClusters
    {
        internal readonly int InputIndexCount;
        internal readonly ModelClusterGroup[] Groups;

        internal ModelSubmeshClusters(int inputIndexCount, ModelClusterGroup[] groups)
        {
            InputIndexCount = inputIndexCount;
            Groups = groups;
        }
    }

    internal sealed class ModelClusterGroup
    {
        internal readonly Vertex[] Vertices;
        internal readonly uint[] Indices;
        internal readonly ModelCluster[] Clusters;
        internal readonly int LeafClusterCount;

        // Root clusters are stored first. The final LeafClusterCount clusters use original triangles.
        internal ModelClusterGroup(Vertex[] vertices, uint[] indices, ModelCluster[] clusters, int leafClusterCount)
        {
            Vertices = vertices;
            Indices = indices;
            Clusters = clusters;
            LeafClusterCount = leafClusterCount;
        }
    }

    internal readonly struct ModelCluster
    {
        internal readonly int IndexCount;
        internal readonly uint Type;
        internal readonly float Error;
        internal readonly float ParentError;
        // Geometric errors can tie (for example when simplifying a flat grid).
        // Keep an explicit level so density metadata can still distinguish those LODs.
        internal readonly int Level;

        internal ModelCluster(int indexCount, uint type, float error, float parentError, int level = 0)
        {
            IndexCount = indexCount;
            Type = type;
            Error = error;
            ParentError = parentError;
            Level = level;
        }
    }

    internal static class ModelClusterBuilder
    {
        internal const int ClusterTriangleCount = 64;
        private const int ClusterVertexCount = 120;
        private const int PartitionTargetSize = 5;
        // The page format has 10 bits per index. Keep even unusually large partitions within it.
        private const int MaxLeafClustersPerGroup = 1024 / ClusterVertexCount;
        private const int MaxSimplificationSteps = 8;
        private static readonly uint VertexStride = (uint)UnsafeUtility.SizeOf<Vertex>();

        internal static IEnumerable<string> Build(Mesh mesh, float targetError, ModelSubmeshClusters[] result)
        {
            var meshName = mesh.name;
            var vertices = ReadVertices(mesh);
            for (int slot = 0; slot < result.Length; slot++)
            {
                if (mesh.GetTopology(slot) != MeshTopology.Triangles || mesh.GetIndexCount(slot) == 0)
                {
                    result[slot] = new ModelSubmeshClusters(0, Array.Empty<ModelClusterGroup>());
                    continue;
                }

                yield return $"Building model clusters: {meshName}, submesh {slot + 1} / {result.Length}";
                var sourceIndices = mesh.GetTriangles(slot);
                if (sourceIndices.Length == 0 || sourceIndices.Length % 3 != 0)
                    throw new InvalidOperationException($"Mesh '{meshName}' has an invalid triangle buffer in submesh {slot}.");

                var indices = new uint[sourceIndices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    uint index = (uint)sourceIndices[i];
                    if (index >= vertices.Length)
                        throw new InvalidOperationException($"Mesh '{meshName}' references a missing vertex in submesh {slot}.");
                    indices[i] = index;
                }
                var (submeshVertices, submeshIndices) = MeshOperations.Reindex(vertices, indices, VertexStride);
                float absoluteTargetError = targetError * GetPositionScale(submeshVertices);
                var leaves = BuildMeshlets(submeshIndices, submeshVertices);
                var partitions = Partition(leaves, submeshVertices);

                var groups = new List<ModelClusterGroup>(partitions.Length);
                for (int p = 0; p < partitions.Length; p++)
                {
                    if (p > 0 && p % 32 == 0)
                        yield return $"Building model clusters: {meshName}, partition {p + 1} / {partitions.Length}";

                    var partition = partitions[p];
                    for (int first = 0; first < partition.Count; first += MaxLeafClustersPerGroup)
                    {
                        int count = Math.Min(MaxLeafClustersPerGroup, partition.Count - first);
                        groups.Add(BuildGroup(partition, first, count, submeshVertices, absoluteTargetError));
                    }
                }
                result[slot] = new ModelSubmeshClusters(sourceIndices.Length, groups.ToArray());
            }
        }

        private static Vertex[] ReadVertices(Mesh mesh)
        {
            Mesh copy = null;
            try
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.Normal) ||
                    (mesh.HasVertexAttribute(VertexAttribute.TexCoord0) && !mesh.HasVertexAttribute(VertexAttribute.Tangent)))
                {
                    copy = UnityEngine.Object.Instantiate(mesh);
                    if (!copy.HasVertexAttribute(VertexAttribute.Normal))
                        copy.RecalculateNormals();
                    if (copy.HasVertexAttribute(VertexAttribute.TexCoord0) && !copy.HasVertexAttribute(VertexAttribute.Tangent))
                        copy.RecalculateTangents();
                    mesh = copy;
                }

                var positions = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uv0 = mesh.uv;
                var uv1 = mesh.uv2;
                var colors = mesh.colors;
                var vertices = new Vertex[positions.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    var color = colors.Length != 0 ? colors[i] : Color.white;
                    vertices[i] = new Vertex
                    {
                        position = positions[i],
                        normal = normals.Length != 0 ? normals[i] : Vector3.zero,
                        tangent = tangents.Length != 0 ? tangents[i] : Vector4.zero,
                        uv0 = uv0.Length != 0 ? uv0[i] : Vector2.zero,
                        uv1 = uv1.Length != 0 ? uv1[i] : Vector2.zero,
                        color = new Vector4(color.r, color.g, color.b, color.a)
                    };
                }
                return vertices;
            }
            finally
            {
                if (copy != null)
                    UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static float GetPositionScale(Vertex[] vertices)
        {
            Vector3 min = vertices[0].position;
            Vector3 max = min;
            foreach (var vertex in vertices)
            {
                min = Vector3.Min(min, vertex.position);
                max = Vector3.Max(max, vertex.position);
            }
            var size = max - min;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        }

        private static uint[][] BuildMeshlets(uint[] indices, Vertex[] vertices)
        {
            MeshOperations.SpatialSortTriangles(indices, vertices, VertexStride);
            var capacity = MeshOperations.BuildMeshletsBound((uint)indices.Length, ClusterVertexCount, ClusterTriangleCount);
            var meshlets = new Meshlet[capacity];
            var meshletVertices = new uint[capacity * ClusterVertexCount];
            var meshletTriangles = new byte[capacity * ClusterTriangleCount * 3];
            var count = MeshOperations.BuildMeshlets(meshlets, meshletVertices, meshletTriangles, indices, vertices,
                VertexStride, ClusterVertexCount, ClusterTriangleCount, 0f);

            var clusters = new uint[count][];
            for (int i = 0; i < count; i++)
            {
                var meshlet = meshlets[i];
                var cluster = new uint[meshlet.triangleCount * 3];
                for (int j = 0; j < cluster.Length; j++)
                    cluster[j] = meshletVertices[meshlet.vertexOffset + meshletTriangles[meshlet.triangleOffset + j]];
                clusters[i] = cluster;
            }
            return clusters;
        }

        private static List<uint[]>[] Partition(uint[][] leaves, Vertex[] vertices)
        {
            var sizes = new uint[leaves.Length];
            int indexCount = 0;
            for (int i = 0; i < leaves.Length; i++)
            {
                sizes[i] = (uint)leaves[i].Length;
                indexCount += leaves[i].Length;
            }

            var indices = new uint[indexCount];
            int offset = 0;
            foreach (var leaf in leaves)
            {
                Array.Copy(leaf, 0, indices, offset, leaf.Length);
                offset += leaf.Length;
            }

            var partitionIds = new uint[leaves.Length];
            var count = MeshOperations.PartitionMeshlets(partitionIds, indices, sizes, vertices, PartitionTargetSize);
            var partitions = new List<uint[]>[count];
            for (int i = 0; i < partitions.Length; i++)
                partitions[i] = new List<uint[]>();
            for (int i = 0; i < leaves.Length; i++)
                partitions[partitionIds[i]].Add(leaves[i]);
            return partitions;
        }

        private static ModelClusterGroup BuildGroup(List<uint[]> partition, int first, int count,
            Vertex[] sourceVertices, float absoluteTargetError)
        {
            var vertices = new List<Vertex>();
            var vertexMap = new Dictionary<uint, uint>();
            var clusterIndices = new List<uint[]>(count);
            var clusters = new List<ModelCluster>(count);
            var combinedLeaves = new List<uint>();

            for (int i = first; i < first + count; i++)
            {
                var source = partition[i];
                var indices = new uint[source.Length];
                for (int j = 0; j < source.Length; j++)
                {
                    uint sourceIndex = source[j];
                    if (!vertexMap.TryGetValue(sourceIndex, out uint groupIndex))
                    {
                        groupIndex = (uint)vertices.Count;
                        vertexMap.Add(sourceIndex, groupIndex);
                        vertices.Add(sourceVertices[sourceIndex]);
                    }
                    indices[j] = groupIndex;
                }
                combinedLeaves.AddRange(indices);
                clusterIndices.Add(indices);
                clusters.Add(new ModelCluster(indices.Length, count == 1 ? 0u : 1u, 0f, 0f));
            }

            var groupVertices = vertices.ToArray();
            if (count > 1)
                BuildHierarchy(groupVertices, combinedLeaves.ToArray(), count, absoluteTargetError, clusterIndices, clusters);

            int indexCount = 0;
            foreach (var cluster in clusters)
                indexCount += cluster.IndexCount;
            var groupIndices = new uint[indexCount];
            var packedClusters = new ModelCluster[clusters.Count];
            int offset = 0;
            for (int i = clusters.Count - 1; i >= 0; i--)
            {
                var indices = clusterIndices[i];
                MeshOperations.SpatialSortTriangles(indices, groupVertices, VertexStride);
                MeshOperations.OptimizeVertexCache(indices, (uint)groupVertices.Length);
                MeshOperations.OptimizeOverdraw(indices, groupVertices, VertexStride, 1f);
                Array.Copy(indices, 0, groupIndices, offset, indices.Length);
                offset += indices.Length;
                packedClusters[clusters.Count - 1 - i] = clusters[i];
            }
            MeshOperations.OptimizeVertexFetch(groupIndices, groupVertices, VertexStride);
            return new ModelClusterGroup(groupVertices, groupIndices, packedClusters, count);
        }

        private static void BuildHierarchy(Vertex[] vertices, uint[] indices, int levelCount, float absoluteTargetError,
            List<uint[]> clusterIndices, List<ModelCluster> clusters)
        {
            float scale = GetPositionScale(vertices);
            if (scale == 0f)
            {
                MarkRoot(clusters, 0);
                return;
            }

            // meshoptimizer's relative error uses the input vertex bounds. Keep the same tolerance
            // in model units while passing only this group's vertices to each simplification.
            float relativeTargetError = absoluteTargetError / scale;
            var resultError = new float[1];
            float accumulatedError = 0f;
            for (int step = 0; step < MaxSimplificationSteps; step++)
            {
                int levelStart = clusters.Count - levelCount;
                uint simplifiedCount = MeshOperations.Simplify(indices, vertices, VertexStride,
                    (uint)(indices.Length / 2), relativeTargetError, 0x1, resultError);
                if (simplifiedCount == 0 || simplifiedCount >= indices.Length)
                {
                    MarkRoot(clusters, levelStart);
                    break;
                }

                Array.Resize(ref indices, (int)simplifiedCount);
                var parents = BuildMeshlets(indices, vertices);
                if (parents.Length >= levelCount)
                {
                    MarkRoot(clusters, levelStart);
                    break;
                }

                accumulatedError += resultError[0] * scale;
                for (int i = levelStart; i < clusters.Count; i++)
                {
                    var cluster = clusters[i];
                    clusters[i] = new ModelCluster(cluster.IndexCount, cluster.Type, cluster.Error, accumulatedError, cluster.Level);
                }

                bool reachedRoot = parents.Length == 1 || step == MaxSimplificationSteps - 1;
                foreach (var parent in parents)
                {
                    clusterIndices.Add(parent);
                    // A zero-error reduction is still a useful coarser level. Equal geometric
                    // thresholds form empty half-open intervals rather than overlapping LODs.
                    clusters.Add(new ModelCluster(parent.Length, reachedRoot ? 3u : 2u, accumulatedError, 0f, step + 1));
                }

                if (reachedRoot)
                    break;
                levelCount = parents.Length;
            }
        }

        private static void MarkRoot(List<ModelCluster> clusters, int first)
        {
            for (int i = first; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                clusters[i] = new ModelCluster(cluster.IndexCount, first == 0 ? 0u : 3u, cluster.Error, 0f, cluster.Level);
            }
        }
    }
}
