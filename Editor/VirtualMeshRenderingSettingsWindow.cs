using System.Collections.Generic;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace Unity.VirtualMesh.Editor
{
    internal sealed class VirtualMeshRenderingSettingsWindow : EditorWindow
    {
        const string MenuPath = "Tools/Virtual Mesh/Rendering Settings";

        [SerializeField] UniversalRendererData selectedRenderer;

        PopupField<UniversalRendererData> rendererPicker;
        VisualElement fields;
        HelpBox status;
        Button save;
        SerializedObject settings;
        Toggle pausePreview;

        [MenuItem(MenuPath)]
        static void Open() => GetWindow<VirtualMeshRenderingSettingsWindow>("Virtual Mesh");

        public void CreateGUI()
        {
            ReleaseBinding();
            rootVisualElement.Clear();
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            rootVisualElement.Add(scroll);
            var content = scroll.contentContainer;
            content.style.paddingLeft = 12;
            content.style.paddingRight = 12;
            content.style.paddingTop = 8;
            content.style.paddingBottom = 8;

            rendererPicker = new PopupField<UniversalRendererData> { label = "Renderer" };
            rendererPicker.formatSelectedValueCallback = renderer => renderer != null ? renderer.name : "None";
            rendererPicker.formatListItemCallback = renderer => AssetDatabase.GetAssetPath(renderer);
            rendererPicker.RegisterValueChangedCallback(change => BindRenderer(change.newValue));
            content.Add(rendererPicker);
            status = new HelpBox();
            content.Add(status);
            pausePreview = new Toggle("Pause VM in Edit Mode")
            {
                tooltip = "Hide opted-in VM previews and their source meshes and release VM buffers for this Editor session."
            };
            pausePreview.RegisterValueChangedCallback(change =>
            {
                VirtualMeshRuntime.SetEditorPreviewPaused(change.newValue);
                RefreshPreviewControl();
            });
            content.Add(pausePreview);
            pausePreview.schedule.Execute(RefreshPreviewControl).Every(300);
            RefreshPreviewControl();
            fields = new VisualElement();
            content.Add(fields);

            save = new Button(() =>
            {
                if (settings?.targetObject is VirtualMeshModelRenderFeature feature)
                {
                    settings.ApplyModifiedProperties();
                    AssetDatabase.SaveAssetIfDirty(feature);
                }
            }) { text = "Save Renderer Settings" };
            save.style.marginTop = 10;
            content.Add(save);
            RefreshRenderers();
        }

        void OnFocus()
        {
            if (rendererPicker != null) RefreshRenderers();
        }

        void OnDisable()
        {
            ReleaseBinding();
            rendererPicker = null;
        }

        void RefreshPreviewControl()
        {
            pausePreview.SetValueWithoutNotify(VirtualMeshRuntime.IsEditorPreviewPaused);
            pausePreview.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode);
        }

        void RefreshRenderers()
        {
            var renderers = new List<UniversalRendererData>();
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
                foreach (var renderer in pipeline.rendererDataList)
                    if (renderer is UniversalRendererData universal && FindFeature(universal) != null && !renderers.Contains(universal))
                        renderers.Add(universal);

            var projectRenderers = new List<UniversalRendererData>();
            foreach (string guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                if (FindFeature(renderer) != null && !renderers.Contains(renderer)) projectRenderers.Add(renderer);
            }
            projectRenderers.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
            renderers.AddRange(projectRenderers);

            rendererPicker.choices = renderers;
            rendererPicker.SetEnabled(renderers.Count != 0);
            var rendererToEdit = renderers.Contains(selectedRenderer) ? selectedRenderer :
                renderers.Find(renderer => FindFeature(renderer).isActive) ?? (renderers.Count > 0 ? renderers[0] : null);
            rendererPicker.SetValueWithoutNotify(rendererToEdit);
            BindRenderer(rendererToEdit);
        }

        static VirtualMeshModelRenderFeature FindFeature(UniversalRendererData renderer)
        {
            if (renderer == null) return null;
            VirtualMeshModelRenderFeature first = null;
            foreach (var candidate in renderer.rendererFeatures)
            {
                if (!(candidate is VirtualMeshModelRenderFeature feature)) continue;
                if (feature.isActive) return feature;
                if (first == null) first = feature;
            }
            return first;
        }

        void BindRenderer(UniversalRendererData renderer)
        {
            selectedRenderer = renderer;
            var feature = FindFeature(renderer);
            UpdateStatus(feature);
            if (settings != null && settings.targetObject == feature) return;
            ReleaseBinding();
            fields.Clear();
            save.SetEnabled(feature != null);
            if (feature == null) return;

            settings = new SerializedObject(feature);
            var lod = Section("LOD and Triangle Budgets");
            var budget = settings.FindProperty(nameof(VirtualMeshModelRenderFeature.lodBudget));
            var enabled = budget.FindPropertyRelative(nameof(VirtualMeshLodBudgetSettings.Enabled));
            lod.Add(new PropertyField(enabled, "Adaptive Triangle Budgets"));
            var curve = new VisualElement();
            lod.Add(curve);
            void AddBudgetField(string name, string label) => curve.Add(new PropertyField(budget.FindPropertyRelative(name), label));
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.BudgetMultiplier), "Global Budget Multiplier");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.NearTriangleBudget), "Near Triangle Request");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.MidTriangleBudget), "Mid Triangle Request");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.FarTriangleBudget), "Far Triangle Request");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.NearScreenHeight), "Near Screen Height");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.MidScreenHeight), "Mid Screen Height");
            AddBudgetField(nameof(VirtualMeshLodBudgetSettings.FarScreenHeight), "Far Screen Height");
            var budgetWarning = new HelpBox("Set screen heights in ascending order from Far to Mid to Near. " +
                "VM uses the default screen heights until the values are ordered.", HelpBoxMessageType.Warning);
            curve.Add(budgetWarning);
            var culling = Section("Culling and Distant Geometry");
            AddField(culling, nameof(VirtualMeshModelRenderFeature.enableOcclusion), "Occlusion Culling");
            AddField(culling, nameof(VirtualMeshModelRenderFeature.enableConeCulling), "Cluster Cone Culling");
            AddField(culling, nameof(VirtualMeshModelRenderFeature.enableEarlyWorkFrustum), "Early Work Group Frustum Culling");
            AddField(culling, nameof(VirtualMeshModelRenderFeature.enableImpostors), "Enable Baked Impostors");
            var impostorHeight = AddField(culling, nameof(VirtualMeshModelRenderFeature.impostorMaxScreenHeight), "Impostor Max Screen Height");

            var shading = Section("Shading and Motion");
            AddField(shading, nameof(VirtualMeshModelRenderFeature.enableVisibilityMaterialResolve), "Visibility Material Resolve (Experimental)");
            var farShading = AddField(shading, nameof(VirtualMeshModelRenderFeature.farShadingMaxPixels), "Far Shading Max Pixels");
            AddField(shading, nameof(VirtualMeshModelRenderFeature.enableDepthEqualShading), "Depth Equal Shading");
            AddField(shading, nameof(VirtualMeshModelRenderFeature.enableMotionVectors), "Motion Vectors");

            var advanced = Section("Occlusion Tuning", false);
            AddField(advanced, nameof(VirtualMeshModelRenderFeature.depthBias), "Depth Bias");
            AddField(advanced, nameof(VirtualMeshModelRenderFeature.pixelPadding), "Pixel Padding");

            void RefreshFieldStates()
            {
                curve.SetEnabled(enabled.boolValue);
                var near = budget.FindPropertyRelative(nameof(VirtualMeshLodBudgetSettings.NearScreenHeight)).floatValue;
                var mid = budget.FindPropertyRelative(nameof(VirtualMeshLodBudgetSettings.MidScreenHeight)).floatValue;
                var far = budget.FindPropertyRelative(nameof(VirtualMeshLodBudgetSettings.FarScreenHeight)).floatValue;
                budgetWarning.style.display = float.IsFinite(near) && near > mid && mid > far && far > 0 ? DisplayStyle.None : DisplayStyle.Flex;
                impostorHeight.SetEnabled(feature.enableImpostors);
                farShading.SetEnabled(feature.enableVisibilityMaterialResolve);
                UpdateStatus(feature);
            }

            fields.Bind(settings);
            fields.TrackSerializedObjectValue(settings, _ => RefreshFieldStates());
            RefreshFieldStates();
        }

        void UpdateStatus(VirtualMeshModelRenderFeature feature)
        {
            if (feature == null)
            {
                status.text = "Add Virtual Mesh Model Render Feature to a Universal Renderer Data asset, then return to this window.";
                status.messageType = HelpBoxMessageType.Info;
                return;
            }
            if (!feature.isActive)
            {
                status.text = "Enable Virtual Mesh Model Render Feature on this Renderer to use these settings.";
                status.messageType = HelpBoxMessageType.Warning;
                return;
            }
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
            {
                foreach (var renderer in pipeline.rendererDataList)
                    if (renderer == selectedRenderer)
                    {
                        status.text = "Applies to all VM instances seen by cameras using this Renderer. Screen heights are fractions of the viewport. Triangle requests are per instance.";
                        status.messageType = HelpBoxMessageType.Info;
                        return;
                    }
            }
            status.text = "The active URP pipeline does not list this Renderer. These settings apply when a camera uses this Renderer.";
            status.messageType = HelpBoxMessageType.Warning;
        }

        Foldout Section(string title, bool expanded = true)
        {
            var section = new Foldout { text = title, value = expanded };
            section.style.marginTop = 8;
            fields.Add(section);
            return section;
        }

        PropertyField AddField(VisualElement parent, string property, string label)
        {
            var field = new PropertyField(settings.FindProperty(property), label);
            parent.Add(field);
            return field;
        }

        void ReleaseBinding()
        {
            fields?.Unbind();
            settings?.Dispose();
            settings = null;
        }
    }
}
