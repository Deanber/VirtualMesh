using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>Enables scene validation policy and optional shared-atlas vertex streaming preferences.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Virtual Mesh Scene Settings")]
    public sealed class VirtualMeshSceneSettings : MonoBehaviour
    {
        [SerializeField, Tooltip("Request split vertex streams while this settings component is enabled. The atlas is shared across all loaded scenes and cameras; explicit runtime overrides take priority.")]
        private bool useSplitVertexStreams;
        [SerializeField, Tooltip("Prepare split streams with GPU transpose when supported. CPU fallback preserves data. Only contributes when split streams are requested.")]
        private bool useGpuVertexTranspose;
        internal bool RequestSplitVertexStreams => useSplitVertexStreams;
        internal bool RequestGpuVertexTranspose => useGpuVertexTranspose;
        private Scene registeredScene;
        private bool registered, registeredSplit, registeredGpu;

        private void OnEnable() => UpdateRegistration();

        // Detect actual scene/options changes. This never assigns global overrides
        // and never consults the currently rendering camera.
        private void LateUpdate() => UpdateRegistration();

        private void UpdateRegistration()
        {
            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) { Unregister(); return; }
            bool registrationChanged = !registered || registeredScene != scene;
            if (registrationChanged)
            {
                Unregister(); registeredScene = scene; registered = true;
                VirtualMeshRuntime.ChangeSceneSettingsCount(scene, 1);
            }
            if (registrationChanged || registeredSplit != useSplitVertexStreams || registeredGpu != useGpuVertexTranspose)
            {
                registeredSplit = useSplitVertexStreams; registeredGpu = useGpuVertexTranspose;
                VirtualMeshRuntime.SetSceneVertexStreamRequest(this);
            }
        }

        private void OnDisable() => Unregister();

        private void Unregister()
        {
            VirtualMeshRuntime.RemoveSceneVertexStreamRequest(this);
            if (!registered) return;
            VirtualMeshRuntime.ChangeSceneSettingsCount(registeredScene, -1);
            registered = registeredSplit = registeredGpu = false;
            registeredScene = default;
        }
    }
}
