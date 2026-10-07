#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Brings the stable id and snapshot of every StableRef entry in line with its current value
    /// (<see cref="StableRefEntry.Refresh"/>). The inspector does this as you edit; values written by code — importers,
    /// generators, <c>field.Value = ...</c> in editor scripts — are only covered once the field is drawn, and until
    /// then a rename of their class can't be recovered (no id) or is recovered as the previous type (stale id).
    /// </summary>
    /// <remarks>
    /// <b>Tools/StableRef/Resync All</b> covers ScriptableObjects and prefabs under <c>Assets/</c> and the scenes that
    /// are already open. Values themselves are never changed, and missing entries are left alone (Fix Missing Types
    /// handles them). Changed assets are saved one by one; changed scenes are marked dirty, not saved. In prefab
    /// instances and variants only entries whose value is overridden are touched — inherited ones are resynced in
    /// their source prefab — so no override is created for metadata alone.
    /// </remarks>
    public static class StableRefResync
    {
        /// <summary>Result of a resync run.</summary>
        public struct Report
        {
            /// <summary>Entries whose metadata changed.</summary>
            public int UpdatedEntries;
            /// <summary>Objects (assets, components) with at least one changed entry.</summary>
            public int UpdatedObjects;
            /// <summary>Value types without a stable id — their entries can't be protected until they get one.</summary>
            public HashSet<Type> TypesWithoutId;
        }

        [MenuItem("Tools/StableRef/Resync All", priority = 301)]
        private static void ResyncAllMenu()
        {
            if (!EditorUtility.DisplayDialog("Resync All StableRef Entries",
                    "Updates the stable id and snapshot of every StableRef entry in ScriptableObjects and prefabs under " +
                    "Assets/ and in the open scenes to match its current value. Values are not changed.\n\n" +
                    "Changed assets are saved; changed scenes are marked dirty.",
                    "Resync", "Cancel"))
                return;

            var report = ResyncAll();

            Debug.Log($"[StableRef] Resync: updated {report.UpdatedEntries} entr{(report.UpdatedEntries == 1 ? "y" : "ies")} " +
                      $"in {report.UpdatedObjects} object{(report.UpdatedObjects == 1 ? "" : "s")}.");
            if (report.TypesWithoutId.Count > 0)
            {
                Debug.LogWarning(
                    "[StableRef] Resync: these value types have no stable id, so their entries aren't rename-safe — add " +
                    "[RefTypeId] (or [assembly: RefTypeIdFor]) and run Resync All again:\n" +
                    string.Join("\n", report.TypesWithoutId.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal)));
            }
        }

        /// <summary>
        /// Resyncs ScriptableObjects and prefabs under <c>Assets/</c> and the open scenes, with a cancelable progress
        /// bar. Never opens or saves scenes.
        /// </summary>
        public static Report ResyncAll()
        {
            var report = NewReport();
            StableRefEditorUtility.ScanProject("Resyncing StableRef entries…", source =>
            {
                UnityEngine.Object changed = null;
                foreach (var target in source.Targets)
                    if (ResyncInto(target, ref report)) changed = target;
                if (changed == null) return;

                if (source.IsScene) EditorSceneManager.MarkSceneDirty(source.Scene);
                else AssetDatabase.SaveAssetIfDirty(changed);
            });

            EditorUtility.UnloadUnusedAssetsImmediate();
            return report;
        }

        /// <summary>
        /// Resyncs every StableRef entry on <paramref name="target"/>, nested ones included, as one modification;
        /// marks the object dirty when something changed. Does not save.
        /// </summary>
        /// <returns>The number of entries whose metadata changed.</returns>
        public static int ResyncObject(UnityEngine.Object target)
        {
            var report = NewReport();
            ResyncInto(target, ref report);
            return report.UpdatedEntries;
        }

        private static Report NewReport() => new() { TypesWithoutId = new HashSet<Type>() };

        private static bool ResyncInto(UnityEngine.Object target, ref Report report)
        {
            if (target is not MonoBehaviour && target is not ScriptableObject) return false;
            if (!StableRefPropertyUtils.MayContainStableRef(target.GetType())) return false;

            var so = new SerializedObject(target);
            so.Update();

            int changed = 0;
            foreach (var path in CollectEntryPaths(so))
            {
                var entry = so.FindProperty(path);
                if (entry == null) continue;
                if (StableRefEntry.IsInheritedFromPrefab(entry)) continue;

                var value = entry.FindPropertyRelative(StableRefEntry.ValueFieldName)?.managedReferenceValue;
                if (value != null && StableRefTypeRegistry.GetOrAssignId(value.GetType()) == null)
                    report.TypesWithoutId.Add(value.GetType());

                if (StableRefEntry.Refresh(entry)) changed++;
            }

            if (changed == 0) return false;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
            report.UpdatedEntries += changed;
            report.UpdatedObjects++;
            return true;
        }

        /// <summary>
        /// Paths of all entries that hold a value (with <paramref name="includeEmpty"/> also empty and missing ones),
        /// deepest first: an entry's snapshot includes the metadata of the entries nested in its value, so those must
        /// be up to date before it is captured.
        /// </summary>
        internal static List<string> CollectEntryPaths(SerializedObject so, bool includeEmpty = false)
        {
            var result = new List<string>();
            var iter = so.GetIterator();
            bool enter = true;

            while (iter.Next(enter))
            {
                if (iter.propertyType == SerializedPropertyType.ManagedReference)
                {
                    bool hasValue = iter.managedReferenceValue != null;
                    if ((hasValue || includeEmpty) && iter.name == StableRefEntry.ValueFieldName)
                    {
                        var wrapper = StableRefEntry.FindWrapperOfValue(so, iter.propertyPath);
                        if (wrapper != null) result.Add(wrapper.propertyPath);
                    }
                    enter = hasValue;
                    continue;
                }

                enter = iter.propertyType == SerializedPropertyType.Generic;
            }

            result.Sort((a, b) => Depth(b).CompareTo(Depth(a)));
            return result;
        }

        private static int Depth(string path)
        {
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
                if (path[i] == '.') depth++;
            return depth;
        }
    }
}
#endif
