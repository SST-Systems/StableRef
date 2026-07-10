#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SST.StableRef
{
    public sealed class StableRefMissingTypesWindow : EditorWindow
    {
        [MenuItem("Tools/StableRef/Fix Missing Types")]
        public static void Open()
        {
            var w = GetWindow<StableRefMissingTypesWindow>("Fix Missing StableRef Types");
            w.minSize = new Vector2(420, 300);
        }

        public static void OpenAndScan()
        {
            var w = GetWindow<StableRefMissingTypesWindow>("Fix Missing StableRef Types");
            w.minSize = new Vector2(420, 300);
            EditorApplication.delayCall += w.DoScan;
        }

        private const float SearchW = 160f;
        private const float ClearBtnW = 18f;

        private enum NodeKind { Group, Asset, GameObject, Component, Item }

        private sealed class Node
        {
            public NodeKind Kind;
            public string Label;
            public Texture Icon;
            public bool Expanded = true;

            public UnityEngine.Object PingTarget;
            public GlobalObjectId ObjectId;
            public string AssetPath;
            public bool IsSceneObject;

            public readonly List<Node> Children = new();
        }

        private List<Node> _roots;
        private Vector2 _scroll;
        private string _filter = "";
        private Node _selectedNode;
        private bool _hasScanned;
        private bool _showDomainReloadHint;

        private void OnGUI()
        {
            StableRefEditorUtility.EnsureStyles();
            DrawToolbar();

            if (!_hasScanned)
            {
                EditorGUILayout.HelpBox(
                    "Press \"Scan Project\" to find objects with missing StableRef types.",
                    MessageType.Info);
                return;
            }

            if (_roots == null || _roots.Count == 0)
            {
                EditorGUILayout.HelpBox("No missing StableRef types found.", MessageType.Info);
                DrawTips();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            EditorGUI.indentLevel = 0;
            EditorGUIUtility.SetIconSize(new Vector2(16, 16));
            foreach (var root in _roots)
                if (MatchesFilter(root)) DrawNode(root, 0);
            EditorGUIUtility.SetIconSize(Vector2.zero);
            GUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                _filter = GUILayout.TextField(_filter, EditorStyles.toolbarSearchField,
                    GUILayout.Width(SearchW));
                if (EditorGUI.EndChangeCheck()) Repaint();

                if (!string.IsNullOrEmpty(_filter) &&
                    GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(ClearBtnW)))
                {
                    _filter = "";
                    GUI.FocusControl(null);
                    Repaint();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Scan Project", EditorStyles.toolbarButton, GUILayout.Width(90f)))
                    DoScan();
            }
        }

        private void DrawNode(Node node, int depth)
        {
            switch (node.Kind)
            {
                case NodeKind.Group:
                    EditorGUI.indentLevel = 0;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, null, StableRefEditorUtility.HeaderStyle);
                    if (node.Expanded) DrawChildren(node.Children, depth + 1);
                    break;

                case NodeKind.Asset:
                    EditorGUI.indentLevel = 1;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                    if (node.Expanded) DrawChildren(node.Children, 0);
                    break;

                case NodeKind.GameObject:
                    EditorGUI.indentLevel = 2 + depth;
                    node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                    if (node.Expanded) DrawChildren(node.Children, depth + 1);
                    break;

                case NodeKind.Component:
                    EditorGUI.indentLevel = 2 + depth;
                    if (node.Children.Count > 0)
                    {
                        node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                        if (node.Expanded) DrawChildren(node.Children, depth);
                    }
                    else
                    {
                        DrawLeaf(node);
                    }
                    break;

                case NodeKind.Item:
                    EditorGUI.indentLevel = 3 + depth;
                    if (node.Children.Count > 0)
                    {
                        var prevColor = GUI.color;
                        if (node.Icon == null) GUI.color = new Color(1f, 1f, 1f, 0.55f);
                        node.Expanded = DrawFoldout(node, node.Expanded, node.Label, node.Icon);
                        GUI.color = prevColor;
                        if (node.Expanded) DrawChildren(node.Children, depth + 1);
                    }
                    else
                    {
                        DrawMissingRefLeaf(node);
                    }
                    break;
            }
        }

        private void DrawChildren(List<Node> children, int depth)
        {
            foreach (var c in children)
                if (MatchesFilter(c)) DrawNode(c, depth);
        }

        private bool MatchesFilter(Node node)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            if (node.Label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return node.Children.Any(MatchesFilter);
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
                    if (node.PingTarget != null) EditorGUIUtility.PingObject(node.PingTarget);
                    if (ev.clickCount == 2) newExpanded = !newExpanded;
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

            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition))
            {
                _selectedNode = node;
                if (node.PingTarget != null) EditorGUIUtility.PingObject(node.PingTarget);
                Repaint();
                ev.Use();
                return;
            }

            var prevContent = GUI.contentColor;
            if (isSelected) GUI.contentColor = StableRefEditorUtility.SelectionTextColor;
            GUI.Label(rect, new GUIContent(" " + node.Label, node.Icon));
            GUI.contentColor = prevContent;
        }

        private void DrawMissingRefLeaf(Node node)
        {
            var fullRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            bool isSelected = _selectedNode == node;
            if (isSelected && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(0, fullRect.y, position.width, fullRect.height), StableRefEditorUtility.SelectionColor);

            var rect = EditorGUI.IndentedRect(fullRect);
            var ev = Event.current;

            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition))
            {
                _selectedNode = node;
                if (node.PingTarget != null) EditorGUIUtility.PingObject(node.PingTarget);
                Repaint();
                ev.Use();
                return;
            }

            var prevContent = GUI.contentColor;
            if (isSelected)
                GUI.contentColor = StableRefEditorUtility.SelectionTextColor;
            else
                GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);

            GUI.Label(rect, new GUIContent(" " + node.Label, node.Icon));
            GUI.contentColor = prevContent;
        }

        private static void SetExpandedRecursive(Node node, bool expanded)
        {
            node.Expanded = expanded;
            foreach (var c in node.Children)
                SetExpandedRecursive(c, expanded);
        }

        private static void DrawTips()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                "Tip: If you are sure missing StableRef types exist but the scan finds nothing —\n" +
                "restart Unity, or manually recreate the broken asset files.",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
            GUILayout.Space(8);
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginHorizontal();

            if (_showDomainReloadHint)
            {
                if (GUILayout.Button("Domain Reload", GUILayout.Width(120), GUILayout.Height(24)))
                    EditorUtility.RequestScriptReload();
                var hint = EditorGUIUtility.IconContent("console.infoicon.sml");
                hint.tooltip = "If errors persist after fixing, use Domain Reload to fully reset Unity's serialization state.";
                GUILayout.Label(hint, GUILayout.Width(20), GUILayout.Height(24));
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fix All Missings", GUILayout.Width(160), GUILayout.Height(24)))
                DoFixAll();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private struct MissingRefInfo
        {
            public string GroupLabel;
            public string TypeDisplayName;
            public string ResolvedName;
        }

        private struct ScanEntry
        {
            public UnityEngine.Object Target;
            public GlobalObjectId ObjectId;
            public string AssetPath;
            public bool IsSceneObject;
            public List<MissingRefInfo> MissingRefs;
        }

        private readonly List<ScanEntry> _scanEntries = new();
        private readonly HashSet<GlobalObjectId> _scanSeen = new();

        internal void DoScan()
        {
            _scanEntries.Clear();
            _scanSeen.Clear();
            _hasScanned = true;
            _showDomainReloadHint = false;

            var assetGuids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })
                .Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                .Distinct()
                .ToArray();
            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });

            int total = assetGuids.Length + sceneGuids.Length;
            int idx = 0;

            try
            {
                foreach (var guid in assetGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    EditorUtility.DisplayProgressBar("Scanning assets…", path, (float)idx++ / total);

                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset == null) continue;

                    if (asset is GameObject go)
                    {
                        foreach (var comp in go.GetComponentsInChildren<Component>(true))
                            if (comp != null) CollectMissing(comp, path, isScene: false);
                    }
                    else
                    {
                        CollectMissing(asset, path, isScene: false);
                    }
                }

                foreach (var guid in sceneGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    EditorUtility.DisplayProgressBar("Scanning scenes…", path, (float)idx++ / total);
                    ScanScenePath(path);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            BuildTree();
            Repaint();
        }

        private void ScanScenePath(string scenePath)
        {
            var existingScene = SceneManager.GetSceneByPath(scenePath);
            bool wasLoaded = existingScene.IsValid() && existingScene.isLoaded;

            UnityEngine.SceneManagement.Scene scene;
            if (wasLoaded)
                scene = existingScene;
            else
            {
                try { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive); }
                catch { return; }
            }

            try
            {
                foreach (var root in scene.GetRootGameObjects())
                foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    if (comp != null) CollectMissing(comp, scenePath, isScene: true);
            }
            finally
            {
                if (!wasLoaded) EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        private void CollectMissing(UnityEngine.Object target, string assetPath, bool isScene)
        {
            if (target is not MonoBehaviour && target is not ScriptableObject) return;
            if (!SerializationUtility.HasManagedReferencesWithMissingTypes(target)) return;

            var objectId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if (!_scanSeen.Add(objectId)) return;

            var missingRefs = CollectMissingRefs(target);
            if (missingRefs.Count == 0) return;

            _scanEntries.Add(new ScanEntry
            {
                Target = target,
                ObjectId = objectId,
                AssetPath = assetPath,
                IsSceneObject = isScene,
                MissingRefs = missingRefs
            });
        }

        private static List<MissingRefInfo> CollectMissingRefs(UnityEngine.Object target)
        {
            var result = new List<MissingRefInfo>();
            var so = new SerializedObject(target);
            so.Update();
            var iter = so.GetIterator();

            while (iter.Next(true))
            {
                if (iter.propertyType != SerializedPropertyType.ManagedReference) continue;
                if (iter.name != "Value") continue;
                if (iter.managedReferenceValue != null) continue;

                string parentPath = ParentPath(iter.propertyPath);
                if (parentPath == null) continue;

                var wrapperProp = so.FindProperty(parentPath);
                if (wrapperProp == null) continue;

                var typeIdProp = wrapperProp.FindPropertyRelative("TypeId");
                var dispProp = wrapperProp.FindPropertyRelative("TypeDisplayName");
                if (typeIdProp == null || string.IsNullOrEmpty(typeIdProp.stringValue)) continue;

                string displayName = dispProp?.stringValue;
                if (string.IsNullOrEmpty(displayName)) displayName = "?";

                var recoveredType = StableRefTypeRegistry.GetType(typeIdProp.stringValue);
                string resolvedName = recoveredType != null ? recoveredType.Name : "None";

                result.Add(new MissingRefInfo
                {
                    GroupLabel = GetFieldGroupLabel(so, wrapperProp.propertyPath),
                    TypeDisplayName = displayName,
                    ResolvedName = resolvedName
                });
            }

            return result;
        }

        private static string GetFieldGroupLabel(SerializedObject so, string wrapperPath)
        {
            string fieldPath = StableRefEditorUtility.StripStableRefListArraySuffix(wrapperPath);
            return StableRefEditorUtility.BuildFieldDisplayPath(so, fieldPath);
        }

        private void BuildTree()
        {
            _roots = new List<Node>();

            var prefabGroup = new Node { Kind = NodeKind.Group, Label = "Prefabs" };
            var sceneGroup  = new Node { Kind = NodeKind.Group, Label = "Scenes" };
            var soGroup     = new Node { Kind = NodeKind.Group, Label = "Scriptable Objects" };

            foreach (var assetGroup in _scanEntries.GroupBy(e => e.AssetPath).OrderBy(g => g.Key))
            {
                var assetPath = assetGroup.Key;
                var firstEntry = assetGroup.First();
                bool isScene = firstEntry.IsSceneObject;

                Node targetGroup;
                if (isScene)
                    targetGroup = sceneGroup;
                else if (firstEntry.Target is ScriptableObject)
                    targetGroup = soGroup;
                else
                    targetGroup = prefabGroup;

                var assetNode = new Node
                {
                    Kind = NodeKind.Asset,
                    Label = System.IO.Path.GetFileNameWithoutExtension(assetPath),
                    Icon = isScene
                        ? EditorGUIUtility.IconContent("d_SceneAsset Icon").image
                        : AssetDatabase.GetCachedIcon(assetPath),
                    PingTarget = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath),
                    AssetPath = assetPath
                };

                foreach (var entry in assetGroup)
                {
                    if (entry.Target is ScriptableObject so)
                    {
                        var compNode = new Node
                        {
                            Kind = NodeKind.Component,
                            Label = so.GetType().Name,
                            Icon = EditorGUIUtility.IconContent("cs Script Icon").image,
                            PingTarget = so,
                            ObjectId = entry.ObjectId,
                            AssetPath = assetPath,
                            IsSceneObject = false
                        };
                        AddMissingRefNodes(compNode, entry.MissingRefs, so);
                        assetNode.Children.Add(compNode);
                    }
                    else if (entry.Target is Component comp)
                    {
                        InsertComponentIntoHierarchy(assetNode, comp, entry, assetPath);
                    }
                }

                if (assetNode.Children.Count > 0)
                    targetGroup.Children.Add(assetNode);
            }

            if (prefabGroup.Children.Count > 0) _roots.Add(prefabGroup);
            if (sceneGroup.Children.Count > 0)  _roots.Add(sceneGroup);
            if (soGroup.Children.Count > 0)     _roots.Add(soGroup);
        }

        private static void AddMissingRefNodes(Node compNode, List<MissingRefInfo> missingRefs, UnityEngine.Object pingTarget)
        {
            var groupNodes = new Dictionary<string, Node>();

            foreach (var r in missingRefs)
            {
                Node parent = compNode;

                if (r.GroupLabel != null)
                {
                    if (!groupNodes.TryGetValue(r.GroupLabel, out var groupNode))
                    {
                        groupNode = new Node
                        {
                            Kind = NodeKind.Item,
                            Label = r.GroupLabel,
                            PingTarget = pingTarget
                        };
                        groupNodes[r.GroupLabel] = groupNode;
                        compNode.Children.Add(groupNode);
                    }
                    parent = groupNode;
                }

                parent.Children.Add(new Node
                {
                    Kind = NodeKind.Item,
                    Label = $"Missing ({r.TypeDisplayName}) → {r.ResolvedName}",
                    Icon = EditorGUIUtility.IconContent("console.warnicon.sml").image,
                    PingTarget = pingTarget
                });
            }
        }

        private static void InsertComponentIntoHierarchy(
            Node assetNode, Component comp, ScanEntry entry, string assetPath)
        {
            var chain = new List<Transform>();
            var t = comp.transform;
            while (t != null) { chain.Insert(0, t); t = t.parent; }

            Node current = assetNode;
            foreach (var tr in chain)
            {
                var existing = current.Children.FirstOrDefault(
                    n => n.Kind == NodeKind.GameObject && n.Label == tr.gameObject.name);

                if (existing == null)
                {
                    existing = new Node
                    {
                        Kind = NodeKind.GameObject,
                        Label = tr.gameObject.name,
                        Icon = StableRefEditorUtility.GoIcon,
                        PingTarget = tr.gameObject
                    };
                    current.Children.Add(existing);
                }
                current = existing;
            }

            var compNode = new Node
            {
                Kind = NodeKind.Component,
                Label = comp.GetType().Name,
                Icon = EditorGUIUtility.IconContent("cs Script Icon").image,
                PingTarget = comp,
                ObjectId = entry.ObjectId,
                AssetPath = assetPath,
                IsSceneObject = entry.IsSceneObject
            };
            AddMissingRefNodes(compNode, entry.MissingRefs, comp);
            current.Children.Add(compNode);
        }

        private void DoFixAll()
        {
            var components = new List<Node>();
            foreach (var root in _roots) CollectComponents(root, components);

            var seen = new HashSet<GlobalObjectId>();
            var entries = components.Where(n => seen.Add(n.ObjectId)).ToList();

            int fixedCount = 0;
            var fixedPaths = new HashSet<string>();

            var assetEntries = entries.Where(n => !n.IsSceneObject).ToList();
            try
            {
                for (int i = 0; i < assetEntries.Count; i++)
                {
                    var node = assetEntries[i];
                    EditorUtility.DisplayProgressBar("Fixing assets…",
                        node.AssetPath, (float)i / assetEntries.Count);

                    var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                    if (target == null) continue;

                    fixedCount += FixTarget(target);
                    fixedPaths.Add(node.AssetPath);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            AssetDatabase.SaveAssets();

            var byScene = entries.Where(n => n.IsSceneObject).GroupBy(n => n.AssetPath).ToList();
            try
            {
                for (int si = 0; si < byScene.Count; si++)
                {
                    var sceneGroup = byScene[si];
                    var scenePath = sceneGroup.Key;
                    EditorUtility.DisplayProgressBar("Fixing scenes…",
                        scenePath, (float)si / byScene.Count);

                    var existingScene = SceneManager.GetSceneByPath(scenePath);
                    bool wasLoaded = existingScene.IsValid() && existingScene.isLoaded;

                    UnityEngine.SceneManagement.Scene scene;
                    if (wasLoaded) scene = existingScene;
                    else
                    {
                        try { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive); }
                        catch { continue; }
                    }

                    try
                    {
                        foreach (var node in sceneGroup)
                        {
                            var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                            if (target == null) continue;
                            fixedCount += FixTarget(target);
                        }

                        EditorSceneManager.SaveScene(scene);
                        fixedPaths.Add(scenePath);
                    }
                    finally
                    {
                        if (!wasLoaded) EditorSceneManager.CloseScene(scene, removeScene: true);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (fixedPaths.Count > 0)
                AssetDatabase.Refresh();

            Debug.Log($"[StableRef] Fixed {fixedCount} missing reference{(fixedCount != 1 ? "s" : "")}.");

            EditorApplication.delayCall += () =>
            {
                StableRefHandler.ClearHadValue();
                DoScan();
                _showDomainReloadHint = _roots != null && _roots.Count > 0;
                Repaint();
            };
        }

        private static void CollectComponents(Node node, List<Node> result)
        {
            if (node.Kind == NodeKind.Component)
                result.Add(node);
            foreach (var c in node.Children)
                CollectComponents(c, result);
        }

        private static int FixTarget(UnityEngine.Object target)
        {
            var so = new SerializedObject(target);
            var iter = so.GetIterator();
            int count = 0;

            so.Update();

            while (iter.Next(true))
            {
                if (iter.propertyType != SerializedPropertyType.ManagedReference) continue;
                if (iter.name != "Value") continue;
                if (iter.managedReferenceValue != null) continue;

                string parentPath = ParentPath(iter.propertyPath);
                if (parentPath == null) continue;

                var wrapperProp = so.FindProperty(parentPath);
                if (wrapperProp == null) continue;

                var typeIdProp = wrapperProp.FindPropertyRelative("TypeId");
                var valueProp = wrapperProp.FindPropertyRelative("Value");
                var dispProp = wrapperProp.FindPropertyRelative("TypeDisplayName");
                if (typeIdProp == null || valueProp == null) continue;
                if (string.IsNullOrEmpty(typeIdProp.stringValue)) continue;

                var recoveredType = StableRefTypeRegistry.GetType(typeIdProp.stringValue);
                if (recoveredType != null)
                {
                    valueProp.managedReferenceValue = Activator.CreateInstance(recoveredType);
                    StableRefHandler.RestoreBackup(wrapperProp, valueProp);
                    if (dispProp != null) dispProp.stringValue = recoveredType.Name;
                }
                else
                {
                    valueProp.managedReferenceValue = null;
                    typeIdProp.stringValue = string.Empty;
                    if (dispProp != null) dispProp.stringValue = string.Empty;
                    wrapperProp.FindPropertyRelative("ObjectRefs")?.ClearArray();
                    wrapperProp.FindPropertyRelative("ObjectRefPaths")?.ClearArray();
                    var valData = wrapperProp.FindPropertyRelative("ValuesData");
                    if (valData != null) valData.stringValue = string.Empty;
                }

                count++;
            }

            if (count > 0)
            {
                so.ApplyModifiedProperties();
                SerializationUtility.ClearAllManagedReferencesWithMissingTypes(target);
                EditorUtility.SetDirty(target);
            }

            return count;
        }

        private static string ParentPath(string propertyPath)
        {
            int lastDot = propertyPath.LastIndexOf('.');
            return lastDot > 0 ? propertyPath.Substring(0, lastDot) : null;
        }
    }
}
#endif