#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace SST.StableRef
{
    [CustomPropertyDrawer(typeof(StableRefListBase), useForChildren: true)]
    public class StableRefListDrawer : PropertyDrawer
    {
        private static readonly Dictionary<(int, string), ReorderableList> _cache = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var itemsProp = property.FindPropertyRelative("_items");
            if (itemsProp == null)
            {
                EditorGUI.PropertyField(position, property, label, true);
                return;
            }

            EditorGUI.BeginProperty(position, label, property);

            var headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var ev = Event.current;

            if ((ev.type == EventType.ContextClick || (ev.type == EventType.MouseDown && ev.button == 1))
                && headerRect.Contains(ev.mousePosition))
            {
                StableRefContextMenu.ShowListMenu(itemsProp.Copy());
                ev.Use();
            }
            else
            {
                property.isExpanded = EditorGUI.Foldout(headerRect, property.isExpanded, label, true);
            }

            if (property.isExpanded)
            {
                var list = GetOrCreateList(property, itemsProp);
                bool hasBroken = HasBrokenRefs(itemsProp);
                float y = position.y + EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

                float fixBtnH = EditorGUIUtility.singleLineHeight + 4f;
                if (hasBroken)
                {
                    var btnRect = new Rect(position.x, y, position.width, fixBtnH);
                    if (GUI.Button(btnRect, GUIContent.none))
                        StableRefMissingTypesWindow.OpenAndScan();
                    var warnIcon = EditorGUIUtility.IconContent("console.warnicon.sml");
                    if (warnIcon?.image != null)
                    {
                        const string btnLabel = "Fix Missing References";
                        const float Gap = 4f;
                        float iconW = warnIcon.image.width;
                        float iconH = warnIcon.image.height;
                        float textW = EditorStyles.label.CalcSize(new GUIContent(btnLabel)).x + 8f;
                        float groupX = btnRect.x + (btnRect.width - iconW - Gap - textW) * 0.5f;
                        GUI.DrawTexture(
                            new Rect(groupX, btnRect.y + (fixBtnH - iconH) * 0.5f, iconW, iconH),
                            warnIcon.image, ScaleMode.ScaleToFit);
                        GUI.Label(
                            new Rect(groupX + iconW + Gap, btnRect.y, textW, fixBtnH),
                            btnLabel);
                    }
                    y += fixBtnH + EditorGUIUtility.standardVerticalSpacing;
                }

                int savedIndent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                using (new EditorGUI.DisabledScope(hasBroken))
                    list.DoList(new Rect(position.x, y, position.width, list.GetHeight()));
                EditorGUI.indentLevel = savedIndent;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var itemsProp = property.FindPropertyRelative("_items");
            if (itemsProp == null)
                return EditorGUI.GetPropertyHeight(property, label, true);

            float h = EditorGUIUtility.singleLineHeight;

            if (property.isExpanded)
            {
                h += EditorGUIUtility.standardVerticalSpacing + GetOrCreateList(property, itemsProp).GetHeight();

                if (HasBrokenRefs(itemsProp))
                    h += (EditorGUIUtility.singleLineHeight + 4f) + EditorGUIUtility.standardVerticalSpacing;
            }

            return h;
        }
        
        private ReorderableList GetOrCreateList(SerializedProperty property, SerializedProperty itemsProp)
        {
            var key = (property.serializedObject.targetObject.GetInstanceID(), property.propertyPath);
            if (!_cache.TryGetValue(key, out var list))
                _cache[key] = list = BuildList(itemsProp);

            list.serializedProperty = itemsProp;
            return list;
        }

        private static ReorderableList BuildList(SerializedProperty itemsProp)
        {
            var rl = new ReorderableList(
                itemsProp.serializedObject, itemsProp,
                draggable: true, displayHeader: false,
                displayAddButton: true, displayRemoveButton: true);

            rl.elementHeightCallback = index =>
            {
                if (index >= rl.serializedProperty.arraySize) return EditorGUIUtility.singleLineHeight;
                return EditorGUI.GetPropertyHeight(rl.serializedProperty.GetArrayElementAtIndex(index), true) + 4f;
            };

            rl.drawElementCallback = (rect, index, _, _) =>
            {
                if (index >= rl.serializedProperty.arraySize) return;
                const float LeftPad = 12f;
                var elem = rl.serializedProperty.GetArrayElementAtIndex(index);
                EditorGUI.PropertyField(
                    new Rect(rect.x + LeftPad, rect.y + 2f, rect.width - LeftPad, rect.height - 4f),
                    elem, GUIContent.none, true);
            };

            rl.onAddCallback = list =>
            {
                var arr = list.serializedProperty;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                var elem = arr.GetArrayElementAtIndex(arr.arraySize - 1);

                var valueProp = elem.FindPropertyRelative("Value");
                if (valueProp != null) valueProp.managedReferenceValue = null;
                var typeId = elem.FindPropertyRelative("TypeId");
                if (typeId != null) typeId.stringValue = string.Empty;
                var dispName = elem.FindPropertyRelative("TypeDisplayName");
                if (dispName != null) dispName.stringValue = string.Empty;
                elem.FindPropertyRelative("ObjectRefs")?.ClearArray();
                elem.FindPropertyRelative("ObjectRefPaths")?.ClearArray();
                var valData = elem.FindPropertyRelative("ValuesData");
                if (valData != null) valData.stringValue = string.Empty;

                arr.serializedObject.ApplyModifiedProperties();
            };

            return rl;
        }

        private static bool HasBrokenRefs(SerializedProperty itemsProp)
        {
            for (int i = 0; i < itemsProp.arraySize; i++)
            {
                var elem = itemsProp.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(elem.FindPropertyRelative("TypeId")?.stringValue)
                    && elem.FindPropertyRelative("Value")?.managedReferenceValue == null)
                    return true;
            }
            return false;
        }

    }
}
#endif