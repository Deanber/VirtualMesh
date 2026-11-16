using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEngine;

namespace Unity.VirtualMesh.Editor
{
    [CustomEditor(typeof(VirtualMeshModelAsset))]
    internal sealed class VirtualMeshModelAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var model = (VirtualMeshModelAsset)target;
            EditorGUILayout.HelpBox("This model asset contains publishable clusters. Generated Virtual Mesh prefabs reference it directly; no scene bake or Library cache is needed at runtime.", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Source Mesh", model.SourceMesh, typeof(Mesh), false);
            EditorGUILayout.LabelField("Valid", model.IsValid ? "Yes" : "No");
            EditorGUILayout.LabelField("Vertices", model.Vertices.Length.ToString("N0"));
            EditorGUILayout.LabelField("Clusters", model.Clusters.Length.ToString("N0"));
            EditorGUILayout.LabelField("Material Slots", model.SubMeshCount.ToString());
            EditorGUILayout.LabelField("Geometry", (model.GeometryBytes / (1024d * 1024d)).ToString("F2") + " MiB");
            EditorGUILayout.LabelField("Simplification Error", model.SimplificationError.ToString("G6"));
            EditorGUILayout.LabelField("Revision", model.Revision.ToString());
            using (new EditorGUI.DisabledScope(model.SourceMesh == null))
            {
                if (GUILayout.Button("Bake Source Model"))
                    VirtualMeshModelPrebuilder.BakeSourceAsset(AssetDatabase.GetAssetPath(model.SourceMesh));
                if (GUILayout.Button("Validate LOD Coverage"))
                {
                    var report = VirtualMeshModelAssetBuilder.ValidateSourceClusters(model.SourceMesh, model.SimplificationError);
                    Debug.Log($"[Virtual Mesh] Validated {report.GroupCount} groups, {report.ClusterCount} clusters and {report.LodSamplesChecked} LOD samples without gaps.", model);
                }
            }
        }
    }
}
