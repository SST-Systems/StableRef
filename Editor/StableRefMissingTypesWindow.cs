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
            /// <summary>A scene object whose references Unity lost in memory — the scene is read from disk again.</summary>
            public bool LostInMemory;
            /// <summary>Records of missing classes without data that Fix All clears (see <see cref="StableRefFileRepair.EmptyRecord"/>).</summary>
            public List<long> EmptyRecordRids;
        }

        /// <summary>A scanned file with records Unity can't read back; it is repaired before anything is fixed.</summary>
        private sealed class FileIssue
        {
            public List<StableRefYamlRecords.Unreadable> Records;
            public bool IsScene;
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
            _fileIssues.Clear();
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
            public bool LostInMemory;
            public List<long> EmptyRecordRids;
        }

        private readonly List<ScanEntry> _scanEntries = new();
        private readonly HashSet<GlobalObjectId> _scanSeen = new();
        private readonly Dictionary<string, FileIssue> _fileIssues = new();

        internal void DoScan()
        {
            _scanEntries.Clear();
            _scanSeen.Clear();
            _fileIssues.Clear();
            _hasScanned = true;
            _showDomainReloadHint = false;
            int reloaded = 0;

            StableRefEditorUtility.ScanProject("Scanning assets…", source =>
            {
                // The text is parsed only when it may hold an unreadable record or an object has something missing.
                StableRefYamlRecords.Document document = null;
                StableRefYamlRecords.Document Document() => document ??= StableRefFileRepair.ReadDocument(source.Path);

                if (source.HasRecordWithoutData)
                {
                    var unreadable = StableRefFileRepair.FindUnreadable(Document());
                    if (unreadable.Count > 0)
                    {
                        // Every reference of the file's objects reads as lost until the file is repaired.
                        _fileIssues[source.Path] = new FileIssue { Records = unreadable, IsScene = source.IsScene };
                        return;
                    }
                }

                foreach (var target in source.Targets)
                    CollectMissing(target, source.Path, source.IsScene, Document, ref reloaded,
                        sceneLoaded: source.IsScene, sceneName: source.IsScene ? source.Scene.name : null);
            });

            if (reloaded > 0)
                Debug.Log($"[StableRef] Read {reloaded} asset{(reloaded != 1 ? "s" : "")} from disk again: Unity had " +
                          "lost their references in memory after a domain reload (the files were intact).");

            BuildTree();
            EditorUtility.UnloadUnusedAssetsImmediate();
            Repaint();
        }

        private void CollectMissing(UnityEngine.Object target, string assetPath, bool isScene,
            Func<StableRefYamlRecords.Document> document, ref int reloaded, bool sceneLoaded = false, string sceneName = null)
        {
            if (target is not MonoBehaviour && target is not ScriptableObject) return;
            bool hasMissingData = SerializationUtility.HasManagedReferencesWithMissingTypes(target);
            if (!StableRefPropertyUtils.MayContainStableRef(target.GetType()) && !hasMissingData) return;

            var emptyRecords = hasMissingData
                ? StableRefFileRepair.FindEmptyMissingRecords(target, document())
                : new List<StableRefFileRepair.EmptyRecord>();
            var missingRefs = CollectMissingRefs(target, emptyRecords);
            if (missingRefs.Count == 0) return;

            bool lost = !hasMissingData && StableRefFileRepair.IsLostInMemory(target, document());
            if (lost && !isScene)
            {
                var fresh = StableRefFileRepair.ReloadAsset(target);
                lost = false;
                if (fresh != null)
                {
                    reloaded++;
                    target = fresh;
                    missingRefs = CollectMissingRefs(target);
                    if (missingRefs.Count == 0) return;
                }
            }

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
                SceneName = sceneName,
                LostInMemory = lost,
                EmptyRecordRids = emptyRecords.Count > 0 ? emptyRecords.Select(r => r.Rid).ToList() : null
            });
        }

        /// <summary>
        /// The missing entries of <paramref name="target"/>, plus an entry without a stable id that points at one of
        /// <paramref name="emptyRecords"/> (it isn't missing otherwise; Fix All sets it to None).
        /// </summary>
        private static List<MissingRefInfo> CollectMissingRefs(UnityEngine.Object target,
            List<StableRefFileRepair.EmptyRecord> emptyRecords = null)
        {
            var result = new List<MissingRefInfo>();
            var so = new SerializedObject(target);
            so.Update();

            // YAML field path → class name of the empty record the entry there points at.
            Dictionary<string, string> emptyByPath = null;
            if (emptyRecords != null)
                foreach (var record in emptyRecords)
                foreach (var field in record.Fields)
                    if (field.IsEntry)
                        (emptyByPath ??= new Dictionary<string, string>())[field.Path] = record.ClassName;

            var iter = so.GetIterator();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (TryGetEmptyEntry(so, iter, emptyByPath != null, out var wrapperProp, out string typeId))
                {
                    string emptyClass = null;
                    if (emptyByPath != null)
                    {
                        string yamlPath = StableRefFileRepair.YamlFieldPath(so, wrapperProp.propertyPath);
                        if (emptyByPath.TryGetValue(yamlPath, out emptyClass)) emptyByPath.Remove(yamlPath);
                    }

                    if (!string.IsNullOrEmpty(typeId) || emptyClass != null)
                    {
                        var dispProp = wrapperProp.FindPropertyRelative(StableRefEntry.TypeDisplayNameFieldName);
                        string lost = string.IsNullOrEmpty(dispProp?.stringValue) ? emptyClass : dispProp.stringValue;
                        result.Add(new MissingRefInfo
                        {
                            GroupPath = GetFieldGroupPath(so, wrapperProp.propertyPath),
                            Label = emptyClass != null
                                ? BrokenLabel(lost)
                                : StableRefEntry.BuildMissingLabelText(lost, StableRefTypeRegistry.GetType(typeId))
                        });
                    }
                }

                enter = iter.propertyType == SerializedPropertyType.ManagedReference
                    ? StableRefPropertyUtils.HasManagedValue(iter)
                    : StableRefPropertyUtils.MayHoldEntries(iter);
            }

            // Fields whose path couldn't be matched to a property are still listed, under their YAML path.
            if (emptyByPath != null)
                foreach (var pair in emptyByPath)
                    result.Add(new MissingRefInfo
                    {
                        GroupPath = YamlGroupPath(pair.Key),
                        Label = BrokenLabel(pair.Value)
                    });

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

            var byPath = _scanEntries.GroupBy(e => e.AssetPath).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var assetPath in byPath.Keys.Concat(_fileIssues.Keys).Distinct().OrderBy(p => p))
            {
                byPath.TryGetValue(assetPath, out var assetEntries);
                _fileIssues.TryGetValue(assetPath, out var issue);
                var firstEntry = assetEntries?[0] ?? default;
                bool isScene = issue?.IsScene ?? firstEntry.IsSceneObject;

                Node targetGroup;
                if (isScene)
                    targetGroup = sceneGroup;
                else if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    targetGroup = prefabGroup;
                else
                    targetGroup = soGroup;

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
                    AssetPath = assetPath,
                    IsSceneObject = isScene
                };

                if (issue != null)
                    AddFieldNodes(assetNode, UnreadableRefs(issue.Records), assetObject);

                foreach (var entry in assetEntries ?? Enumerable.Empty<ScanEntry>())
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
                        AddMissingRefNodes(compNode, entry, entry.Target);
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

        /// <summary>The fields pointing at records Unity can't read, each in the branch of its field.</summary>
        private static IEnumerable<MissingRefInfo> UnreadableRefs(List<StableRefYamlRecords.Unreadable> records)
        {
            foreach (var record in records)
            {
                if (record.Fields.Count == 0)
                    yield return new MissingRefInfo { Label = BrokenLabel(record.ClassName) };

                foreach (var field in record.Fields)
                    yield return new MissingRefInfo { GroupPath = YamlGroupPath(field.Path), Label = BrokenLabel(record.ClassName) };
            }
        }

        private static string BrokenLabel(string className) => StableRefFileRepair.BrokenLabelText(className);

        /// <summary>
        /// A YAML field path (<c>Reference.Condition</c>, <c>List[2]</c>) as a field group like
        /// <see cref="GetFieldGroupPath"/> builds — display names, list indices left out.
        /// </summary>
        private static GroupSeg[] YamlGroupPath(string yamlPath)
        {
            if (string.IsNullOrEmpty(yamlPath)) return Array.Empty<GroupSeg>();
            var names = yamlPath.Split('.').Select(s =>
            {
                int bracket = s.IndexOf('[');
                return ObjectNames.NicifyVariableName(bracket >= 0 ? s.Substring(0, bracket) : s);
            });
            return new[] { new GroupSeg { Label = string.Join("/", names) } };
        }

        private static void AddMissingRefNodes(Node compNode, ScanEntry entry, UnityEngine.Object pingTarget)
        {
            if (entry.LostInMemory)
            {
                compNode.LostInMemory = true;
                compNode.Children.Add(new Node
                {
                    Kind = NodeKind.Item,
                    Label = "References lost in memory after a domain reload (the scene file is intact) — " +
                            "Fix All reads the scene from disk again",
                    Icon = StableRefEditorUtility.Icon("console.warnicon.sml").image,
                    PingTarget = pingTarget
                });
                return;
            }

            compNode.EmptyRecordRids = entry.EmptyRecordRids;
            AddFieldNodes(compNode, entry.MissingRefs, pingTarget);
        }

        /// <summary>The missing refs under <paramref name="root"/>, each in the branch of its field.</summary>
        private static void AddFieldNodes(Node root, IEnumerable<MissingRefInfo> refs, UnityEngine.Object pingTarget)
        {
            var scriptIcon = ScriptIcon;
            var groupNodes = new Dictionary<string, Node>();

            foreach (var r in refs)
            {
                Node parent = root;
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
            AddMissingRefNodes(compNode, entry, comp);
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
            AddMissingRefNodes(compNode, entry, sceneAsset);
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
            int repairedFiles = 0;
            var fixedPaths = new HashSet<string>();
            var fixedAssets = new List<(UnityEngine.Object Asset, string Path, int Count, int Cleared)>();
            var notRepaired = new List<string>();

            var reloadScenes = _fileIssues.Where(p => p.Value.IsScene).Select(p => p.Key)
                .Concat(entries.Where(n => n.IsSceneObject && n.LostInMemory).Select(n => n.AssetPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            bool reload = reloadScenes.Count > 0 && StableRefFileRepair.ConfirmSceneReload(reloadScenes);

            int clearedCount = 0;

            int FixAndClear(UnityEngine.Object target, Node node, out int cleared)
            {
                int fixedHere = FixTarget(target, out int unresolved);
                unresolvedCount += unresolved;
                cleared = node != null ? StableRefFileRepair.ClearEmptyRecords(target, node.EmptyRecordRids) : 0;
                clearedCount += cleared;
                fixedCount += fixedHere;
                return fixedHere;
            }

            void FixAsset(UnityEngine.Object target, string path, Node node)
            {
                int fixedHere = FixAndClear(target, node, out int cleared);
                if (fixedHere + cleared == 0) return;
                fixedPaths.Add(path);
                fixedAssets.Add((target, path, fixedHere, cleared));
            }

            var repairPaths = _fileIssues.Where(p => !p.Value.IsScene).Select(p => p.Key).ToList();
            var assetEntries = entries.Where(n => !n.IsSceneObject).ToList();
            try
            {
                for (int i = 0; i < repairPaths.Count; i++)
                {
                    var path = repairPaths[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Repairing files…", path, (float)i / repairPaths.Count))
                        break;

                    var result = StableRefFileRepair.RepairAsset(path);
                    if (result == StableRefFileRepair.RepairResult.Unsaved)
                    {
                        notRepaired.Add(path + " (has unsaved changes — save or revert it first)");
                        continue;
                    }
                    if (result == StableRefFileRepair.RepairResult.Failed)
                    {
                        notRepaired.Add(path + " (couldn't be read or written)");
                        continue;
                    }

                    if (result == StableRefFileRepair.RepairResult.Repaired) repairedFiles++;
                    foreach (var target in StableRefEditorUtility.LoadScanTargets(path))
                        if (target is MonoBehaviour || target is ScriptableObject)
                            FixAsset(target, path, null);
                }

                for (int i = 0; i < assetEntries.Count; i++)
                {
                    var node = assetEntries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Fixing assets…",
                            node.AssetPath, (float)i / assetEntries.Count))
                        break;

                    var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                    if (target != null) FixAsset(target, node.AssetPath, node);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            var unsavedAssets = new List<string>();
            var unsafeAssets = new List<string>();
            foreach (var (asset, path, count, cleared) in fixedAssets)
            {
                if (StableRefFileRepair.HoldsEmptyMissingRecord(asset))
                {
                    fixedCount -= count;
                    clearedCount -= cleared;
                    if (!unsafeAssets.Contains(path)) unsafeAssets.Add(path);
                    continue;
                }
                if (StableRefEditorUtility.TrySaveAsset(asset)) continue;
                fixedCount -= count;
                clearedCount -= cleared;
                if (!unsavedAssets.Contains(path)) unsavedAssets.Add(path);
            }

            if (reload)
            {
                StableRefFileRepair.ReloadScenes(reloadScenes, path =>
                {
                    if (_fileIssues.TryGetValue(path, out var issue) && issue.IsScene)
                        StableRefFileRepair.RepairClosedScene(path);
                });

                foreach (var scenePath in reloadScenes)
                {
                    var scene = SceneManager.GetSceneByPath(scenePath);
                    if (!scene.IsValid() || !scene.isLoaded) continue;

                    int fixedInScene = 0;
                    foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null || !StableRefPropertyUtils.MayContainStableRef(component.GetType())) continue;
                        fixedInScene += FixTarget(component, out int unresolved);
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

            var byScene = entries.Where(n => n.IsSceneObject && !reloadScenes.Contains(n.AssetPath))
                .GroupBy(n => n.AssetPath).ToList();
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

                    int changedInScene = 0;
                    foreach (var node in sceneGroup)
                    {
                        var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(node.ObjectId);
                        if (target == null) continue;
                        changedInScene += FixAndClear(target, node, out int cleared) + cleared;
                    }

                    if (changedInScene > 0)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        fixedPaths.Add(scenePath);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (fixedPaths.Count > 0)
                AssetDatabase.Refresh();

            Debug.Log($"[StableRef] Fixed {fixedCount} missing reference{(fixedCount != 1 ? "s" : "")}" +
                      (repairedFiles > 0 ? $", repaired {repairedFiles} unreadable file{(repairedFiles != 1 ? "s" : "")}" : "") +
                      (reload ? $", read {reloadScenes.Count} scene{(reloadScenes.Count != 1 ? "s" : "")} from disk again" : "") +
                      (clearedCount > 0 ? $", set {clearedCount} missing reference{(clearedCount != 1 ? "s" : "")} without data to None" : "") +                      ". Fixed scenes are marked dirty — save them to keep the changes.");
            if (notRepaired.Count > 0)
            {
                Debug.LogWarning(
                    "[StableRef] These files have records Unity can't read and were left as they are:\n" +
                    string.Join("\n", notRepaired));
            }

            if (unsavedAssets.Count > 0)
            {
                Debug.LogWarning(
                    "[StableRef] Unity didn't save these assets, so their fixes were not written — a prefab with a missing " +
                    "script can't be saved; fix the asset and run Fix All again:\n" + string.Join("\n", unsavedAssets));
            }

            if (unsafeAssets.Count > 0)
            {
                Debug.LogWarning(
                    "[StableRef] These assets were fixed in memory but not saved: they still hold records of missing " +
                    "classes without data (of plain [SerializeReference] fields), and saving them would make their files " +
                    "unreadable — bring the classes back or set those fields to None, then save:\n" +
                    string.Join("\n", unsafeAssets));
            }

            if (unresolvedCount > 0)
            {
                Debug.LogWarning(
                    $"[StableRef] Skipped {unresolvedCount} entr{(unresolvedCount != 1 ? "ies" : "y")} whose stable id " +
                    "no longer resolves to a type. Their recovery data was preserved — restore the type (or its " +
                    "[RefTypeId]) and re-run Fix All, or pick None / another type in the field's selector (or delete the element) to discard.");
            }

            if (reloadScenes.Count > 0 && !reload)
            {
                Debug.LogWarning(
                    "[StableRef] These scenes were not read from disk again, so they were left as they are — don't save " +
                    "them (that would write the lost state); reopen them or run Fix All again:\n" +
                    string.Join("\n", reloadScenes));
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
            => TryGetEmptyEntry(so, valueIter, false, out wrapper, out _);

        /// <summary>
        /// The entry whose <c>Value</c> is <paramref name="valueIter"/>, when that value is null — only with a stable id
        /// unless <paramref name="withoutId"/>.
        /// </summary>
        private static bool TryGetEmptyEntry(SerializedObject so, SerializedProperty valueIter, bool withoutId,
            out SerializedProperty wrapper, out string typeId)
        {
            wrapper = null;
            typeId = null;
            if (valueIter.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (valueIter.name != StableRefEntry.ValueFieldName) return false;
            if (StableRefPropertyUtils.HasManagedValue(valueIter)) return false;

            string parentPath = ParentPath(valueIter.propertyPath);
            if (parentPath == null) return false;

            var candidate = so.FindProperty(parentPath);
            if (candidate == null) return false;

            var typeIdProp = candidate.FindPropertyRelative(StableRefEntry.TypeIdFieldName);
            if (typeIdProp == null) return false;
            typeId = typeIdProp.stringValue;
            if (!withoutId && string.IsNullOrEmpty(typeId)) return false;
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