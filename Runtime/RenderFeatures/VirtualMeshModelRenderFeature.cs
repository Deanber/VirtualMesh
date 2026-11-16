using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// GPU cluster selection, conservative two-phase occlusion, depth, shading and motion.
    /// History is a prediction only: every history rejection is retested against current depth.
    /// Native URP shadow atlases consume a separate light-visible cluster list.
    /// </summary>
    public sealed class VirtualMeshModelRenderFeature : ScriptableRendererFeature
    {
        public bool enableOcclusion = true;
        [Tooltip("Use a finer conservative 4x4 HiZ test. Keeps the same LOD and geometry; useful when the extra tests remove enough occluded clusters.")]
        public bool enableFineHiZOcclusion = false;
        [Tooltip("Test whole GPU work groups against the frustum before individual clusters. Useful when most groups are off-screen.")]
        public bool enableEarlyWorkFrustum;
        public bool enableConeCulling = true;
        public bool enableMotionVectors = true;
        // Opt-in until the matching depth/forward shader variants pass image and GPU checks.
        public bool enableDepthEqualShading = false;
        [Tooltip("Use tagged material visibility and resolve passes where available.")]
        public bool enableVisibilityMaterialResolve;
        [Tooltip("Reconstruct visibility normals per pixel when every resolved material supports it. Uses ordinary visibility normals otherwise.")]
        public bool enableVisibilityNormalResolve;
        [Tooltip("Reuse vertices through indexed visibility draws. Falls back without changing geometry when optional buffer capacity is unavailable.")]
        public bool enableIndexedVisibility;
#if UNITY_EDITOR
        // Test hooks never affect a Player and are restored by the forest A/B harness.
        internal static Func<Material, bool> DiagnosticResolveMaterialFilter;
        internal static float? DiagnosticTimeOverride;
        internal static int? DiagnosticIndexedMaxClusters;
        // A/B validation only: retain the old depth write declaration without
        // changing motion shaders, geometry, or actual consumer dependencies.
        internal static bool DiagnosticForceMotionDepthReadWrite;
#endif
        [Min(0f), Tooltip("Projected cluster diameter in pixels below which a participating material can simplify far LOD shading. Zero disables it.")]
        public float farShadingMaxPixels = 12f;
        public bool enableImpostors = false;
        [Range(.001f, .5f)] public float impostorMaxScreenHeight = .15f;
        public VirtualMeshLodBudgetSettings lodBudget = new VirtualMeshLodBudgetSettings();
        [Min(0.000001f)] public float depthBias = 0.00002f;
        [Min(1f)] public float pixelPadding = 2f;

        ComputeShader cull, pyramid, impostorCull, visibilityTiles, visibilityIndices;
        LocalKeyword fineHiZKeyword;
        Material copy;
        GraphPass shadows, depth, shade, motion;
        readonly Dictionary<int, History> histories = new Dictionary<int, History>();
        readonly Dictionary<int, CameraFrame> cameraFrames = new Dictionary<int, CameraFrame>();
        readonly Dictionary<GraphicsBuffer, BufferHandle> importedBuffers = new Dictionary<GraphicsBuffer, BufferHandle>();
        static readonly uint[] zeroCounters = new uint[VirtualMeshGraphRuntime.VisibilityCounterCount];
        static readonly FieldInfo featuresField = typeof(ScriptableRenderer).GetField("m_RendererFeatures", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo renderingMode = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);

        static RenderingMode GetRenderingMode(ScriptableRenderer renderer) =>
            renderer is UniversalRenderer && renderingMode?.GetValue(renderer) is RenderingMode mode
                ? mode : RenderingMode.Forward;

        sealed class History : IDisposable
        {
            public Camera Camera;
            public RTHandle Texture;
            public int Width, Height, Mips, Revision;
            public bool Valid;
            public double LastUsed;
            public Matrix4x4 ViewProjection, NonJitteredViewProjection;
            public Vector4 NearPlane;
            public float Time;
            public Vector3 Position;
            public Quaternion Rotation;
            public Matrix4x4 Projection;
            public void Dispose() { Texture?.Release(); Texture = null; Valid = false; }
        }

        sealed class CameraFrame
        {
            public VirtualMeshGraphRuntime.Frame Frame;
            public History History;
            public Matrix4x4 ViewProjection, NonJitteredViewProjection, PreviousViewProjection;
            public Vector4 NearPlane;
            public float Time, PreviousTime;
            public bool HasPrevious;
            public bool FineHiZOcclusion;
            public int Width, Height, Mips;
            public Vector3 Position;
            public Quaternion Rotation;
            public Matrix4x4 Projection;
            public VisibilityDraw[] VisibilityDraws;
            public TextureHandle VisibilityTexture, MaterialDepth;
            public BufferHandle VisibilityTileMasks;
            public int TilesX, TilesY;
            public VirtualMeshIndexedVisibility IndexedVisibilityResources;
            public bool IndexedVisibilityEligible;
            public int IndexedVisibilityBatchCount;
            public bool NormalResolveEligible;
        }

        public override void Create()
        {
            cull = Resources.Load<ComputeShader>("VirtualMeshModelGraphCull");
            if (cull != null)
            {
                cull.SetInt("_VMDiagnosticModelMode", 0); cull.SetInts("_VMDiagnosticExcludedModelBits", 0, 0);
                fineHiZKeyword = new LocalKeyword(cull, "VM_HIZ_FINE_4X4");
            }
            pyramid = Resources.Load<ComputeShader>("VirtualMeshModelHiZ");
            impostorCull = Resources.Load<ComputeShader>("VirtualMeshModelImpostorCull");
            visibilityTiles = Resources.Load<ComputeShader>("VirtualMeshVisibilityTiles");
            visibilityIndices = Resources.Load<ComputeShader>("VirtualMeshVisibilityIndices");
            CoreUtils.Destroy(copy);
            var shader = Shader.Find("Hidden/VirtualMesh/GraphDepthCopy");
            if (shader != null) copy = CoreUtils.CreateEngineMaterial(shader);
            shadows = new GraphPass(this, 0, (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingShadows - 1));
            depth = new GraphPass(this, 1, RenderPassEvent.AfterRenderingPrePasses);
            depth.ConfigureInput(ScriptableRenderPassInput.Depth);
            depth.requiresIntermediateTexture = true;
            shade = new GraphPass(this, 2, RenderPassEvent.BeforeRenderingOpaques);
            motion = new GraphPass(this, 3, (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingPostProcessing - 1));
            VirtualMeshGraphRuntime.CameraUsesGraph = UsesGraph;
            VirtualMeshGraphRuntime.CameraLodBudget = camera => ResolveFeature(camera)?.lodBudget;
            VirtualMeshGraphRuntime.CameraImpostorMaxHeight = camera =>
            {
                var feature = ResolveFeature(camera);
                // Stacked cameras inherit raster depth. Until their seed depth can exclude
                // previous cameras' proxies, retain cluster geometry for the whole stack.
                if (camera.cameraType != CameraType.SceneView && camera.TryGetComponent<UniversalAdditionalCameraData>(out var extra) &&
                    (extra.renderType == CameraRenderType.Overlay || extra.cameraStack != null && extra.cameraStack.Count > 0)) return 0;
                return feature != null && feature.enableImpostors && feature.impostorCull != null &&
                    float.IsFinite(feature.impostorMaxScreenHeight) ? Mathf.Clamp(feature.impostorMaxScreenHeight, .001f, .5f) : 0;
            };
        }

        internal static bool UsesGraph(Camera camera) => ResolveFeature(camera) != null;

        static VirtualMeshModelRenderFeature ResolveFeature(Camera camera)
        {
            if (camera == null || camera.cameraType == CameraType.Preview || camera.stereoEnabled || camera.rect != new Rect(0, 0, 1, 1)) return null;
            // Permission to use dynamic resolution is common even at full size.
            // A genuinely scaled hardware viewport takes the conservative fallback.
            if (camera.allowDynamicResolution && (!Mathf.Approximately(ScalableBufferManager.widthScaleFactor, 1f) ||
                !Mathf.Approximately(ScalableBufferManager.heightScaleFactor, 1f))) return null;
            var pipeline = UniversalRenderPipeline.asset;
            if (pipeline == null || pipeline.msaaSampleCount != 1 || (camera.targetTexture != null && camera.targetTexture.antiAliasing != 1)) return null;
            if (GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>()?.enableRenderCompatibilityMode == true) return null;
            // URP renders Scene View with the pipeline's default renderer, ignoring camera overrides.
            var renderer = camera.cameraType != CameraType.SceneView && camera.TryGetComponent<UniversalAdditionalCameraData>(out var extra)
                ? extra.scriptableRenderer : null;
            renderer ??= pipeline.scriptableRenderer;
            var mode = GetRenderingMode(renderer);
            if (mode != RenderingMode.ForwardPlus && mode != RenderingMode.Deferred && mode != RenderingMode.DeferredPlus) return null;
            if (!(featuresField?.GetValue(renderer) is List<ScriptableRendererFeature> features)) return null;
            foreach (var item in features)
                if (item is VirtualMeshModelRenderFeature feature && feature.isActive && feature.cull != null && feature.pyramid != null && feature.copy != null) return feature;
            return null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            if (VirtualMeshRuntime.IsEditorPreviewPaused && !Application.isPlaying)
            {
                ClearCameraHistory();
                return;
            }
#endif
            if (ResolveFeature(renderingData.cameraData.camera) != this) return;
            renderer.EnqueuePass(shadows);
            renderer.EnqueuePass(depth);
            renderer.EnqueuePass(shade);
            if (enableMotionVectors) renderer.EnqueuePass(motion);
        }

        protected override void Dispose(bool disposing)
        {
            ClearCameraHistory();
            CoreUtils.Destroy(copy); copy = null;
        }

        void ClearCameraHistory()
        {
            foreach (var h in histories.Values) h.Dispose();
            histories.Clear(); cameraFrames.Clear();
        }

        sealed class GraphPass : ScriptableRenderPass
        {
            readonly VirtualMeshModelRenderFeature owner;
            readonly int phase;
            public GraphPass(VirtualMeshModelRenderFeature feature, int phase, RenderPassEvent when)
            { owner = feature; this.phase = phase; renderPassEvent = when; }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer data)
            {
                var camera = data.Get<UniversalCameraData>();
                if (!VirtualMeshGraphRuntime.TryGetFrame(camera.camera, out var frame) || !frame.UsesRenderGraph) return;
                var resources = data.Get<UniversalResourceData>();
                if (phase == 0) owner.RecordShadows(graph, frame);
                else if (phase == 1)
                {
                    var mode = GetRenderingMode(camera.renderer);
                    bool deferred = mode == RenderingMode.Deferred || mode == RenderingMode.DeferredPlus;
                    owner.RecordDepth(graph, camera, resources, frame, deferred);
                }
                else if (owner.cameraFrames.TryGetValue(camera.camera.GetInstanceID(), out var recorded) && recorded.Frame == frame)
                {
                    if (phase == 2)
                    {
                        // Deferred modes run this stage after their lighting pass. VM materials
                        // still shade forward into camera color using the VM-visible list.
                        owner.RecordDraw(graph, recorded, frame.Colors, resources.activeColorTexture, resources.activeDepthTexture, "VM Shade",
                            owner.enableDepthEqualShading ? "GraphForwardLit" : "ForwardLit", 0, true, skipResolved: true);
                        owner.RecordMaterialResolve(graph, recorded, resources.activeColorTexture, resources.activeDepthTexture);
                        owner.RecordProxyDraw(graph, recorded, resources.activeColorTexture, resources.activeDepthTexture, "VM Proxy Shade", "ForwardLit", true);
                    }
                    else if (resources.motionVectorColor.IsValid() && resources.activeDepthTexture.IsValid())
                    {
                        owner.RecordDraw(graph, recorded, frame.Colors, resources.motionVectorColor, resources.activeDepthTexture, "VM Motion Vectors", "MotionVectors", 0, false);
                        owner.RecordProxyDraw(graph, recorded, resources.motionVectorColor, resources.activeDepthTexture, "VM Proxy Motion", "MotionVectors", false);
                    }
                }
            }
        }

        CameraFrame PrepareCamera(RenderGraph graph, UniversalCameraData camera, TextureHandle source, VirtualMeshGraphRuntime.Frame frame)
        {
            var desc = source.GetDescriptor(graph);
            int id = camera.camera.GetInstanceID();
            if (!histories.TryGetValue(id, out var history))
            { history = new History { Camera = camera.camera }; histories.Add(id, history); }
            if (history.Texture == null || history.Width != desc.width || history.Height != desc.height)
            {
                history.Dispose(); history.Width = desc.width; history.Height = desc.height;
                history.Mips = 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(desc.width, desc.height), 2));
                var rt = new RenderTexture(desc.width, desc.height, 0, GraphicsFormat.R32_SFloat)
                { name = "VM HiZ History " + id, enableRandomWrite = true, useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                rt.Create(); history.Texture = RTHandles.Alloc(rt, transferOwnership: true);
            }
            var view = camera.GetViewMatrix();
            var near = GeometryUtility.CalculateFrustumPlanes(camera.camera)[4];
            var position = camera.camera.transform.position;
            var rotation = camera.camera.transform.rotation;
            var projection = camera.camera.nonJitteredProjectionMatrix;
            bool cut = Vector3.Distance(position, history.Position) > Mathf.Max(5f, camera.camera.nearClipPlane * 20f) ||
                Quaternion.Angle(rotation, history.Rotation) > 45f ||
                Mathf.Abs(projection.m00 - history.Projection.m00) > .1f || Mathf.Abs(projection.m11 - history.Projection.m11) > .1f ||
                Mathf.Abs(projection.m32 - history.Projection.m32) > .1f;
            var current = new CameraFrame
            {
                Frame = frame, History = history, Width = desc.width, Height = desc.height, Mips = history.Mips,
                FineHiZOcclusion = enableFineHiZOcclusion,
                ViewProjection = GL.GetGPUProjectionMatrix(camera.GetProjectionMatrix(), true) * view,
                NonJitteredViewProjection = GL.GetGPUProjectionMatrix(camera.camera.nonJitteredProjectionMatrix, true) * view,
                NearPlane = new Vector4(near.normal.x, near.normal.y, near.normal.z, near.distance),
                Time =
#if UNITY_EDITOR
                    DiagnosticTimeOverride ??
#endif
                    (Application.isPlaying ? UnityEngine.Time.time : UnityEngine.Time.realtimeSinceStartup),
                PreviousTime = history.Time, PreviousViewProjection = history.NonJitteredViewProjection,
                HasPrevious = history.Valid && !cut && history.Revision == frame.HistoryRevision && UnityEngine.Time.realtimeSinceStartupAsDouble - history.LastUsed < 1.0,
                Position = position, Rotation = rotation, Projection = projection
            };
            history.LastUsed = UnityEngine.Time.realtimeSinceStartupAsDouble;
            cameraFrames[id] = current;
            var expired = new List<int>();
            foreach (var entry in histories)
                if (entry.Key != id && (entry.Value.Camera == null || UnityEngine.Time.realtimeSinceStartupAsDouble - entry.Value.LastUsed > 10)) expired.Add(entry.Key);
            foreach (var key in expired) { histories[key].Dispose(); histories.Remove(key); cameraFrames.Remove(key); }
            return current;
        }

        BufferHandle ImportBuffer(RenderGraph graph, GraphicsBuffer buffer)
        {
            if (!importedBuffers.TryGetValue(buffer, out var handle))
            { handle = graph.ImportBuffer(buffer); importedBuffers.Add(buffer, handle); }
            return handle;
        }
        void ReadBuffer(IBaseRenderGraphBuilder builder, RenderGraph graph, GraphicsBuffer buffer)
        { if (buffer != null) builder.UseBuffer(ImportBuffer(graph, buffer), AccessFlags.Read); }
        void WriteBuffer(IBaseRenderGraphBuilder builder, RenderGraph graph, GraphicsBuffer buffer)
        { if (buffer != null) builder.UseBuffer(ImportBuffer(graph, buffer), AccessFlags.ReadWrite); }
        void ReadGeometry(IBaseRenderGraphBuilder builder, RenderGraph graph, VirtualMeshGraphRuntime.Frame frame, bool raster)
        {
            ReadBuffer(builder, graph, frame.Atlas.Clusters); ReadBuffer(builder, graph, frame.Atlas.ModelTable);
            if (raster)
            {
                ReadBuffer(builder, graph, frame.Atlas.Vertices);
                ReadBuffer(builder, graph, frame.Atlas.VertexPositionUvColor);
                ReadBuffer(builder, graph, frame.Atlas.VertexNormalTangent);
                ReadBuffer(builder, graph, frame.Atlas.VertexCold);
                // Both sides of the uniform layout branch can remain reflected.
                // Bind/import exact-stride inactive SRVs, never a wrong-stride alias.
                ReadBuffer(builder, graph, VirtualMeshVertexBindings.Legacy);
                ReadBuffer(builder, graph, VirtualMeshVertexBindings.PositionUvColor);
                ReadBuffer(builder, graph, VirtualMeshVertexBindings.NormalTangent);
                ReadBuffer(builder, graph, VirtualMeshVertexBindings.Cold);
                ReadBuffer(builder, graph, frame.Atlas.Indices); ReadBuffer(builder, graph, frame.PreviousObjectToWorld);
            }
            else
            {
                ReadBuffer(builder, graph, frame.Atlas.CullingData);
                ReadBuffer(builder, graph, frame.Atlas.WorkBounds);
            }
        }

        sealed class CullData
        {
            public ComputeShader Shader;
            public VirtualMeshGraphRuntime.Frame Frame;
            public VirtualMeshGraphRuntime.Batch[] Batches;
            public TextureHandle HiZ;
            public Matrix4x4 ViewProjection;
            public Vector4 NearPlane;
            public int Width, Height, Mips, Phase;
            public bool Valid, Cone, EarlyWorkFrustum, FineHiZOcclusion;
            public LocalKeyword FineHiZKeyword;
            public float Bias, Padding;
        }
        void RecordShadows(RenderGraph graph, VirtualMeshGraphRuntime.Frame frame)
        {
            // RenderGraph imports do not deduplicate native buffers. All passes for
            // this camera must share handles so append writes, draws and indirect
            // dispatches are ordered by real resource dependencies.
            importedBuffers.Clear();
            if (frame.Shadows.Length == 0) return;
            using var builder = graph.AddComputePass<CullData>("VM Shadow Cluster Selection", out var pass);
            pass.Shader = cull; pass.Frame = frame; pass.Batches = frame.Shadows; pass.HiZ = TextureHandle.nullHandle;
            pass.ViewProjection = Matrix4x4.identity; pass.NearPlane = Vector4.zero;
            pass.Width = pass.Height = pass.Mips = 0; pass.Phase = 2; pass.Valid = false; pass.Cone = enableConeCulling; pass.Bias = depthBias; pass.Padding = pixelPadding;
            ReadGeometry(builder, graph, frame, false);
            foreach (var b in frame.Shadows)
            { ReadBuffer(builder, graph, b.Instances); ReadBuffer(builder, graph, b.LodParameters); ReadBuffer(builder, graph, b.InstanceModelIds); ReadBuffer(builder, graph, b.WorkItems); WriteBuffer(builder, graph, b.Visible); WriteBuffer(builder, graph, b.Arguments); WriteBuffer(builder, graph, b.Counters); }
            // URP's native shadow renderer list consumes these indirect buffers externally.
            // Preserve this graph side effect and its order before the native shadow passes.
            builder.AllowPassCulling(false); builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (CullData d, ComputeGraphContext context) =>
            {
                foreach (var b in d.Batches) VirtualMeshGraphRuntime.RecordCull(context.cmd, d.Frame, b, d.Cone);
            });
        }

        void RecordSelection(RenderGraph graph, CameraFrame camera, TextureHandle hiZ, bool retest)
        {
            using var builder = graph.AddComputePass<CullData>(retest ? "VM Current Depth Retest" : "VM History Cluster Selection", out var pass);
            pass.Shader = cull; pass.Frame = camera.Frame; pass.Batches = camera.Frame.Colors; pass.HiZ = hiZ;
            pass.ViewProjection = retest ? camera.ViewProjection : camera.History.ViewProjection;
            pass.NearPlane = retest ? camera.NearPlane : camera.History.NearPlane;
            pass.Width = camera.Width; pass.Height = camera.Height; pass.Mips = camera.Mips; pass.Phase = retest ? 1 : 0;
            pass.Valid = enableOcclusion && (retest || camera.HasPrevious); pass.Cone = enableConeCulling; pass.Bias = depthBias; pass.Padding = pixelPadding;
            pass.EarlyWorkFrustum = enableEarlyWorkFrustum;
            pass.FineHiZOcclusion = camera.FineHiZOcclusion; pass.FineHiZKeyword = fineHiZKeyword;
            // Local compute keywords are command-buffer state. Declare the side effect
            // and capture this camera's choice instead of mutating the shared asset.
            builder.AllowGlobalStateModification(true);
            builder.UseTexture(hiZ, AccessFlags.Read); ReadGeometry(builder, graph, camera.Frame, false);
            foreach (var b in pass.Batches)
            {
                ReadBuffer(builder, graph, b.Instances); ReadBuffer(builder, graph, b.InstanceModelIds); ReadBuffer(builder, graph, b.WorkItems);
                if (!retest) ReadBuffer(builder, graph, b.LodParameters);
                WriteBuffer(builder, graph, b.Visible); WriteBuffer(builder, graph, b.Counters);
                if (retest) ReadBuffer(builder, graph, b.Deferred); else WriteBuffer(builder, graph, b.Deferred);
                WriteBuffer(builder, graph, retest ? b.Arguments : b.EarlyArguments);
                if (retest) { WriteBuffer(builder, graph, b.RetestDispatchArguments); WriteBuffer(builder, graph, b.RecoveredArguments); }
            }
            builder.SetRenderFunc(static (CullData d, ComputeGraphContext context) =>
            {
                var cmd = context.cmd; var shader = d.Shader;
                int kernel = shader.FindKernel(d.Phase == 1 ? "Retest" : "Select");
                if (d.Phase == 0) cmd.SetComputeIntParam(shader, "_VMEnableEarlyWorkFrustum", d.EarlyWorkFrustum ? 1 : 0);
                cmd.SetKeyword(shader, d.FineHiZKeyword, d.FineHiZOcclusion);
                try
                {
                    foreach (var b in d.Batches)
                    {
                        if (d.Phase == 0) cmd.SetBufferData(b.Counters, zeroCounters);
                        VirtualMeshGraphRuntime.BindCullParameters(cmd, shader, kernel, d.Frame, b, bindWorkBounds: d.Phase == 0, retest: d.Phase == 1);
                        if (!d.Cone) cmd.SetComputeIntParam(shader, "_VMConeDataEnabled", 0);
                        cmd.SetComputeTextureParam(shader, kernel, "_VMHiZTexture", d.HiZ);
                        BindHiZ(cmd, shader, d);
                        if (d.Phase == 0)
                        {
                            for (int offset = 0; offset < b.WorkCount; offset += 65535)
                            { cmd.SetComputeIntParam(shader, "_VMWorkItemOffset", offset); cmd.DispatchCompute(shader, kernel, Math.Min(65535, b.WorkCount - offset), 1, 1); }
                            VirtualMeshGraphRuntime.RecordDrawArguments(cmd, shader, b, b.EarlyArguments, 0);
                        }
                        else
                        {
                            int prepare = shader.FindKernel("PrepareRetest");
                            cmd.SetComputeBufferParam(shader, prepare, "_VMCounters", b.Counters);
                            cmd.SetComputeBufferParam(shader, prepare, "_VMRetestDispatchArguments", b.RetestDispatchArguments);
                            cmd.DispatchCompute(shader, prepare, 1, 1, 1);
                            cmd.DispatchCompute(shader, kernel, b.RetestDispatchArguments, 0);
                            VirtualMeshGraphRuntime.RecordDrawArguments(cmd, shader, b, b.Arguments, 0, retested: true);
                            VirtualMeshGraphRuntime.RecordVisibilityReadback(cmd, d.Frame, b);
                        }
                    }
                }
                finally
                {
                    // Later users of the shared compute resource inherit the baseline.
                    // Every selection/retest pass sets its own captured choice again.
                    cmd.SetKeyword(shader, d.FineHiZKeyword, false);
                }
            });
        }

        static void BindHiZ(ComputeCommandBuffer cmd, ComputeShader shader, CullData d)
        {
            cmd.SetComputeIntParam(shader, "_VMHiZValid", d.Valid ? 1 : 0);
            cmd.SetComputeMatrixParam(shader, "_VMHiZViewProjection", d.ViewProjection);
            cmd.SetComputeVectorParam(shader, "_VMHiZNdcToUV", new Vector4(.5f, SystemInfo.graphicsUVStartsAtTop ? -.5f : .5f, .5f, .5f));
            bool gl = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
            cmd.SetComputeVectorParam(shader, "_VMHiZClipToDepth", gl ? new Vector4(.5f, .5f) : new Vector4(1, 0));
            cmd.SetComputeVectorParam(shader, "_VMHiZNearPlaneWS", d.NearPlane);
            cmd.SetComputeIntParams(shader, "_VMHiZSize", d.Width, d.Height);
            cmd.SetComputeIntParam(shader, "_VMHiZMipCount", d.Mips);
            cmd.SetComputeIntParam(shader, "_VMHiZReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.SetComputeFloatParam(shader, "_VMHiZDepthBias", d.Bias); cmd.SetComputeFloatParam(shader, "_VMHiZPixelPadding", d.Padding);
        }

        void RecordDepth(RenderGraph graph, UniversalCameraData camera, UniversalResourceData resources,
            VirtualMeshGraphRuntime.Frame frame, bool deferred)
        {
            if (!resources.cameraDepthTexture.IsValid() || !resources.activeDepthTexture.IsValid()) return;
            // URP deferred prepasses write directly into the raster depth
            // attachment. The forward path can write directly to sampled depth.
            var sceneDepth = deferred ? resources.activeDepthTexture : resources.cameraDepthTexture;
            var current = PrepareCamera(graph, camera, sceneDepth, frame);
            var depthDesc = resources.activeDepthTexture.GetDescriptor(graph);
            depthDesc.name = "VM Current Occluder Depth"; depthDesc.msaaSamples = MSAASamples.None;
            depthDesc.clearBuffer = false; depthDesc.bindTextureMS = false;
            var depthTarget = graph.CreateTexture(depthDesc);
            CopyDepth(graph, sceneDepth, depthTarget, "VM Seed Scene Depth");
            var history = graph.ImportTexture(current.History.Texture);
            RecordSelection(graph, current, history, false);
            bool normals = resources.cameraNormalsTexture.IsValid();
            var normalTarget = normals ? resources.cameraNormalsTexture : TextureHandle.nullHandle;
            var layerTarget = normals ? resources.renderingLayersTexture : TextureHandle.nullHandle;
            PrepareMaterialResolve(graph, current, normalTarget, layerTarget, resources.activeDepthTexture);
            // Populate normals together with occluder depth instead of repeating
            // every visible cluster in a separate final normals geometry pass.
            RecordVisibilityDepth(graph, current, normalTarget, layerTarget, depthTarget, normals, 1);
            var hiZDesc = sceneDepth.GetDescriptor(graph);
            hiZDesc.name = "VM Current HiZ"; hiZDesc.depthBufferBits = DepthBits.None; hiZDesc.colorFormat = GraphicsFormat.R32_SFloat;
            hiZDesc.msaaSamples = MSAASamples.None; hiZDesc.bindTextureMS = false; hiZDesc.enableRandomWrite = true;
            hiZDesc.useMipMap = true; hiZDesc.autoGenerateMips = false; hiZDesc.clearBuffer = false;
            var currentHiZ = graph.CreateTexture(hiZDesc);
            BuildPyramid(graph, current, depthTarget, currentHiZ, false);
            RecordSelection(graph, current, currentHiZ, true);
            RecordVisibilityDepth(graph, current, normalTarget, layerTarget, depthTarget, normals, 2);
            if (!current.NormalResolveEligible) RecordVisibilityTiles(graph, current);
            BuildPyramid(graph, current, depthTarget, history, true);
            // The completed opaque HiZ can reject whole proxies. Their reconstructed cutout
            // depth is then added to the camera buffers, never to current/history occluder data.
            RecordProxySelection(graph, current, history);
            RecordProxyDraw(graph, current, normalTarget, depthTarget, "VM Proxy Depth", normals ? "DepthNormals" : "DepthOnly", false, layerTarget);
            CopyDepth(graph, depthTarget, resources.activeDepthTexture, "VM Publish Raster Depth");
            if (!resources.cameraDepthTexture.Equals(resources.activeDepthTexture))
                CopyDepth(graph, depthTarget, resources.cameraDepthTexture, "VM Publish Sampled Depth");
        }

        readonly struct VisibilityDraw
        {
            public readonly int Id, RasterPass, ResolvePass, IndexedRasterPass, NormalResolvePass;
            public VisibilityDraw(int id, int rasterPass, int resolvePass, int indexedRasterPass, int normalResolvePass = -1)
            { Id = id; RasterPass = rasterPass; ResolvePass = resolvePass; IndexedRasterPass = indexedRasterPass; NormalResolvePass = normalResolvePass; }
        }

        void PrepareMaterialResolve(RenderGraph graph, CameraFrame camera, TextureHandle normals, TextureHandle layers, TextureHandle sceneDepth)
        {
            // Visibility stores the raster depth as float bits. Exact equality with a
            // D32 attachment preserves the old resolve's depth test; D24 quantization
            // requires a separate path, not an epsilon that could reveal hidden leaves.
            var depthFormat = sceneDepth.GetDescriptor(graph).format;
            if (!enableVisibilityMaterialResolve || visibilityTiles == null ||
                (depthFormat != GraphicsFormat.D32_SFloat && depthFormat != GraphicsFormat.D32_SFloat_S8_UInt) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat, GraphicsFormatUsage.Render) ||
                SystemInfo.supportedRenderTargetCount < (layers.IsValid() ? 3 : normals.IsValid() ? 2 : 1) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_UInt, GraphicsFormatUsage.Render))
                return;

            var batches = camera.Frame.Colors;
            var draws = new VisibilityDraw[batches.Length];
            int count = 0;
            string rasterName = normals.IsValid() ? "VMVisibilityNormals" : "VMVisibility";
            for (int i = 0; i < batches.Length && count < 64; i++)
            {
                var material = batches[i].Material;
#if UNITY_EDITOR
                if (DiagnosticResolveMaterialFilter != null && !DiagnosticResolveMaterialFilter(material)) continue;
#endif
                if (VirtualMeshMaterialSupport.GetShadingFamily(material).Length == 0) continue;
                int raster = material.FindPass(rasterName), resolve = material.FindPass("VMResolve");
                if (raster >= 0 && resolve >= 0) draws[i] = new VisibilityDraw(++count, raster, resolve,
                    material.FindPass(normals.IsValid() ? "VMVisibilityNormalsIndexed" : "VMVisibilityIndexed"));
            }
            if (count == 0) return;

            // The visibility attachment layout is shared by the entire phase. Only
            // switch it to the plain ID output if all participating materials can
            // reconstruct their normals; otherwise retain the existing MRT path.
            bool normalResolve = enableVisibilityNormalResolve && normals.IsValid();
            if (normalResolve)
            {
                for (int i = 0; i < draws.Length; i++)
                {
                    if (draws[i].Id == 0) continue;
                    var material = batches[i].Material;
                    if (material.FindPass("VMVisibility") < 0 || material.FindPass("VMNormalResolve") < 0)
                    { normalResolve = false; break; }
                }
            }
            if (normalResolve)
            {
                for (int i = 0; i < draws.Length; i++)
                {
                    var draw = draws[i];
                    if (draw.Id == 0) continue;
                    var material = batches[i].Material;
                    draws[i] = new VisibilityDraw(draw.Id, material.FindPass("VMVisibility"), draw.ResolvePass,
                        material.FindPass("VMVisibilityIndexed"), material.FindPass("VMNormalResolve"));
                }
            }
            camera.NormalResolveEligible = normalResolve;
            camera.VisibilityDraws = draws;
            // Start on the measured D3D backend only. Unsupported adapters retain
            // ordinary visibility, and allocation failure never changes residency.
            if (enableIndexedVisibility && visibilityIndices != null &&
                (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12))
            {
                bool hasIndexedPass = false;
                foreach (var draw in draws) if (draw.Id != 0 && draw.IndexedRasterPass >= 0) { hasIndexedPass = true; break; }
                if (hasIndexedPass) camera.IndexedVisibilityResources = VirtualMeshGraphRuntime.TryGetIndexedVisibilityResources(camera.Frame);
                camera.IndexedVisibilityEligible = camera.IndexedVisibilityResources != null;
            }
            camera.TilesX = (camera.Width + 15) / 16;
            camera.TilesY = (camera.Height + 15) / 16;
            camera.VisibilityTexture = graph.CreateTexture(new TextureDesc(camera.Width, camera.Height)
            {
                name = "VM Material Visibility", colorFormat = GraphicsFormat.R32G32B32A32_UInt,
                clearBuffer = true, clearColor = Color.clear, filterMode = FilterMode.Point
            });
            camera.VisibilityTileMasks = graph.CreateBuffer(new BufferDesc(camera.TilesX * camera.TilesY, 8)
            { name = "VM Visibility Tile Material Masks", target = GraphicsBuffer.Target.Structured });
            camera.MaterialDepth = graph.CreateTexture(new TextureDesc(camera.Width, camera.Height)
            {
                name = "VM Material Depth", format = GraphicsFormat.D32_SFloat,
                clearBuffer = false, filterMode = FilterMode.Point
            });
        }

        void RecordVisibilityDepth(RenderGraph graph, CameraFrame camera, TextureHandle normals, TextureHandle layers,
            TextureHandle depth, bool hasNormals, int visibility)
        {
            string label = visibility == 1 ? "VM Early Depth" : "VM Recovered Depth";
            RecordDraw(graph, camera, camera.Frame.Colors, normals, depth, label,
                hasNormals ? "DepthNormals" : "DepthOnly", visibility, false, layers, skipResolved: true);
            if (camera.VisibilityDraws != null)
                RecordDraw(graph, camera, camera.Frame.Colors, camera.NormalResolveEligible ? TextureHandle.nullHandle : normals, depth,
                    visibility == 1 ? "VM Early Material Visibility" : "VM Recovered Material Visibility",
                    null, visibility, false, camera.NormalResolveEligible ? TextureHandle.nullHandle : layers, writeVisibility: true);
            if (camera.NormalResolveEligible)
                RecordNormalResolve(graph, camera, normals, layers, depth, visibility);
        }

        sealed class VisibilityTilesData
        {
            public CameraFrame Camera;
            public ComputeShader Shader;
        }

        void RecordVisibilityTiles(RenderGraph graph, CameraFrame camera)
        {
            if (camera.VisibilityDraws == null) return;
            using var builder = graph.AddComputePass<VisibilityTilesData>("VM Material Tile Classification", out var pass);
            pass.Camera = camera; pass.Shader = visibilityTiles;
            builder.UseTexture(camera.VisibilityTexture, AccessFlags.Read);
            builder.UseBuffer(camera.VisibilityTileMasks, AccessFlags.Write);
            builder.SetRenderFunc(static (VisibilityTilesData d, ComputeGraphContext context) =>
            {
                var camera = d.Camera;
                int kernel = d.Shader.FindKernel("BuildTileMasks");
                context.cmd.SetComputeTextureParam(d.Shader, kernel, "_VMVisibilityTexture", camera.VisibilityTexture);
                context.cmd.SetComputeBufferParam(d.Shader, kernel, "_VMVisibilityTileMasks", camera.VisibilityTileMasks);
                context.cmd.SetComputeIntParams(d.Shader, "_VMVisibilityTileDimensions", camera.Width, camera.Height, camera.TilesX, camera.TilesY);
                context.cmd.DispatchCompute(d.Shader, kernel, camera.TilesX, camera.TilesY, 1);
            });
        }

        sealed class MaterialResolveData
        {
            public CameraFrame Camera;
            public float FarShadingMaxPixels;
        }

        sealed class MaterialDepthData
        {
            public TextureHandle Visibility, SceneDepth;
            public Material Material;
        }

        void RecordMaterialDepth(RenderGraph graph, CameraFrame camera, TextureHandle sceneDepth, string label = "VM Material Depth")
        {
            using var builder = graph.AddRasterRenderPass<MaterialDepthData>(label, out var pass);
            pass.Visibility = camera.VisibilityTexture; pass.SceneDepth = sceneDepth; pass.Material = copy;
            builder.UseTexture(pass.Visibility, AccessFlags.Read);
            builder.UseTexture(sceneDepth, AccessFlags.Read);
            builder.SetRenderAttachmentDepth(camera.MaterialDepth, AccessFlags.Write);
            builder.SetRenderFunc(static (MaterialDepthData d, RasterGraphContext context) =>
            {
                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_VMVisibilityTexture", d.Visibility);
                properties.SetTexture("_VMDepthSource", d.SceneDepth);
                context.cmd.DrawProcedural(Matrix4x4.identity, d.Material, 2, MeshTopology.Triangles, 3, 1, properties);
            });
        }

        sealed class NormalResolveData
        {
            public CameraFrame Camera;
            public int Phase;
            public bool RenderingLayers;
        }

        void RecordNormalResolve(RenderGraph graph, CameraFrame camera, TextureHandle normals, TextureHandle layers,
            TextureHandle sceneDepth, int visibility)
        {
            // Resolve immediately after each phase. A recovered non-resolved
            // surface may win an equal-depth tie against early visibility; bit 6
            // identifies the new phase so old IDs cannot overwrite its normals.
            RecordVisibilityTiles(graph, camera);
            RecordMaterialDepth(graph, camera, sceneDepth,
                visibility == 1 ? "VM Early Normal Material Depth" : "VM Recovered Normal Material Depth");
            using var builder = graph.AddRasterRenderPass<NormalResolveData>(
                visibility == 1 ? "VM Early Normal Resolve" : "VM Recovered Normal Resolve", out var pass);
            pass.Camera = camera; pass.Phase = visibility == 2 ? 1 : 0; pass.RenderingLayers = layers.IsValid();
            builder.SetRenderAttachment(normals, 0, AccessFlags.ReadWrite);
            if (pass.RenderingLayers) builder.SetRenderAttachment(layers, 1, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(camera.MaterialDepth, AccessFlags.Read);
            builder.UseTexture(camera.VisibilityTexture, AccessFlags.Read);
            builder.UseBuffer(camera.VisibilityTileMasks, AccessFlags.Read);
            ReadGeometry(builder, graph, camera.Frame, true);
            ReadBuffer(builder, graph, camera.Frame.Instances);
            ReadBuffer(builder, graph, camera.Frame.InstanceModelIds);
            // URP disables this keyword at the end of its prepass. Scope it to
            // this draw rather than inheriting a potentially stale global state.
            if (pass.RenderingLayers) builder.AllowGlobalStateModification(true);
            // Do not use all global textures: camera normals are an attachment
            // here, and global texture reads would introduce a feedback hazard.
            builder.SetRenderFunc(static (NormalResolveData d, RasterGraphContext context) =>
            {
                if (d.RenderingLayers) context.cmd.EnableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                try
                {
                    var camera = d.Camera;
                    for (int i = 0; i < camera.VisibilityDraws.Length; i++)
                    {
                        var draw = camera.VisibilityDraws[i];
                        if (draw.Id == 0) continue;
                        var batch = camera.Frame.Colors[i];
                        var p = batch.Properties;
                        p.SetInt("_VMGraphDraw", 1); p.SetInt("_VMUseGeometryAtlas", 1);
                        p.SetFloat("_VMCurrentTime", camera.Time);
                        p.SetInt("_VMUseRecoveredRange", d.Phase);
                        p.SetInteger("_VMVisibilityBatchId", draw.Id);
                        p.SetTexture("_VMVisibilityTexture", camera.VisibilityTexture);
                        p.SetBuffer("_VMVisibilityTileMasks", camera.VisibilityTileMasks);
                        p.SetVector("_VMVisibilitySize", new Vector4(camera.Width, camera.Height, camera.TilesX, camera.TilesY));
                        context.cmd.DrawProcedural(Matrix4x4.identity, batch.Material, draw.NormalResolvePass,
                            MeshTopology.Triangles, 6, camera.TilesX * camera.TilesY, p);
                    }
                }
                finally
                {
                    if (d.RenderingLayers) context.cmd.DisableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                }
            });
        }

        void RecordMaterialResolve(RenderGraph graph, CameraFrame camera, TextureHandle color, TextureHandle depth)
        {
            if (camera.VisibilityDraws == null || !color.IsValid() || !depth.IsValid()) return;
            // Generate after non-resolved geometry has finished writing scene depth.
            // A stale visibility ID behind that geometry must not shade over it.
            RecordMaterialDepth(graph, camera, depth);
            using var builder = graph.AddRasterRenderPass<MaterialResolveData>("VM Material Resolve", out var pass);
            pass.Camera = camera;
            pass.FarShadingMaxPixels = float.IsFinite(farShadingMaxPixels) ? Mathf.Max(0, farShadingMaxPixels) : 0;
            builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(camera.MaterialDepth, AccessFlags.Read);
            builder.UseTexture(camera.VisibilityTexture, AccessFlags.Read);
            builder.UseBuffer(camera.VisibilityTileMasks, AccessFlags.Read);
            ReadGeometry(builder, graph, camera.Frame, true);
            ReadBuffer(builder, graph, camera.Frame.Instances);
            ReadBuffer(builder, graph, camera.Frame.InstanceModelIds);
            builder.UseAllGlobalTextures(true);
            builder.SetRenderFunc(static (MaterialResolveData d, RasterGraphContext context) =>
            {
                var camera = d.Camera;
                for (int i = 0; i < camera.VisibilityDraws.Length; i++)
                {
                    var draw = camera.VisibilityDraws[i];
                    if (draw.Id == 0) continue;
                    var batch = camera.Frame.Colors[i];
                    var p = batch.Properties;
                    p.SetInt("_VMGraphDraw", 1);
                    p.SetInt("_VMUseGeometryAtlas", 1);
                    p.SetFloat("_VMCurrentTime", camera.Time);
                    p.SetInteger("_VMVisibilityBatchId", draw.Id);
                    p.SetTexture("_VMVisibilityTexture", camera.VisibilityTexture);
                    p.SetBuffer("_VMVisibilityTileMasks", camera.VisibilityTileMasks);
                    p.SetVector("_VMVisibilitySize", new Vector4(camera.Width, camera.Height, camera.TilesX, camera.TilesY));
                    p.SetFloat("_VMFarShadingMaxPixels", d.FarShadingMaxPixels);
                    context.cmd.DrawProcedural(Matrix4x4.identity, batch.Material, draw.ResolvePass,
                        MeshTopology.Triangles, 6, camera.TilesX * camera.TilesY, p);
                }
            });
        }

        sealed class ProxyCullData
        {
            public ComputeShader Shader;
            public VirtualMeshGraphRuntime.Frame Frame;
            public TextureHandle HiZ;
            public CullData HiZParameters;
        }
        void RecordProxySelection(RenderGraph graph, CameraFrame camera, TextureHandle hiZ)
        {
            if (camera.Frame.ProxyColors.Length == 0) return;
            using var builder = graph.AddComputePass<ProxyCullData>("VM Whole Model Proxy Selection", out var pass);
            pass.Shader = impostorCull; pass.Frame = camera.Frame; pass.HiZ = hiZ;
            pass.HiZParameters = new CullData { Valid = enableOcclusion, ViewProjection = camera.ViewProjection,
                NearPlane = camera.NearPlane, Width = camera.Width, Height = camera.Height, Mips = camera.Mips,
                Bias = depthBias, Padding = pixelPadding };
            builder.UseTexture(hiZ, AccessFlags.Read); ReadBuffer(builder, graph, camera.Frame.Instances);
            foreach (var batch in camera.Frame.ProxyColors)
            { ReadBuffer(builder, graph, batch.Candidates); WriteBuffer(builder, graph, batch.Visible); WriteBuffer(builder, graph, batch.Arguments); }
            builder.SetRenderFunc(static (ProxyCullData d, ComputeGraphContext context) =>
            {
                var cmd = context.cmd; var shader = d.Shader; int kernel = shader.FindKernel("Cull");
                BindHiZ(cmd, shader, d.HiZParameters);
                cmd.SetComputeTextureParam(shader, kernel, "_VMHiZTexture", d.HiZ);
                cmd.SetComputeBufferParam(shader, kernel, "_VMInstances", d.Frame.Instances);
                foreach (var batch in d.Frame.ProxyColors)
                {
                    cmd.SetBufferCounterValue(batch.Visible, 0);
                    cmd.SetComputeBufferParam(shader, kernel, "_VMImpostorCandidates", batch.Candidates);
                    cmd.SetComputeBufferParam(shader, kernel, "_VMImpostorVisible", batch.Visible);
                    cmd.SetComputeVectorParam(shader, "_VMImpostorBoundsCenter", batch.Key.Asset.Bounds.center);
                    cmd.SetComputeVectorParam(shader, "_VMImpostorBoundsExtents", batch.Key.Asset.Bounds.extents);
                    cmd.SetComputeIntParam(shader, "_VMImpostorCandidateCount", batch.Count);
                    for (int offset = 0; offset < batch.Count; offset += 65535 * 64)
                    {
                        cmd.SetComputeIntParam(shader, "_VMImpostorCandidateOffset", offset);
                        cmd.DispatchCompute(shader, kernel, Math.Min(65535, (batch.Count - offset + 63) / 64), 1, 1);
                    }
                    cmd.CopyCounterValue(batch.Visible, batch.Arguments, 4);
                }
            });
        }

        static bool HasReadOnlyMotionDepth(Material material)
        {
#if UNITY_EDITOR
            if (DiagnosticForceMotionDepthReadWrite) return false;
#endif
            if (material == null || material.shader == null) return false;
            // These audited MotionVectors passes use ZWrite Off and do not write
            // stencil. External tagged adapters keep the original declaration.
            switch (material.shader.name)
            {
                case "Virtual Mesh/Model Lit":
                case "Virtual Mesh/ElectricDreams Foliage":
                case "Virtual Mesh/ElectricDreams Hornbeam":
                case "Virtual Mesh/ElectricDreams Surface":
                case "Virtual Mesh/Model Impostor": return true;
                default: return false;
            }
        }

        sealed class ProxyDrawData { public CameraFrame Camera; public string Pass; public bool RenderingLayers; }
        void RecordProxyDraw(RenderGraph graph, CameraFrame camera, TextureHandle color, TextureHandle depthTarget,
            string label, string shaderPass, bool lit, TextureHandle renderingLayers = default)
        {
            if (camera.Frame.ProxyColors.Length == 0) return;
            using var builder = graph.AddRasterRenderPass<ProxyDrawData>(label, out var pass);
            pass.Camera = camera; pass.Pass = shaderPass; pass.RenderingLayers = renderingLayers.IsValid();
            if (color.IsValid()) builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
            if (pass.RenderingLayers) builder.SetRenderAttachment(renderingLayers, 1, AccessFlags.ReadWrite);
            // Motion only tests depth. A false write dependency keeps an otherwise
            // unused full-geometry pass alive for Scene View's final depth copy.
            bool readOnlyDepth = shaderPass == "MotionVectors";
            if (readOnlyDepth)
                foreach (var batch in camera.Frame.ProxyColors)
                    if (!HasReadOnlyMotionDepth(batch.Material)) { readOnlyDepth = false; break; }
            builder.SetRenderAttachmentDepth(depthTarget, readOnlyDepth ? AccessFlags.Read : AccessFlags.ReadWrite);
            ReadBuffer(builder, graph, camera.Frame.Instances); ReadBuffer(builder, graph, camera.Frame.PreviousObjectToWorld);
            foreach (var batch in camera.Frame.ProxyColors)
            { ReadBuffer(builder, graph, batch.Views); ReadBuffer(builder, graph, batch.Visible); ReadBuffer(builder, graph, batch.Arguments); }
            if (lit) builder.UseAllGlobalTextures(true);
            if (pass.RenderingLayers) builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (ProxyDrawData d, RasterGraphContext context) =>
            {
                if (d.RenderingLayers) context.cmd.EnableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                try
                {
                    foreach (var batch in d.Camera.Frame.ProxyColors)
                    {
                        int passIndex = batch.Material.FindPass(d.Pass);
                        if (passIndex < 0) throw new InvalidOperationException("Missing impostor pass: " + d.Pass);
                        var p = batch.Properties; var c = d.Camera;
                        p.SetInt("_VMGraphDraw", 1);
                        p.SetMatrix("_VMPreviousViewProjection", c.PreviousViewProjection); p.SetMatrix("_VMCurrentViewProjection", c.NonJitteredViewProjection);
                        p.SetInt("_VMHasPreviousFrame", c.HasPrevious ? 1 : 0);
                        context.cmd.DrawProceduralIndirect(Matrix4x4.identity, batch.Material, passIndex, MeshTopology.Triangles, batch.Arguments, 0, p);
                    }
                }
                finally
                {
                    if (d.RenderingLayers) context.cmd.DisableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                }
            });
        }

        sealed class PyramidData
        {
            public ComputeShader Shader;
            public TextureHandle Source, Destination;
            public int Mip, Width, Height, SourceWidth, SourceHeight;
            public CameraFrame Commit;
        }
        void BuildPyramid(RenderGraph graph, CameraFrame camera, TextureHandle source, TextureHandle destination, bool history)
        {
            for (int mip = 0; mip < camera.Mips; mip++)
            {
                using var builder = graph.AddComputePass<PyramidData>((history ? "VM Final HiZ " : "VM Current HiZ ") + mip, out var pass);
                pass.Shader = pyramid; pass.Source = mip == 0 ? source : destination; pass.Destination = destination; pass.Mip = mip;
                pass.Width = Math.Max(1, camera.Width >> mip); pass.Height = Math.Max(1, camera.Height >> mip);
                pass.SourceWidth = mip == 0 ? camera.Width : Math.Max(1, camera.Width >> (mip - 1));
                pass.SourceHeight = mip == 0 ? camera.Height : Math.Max(1, camera.Height >> (mip - 1));
                pass.Commit = history && mip == camera.Mips - 1 ? camera : null;
                if (mip == 0) builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, AccessFlags.ReadWrite);
                if (history) builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PyramidData d, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd; int kernel = d.Shader.FindKernel(d.Mip == 0 ? "CopyDepth" : "ReduceDepth");
                    cmd.SetComputeIntParams(d.Shader, "_VMHiZSourceSize", d.SourceWidth, d.SourceHeight);
                    cmd.SetComputeIntParams(d.Shader, "_VMHiZDestinationSize", d.Width, d.Height);
                    cmd.SetComputeIntParam(d.Shader, "_VMHiZCopyYFlip", 0);
                    cmd.SetComputeIntParam(d.Shader, "_VMHiZReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
                    cmd.SetComputeTextureParam(d.Shader, kernel, d.Mip == 0 ? "_VMHiZDepthSource" : "_VMHiZSource", d.Source, Math.Max(0, d.Mip - 1));
                    cmd.SetComputeTextureParam(d.Shader, kernel, "_VMHiZDestination", d.Destination, d.Mip);
                    cmd.DispatchCompute(d.Shader, kernel, (d.Width + 7) / 8, (d.Height + 7) / 8, 1);
                    if (d.Commit != null)
                    {
                        var c = d.Commit; var h = c.History;
                        h.ViewProjection = c.ViewProjection; h.NonJitteredViewProjection = c.NonJitteredViewProjection;
                        h.NearPlane = c.NearPlane; h.Time = c.Time; h.Revision = c.Frame.HistoryRevision; h.Valid = true;
                        h.Position = c.Position; h.Rotation = c.Rotation; h.Projection = c.Projection;
                    }
                });
            }
        }

        sealed class CopyData { public TextureHandle Source; public Material Material; public int Pass; }
        void CopyDepth(RenderGraph graph, TextureHandle source, TextureHandle destination, string label)
        {
            if (source.Equals(destination)) return;
            using var builder = graph.AddRasterRenderPass<CopyData>(label, out var pass);
            pass.Source = source; pass.Material = copy; pass.Pass = destination.GetDescriptor(graph).depthBufferBits != DepthBits.None ? 0 : 1;
            builder.UseTexture(source, AccessFlags.Read);
            if (pass.Pass == 0) builder.SetRenderAttachmentDepth(destination, AccessFlags.Write);
            else builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (CopyData d, RasterGraphContext context) =>
            {
                var properties = new MaterialPropertyBlock(); properties.SetTexture("_VMDepthSource", d.Source);
                context.cmd.DrawProcedural(Matrix4x4.identity, d.Material, d.Pass, MeshTopology.Triangles, 3, 1, properties);
            });
        }

        sealed class DrawData
        {
            public CameraFrame Camera;
            public VirtualMeshGraphRuntime.Batch[] Batches;
            public string Pass;
            public int Visibility;
            public bool SkipResolved, WriteVisibility, RenderingLayers;
        }
        void RecordDraw(RenderGraph graph, CameraFrame camera, VirtualMeshGraphRuntime.Batch[] batches, TextureHandle color, TextureHandle depthTarget,
            string label, string shaderPass, int visibility, bool lit, TextureHandle renderingLayers = default,
            bool skipResolved = false, bool writeVisibility = false)
        {
            if (writeVisibility && camera.IndexedVisibilityEligible)
            {
                RecordIndexedVisibilityGroups(graph, camera, batches, color, depthTarget, renderingLayers, visibility);
                return;
            }
            using var builder = graph.AddRasterRenderPass<DrawData>(label, out var pass);
            pass.Camera = camera; pass.Batches = batches; pass.Pass = shaderPass; pass.Visibility = visibility;
            pass.SkipResolved = skipResolved; pass.WriteVisibility = writeVisibility;
            pass.RenderingLayers = renderingLayers.IsValid();
            if (writeVisibility) builder.SetRenderAttachment(camera.VisibilityTexture, 0, AccessFlags.ReadWrite);
            if (color.IsValid()) builder.SetRenderAttachment(color, writeVisibility ? 1 : 0, AccessFlags.ReadWrite);
            if (pass.RenderingLayers) builder.SetRenderAttachment(renderingLayers, writeVisibility ? 2 : 1, AccessFlags.ReadWrite);
            // Motion only tests depth. A false write dependency keeps an otherwise
            // unused full-geometry pass alive for Scene View's final depth copy.
            bool readOnlyDepth = shaderPass == "MotionVectors";
            if (readOnlyDepth)
                foreach (var batch in batches)
                    if (!HasReadOnlyMotionDepth(batch.Material)) { readOnlyDepth = false; break; }
            builder.SetRenderAttachmentDepth(depthTarget, readOnlyDepth ? AccessFlags.Read : AccessFlags.ReadWrite);
            ReadGeometry(builder, graph, camera.Frame, true);
            for (int i = 0; i < batches.Length; i++)
            {
                var b = batches[i];
                bool resolved = camera.VisibilityDraws != null && camera.VisibilityDraws[i].Id != 0;
                if (skipResolved && resolved || writeVisibility && !resolved) continue;
                ReadBuffer(builder, graph, b.Instances); ReadBuffer(builder, graph, b.InstanceModelIds);
                ReadBuffer(builder, graph, b.Visible);
                if (visibility == 2) ReadBuffer(builder, graph, b.Counters);
                ReadBuffer(builder, graph, visibility == 1 ? b.EarlyArguments : visibility == 2 ? b.RecoveredArguments : b.Arguments);
            }
            // Forward+ material lighting consumes URP's graph-published shadow, SSAO and probe textures.
            if (lit) builder.UseAllGlobalTextures(true);
            if (pass.RenderingLayers) builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (DrawData d, RasterGraphContext context) =>
            {
                if (d.RenderingLayers) context.cmd.EnableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                try
                {
                    for (int i = 0; i < d.Batches.Length; i++)
                    {
                        var b = d.Batches[i];
                        var visibilityDraw = d.Camera.VisibilityDraws == null ? default : d.Camera.VisibilityDraws[i];
                        if (d.SkipResolved && visibilityDraw.Id != 0 || d.WriteVisibility && visibilityDraw.Id == 0) continue;
                        int passIndex = d.WriteVisibility ? visibilityDraw.RasterPass : b.Material.FindPass(d.Pass);
                        if (passIndex < 0 && d.Pass == "GraphForwardLit") passIndex = b.Material.FindPass("ForwardLit");
                        if (passIndex < 0) continue;
                        var p = b.Properties; var c = d.Camera;
                        p.SetInt("_VMGraphDraw", 1); p.SetInt("_VMUseGeometryAtlas", 1);
                        p.SetBuffer("_VMVisibleClusters", b.Visible);
                        p.SetBuffer("_VMVisibleRangeCounters", b.Counters);
                        p.SetInt("_VMUseRecoveredRange", d.Visibility == 2 ? 1 : 0);
                        if (d.WriteVisibility) p.SetInteger("_VMVisibilityBatchId", visibilityDraw.Id);
                        p.SetBuffer("_VMPreviousObjectToWorld", c.Frame.PreviousObjectToWorld);
                        p.SetMatrix("_VMPreviousViewProjection", c.PreviousViewProjection); p.SetMatrix("_VMCurrentViewProjection", c.NonJitteredViewProjection);
                        p.SetFloat("_VMPreviousTime", c.PreviousTime); p.SetFloat("_VMCurrentTime", c.Time); p.SetInt("_VMHasPreviousFrame", c.HasPrevious ? 1 : 0);
                        var args = d.Visibility == 1 ? b.EarlyArguments : d.Visibility == 2 ? b.RecoveredArguments : b.Arguments;
                        context.cmd.DrawProceduralIndirect(Matrix4x4.identity, b.Material, passIndex, MeshTopology.Triangles, args, 0, p);
                    }
                }
                finally
                {
                    if (d.RenderingLayers) context.cmd.DisableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                }
            });
        }

        sealed class IndexBatch
        {
            public VirtualMeshGraphRuntime.Batch Batch;
            public VisibilityDraw Draw;
            public int Capacity, OutputStart;
            public bool Indexed => Draw.IndexedRasterPass >= 0;
        }

        sealed class IndexGroupData
        {
            public CameraFrame Camera;
            public IndexBatch[] Batches;
            public ComputeShader Shader;
            public int Phase;
            public bool RenderingLayers;
        }

        void RecordIndexedVisibilityGroups(RenderGraph graph, CameraFrame camera, VirtualMeshGraphRuntime.Batch[] batches,
            TextureHandle normals, TextureHandle depthTarget, TextureHandle layers, int phase)
        {
            int capacity = camera.IndexedVisibilityResources.CapacityClusters, used = 0;
            var group = new List<IndexBatch>();
            for (int i = 0; i < batches.Length; i++)
            {
                var draw = camera.VisibilityDraws[i];
                if (draw.Id == 0) continue;
                var batch = batches[i];
                int reservation = 0;
                if (draw.IndexedRasterPass >= 0)
                {
                    // Existing asynchronous counters only size an acceleration slice.
                    // If stale estimates are too small, the GPU draws all original
                    // geometry through the fallback. They never decide visibility.
                    long estimate = batch.Owner.LastSampleGeneration < 0 ? batch.Capacity :
                        (long)batch.Owner.LastCounters[phase == 1 ? 0 : 2] * 9 / 8 + 64;
                    reservation = (int)Math.Min(capacity, Math.Min(batch.Capacity, Math.Max(1, estimate)));
                }
                if (group.Count > 0 && used + reservation > capacity)
                {
                    RecordIndexedVisibilityGroup(graph, camera, group.ToArray(), normals, depthTarget, layers, phase);
                    group.Clear(); used = 0;
                }
                int admitted = reservation;
#if UNITY_EDITOR
                if (DiagnosticIndexedMaxClusters.HasValue) admitted = Math.Clamp(DiagnosticIndexedMaxClusters.Value, 0, reservation);
#endif
                group.Add(new IndexBatch { Batch = batch, Draw = draw, Capacity = admitted, OutputStart = used * 192 });
                used += reservation;
            }
            if (group.Count > 0) RecordIndexedVisibilityGroup(graph, camera, group.ToArray(), normals, depthTarget, layers, phase);
        }

        void RecordIndexedVisibilityGroup(RenderGraph graph, CameraFrame camera, IndexBatch[] batches,
            TextureHandle normals, TextureHandle depthTarget, TextureHandle layers, int phase)
        {
            var resources = camera.IndexedVisibilityResources;
            using (var build = graph.AddComputePass<IndexGroupData>("VM Build Visibility Indices", out var pass))
            {
                pass.Camera = camera; pass.Batches = batches; pass.Shader = visibilityIndices; pass.Phase = phase;
                ReadBuffer(build, graph, camera.Frame.Atlas.Clusters);
                ReadBuffer(build, graph, resources.CanonicalCorners);
                WriteBuffer(build, graph, resources.Indices); WriteBuffer(build, graph, resources.IndexedArguments);
                WriteBuffer(build, graph, resources.FallbackArguments); WriteBuffer(build, graph, resources.DispatchArguments);
                WriteBuffer(build, graph, resources.Diagnostics);
                foreach (var item in batches)
                {
                    if (!item.Indexed) continue;
                    camera.IndexedVisibilityBatchCount++;
                    var b = item.Batch;
                    ReadBuffer(build, graph, b.Instances); ReadBuffer(build, graph, b.Visible); ReadBuffer(build, graph, b.Counters);
                    ReadBuffer(build, graph, phase == 1 ? b.EarlyArguments : b.RecoveredArguments);
                }
                build.SetRenderFunc(static (IndexGroupData d, ComputeGraphContext context) =>
                {
                    var r = d.Camera.IndexedVisibilityResources; var s = d.Shader; var cmd = context.cmd;
                    int prepare = s.FindKernel("Prepare"), buildIndices = s.FindKernel("Build");
                    cmd.SetComputeIntParam(s, "_VMIndexRecovered", d.Phase == 2 ? 1 : 0);
                    cmd.SetComputeBufferParam(s, prepare, "_VMIndexedArguments", r.IndexedArguments);
                    cmd.SetComputeBufferParam(s, prepare, "_VMFallbackArguments", r.FallbackArguments);
                    cmd.SetComputeBufferParam(s, prepare, "_VMIndexDispatchArguments", r.DispatchArguments);
                    cmd.SetComputeBufferParam(s, prepare, "_VMIndexDiagnostics", r.Diagnostics);
                    cmd.SetComputeBufferParam(s, buildIndices, "_VMIndexedArguments", r.IndexedArguments);
                    cmd.SetComputeBufferParam(s, buildIndices, "_VMClusters", d.Camera.Frame.Atlas.Clusters);
                    cmd.SetComputeBufferParam(s, buildIndices, "_VMCanonicalCorners", r.CanonicalCorners);
                    cmd.SetComputeBufferParam(s, buildIndices, "_VMIndexedIndices", r.Indices);
                    foreach (var item in d.Batches)
                    {
                        if (!item.Indexed) continue;
                        var b = item.Batch; int slot = item.Draw.Id - 1;
                        cmd.SetComputeIntParam(s, "_VMIndexCapacityClusters", item.Capacity);
                        cmd.SetComputeIntParam(s, "_VMIndexBatch", slot);
                        cmd.SetComputeIntParam(s, "_VMIndexOutputStart", item.OutputStart);
                        cmd.SetComputeIntParam(s, "_VMIndexDiagnosticOffset", ((d.Phase - 1) * 64 + slot) * 4);
                        cmd.SetComputeBufferParam(s, prepare, "_VMSourceArguments", d.Phase == 1 ? b.EarlyArguments : b.RecoveredArguments);
                        cmd.SetComputeBufferParam(s, prepare, "_VMVisibleRangeCounters", b.Counters);
                        cmd.DispatchCompute(s, prepare, 1, 1, 1);
                        cmd.SetComputeBufferParam(s, buildIndices, "_VMVisibleRangeCounters", b.Counters);
                        cmd.SetComputeBufferParam(s, buildIndices, "_VMVisibleClusters", b.Visible);
                        cmd.SetComputeBufferParam(s, buildIndices, "_VMInstances", b.Instances);
                        cmd.DispatchCompute(s, buildIndices, r.DispatchArguments, (uint)(slot * 3 * sizeof(uint)));
                    }
                });
            }
            string label = phase == 1 ? "VM Early Indexed Material Visibility" : "VM Recovered Indexed Material Visibility";
            using var raster = graph.AddRasterRenderPass<IndexGroupData>(label, out var data);
            data.Camera = camera; data.Batches = batches; data.Phase = phase; data.RenderingLayers = layers.IsValid();
            raster.SetRenderAttachment(camera.VisibilityTexture, 0, AccessFlags.ReadWrite);
            if (normals.IsValid()) raster.SetRenderAttachment(normals, 1, AccessFlags.ReadWrite);
            if (data.RenderingLayers) raster.SetRenderAttachment(layers, 2, AccessFlags.ReadWrite);
            raster.SetRenderAttachmentDepth(depthTarget, AccessFlags.ReadWrite);
            ReadGeometry(raster, graph, camera.Frame, true);
            ReadBuffer(raster, graph, resources.Indices); ReadBuffer(raster, graph, resources.IndexedArguments);
            ReadBuffer(raster, graph, resources.FallbackArguments);
            foreach (var item in batches)
            {
                var b = item.Batch;
                ReadBuffer(raster, graph, b.Instances); ReadBuffer(raster, graph, b.InstanceModelIds);
                ReadBuffer(raster, graph, b.Visible); ReadBuffer(raster, graph, b.Counters);
                if (!item.Indexed) ReadBuffer(raster, graph, phase == 1 ? b.EarlyArguments : b.RecoveredArguments);
            }
            if (data.RenderingLayers) raster.AllowGlobalStateModification(true);
            raster.SetRenderFunc(static (IndexGroupData d, RasterGraphContext context) =>
            {
                if (d.RenderingLayers) context.cmd.EnableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                try
                {
                    var c = d.Camera; var r = c.IndexedVisibilityResources;
                    foreach (var item in d.Batches)
                    {
                        var b = item.Batch; var p = b.Properties; int slot = item.Draw.Id - 1;
                        p.SetInt("_VMGraphDraw", 1); p.SetInt("_VMUseGeometryAtlas", 1);
                        p.SetBuffer("_VMVisibleClusters", b.Visible); p.SetBuffer("_VMVisibleRangeCounters", b.Counters);
                        p.SetInt("_VMUseRecoveredRange", d.Phase == 2 ? 1 : 0);
                        p.SetInteger("_VMVisibilityBatchId", item.Draw.Id);
                        p.SetFloat("_VMCurrentTime", c.Time);
                        if (item.Indexed)
                        {
                            context.cmd.DrawProceduralIndirect(r.Indices, Matrix4x4.identity, b.Material, item.Draw.IndexedRasterPass,
                                MeshTopology.Triangles, r.IndexedArguments, slot * 5 * sizeof(uint), p);
                            context.cmd.DrawProceduralIndirect(Matrix4x4.identity, b.Material, item.Draw.RasterPass,
                                MeshTopology.Triangles, r.FallbackArguments, slot * 4 * sizeof(uint), p);
                        }
                        else context.cmd.DrawProceduralIndirect(Matrix4x4.identity, b.Material, item.Draw.RasterPass,
                            MeshTopology.Triangles, d.Phase == 1 ? b.EarlyArguments : b.RecoveredArguments, 0, p);
                    }
                }
                finally
                {
                    if (d.RenderingLayers) context.cmd.DisableShaderKeyword(ShaderKeywordStrings.WriteRenderingLayers);
                }
            });
        }
    }
}
