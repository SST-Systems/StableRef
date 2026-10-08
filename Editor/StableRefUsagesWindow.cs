#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using NodeKind = SST.StableRef.StableRefResultTree.NodeKind;

namespace SST.StableRef
{
    public sealed class StableRefUsagesWindow : EditorWindow
    {
        private const float FindW = 90f;
        private const float SearchW = 160f;
        private const float ClearBtnW = 18f;
        private const float DefaultW = 360f;
        private const float DefaultH = 560f;
        private const string ProgressTitle = "StableRef Usages";

        private sealed class Node : StableRefResultTree.Node
        {
            public Type ConcreteType;
            public string PrefabAssetPath;
            public string TransformPath;
            public string ScenePath;
        }

        private List<StableRefResultTree.Node> _roots;
        private StableRefResultTree _tree;
        private string _searchText = "";
        /// <summary>A type whose declaration index was still being built when the filter last ran.</summary>
        private Type _pendingSearchType;

        private static readonly Dictionary<Type, Texture> _iconCache = new();

        [MenuItem("Tools/StableRef/Find Usages", priority = 100)]
        public static void Open()
        {
            var win = GetWindow<StableRefUsagesWindow>("StableRef Usages");
            win.minSize = new Vector2(DefaultW, 160f);
            if (win.position.width < DefaultW || win.position.height < DefaultH)
                win.position = new Rect(win.position.x, win.position.y, DefaultW, DefaultH);
        }

        private static readonly Regex ClassDeclarationRegex = new(@"\b(?:class|struct|record)\s+\w", RegexOptions.Compiled);

        /// <summary>
        /// Searches the usages of every type declared in the selected script — the search matches the declaring script's
        /// name too, so a file with several classes (or a class named unlike its file) is covered as a whole.
        /// </summary>
        [MenuItem("Assets/Find StableRef Usages", priority = 27)]
        private static void OpenFromAsset()
        {
            var ms = Selection.activeObject as MonoScript;
            if (ms == null) return;

            // The filter matches script names through the declaration index: build it during the scan and wait for it
            // afterwards, so the result doesn't fill in a moment after the scan.
            StableRefEditorUtility.RequestDeclaringIndex(ms, wait: false);

            var win = GetWindow<StableRefUsagesWindow>("StableRef Usages");
            win.minSize = new Vector2(DefaultW, 160f);
            win._searchText = ms.name;
            win._tree.Filter = ms.name;
            win.RunSearch();

            StableRefEditorUtility.RequestDeclaringIndex(ms, wait: true);
        }

        /// <summary>A text check only — the menu is validated whenever it opens, so no type is resolved here.</summary>
        [MenuItem("Assets/Find StableRef Usages", validate = true, priority = 27)]
        private static bool OpenFromAssetValidate()
            => Selection.activeObject is MonoScript ms && ClassDeclarationRegex.IsMatch(ms.text);

        private void OnEnable()
        {
            _roots = null;
            _searchText = "";
            _tree = new StableRefResultTree
            {
                Clicked = OnNodeClicked,
                ResolveIcon = n => ((Node)n).ConcreteType is { } type ? GetTypeIcon(type) : n.Icon,
                ExtraSearchText = n => GetScriptSearchName((Node)n),
                EmptyGroupText = "No usages found"
            };
        }

        /// <summary>
        /// Lower-case name of the script declaring the type of a value <paramref name="node"/>, shared by all values of
        /// that type; null for other rows. It comes from the declaration index, built in the background: until it is
        /// ready only labels match and the filter runs again once it is (see <see cref="Update"/>), so typing never
        /// waits for the index.
        /// </summary>
        private string GetScriptSearchName(Node node)
        {
            var type = node.ConcreteType;
            if (type == null) return null;
            if (StableRefEditorUtility.TryGetDeclaringFileSearchName(type, out var name)) return name;

            _pendingSearchType ??= type;
            return null;
        }

