#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Property drawer for <see cref="StableRefBase"/> (<see cref="StableRef{T}"/> fields and
    /// <see cref="StableRefList{T}"/> elements): the shared selector field plus the StableRef-specific parts —
    /// id / snapshot sync, the missing-entry state with its fix button, and the find-script button.
    /// </summary>
    [CustomPropertyDrawer(typeof(StableRefBase), useForChildren: true)]
    public sealed class StableRefHandler : PropertyDrawer
    {
        private const float BtnW = 22f;

        private static GUIStyle _pingStyle;
        private static GUIStyle PingStyle => _pingStyle ??= new GUIStyle(EditorStyles.miniButton)
            { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(1, 1, 1, 1) };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var valueProp = property.FindPropertyRelative(StableRefEntry.ValueFieldName);
            if (valueProp == null)
            {
                EditorGUI.HelpBox(position, "StableRef: 'Value' field not found.", MessageType.Error);
                EditorGUI.EndProperty();
                return;
            }

            var options = new StableRefSelectorField.Options { RequireStableId = true };

            if (StableRefMultiEdit.IsMixed(valueProp, property.propertyPath))
            {
                options.Mixed = true;
                StableRefSelectorField.Draw(position, valueProp, label, options);
            }
            else if (valueProp.managedReferenceValue != null)
            {
                DrawValue(position, property, valueProp, label, options);
            }
            else if (StableRefEntry.IsMissing(property))
            {
                DrawMissing(position, property, valueProp, label, options);
            }
            else
            {
                StableRefSelectorField.Draw(position, valueProp, label, options);
            }

            StableRefContextMenu.HandleElementContextClick(position, valueProp);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var valueProp = property.FindPropertyRelative(StableRefEntry.ValueFieldName);
            if (valueProp == null || StableRefEntry.IsMissing(property)) return EditorGUIUtility.singleLineHeight;
            return StableRefSelectorField.GetHeight(valueProp, StableRefMultiEdit.IsMixed(valueProp, property.propertyPath));
        }

        private static void DrawValue(Rect position, SerializedProperty property, SerializedProperty valueProp,
            GUIContent label, StableRefSelectorField.Options options)
        {
            bool multi = property.serializedObject.isEditingMultipleObjects;
            if (!multi && !StableRefEntry.IsInheritedFromPrefab(property)) StableRefEntry.Sync(property);

            var fieldRect = new Rect(position.x, position.y, position.width - BtnW - 2f, position.height);
            var btnRect = new Rect(position.xMax - BtnW, position.y, BtnW, EditorGUIUtility.singleLineHeight);

            EditorGUI.BeginChangeCheck();
            StableRefSelectorField.Draw(fieldRect, valueProp, label, options);
            if (EditorGUI.EndChangeCheck())
            {
                if (multi) StableRefMultiEdit.CaptureAllTargets(property);
                else StableRefSnapshotCodec.Capture(property, valueProp);
            }

            if (EnabledButton(btnRect, StableRefEditorUtility.Icon("Search Icon")))
                StableRefEditorUtility.PingScript(valueProp.managedReferenceValue?.GetType());
        }

        private static void DrawMissing(Rect position, SerializedProperty property, SerializedProperty valueProp,
            GUIContent label, StableRefSelectorField.Options options)
        {
            float h = EditorGUIUtility.singleLineHeight;
            var lineRect = new Rect(position.x, position.y, position.width - BtnW - 2f, h);
            var controlRect = label != GUIContent.none ? EditorGUI.PrefixLabel(lineRect, label) : lineRect;
            var btnRect = new Rect(position.xMax - BtnW, position.y, BtnW, h);

            options.LabelOverride = StableRefEntry.BuildMissingLabel(property);
            options.LabelColor = StableRefEntry.MissingLabelColor;

            StableRefSelectorField.Draw(controlRect, valueProp, GUIContent.none, options);

            if (EnabledButton(btnRect, EditorGUIUtility.IconContent("console.warnicon.sml")))
                DoRecreate(property);
        }

        private static void DoRecreate(SerializedProperty wrapperProp)
        {
            var targets = wrapperProp.serializedObject.targetObjects;
            var wrapperPath = wrapperProp.propertyPath;

            EditorApplication.delayCall += () =>
            {
                int unresolvedTotal = 0;
                foreach (var target in targets)
                {
                    if (target == null) continue;

                    int fixedCount = StableRefMissingTypesWindow.FixTarget(target, out int unresolved, wrapperPath);
                    unresolvedTotal += unresolved;
                    if (fixedCount > 0 && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(target)))
                        AssetDatabase.SaveAssetIfDirty(target);
                }

                StableRefMultiEdit.Invalidate();

                if (unresolvedTotal > 0)
                    Debug.LogWarning(
                        "[StableRef] The entry's stable id no longer resolves to a type. It was kept " +
                        "untouched — restore the type (or its [RefTypeId]), or pick None / another type in " +
                        "its selector (or delete the element) to discard it.");
            };
        }

        /// <summary>Side button that stays clickable inside a disabled (e.g. read-only) inspector.</summary>
        private static bool EnabledButton(Rect rect, GUIContent content)
        {
            bool prev = GUI.enabled;
            GUI.enabled = true;
            bool clicked = GUI.Button(rect, content, PingStyle);
            GUI.enabled = prev;
            return clicked;
        }
    }
}
#endif
