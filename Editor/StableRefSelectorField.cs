#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// The selector field itself — popup button with the current type, foldout and the value's children —
    /// drawn for a <c>[SerializeReference]</c> property. It writes nothing on its own: picking a type goes
    /// through <see cref="StableRefSelectorWindow"/>, the context menu through <see cref="StableRefContextMenu"/>.
    /// </summary>
    /// <remarks>
    /// Shared by every selector drawer so they differ only in what surrounds the field:
    /// <see cref="StableRefHandler"/> (<see cref="StableRef{T}"/> and <see cref="StableRefList{T}"/> elements)
    /// adds id / snapshot sync, the missing-entry fix button and the find-script button;
    /// <see cref="RefSelectorDrawer"/> (<see cref="RefSelectorAttribute"/>) draws the bare field.
    /// </remarks>
    internal static class StableRefSelectorField
    {
        internal struct Options
        {
            /// <summary>Offer only types that have a stable id (StableRef fields).</summary>
            public bool RequireStableId;
            /// <summary>The selected targets hold different types: show <c>—</c> and no children.</summary>
            public bool Mixed;
            /// <summary>Button text replacing the current type name, e.g. a missing-type label.</summary>
            public GUIContent LabelOverride;
            /// <summary>Content color for <see cref="LabelOverride"/>.</summary>
            public Color? LabelColor;
        }

        private const float FoldoutW = 4f;

        private static readonly GUIContent MixedLabel = new("—", "The selected objects hold different types.");
        private static readonly Dictionary<(Type, bool), string> _labelCache = new();

        internal static void Draw(Rect position, SerializedProperty property, GUIContent label, Options options)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                EditorGUI.HelpBox(position, "The type selector works only with [SerializeReference].", MessageType.Error);
                return;
            }

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

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

            bool hasChildren = !options.Mixed && property.managedReferenceValue != null && HasVisibleChildren(property);
            if (hasChildren)
                DrawFoldout(property, new Rect(hasLabel ? controlLine.x - 4f : controlLine.x, controlLine.y,
                    FoldoutW, controlLine.height));

            var btnRect = new Rect(btnX, controlLine.y, btnW, controlLine.height);
            var buttonLabel = options.Mixed ? MixedLabel : options.LabelOverride ?? new GUIContent(GetCurrentLabel(property));

            var prevColor = GUI.contentColor;
            if (!options.Mixed && options.LabelOverride != null && options.LabelColor.HasValue)
                GUI.contentColor = options.LabelColor.Value;
            bool clicked = GUI.Button(btnRect, buttonLabel, EditorStyles.popup);
            GUI.contentColor = prevColor;

            if (clicked)
                StableRefSelectorWindow.Show(btnRect, property,
                    StableRefPropertyUtils.GetEntries(property, options.RequireStableId));

            if (hasChildren && property.isExpanded)
            {
                float yOff = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                EditorGUI.indentLevel++;
                DrawChildren(new Rect(position.x, position.y + yOff, position.width, position.height - yOff), property);
                EditorGUI.indentLevel--;
            }
        }

        internal static float GetHeight(SerializedProperty property, bool mixed)
        {
            float h = EditorGUIUtility.singleLineHeight;
            if (mixed || property.propertyType != SerializedPropertyType.ManagedReference) return h;
            if (property.managedReferenceValue == null || !property.isExpanded) return h;

            var custom = StableRefDrawing.ChildrenDrawer;
            if (custom != null)
            {
                if (!HasVisibleChildren(property)) return h;
                int prevIndent = EditorGUI.indentLevel;
                EditorGUI.indentLevel++;
                float childrenH = custom.GetChildrenHeight(property);
                EditorGUI.indentLevel = prevIndent;
                return h + EditorGUIUtility.standardVerticalSpacing + childrenH;
            }

            var child = property.Copy();
            var end = property.GetEndProperty();
            if (child.NextVisible(true))
                while (!SerializedProperty.EqualContents(child, end))
                {
                    h += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
                    if (!child.NextVisible(false)) break;
                }

            return h;
        }

        private static void DrawFoldout(SerializedProperty property, Rect rect)
        {
            int prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUI.BeginChangeCheck();
            bool expanded = EditorGUI.Foldout(rect, property.isExpanded, GUIContent.none, true);
            EditorGUI.indentLevel = prevIndent;
            if (!EditorGUI.EndChangeCheck()) return;

            if (Event.current.alt) StableRefPropertyUtils.SetExpandedRecursive(property, expanded);
            else property.isExpanded = expanded;
            StableRefListDrawer.InvalidateCache();
        }

        private static bool HasVisibleChildren(SerializedProperty property)
        {
            var child = property.Copy();
            var end = property.GetEndProperty();
            return child.NextVisible(true) && !SerializedProperty.EqualContents(child, end);
        }

        private static void DrawChildren(Rect rect, SerializedProperty property)
        {
            var custom = StableRefDrawing.ChildrenDrawer;
            if (custom != null)
            {
                custom.DrawChildren(rect, property);
                return;
            }

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

        private static string GetCurrentLabel(SerializedProperty property)
        {
            var value = property.managedReferenceValue;
            if (value == null) return "None";

            var key = (value.GetType(), StableRefSelectorWindow.ShowCategoryInLabel);
            if (_labelCache.TryGetValue(key, out var cached)) return cached;

            var meta = StableRefTypeMetadata.Get(key.Item1);
            string text = key.Item2 && !string.IsNullOrEmpty(meta.Category)
                ? $"{meta.Category}/{meta.DisplayName}"
                : meta.DisplayName;
            return _labelCache[key] = text;
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
    }
}
#endif
