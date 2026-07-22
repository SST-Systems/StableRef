#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
        
        private static readonly Dictionary<string, bool> _hadValue = new();

        internal static void ClearHadValue() => _hadValue.Clear();

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
            string pvKey = $"{property.serializedObject.targetObject.GetInstanceID()}:{property.propertyPath}";

            if (hasValue)
            {
                _hadValue[pvKey] = true;
            }
            else if (hasTypeId && _hadValue.TryGetValue(pvKey, out var hv) && hv)
            {
                _hadValue.Remove(pvKey);
                typeIdProp.stringValue = string.Empty;
                property.FindPropertyRelative("TypeDisplayName").stringValue = string.Empty;
                hasTypeId = false;
            }

            float h = EditorGUIUtility.singleLineHeight;

            if (hasValue)
            {
                SyncTypeId(property, valueProp, typeIdProp);

                const float BtnW = 22f;
                var fieldRect = new Rect(position.x, position.y, position.width - BtnW - 2f, position.height);
                var btnRect   = new Rect(position.xMax - BtnW, position.y, BtnW, h);

                EditorGUI.BeginChangeCheck();
                DrawSelector(fieldRect, valueProp, label);
                if (EditorGUI.EndChangeCheck())
                    SnapshotBackup(property, valueProp);

                bool prev = GUI.enabled;
                GUI.enabled = true;
                if (GUI.Button(btnRect, EditorGUIUtility.IconContent("d_Search Icon"), PingStyle))
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

                var displayNameProp = property.FindPropertyRelative("TypeDisplayName");
                string displayName = displayNameProp?.stringValue;
                if (string.IsNullOrEmpty(displayName)) displayName = "?";

                var recoveredType = StableRefTypeRegistry.GetType(typeIdProp.stringValue);
                string newName = recoveredType != null ? StableRefGenericUtils.DisplayName(recoveredType) : "None";

                BrokenLabelOverride = new GUIContent($"Missing ({displayName}) → {newName}");
                BrokenColorOverride = new Color(0.65f, 0.65f, 0.65f);

                using (new EditorGUI.DisabledScope(true))
                    DrawSelector(controlRect, valueProp, GUIContent.none);

                bool prevEnabled = GUI.enabled;
                GUI.enabled = true;
                if (GUI.Button(btnRect, EditorGUIUtility.IconContent("console.warnicon.sml"), PingStyle))
                    DoRecreate(property, valueProp, typeIdProp, recoveredType);
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
                EditorGUI.BeginChangeCheck();
                bool expanded = EditorGUI.Foldout(
                    new Rect(foldoutX, controlLine.y, FoldoutW, controlLine.height), property.isExpanded, GUIContent.none, true);
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

            GUIContent buttonLabel = BrokenLabelOverride ?? new GUIContent(GetCurrentLabel(property));
            BrokenLabelOverride = null;

            var prevColor = GUI.contentColor;
            if (BrokenColorOverride.HasValue) GUI.contentColor = BrokenColorOverride.Value;
            BrokenColorOverride = null;

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

        private static void SyncTypeId(
            SerializedProperty wrapperProp,
            SerializedProperty valueProp,
            SerializedProperty typeIdProp)
        {
            var type = valueProp.managedReferenceValue?.GetType();
            if (type == null) return;

            var id = StableRefTypeRegistry.GetOrAssignId(type);
            if (id == null) return;

            var displayName = StableRefGenericUtils.DisplayName(type);
            var dispProp = wrapperProp.FindPropertyRelative("TypeDisplayName");
            if (typeIdProp.stringValue == id && dispProp?.stringValue == displayName) return;

            typeIdProp.stringValue = id;
            if (dispProp != null) dispProp.stringValue = displayName;
            SnapshotBackup(wrapperProp, valueProp);
        }

        private static void DoRecreate(
            SerializedProperty wrapperProp,
            SerializedProperty valueProp,
            SerializedProperty typeIdProp,
            Type type)
        {
            var target = wrapperProp.serializedObject.targetObject;
            var wrapperPath = wrapperProp.propertyPath;
            var assetPath = AssetDatabase.GetAssetPath(target);

            EditorApplication.delayCall += () =>
            {
                var so = new SerializedObject(target);
                so.Update();

                var wp = so.FindProperty(wrapperPath);
                if (wp == null) return;
                var vp = wp.FindPropertyRelative("Value");
                if (vp == null) return;

                if (type != null)
                {
                    vp.managedReferenceValue = Activator.CreateInstance(type);
                    RestoreBackup(wp, vp);
                }
                else
                {
                    vp.managedReferenceValue = null;
                    wp.FindPropertyRelative("TypeId").stringValue = string.Empty;
                    wp.FindPropertyRelative("TypeDisplayName").stringValue = string.Empty;
                    wp.FindPropertyRelative("ObjectRefs")?.ClearArray();
                    wp.FindPropertyRelative("ObjectRefPaths")?.ClearArray();
                    var valData = wp.FindPropertyRelative("ValuesData");
                    if (valData != null) valData.stringValue = string.Empty;
                }

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);

                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }

                SerializationUtility.ClearAllManagedReferencesWithMissingTypes(target);
                AssetDatabase.Refresh();
            };
        }

        internal static void SnapshotBackup(SerializedProperty wrapperProp, SerializedProperty valueProp)
        {
            if (valueProp.managedReferenceValue == null) return;

            var objectRefsProp = wrapperProp.FindPropertyRelative("ObjectRefs");
            var objectRefPathsProp = wrapperProp.FindPropertyRelative("ObjectRefPaths");
            var valuesDataProp = wrapperProp.FindPropertyRelative("ValuesData");

            objectRefsProp.ClearArray();
            objectRefPathsProp.ClearArray();

            var lines = new List<string>();
            string basePath = valueProp.propertyPath;
            var child = valueProp.Copy();
            var end = valueProp.GetEndProperty();

            if (!child.NextVisible(true)) goto done;

            while (!SerializedProperty.EqualContents(child, end))
            {
                if (!child.propertyPath.StartsWith(basePath, StringComparison.Ordinal)) break;

                string relPath = child.propertyPath.Substring(basePath.Length).TrimStart('.');

                switch (child.propertyType)
                {
                    case SerializedPropertyType.ObjectReference:
                        int objIdx = objectRefsProp.arraySize;
                        objectRefsProp.arraySize++;
                        objectRefsProp.GetArrayElementAtIndex(objIdx).objectReferenceValue = child.objectReferenceValue;
                        objectRefPathsProp.arraySize++;
                        objectRefPathsProp.GetArrayElementAtIndex(objIdx).stringValue = relPath;
                        break;
                    case SerializedPropertyType.Integer:
                        lines.Add(Encode(relPath, child.intValue.ToString())); break;
                    case SerializedPropertyType.Boolean:
                        lines.Add(Encode(relPath, child.boolValue ? "1" : "0")); break;
                    case SerializedPropertyType.Float:
                        lines.Add(Encode(relPath, child.floatValue.ToString("R"))); break;
                    case SerializedPropertyType.String:
                        if (!string.IsNullOrEmpty(child.stringValue))
                            lines.Add(Encode(relPath, child.stringValue));
                        break;
                    case SerializedPropertyType.Enum:
                        lines.Add(Encode(relPath, child.enumValueIndex.ToString())); break;
                    case SerializedPropertyType.Color:
                    {
                        var c = child.colorValue;
                        lines.Add(Encode(relPath, $"{c.r:R}|{c.g:R}|{c.b:R}|{c.a:R}"));
                        break;
                    }
                    case SerializedPropertyType.Vector2:
                    {
                        var v = child.vector2Value;
                        lines.Add(Encode(relPath, $"{v.x:R}|{v.y:R}"));
                        break;
                    }
                    case SerializedPropertyType.Vector3:
                    {
                        var v = child.vector3Value;
                        lines.Add(Encode(relPath, $"{v.x:R}|{v.y:R}|{v.z:R}"));
                        break;
                    }
                    case SerializedPropertyType.Vector4:
                    {
                        var v = child.vector4Value;
                        lines.Add(Encode(relPath, $"{v.x:R}|{v.y:R}|{v.z:R}|{v.w:R}"));
                        break;
                    }
                    case SerializedPropertyType.Vector2Int:
                    {
                        var v = child.vector2IntValue;
                        lines.Add(Encode(relPath, $"{v.x}|{v.y}"));
                        break;
                    }
                    case SerializedPropertyType.Vector3Int:
                    {
                        var v = child.vector3IntValue;
                        lines.Add(Encode(relPath, $"{v.x}|{v.y}|{v.z}"));
                        break;
                    }
                    case SerializedPropertyType.Quaternion:
                    {
                        var q = child.quaternionValue;
                        lines.Add(Encode(relPath, $"{q.x:R}|{q.y:R}|{q.z:R}|{q.w:R}"));
                        break;
                    }
                    case SerializedPropertyType.Rect:
                    {
                        var r = child.rectValue;
                        lines.Add(Encode(relPath, $"{r.x:R}|{r.y:R}|{r.width:R}|{r.height:R}"));
                        break;
                    }
                    case SerializedPropertyType.Bounds:
                    {
                        var b = child.boundsValue;
                        lines.Add(Encode(relPath, $"{b.center.x:R}|{b.center.y:R}|{b.center.z:R}|{b.size.x:R}|{b.size.y:R}|{b.size.z:R}"));
                        break;
                    }
                    case SerializedPropertyType.LayerMask:
                        lines.Add(Encode(relPath, child.intValue.ToString()));
                        break;
                }

                if (!child.NextVisible(false)) break;
            }

            done:
            valuesDataProp.stringValue = string.Join("\n", lines);
        }

        internal static void RestoreBackup(SerializedProperty wrapperProp, SerializedProperty valueProp)
        {
            var objectRefsProp = wrapperProp.FindPropertyRelative("ObjectRefs");
            var objectRefPathsProp = wrapperProp.FindPropertyRelative("ObjectRefPaths");
            var valuesDataProp = wrapperProp.FindPropertyRelative("ValuesData");

            for (int i = 0; i < objectRefPathsProp.arraySize; i++)
            {
                string relPath = objectRefPathsProp.GetArrayElementAtIndex(i).stringValue;
                var obj = objectRefsProp.GetArrayElementAtIndex(i).objectReferenceValue;
                var targetProp = valueProp.FindPropertyRelative(relPath);
                if (targetProp != null && targetProp.propertyType == SerializedPropertyType.ObjectReference)
                    targetProp.objectReferenceValue = obj;
            }

            string data = valuesDataProp.stringValue;
            if (string.IsNullOrEmpty(data)) return;

            foreach (var line in data.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                int sep = line.IndexOf('=');
                if (sep < 0) continue;

                string relPath = Decode(line.Substring(0, sep));
                string val = Decode(line.Substring(sep + 1));
                var targetProp = valueProp.FindPropertyRelative(relPath);
                if (targetProp == null) continue;

                try
                {
                    switch (targetProp.propertyType)
                    {
                        case SerializedPropertyType.Integer:
                            if (int.TryParse(val, out int iv)) targetProp.intValue = iv; break;
                        case SerializedPropertyType.Boolean:
                            targetProp.boolValue = val == "1"; break;
                        case SerializedPropertyType.Float:
                            if (float.TryParse(val, out float fv)) targetProp.floatValue = fv; break;
                        case SerializedPropertyType.String:
                            targetProp.stringValue = val; break;
                        case SerializedPropertyType.Enum:
                            if (int.TryParse(val, out int ev)) targetProp.enumValueIndex = ev; break;
                        case SerializedPropertyType.Color:
                        {
                            var p = val.Split('|');
                            if (p.Length == 4
                                && float.TryParse(p[0], out float r) && float.TryParse(p[1], out float g)
                                && float.TryParse(p[2], out float b) && float.TryParse(p[3], out float a))
                                targetProp.colorValue = new Color(r, g, b, a);
                            break;
                        }
                        case SerializedPropertyType.Vector2:
                        {
                            var p = val.Split('|');
                            if (p.Length == 2
                                && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y))
                                targetProp.vector2Value = new Vector2(x, y);
                            break;
                        }
                        case SerializedPropertyType.Vector3:
                        {
                            var p = val.Split('|');
                            if (p.Length == 3
                                && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y)
                                && float.TryParse(p[2], out float z))
                                targetProp.vector3Value = new Vector3(x, y, z);
                            break;
                        }
                        case SerializedPropertyType.Vector4:
                        {
                            var p = val.Split('|');
                            if (p.Length == 4
                                && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y)
                                && float.TryParse(p[2], out float z) && float.TryParse(p[3], out float w))
                                targetProp.vector4Value = new Vector4(x, y, z, w);
                            break;
                        }
                        case SerializedPropertyType.Vector2Int:
                        {
                            var p = val.Split('|');
                            if (p.Length == 2
                                && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y))
                                targetProp.vector2IntValue = new Vector2Int(x, y);
                            break;
                        }
                        case SerializedPropertyType.Vector3Int:
                        {
                            var p = val.Split('|');
                            if (p.Length == 3
                                && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y)
                                && int.TryParse(p[2], out int z))
                                targetProp.vector3IntValue = new Vector3Int(x, y, z);
                            break;
                        }
                        case SerializedPropertyType.Quaternion:
                        {
                            var p = val.Split('|');
                            if (p.Length == 4
                                && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y)
                                && float.TryParse(p[2], out float z) && float.TryParse(p[3], out float w))
                                targetProp.quaternionValue = new Quaternion(x, y, z, w);
                            break;
                        }
                        case SerializedPropertyType.Rect:
                        {
                            var p = val.Split('|');
                            if (p.Length == 4
                                && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y)
                                && float.TryParse(p[2], out float w) && float.TryParse(p[3], out float h))
                                targetProp.rectValue = new Rect(x, y, w, h);
                            break;
                        }
                        case SerializedPropertyType.Bounds:
                        {
                            var p = val.Split('|');
                            if (p.Length == 6
                                && float.TryParse(p[0], out float cx) && float.TryParse(p[1], out float cy)
                                && float.TryParse(p[2], out float cz) && float.TryParse(p[3], out float sx)
                                && float.TryParse(p[4], out float sy) && float.TryParse(p[5], out float sz))
                                targetProp.boundsValue = new Bounds(new Vector3(cx, cy, cz), new Vector3(sx, sy, sz));
                            break;
                        }
                        case SerializedPropertyType.LayerMask:
                            if (int.TryParse(val, out int lm)) targetProp.intValue = lm;
                            break;
                    }
                }
                catch { /* ignore per-field restore errors */ }
            }
        }

        private static string Encode(string key, string value)
            => $"{EscapeField(key)}={EscapeField(value)}";

        private static string EscapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("%", "%25").Replace("\n", "%0A").Replace("=", "%3D");
        }

        private static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("%3D", "=").Replace("%0A", "\n").Replace("%25", "%");
        }
    }
}
#endif