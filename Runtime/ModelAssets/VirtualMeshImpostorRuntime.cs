using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Whole-model proxy resources. Camera lists are independent; textures belong to
    /// persistent assets. No proxy depth is used as conservative HiZ occluder evidence.</summary>
    internal static class VirtualMeshImpostorRuntime
    {
        static readonly Dictionary<Material, uint> materialCrcs = new Dictionary<Material, uint>();
        static readonly Dictionary<VirtualMeshModelAsset, Counts> modelCounts = new Dictionary<VirtualMeshModelAsset, Counts>();
        static int crcGeneration = -1;
        static Shader shader;
        struct Counts { public int Revision; public long Finest, Roots; }

        internal static void BeginContext(int generation)
        {
            if (crcGeneration == generation) return;
            materialCrcs.Clear(); crcGeneration = generation;
        }
        static uint MaterialCrc(Material material)
        {
            if (!materialCrcs.TryGetValue(material, out uint crc))
            { crc = unchecked((uint)material.ComputeCRC()); materialCrcs.Add(material, crc); }
            return crc;
        }
        internal static VirtualMeshModelImpostorAsset Resolve(VirtualMeshInstance instance)
        {
            if (instance.Impostor != null && instance.Impostor.MatchesSource(instance.Model, instance.Materials, MaterialCrc))
                return instance.Impostor;
            foreach (var candidate in instance.Model.ImpostorVariants ?? Array.Empty<VirtualMeshModelImpostorAsset>())
                if (candidate != null && candidate.MatchesSource(instance.Model, instance.Materials, MaterialCrc)) return candidate;
            return null;
        }
        static Counts GetCounts(VirtualMeshModelAsset model)
        {
            if (modelCounts.TryGetValue(model, out var count) && count.Revision == model.Revision) return count;
            count = new Counts { Revision = model.Revision };
            foreach (var cluster in model.Clusters)
            {
                if (cluster.type == 0 || cluster.type == 1) count.Finest += cluster.indexCount / 3;
                if (cluster.type == 0 || cluster.type == 3) count.Roots += cluster.indexCount / 3;
            }
            modelCounts[model] = count;
            return count;
        }
        internal static long FinestTriangles(VirtualMeshModelAsset model) => GetCounts(model).Finest;
        internal static long RootTriangles(VirtualMeshModelAsset model) => GetCounts(model).Roots;

        internal readonly struct Key : IEquatable<Key>
        {
            public readonly VirtualMeshModelImpostorAsset Asset;
            public readonly int Layer;
            public readonly uint RenderingLayers;
            public readonly ulong SceneMask;
            public readonly bool Shadow, ReceiveShadows;
            public Key(VirtualMeshInstance instance, VirtualMeshModelImpostorAsset asset, bool shadow)
            {
                Asset = asset; Shadow = shadow; Layer = instance.RenderTransform.gameObject.layer;
                SceneMask = instance.RenderTransform.gameObject.sceneCullingMask;
                RenderingLayers = instance.SourceRenderer != null ? instance.SourceRenderer.renderingLayerMask : 1u;
                ReceiveShadows = instance.SourceRenderer == null || instance.SourceRenderer.receiveShadows;
            }
            public bool Equals(Key other) => Asset == other.Asset && Layer == other.Layer && RenderingLayers == other.RenderingLayers &&
                SceneMask == other.SceneMask && Shadow == other.Shadow && ReceiveShadows == other.ReceiveShadows;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { int hash = Asset.GetInstanceID(); hash = hash * 397 ^ Layer;
                    hash = hash * 397 ^ (int)RenderingLayers; hash = hash * 397 ^ SceneMask.GetHashCode();
                    return ((hash * 397 ^ (Shadow ? 1 : 0)) * 397) ^ (ReceiveShadows ? 1 : 0); }
            }
        }

        internal sealed class Batch : IDisposable
        {
            public readonly Key Key;
            public readonly List<uint> InstanceIds = new List<uint>();
            public readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            public GraphicsBuffer Candidates, Visible, Arguments, Views;
            public Material Material;
            public Bounds Bounds;
            public bool HasBounds, Disposed, ReadbackPending;
            public int Capacity, Generation, LastSampleGeneration = -1, VisibleCount;
            public double LastUsed, LastReadback;
            public int Count => InstanceIds.Count;
            public long Bytes => (long)Capacity * 8 + (Arguments != null ? 16 : 0) + (Views != null ? (long)Key.Asset.ViewCount * 48 : 0);
            public long RequiredBytes => (long)Math.Max(1, Mathf.NextPowerOfTwo(Count)) * 8 + 16 + (long)Key.Asset.ViewCount * 48;
            public Batch(Key key) { Key = key; }
            public void Reset() { InstanceIds.Clear(); HasBounds = false; }
            public void Add(int instanceId, Bounds bounds, double now)
            {
                InstanceIds.Add((uint)instanceId); LastUsed = now;
                if (HasBounds) Bounds.Encapsulate(bounds); else { Bounds = bounds; HasBounds = true; }
            }
            public void Prepare(Camera camera, GraphicsBuffer instances, GraphicsBuffer previous, int generation)
            {
                int capacity = Math.Max(1, Mathf.NextPowerOfTwo(Count));
                if (Capacity < capacity)
                {
                    Candidates?.Dispose(); Visible?.Dispose(); Capacity = capacity;
                    Candidates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 4);
                    Visible = new GraphicsBuffer(GraphicsBuffer.Target.Append, Capacity, 4);
                }
                if (Arguments == null) Arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
                if (Views == null)
                {
                    Views = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Key.Asset.ViewCount, VirtualMeshImpostorView.Stride);
                    Views.SetData(Key.Asset.Views);
                }
                if (Material == null)
                {
                    if (shader == null) shader = Resources.Load<Shader>("VirtualMeshModelImpostor");
                    if (shader == null || !shader.isSupported) throw new InvalidOperationException("Virtual Mesh whole-model impostor shader is unavailable.");
                    Material = new Material(shader) { name = Key.Asset.name + " (Virtual Mesh Proxy)", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                }
                Candidates.SetData(InstanceIds);
                Visible.SetCounterValue(0);
                if (Key.Shadow) Visible.SetData(InstanceIds);
                Arguments.SetData(new[] { new GraphicsBuffer.IndirectDrawArgs { vertexCountPerInstance = 6,
                    instanceCount = Key.Shadow ? (uint)Count : 0, startVertex = 0, startInstance = 0 } });
                Generation = generation;
                var p = Properties; var asset = Key.Asset;
                p.SetBuffer("_VMInstances", instances); p.SetBuffer("_VMPreviousObjectToWorld", previous);
                p.SetBuffer("_VMImpostorVisible", Visible); p.SetBuffer("_VMImpostorViews", Views);
                p.SetTexture("_VMImpostorAlbedoCoverage", asset.AlbedoCoverage);
                p.SetTexture("_VMImpostorNormalRoughness", asset.NormalRoughness); p.SetTexture("_VMImpostorDepth", asset.Depth);
                p.SetInt("_VMImpostorViewCount", asset.ViewCount);
                p.SetVector("_VMImpostorBoundsCenter", asset.Bounds.center); p.SetVector("_VMImpostorBoundsExtents", asset.Bounds.extents);
                p.SetVector("_VMImpostorTextureSize", new Vector4(asset.Depth.width, asset.Depth.height, 1f / asset.Depth.width, 1f / asset.Depth.height));
                p.SetVector("_VMProxyObserverPosition", camera.transform.position); p.SetVector("_VMProxyObserverDirection", -camera.transform.forward);
                p.SetInt("_VMProxyOrthographic", camera.orthographic ? 1 : 0); p.SetInt("_VMGraphDraw", Key.Shadow ? 0 : 1);
                p.SetFloat("_VMReceiveShadows", Key.ReceiveShadows ? 1 : 0);
                p.SetVector("unity_RenderingLayer", new Vector4(BitConverter.Int32BitsToSingle(unchecked((int)Key.RenderingLayers)), 0, 0, 0));
                p.SetVector("unity_LightData", new Vector4(0, 0, 1, 0)); p.SetVector("unity_ProbesOcclusion", Vector4.one);
                p.CopySHCoefficientArraysFrom(new[] { RenderSettings.ambientProbe });
            }
            public void RegisterShadow(Camera camera)
            {
                var parameters = new RenderParams(Material) { camera = camera, worldBounds = Bounds,
                    layer = Key.Layer, renderingLayerMask = Key.RenderingLayers, overrideSceneCullingMask = true,
                    sceneCullingMask = Key.SceneMask, matProps = Properties, shadowCastingMode = ShadowCastingMode.ShadowsOnly,
                    receiveShadows = false, lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                    motionVectorMode = MotionVectorGenerationMode.ForceNoMotion };
                Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, Arguments, 1);
            }
            public void Readback()
            {
                if (Key.Shadow) { VisibleCount = Count; LastSampleGeneration = Generation; return; }
                double now = Time.realtimeSinceStartupAsDouble;
                if (ReadbackPending || Disposed || !SystemInfo.supportsAsyncGPUReadback || now - LastReadback < .5) return;
                ReadbackPending = true; LastReadback = now; int requestedGeneration = Generation;
                AsyncGPUReadback.Request(Arguments, request =>
                {
                    ReadbackPending = false;
                    if (Disposed || request.hasError) return;
                    var values = request.GetData<uint>();
                    if (values.Length > 1) { VisibleCount = checked((int)values[1]); LastSampleGeneration = requestedGeneration; }
                });
            }
            public void Dispose()
            {
                if (Disposed) return; Disposed = true;
                Candidates?.Dispose(); Visible?.Dispose(); Arguments?.Dispose(); Views?.Dispose();
                if (Material != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(Material); else UnityEngine.Object.DestroyImmediate(Material); }
            }
        }

        internal sealed class CameraLists : IDisposable
        {
            public readonly Dictionary<Key, Batch> Batches = new Dictionary<Key, Batch>();
            public Batch[] Colors = Array.Empty<Batch>(), Shadows = Array.Empty<Batch>();
            public int ColorInstances, ShadowInstances, UnavailableInstances;
            readonly HashSet<VirtualMeshModelImpostorAsset> usedTextures = new HashSet<VirtualMeshModelImpostorAsset>();
            public long TextureBytes { get; private set; }
            public void Reset() { foreach (var batch in Batches.Values) batch.Reset(); ColorInstances = ShadowInstances = UnavailableInstances = 0; }
            public void Add(VirtualMeshInstance instance, VirtualMeshModelImpostorAsset asset, int index, bool shadow, Bounds bounds, double now)
            {
                var key = new Key(instance, asset, shadow);
                if (!Batches.TryGetValue(key, out var batch)) { batch = new Batch(key); Batches.Add(key, batch); }
                batch.Add(index, bounds, now);
                if (shadow) ShadowInstances++; else ColorInstances++;
            }
            public long Bytes { get { long bytes = 0; foreach (var b in Batches.Values) bytes += b.Bytes; return bytes; } }
            public long RequiredBytes { get { long bytes = 0; foreach (var b in Batches.Values) bytes += Math.Max(b.Bytes, b.Count > 0 ? b.RequiredBytes : 0); return bytes; } }
            public void Prepare(Camera camera, GraphicsBuffer instances, GraphicsBuffer shadowInstances, GraphicsBuffer previous, int generation, double now)
            {
                var colors = new List<Batch>(); var shadows = new List<Batch>(); var stale = new List<Key>();
                usedTextures.Clear(); TextureBytes = 0;
                foreach (var pair in Batches)
                {
                    var batch = pair.Value;
                    if (batch.Count == 0) { if (now - batch.LastUsed > 2) stale.Add(pair.Key); continue; }
                    batch.Prepare(camera, pair.Key.Shadow ? shadowInstances : instances, previous, generation);
                    if (usedTextures.Add(pair.Key.Asset))
                        TextureBytes += TextureBytesFor(pair.Key.Asset.AlbedoCoverage) + TextureBytesFor(pair.Key.Asset.NormalRoughness) + TextureBytesFor(pair.Key.Asset.Depth);
                    if (pair.Key.Shadow) shadows.Add(batch); else colors.Add(batch);
                }
                foreach (var key in stale) { Batches[key].Dispose(); Batches.Remove(key); }
                Colors = colors.ToArray(); Shadows = shadows.ToArray();
            }
            public void Dispose() { foreach (var b in Batches.Values) b.Dispose(); Batches.Clear(); usedTextures.Clear(); TextureBytes = 0; Colors = Shadows = Array.Empty<Batch>(); }
        }
        static long TextureBytesFor(Texture2DArray texture)
        {
            int width = texture.width, height = texture.height;
            long bytes = 0, blockWidth = GraphicsFormatUtility.GetBlockWidth(texture.graphicsFormat), blockHeight = GraphicsFormatUtility.GetBlockHeight(texture.graphicsFormat);
            long blockBytes = GraphicsFormatUtility.GetBlockSize(texture.graphicsFormat);
            for (int mip = 0; mip < texture.mipmapCount; mip++)
            {
                bytes += ((width + blockWidth - 1) / blockWidth) * ((height + blockHeight - 1) / blockHeight) * texture.depth * blockBytes;
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            return bytes;
        }
        internal static void Release() { materialCrcs.Clear(); modelCounts.Clear(); shader = null; crcGeneration = -1; }
    }
}
