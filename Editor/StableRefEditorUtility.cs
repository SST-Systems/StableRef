#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        /// <summary>
        /// The managed-reference property holding the value of <paramref name="property"/>: the <c>Value</c> of a
        /// <see cref="StableRef{T}"/> entry, or the property itself for a plain <c>[SerializeReference]</c> field;
        /// <see langword="null"/> for anything else.
        /// </summary>
        /// <remarks>
        /// For inspector code that finds a field by name and reads its managed reference — it keeps working when the
        /// field changes from <c>[SerializeReference] T</c> to <see cref="StableRef{T}"/>. Write values through
        /// <see cref="StableRefEntry"/> (<c>Sync</c> after assigning, <c>Clear</c> to empty), not through this property
        /// alone.
        /// </remarks>
        public static SerializedProperty GetValueProperty(SerializedProperty property)
        {
            if (property == null) return null;
            if (property.propertyType == SerializedPropertyType.ManagedReference) return property;
            if (property.propertyType != SerializedPropertyType.Generic) return null;

            var value = property.FindPropertyRelative(StableRefEntry.ValueFieldName);
            if (value == null || value.propertyType != SerializedPropertyType.ManagedReference) return null;
            return property.FindPropertyRelative(StableRefEntry.TypeIdFieldName) != null ? value : null;
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

        /// <summary>
        /// One source visited by <see cref="ScanProject"/>: an asset file under <c>Assets/</c> or an open scene, with
        /// the objects in it that can hold serialized StableRef entries.
        /// </summary>
        internal struct ScanSource
        {
            /// <summary>Asset path, or the scene's path (empty for an unsaved scene).</summary>
            public string Path;
            public bool IsScene;
            /// <summary>The scene; valid only when <see cref="IsScene"/>.</summary>
            public Scene Scene;
            /// <summary>See <see cref="LoadScanTargets"/>; for a scene, every component in it.</summary>
            public List<UnityEngine.Object> Targets;
        }

        /// <summary>
        /// The scan shared by the project-wide tools: every ScriptableObject asset (sub-assets included) and prefab
        /// under <c>Assets/</c>, then every loaded open scene, under a cancelable progress bar (canceling skips the
        /// remaining assets; the open scenes are still visited). Never opens, closes or saves anything and doesn't
        /// unload what it loaded — call <c>EditorUtility.UnloadUnusedAssetsImmediate</c> once done with the targets.
        /// </summary>
        internal static void ScanProject(string progressTitle, Action<ScanSource> visit)
        {
            var guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })
                .Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                .Distinct()
                .ToArray();

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (EditorUtility.DisplayCancelableProgressBar(progressTitle, path, (float)i / guids.Length))
                        break;

                    var targets = LoadScanTargets(path);
                    if (targets.Count > 0) visit(new ScanSource { Path = path, Targets = targets });
                }

                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;

                    var targets = new List<UnityEngine.Object>();
                    foreach (var root in scene.GetRootGameObjects())
                        AddComponents(root, targets);
                    visit(new ScanSource { Path = scene.path, IsScene = true, Scene = scene, Targets = targets });
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        /// <summary>
        /// The objects of the asset file at <paramref name="path"/> that can hold StableRef entries: every component
        /// of a prefab (missing scripts skipped), or the ScriptableObjects of any other file — the main asset first,
        /// then the sub-assets stored in the same file (graph nodes, Timeline clips, <c>StateMachineBehaviour</c>s).
        /// </summary>
        internal static List<UnityEngine.Object> LoadScanTargets(string path)
        {
            var targets = new List<UnityEngine.Object>();
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) return targets;

            if (main is GameObject go)
            {
                AddComponents(go, targets);
                return targets;
            }

            if (main is ScriptableObject) targets.Add(main);
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj is ScriptableObject && obj != main)
                    targets.Add(obj);
            return targets;
        }

        /// <summary>
        /// Label of a scanned object in the tool windows: its type name, prefixed by its own name for a ScriptableObject
        /// sub-asset — a file can hold many sub-assets of one type.
        /// </summary>
        internal static string ScanTargetLabel(UnityEngine.Object target)
        {
            string typeName = target.GetType().Name;
            return target is ScriptableObject && AssetDatabase.IsSubAsset(target) && !string.IsNullOrEmpty(target.name)
                ? $"{target.name} ({typeName})"
                : typeName;
        }

        private static void AddComponents(GameObject root, List<UnityEngine.Object> targets)
        {
            foreach (var comp in root.GetComponentsInChildren<Component>(true))
                if (comp != null) targets.Add(comp);
        }
    }
}
#endif