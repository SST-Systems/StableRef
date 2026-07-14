#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    internal static class StableRefEditorUtility
    {
        public const float ArrowW = 14f;

        public static readonly Color SelectionColor = new Color(0.17f, 0.44f, 0.75f, 0.8f);
        public static readonly Color SelectionTextColor = Color.white;

        private static Texture _goIcon;
        public static Texture GoIcon => _goIcon ??= EditorGUIUtility.IconContent("d_GameObject Icon").image;

        public static GUIStyle FoldoutStyle { get; private set; }
        public static GUIStyle HeaderStyle { get; private set; }

        private static bool _lastIsProSkin;
        private static bool _stylesReady;

        public static void EnsureStyles()
        {
            if (_stylesReady && _lastIsProSkin == EditorGUIUtility.isProSkin) return;
            _stylesReady = true;
            _lastIsProSkin = EditorGUIUtility.isProSkin;

            FoldoutStyle = new GUIStyle(EditorStyles.foldout);
            OverrideTextColors(FoldoutStyle, EditorStyles.foldout.normal.textColor);

            HeaderStyle = new GUIStyle(EditorStyles.foldoutHeader);
            OverrideTextColors(HeaderStyle, EditorStyles.foldoutHeader.normal.textColor);
        }

        public static void OverrideTextColors(GUIStyle style, Color c)
        {
            style.onNormal.textColor = c;
            style.focused.textColor = c;
            style.onFocused.textColor = c;
            style.active.textColor = c;
            style.onActive.textColor = c;
        }

        public static string BuildFieldDisplayPath(SerializedObject so, string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;

            var parts = propertyPath.Split('.');
            var display = new List<string>(parts.Length);
            string accumulated = "";

            for (int i = 0; i < parts.Length; i++)
            {
                accumulated = i == 0 ? parts[i] : accumulated + "." + parts[i];
                var prop = so.FindProperty(accumulated);
                if (prop != null) display.Add(prop.displayName);
            }

            return display.Count > 0 ? string.Join("/", display) : null;
        }

        public static string StripStableRefListArraySuffix(string propertyPath)
        {
            const string arrayMarker = ".Array.data[";
            const string itemsSuffix = "._items";

            int arrayIdx = propertyPath.IndexOf(arrayMarker, StringComparison.Ordinal);
            string path = arrayIdx >= 0 ? propertyPath.Substring(0, arrayIdx) : propertyPath;

            if (path.EndsWith(itemsSuffix, StringComparison.Ordinal))
                path = path.Substring(0, path.Length - itemsSuffix.Length);

            return path;
        }

        public static void PingScript(Type type)
        {
            if (type == null) return;
            foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {type.Name}"))
            {
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (ms != null && ms.GetClass() == type)
                {
                    EditorGUIUtility.PingObject(ms);
                    return;
                }
            }
        }
    }
}
#endif