#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Shared editor helpers behind the StableRef inspector and its tool windows: label building, the styles the
    /// windows draw with, and jumping to the script that declares a value type.
    /// </summary>
    /// <remarks>
    /// Public so an inspector integration can reuse them rather than reimplement them — in particular
    /// <see cref="PingScript"/>, which every StableRef field offers as a button next to its type selector.
    /// </remarks>
    public static class StableRefEditorUtility
    {
        public const float ArrowW = 14f;

        public const string ValueLabelPrefix = "SR: ";

        /// <summary>
        /// <see cref="SerializedProperty.managedReferenceId"/> of a null managed reference
        /// (<c>ManagedReferenceUtility.RefIdNull</c>).
        /// </summary>
        internal const long ManagedRefIdNull = -2;

        public static readonly Color SelectionColor = new Color(0.17f, 0.44f, 0.75f, 0.8f);
        public static readonly Color SelectionTextColor = Color.white;

        private static readonly Dictionary<(string, bool), GUIContent> _iconCache = new();

        /// <summary>
        /// Skin-correct editor icon. <paramref name="name"/> may be given with or without the <c>d_</c>
        /// (dark-skin) prefix: the variant matching the current skin is tried first, the other one second,
        /// then <paramref name="fallbackText"/>.
        /// </summary>
        /// <remarks>
        /// The prefix must be chosen from the skin, not taken from the caller: <c>d_</c> textures also load on
        /// the light skin, where their light-gray art is nearly invisible on light buttons.
        /// </remarks>
        public static GUIContent Icon(string name, string fallbackText = "")
        {
            bool pro = EditorGUIUtility.isProSkin;
            var key = (name, pro);
            if (_iconCache.TryGetValue(key, out var cached)) return cached;

            string light = name.StartsWith("d_", StringComparison.Ordinal) ? name.Substring(2) : name;
            string dark = "d_" + light;

            var content = TryIcon(pro ? dark : light) ?? TryIcon(pro ? light : dark) ?? new GUIContent(fallbackText);
            return _iconCache[key] = content;
        }

        private static GUIContent TryIcon(string name)
        {
            var content = EditorGUIUtility.IconContent(name);
            return content?.image != null ? content : null;
        }

        public static Texture GoIcon => Icon("GameObject Icon").image;

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

        /// <summary>Session-stable numeric id of an object (InstanceID on Unity &lt; 6000.5, EntityId on 6000.5+).</summary>
        public static long GetStableId(UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return unchecked((long)EntityId.ToULong(obj.GetEntityId()));
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>Numeric id of a property's object reference, or 0 when it holds none.</summary>
        public static long GetObjectReferenceId(SerializedProperty property)
        {
#if UNITY_6000_5_OR_NEWER
            return unchecked((long)EntityId.ToULong(property.objectReferenceEntityIdValue));
#else
            return property.objectReferenceInstanceIDValue;
#endif
        }

        /// <summary>Resolves an id from <see cref="GetObjectReferenceId"/> back to its object (null if gone).</summary>
        public static UnityEngine.Object IdToObject(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong(unchecked((ulong)id)));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }
    }
}
#endif