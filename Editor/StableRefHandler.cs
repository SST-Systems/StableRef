#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    [CustomPropertyDrawer(typeof(StableRefBase), useForChildren: true)]
    public sealed class StableRefHandler : PropertyDrawer
    {
        internal static GUIContent BrokenLabelOverride;
        internal static Color? BrokenColorOverride;

        private static GUIStyle _pingStyle;
        private static GUIStyle PingStyle => _pingStyle ??= new GUIStyle(EditorStyles.miniButton)
            { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(1, 1, 1, 1) };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var valueProp = property.FindPropertyRelative("Value");
            var typeIdProp = property.FindPropertyRelative("TypeId");

            if (valueProp == null)
            {
                EditorGUI.HelpBox(position, "StableRef: 'Value' field not found.", MessageType.Error);
                EditorGUI.EndProperty();
                return;
            }

            bool hasValue = valueProp.managedReferenceValue != null;
            bool hasTypeId = !string.IsNullOrEmpty(typeIdProp?.stringValue);

            float h = EditorGUIUtility.singleLineHeight;

            if (hasValue)
            {
                StableRefEntry.Sync(property);

                const float BtnW = 22f;
                var fieldRect = new Rect(position.x, position.y, position.width - BtnW - 2f, position.height);
                var btnRect   = new Rect(position.xMax - BtnW, position.y, BtnW, h);

                EditorGUI.BeginChangeCheck();
                DrawSelector(fieldRect, valueProp, label);
                if (EditorGUI.EndChangeCheck())
                    StableRefSnapshotCodec.Capture(property, valueProp);

                bool prev = GUI.enabled;
                GUI.enabled = true;
                if (GUI.Button(btnRect, StableRefEditorUtility.Icon("d_Search Icon"), PingStyle))
                    StableRefEditorUtility.PingScript(valueProp.managedReferenceValue?.GetType());
                GUI.enabled = prev;
            }
            else if (hasTypeId)
            {
                const float BtnW = 22f;
                var lineRect = new Rect(position.x, position.y, position.width - BtnW - 2f, h);
                var controlRect = label != GUIContent.none
                    ? EditorGUI.PrefixLabel(lineRect, label)
                    : lineRect;
                var btnRect = new Rect(position.xMax - BtnW, position.y, BtnW, h);

                BrokenLabelOverride = StableRefEntry.BuildMissingLabel(property);
                BrokenColorOverride = StableRefEntry.MissingLabelColor;

                using (new EditorGUI.DisabledScope(true))
                    DrawSelector(controlRect, valueProp, GUIContent.none);

                bool prevEnabled = GUI.enabled;
                GUI.enabled = true;
                if (GUI.Button(btnRect, EditorGUIUtility.IconContent("console.warnicon.sml"), PingStyle))
                    DoRecreate(property);
                GUI.enabled = prevEnabled;
            }
            else
            {
                DrawSelector(position, valueProp, label);
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var valueProp = property.FindPropertyRelative("Value");
            var typeIdProp = property.FindPropertyRelative("TypeId");

            if (valueProp == null) return EditorGUIUtility.singleLineHeight;

            bool hasValue = valueProp.managedReferenceValue != null;
            bool hasTypeId = !string.IsNullOrEmpty(typeIdProp?.stringValue);

            if (hasValue) return GetSelectorHeight(valueProp);
            if (hasTypeId) return EditorGUIUtility.singleLineHeight;
            return GetSelectorHeight(valueProp);
        }

        private static void DrawSelector(Rect position, SerializedProperty property, GUIContent label)
        {
            var brokenLabel = BrokenLabelOverride;
            BrokenLabelOverride = null;
            Color? brokenColor = BrokenColorOverride;
            BrokenColorOverride = null;

            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                EditorGUI.HelpBox(position, "StableRef selector works only with [SerializeReference].", MessageType.Error);
                return;
            }

            bool hasValue = property.managedReferenceValue != null;
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            const float FoldoutW = 4f;

            var ev = Event.current;
            if (line.Contains(ev.mousePosition) &&
                (ev.type == EventType.ContextClick ||
                 (ev.type == EventType.MouseDown && ev.button == 1)))
            {
                StableRefContextMenu.ShowDirectMenu(property);
                ev.Use();
                return;
            }

            bool hasLabel = label != GUIContent.none && !string.IsNullOrEmpty(label.text);
            var controlLine = hasLabel
                ? EditorGUI.PrefixLabel(line, TruncatedLabel(label, EditorGUIUtility.labelWidth - 12f))
                : line;

            float btnX = hasLabel ? controlLine.x : controlLine.x + FoldoutW;
            float btnW = hasLabel ? controlLine.width : controlLine.width - FoldoutW;

            bool hasChildren = hasValue && HasVisibleChildren(property);
            if (hasChildren)
            {
                float foldoutX = hasLabel ? controlLine.x - 4f : controlLine.x;
                int prevIndent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                EditorGUI.BeginChangeCheck();
                bool expanded = EditorGUI.Foldout(
                    new Rect(foldoutX, controlLine.y, FoldoutW, controlLine.height), property.isExpanded, GUIContent.none, true);
                EditorGUI.indentLevel = prevIndent;
                if (EditorGUI.EndChangeCheck())
                {
                    if (ev.alt)
                        StableRefPropertyUtils.SetExpandedRecursive(property, expanded);
                    else
                        property.isExpanded = expanded;
                    StableRefListDrawer.InvalidateCache();
                }
            }

            var btnRect = new Rect(btnX, controlLine.y, btnW, controlLine.height);

            GUIContent buttonLabel = brokenLabel ?? new GUIContent(GetCurrentLabel(property));

            var prevColor = GUI.contentColor;
            if (brokenColor.HasValue) GUI.contentColor = brokenColor.Value;

            bool clicked = GUI.Button(btnRect, buttonLabel, EditorStyles.popup);
            GUI.contentColor = prevColor;
            if (clicked)
                StableRefSelectorWindow.Show(btnRect, property, StableRefPropertyUtils.GetEntries(property));

            if (hasChildren && property.isExpanded)
            {
                float yOff = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                EditorGUI.indentLevel++;
                DrawChildren(new Rect(position.x, position.y + yOff, position.width, position.height - yOff), property);
                EditorGUI.indentLevel--;
            }
        }

        private static float GetSelectorHeight(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
                return EditorGUIUtility.singleLineHeight;

            float h = EditorGUIUtility.singleLineHeight;

            if (property.managedReferenceValue != null && property.isExpanded)
            {
                var child = property.Copy();
                var end = property.GetEndProperty();
                if (child.NextVisible(true))
                    while (!SerializedProperty.EqualContents(child, end))
                    {
                        h += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
                        if (!child.NextVisible(false)) break;
                    }
            }

            return h;
        }

        private static bool HasVisibleChildren(SerializedProperty property)
        {
            var child = property.Copy();
            var end = property.GetEndProperty();
            return child.NextVisible(true) && !SerializedProperty.EqualContents(child, end);
        }

        private static string GetCurrentLabel(SerializedProperty property)
        {
            if (property.managedReferenceValue == null) return "None";
            var t = property.managedReferenceValue.GetType();
            var cat = t.GetCustomAttribute<StableRefCategoryAttribute>();
            return cat != null && StableRefSelectorWindow.ShowCategoryInLabel
                ? $"{cat.Category}/{StableRefGenericUtils.DisplayName(t)}"
                : StableRefGenericUtils.DisplayName(t);
        }

        private static GUIContent TruncatedLabel(GUIContent label, float maxWidth)
        {
            var style = EditorStyles.label;
            if (style.CalcSize(label).x <= maxWidth) return label;
            float ellipsisW = style.CalcSize(new GUIContent("...")).x;
            var text = label.text;
            while (text.Length > 0 && style.CalcSize(new GUIContent(text)).x + ellipsisW > maxWidth)
                text = text.Substring(0, text.Length - 1);
            return new GUIContent(text + "...", label.tooltip);
        }

        private static void DrawChildren(Rect rect, SerializedProperty property)
        {
            var child = property.Copy();
            var end = property.GetEndProperty();
            float y = rect.y;
            if (!child.NextVisible(true)) return;
            while (!SerializedProperty.EqualContents(child, end))
            {
                float h = EditorGUI.GetPropertyHeight(child, true);
                EditorGUI.PropertyField(new Rect(rect.x, y, rect.width, h), child, true);
                y += h + EditorGUIUtility.standardVerticalSpacing;
                if (!child.NextVisible(false)) break;
            }
        }

        private static void DoRecreate(SerializedProperty wrapperProp)
        {
            var target = wrapperProp.serializedObject.targetObject;
            var wrapperPath = wrapperProp.propertyPath;
            var assetPath = AssetDatabase.GetAssetPath(target);

            EditorApplication.delayCall += () =>
            {
                if (target == null) return;

                int fixedCount = StableRefMissingTypesWindow.FixTarget(target, out int unresolved, wrapperPath);
                if (fixedCount == 0)
                {
                    if (unresolved > 0)
                        Debug.LogWarning(
                            "[StableRef] The entry's stable id no longer resolves to a type. It was kept " +
                            "untouched — restore the type (or its [StableTypeId]), or right-click the field " +
                            "and choose Clear Entry to discard it.");
                    return;
                }

                if (!string.IsNullOrEmpty(assetPath))
                    AssetDatabase.SaveAssetIfDirty(target);
            };
        }
    }
}
#endif