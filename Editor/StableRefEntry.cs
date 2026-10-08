#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Entry-level operations on a serialized StableRef — the canonical way to stamp, clear, inspect
    /// and recreate an entry through its <see cref="SerializedProperty"/>.
    /// </summary>
    /// <remarks>
    /// Public so an inspector integration that draws StableRef fields itself uses the same logic as the
    /// built-in drawer instead of reimplementing it. Every method takes the property of the entry
    /// (the <see cref="StableRef{T}"/> wrapper), not its <c>Value</c> field, and none of them call
    /// <c>ApplyModifiedProperties</c> — the caller owns the apply, so metadata and value changes land in
    /// the same undo step.
    /// </remarks>
    public static class StableRefEntry
    {
        /// <summary>Name of the serialized <c>Value</c> field of an entry.</summary>
        public const string ValueFieldName = "Value";
        /// <summary>Name of the serialized <c>TypeId</c> field of an entry.</summary>
        public const string TypeIdFieldName = "TypeId";
        /// <summary>Name of the serialized <c>TypeDisplayName</c> field of an entry.</summary>
        public const string TypeDisplayNameFieldName = "TypeDisplayName";
        /// <summary>Name of the serialized <c>ObjectRefs</c> field of an entry.</summary>
        public const string ObjectRefsFieldName = "ObjectRefs";
        /// <summary>Name of the serialized <c>ObjectRefPaths</c> field of an entry.</summary>
        public const string ObjectRefPathsFieldName = "ObjectRefPaths";
        /// <summary>Name of the serialized <c>ValuesData</c> field of an entry.</summary>
        public const string ValuesDataFieldName = "ValuesData";
        /// <summary>Name of the backing list field of a <see cref="StableRefList{T}"/>.</summary>
        public const string ListItemsFieldName = "_items";

        /// <summary>
        /// The entry that owns the <c>Value</c> property at <paramref name="valuePath"/>, or
        /// <see langword="null"/> when the path is not a StableRef <c>Value</c> field (e.g. a plain
        /// <c>[SerializeReference]</c> / <c>[RefSelector]</c> field).
        /// </summary>
        internal static SerializedProperty FindWrapperOfValue(SerializedObject so, string valuePath)
        {
            const string ValueSuffix = "." + ValueFieldName;
            if (!valuePath.EndsWith(ValueSuffix, StringComparison.Ordinal)) return null;

            var wrapper = so.FindProperty(valuePath.Substring(0, valuePath.Length - ValueSuffix.Length));
            if (wrapper == null || wrapper.FindPropertyRelative(TypeIdFieldName) == null) return null;
            return wrapper;
        }

        /// <summary>
        /// True when <paramref name="valueProp"/> is the <c>Value</c> of a StableRef entry that holds recoverable
        /// data but no value — replacing it discards that data, so it is confirmed first. Plain
        /// <c>[SerializeReference]</c> / <see cref="RefSelectorAttribute"/> fields are at the user's own risk and
        /// never count (see <see cref="PointsAtMissingType"/>).
        /// </summary>
        internal static bool HoldsMissingData(SerializedObject so, SerializedProperty valueProp)
        {
            if (valueProp.managedReferenceValue != null) return false;
            var wrapper = FindWrapperOfValue(so, valueProp.propertyPath);
            return wrapper != null && (PointsAtMissingType(valueProp) || IsMissing(wrapper));
        }

        /// <summary>True when the managed reference is set but its class can't be loaded.</summary>
        internal static bool PointsAtMissingType(SerializedProperty valueProp)
            => valueProp.managedReferenceValue == null
               && valueProp.managedReferenceId != StableRefEditorUtility.ManagedRefIdNull;

        /// <summary>
        /// Releases Unity's native missing-type data of <paramref name="target"/> after an explicit discard or a
        /// fix: drops the records in <paramref name="discardedIds"/>, then everything else once no StableRef entry
        /// on the object is still missing. Clears the inspector's "contains SerializeReference types which are
        /// missing" warning without waiting for a domain reload.
        /// </summary>
        /// <remarks>
        /// The full clear is what makes the warning go away reliably: a discarded value can leave records whose id
        /// can't be matched from the property side. Unity reports a field whose class can't be loaded as null
        /// (<c>managedReferenceId == -2</c>, also in <c>GetManagedReferenceIds</c> and JSON), so the records of
        /// plain <c>[SerializeReference]</c> / <see cref="RefSelectorAttribute"/> fields and of never-stamped
        /// entries can't be told apart and are cleared too — call this only after an explicit fix or discard of a
        /// missing StableRef entry.
        /// </remarks>
        internal static void ReleaseMissingData(UnityEngine.Object target, IEnumerable<long> discardedIds = null)
        {
            if (target == null || !SerializationUtility.HasManagedReferencesWithMissingTypes(target)) return;

            if (discardedIds != null)
            {
                var missingIds = new HashSet<long>();
                foreach (var missing in SerializationUtility.GetManagedReferencesWithMissingTypes(target))
                    missingIds.Add(missing.referenceId);
                foreach (long id in discardedIds)
                    if (missingIds.Contains(id))
                        SerializationUtility.ClearManagedReferenceWithMissingType(target, id);
            }

            if (SerializationUtility.HasManagedReferencesWithMissingTypes(target) && !StillNeedsMissingData(target))
                SerializationUtility.ClearAllManagedReferencesWithMissingTypes(target);
        }

        private static bool StillNeedsMissingData(UnityEngine.Object target)
        {
            var so = new SerializedObject(target);
            var iter = so.GetIterator();
            bool enter = true;

            while (iter.Next(enter))
            {
                if (iter.propertyType != SerializedPropertyType.ManagedReference)
                {
                    enter = StableRefPropertyUtils.MayHoldEntries(iter);
                    continue;
                }

                if (StableRefPropertyUtils.HasManagedValue(iter))
                {
                    enter = true;
                    continue;
                }

                var wrapper = FindWrapperOfValue(so, iter.propertyPath);
                bool needed = wrapper != null
                    ? IsMissing(wrapper)
                    : iter.managedReferenceId != StableRefEditorUtility.ManagedRefIdNull;
                if (needed) return true;
                enter = false;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="entry"/> belongs to a prefab instance (or variant) and isn't overridden there: its
        /// data comes from the source prefab, so stamping metadata here would only create an override. Such entries are
        /// synced in their source prefab — the drawer, multi-object editing and Resync all follow this rule.
        /// </summary>
        internal static bool IsInheritedFromPrefab(SerializedProperty entry)
            => !entry.prefabOverride && PrefabUtility.IsPartOfPrefabInstance(entry.serializedObject.targetObject);

        /// <summary>Content color the built-in drawer uses for a missing entry's label.</summary>
        public static readonly Color MissingLabelColor = new Color(0.65f, 0.65f, 0.65f);

        /// <summary>
        /// Stamps <c>TypeId</c> and <c>TypeDisplayName</c> from the entry's current value and refreshes
        /// the snapshot. No-op (returns <see langword="false"/>) when the entry holds no value, the type
        /// has no stable id, or the stored metadata is already up to date.
        /// </summary>
        public static bool Sync(SerializedProperty entry)
        {
            if (!TryGetValueProperty(entry, out var valueProp)) return false;

            var type = valueProp.managedReferenceValue?.GetType();
            if (type == null) return false;

            var id = StableRefTypeRegistry.GetOrAssignId(type);
            if (id == null) return false;

            var typeIdProp = entry.FindPropertyRelative(TypeIdFieldName);
            var dispProp = entry.FindPropertyRelative(TypeDisplayNameFieldName);
            if (typeIdProp == null) return false;

            var displayName = StableRefGenericUtils.DisplayName(type);
            if (typeIdProp.stringValue == id && dispProp?.stringValue == displayName) return false;

            typeIdProp.stringValue = id;
            if (dispProp != null) dispProp.stringValue = displayName;
            StableRefSnapshotCodec.Capture(entry, valueProp);
            return true;
        }

        /// <summary>
        /// Brings all metadata of an entry that holds a value in line with that value: <see cref="Sync"/>, and the
        /// snapshot re-captured even when the type is unchanged (code may have changed the value's fields). An id
        /// left over from another type is cleared together with its snapshot when the current type has no stable id,
        /// so recovery can't recreate the wrong type. Returns whether anything changed; missing and empty entries are
        /// left as they are.
        /// </summary>
        /// <remarks>
        /// The inspector keeps entries in sync as they are edited; this is for values written by code (importers,
        /// generators, <c>field.Value = ...</c>) — see <see cref="StableRefResync"/>.
        /// </remarks>
        public static bool Refresh(SerializedProperty entry)
        {
            if (!TryGetValueProperty(entry, out var valueProp)) return false;
            var type = valueProp.managedReferenceValue?.GetType();
            if (type == null) return false;

            if (StableRefTypeRegistry.GetOrAssignId(type) == null)
            {
                var typeIdProp = entry.FindPropertyRelative(TypeIdFieldName);
                if (typeIdProp == null || string.IsNullOrEmpty(typeIdProp.stringValue)) return false;
                if (StableRefTypeRegistry.GetType(typeIdProp.stringValue) == type) return false;

                typeIdProp.stringValue = string.Empty;
                var dispProp = entry.FindPropertyRelative(TypeDisplayNameFieldName);
                if (dispProp != null) dispProp.stringValue = StableRefGenericUtils.DisplayName(type);
                ClearSnapshot(entry);
                return true;
            }

            if (Sync(entry)) return true;

            string before = SnapshotSignature(entry);
            StableRefSnapshotCodec.Capture(entry, valueProp);
            return SnapshotSignature(entry) != before;
        }

        private static string SnapshotSignature(SerializedProperty entry)
        {
            var sb = new System.Text.StringBuilder(entry.FindPropertyRelative(ValuesDataFieldName)?.stringValue);
            var refs = entry.FindPropertyRelative(ObjectRefsFieldName);
            var paths = entry.FindPropertyRelative(ObjectRefPathsFieldName);
            int count = Math.Max(refs?.arraySize ?? 0, paths?.arraySize ?? 0);
            for (int i = 0; i < count; i++)
            {
                sb.Append('\n');
                if (refs != null && i < refs.arraySize)
                    sb.Append(StableRefEditorUtility.GetObjectReferenceId(refs.GetArrayElementAtIndex(i)));
                sb.Append('@');
                if (paths != null && i < paths.arraySize)
                    sb.Append(paths.GetArrayElementAtIndex(i).stringValue);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Fully resets the entry: sets <c>Value</c> to <see langword="null"/> and clears all metadata —
        /// <c>TypeId</c>, <c>TypeDisplayName</c>, <c>ObjectRefs</c>, <c>ObjectRefPaths</c>, <c>ValuesData</c>.
        /// Use it wherever an entry is emptied so no stale recovery data survives.
        /// </summary>
        public static void Clear(SerializedProperty entry)
        {
            if (entry == null) return;

            var valueProp = entry.FindPropertyRelative(ValueFieldName);
            if (valueProp != null && valueProp.propertyType == SerializedPropertyType.ManagedReference)
                valueProp.managedReferenceValue = null;

            var typeIdProp = entry.FindPropertyRelative(TypeIdFieldName);
            if (typeIdProp != null) typeIdProp.stringValue = string.Empty;
            var dispProp = entry.FindPropertyRelative(TypeDisplayNameFieldName);
            if (dispProp != null) dispProp.stringValue = string.Empty;
            ClearSnapshot(entry);
        }

        private static void ClearSnapshot(SerializedProperty entry)
        {
            entry.FindPropertyRelative(ObjectRefsFieldName)?.ClearArray();
            entry.FindPropertyRelative(ObjectRefPathsFieldName)?.ClearArray();
            var valuesDataProp = entry.FindPropertyRelative(ValuesDataFieldName);
            if (valuesDataProp != null) valuesDataProp.stringValue = string.Empty;
        }

        /// <summary>
        /// True when the entry is in the missing state: a <c>TypeId</c> is stored but the managed
        /// reference currently holds no value.
        /// </summary>
        public static bool IsMissing(SerializedProperty entry)
        {
            if (!TryGetValueProperty(entry, out var valueProp)) return false;
            var typeIdProp = entry.FindPropertyRelative(TypeIdFieldName);
            return typeIdProp != null
                && !string.IsNullOrEmpty(typeIdProp.stringValue)
                && valueProp.managedReferenceValue == null;
        }

        /// <summary>
        /// Recreates a missing entry's value from its stored <c>TypeId</c> and replays the snapshot onto
        /// the fresh instance. Returns <see langword="false"/> when the id no longer resolves or the type
        /// cannot be instantiated — the entry is left untouched in that case, so its recovery data is
        /// preserved. Performs no <c>ApplyModifiedProperties</c> and no <c>AssetDatabase</c> work.
        /// </summary>
        public static bool TryRecreate(SerializedProperty entry)
        {
            if (!TryGetValueProperty(entry, out var valueProp)) return false;
            if (valueProp.managedReferenceValue != null) return true;

            var typeIdProp = entry.FindPropertyRelative(TypeIdFieldName);
            if (typeIdProp == null || string.IsNullOrEmpty(typeIdProp.stringValue)) return false;

            var type = StableRefTypeRegistry.GetType(typeIdProp.stringValue);
            if (type == null) return false;

            object instance;
            try
            {
                instance = Activator.CreateInstance(type, nonPublic: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StableRef] Could not instantiate '{type.FullName}': {e.Message}");
                return false;
            }

            valueProp.managedReferenceValue = instance;
            StableRefSnapshotCodec.Restore(entry, valueProp);

            var dispProp = entry.FindPropertyRelative(TypeDisplayNameFieldName);
            if (dispProp != null) dispProp.stringValue = StableRefGenericUtils.DisplayName(type);
            return true;
        }

        /// <summary>Label the built-in drawer shows for a missing entry, built from its stored metadata.</summary>
        public static GUIContent BuildMissingLabel(SerializedProperty entry)
        {
            string displayName = entry?.FindPropertyRelative(TypeDisplayNameFieldName)?.stringValue;
            string typeId = entry?.FindPropertyRelative(TypeIdFieldName)?.stringValue;
            var resolvedType = string.IsNullOrEmpty(typeId) ? null : StableRefTypeRegistry.GetType(typeId);
            return new GUIContent(BuildMissingLabelText(displayName, resolvedType));
        }

        /// <summary>
        /// Text of the missing-entry label: <c>Missing (lost name) → resolved name</c>, with <c>?</c> for
        /// an unknown lost name and <c>None</c> when the id does not resolve to a type.
        /// </summary>
        public static string BuildMissingLabelText(string lostDisplayName, Type resolvedType)
        {
            if (string.IsNullOrEmpty(lostDisplayName)) lostDisplayName = "?";
            string resolvedName = resolvedType != null ? StableRefGenericUtils.DisplayName(resolvedType) : "None";
            return $"Missing ({lostDisplayName}) → {resolvedName}";
        }

        private static bool TryGetValueProperty(SerializedProperty entry, out SerializedProperty valueProp)
        {
            valueProp = entry?.FindPropertyRelative(ValueFieldName);
            return valueProp != null && valueProp.propertyType == SerializedPropertyType.ManagedReference;
        }
    }
}
#endif
