using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Unity.VirtualMesh.Editor
{
    public sealed class LeafCoverageValidation
    {
        public bool Accepted;
        public float MaxProjectedPixels, CoverageErrorWorld;
        public string Reason;
        public object Report;
    }

    [Serializable]
    public sealed class LeafCoverageValidationOptions
    {
        public int[] Resolutions = { 128, 256 };
        public Bounds? CaptureBounds;
        public float MaximumProjectedPixels = 128;
        public float MinimumProjectedPixels = 8;
        public float ContourTolerancePixels = 1;
        public float CoverageErrorSafety = 1.25f;
        public float MeanCoverageErrorLimit = .08f;
        public float WorstCoverageErrorLimit = .20f;
        public float MinimumAreaRatio = .75f, MaximumAreaRatio = 1.25f;
        public bool SaveMaskPngs;
        public string ReportDirectory;
        public Action<int, int> Progress;
    }

    [Serializable]
    public sealed class LeafCoverageViewResult
    {
        public int Resolution, Direction, ReferencePixels, CandidatePixels, MissingPixels, ExtraPixels;
        public float OpacityFactor, CoverageError, AreaRatio, MeanMissingDistancePixels, MeanExtraDistancePixels;
        public float MaximumMissingDistancePixels, MaximumExtraDistancePixels, WorldUnitsPerPixel;
        public bool ReferenceEmptyWithExtraCoverage, ReferenceCacheReused;
    }

    [Serializable]
    public sealed class LeafCoverageValidationReport
    {
        public bool Supported, Accepted;
        public string Reason, ModelPath, ModelName, MaterialPath, MaterialName, ShaderName, MaterialFingerprint;
        public string TextureWarmupBefore, TextureWarmupAfter;
        public bool TextureWarmupChangedState;
        public int ModelRevision, Submesh, ReferenceTriangles, CandidateTriangles, MipmapLimit, DirectionCount;
        public float MaxProjectedPixels, CoverageErrorWorld, MeanCoverageError, WorstCoverageError;
        public float MinimumAreaRatio = 1, MaximumAreaRatio = 1;
        public Bounds CaptureBounds;
        public int[] Resolutions;
        public float[] OpacityFactors, ObserverDistances;
        public LeafCoverageViewResult[] Views;
        public string Scope = "Empirical finite-view alpha coverage gate against original finest source triangles. " +
            "Twelve orthographic directions and configured resolutions/opacity endpoints do not prove every perspective, " +
            "wind deformation, mip state or lighting appearance. No proxy/model/scene asset is published.";
    }

    /// <summary>Editor-thread, cancellable, source-material coverage validation. Original type0/1
    /// triangles are the reference; type2/3 and prior coarse candidates never become the oracle.</summary>
    public static class VirtualMeshLeafCoverageValidator
    {
        const string PassName = "VMImpostorBake";
        const long ReferenceCacheLimit = 64L * 1024 * 1024;
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static readonly Dictionary<string, bool[]> ReferenceCache = new Dictionary<string, bool[]>();
        static readonly Queue<string> ReferenceOrder = new Queue<string>();
        static long referenceCacheBytes;
        static readonly Vector3[] Directions = MakeDirections();
        // Keep the public mask metrics genuinely managed: shader IDs initialize only
        // when GPU capture is requested, so the metric can run in an offline test host.
        static class CaptureIds
        {
            public static readonly int Center = Shader.PropertyToID("_VMImpostorCenter"), Direction = Shader.PropertyToID("_VMImpostorDirectionDepth"),
                Camera = Shader.PropertyToID("_WorldSpaceCameraPos"), Alpha = Shader.PropertyToID("_AlphaToMaskAvailable");
        }

        sealed class MeshData
        {
            public Vector3[] Positions, Normals;
            public Vector4[] Tangents;
            public Vector2[] UV0, UV1;
            public Color[] Colors;
            public int[] Indices;
        }

        sealed class CaptureTargets : IDisposable
        {
            public readonly int Size;
            public readonly RenderTexture Color, Normal, LinearDepth, Z;
            public readonly Texture2D Readback;
            public CaptureTargets(int size)
            {
                Size = size;
                try
                {
                    Color = Target(size, GraphicsFormat.R8G8B8A8_UNorm, "Leaf coverage");
                    Normal = Target(size, GraphicsFormat.R8G8B8A8_UNorm, "Leaf capture normal");
                    LinearDepth = Target(size, GraphicsFormat.R32_SFloat, "Leaf capture depth");
                    Z = new RenderTexture(size, size, 32, RenderTextureFormat.Depth) { name = "Leaf validation Z", antiAliasing = 1, hideFlags = HideFlags.HideAndDontSave };
                    if (!Z.Create()) throw new InvalidOperationException("Could not allocate leaf validation depth.");
                    Readback = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { Release(Color); Release(Normal); Release(LinearDepth); Release(Z); Destroy(Readback); }
        }

        public static async Task<LeafCoverageValidation> ValidateAsync(VirtualMeshModelAsset source, Material[] materials,
            int submesh, VirtualMeshModelVertex[] candidateVertices, uint[] candidateIndices,
            LeafCoverageValidationOptions options, CancellationToken cancellationToken)
        {
            if (SynchronizationContext.Current == null) throw new InvalidOperationException("Begin leaf validation on the Unity editor thread.");
            cancellationToken.ThrowIfCancellationRequested();
            if (source == null || !source.IsValid) throw new ArgumentException("A valid original model is required.", nameof(source));
            if (submesh < 0 || submesh >= source.SubMeshCount) throw new ArgumentOutOfRangeException(nameof(submesh));
            if (materials == null || materials.Length != source.SubMeshCount) throw new ArgumentException("Pass the complete source material array.", nameof(materials));
            if (candidateVertices == null || candidateIndices == null || candidateIndices.Length % 3 != 0) throw new ArgumentException("Candidate arrays must describe complete triangles.");
            var settings = Snapshot(options ?? new LeafCoverageValidationOptions());
            var material = materials[submesh];
            var report = new LeafCoverageValidationReport { ModelPath = AssetDatabase.GetAssetPath(source), ModelName = source.name,
                ModelRevision = source.Revision, Submesh = submesh, CandidateTriangles = candidateIndices.Length / 3,
                MaterialPath = material != null ? AssetDatabase.GetAssetPath(material) : null, MaterialName = material != null ? material.name : null,
                ShaderName = material != null && material.shader != null ? material.shader.name : null,
                MipmapLimit = QualitySettings.globalTextureMipmapLimit, DirectionCount = Directions.Length, Resolutions = settings.Resolutions };
            string unsupported = UnsupportedReason(material);
            if (unsupported != null) return Finish(report, false, unsupported, settings);
            report.Supported = true;
            if (candidateIndices.Length == 0) return Finish(report, false, "Candidate has no surviving triangles.", settings);
            Bounds bounds = settings.CaptureBounds ?? source.Bounds;
            ValidateBounds(bounds, source.Bounds);
            report.CaptureBounds = bounds;
            report.MaterialFingerprint = Fingerprint(material);
            string materialProperties = Fingerprint(material, false);
            var sourceVertices = source.Vertices; var sourceIndices = source.Indices; var sourceClusters = source.Clusters;
            string sourceHash = source.SourceHash;
            int editorThread = Thread.CurrentThread.ManagedThreadId;
            await Gate.WaitAsync(cancellationToken);
            Mesh original = null, candidate = null; Material capture = null;
            Scene preview = default;
            var targets = new Dictionary<int, CaptureTargets>(); var rows = new List<LeafCoverageViewResult>();
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckSource();
                var referenceIndices = await Task.Run(() => FinestIndices(sourceClusters, sourceIndices, submesh, cancellationToken), cancellationToken);
                report.ReferenceTriangles = referenceIndices.Length / 3;
                if (referenceIndices.Length == 0) return Finish(report, false, "Original finest submesh has no triangles.", settings);
                var data = await Task.Run(() => new[] { Compact(sourceVertices, referenceIndices, bounds, cancellationToken),
                    Compact(candidateVertices, candidateIndices, bounds, cancellationToken) }, cancellationToken);
                original = MakeMesh(data[0], bounds, source.name + " finest leaf validation");
                candidate = MakeMesh(data[1], bounds, source.name + " candidate leaf validation");
                preview = EditorSceneManager.NewPreviewScene();
                capture = new Material(material) { name = material.name + " (Temporary Leaf Validation)", hideFlags = HideFlags.HideAndDontSave };
                var originalRenderer = MakeRenderer(preview, original, capture);
                var candidateRenderer = MakeRenderer(preview, candidate, capture);
                // Supported source shaders have a known static-wind condition. Never silently
                // validate an animated source using a frozen replacement.
                Set(capture, "_WindEnabled", 0); Set(capture, "_WindAmplitude", 0);
                bool distanceOpacity = material.shader.name == "ElectricDreams/Source Hornbeam" && material.GetFloat("_SourceBranch") < .5f &&
                    material.GetFloat("_DistanceOpacity") > .5f && material.GetFloat("_AlphaClip") > .5f;
                float farStrength = distanceOpacity ? material.GetFloat("_FarOpacityStrength") : 1;
                float originalIntensity = material.GetFloat("_OpacityIntensity");
                if (!Finite(farStrength) || farStrength < 0 || !Finite(originalIntensity) || originalIntensity < 0)
                    return Finish(report, false, "Source opacity parameters must be finite and nonnegative.", settings);
                float opacityDistance = distanceOpacity ? Mathf.Max(material.GetFloat("_OpacityDistance"), .001f) : 0;
                if (!Finite(opacityDistance)) return Finish(report, false, "Source opacity distance must be finite.", settings);
                // Keep the original shader multiplication order and half arithmetic intact.
                // DrawRenderer supplies the true fixed renderer bounds; only the camera global
                // moves to sample near/middle/far. The orthographic view matrix remains fixed.
                report.OpacityFactors = distanceOpacity ? new[] { 1f, Mathf.Lerp(1f, farStrength, .5f), farStrength } : new[] { 1f };
                report.ObserverDistances = distanceOpacity ? new[] { 0f, opacityDistance * .6f, opacityDistance * 1.2f } : new[] { 0f };
                ShaderUtil.allowAsyncCompilation = false;
                int pass = capture.FindPass(PassName);
                ShaderUtil.CompilePass(capture, pass, true);
                foreach (int size in settings.Resolutions) targets.Add(size, new CaptureTargets(size));
                // A first GPU use may finish uploading Unity's default textures and
                // advance updateCount without changing material content. Prime the
                // source synchronously BEFORE freezing the texture-content baseline.
                // No await, user callback or candidate acceptance occurs in this span.
                // Geometry, material properties/references and mip settings remain
                // strictly checked on both sides; all later texture updates abort.
                CheckSource();
                report.TextureWarmupBefore = report.MaterialFingerprint;
                Basis(bounds, Directions[0], out var warmRight, out var warmUp, out float warmExtent, out float warmDepth);
                Capture(originalRenderer, capture, pass, bounds, Directions[0], warmRight, warmUp, warmExtent, warmDepth,
                    report.ObserverDistances[0], targets[settings.Resolutions[0]]);
                CheckSource(false);
                report.MaterialFingerprint = Fingerprint(material);
                report.TextureWarmupAfter = report.MaterialFingerprint;
                report.TextureWarmupChangedState = report.TextureWarmupBefore != report.TextureWarmupAfter;
                int complete = 0, total = settings.Resolutions.Length * Directions.Length * report.OpacityFactors.Length;
                string identity = source.GetInstanceID() + ":" + report.ModelRevision + ":" + sourceHash + ":" + submesh + ":" +
                    report.MaterialFingerprint + ":" + report.MipmapLimit + ":" + BoundsKey(bounds);
                for (int opacityIndex = 0; opacityIndex < report.OpacityFactors.Length; opacityIndex++)
                {
                    float opacityFactor = report.OpacityFactors[opacityIndex];
                    float observerDistance = report.ObserverDistances[opacityIndex];
                    foreach (int size in settings.Resolutions)
                    {
                        for (int directionIndex = 0; directionIndex < Directions.Length; directionIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); CheckSource();
                            var direction = Directions[directionIndex];
                            Basis(bounds, direction, out var right, out var up, out float extent, out float halfDepth);
                            string key = identity + ":" + size + ":" + directionIndex + ":" + FloatKey(opacityFactor) + ":" + FloatKey(observerDistance);
                            bool reused = ReferenceCache.TryGetValue(key, out var referenceMask);
                            if (!reused)
                            {
                                referenceMask = Capture(originalRenderer, capture, pass, bounds, direction, right, up, extent, halfDepth, observerDistance, targets[size]);
                                CacheReference(key, referenceMask);
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            var candidateMask = Capture(candidateRenderer, capture, pass, bounds, direction, right, up, extent, halfDepth, observerDistance, targets[size]);
                            var row = await Task.Run(() => CompareMasks(referenceMask, candidateMask, size, cancellationToken), cancellationToken);
                            row.Direction = directionIndex; row.OpacityFactor = opacityFactor; row.WorldUnitsPerPixel = 2 * extent / size; row.ReferenceCacheReused = reused;
                            rows.Add(row);
                            if (settings.SaveMaskPngs)
                            {
                                string suffix = "v" + directionIndex + "-r" + size + "-opacity" + opacityIndex;
                                SaveMasks(settings.ReportDirectory, source.name + "-m" + material.GetInstanceID() + "-s" + submesh + "-t" + report.CandidateTriangles + "-" + suffix, referenceMask, candidateMask, size);
                            }
                            settings.Progress?.Invoke(++complete, total);
                            // No graphics globals or active target remain changed across this yield.
                            await Task.Yield();
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested(); CheckSource();
                report.Views = rows.ToArray(); report.Supported = true;
                report.MeanCoverageError = rows.Average(r => r.CoverageError); report.WorstCoverageError = rows.Max(r => r.CoverageError);
                var occupied = rows.Where(r => r.ReferencePixels > 0).ToArray();
                if (occupied.Length == 0) return Finish(report, false, "The original finest surface has no coverage at the tested views/resolutions.", settings);
                report.MinimumAreaRatio = occupied.Min(r => r.AreaRatio); report.MaximumAreaRatio = occupied.Max(r => r.AreaRatio);
                float worldError = rows.Max(r => Mathf.Max(r.MaximumMissingDistancePixels, r.MaximumExtraDistancePixels) * r.WorldUnitsPerPixel);
                report.CoverageErrorWorld = worldError * settings.CoverageErrorSafety;
                float diameter = bounds.extents.magnitude * 2;
                float derivedPixels = report.CoverageErrorWorld > 0 ? settings.ContourTolerancePixels * diameter / report.CoverageErrorWorld : settings.MaximumProjectedPixels;
                // Never extrapolate to a model larger than the smallest explicitly tested frame.
                report.MaxProjectedPixels = Mathf.Min(settings.MaximumProjectedPixels, settings.Resolutions.Min(), derivedPixels);
                bool accepted = report.MeanCoverageError <= settings.MeanCoverageErrorLimit && report.WorstCoverageError <= settings.WorstCoverageErrorLimit &&
                    report.MinimumAreaRatio >= settings.MinimumAreaRatio && report.MaximumAreaRatio <= settings.MaximumAreaRatio &&
                    !rows.Any(r => r.ReferenceEmptyWithExtraCoverage) && Finite(report.MaxProjectedPixels) && report.MaxProjectedPixels >= settings.MinimumProjectedPixels;
                return Finish(report, accepted, accepted ? "Finite-view alpha coverage gates passed; use only at or below the reported projected size." :
                    "Coverage difference, area ratio, isolated missing/extra coverage, or the minimum usable projected size failed the gate.", settings);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync;
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                Destroy(original); Destroy(candidate); Destroy(capture); foreach (var target in targets.Values) target.Dispose();
                Gate.Release();
            }

            void CheckSource(bool includeTextureState = true)
            {
                if (Thread.CurrentThread.ManagedThreadId != editorThread) throw new InvalidOperationException("Leaf GPU capture left the editor thread.");
                string currentFingerprint = Fingerprint(material, includeTextureState);
                string expectedFingerprint = includeTextureState ? report.MaterialFingerprint : materialProperties;
                if (source == null || source.Revision != report.ModelRevision || source.SourceHash != sourceHash ||
                    !ReferenceEquals(source.Vertices, sourceVertices) || !ReferenceEquals(source.Indices, sourceIndices) || !ReferenceEquals(source.Clusters, sourceClusters) ||
                    materials[submesh] != material || currentFingerprint != expectedFingerprint || QualitySettings.globalTextureMipmapLimit != report.MipmapLimit)
                    throw new InvalidOperationException("Source geometry, material/texture properties or mip quality changed during leaf validation; retry before publication. " +
                        "Fingerprint before=" + expectedFingerprint + "; after=" + currentFingerprint +
                        "; revision=" + report.ModelRevision + "/" + (source != null ? source.Revision : -1) +
                        "; mip=" + report.MipmapLimit + "/" + QualitySettings.globalTextureMipmapLimit);
            }
        }

        public static void ClearReferenceCache()
        { ReferenceCache.Clear(); ReferenceOrder.Clear(); referenceCacheBytes = 0; }

        static LeafCoverageValidation Finish(LeafCoverageValidationReport report, bool accepted, string reason, LeafCoverageValidationOptions settings)
        {
            report.Accepted = accepted; report.Reason = reason;
            if (!string.IsNullOrEmpty(settings.ReportDirectory))
            {
                string path = Path.Combine(settings.ReportDirectory, Sanitize(report.ModelName) + "-" + Sanitize(report.MaterialName) + "-s" + report.Submesh + "-t" + report.CandidateTriangles + "-coverage.json");
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
            return new LeafCoverageValidation { Accepted = accepted, MaxProjectedPixels = report.MaxProjectedPixels,
                CoverageErrorWorld = report.CoverageErrorWorld, Reason = reason, Report = report };
        }

        static string UnsupportedReason(Material material)
        {
            if (material == null || material.shader == null ||
                material.shader.name != "ElectricDreams/Source Hornbeam" && material.shader.name != "ElectricDreams/Source Foliage") return "Unsupported source shader; alpha-aware source capture is required.";
            if (material.FindPass(PassName) < 0) return "Source capture pass unavailable. Initialize the project's URP before validation.";
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || SystemInfo.supportedRenderTargetCount < 3 ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render)) return "Leaf validation requires a graphics device and three supported MRTs.";
            if (!material.HasProperty("_WindEnabled") || !material.HasProperty("_WindAmplitude")) return "Source wind behavior is not known; no frozen-wind validation is allowed.";
            float wind = material.GetFloat("_WindEnabled"), amplitude = material.GetFloat("_WindAmplitude");
            if (!Finite(wind) || !Finite(amplitude) || wind >= .5f && amplitude > 0) return "Effective source wind is nonzero/invalid; a static capture cannot certify this candidate.";
            return null;
        }

        static LeafCoverageValidationOptions Snapshot(LeafCoverageValidationOptions o)
        {
            if (o.Resolutions == null || o.Resolutions.Length == 0 || o.Resolutions.Any(s => s < 16 || s > 1024 || (s & (s - 1)) != 0) ||
                !Finite(o.MaximumProjectedPixels) || !Finite(o.MinimumProjectedPixels) || o.MinimumProjectedPixels <= 0 || o.MaximumProjectedPixels < o.MinimumProjectedPixels ||
                !Finite(o.ContourTolerancePixels) || o.ContourTolerancePixels <= 0 || !Finite(o.CoverageErrorSafety) || o.CoverageErrorSafety < 1 ||
                !Finite(o.MeanCoverageErrorLimit) || o.MeanCoverageErrorLimit < 0 || !Finite(o.WorstCoverageErrorLimit) || o.WorstCoverageErrorLimit < 0 ||
                !Finite(o.MinimumAreaRatio) || o.MinimumAreaRatio < 0 || o.MinimumAreaRatio > 1 || !Finite(o.MaximumAreaRatio) || o.MaximumAreaRatio < 1)
                throw new ArgumentException("Invalid leaf coverage validation settings.");
            string directory = null;
            if (!string.IsNullOrEmpty(o.ReportDirectory))
            {
                string project = Directory.GetParent(Application.dataPath).FullName;
                string logs = Path.GetFullPath(Path.Combine(project, "Logs"));
                directory = Path.GetFullPath(Path.IsPathRooted(o.ReportDirectory) ? o.ReportDirectory : Path.Combine(project, o.ReportDirectory));
                if (!directory.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Diagnostic leaf captures must stay below this project's Logs directory, not Assets.");
                Directory.CreateDirectory(directory);
            }
            if (o.SaveMaskPngs && directory == null) throw new ArgumentException("SaveMaskPngs requires a Logs report directory.");
            return new LeafCoverageValidationOptions { Resolutions = o.Resolutions.Distinct().OrderBy(s => s).ToArray(), CaptureBounds = o.CaptureBounds,
                MaximumProjectedPixels = o.MaximumProjectedPixels, MinimumProjectedPixels = o.MinimumProjectedPixels, ContourTolerancePixels = o.ContourTolerancePixels,
                CoverageErrorSafety = o.CoverageErrorSafety, MeanCoverageErrorLimit = o.MeanCoverageErrorLimit, WorstCoverageErrorLimit = o.WorstCoverageErrorLimit,
                MinimumAreaRatio = o.MinimumAreaRatio, MaximumAreaRatio = o.MaximumAreaRatio, ReportDirectory = directory, SaveMaskPngs = o.SaveMaskPngs, Progress = o.Progress };
        }

        static uint[] FinestIndices(VirtualMeshModelCluster[] clusters, uint[] indices, int submesh, CancellationToken token)
        {
            var output = new List<uint>();
            foreach (var c in clusters)
            {
                token.ThrowIfCancellationRequested();
                if (c.submesh != submesh || c.type != 0 && c.type != 1) continue;
                long end = (long)c.indexStart + c.indexCount;
                if (c.indexCount % 3 != 0 || end > indices.Length) throw new InvalidDataException("Invalid original finest cluster index range.");
                for (long i = c.indexStart; i < end; i++) output.Add(indices[i]);
            }
            return output.ToArray();
        }

        static MeshData Compact(VirtualMeshModelVertex[] vertices, uint[] indices, Bounds bounds, CancellationToken token)
        {
            var remap = new Dictionary<uint, int>(); var compact = new List<VirtualMeshModelVertex>(); var output = new int[indices.Length];
            float tolerance = Mathf.Max(.00001f, bounds.extents.magnitude * .00001f); var acceptedBounds = bounds; acceptedBounds.Expand(tolerance * 2);
            for (int i = 0; i < indices.Length; i++)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                uint old = indices[i]; if (old >= vertices.Length) throw new InvalidDataException("Leaf capture index exceeds its vertex buffer.");
                if (!remap.TryGetValue(old, out int next))
                {
                    var v = vertices[old];
                    if (!Finite(v.position.x) || !Finite(v.position.y) || !Finite(v.position.z) || !acceptedBounds.Contains(v.position))
                        throw new InvalidDataException("Capture bounds do not contain the complete original/candidate surface.");
                    remap.Add(old, next = compact.Count); compact.Add(v);
                }
                output[i] = next;
            }
            var data = new MeshData { Positions = new Vector3[compact.Count], Normals = new Vector3[compact.Count], Tangents = new Vector4[compact.Count],
                UV0 = new Vector2[compact.Count], UV1 = new Vector2[compact.Count], Colors = new Color[compact.Count], Indices = output };
            for (int i = 0; i < compact.Count; i++)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                var v = compact[i]; data.Positions[i] = v.position; data.Normals[i] = v.normal; data.Tangents[i] = v.tangent;
                data.UV0[i] = v.uv0; data.UV1[i] = v.uv1; data.Colors[i] = v.color;
            }
            return data;
        }

        static Mesh MakeMesh(MeshData data, Bounds bounds, string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            try
            {
                mesh.vertices = data.Positions; mesh.normals = data.Normals; mesh.tangents = data.Tangents; mesh.uv = data.UV0; mesh.uv2 = data.UV1; mesh.colors = data.Colors;
                mesh.SetIndices(data.Indices, MeshTopology.Triangles, 0, false); mesh.bounds = bounds; return mesh;
            }
            catch { Destroy(mesh); throw; }
        }

        static MeshRenderer MakeRenderer(Scene scene, Mesh mesh, Material material)
        {
            var gameObject = new GameObject(mesh.name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return renderer;
        }

        static bool[] Capture(MeshRenderer renderer, Material material, int pass, Bounds bounds, Vector3 direction, Vector3 right, Vector3 up, float extent, float halfDepth, float observerDistance, CaptureTargets t)
        {
            var previousActive = RenderTexture.active; var center = Shader.GetGlobalVector(CaptureIds.Center); var depth = Shader.GetGlobalVector(CaptureIds.Direction);
            var camera = Shader.GetGlobalVector(CaptureIds.Camera); float alpha = Shader.GetGlobalFloat(CaptureIds.Alpha);
            try
            {
                const float near = .01f; Vector3 eye = bounds.center + direction * (halfDepth + near);
                var view = Matrix4x4.identity;
                view.SetRow(0, new Vector4(right.x, right.y, right.z, -Vector3.Dot(right, eye)));
                view.SetRow(1, new Vector4(up.x, up.y, up.z, -Vector3.Dot(up, eye)));
                view.SetRow(2, new Vector4(direction.x, direction.y, direction.z, -Vector3.Dot(direction, eye)));
                var projection = Matrix4x4.Ortho(-extent, extent, -extent, extent, near, halfDepth * 2 + near);
                using (var cmd = new CommandBuffer { name = "Validate original/candidate leaf coverage" })
                {
                    cmd.SetRenderTarget(new RenderTargetIdentifier[] { t.Color, t.Normal, t.LinearDepth }, t.Z);
                    cmd.SetViewport(new Rect(0, 0, t.Size, t.Size)); cmd.ClearRenderTarget(true, true, Color.clear, 1);
                    cmd.SetViewProjectionMatrices(view, projection);
                    cmd.SetGlobalVector(CaptureIds.Center, bounds.center); cmd.SetGlobalVector(CaptureIds.Direction, new Vector4(direction.x, direction.y, direction.z, halfDepth));
                    cmd.SetGlobalVector(CaptureIds.Camera, bounds.center + Vector3.forward * observerDistance); cmd.SetGlobalFloat(CaptureIds.Alpha, 0);
                    cmd.DrawRenderer(renderer, material, 0, pass); Graphics.ExecuteCommandBuffer(cmd);
                }
                RenderTexture.active = t.Color; t.Readback.ReadPixels(new Rect(0, 0, t.Size, t.Size), 0, 0, false); t.Readback.Apply(false, false);
                var pixels = t.Readback.GetPixels32(); var mask = new bool[pixels.Length];
                for (int i = 0; i < mask.Length; i++) mask[i] = pixels[i].a >= 128;
                return mask;
            }
            finally
            {
                RenderTexture.active = previousActive; Shader.SetGlobalVector(CaptureIds.Center, center); Shader.SetGlobalVector(CaptureIds.Direction, depth);
                Shader.SetGlobalVector(CaptureIds.Camera, camera); Shader.SetGlobalFloat(CaptureIds.Alpha, alpha);
            }
        }

        /// <summary>Pure managed mask metric; public for independent CPU regression tests.</summary>
        public static LeafCoverageViewResult CompareMasks(bool[] reference, bool[] candidate, int size, CancellationToken cancellationToken)
        {
            if (reference == null || candidate == null || size < 1 || size > 4096 || reference.Length != (long)size * size || candidate.Length != reference.Length)
                throw new ArgumentException("Masks must have the declared square size.");
            var result = new LeafCoverageViewResult { Resolution = size };
            for (int i = 0; i < reference.Length; i++)
            {
                if ((i & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (reference[i]) result.ReferencePixels++; if (candidate[i]) result.CandidatePixels++;
                if (reference[i] && !candidate[i]) result.MissingPixels++; if (!reference[i] && candidate[i]) result.ExtraPixels++;
            }
            result.ReferenceEmptyWithExtraCoverage = result.ReferencePixels == 0 && result.CandidatePixels > 0;
            result.CoverageError = (result.MissingPixels + result.ExtraPixels) / (float)Math.Max(1, result.ReferencePixels);
            result.AreaRatio = result.ReferencePixels == 0 ? (result.CandidatePixels == 0 ? 1 : 0) : result.CandidatePixels / (float)result.ReferencePixels;
            if (result.MissingPixels > 0) Distances(reference, candidate, size, result.MissingPixels, cancellationToken, out result.MeanMissingDistancePixels, out result.MaximumMissingDistancePixels);
            if (result.ExtraPixels > 0) Distances(candidate, reference, size, result.ExtraPixels, cancellationToken, out result.MeanExtraDistancePixels, out result.MaximumExtraDistancePixels);
            return result;
        }

        static void Distances(bool[] from, bool[] to, int size, int count, CancellationToken token, out float mean, out float maximum)
        {
            var distance = DistanceField(to, size, token); double sum = 0; maximum = 0;
            for (int i = 0; i < from.Length; i++) if (from[i] && !to[i]) { float d = (float)Math.Sqrt(distance[i]); sum += d; maximum = Math.Max(maximum, d); }
            mean = (float)(sum / Math.Max(1, count));
        }

        static double[] DistanceField(bool[] mask, int n, CancellationToken token)
        {
            // Exact separable squared Euclidean distance transform. A finite large
            // sentinel avoids Inf-Inf in empty rows; a completely empty target has
            // a defined full-frame-diagonal miss instead of hiding a lost island.
            var field = new double[mask.Length];
            if (!mask.Any(v => v)) { Array.Fill(field, 2.0 * n * n); return field; }
            var f = new double[n]; var d = new double[n]; var v = new int[n]; var z = new double[n + 1];
            for (int y = 0; y < n; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < n; x++) f[x] = mask[y * n + x] ? 0 : 1e12;
                Transform1D(f, d, v, z, n); for (int x = 0; x < n; x++) field[y * n + x] = d[x];
            }
            for (int x = 0; x < n; x++)
            {
                token.ThrowIfCancellationRequested();
                for (int y = 0; y < n; y++) f[y] = field[y * n + x];
                Transform1D(f, d, v, z, n); for (int y = 0; y < n; y++) field[y * n + x] = d[y];
            }
            return field;
        }
        static void Transform1D(double[] f, double[] d, int[] v, double[] z, int n)
        {
            int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s;
                do { int p = v[k]; s = ((f[q] + q * q) - (f[p] + p * p)) / (2.0 * (q - p)); if (s <= z[k]) k--; else break; } while (k >= 0);
                k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++) { while (z[k + 1] < q) k++; double delta = q - v[k]; d[q] = delta * delta + f[v[k]]; }
        }

        static void Basis(Bounds bounds, Vector3 direction, out Vector3 right, out Vector3 up, out float extent, out float halfDepth)
        {
            right = Vector3.Cross(Vector3.up, direction).normalized; up = Vector3.Cross(direction, right).normalized;
            float padding = Math.Max(.00001f, bounds.extents.magnitude * .02f);
            extent = Math.Max(ProjectExtent(bounds.extents, right), ProjectExtent(bounds.extents, up)) + padding;
            halfDepth = ProjectExtent(bounds.extents, direction) + padding;
        }
        static Vector3[] MakeDirections()
        {
            float phi = (1 + Mathf.Sqrt(5)) / 2; var output = new List<Vector3>();
            foreach (float a in new[] { -1f, 1f }) foreach (float b in new[] { -1f, 1f })
            { output.Add(new Vector3(0, a, b * phi).normalized); output.Add(new Vector3(a, b * phi, 0).normalized); output.Add(new Vector3(b * phi, 0, a).normalized); }
            return output.ToArray();
        }
        static float ProjectExtent(Vector3 e, Vector3 axis) => Mathf.Abs(axis.x) * e.x + Mathf.Abs(axis.y) * e.y + Mathf.Abs(axis.z) * e.z;
        static void ValidateBounds(Bounds b, Bounds source)
        {
            if (!Finite(b.center.x) || !Finite(b.center.y) || !Finite(b.center.z) || !Finite(b.extents.x) || !Finite(b.extents.y) || !Finite(b.extents.z) ||
                b.extents.x < 0 || b.extents.y < 0 || b.extents.z < 0 || b.extents.sqrMagnitude <= 0 || b.center != source.center)
                throw new ArgumentException("Capture bounds must be finite, nonempty and preserve the source model center.");
        }
        static string Fingerprint(Material material, bool includeTextureState = true)
        {
            if (material == null) return "<missing>";
            var key = new StringBuilder().Append(material.GetInstanceID()).Append(':').Append(material.ComputeCRC());
            string path = AssetDatabase.GetAssetPath(material); if (!string.IsNullOrEmpty(path)) key.Append(':').Append(AssetDatabase.GetAssetDependencyHash(path));
            foreach (string property in material.GetTexturePropertyNames())
            {
                var t = material.GetTexture(property);
                if (t == null) continue;
                key.Append(':').Append(property).Append('=').Append(t.GetInstanceID());
                // This gate certifies coverage, not albedo/normal/roughness. Unity
                // may lazily upload an unused default color texture after a yield.
                // Keep ALL properties/bindings in ComputeCRC and the identity key,
                // but content counters only belong to actual alpha dependencies.
                if (includeTextureState && AffectsAlpha(material, property)) key.Append('/').Append(t.updateCount);
            }
            return key.ToString();
        }
        static bool AffectsAlpha(Material material, string property)
        {
            const float halfBoundaryPadding = .001f;
            // Be conservative around half-precision selector boundaries. Ordinary
            // source enum/toggle values are exactly 0/1/2/3.
            if (material.GetFloat("_AlphaClip") <= .5f - halfBoundaryPadding) return false;
            float selector = material.GetFloat("_OpacitySource");
            if (property == "_OpacityMap") return selector >= 2.5f - halfBoundaryPadding;
            if (property == "_PackedMap") return selector >= .5f - halfBoundaryPadding && selector <= 2.5f + halfBoundaryPadding;
            return property == "_BaseMap" && material.GetFloat("_UseBaseAlpha") >= .5f - halfBoundaryPadding;
        }
        static string BoundsKey(Bounds b) => FloatKey(b.center.x) + "," + FloatKey(b.center.y) + "," + FloatKey(b.center.z) + "," + FloatKey(b.extents.x) + "," + FloatKey(b.extents.y) + "," + FloatKey(b.extents.z);
        static string FloatKey(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        static void CacheReference(string key, bool[] value)
        {
            if (ReferenceCache.ContainsKey(key)) return;
            while (referenceCacheBytes + value.Length > ReferenceCacheLimit && ReferenceOrder.Count > 0)
            { string old = ReferenceOrder.Dequeue(); if (ReferenceCache.TryGetValue(old, out var mask)) { referenceCacheBytes -= mask.Length; ReferenceCache.Remove(old); } }
            ReferenceCache.Add(key, value); ReferenceOrder.Enqueue(key); referenceCacheBytes += value.Length;
        }
        static void SaveMasks(string directory, string name, bool[] reference, bool[] candidate, int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var colors = new Color32[reference.Length];
                for (int i = 0; i < colors.Length; i++) colors[i] = reference[i] && candidate[i] ? new Color32(255, 255, 255, 255) : reference[i] ? new Color32(255, 0, 0, 255) : candidate[i] ? new Color32(0, 255, 0, 255) : new Color32(0, 0, 0, 255);
                texture.SetPixels32(colors); texture.Apply(false); File.WriteAllBytes(Path.Combine(directory, Sanitize(name) + ".png"), texture.EncodeToPNG());
            }
            finally { Destroy(texture); }
        }
        static string Sanitize(string text) => string.Concat((text ?? "model").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_'));
        static RenderTexture Target(int size, GraphicsFormat format, string name)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(size, size) { graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1 })
                { name = name, hideFlags = HideFlags.HideAndDontSave };
            if (rt.Create()) return rt; Destroy(rt); throw new InvalidOperationException("Could not allocate leaf validation target.");
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Set(Material material, string name, float value) { if (material.HasProperty(name)) material.SetFloat(name, value); }
        static void Destroy(Object value) { if (value != null) Object.DestroyImmediate(value); }
        static void Release(RenderTexture value) { if (value != null) { value.Release(); Destroy(value); } }
    }
}