        private void Update()
        {
            if (_tree.ApplyTypedFilter()) Repaint();

            if (_pendingSearchType == null || !StableRefEditorUtility.TryGetDeclaringFileName(_pendingSearchType, out _)) return;
            _pendingSearchType = null;
            _tree.RefreshFilter();
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (_roots == null)
            {
                EditorGUILayout.HelpBox(
                    "Press \"Find Usages\" to search across prefabs, active scenes and scriptable objects.",
                    MessageType.Info);
                return;
            }

            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _tree.OnGUI(rect);
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                _searchText = GUILayout.TextField(_searchText, EditorStyles.toolbarSearchField,
                    GUILayout.Width(SearchW));
                if (EditorGUI.EndChangeCheck())
                {
                    _tree.SetFilterDelayed(_searchText);
                    Repaint();
                }

                if (!string.IsNullOrEmpty(_searchText)
                    && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(ClearBtnW)))
                {
                    _searchText = "";
                    _tree.Filter = "";
                    GUI.FocusControl(null);
                    Repaint();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Find Usages", EditorStyles.toolbarButton, GUILayout.Width(FindW)))
                    RunSearch();
            }
        }

        private static void OnNodeClicked(StableRefResultTree.Node n, int clickCount)
        {
            var node = (Node)n;
            if (node.ConcreteType != null && node.Children.Count == 0)
            {
                if (clickCount == 2 && StableRefEditorUtility.FindScript(node.ConcreteType) is { } script)
                    AssetDatabase.OpenAsset(script);
                else
                    StableRefEditorUtility.PingScript(node.ConcreteType);
                return;
            }

            if (node.PingTarget != null)
                EditorGUIUtility.PingObject(ResolvePingTarget(node));
        }

        private void RunSearch()
        {
            var prefabGroup = new Node { Kind = NodeKind.Group, Label = "Prefabs", Expanded = true };
            var sceneGroup = new Node { Kind = NodeKind.Group, Label = "Active Scenes", Expanded = true };
            var soGroup = new Node { Kind = NodeKind.Group, Label = "Scriptable Objects", Expanded = true };

            try
            {
                ScanPrefabs(prefabGroup);
                ScanScenes(sceneGroup);
                ScanScriptableObjects(soGroup);
            }
            finally { EditorUtility.ClearProgressBar(); }

            _roots = new List<StableRefResultTree.Node> { prefabGroup, sceneGroup, soGroup };
            _tree.SetRoots(_roots);
            var roots = _roots;
            EditorApplication.delayCall += () => WarmUpSearchIndex(roots, new HashSet<System.Reflection.Assembly>());

            EditorUtility.UnloadUnusedAssetsImmediate();
            Repaint();
        }

        /// <summary>Starts building the declaration index of every assembly a found value comes from.</summary>
        private static void WarmUpSearchIndex(List<StableRefResultTree.Node> nodes, HashSet<System.Reflection.Assembly> seen)
        {
            foreach (var n in nodes)
            {
                var type = ((Node)n).ConcreteType;
                if (type != null && seen.Add(type.Assembly))
                    StableRefEditorUtility.TryGetDeclaringFileName(type, out _);
                WarmUpSearchIndex(n.Children, seen);
            }
        }

        /// <summary>Asset paths under <c>Assets/</c> matching <paramref name="filter"/> that may hold StableRef entries.</summary>
        private static List<string> FindCandidates(string filter, string what, float progress)
        {
            var paths = AssetDatabase.FindAssets(filter, new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToList();
            EditorUtility.DisplayProgressBar(ProgressTitle, $"Looking for StableRef data in {what}…", progress);
            return StableRefEditorUtility.FilterScanPaths(paths);
        }

        private static void ScanPrefabs(Node group)
        {
            var paths = FindCandidates("t:Prefab", "prefabs", 0f);
            for (int i = 0; i < paths.Count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(ProgressTitle,
                        $"Prefabs ({i + 1} / {paths.Count})", 0.7f * i / paths.Count))
                    break;

                var path = paths[i];
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                var assetNode = new Node
                {
                    Kind = NodeKind.Asset, Label = go.name,
                    Icon = AssetDatabase.GetCachedIcon(path), PingTarget = go
                };
                ScanGameObjectTree(go, assetNode, go);
                if (assetNode.Children.Count > 0) group.Children.Add(assetNode);
            }
        }

