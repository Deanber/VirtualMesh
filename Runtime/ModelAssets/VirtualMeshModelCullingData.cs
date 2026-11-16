using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Optional, model-local bounds for one cluster, in the same order as Clusters.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct VirtualMeshModelClusterCullingData
    {
        // xyz is the GEOMETRIC normal-cone axis. w >= 1 disables cone rejection.
        public Vector4 axisCutoff;
        // A sphere around this cluster's referenced positions, not the shared LOD-group sphere.
        public Vector4 centerRadius;
        public const int Stride = 32;

        public static VirtualMeshModelClusterCullingData Disabled => new VirtualMeshModelClusterCullingData
        {
            axisCutoff = new Vector4(0, 0, 0, 1),
            // Also conservative if a consumer uses the sphere for occlusion.
            // Valid wide/degenerate cones replace this with their measured sphere.
            centerRadius = new Vector4(0, 0, 0, float.MaxValue)
        };
    }

    /// <summary>
    /// Derives conservative culling metadata from already published geometry. No native
    /// plugin, Mesh access, Unity objects or geometry simplification is required. Build
    /// can run on a worker thread while the input arrays remain immutable.
    /// </summary>
    public static class VirtualMeshModelCullingData
    {
        public const int CurrentVersion = 1;
        private const int MaximumClusterTriangles = 64;

        public static VirtualMeshModelClusterCullingData[] Build(VirtualMeshModelVertex[] vertices,
            uint[] indices, VirtualMeshModelCluster[] clusters, CancellationToken cancellationToken = default)
        {
            if (vertices == null || indices == null || clusters == null) throw new ArgumentNullException();
            var result = new VirtualMeshModelClusterCullingData[clusters.Length];
            var normals = new Vector3[MaximumClusterTriangles];
            for (int i = 0; i < clusters.Length; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                result[i] = BuildCluster(vertices, indices, clusters[i], normals);
            }
            return result;
        }

        /// <summary>Validates storage, including the deliberately disabled representation.</summary>
        public static bool IsValid(VirtualMeshModelClusterCullingData data)
        {
            if (!Finite(data.axisCutoff) || !Finite(data.centerRadius) || data.centerRadius.w < 0 ||
                data.axisCutoff.w < 0 || data.axisCutoff.w > 1) return false;
            if (data.axisCutoff.w == 1) return true;
            double length = (double)data.axisCutoff.x * data.axisCutoff.x +
                (double)data.axisCutoff.y * data.axisCutoff.y + (double)data.axisCutoff.z * data.axisCutoff.z;
            return Math.Abs(length - 1) < 0.0001;
        }

        private static VirtualMeshModelClusterCullingData BuildCluster(VirtualMeshModelVertex[] vertices,
            uint[] indices, VirtualMeshModelCluster cluster, Vector3[] normals)
        {
            var disabled = VirtualMeshModelClusterCullingData.Disabled;
            if (cluster.indexCount == 0 || cluster.indexCount % 3 != 0 ||
                cluster.indexCount / 3 > MaximumClusterTriangles ||
                (ulong)cluster.indexStart + cluster.indexCount > (ulong)indices.Length) return disabled;
            int start = (int)cluster.indexStart, end = start + (int)cluster.indexCount, normalCount = 0;
            Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int index = start; index < end; index += 3)
            {
                uint ia = indices[index], ib = indices[index + 1], ic = indices[index + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length) return disabled;
                Vector3 a = vertices[ia].position, b = vertices[ib].position, c = vertices[ic].position;
                if (!Finite(a) || !Finite(b) || !Finite(c)) return disabled;
                minimum = Vector3.Min(minimum, Vector3.Min(a, Vector3.Min(b, c)));
                maximum = Vector3.Max(maximum, Vector3.Max(a, Vector3.Max(b, c)));
                // Double intermediates avoid treating very small but nonzero faces as
                // degenerate. Authored vertex normals and normal maps are irrelevant.
                double ux = (double)b.x - a.x, uy = (double)b.y - a.y, uz = (double)b.z - a.z;
                double vx = (double)c.x - a.x, vy = (double)c.y - a.y, vz = (double)c.z - a.z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length == 0) continue;
                normals[normalCount++] = new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
            }
            // The float center used by the GPU is also used when measuring the radius.
            Vector3 center = new Vector3((float)(((double)minimum.x + maximum.x) * 0.5),
                (float)(((double)minimum.y + maximum.y) * 0.5), (float)(((double)minimum.z + maximum.z) * 0.5));
            double radiusSquared = 0;
            for (int index = start; index < end; index++)
            {
                Vector3 p = vertices[indices[index]].position;
                double dx = (double)p.x - center.x, dy = (double)p.y - center.y, dz = (double)p.z - center.z;
                radiusSquared = Math.Max(radiusSquared, dx * dx + dy * dy + dz * dz);
            }
            float radius = (float)(Math.Sqrt(radiusSquared) * 1.00001 + 0.0000001);
            if (!Finite(center) || !float.IsFinite(radius)) return disabled;
            disabled.centerRadius = new Vector4(center.x, center.y, center.z, radius);
            if (normalCount == 0) return disabled;

            // Ritter's sphere of unit face normals supplies a stable candidate axis.
            // Its optimality only affects rejection rate: the following full scan
            // independently encloses EVERY face normal, including narrow slivers.
            Vector3 axis = NormalSphereCenter(normals, normalCount);
            double axisLength = Math.Sqrt((double)axis.x * axis.x + (double)axis.y * axis.y + (double)axis.z * axis.z);
            if (axisLength < 1e-12) return disabled;
            axis = new Vector3((float)(axis.x / axisLength), (float)(axis.y / axisLength), (float)(axis.z / axisLength));
            double minimumDot = 1;
            for (int i = 0; i < normalCount; i++)
                minimumDot = Math.Min(minimumDot, (double)axis.x * normals[i].x +
                    (double)axis.y * normals[i].y + (double)axis.z * normals[i].z);
            // Wide cones are deliberately disabled. The margin covers conversion of
            // double face normals/axis to float and subsequent GPU normalization.
            minimumDot -= 0.000002;
            if (minimumDot <= 0.1) return disabled;
            float cutoff = (float)(Math.Sqrt(Math.Max(0, 1 - minimumDot * minimumDot)) + 0.00002);
            if (cutoff >= 1) return disabled;
            return new VirtualMeshModelClusterCullingData
            {
                axisCutoff = new Vector4(axis.x, axis.y, axis.z, cutoff),
                centerRadius = disabled.centerRadius
            };
        }

        private static Vector3 NormalSphereCenter(Vector3[] normals, int count)
        {
            int bestA = 0, bestB = 0;
            float bestDistance = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                int minimum = 0, maximum = 0;
                for (int i = 1; i < count; i++)
                {
                    if (normals[i][axis] < normals[minimum][axis]) minimum = i;
                    if (normals[i][axis] > normals[maximum][axis]) maximum = i;
                }
                float distance = (normals[maximum] - normals[minimum]).sqrMagnitude;
                if (distance > bestDistance) { bestDistance = distance; bestA = minimum; bestB = maximum; }
            }
            Vector3 center = (normals[bestA] + normals[bestB]) * 0.5f;
            float radius = (normals[bestA] - normals[bestB]).magnitude * 0.5f;
            for (int i = 0; i < count; i++)
            {
                Vector3 delta = normals[i] - center;
                float distance = delta.magnitude;
                if (distance <= radius) continue;
                float nextRadius = (radius + distance) * 0.5f;
                center += delta * ((nextRadius - radius) / distance);
                radius = nextRadius;
            }
            return center;
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Vector4 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
    }
}
