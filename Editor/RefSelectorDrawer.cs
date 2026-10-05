#if UNITY_EDITOR
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
    /// Integrity is the user's responsibility: a field whose class can no longer be loaded simply shows an empty
    /// selector (<c>None</c>), like a bare <c>[SerializeReference]</c>. Picking a type or None replaces it without
    /// confirmation.
    /// </remarks>
    [CustomPropertyDrawer(typeof(RefSelectorAttribute))]
    public sealed class RefSelectorDrawer : PropertyDrawer
    {
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
            StableRefSelectorField.Draw(position, property, label, options);
            StableRefContextMenu.HandleElementContextClick(position, property);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => property.propertyType == SerializedPropertyType.ManagedReference
                ? StableRefSelectorField.GetHeight(property, StableRefMultiEdit.IsMixed(property))
                : EditorGUIUtility.singleLineHeight;
    }
}
#endif
