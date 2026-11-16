using UnityEditor;

namespace Unity.VirtualMesh.Editor
{
    // Source-only distributions regenerate metadata in the consuming project.
    // Reapply the native binary's platform contract after that initial import.
    internal sealed class VirtualMeshNativePluginImport : AssetPostprocessor
    {
        private const string PluginPath = "Packages/com.unity.virtualmesh/Editor/x86_64/meshoptimizer.dll";

        private void OnPreprocessAsset()
        {
            if (assetPath == PluginPath && assetImporter is PluginImporter importer)
                Configure(importer);
        }

        [InitializeOnLoadMethod]
        private static void ScheduleConfiguration() => EditorApplication.delayCall += ConfigureImportedPlugin;

        private static void ConfigureImportedPlugin()
        {
            if (AssetImporter.GetAtPath(PluginPath) is PluginImporter importer && Configure(importer))
                importer.SaveAndReimport();
        }

        private static bool Configure(PluginImporter importer)
        {
            bool changed = importer.GetCompatibleWithAnyPlatform() || !importer.GetCompatibleWithEditor() ||
                importer.GetEditorData("OS") != "Windows" || importer.GetEditorData("CPU") != "x86_64";
            if (importer.GetCompatibleWithAnyPlatform()) importer.SetCompatibleWithAnyPlatform(false);
            var visited = new System.Collections.Generic.HashSet<int>();
            foreach (var field in typeof(BuildTarget).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                // Removed positive enum values remain for serialization compatibility.
                // They must not be passed back into the native importer API.
                if (field.IsDefined(typeof(System.ObsoleteAttribute), false)) continue;
                var target = (BuildTarget)field.GetValue(null);
                if ((int)target < 0 || !visited.Add((int)target)) continue;
                if (importer.GetCompatibleWithPlatform(target))
                {
                    importer.SetCompatibleWithPlatform(target, false);
                    changed = true;
                }
            }
            if (!changed) return false;
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("OS", "Windows");
            importer.SetEditorData("CPU", "x86_64");
            return true;
        }
    }
}
