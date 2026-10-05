#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace SST.StableRef
{
    /// <summary>
    /// Multi-object editing support for the selector drawers. A multi-object <see cref="SerializedObject"/>
    /// only reports the first target's managed reference, so the drawers ask here whether the selected
    /// targets actually hold different types, and route metadata writes (id stamping, snapshots) through
    /// one <see cref="SerializedObject"/> per target instead of the shared one.
    /// </summary>
    /// <remarks>
    /// The per-target scan is cached per (inspector <see cref="SerializedObject"/>, property path) and is
    /// dropped on selection change, undo/redo and any recorded property modification, so it runs once per
    /// change rather than on every repaint.
    /// </remarks>
    internal static class StableRefMultiEdit
    {
        private const int MaxCacheEntries = 512;

        private static readonly Dictionary<(SerializedObject, string, bool), bool> _mixedCache = new();
        private static bool _syncing;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            Selection.selectionChanged -= Invalidate;
            Selection.selectionChanged += Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
            Undo.undoRedoPerformed += Invalidate;
            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        internal static void Invalidate()
        {
            if (_mixedCache.Count == 0) return;
            _mixedCache.Clear();
            StableRefListDrawer.InvalidateCache();
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] mods)
        {
            if (!_syncing) Invalidate();
            return mods;
        }

        /// <summary>
        /// True when <paramref name="valueProp"/> is edited on several targets that do not all hold the same
        /// type (or the same none / missing state). On a cache miss with <paramref name="wrapperPath"/> set,
        /// each target's StableRef metadata is also synced on its own — never through the shared object.
        /// </summary>
        internal static bool IsMixed(SerializedProperty valueProp, string wrapperPath = null)
        {
            var shared = valueProp.serializedObject;
            if (!shared.isEditingMultipleObjects) return false;

            var key = (shared, valueProp.propertyPath, wrapperPath != null);
            if (_mixedCache.TryGetValue(key, out bool cached)) return cached;

            bool mixed = false;
            string first = null;
            bool hasFirst = false;

            foreach (var target in shared.targetObjects)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();

                string signature = Signature(so.FindProperty(valueProp.propertyPath));

                if (wrapperPath != null)
                {
                    var wrapper = so.FindProperty(wrapperPath);
                    if (wrapper != null)
                    {
                        if (StableRefEntry.Sync(wrapper))
                        {
                            _syncing = true;
                            try { so.ApplyModifiedProperties(); }
                            finally { _syncing = false; }
                        }
                        if (StableRefEntry.IsMissing(wrapper))
                            signature += "|" + wrapper.FindPropertyRelative(StableRefEntry.TypeIdFieldName).stringValue;
                    }
                }
                if (!hasFirst)
                {
                    first = signature;
                    hasFirst = true;
                }
                else if (signature != first)
                {
                    mixed = true;
                }
            }

            if (_mixedCache.Count >= MaxCacheEntries) _mixedCache.Clear();
            _mixedCache[key] = mixed;
            return mixed;
        }

        /// <summary>
        /// Applies the pending edit of the shared object, then refreshes the snapshot of every target from
        /// its own value. All writes happen in the same GUI event, so they collapse into one undo step.
        /// </summary>
        internal static void CaptureAllTargets(SerializedProperty wrapperProp)
        {
            var shared = wrapperProp.serializedObject;
            string wrapperPath = wrapperProp.propertyPath;
            shared.ApplyModifiedProperties();

            foreach (var target in shared.targetObjects)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var wrapper = so.FindProperty(wrapperPath);
                var value = wrapper?.FindPropertyRelative(StableRefEntry.ValueFieldName);
                if (value == null || value.propertyType != SerializedPropertyType.ManagedReference) continue;

                StableRefSnapshotCodec.Capture(wrapper, value);
                so.ApplyModifiedProperties();
            }
        }

        private static string Signature(SerializedProperty prop)
        {
            if (prop == null || prop.propertyType != SerializedPropertyType.ManagedReference) return "<absent>";
            if (prop.managedReferenceValue != null) return prop.managedReferenceFullTypename;
            return prop.managedReferenceId == StableRefEditorUtility.ManagedRefIdNull ? "" : "<missing>";
        }
    }
}
#endif
