#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unity.VirtualMesh.Runtime
{
    [InitializeOnLoad]
    internal static class VirtualMeshResidentEditorInvalidation
    {
        static VirtualMeshResidentEditorInvalidation()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!VirtualMeshGraphRuntime.HasResidentInstances)
                return;

            var roots = new HashSet<GameObject>();
            bool invalidateAll = false;
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        AddChangedObject(roots, ResolveObject(properties.instanceId));
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure);
                        AddRoot(roots, structure.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy);
                        AddRoot(roots, hierarchy.instanceId);
                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var children);
                        AddRoot(roots, children.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        AddRoot(roots, parent.instanceId);
                        AddRoot(roots, parent.previousParentInstanceId);
                        AddRoot(roots, parent.newParentInstanceId);
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                        AddRoot(roots, created.instanceId);
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        invalidateAll |= !AddRoot(roots, destroyed.parentInstanceId);
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (int instanceId in prefabs.instanceIds)
                            AddRoot(roots, instanceId);
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset);
                        InvalidateAsset(ResolveObject(asset.instanceId));
                        break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var createdAsset);
                        InvalidateAsset(ResolveObject(createdAsset.instanceId));
                        break;
                    case ObjectChangeKind.ChangeScene:
                    case ObjectChangeKind.DestroyAssetObject:
                        invalidateAll = true;
                        break;
                }
            }

            if (invalidateAll)
            {
                VirtualMeshGraphRuntime.InvalidateAllResidents();
                return;
            }

            foreach (GameObject root in roots)
                VirtualMeshGraphRuntime.InvalidateEditorHierarchy(root);
        }

        private static bool AddRoot(HashSet<GameObject> roots, int instanceId)
        {
            if (instanceId == 0 || !(ResolveObject(instanceId) is GameObject root) || !root)
                return false;

            roots.Add(root);
            return true;
        }

        private static Object ResolveObject(int instanceId)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        private static void AddChangedObject(HashSet<GameObject> roots, Object changed)
        {
            switch (changed)
            {
                case GameObject root when root:
                    roots.Add(root);
                    break;
                case VirtualMeshInstance instance when instance:
                    instance.InvalidateRenderState();
                    break;
                case Component component when component &&
                    (component is Transform || component is Renderer || component is MeshFilter || component is LODGroup):
                    roots.Add(component.gameObject);
                    break;
            }
        }

        private static void InvalidateAsset(Object changed)
        {
            switch (changed)
            {
                case Material material when material:
                    VirtualMeshGraphRuntime.InvalidateSharedMaterial(material);
                    break;
                case VirtualMeshModelAsset model when model:
                    VirtualMeshGraphRuntime.InvalidateModel(model);
                    break;
            }
        }
    }
}
#endif
