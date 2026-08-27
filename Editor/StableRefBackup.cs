#if UNITY_EDITOR
using UnityEditor;

namespace SST.StableRef
{
    /// <summary>
    /// Public entry point to the value snapshot a StableRef entry keeps next to its managed reference,
    /// for inspector integrations that draw StableRef fields themselves instead of going through the
    /// built-in property drawer.
    /// </summary>
    /// <remarks>
    /// The snapshot — <see cref="StableRefBase.ValuesData"/>, <see cref="StableRefBase.ObjectRefs"/> and
    /// <see cref="StableRefBase.ObjectRefPaths"/> — is what lets a reference be rebuilt with its data
    /// intact once the value type is renamed and resolved through its stable id. The built-in drawer
    /// refreshes it on every edit on its own, so ordinary usage never needs this class. A drawer written
    /// for another inspector framework takes over that responsibility and must call <see cref="Capture"/>
    /// whenever it changes the value or any of the value's own fields; otherwise recovery restores data
    /// captured before those edits.
    /// </remarks>
    public static class StableRefBackup
    {
        private const string ValueFieldName = "Value";

        /// <summary>
        /// Refreshes the snapshot of a StableRef entry from the value it currently holds. Does nothing when
        /// the entry holds no value, since there is nothing to capture.
        /// </summary>
        /// <param name="entryProperty">
        /// Serialized property of the StableRef entry itself — the wrapper, not its <c>Value</c> field.
        /// </param>
        public static void Capture(SerializedProperty entryProperty)
        {
            if (!TryGetValueProperty(entryProperty, out var valueProperty))
            {
                return;
            }

            StableRefHandler.SnapshotBackup(entryProperty, valueProperty);
        }

        /// <summary>
        /// Writes the snapshot back onto the value the entry currently holds, matching fields by the property
        /// paths recorded at capture time. Call it after replacing the value with a freshly created instance
        /// of the resolved type; fields absent from the new type are skipped.
        /// </summary>
        /// <param name="entryProperty">
        /// Serialized property of the StableRef entry itself — the wrapper, not its <c>Value</c> field.
        /// </param>
        public static void Restore(SerializedProperty entryProperty)
        {
            if (!TryGetValueProperty(entryProperty, out var valueProperty))
            {
                return;
            }

            StableRefHandler.RestoreBackup(entryProperty, valueProperty);
        }

        private static bool TryGetValueProperty(SerializedProperty entryProperty, out SerializedProperty valueProperty)
        {
            valueProperty = entryProperty?.FindPropertyRelative(ValueFieldName);

            return valueProperty != null && valueProperty.propertyType == SerializedPropertyType.ManagedReference;
        }
    }
}
#endif
