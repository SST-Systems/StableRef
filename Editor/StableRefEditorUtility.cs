#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SST.StableRef
{
    /// <summary>
    /// Shared editor helpers behind the StableRef inspector and its tool windows: label building, skin-correct icons,
    /// and finding the script that declares a value type.
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

        public static string BuildFieldDisplayPath(SerializedObject so, string propertyPath)
            => BuildFieldDisplayPath(so, propertyPath, null);

        /// <summary>
        /// <see cref="BuildFieldDisplayPath(SerializedObject, string)"/> that remembers the display name of every path
        /// prefix in <paramref name="displayNames"/> — entries of one object share most of their prefixes.
        /// </summary>
        internal static string BuildFieldDisplayPath(SerializedObject so, string propertyPath,
            Dictionary<string, string> displayNames)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;

            var parts = propertyPath.Split('.');
            var display = new List<string>(parts.Length);
            string accumulated = "";

            for (int i = 0; i < parts.Length; i++)
            {
                accumulated = i == 0 ? parts[i] : accumulated + "." + parts[i];
                if (displayNames == null || !displayNames.TryGetValue(accumulated, out var name))
                {
                    name = so.FindProperty(accumulated)?.displayName;
                    if (displayNames != null) displayNames[accumulated] = name;
                }
                if (name != null) display.Add(name);
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

        private static readonly Dictionary<Type, string> _scriptPathCache = new();
        private static readonly Dictionary<Type, string> _declaringFileNames = new();
        private static readonly Dictionary<Type, string> _declaringFileSearchNames = new();
        private static readonly Dictionary<System.Reflection.Assembly, string> _assemblyNames = new();
        private static readonly Dictionary<string, Task<SourceIndex>> _sourceIndex = new();
        private static Dictionary<string, string[]> _sourceFilesByAssembly;
        private static readonly Regex DeclarationRegex =
            new(@"\b(?:class|struct|interface|record)\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex NamespaceRegex = new(@"\bnamespace\s+([\w.]+)", RegexOptions.Compiled);

        /// <summary>
        /// Pings the script that declares <paramref name="type"/> (see <see cref="FindScript"/>); does nothing when there
        /// is none.
        /// </summary>
        public static void PingScript(Type type)
        {
            var script = FindScript(type);
            if (script != null) EditorGUIUtility.PingObject(script);
        }

        /// <summary>
        /// The script that declares <paramref name="type"/>, or <see langword="null"/> when it has no source in the
        /// project (a precompiled DLL, a dynamic type).
        /// </summary>
        /// <remarks>
        /// First the file named after the class — for a nested type the outermost one, for a generic one without the
        /// <c>`N</c> suffix. Otherwise the source files of the type's assembly are searched for its declaration, so
        /// classes in a differently named file and several classes per file are found too; when several files declare
        /// the name, the one with the type's namespace wins. The declaration index is built once per assembly (the first
        /// lookup in a large assembly takes a moment); results are cached per type until the next domain reload.
        /// </remarks>
        public static MonoScript FindScript(Type type)
        {
            if (type == null) return null;
            if (_scriptPathCache.TryGetValue(type, out var cachedPath))
                return cachedPath != null ? AssetDatabase.LoadAssetAtPath<MonoScript>(cachedPath) : null;

            var script = FindScriptByFileName(type) ?? FindScriptInAssemblySources(type);
            _scriptPathCache[type] = script != null ? AssetDatabase.GetAssetPath(script) : null;
            return script;
        }

        /// <summary>The fast half of <see cref="FindScript"/>: only the file named after the (outermost) class.</summary>
        internal static MonoScript FindScriptByFileName(Type type)
        {
            if (type == null) return null;
            var root = DeclaringRoot(type);
            string name = StripArity(root.Name);

            MonoScript byText = null;
            foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {name}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != name) continue;

                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms == null) continue;

                var cls = ms.GetClass();
                if (cls == root) return ms;
                if (byText == null && cls == null && Declares(ms.text, name) && DeclaresNamespace(ms.text, root.Namespace))
                    byText = ms;
            }
            return byText;
        }

        private static MonoScript FindScriptInAssemblySources(Type type)
        {
            var index = RequestSourceIndex(CachedAssemblyName(type.Assembly), background: false);
            string file = index.IsFaulted ? null : index.Result?.FindFile(DeclaringRoot(type));
            if (file == null) return null;

            return AssetDatabase.LoadAssetAtPath<MonoScript>(file)
                   ?? AssetDatabase.LoadAssetAtPath<MonoScript>(FileUtil.GetLogicalPath(file));
        }

        /// <summary>
        /// Name (without extension) of the source file that declares <paramref name="type"/>, looked up only in the
        /// declaration index of its assembly — no asset database queries, so it is cheap enough for search filters.
        /// Returns <see langword="false"/> while that index is still being built in the background (the call starts
        /// it); then <paramref name="fileName"/> is <see langword="null"/> when the type has no source.
        /// </summary>
        internal static bool TryGetDeclaringFileName(Type type, out string fileName)
        {
            fileName = null;
            if (type == null) return true;
            if (_declaringFileNames.TryGetValue(type, out fileName)) return true;

            var index = RequestSourceIndex(CachedAssemblyName(type.Assembly), background: true);
            if (!index.IsCompleted) return false;

            string file = index.IsFaulted ? null : index.Result?.FindFile(DeclaringRoot(type));
            fileName = file != null ? Path.GetFileNameWithoutExtension(file) : null;
            _declaringFileNames[type] = fileName;
            return true;
        }

        /// <summary>
        /// <see cref="TryGetDeclaringFileName"/> in lower case, for search filters: one shared string per type, so
        /// matching many rows of a type allocates nothing.
        /// </summary>
        internal static bool TryGetDeclaringFileSearchName(Type type, out string lowerName)
        {
            lowerName = null;
            if (type == null) return true;
            if (_declaringFileSearchNames.TryGetValue(type, out lowerName)) return true;
            if (!TryGetDeclaringFileName(type, out var name)) return false;

            _declaringFileSearchNames[type] = lowerName = name?.ToLowerInvariant();
            return true;
        }

        /// <summary>
        /// Starts building the declaration index of the assembly that compiles <paramref name="script"/> in the
        /// background, or with <paramref name="wait"/> blocks until it is ready — for an explicit action whose result
        /// must not change a moment later (start it first so it is built while other work runs).
        /// </summary>
        internal static void RequestDeclaringIndex(MonoScript script, bool wait)
        {
            if (script == null) return;
            string assembly = Path.GetFileNameWithoutExtension(
                CompilationPipeline.GetAssemblyNameFromScriptPath(AssetDatabase.GetAssetPath(script)) ?? "");
            if (assembly.Length > 0) RequestSourceIndex(assembly, background: !wait);
        }

        /// <summary><c>assembly.GetName().Name</c>, cached — <c>GetName</c> builds a new object on every call.</summary>
        private static string CachedAssemblyName(System.Reflection.Assembly assembly)
        {
            if (!_assemblyNames.TryGetValue(assembly, out var name))
                _assemblyNames[assembly] = name = assembly.GetName().Name;
            return name;
        }

        /// <summary>
        /// Type names declared in the source files of one assembly, with the namespaces of each file. Built once per
        /// assembly (until the next domain reload) from <c>CompilationPipeline</c> source files without touching the
        /// Unity API, so it can be built on a worker thread.
        /// </summary>
        private sealed class SourceIndex
        {
            private readonly string[] _files;
            private readonly List<string>[] _namespaces;
            private readonly Dictionary<string, List<int>> _filesByName = new(StringComparer.Ordinal);

            public SourceIndex(string[] files, string[] physical, bool parallel)
            {
                _files = files;
                _namespaces = new List<string>[files.Length];
                var declared = new List<string>[files.Length];

                void Read(int i)
                {
                    string text;
                    try { text = File.ReadAllText(physical[i]); }
                    catch (Exception) { return; }

                    var names = new List<string>();
                    foreach (Match m in DeclarationRegex.Matches(text))
                        names.Add(m.Groups[1].Value);
                    var namespaces = new List<string>(1);
                    foreach (Match m in NamespaceRegex.Matches(text))
                        namespaces.Add(m.Groups[1].Value);
                    declared[i] = names;
                    _namespaces[i] = namespaces;
                }

                if (parallel) Parallel.For(0, files.Length, Read);
                else for (int i = 0; i < files.Length; i++) Read(i);

                for (int i = 0; i < files.Length; i++)
                {
                    if (declared[i] == null) continue;
                    foreach (var name in declared[i])
                    {
                        if (!_filesByName.TryGetValue(name, out var list)) _filesByName[name] = list = new List<int>(1);
                        if (list.Count == 0 || list[list.Count - 1] != i) list.Add(i);
                    }
                }
            }

            /// <summary>The file declaring <paramref name="root"/> (an outermost type); the one with its namespace wins.</summary>
            public string FindFile(Type root)
            {
                if (!_filesByName.TryGetValue(StripArity(root.Name), out var candidates)) return null;
                if (candidates.Count > 1 && !string.IsNullOrEmpty(root.Namespace))
                    foreach (int i in candidates)
                        if (_namespaces[i] != null && _namespaces[i].Contains(root.Namespace))
                            return _files[i];
                return _files[candidates[0]];
            }
        }

        /// <summary>
        /// The declaration index of <paramref name="assemblyName"/>: built in place (in parallel) unless
        /// <paramref name="background"/>, which starts it on a worker thread, reading one file at a time so the editor
        /// stays responsive. A synchronous request for an index being built in the background waits for it.
        /// </summary>
        private static Task<SourceIndex> RequestSourceIndex(string assemblyName, bool background)
        {
            if (_sourceIndex.TryGetValue(assemblyName, out var task))
            {
                if (!background && !task.IsCompleted)
                {
                    try { task.Wait(); }
                    catch (AggregateException) { }
                }
                return task;
            }

            if (_sourceFilesByAssembly == null)
            {
                _sourceFilesByAssembly = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var asm in CompilationPipeline.GetAssemblies())
                    _sourceFilesByAssembly[asm.name] = asm.sourceFiles;
            }

            if (!_sourceFilesByAssembly.TryGetValue(assemblyName, out var files))
                task = Task.FromResult<SourceIndex>(null);
            else
            {
                var physical = new string[files.Length];
                for (int i = 0; i < files.Length; i++)
                    physical[i] = FileUtil.GetPhysicalPath(files[i]);
                task = background
                    ? Task.Run(() => new SourceIndex(files, physical, parallel: false))
                    : Task.FromResult(new SourceIndex(files, physical, parallel: true));
            }

            _sourceIndex[assemblyName] = task;
            return task;
        }

        private static Type DeclaringRoot(Type type)
        {
            if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
            while (type.DeclaringType != null) type = type.DeclaringType;
            return type;
        }

        private static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        private static bool Declares(string source, string name)
        {
            foreach (Match m in DeclarationRegex.Matches(source))
                if (m.Groups[1].Value == name) return true;
            return false;
        }

        private static bool DeclaresNamespace(string source, string ns)
            => string.IsNullOrEmpty(ns) || Regex.IsMatch(source, $@"\bnamespace\s+{Regex.Escape(ns)}(?![\w.])");

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
        /// under <c>Assets/</c> that may hold StableRef entries (<see cref="FilterScanPaths"/>), then every loaded open
        /// scene, under a cancelable progress bar (canceling skips the remaining assets; the open scenes are still
        /// visited). Never opens, closes or saves anything and doesn't unload what it loaded — call
        /// <c>EditorUtility.UnloadUnusedAssetsImmediate</c> once done with the targets.
        /// </summary>
        internal static void ScanProject(string progressTitle, Action<ScanSource> visit)
        {
            var allPaths = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })
                .Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                .Distinct()
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToList();

            try
            {
                EditorUtility.DisplayProgressBar(progressTitle, "Looking for StableRef data…", 0f);
                var paths = FilterScanPaths(allPaths);

                for (int i = 0; i < paths.Count; i++)
                {
                    var path = paths[i];
                    if (EditorUtility.DisplayCancelableProgressBar(progressTitle, path, (float)i / paths.Count))
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

        private static readonly byte[] EntryMarker = Encoding.ASCII.GetBytes("TypeDisplayName:");
        private static readonly byte[] YamlHeader = Encoding.ASCII.GetBytes("%YAML");
        private static readonly byte[] UnityYamlTag = Encoding.ASCII.GetBytes("tag:unity3d.com");
        private static readonly byte[] SourcePrefabKey = Encoding.ASCII.GetBytes("m_SourcePrefab: {");
        private static readonly byte[] GuidKey = Encoding.ASCII.GetBytes("guid: ");
        private const int GuidLength = 32;
        private const int HeaderLength = 256;

        private struct FileCheck
        {
            /// <summary>The file has StableRef entries, or can't be checked as text (binary, unreadable).</summary>
            public bool MayHold;
            /// <summary>GUIDs of the prefabs instantiated in the file (nested prefabs, the base of a variant).</summary>
            public List<string> SourcePrefabs;
        }

        /// <summary>
        /// The asset files among <paramref name="paths"/> that may hold StableRef entries, in their order — checked as
        /// text before anything is loaded. A text-serialized file is kept when it contains an entry's
        /// <c>TypeDisplayName</c> key (every entry writes it, empty or not) or instantiates a prefab that is kept (an
        /// unmodified variant or nested prefab shows its source's entries); binary or unreadable files are always kept.
        /// Files are read in parallel without touching the Unity API.
        /// </summary>
        internal static List<string> FilterScanPaths(IReadOnlyList<string> paths)
        {
            var physical = new string[paths.Count];
            for (int i = 0; i < paths.Count; i++)
                physical[i] = FileUtil.GetPhysicalPath(paths[i]);

            var checks = new FileCheck[paths.Count];
            Parallel.For(0, paths.Count, i => checks[i] = CheckFile(physical[i]));

            var byGuid = new Dictionary<string, int>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
                byGuid[AssetDatabase.AssetPathToGUID(paths[i])] = i;

            var resolved = new Dictionary<string, bool>();
            var result = new List<string>();
            for (int i = 0; i < paths.Count; i++)
                if (MayHold(checks[i], byGuid, checks, resolved))
                    result.Add(paths[i]);
            return result;
        }

        private static bool MayHold(FileCheck check, Dictionary<string, int> byGuid, FileCheck[] checks,
            Dictionary<string, bool> resolved)
        {
            if (check.MayHold) return true;
            if (check.SourcePrefabs == null) return false;

            foreach (var guid in check.SourcePrefabs)
                if (SourcePrefabMayHold(guid, byGuid, checks, resolved))
                    return true;
            return false;
        }

        private static bool SourcePrefabMayHold(string guid, Dictionary<string, int> byGuid, FileCheck[] checks,
            Dictionary<string, bool> resolved)
        {
            if (resolved.TryGetValue(guid, out bool known)) return known;
            resolved[guid] = false;

            // Model files and other imported prefabs carry no serialized StableRef data of their own.
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return false;

            var check = byGuid.TryGetValue(guid, out int index)
                ? checks[index]
                : CheckFile(FileUtil.GetPhysicalPath(path));
            return resolved[guid] = MayHold(check, byGuid, checks, resolved);
        }

        private static FileCheck CheckFile(string physicalPath)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(physicalPath); }
            catch (Exception) { return new FileCheck { MayHold = true }; }

            int header = Math.Min(bytes.Length, HeaderLength);
            if (IndexOf(bytes, YamlHeader, 0, header) != 0 || IndexOf(bytes, UnityYamlTag, 0, header) < 0)
                return new FileCheck { MayHold = true };
            if (IndexOf(bytes, EntryMarker, 0, bytes.Length) >= 0)
                return new FileCheck { MayHold = true };

            List<string> sources = null;
            int at = 0;
            while ((at = IndexOf(bytes, SourcePrefabKey, at, bytes.Length)) >= 0)
            {
                int end = Array.IndexOf(bytes, (byte)'}', at);
                if (end < 0) break;

                int guid = IndexOf(bytes, GuidKey, at, end);
                if (guid >= 0 && guid + GuidKey.Length + GuidLength <= end)
                    (sources ??= new List<string>()).Add(Encoding.ASCII.GetString(bytes, guid + GuidKey.Length, GuidLength));
                at = end;
            }
            return new FileCheck { SourcePrefabs = sources };
        }

        /// <summary>First index of <paramref name="pattern"/> in <paramref name="data"/>[start, end), or -1.</summary>
        private static int IndexOf(byte[] data, byte[] pattern, int start, int end)
        {
            int last = end - pattern.Length;
            byte first = pattern[0];
            for (int i = start; i <= last; i++)
            {
                i = Array.IndexOf(data, first, i, last - i + 1);
                if (i < 0) return -1;

                int k = 1;
                while (k < pattern.Length && data[i + k] == pattern[k]) k++;
                if (k == pattern.Length) return i;
            }
            return -1;
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