#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SST.StableRef
{
    public sealed class StableRefUsagesWindow : EditorWindow
    {
        private const float FilterW = 110f;
        private const float FindW = 90f;
        private const float SearchW = 160f;
        private const float ClearBtnW = 18f;
        private const float DefaultW = 360f;
        private const float DefaultH = 560f;

        private enum NodeKind { Group, Asset, GameObject, Component, Item }

        private sealed class Node
        {
            public NodeKind Kind;
            public string Label;
            public Texture Icon;
            public Type BaseType;
            public Type ConcreteType;
            public UnityEngine.Object PingTarget;
            public string PrefabAssetPath;
            public string TransformPath;
            public string ScenePath;
            public readonly List<Node> Children = new();
            public bool Expanded = true;
        }

        private List<Node> _roots;
        private Vector2 _scroll;
        private string _searchText = "";
        private Type _typeFilter;
        private readonly List<Type> _availableTypes = new();
        private readonly HashSet<Node> _visibleNodes = new();
        private bool _visibilityDirty;
        private Node _selectedNode;

        private static readonly Dictionary<Type, Texture> _iconCache = new();

        [MenuItem("Tools/StableRef/Find Usages", priority = 100)]
        public static void Open()
        {
            var win = GetWindow<StableRefUsagesWindow>("StableRef Usages");
            win.minSize = new Vector2(DefaultW, 160f);
            if (win.position.width < DefaultW || win.position.height < DefaultH)
                win.position = new Rect(win.position.x, win.position.y, DefaultW, DefaultH);
        }

        [MenuItem("Assets/Find StableRef Usages", priority = 27)]
        private static void OpenFromAsset()
        {
            var ms = Selection.activeObject as MonoScript;
            if (ms == null) return;
            var type = ms.GetClass();
            if (type == null) return;

            var win = GetWindow<StableRefUsagesWindow>("StableRef Usages");
            win.minSize = new Vector2(DefaultW, 160f);
            win._searchText = type.Name;
            win.RunSearch();
        }

        [MenuItem("Assets/Find StableRef Usages", validate = true, priority = 27)]
        private static bool OpenFromAssetValidate()
        {
            var ms = Selection.activeObject as MonoScript;
            if (ms == null) return false;
            var type = ms.GetClass();
            return type != null && !type.IsAbstract && !type.IsInterface;
        }

        private void OnGUI()
        {
            StableRefEditorUtility.EnsureStyles();
            DrawToolbar();

            if (_roots == null)
            {
                EditorGUILayout.HelpBox(
                    "Press \"Find Usages\" to search across prefabs, active scenes and scriptable objects.",
                    MessageType.Info);
                return;
            }

            if (_roots.Count == 0)
            {
                EditorGUILayout.HelpBox("No StableRef usages found.", MessageType.Info);
                return;
            }

            RefreshVisibilityIfNeeded();

            bool anyVisible = false;
            foreach (var root in _roots)
                if (IsVisible(root)) { anyVisible = true; break; }

            if (!anyVisible)
            {
                EditorGUILayout.HelpBox("No usages match the current filter.", MessageType.Info);
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            EditorGUI.indentLevel = 0;
            EditorGUIUtility.SetIconSize(new Vector2(16, 16));
            foreach (var root in _roots)
                if (IsVisible(root)) DrawNode(root, 0);
            EditorGUIUtility.SetIconSize(Vector2.zero);
            GUILayout.EndScrollView();
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
                    InvalidateVisibility();
                    Repaint();
                }

                if (!string.IsNullOrEmpty(_searchText)
                    && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(ClearBtnW)))
                {
                    _searchText = "";
                    InvalidateVisibility();
                    GUI.FocusControl(null);
                    Repaint();
                }

                string filterLabel = _typeFilter == null ? "All Types" : GetTypeDisplayName(_typeFilter);
                EditorGUI.BeginDisabledGroup(_availableTypes.Count == 0);
                if (EditorGUILayout.DropdownButton(new GUIContent(filterLabel), FocusType.Passive,
                        EditorStyles.toolbarDropDown, GUILayout.Width(FilterW)))
                    ShowTypeFilterMenu();
                EditorGUI.EndDisabledGroup();

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Find Usages", EditorStyles.toolbarButton, GUILayout.Width(FindW)))
                    RunSearch();
            }
        }

        private void ShowTypeFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All Types"), _typeFilter == null, () =>
            {
                _typeFilter = null;
                InvalidateVisibility();
                Repaint();
            });
            menu.AddSeparator("");
            foreach (var type in _availableTypes)
            {
                var t = type;
                menu.AddItem(new GUIContent(GetTypeDisplayName(t)), _typeFilter == t, () =>
                {
                    _typeFilter = t;
                    InvalidateVisibility();
                    Repaint();
                });
            }
            menu.ShowAsContext();
        }

        private void InvalidateVisibility() => _visibilityDirty = true;

        private void RefreshVisibilityIfNeeded()
        {
            if (!_visibilityDirty) return;
            _visibilityDirty = false;
            _visibleNodes.Clear();
            foreach (var root in _roots)
                ComputeVisible(root);
        }

        private bool ComputeVisible(Node node)
        {
            if (node.Kind == NodeKind.Group)
            {
                foreach (var c in node.Children)
                    ComputeVisible(c);
                _visibleNodes.Add(node);
                return true;
            }

            bool vis;
            if (node.Children.Count > 0)
            {
                vis = false;
                foreach (var c in node.Children)
                    if (ComputeVisible(c)) vis = true;
            }
            else
            {
                bool textOk = string.IsNullOrEmpty(_searchText)
                    || node.Label.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                vis = textOk && (_typeFilter == null || node.BaseType == _typeFilter);
            }
            if (vis) _visibleNodes.Add(node);
            return vis;
        }

        private bool IsVisible(Node node) => _visibleNodes.Contains(node);

        private void DrawNode(Node node, int depth)
        {
            switch (node.Kind)
            {
                case NodeKind.Group:
                    EditorGUI.indentLevel = 0;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, null, StableRefEditorUtility.HeaderStyle);
                    if (node.Expanded)
                    {
                        bool anyChildVisible = false;
                        foreach (var c in node.Children)
                            if (IsVisible(c)) { anyChildVisible = true; break; }

                        if (anyChildVisible)
                            DrawVisibleChildren(node.Children, depth + 1);
                        else
                        {
                            EditorGUI.indentLevel = 1;
                            using (new EditorGUI.DisabledScope(true))
                                EditorGUILayout.LabelField(node.Children.Count == 0 ? "No usages found" : "No matches");
                        }
                    }
                    return;

                case NodeKind.Asset:
                    EditorGUI.indentLevel = 1;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                    if (node.Expanded) DrawVisibleChildren(node.Children, 0);
                    return;

                case NodeKind.GameObject:
                    EditorGUI.indentLevel = 2 + depth;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                    if (node.Expanded) DrawVisibleChildren(node.Children, depth + 1);
                    return;

                case NodeKind.Component:
                    EditorGUI.indentLevel = 2 + depth;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                    if (node.Expanded) DrawVisibleChildren(node.Children, depth);
                    return;

                case NodeKind.Item:
                    EditorGUI.indentLevel = 3 + depth;
                    if (node.Children.Count > 0)
                    {
                        var prevColor = GUI.color;
                        if (node.Icon == null) GUI.color = new Color(1f, 1f, 1f, 0.55f);
                        node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                        GUI.color = prevColor;
                        if (node.Expanded) DrawVisibleChildren(node.Children, depth + 1);
                    }
                    else
                    {
                        DrawLeaf(node);
                    }
                    return;
            }
        }

        private void DrawVisibleChildren(List<Node> children, int depth)
        {
            foreach (var c in children)
                if (IsVisible(c)) DrawNode(c, depth);
        }

        private bool DrawFoldout(Node node, bool expanded, string label, Texture icon, GUIStyle style = null)
        {
            bool altHeld = (Event.current.modifiers & EventModifiers.Alt) != 0;
            var ev = Event.current;
            var drawStyle = style ?? StableRefEditorUtility.FoldoutStyle;

            var rowRect = GUILayoutUtility.GetRect(
                new GUIContent(label, icon), drawStyle,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            bool selectable = node.Kind != NodeKind.Group;
            bool isSelected = selectable && _selectedNode == node;

            if (isSelected && ev.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(0, rowRect.y, position.width, rowRect.height), StableRefEditorUtility.SelectionColor);

            var prevContent = GUI.contentColor;
            if (isSelected) GUI.contentColor = StableRefEditorUtility.SelectionTextColor;

            bool newExpanded = EditorGUI.Foldout(rowRect, expanded,
                new GUIContent(label, icon), toggleOnLabelClick: false, drawStyle);

            GUI.contentColor = prevContent;

            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                var indented = EditorGUI.IndentedRect(rowRect);
                var labelRect = new Rect(indented.x + StableRefEditorUtility.ArrowW, rowRect.y,
                                         rowRect.xMax - indented.x - StableRefEditorUtility.ArrowW, rowRect.height);
                if (labelRect.Contains(ev.mousePosition))
                {
                    if (selectable) _selectedNode = node;
                    if (node.PingTarget != null)
                        EditorGUIUtility.PingObject(ResolvePingTarget(node));
                    if (ev.clickCount == 2)
                        newExpanded = !newExpanded;
                    Repaint();
                }
            }

            if (newExpanded != expanded && altHeld)
                SetExpandedRecursive(node, newExpanded);

            return newExpanded;
        }

        private void DrawLeaf(Node node)
        {
            var fullRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            bool isSelected = _selectedNode == node;
            if (isSelected && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(0, fullRect.y, position.width, fullRect.height), StableRefEditorUtility.SelectionColor);

            var rect = EditorGUI.IndentedRect(fullRect);
            var ev = Event.current;

            if (node.ConcreteType != null && ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition))
            {
                _selectedNode = node;
                StableRefEditorUtility.PingScript(node.ConcreteType);
                Repaint();
                ev.Use();
                return;
            }

            var prevContent = GUI.contentColor;
            if (isSelected) GUI.contentColor = StableRefEditorUtility.SelectionTextColor;
            string displayLabel = node.BaseType != null
                ? $"{GetTypeDisplayName(node.BaseType)}: {node.Label}"
                : node.Label;
            GUI.Label(rect, new GUIContent(" " + displayLabel, node.Icon));
            GUI.contentColor = prevContent;
        }

        private static void SetExpandedRecursive(Node node, bool expanded)
        {
            node.Expanded = expanded;
            foreach (var c in node.Children)
                SetExpandedRecursive(c, expanded);
        }

        private void RunSearch()
        {
            _roots = new List<Node>();
            _typeFilter = null;
            _availableTypes.Clear();

            var prefabGroup = new Node { Kind = NodeKind.Group, Label = "Prefabs" };
            var sceneGroup = new Node { Kind = NodeKind.Group, Label = "Active Scenes" };
            var soGroup = new Node { Kind = NodeKind.Group, Label = "Scriptable Objects" };

            try
            {
                ScanPrefabs(prefabGroup);
                ScanScenes(sceneGroup);
                ScanScriptableObjects(soGroup);
            }
            finally { EditorUtility.ClearProgressBar(); }

            _roots.Add(prefabGroup);
            _roots.Add(sceneGroup);
            _roots.Add(soGroup);

            CollectAvailableTypes();
            InvalidateVisibility();
            Repaint();
        }

        private static void ScanPrefabs(Node group)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab");
            for (int i = 0; i < guids.Length; i++)
            {
                EditorUtility.DisplayProgressBar("StableRef Usages",
                    $"Prefabs ({i + 1} / {guids.Length})", 0.7f * i / guids.Length);

                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                var assetNode = new Node
                {
                    Kind = NodeKind.Asset, Label = go.name,
                    Icon = AssetDatabase.GetCachedIcon(path), PingTarget = go,
                    Expanded = true
                };
                ScanGameObjectTree(go, assetNode, go);
                if (assetNode.Children.Count > 0) group.Children.Add(assetNode);
            }
        }

        // Only reads scenes that are already open — never opens or closes anything. Opening every
        // scene in the project additively was expensive on large projects and had side effects
        // (e.g. triggering the engine's lighting auto-bake), so this only ever looks at what's
        // already loaded in the editor.
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
                    Icon = EditorGUIUtility.IconContent("d_SceneAsset Icon").image,
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
            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            for (int i = 0; i < guids.Length; i++)
            {
                EditorUtility.DisplayProgressBar("StableRef Usages",
                    $"Scriptable Objects ({i + 1} / {guids.Length})",
                    0.8f + 0.2f * i / guids.Length);

                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.StartsWith("Assets/")) continue;

                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (so == null) continue;

                var compNode = new Node
                {
                    Kind = NodeKind.Component, Label = so.GetType().Name,
                    Icon = EditorGUIUtility.IconContent("cs Script Icon").image,
                    PingTarget = so
                };
                ScanSerializedObject(new SerializedObject(so), compNode, so);
                if (compNode.Children.Count == 0) continue;

                var assetNode = new Node
                {
                    Kind = NodeKind.Asset, Label = Path.GetFileNameWithoutExtension(path),
                    Icon = AssetDatabase.GetCachedIcon(path), PingTarget = so
                };
                assetNode.Children.Add(compNode);
                group.Children.Add(assetNode);
            }
        }

        private void CollectAvailableTypes() => CollectAvailableTypes(_roots);

        private void CollectAvailableTypes(List<Node> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.BaseType != null && !_availableTypes.Contains(node.BaseType))
                    _availableTypes.Add(node.BaseType);
                if (node.Children.Count > 0)
                    CollectAvailableTypes(node.Children);
            }
        }

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
                    TransformPath = transformPath,
                    Expanded = true
                };
            }

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var compNode = new Node
                {
                    Kind = NodeKind.Component,
                    Label = comp.GetType().Name,
                    Icon = EditorGUIUtility.IconContent("cs Script Icon").image,
                    PingTarget = pingOverride ?? comp,
                    PrefabAssetPath = prefabPath,
                    TransformPath = transformPath,
                    Expanded = true
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

            var iter = so.GetIterator();
            bool enter = true;
            while (iter.Next(enter))
            {
                enter = true;
                if (iter.isArray && StableRefPropertyUtils.IsStableRefArray(iter))
                {
                    string listPath = StableRefEditorUtility.StripStableRefListArraySuffix(iter.propertyPath);
                    string label = StableRefEditorUtility.BuildFieldDisplayPath(so, listPath);
                    var node = BuildStableRefListNode(iter, pingTarget, label);
                    if (node != null) parent.Children.Add(node);
                    enter = false;
                }
                else if (iter.propertyType == SerializedPropertyType.ManagedReference
                    && StableRefPropertyUtils.IsStableRefValueField(iter)
                    && iter.managedReferenceValue != null)
                {
                    var baseType = StableRefPropertyUtils.GetBaseType(iter);
                    var itemNode = BuildItemNode(iter, pingTarget, baseType);
                    if (itemNode != null)
                    {
                        string iterPath = iter.propertyPath;
                        int lastDot = iterPath.LastIndexOf('.');
                        string fieldPath = lastDot > 0 ? iterPath.Substring(0, lastDot) : iterPath;
                        string groupLabel = StableRefEditorUtility.BuildFieldDisplayPath(so, fieldPath);

                        if (!string.IsNullOrEmpty(groupLabel))
                        {
                            if (!fieldGroupNodes.TryGetValue(groupLabel, out var groupNode))
                            {
                                groupNode = new Node { Kind = NodeKind.Item, Label = groupLabel, PingTarget = pingTarget, Expanded = true };
                                fieldGroupNodes[groupLabel] = groupNode;
                                parent.Children.Add(groupNode);
                            }
                            groupNode.Children.Add(itemNode);
                        }
                        else
                        {
                            parent.Children.Add(itemNode);
                        }
                    }
                    enter = false;
                }
                else if (iter.propertyType == SerializedPropertyType.ManagedReference)
                {
                    enter = false;
                }
            }
        }

        private static Node BuildListNode(SerializedProperty arrayProp, UnityEngine.Object pingTarget, string label = null)
        {
            var baseType = StableRefPropertyUtils.GetArrayElementBaseType(arrayProp);
            var node = new Node { Kind = NodeKind.Item, Label = label ?? arrayProp.displayName, PingTarget = pingTarget };

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var item = BuildItemNode(arrayProp.GetArrayElementAtIndex(i), pingTarget, baseType);
                if (item != null) node.Children.Add(item);
            }

            return node.Children.Count > 0 ? node : null;
        }

        private static Node BuildStableRefListNode(SerializedProperty arrayProp, UnityEngine.Object pingTarget, string label = null)
        {
            var baseType = StableRefPropertyUtils.GetStableRefValueBaseType(arrayProp);
            var node = new Node { Kind = NodeKind.Item, Label = label ?? arrayProp.displayName, PingTarget = pingTarget };

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var elem = arrayProp.GetArrayElementAtIndex(i);
                var valueProp = elem.FindPropertyRelative("Value");
                if (valueProp == null || valueProp.managedReferenceValue == null) continue;

                var item = BuildItemNode(valueProp, pingTarget, baseType);
                if (item != null) node.Children.Add(item);
            }

            return node.Children.Count > 0 ? node : null;
        }

        private static Node BuildItemNode(SerializedProperty prop, UnityEngine.Object pingTarget, Type baseType)
        {
            var value = prop.managedReferenceValue;
            if (value == null) return null;

            var type = value.GetType();
            var node = new Node
            {
                Kind = NodeKind.Item,
                Label = type.Name,
                Icon = GetTypeIcon(type),
                PingTarget = pingTarget,
                BaseType = baseType,
                ConcreteType = type
            };

            var iter = prop.Copy();
            var end = prop.GetEndProperty();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (SerializedProperty.EqualContents(iter, end)) break;
                enter = true;
                if (iter.isArray
                    && iter.propertyType != SerializedPropertyType.String
                    && StableRefPropertyUtils.IsManagedReferenceArray(iter))
                {
                    var sub = BuildListNode(iter, pingTarget);
                    if (sub != null) node.Children.Add(sub);
                    enter = false;
                }
                else if (iter.isArray && StableRefPropertyUtils.IsStableRefArray(iter))
                {
                    var sub = BuildStableRefListNode(iter, pingTarget);
                    if (sub != null) node.Children.Add(sub);
                    enter = false;
                }
                else if (iter.propertyType == SerializedPropertyType.ManagedReference)
                {
                    enter = false;
                }
            }

            return node;
        }

        private static void TagScenePath(Node node, string scenePath)
            => TagScenePathRecursive(node, scenePath, null);

        private static void TagScenePathRecursive(Node node, string scenePath, string inheritedTransformPath)
        {
            node.ScenePath = scenePath;
            if (string.IsNullOrEmpty(node.TransformPath))
                node.TransformPath = inheritedTransformPath;
            foreach (var child in node.Children)
                TagScenePathRecursive(child, scenePath, node.TransformPath);
        }

        private static GameObject FindInScene(UnityEngine.SceneManagement.Scene scene, string transformPath)
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

        private static string GetTypeDisplayName(Type type)
        {
            string name = type.IsGenericType
                ? type.Name.Substring(0, type.Name.IndexOf('`'))
                : type.Name;

            if (name.StartsWith("I") && type.IsInterface)
                name = name.Substring(1);

            return name;
        }

        private static Texture GetTypeIcon(Type type)
        {
            if (_iconCache.TryGetValue(type, out var cached)) return cached;
            foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {type.Name}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms != null && ms.GetClass() == type)
                    return _iconCache[type] = AssetDatabase.GetCachedIcon(path);
            }
            return _iconCache[type] = EditorGUIUtility.IconContent("cs Script Icon").image;
        }
    }
}
#endif