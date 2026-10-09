#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using NodeKind = SST.StableRef.StableRefResultTree.NodeKind;

namespace SST.StableRef
{
    public sealed class StableRefMissingTypesWindow : EditorWindow
    {
        [MenuItem("Tools/StableRef/Fix Missing Types", priority = 200)]
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

        private const float SearchW = 220f;

        private sealed class Node : StableRefResultTree.Node
        {
            public GlobalObjectId ObjectId;
            public string AssetPath;
            public bool IsSceneObject;
        }

        private List<StableRefResultTree.Node> _roots;
        private StableRefResultTree _tree;
        private string _filter = "";
        private SearchField _searchField;
        private bool _hasScanned;
        private bool _showDomainReloadHint;

        private bool HasAnyResults => _roots != null && _roots.Any(r => r.Children.Count > 0);

        private static Texture ScriptIcon => StableRefEditorUtility.Icon("cs Script Icon").image;

        private void OnEnable()
        {
            _roots = null;
            _scanEntries.Clear();
            _hasScanned = false;
            _showDomainReloadHint = false;
            _filter = "";
            _searchField = new SearchField();
            _tree = new StableRefResultTree
            {
                Clicked = (node, _) =>
                {
                    if (node.PingTarget != null) EditorGUIUtility.PingObject(node.PingTarget);
                },
                EmptyGroupText = "No missing types found"
            };
        }

        private void Update()
        {
            if (_tree.ApplyTypedFilter()) Repaint();
        }

        private void OnGUI()
        {
            if (_hasScanned && _roots != null && _tree.HandleKeyboard(_searchField.HasFocus(), _filter.Length > 0))
                Repaint();

            DrawToolbar();

            if (!_hasScanned || _roots == null)
            {
                EditorGUILayout.HelpBox(
                    "Press \"Scan Project\" to find objects with missing StableRef types.",
                    MessageType.Info);
                return;
            }

            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _tree.OnGUI(rect);

            if (!HasAnyResults) DrawTips();

            DrawFooter();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string next = _searchField.OnToolbarGUI(_filter, GUILayout.Width(SearchW)) ?? "";
                if (next != _filter)
                {
                    _filter = next;
                    if (_filter.Length == 0) _tree.Filter = "";
                    else _tree.SetFilterDelayed(_filter);
                    Repaint();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Scan Project", EditorStyles.toolbarButton, GUILayout.Width(90f)))
                    DoScan();
            }
        }

