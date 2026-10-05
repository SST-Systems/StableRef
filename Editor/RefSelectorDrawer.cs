#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Property drawer for a plain <c>[SerializeReference]</c> field marked with
    /// <see cref="RefSelectorAttribute"/>: the shared selector field and nothing else — no stable id, no
    /// recovery snapshot, no fix or find-script buttons.
    /// </summary>
    /// <remarks>
    /// When the stored class can no longer be resolved the field shows <c>Missing (ClassName)</c> instead of
    /// <c>None</c>, so the lost data is not overwritten unnoticed. Picking a type replaces the reference; the
    /// native missing-type data is left in the file and is never discarded by this drawer.
    /// </remarks>
    [CustomPropertyDrawer(typeof(RefSelectorAttribute))]
    public sealed class RefSelectorDrawer : PropertyDrawer
    {
        private static readonly Dictionary<(Object, long), GUIContent> _missingLabelCache = new();

        [InitializeOnLoadMethod]
        private static void Register()
        {
            Selection.selectionChanged -= _missingLabelCache.Clear;
            Selection.selectionChanged += _missingLabelCache.Clear;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                EditorGUI.HelpBox(position,
                    "[RefSelector] works only on [SerializeReference] fields.", MessageType.Error);
                EditorGUI.EndProperty();
                return;
            }

            var options = new StableRefSelectorField.Options { Mixed = StableRefMultiEdit.IsMixed(property) };
            if (!options.Mixed)
            {
                options.LabelOverride = GetMissingLabel(property);
                options.LabelColor = StableRefEntry.MissingLabelColor;
            }

            StableRefSelectorField.Draw(position, property, label, options);
            StableRefContextMenu.HandleElementContextClick(position, property);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => property.propertyType == SerializedPropertyType.ManagedReference
                ? StableRefSelectorField.GetHeight(property, StableRefMultiEdit.IsMixed(property))
                : EditorGUIUtility.singleLineHeight;

        private static GUIContent GetMissingLabel(SerializedProperty property)
        {
            if (property.managedReferenceValue != null) return null;

            long id = property.managedReferenceId;
            if (id == StableRefEditorUtility.ManagedRefIdNull) return null;

            var target = property.serializedObject.targetObject;
            if (target == null) return null;

            var key = (target, id);
            if (_missingLabelCache.TryGetValue(key, out var cached)) return cached;

            GUIContent result = null;
            if (SerializationUtility.HasManagedReferencesWithMissingTypes(target))
            {
                foreach (var m in SerializationUtility.GetManagedReferencesWithMissingTypes(target))
                {
                    if (m.referenceId != id) continue;
                    string fullName = string.IsNullOrEmpty(m.namespaceName)
                        ? m.className
                        : $"{m.namespaceName}.{m.className}";
                    result = new GUIContent($"Missing ({m.className})",
                        $"Type '{fullName}' from assembly '{m.assemblyName}' was not found.\n" +
                        "[RefSelector] fields are not tracked by Fix Missing Types: restore the class or add " +
                        "[MovedFrom] to its new location. Picking a type here replaces the value with a new " +
                        "empty instance.");
                    break;
                }
            }

            return _missingLabelCache[key] = result;
        }
    }
}
#endif
