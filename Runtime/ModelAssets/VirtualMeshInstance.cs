using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Selects how a virtual mesh instance refreshes its render state.</summary>
    public enum VirtualMeshValidationMode
    {
        /// <summary>Uses the validation policy configured for the instance's scene.</summary>
        SceneDefault,
        /// <summary>Validates on registration and after a rendering change is reported.</summary>
        ChangeDriven,
        /// <summary>Validates on each render context to detect runtime changes automatically.</summary>
        Dynamic
    }

    /// <summary>A movable instance of reusable, prebuilt model clusters. No scene bake is required.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Virtual Mesh Instance")]
    public sealed class VirtualMeshInstance : MonoBehaviour
    {
        [SerializeField, Tooltip("Inherit validation from the scene, or explicitly select change-driven or dynamic validation for this instance.")]
        private VirtualMeshValidationMode validationMode;

        private static readonly System.Collections.Concurrent.ConcurrentQueue<VirtualMeshInstance> validationChanges = new();
        [System.NonSerialized] private int validationEnqueued;
        [System.NonSerialized] private int registeredForValidation;

        /// <summary>
        /// Gets or sets how the instance selects its validation policy. Set this on the main thread.
        /// </summary>
        public VirtualMeshValidationMode ValidationMode
        {
            get => validationMode;
            set
            {
                if (validationMode == value)
                    return;

                validationMode = value;
                InvalidateRenderState();
            }
        }

        /// <summary>
        /// Gets the effective validation policy, including the scene default, or sets an explicit
        /// per-instance policy. Access this on the main thread. Change-driven instances require
        /// <see cref="InvalidateRenderState"/> after runtime changes to their rendering state.
        /// </summary>
        public bool UseChangeDrivenValidation
        {
            get => validationMode switch
            {
                VirtualMeshValidationMode.ChangeDriven => true,
                VirtualMeshValidationMode.Dynamic => false,
                _ => VirtualMeshRuntime.SceneUsesChangeDrivenValidation(this)
            };
            set => ValidationMode = value ? VirtualMeshValidationMode.ChangeDriven : VirtualMeshValidationMode.Dynamic;
        }

        internal static bool TryDequeueRenderStateChange(out VirtualMeshInstance instance)
        {
            if (!validationChanges.TryDequeue(out instance))
                return false;

            System.Threading.Volatile.Write(ref instance.validationEnqueued, 0);
            return true;
        }

        internal void SetRegisteredForValidation(bool registered) =>
            System.Threading.Volatile.Write(ref registeredForValidation, registered ? 1 : 0);

        /// <summary>
        /// Marks this instance for validation at the next render context. After changing its model,
        /// transform (including through a parent), layer, VM settings or source renderer, call this
        /// on each affected instance. The notification itself can be issued from any thread.
        /// </summary>
        public void InvalidateRenderState()
        {
            if (System.Threading.Interlocked.Exchange(ref validationEnqueued, 1) == 0)
                validationChanges.Enqueue(this);
        }

        /// <summary>
        /// Marks instances using a shared material for validation after an in-place material edit.
        /// Call this on the main thread. For a material assignment change, invalidate the instance.
        /// </summary>
        public static void InvalidateSharedMaterial(Material material)
        {
            VirtualMeshGraphRuntime.InvalidateSharedMaterial(material);
        }

        public VirtualMeshModelAsset Model;
        [Tooltip("Optional whole-model far representation. When unset, matching variants on Model are used.")]
        public VirtualMeshModelImpostorAsset Impostor;
        public Material[] Materials;
        [Tooltip("Generated model materials preserve the required shader variants in a player build.")]
        public Material[] RenderMaterials;
        public MeshRenderer SourceRenderer;
        [Min(0.01f)] public float PixelError = 1f;
        [Min(0f), Tooltip("Target average triangle size in pixels. Dense geometry may use up to twice Pixel Error. Set 0 to disable density adjustment.")]
        public float TargetTrianglePixels = 2f;
        [Min(0.25f), Tooltip("Multiplies the cluster LOD error for shadows. Higher values use coarser shadow geometry without changing visible geometry.")]
        public float ShadowLodMultiplier = 1f;
        public bool PreviewInEditMode = true;

        public string Diagnostic { get; private set; }
        public bool IsUsingVirtualMesh { get; private set; }
        private MeshRenderer hiddenRenderer;
        private bool originalForceOff;
        private ShadowCastingMode originalShadowMode;
        private bool sourceShadowsSuppressed;

        internal Transform RenderTransform => SourceRenderer != null ? SourceRenderer.transform : transform;
        internal bool SourceWasHidden => GetSourceWasHidden(SourceRenderer);
        internal ShadowCastingMode SourceShadowMode => GetSourceShadowMode(SourceRenderer);

        internal bool GetSourceWasHidden(MeshRenderer renderer) => renderer != null &&
            (hiddenRenderer == renderer ? originalForceOff : renderer.forceRenderingOff);

        internal ShadowCastingMode GetSourceShadowMode(MeshRenderer renderer) => renderer == null ? ShadowCastingMode.On :
            sourceShadowsSuppressed && hiddenRenderer == renderer ? originalShadowMode : renderer.shadowCastingMode;

        private void Reset()
        {
            SourceRenderer = GetComponent<MeshRenderer>();
            if (SourceRenderer != null) Materials = SourceRenderer.sharedMaterials;
        }

        private void OnEnable() => VirtualMeshRuntime.Register(this);
        private void OnDisable() { RestoreSource(); VirtualMeshRuntime.Unregister(this); }
        private void OnDestroy() { RestoreSource(); VirtualMeshRuntime.Unregister(this); }
        private void OnValidate()
        {
            PixelError = float.IsFinite(PixelError) ? Mathf.Max(0.01f, PixelError) : 1f;
            TargetTrianglePixels = float.IsFinite(TargetTrianglePixels) ? Mathf.Max(0f, TargetTrianglePixels) : 2f;
            ShadowLodMultiplier = float.IsFinite(ShadowLodMultiplier) ? Mathf.Max(0.25f, ShadowLodMultiplier) : 1f;
            // Prefab assets are also validated during import. Registration already
            // queues the current values if such an instance later enters a scene.
            if (System.Threading.Volatile.Read(ref registeredForValidation) != 0)
                InvalidateRenderState();
        }

        internal void SetVirtualRendering(bool active)
        {
            if (hiddenRenderer != null && hiddenRenderer != SourceRenderer) RestoreSource();
            IsUsingVirtualMesh = active;
            if (!active) { RestoreSource(); return; }
            if (SourceRenderer == null) return;
            CaptureSource();
            if (sourceShadowsSuppressed) hiddenRenderer.shadowCastingMode = originalShadowMode;
            sourceShadowsSuppressed = false;
            if (!hiddenRenderer.forceRenderingOff) hiddenRenderer.forceRenderingOff = true;
        }

        internal void SetResidencyFallback()
        {
            if (hiddenRenderer != null && hiddenRenderer != SourceRenderer) RestoreSource();
            IsUsingVirtualMesh = false;
            if (SourceRenderer == null) return;
            CaptureSource();
            hiddenRenderer.shadowCastingMode = ShadowCastingMode.Off;
            sourceShadowsSuppressed = true;
            hiddenRenderer.forceRenderingOff = originalForceOff || originalShadowMode == ShadowCastingMode.ShadowsOnly;
        }

        private void CaptureSource()
        {
            if (hiddenRenderer != null)
            {
                if (!sourceShadowsSuppressed) originalShadowMode = hiddenRenderer.shadowCastingMode;
                return;
            }
            hiddenRenderer = SourceRenderer;
            originalForceOff = hiddenRenderer.forceRenderingOff;
            originalShadowMode = hiddenRenderer.shadowCastingMode;
        }

        internal void SetDiagnostic(string message, bool warn = true)
        {
            if (Diagnostic == message) return;
            Diagnostic = message;
            if (warn && !string.IsNullOrEmpty(message)) Debug.LogWarning("[Virtual Mesh] " + message, this);
        }

        internal void RestoreSource()
        {
            if (hiddenRenderer != null)
            {
                hiddenRenderer.forceRenderingOff = originalForceOff;
                if (sourceShadowsSuppressed) hiddenRenderer.shadowCastingMode = originalShadowMode;
            }
            hiddenRenderer = null;
            sourceShadowsSuppressed = false;
            IsUsingVirtualMesh = false;
        }
    }
}