        private static void DrawTips()
        {
            EditorGUILayout.LabelField(
                "Tip: only assets and scenes that are currently open are scanned —\n" +
                "open the scenes you want checked, then re-run the scan.",
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
            using (new EditorGUI.DisabledScope(!HasAnyResults))
            {
                if (GUILayout.Button("Fix All Missings", GUILayout.Width(160), GUILayout.Height(24)))
                    DoFixAll();
            }
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private struct GroupSeg
        {
            public string Label;
            public bool IsValue;
        }

        private struct MissingRefInfo
        {
            public GroupSeg[] GroupPath;
            public string Label;
        }

        private struct ScanEntry
        {
            public UnityEngine.Object Target;
            public GlobalObjectId ObjectId;
            public string AssetPath;
            public bool IsSceneObject;
            public List<MissingRefInfo> MissingRefs;
            public string TypeName;
            public string Label;
            public List<string> GoNameChain;
            public bool SceneLoaded;
            public string SceneName;
        }

        private readonly List<ScanEntry> _scanEntries = new();
        private readonly HashSet<GlobalObjectId> _scanSeen = new();

        internal void DoScan()
        {
            _scanEntries.Clear();
            _scanSeen.Clear();
            _hasScanned = true;
            _showDomainReloadHint = false;

            StableRefEditorUtility.ScanProject("Scanning assets…", source =>
            {
                foreach (var target in source.Targets)
                    CollectMissing(target, source.Path, source.IsScene, sceneLoaded: source.IsScene,
                        sceneName: source.IsScene ? source.Scene.name : null);
            });

            BuildTree();
            EditorUtility.UnloadUnusedAssetsImmediate();
            Repaint();
        }

        private void CollectMissing(UnityEngine.Object target, string assetPath, bool isScene,
            bool sceneLoaded = false, string sceneName = null)
        {
            if (target is not MonoBehaviour && target is not ScriptableObject) return;
            if (!StableRefPropertyUtils.MayContainStableRef(target.GetType())
                && !SerializationUtility.HasManagedReferencesWithMissingTypes(target)) return;

            var missingRefs = CollectMissingRefs(target);
            if (missingRefs.Count == 0) return;

            var objectId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if (!_scanSeen.Add(objectId)) return;

            List<string> goChain = null;
            if (target is Component comp)
            {
                goChain = new List<string>();
                for (var t = comp.transform; t != null; t = t.parent)
                    goChain.Add(t.gameObject.name);
                goChain.Reverse();
            }

            _scanEntries.Add(new ScanEntry
            {
                Target = target,
                ObjectId = objectId,
                AssetPath = assetPath,
                IsSceneObject = isScene,
                MissingRefs = missingRefs,
                TypeName = target.GetType().Name,
                Label = StableRefEditorUtility.ScanTargetLabel(target),
                GoNameChain = goChain,
                SceneLoaded = sceneLoaded,
                SceneName = sceneName
            });
        }

        private static List<MissingRefInfo> CollectMissingRefs(UnityEngine.Object target)
        {
            var result = new List<MissingRefInfo>();
            var so = new SerializedObject(target);
            so.Update();
            var iter = so.GetIterator();
            bool enter = true;

            while (iter.Next(enter))
            {
                if (TryGetMissingWrapper(so, iter, out var wrapperProp))
                {
                    var typeIdProp = wrapperProp.FindPropertyRelative(StableRefEntry.TypeIdFieldName);
                    var dispProp = wrapperProp.FindPropertyRelative(StableRefEntry.TypeDisplayNameFieldName);
                    var recoveredType = StableRefTypeRegistry.GetType(typeIdProp.stringValue);

                    result.Add(new MissingRefInfo
                    {
                        GroupPath = GetFieldGroupPath(so, wrapperProp.propertyPath),
                        Label = StableRefEntry.BuildMissingLabelText(dispProp?.stringValue, recoveredType)
                    });
                }

                enter = iter.propertyType == SerializedPropertyType.ManagedReference
                    ? StableRefPropertyUtils.HasManagedValue(iter)
                    : StableRefPropertyUtils.MayHoldEntries(iter);
            }

            return result;
        }

        private static GroupSeg[] GetFieldGroupPath(SerializedObject so, string wrapperPath)
        {
            if (string.IsNullOrEmpty(wrapperPath)) return Array.Empty<GroupSeg>();

            var parts = new List<GroupSeg>();
            var run = new List<string>();
            var segs = wrapperPath.Split('.');
            string accumulated = "";

            for (int i = 0; i < segs.Length; i++)
            {
                string seg = segs[i];
                accumulated = i == 0 ? seg : accumulated + "." + seg;

                int bracket = seg.IndexOf('[');
                string baseName = bracket >= 0 ? seg.Substring(0, bracket) : seg;
                if (baseName == StableRefEntry.ListItemsFieldName || baseName == "Array" || baseName == "data") continue;

                var prop = so.FindProperty(accumulated);
                if (prop == null) continue;

                if (prop.propertyType == SerializedPropertyType.ManagedReference)
                {
                    if (run.Count > 0) { parts.Add(new GroupSeg { Label = string.Join("/", run) }); run.Clear(); }
                    var v = prop.managedReferenceValue;
                    if (v != null) parts.Add(new GroupSeg { Label = StableRefEditorUtility.ValueLabelPrefix + StableRefGenericUtils.DisplayName(v.GetType()), IsValue = true });
                    continue;
                }

                run.Add(prop.displayName);
            }

            if (run.Count > 0) parts.Add(new GroupSeg { Label = string.Join("/", run) });
            return parts.ToArray();
        }

        private void BuildTree()
        {
            _roots = new List<StableRefResultTree.Node>();

            var prefabGroup = new Node { Kind = NodeKind.Group, Label = "Prefabs", Expanded = true };
            var sceneGroup  = new Node { Kind = NodeKind.Group, Label = "Active Scenes", Expanded = true };
            var soGroup     = new Node { Kind = NodeKind.Group, Label = "Scriptable Objects", Expanded = true };

            foreach (var assetGroup in _scanEntries.GroupBy(e => e.AssetPath).OrderBy(g => g.Key))
            {
                var assetPath = assetGroup.Key;
                var firstEntry = assetGroup.First();
                bool isScene = firstEntry.IsSceneObject;

                Node targetGroup;
                if (isScene)
                    targetGroup = sceneGroup;
                else if (firstEntry.GoNameChain == null)
                    targetGroup = soGroup;
                else
                    targetGroup = prefabGroup;

                var assetObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);

                var assetNode = new Node
                {
                    Kind = NodeKind.Asset,
                    Label = string.IsNullOrEmpty(assetPath)
                        ? (string.IsNullOrEmpty(firstEntry.SceneName) ? "Untitled" : firstEntry.SceneName)
                        : System.IO.Path.GetFileNameWithoutExtension(assetPath),
                    Icon = isScene
                        ? StableRefEditorUtility.Icon("SceneAsset Icon").image
                        : AssetDatabase.GetCachedIcon(assetPath),
                    PingTarget = assetObject,
                    AssetPath = assetPath
                };

                foreach (var entry in assetGroup)
                {
                    if (entry.GoNameChain == null)
                    {
                        var compNode = new Node
                        {
                            Kind = NodeKind.Component,
                            Label = entry.Label,
                            Icon = ScriptIcon,
                            PingTarget = entry.Target,
                            ObjectId = entry.ObjectId,
                            AssetPath = assetPath,
                            IsSceneObject = false
                        };
                        AddMissingRefNodes(compNode, entry.MissingRefs, entry.Target);
                        assetNode.Children.Add(compNode);
                    }
                    else
                    {
                        bool liveTarget = !entry.IsSceneObject
                                          || (entry.SceneLoaded && entry.Target != null);
                        if (liveTarget)
                            InsertComponentIntoHierarchy(assetNode, (Component)entry.Target, entry, assetPath);
                        else
                            InsertSceneComponentIntoHierarchy(assetNode, entry, assetPath, assetObject);
                    }
                }

                if (assetNode.Children.Count > 0)
                    targetGroup.Children.Add(assetNode);
            }

            _roots.Add(prefabGroup);
            _roots.Add(sceneGroup);
            _roots.Add(soGroup);
            _tree.SetRoots(_roots);
        }

        private static void AddMissingRefNodes(Node compNode, List<MissingRefInfo> missingRefs, UnityEngine.Object pingTarget)
        {
            var scriptIcon = ScriptIcon;
            var groupNodes = new Dictionary<string, Node>();

            foreach (var r in missingRefs)
            {
                Node parent = compNode;
                string key = "";

                if (r.GroupPath != null)
                {
                    for (int i = 0; i < r.GroupPath.Length; i++)
                    {
                        var seg = r.GroupPath[i];
                        key = key.Length == 0 ? seg.Label : key + "/" + seg.Label;
                        if (!groupNodes.TryGetValue(key, out var groupNode))
                        {
                            groupNode = new Node
                            {
                                Kind = NodeKind.Item,
                                Label = seg.Label,
                                Icon = seg.IsValue ? scriptIcon : null,
                                PingTarget = pingTarget
                            };
                            groupNodes[key] = groupNode;
                            parent.Children.Add(groupNode);
                        }
                        parent = groupNode;
                    }
                }

                parent.Children.Add(new Node
                {
                    Kind = NodeKind.Item,
                    Label = StableRefEditorUtility.ValueLabelPrefix + r.Label,
                    Icon = StableRefEditorUtility.Icon("console.warnicon.sml").image,
                    Muted = true,
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

            StableRefResultTree.Node current = assetNode;
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
                Icon = ScriptIcon,
                PingTarget = comp,
                ObjectId = entry.ObjectId,
                AssetPath = assetPath,
                IsSceneObject = entry.IsSceneObject
            };
            AddMissingRefNodes(compNode, entry.MissingRefs, comp);
            current.Children.Add(compNode);
        }

        private static void InsertSceneComponentIntoHierarchy(
            Node assetNode, ScanEntry entry, string assetPath, UnityEngine.Object sceneAsset)
        {
            StableRefResultTree.Node current = assetNode;
            var chain = entry.GoNameChain;
            for (int i = 0; i < chain.Count; i++)
            {
                string goName = chain[i];
                var existing = current.Children.FirstOrDefault(
                    n => n.Kind == NodeKind.GameObject && n.Label == goName);

                if (existing == null)
                {
                    existing = new Node
                    {
                        Kind = NodeKind.GameObject,
                        Label = goName,
                        Icon = StableRefEditorUtility.GoIcon,
                        PingTarget = sceneAsset
                    };
                    current.Children.Add(existing);
                }
                current = existing;
            }

            var compNode = new Node
            {
                Kind = NodeKind.Component,
                Label = entry.TypeName,
                Icon = ScriptIcon,
                PingTarget = sceneAsset,
                ObjectId = entry.ObjectId,
                AssetPath = assetPath,
                IsSceneObject = true
            };
            AddMissingRefNodes(compNode, entry.MissingRefs, sceneAsset);
            current.Children.Add(compNode);
        }

        private void DoFixAll()
        {
            var components = new List<Node>();
            foreach (var root in _roots) CollectComponents((Node)root, components);

            var seen = new HashSet<GlobalObjectId>();
            var entries = components.Where(n => seen.Add(n.ObjectId)).ToList();

            int fixedCount = 0;
            int unresolvedCount = 0;
            var fixedPaths = new HashSet<string>();
            var fixedAssets = new List<(UnityEngine.Object Asset, string Path, int Count)>();

            var assetEntries = entries.Where(n => !n.IsSceneObject).ToList();
            try
            {
                for (int i = 0; i < assetEntries.Count; i++)
                {
                    var node = assetEntries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Fixing assets…",
                            node.AssetPath, (float)i / assetEntries.Count))
                        break;

                    var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                    if (target == null) continue;

                    int fixedHere = FixTarget(target, out int unresolved);
                    fixedCount += fixedHere;
                    unresolvedCount += unresolved;
                    if (fixedHere > 0)
                    {
                        fixedPaths.Add(node.AssetPath);
                        fixedAssets.Add((target, node.AssetPath, fixedHere));
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            var unsavedAssets = new List<string>();
            foreach (var (asset, path, count) in fixedAssets)
            {
                if (StableRefEditorUtility.TrySaveAsset(asset)) continue;
                fixedCount -= count;
                if (!unsavedAssets.Contains(path)) unsavedAssets.Add(path);
            }

            var byScene = entries.Where(n => n.IsSceneObject).GroupBy(n => n.AssetPath).ToList();
            int skippedScenes = 0;
            try
            {
                for (int si = 0; si < byScene.Count; si++)
                {
                    var sceneGroup = byScene[si];
                    var scenePath = sceneGroup.Key;
                    if (EditorUtility.DisplayCancelableProgressBar("Fixing scenes…",
                            scenePath, (float)si / byScene.Count))
                        break;

                    var scene = SceneManager.GetSceneByPath(scenePath);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        skippedScenes++;
                        continue;
                    }

                    int fixedInScene = 0;
                    foreach (var node in sceneGroup)
                    {
                        var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                        if (target == null) continue;
                        fixedInScene += FixTarget(target, out int unresolved);
                        unresolvedCount += unresolved;
                    }

                    if (fixedInScene > 0)
                    {
                        fixedCount += fixedInScene;
                        EditorSceneManager.MarkSceneDirty(scene);
                        fixedPaths.Add(scenePath);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (fixedPaths.Count > 0)
                AssetDatabase.Refresh();

            Debug.Log($"[StableRef] Fixed {fixedCount} missing reference{(fixedCount != 1 ? "s" : "")}. " +
                      "Fixed scenes are marked dirty — save them to keep the changes.");

            if (unsavedAssets.Count > 0)
            {
                Debug.LogWarning(
                    "[StableRef] Unity didn't save these assets, so their fixes were not written — a prefab with a missing " +
                    "script can't be saved; fix the asset and run Fix All again:\n" + string.Join("\n", unsavedAssets));
            }

            if (unresolvedCount > 0)
            {
                Debug.LogWarning(
                    $"[StableRef] Skipped {unresolvedCount} entr{(unresolvedCount != 1 ? "ies" : "y")} whose stable id " +
                    "no longer resolves to a type. Their recovery data was preserved — restore the type (or its " +
                    "[RefTypeId]) and re-run Fix All, or pick None / another type in the field's selector (or delete the element) to discard.");
            }

            if (skippedScenes > 0)
            {
                Debug.LogWarning(
                    $"[StableRef] Missing references in {skippedScenes} closed scene{(skippedScenes != 1 ? "s" : "")} " +
                    $"weren't fixed — open {(skippedScenes != 1 ? "them" : "it")} and re-scan to fix.");
            }

            EditorApplication.delayCall += () =>
            {
                DoScan();
                _showDomainReloadHint = HasAnyResults;
                Repaint();
            };
        }

        private static void CollectComponents(Node node, List<Node> result)
        {
            if (node.Kind == NodeKind.Component)
                result.Add(node);
            foreach (var c in node.Children)
                CollectComponents((Node)c, result);
        }

        private const int MaxFixPasses = 8;

        /// <summary>
        /// Recreates every missing StableRef entry on <paramref name="target"/> (optionally only under
        /// <paramref name="pathPrefix"/>) from its stored id and snapshot. Runs in passes until a fixed
        /// point: a recreated value can itself contain missing StableRef entries — their metadata is
        /// restored by the snapshot on the pass that recreates the parent, and the next pass recreates
        /// them. Entries whose id no longer resolves are skipped and counted in
        /// <paramref name="unresolved"/>; their recovery data (and the target's native missing-type
        /// data) is preserved so they stay fixable once the type is back.
        /// </summary>
        internal static int FixTarget(UnityEngine.Object target, out int unresolved, string pathPrefix = null)
        {
            int totalFixed = 0;
            unresolved = 0;
            var replacedIds = new List<long>();

            for (int pass = 0; pass < MaxFixPasses; pass++)
            {
                var so = new SerializedObject(target);
                so.Update();

                var wrapperPaths = CollectMissingWrapperPaths(so, pathPrefix);
                int fixedThisPass = 0;
                unresolved = 0;

                foreach (var path in wrapperPaths)
                {
                    var wrapper = so.FindProperty(path);
                    if (wrapper == null) continue;
                    long oldId = wrapper.FindPropertyRelative(StableRefEntry.ValueFieldName).managedReferenceId;
                    if (StableRefEntry.TryRecreate(wrapper))
                    {
                        fixedThisPass++;
                        replacedIds.Add(oldId);
                    }
                    else unresolved++;
                }

                if (fixedThisPass == 0) break;
                so.ApplyModifiedProperties();
                totalFixed += fixedThisPass;
            }

            if (totalFixed > 0)
            {
                StableRefEntry.ReleaseMissingData(target, replacedIds);
                EditorUtility.SetDirty(target);
            }

            return totalFixed;
        }

        private static List<string> CollectMissingWrapperPaths(SerializedObject so, string pathPrefix)
        {
            var result = new List<string>();
            var iter = so.GetIterator();
            bool enter = true;

            while (iter.Next(enter))
            {
                if (TryGetMissingWrapper(so, iter, out var wrapper))
                {
                    string path = wrapper.propertyPath;
                    if (pathPrefix == null
                        || path == pathPrefix
                        || path.StartsWith(pathPrefix + ".", StringComparison.Ordinal))
                        result.Add(path);
                }

                enter = iter.propertyType == SerializedPropertyType.ManagedReference
                    ? StableRefPropertyUtils.HasManagedValue(iter)
                    : StableRefPropertyUtils.MayHoldEntries(iter);
            }

            return result;
        }

        private static bool TryGetMissingWrapper(SerializedObject so, SerializedProperty valueIter, out SerializedProperty wrapper)
        {
            wrapper = null;
            if (valueIter.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (valueIter.name != StableRefEntry.ValueFieldName) return false;
            if (StableRefPropertyUtils.HasManagedValue(valueIter)) return false;

            string parentPath = ParentPath(valueIter.propertyPath);
            if (parentPath == null) return false;

            var candidate = so.FindProperty(parentPath);
            if (candidate == null) return false;

            var typeIdProp = candidate.FindPropertyRelative(StableRefEntry.TypeIdFieldName);
            if (typeIdProp == null || string.IsNullOrEmpty(typeIdProp.stringValue)) return false;
            if (candidate.FindPropertyRelative(StableRefEntry.ValuesDataFieldName) == null) return false;

            wrapper = candidate;
            return true;
        }

        private static string ParentPath(string propertyPath)
        {
            int lastDot = propertyPath.LastIndexOf('.');
            return lastDot > 0 ? propertyPath.Substring(0, lastDot) : null;
        }
    }
}
#endif