        private static void ScanScenes(Node group)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                bool hasPath = !string.IsNullOrEmpty(scene.path);
                var sceneAsset = hasPath
                    ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scene.path)
                    : null;

                var pingOverride = hasPath ? sceneAsset : null;

                var sceneNode = new Node
                {
                    Kind = NodeKind.Asset,
                    Label = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name,
                    Icon = StableRefEditorUtility.Icon("SceneAsset Icon").image,
                    PingTarget = sceneAsset
                };
                foreach (var root in scene.GetRootGameObjects())
                    ScanGameObjectTree(root, sceneNode, null, "", pingOverride);
                if (hasPath) TagScenePath(sceneNode, scene.path);
                if (sceneNode.Children.Count > 0) group.Children.Add(sceneNode);
            }
        }

        private static void ScanScriptableObjects(Node group)
        {
            var paths = FindCandidates("t:ScriptableObject", "scriptable objects", 0.7f);
            for (int i = 0; i < paths.Count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(ProgressTitle,
                        $"Scriptable Objects ({i + 1} / {paths.Count})",
                        0.8f + 0.2f * i / paths.Count))
                    break;

                var path = paths[i];
                if (!path.StartsWith("Assets/")) continue;

                Node assetNode = null;
                foreach (var target in StableRefEditorUtility.LoadScanTargets(path))
                {
                    if (target is not ScriptableObject so) continue;
                    if (!StableRefPropertyUtils.MayContainStableRef(so.GetType())) continue;

                    var compNode = new Node
                    {
                        Kind = NodeKind.Component, Label = StableRefEditorUtility.ScanTargetLabel(so),
                        Icon = ScriptIcon,
                        PingTarget = so
                    };
                    ScanSerializedObject(new SerializedObject(so), compNode, so);
                    if (compNode.Children.Count == 0) continue;

                    assetNode ??= new Node
                    {
                        Kind = NodeKind.Asset, Label = Path.GetFileNameWithoutExtension(path),
                        Icon = AssetDatabase.GetCachedIcon(path), PingTarget = AssetDatabase.LoadMainAssetAtPath(path)
                    };
                    assetNode.Children.Add(compNode);
                }

                if (assetNode != null) group.Children.Add(assetNode);
            }
        }

        private static Texture ScriptIcon => StableRefEditorUtility.Icon("cs Script Icon").image;

        private static void ScanGameObjectTree(
            GameObject go, Node parentNode, GameObject prefabRoot, string parentPath = "",
            UnityEngine.Object pingOverride = null)
        {
            bool isRoot = prefabRoot != null && go == prefabRoot;
            string prefabPath = prefabRoot != null ? AssetDatabase.GetAssetPath(prefabRoot) : null;
            string transformPath = isRoot
                ? ""
                : string.IsNullOrEmpty(parentPath) ? go.name : parentPath + "/" + go.name;

            Node target = parentNode;
            if (!isRoot)
            {
                target = new Node
                {
                    Kind = NodeKind.GameObject,
                    Label = go.name,
                    Icon = StableRefEditorUtility.GoIcon,
                    PingTarget = pingOverride ?? go,
                    PrefabAssetPath = prefabPath,
                    TransformPath = transformPath
                };
            }

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp is not MonoBehaviour || !StableRefPropertyUtils.MayContainStableRef(comp.GetType())) continue;

                var compNode = new Node
                {
                    Kind = NodeKind.Component,
                    Label = comp.GetType().Name,
                    Icon = ScriptIcon,
                    PingTarget = pingOverride ?? comp,
                    PrefabAssetPath = prefabPath,
                    TransformPath = transformPath
                };
                ScanSerializedObject(new SerializedObject(comp), compNode, pingOverride ?? comp);
                if (compNode.Children.Count > 0) target.Children.Add(compNode);
            }

            foreach (Transform child in go.transform)
                ScanGameObjectTree(child.gameObject, target, prefabRoot, transformPath, pingOverride);

            if (!isRoot && target.Children.Count > 0)
                parentNode.Children.Add(target);
        }

        private static void ScanSerializedObject(SerializedObject so, Node parent, UnityEngine.Object pingTarget)
        {
            var fieldGroupNodes = new Dictionary<string, Node>();
            var displayNames = new Dictionary<string, string>();

            var iter = so.GetIterator();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (iter.propertyType == SerializedPropertyType.ManagedReference)
                {
                    if (StableRefPropertyUtils.HasManagedValue(iter) && StableRefPropertyUtils.IsStableRefValueField(iter))
                        AddEntryNode(so, iter, parent, pingTarget, fieldGroupNodes, displayNames);
                    enter = false;
                    continue;
                }

                if (iter.isArray && iter.propertyType == SerializedPropertyType.Generic
                    && StableRefPropertyUtils.IsStableRefArray(iter))
                {
                    string listPath = StableRefEditorUtility.StripStableRefListArraySuffix(iter.propertyPath);
                    string label = StableRefEditorUtility.BuildFieldDisplayPath(so, listPath, displayNames);
                    var node = BuildStableRefListNode(iter, pingTarget, label);
                    if (node != null) parent.Children.Add(node);
                    enter = false;
                    continue;
                }

                enter = StableRefPropertyUtils.MayHoldEntries(iter);
            }
        }

        private static void AddEntryNode(SerializedObject so, SerializedProperty valueProp, Node parent,
            UnityEngine.Object pingTarget, Dictionary<string, Node> fieldGroupNodes, Dictionary<string, string> displayNames)
        {
            var itemNode = BuildItemNode(valueProp, pingTarget);
            if (itemNode == null) return;

            string valuePath = valueProp.propertyPath;
            int lastDot = valuePath.LastIndexOf('.');
            string fieldPath = lastDot > 0 ? valuePath.Substring(0, lastDot) : valuePath;
            string groupLabel = StableRefEditorUtility.BuildFieldDisplayPath(so, fieldPath, displayNames);

            if (string.IsNullOrEmpty(groupLabel))
            {
                parent.Children.Add(itemNode);
                return;
            }

            if (!fieldGroupNodes.TryGetValue(groupLabel, out var groupNode))
            {
                groupNode = new Node { Kind = NodeKind.Item, Label = groupLabel, PingTarget = pingTarget };
                fieldGroupNodes[groupLabel] = groupNode;
                parent.Children.Add(groupNode);
            }
            groupNode.Children.Add(itemNode);
        }

        private static Node BuildListNode(SerializedProperty arrayProp, UnityEngine.Object pingTarget, string label = null)
        {
            var node = new Node { Kind = NodeKind.Item, Label = label ?? arrayProp.displayName, PingTarget = pingTarget };

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var item = BuildItemNode(arrayProp.GetArrayElementAtIndex(i), pingTarget);
                if (item != null) node.Children.Add(item);
            }

            return node.Children.Count > 0 ? node : null;
        }

        private static Node BuildStableRefListNode(SerializedProperty arrayProp, UnityEngine.Object pingTarget, string label = null)
        {
            var node = new Node { Kind = NodeKind.Item, Label = label ?? arrayProp.displayName, PingTarget = pingTarget };

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var elem = arrayProp.GetArrayElementAtIndex(i);
                var valueProp = elem.FindPropertyRelative(StableRefEntry.ValueFieldName);
                if (valueProp == null) continue;

                var item = BuildItemNode(valueProp, pingTarget);
                if (item != null) node.Children.Add(item);
            }

            return node.Children.Count > 0 ? node : null;
        }

        private static Node BuildItemNode(SerializedProperty prop, UnityEngine.Object pingTarget)
        {
            var type = StableRefPropertyUtils.GetManagedReferenceType(prop);
            if (type == null) return null;

            var node = new Node
            {
                Kind = NodeKind.Item,
                Label = StableRefEditorUtility.ValueLabelPrefix + GetTypeDisplayName(type),
                PingTarget = pingTarget,
                ConcreteType = type
            };

            var iter = prop.Copy();
            var end = prop.GetEndProperty();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (SerializedProperty.EqualContents(iter, end)) break;

                if (iter.isArray
                    && iter.propertyType != SerializedPropertyType.String
                    && StableRefPropertyUtils.IsManagedReferenceArray(iter))
                {
                    var sub = BuildListNode(iter, pingTarget);
                    if (sub != null) node.Children.Add(sub);
                    enter = false;
                }
                else if (iter.isArray && iter.propertyType == SerializedPropertyType.Generic
                         && StableRefPropertyUtils.IsStableRefArray(iter))
                {
                    var sub = BuildStableRefListNode(iter, pingTarget, StableRefListFieldLabel(iter));
                    if (sub != null) node.Children.Add(sub);
                    enter = false;
                }
                else if (iter.propertyType == SerializedPropertyType.Generic
                         && iter.FindPropertyRelative(StableRefEntry.TypeIdFieldName) != null
                         && iter.FindPropertyRelative(StableRefEntry.ValueFieldName) is
                             { propertyType: SerializedPropertyType.ManagedReference } nestedValue)
                {
                    var sub = BuildItemNode(nestedValue, pingTarget);
                    if (sub != null)
                    {
                        var group = new Node { Kind = NodeKind.Item, Label = iter.displayName, PingTarget = pingTarget };
                        group.Children.Add(sub);
                        node.Children.Add(group);
                    }
                    enter = false;
                }
                else
                {
                    enter = iter.propertyType != SerializedPropertyType.ManagedReference
                            && StableRefPropertyUtils.MayHoldEntries(iter);
                }
            }

            return node;
        }

        private static string StableRefListFieldLabel(SerializedProperty itemsArray)
        {
            const string suffix = "._items";
            string path = itemsArray.propertyPath;
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                var listProp = itemsArray.serializedObject.FindProperty(path.Substring(0, path.Length - suffix.Length));
                if (listProp != null) return listProp.displayName;
            }
            return null;
        }

        private static void TagScenePath(Node node, string scenePath)
            => TagScenePathRecursive(node, scenePath, null);

        private static void TagScenePathRecursive(Node node, string scenePath, string inheritedTransformPath)
        {
            node.ScenePath = scenePath;
            if (string.IsNullOrEmpty(node.TransformPath))
                node.TransformPath = inheritedTransformPath;
            foreach (var child in node.Children)
                TagScenePathRecursive((Node)child, scenePath, node.TransformPath);
        }

        private static GameObject FindInScene(Scene scene, string transformPath)
        {
            if (string.IsNullOrEmpty(transformPath)) return null;
            int slash = transformPath.IndexOf('/');
            string rootName = slash < 0 ? transformPath : transformPath.Substring(0, slash);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != rootName) continue;
                if (slash < 0) return root;
                var found = root.transform.Find(transformPath.Substring(slash + 1));
                if (found != null) return found.gameObject;
            }
            return null;
        }

        private static UnityEngine.Object ResolvePingTarget(Node node)
        {
            if (node.PingTarget == null) return null;

            if (!string.IsNullOrEmpty(node.ScenePath))
            {
                var scene = SceneManager.GetSceneByPath(node.ScenePath);
                if (scene.IsValid() && scene.isLoaded)
                {
                    var go = FindInScene(scene, node.TransformPath);
                    if (go != null) return go;
                }
                return node.PingTarget;
            }

            if (string.IsNullOrEmpty(node.PrefabAssetPath)) return node.PingTarget;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.assetPath != node.PrefabAssetPath) return node.PingTarget;

            Transform found = string.IsNullOrEmpty(node.TransformPath)
                ? stage.prefabContentsRoot.transform
                : stage.prefabContentsRoot.transform.Find(node.TransformPath);

            if (found == null) return node.PingTarget;

            if (node.Kind == NodeKind.Component && node.PingTarget is Component originalComp)
            {
                var comp = found.GetComponent(originalComp.GetType());
                return comp != null ? comp : (UnityEngine.Object)found.gameObject;
            }

            return found.gameObject;
        }

        private static string GetTypeDisplayName(Type type) => StableRefGenericUtils.DisplayName(type);

        /// <summary>Icon of the script declaring <paramref name="type"/>; resolved when its row is first drawn.</summary>
        private static Texture GetTypeIcon(Type type)
        {
            if (_iconCache.TryGetValue(type, out var cached)) return cached;
            var script = StableRefEditorUtility.FindScriptByFileName(type);
            var icon = script != null ? AssetDatabase.GetCachedIcon(AssetDatabase.GetAssetPath(script)) : null;
            return _iconCache[type] = icon != null ? icon : ScriptIcon;
        }
    }
}
#endif
