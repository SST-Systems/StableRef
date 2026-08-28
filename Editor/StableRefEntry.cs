#if UNITY_EDITOR
using System;
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
