#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting.APIUpdating;

namespace SST.StableRef
{
    /// <summary>
    /// Objects whose managed references Unity lost although their file holds them, and getting them back before
    /// anything is recreated from snapshots:
    /// <list type="bullet">
    /// <item>an unreadable file — a record of a missing class without data makes Unity drop every reference of the
    /// object (see <see cref="StableRefYamlRecords"/>); the record is removed from the file and the file reimported;</item>
    /// <item>a loss in memory — an object holding Unity's missing-type data loses all its references on the next domain
    /// reload, while its file is intact; the object is read from disk again (a scene only after the user confirms).</item>
    /// </list>
    /// </summary>
    internal static class StableRefFileRepair
    {
        private static readonly Dictionary<string, bool> _resolved = new();
        private static HashSet<string> _movedFrom;

        /// <summary>
        /// The parsed text of a text-serialized Unity file; <see cref="StableRefYamlRecords.Document.Empty"/> for a
        /// binary, unreadable or unsaved one.
        /// </summary>
        internal static StableRefYamlRecords.Document ReadDocument(string path)
        {
            if (string.IsNullOrEmpty(path)) return StableRefYamlRecords.Document.Empty;
            string text;
            try { text = File.ReadAllText(FileUtil.GetPhysicalPath(path)); }
            catch (Exception) { return StableRefYamlRecords.Document.Empty; }
            return text.StartsWith("%YAML", StringComparison.Ordinal)
                ? StableRefYamlRecords.Parse(text)
                : StableRefYamlRecords.Document.Empty;
        }

        /// <summary>Records of <paramref name="document"/> Unity can't read back.</summary>
        internal static List<StableRefYamlRecords.Unreadable> FindUnreadable(StableRefYamlRecords.Document document)
            => document.FindUnreadable(TypeResolves);

        /// <summary>
        /// Whether Unity can load the class a managed-reference record names: by its name, or through a
        /// <see cref="MovedFromAttribute"/> on the class it was renamed or moved to.
        /// </summary>
        internal static bool TypeResolves(string className, string ns, string assembly)
        {
            string key = assembly + "|" + ns + "|" + className;
            if (_resolved.TryGetValue(key, out bool known)) return known;

            string name = className.Replace('/', '+');
            int generic = name.IndexOf('[');
            if (generic > 0) name = name.Substring(0, generic);
            string fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            bool found = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != assembly) continue;
                found = asm.GetType(fullName, false) != null;
                break;
            }

            return _resolved[key] = found || MovedFromKeys().Contains(key);
        }

        private static HashSet<string> MovedFromKeys()
        {
            if (_movedFrom != null) return _movedFrom;
            _movedFrom = new HashSet<string>();
            foreach (var type in TypeCache.GetTypesWithAttribute<MovedFromAttribute>())
            foreach (var data in type.GetCustomAttributesData())
            {
                if (data.AttributeType != typeof(MovedFromAttribute)) continue;

                string ns = null, assembly = null, className = null;
                var parameters = data.Constructor.GetParameters();
                for (int i = 0; i < parameters.Length && i < data.ConstructorArguments.Count; i++)
                {
                    string value = data.ConstructorArguments[i].Value as string;
                    switch (parameters[i].Name)
                    {
                        case "sourceNamespace": ns = value; break;
                        case "sourceAssembly": assembly = value; break;
                        case "sourceClassName": className = value; break;
                    }
                }

                string ownName = type.DeclaringType != null ? type.DeclaringType.Name + "/" + type.Name : type.Name;
                _movedFrom.Add((assembly ?? type.Assembly.GetName().Name) + "|" + (ns ?? type.Namespace ?? "") + "|" +
                               (className ?? ownName));
            }
            return _movedFrom;
        }

        /// <summary>
        /// File id of <paramref name="target"/>'s YAML document in its asset file or scene, or 0 when it has none of
        /// its own (an object of a prefab instance, an unsaved scene).
        /// </summary>
        internal static long FileIdOf(UnityEngine.Object target)
        {
            if (target == null) return 0;
            if (EditorUtility.IsPersistent(target))
                return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out _, out long localId) ? localId : 0;

            var id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            return id.identifierType == 2 && id.targetPrefabId == 0 ? (long)id.targetObjectId : 0;
        }

        /// <summary>
        /// True when Unity lost <paramref name="target"/>'s managed references in memory while its file still holds
        /// them: no field holds a value, there is no missing-type data to explain it, nothing is unsaved, and the
        /// object's records in <paramref name="document"/> point at something. Reading the object from disk again
        /// brings them back; recreating them from snapshots would be needless.
        /// </summary>
        internal static bool IsLostInMemory(UnityEngine.Object target, StableRefYamlRecords.Document document)
        {
            if (target == null || EditorUtility.IsDirty(target)) return false;
            if (SerializationUtility.HasManagedReferencesWithMissingTypes(target)) return false;
            long fileId = FileIdOf(target);
            if (fileId == 0 || !document.HasRecords(fileId)) return false;
            return !HoldsAnyValue(target);
        }

        private static bool HoldsAnyValue(UnityEngine.Object target)
        {
            var iter = new SerializedObject(target).GetIterator();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (iter.propertyType == SerializedPropertyType.ManagedReference)
                {
                    if (StableRefPropertyUtils.HasManagedValue(iter)) return true;
                    enter = false;
                    continue;
                }
                enter = StableRefPropertyUtils.MayHoldEntries(iter);
            }
            return false;
        }

        /// <summary>
        /// A record of a missing class whose data is empty, held in memory by a loaded object and used by a StableRef
        /// entry. Saving the object writes it without <c>data:</c>, and the file can't be read back (see
        /// <see cref="StableRefYamlRecords"/>).
        /// </summary>
        internal sealed class EmptyRecord
        {
            public long Rid;
            /// <summary>Class name without namespace, as the tool windows show it.</summary>
            public string ClassName;
            public List<StableRefYamlRecords.FieldUse> Fields;
        }

        /// <summary>
        /// Unity's missing-type records of <paramref name="target"/> whose data is empty (the class had no serialized
        /// fields) and the file id of its YAML document; empty when there are none or the object has no document. Only
        /// native calls — cheap enough to run before anything reads the file.
        /// </summary>
        private static List<ManagedReferenceMissingType> BlankRecords(UnityEngine.Object target, out long fileId)
        {
            fileId = 0;
            var result = new List<ManagedReferenceMissingType>();
            if (target == null || !SerializationUtility.HasManagedReferencesWithMissingTypes(target)) return result;
            foreach (var missing in SerializationUtility.GetManagedReferencesWithMissingTypes(target))
                if (string.IsNullOrWhiteSpace(missing.serializedData))
                    result.Add(missing);
            if (result.Count > 0) fileId = FileIdOf(target);
            if (fileId == 0) result.Clear();
            return result;
        }

        /// <summary>
        /// The records of <paramref name="target"/> with empty data that a StableRef entry points at — found by the
        /// object's YAML document in <paramref name="document"/>, since the editor API reads every field of a missing
        /// class as null.
        /// </summary>
        internal static List<EmptyRecord> FindEmptyMissingRecords(UnityEngine.Object target,
            StableRefYamlRecords.Document document)
        {
            var result = new List<EmptyRecord>();
            foreach (var missing in BlankRecords(target, out long fileId))
            {
                var fields = document.FieldsUsing(fileId, missing.referenceId);
                if (fields.Exists(f => f.IsEntry))
                    result.Add(new EmptyRecord { Rid = missing.referenceId, ClassName = missing.className, Fields = fields });
            }
            return result;
        }

        /// <summary>
        /// Whether <paramref name="target"/> holds a record with empty data that any field (plain ones too) points at in
        /// its file — saving it would write a file Unity can't read back. A record no field uses is dropped on save and
        /// doesn't count.
        /// </summary>
        internal static bool HoldsEmptyMissingRecord(UnityEngine.Object target)
        {
            var blank = BlankRecords(target, out long fileId);
            if (blank.Count == 0) return false;
            var document = ReadDocument(FilePathOf(target));
            return blank.Exists(m => document.FieldsUsing(fileId, m.referenceId).Count > 0);
        }

        /// <summary>
        /// Clears the records <paramref name="rids"/> of <paramref name="target"/> that Unity still holds as missing with
        /// empty data, so the fields pointing at them become None before the object is saved (a StableRef entry keeps its
        /// stable id and snapshot and stays missing). Only these records go — never the full release of missing-type
        /// data, which would also drop other fields' records that do hold data. Returns how many were cleared; the
        /// object is marked dirty when any was.
        /// </summary>
        internal static int ClearEmptyRecords(UnityEngine.Object target, IReadOnlyCollection<long> rids)
        {
            if (rids == null || rids.Count == 0) return 0;
            int cleared = 0;
            foreach (var record in BlankRecords(target, out _))
            {
                if (!rids.Contains(record.referenceId)) continue;
                SerializationUtility.ClearManagedReferenceWithMissingType(target, record.referenceId);
                cleared++;
            }
            if (cleared == 0) return 0;

            EditorUtility.SetDirty(target);
            _brokenCache.Clear();
            return cleared;
        }

        /// <summary>
        /// Clears the missing-type records of <paramref name="target"/> with empty data that only StableRef entries
        /// point at (by its file) and none of those entries is still missing — they were discarded, replaced or fixed.
        /// Unity keeps such a record although the entry was set to None (the hidden reference id survives) and a save
        /// would write it in a form Unity can't read back; its id can't be read from the field, hence the file.
        /// Part of <see cref="StableRefEntry.ReleaseMissingData"/>: an entry that is still missing keeps its record.
        /// </summary>
        internal static void ReleaseEmptyRecords(UnityEngine.Object target)
        {
            var empty = BlankRecords(target, out long fileId);
            if (empty.Count == 0) return;

            var document = ReadDocument(FilePathOf(target));
            HashSet<string> stillMissing = null;
            foreach (var record in empty)
            {
                var fields = document.FieldsUsing(fileId, record.referenceId);
                if (fields.Count == 0 || fields.Exists(f => !f.IsEntry)) continue;
                stillMissing ??= MissingEntryPaths(target);
                if (fields.Exists(f => stillMissing.Contains(f.Path))) continue;
                SerializationUtility.ClearManagedReferenceWithMissingType(target, record.referenceId);
                _brokenCache.Clear();
            }
        }

        /// <summary>Paths (as <see cref="YamlFieldPath"/> writes them) of the missing StableRef entries of <paramref name="target"/>.</summary>
        private static HashSet<string> MissingEntryPaths(UnityEngine.Object target)
        {
            var result = new HashSet<string>();
            var so = new SerializedObject(target);
            var iter = so.GetIterator();
            bool enter = true;
            while (iter.Next(enter))
            {
                if (iter.propertyType != SerializedPropertyType.ManagedReference)
                {
                    enter = StableRefPropertyUtils.MayHoldEntries(iter);
                    continue;
                }
                enter = StableRefPropertyUtils.HasManagedValue(iter);
                if (enter) continue;

                var wrapper = StableRefEntry.FindWrapperOfValue(so, iter.propertyPath);
                if (wrapper != null && StableRefEntry.IsMissing(wrapper))
                    result.Add(YamlFieldPath(so, wrapper.propertyPath));
            }
            return result;
        }

        /// <summary>
        /// <paramref name="propertyPath"/> as <see cref="StableRefYamlRecords.FieldUse.Path"/> writes it: list elements as
        /// <c>[i]</c> (without <c>_items</c>), an entry's <c>Value</c> left out.
        /// </summary>
        internal static string YamlFieldPath(SerializedObject so, string propertyPath)
        {
            var segs = propertyPath.Split('.');
            var sb = new StringBuilder();
            string accumulated = "";
            for (int i = 0; i < segs.Length; i++)
            {
                string seg = segs[i];
                string parent = accumulated;
                accumulated = i == 0 ? seg : accumulated + "." + seg;

                bool arrayNext = i + 1 < segs.Length && segs[i + 1] == "Array";
                if (seg == "Array" || (seg == StableRefEntry.ListItemsFieldName && arrayNext)) continue;
                if (seg.StartsWith("data[", StringComparison.Ordinal))
                {
                    sb.Append(seg, 4, seg.Length - 4);
                    continue;
                }
                if (seg == StableRefEntry.ValueFieldName && parent.Length > 0
                    && so.FindProperty(parent + "." + StableRefEntry.TypeIdFieldName) != null) continue;

                if (sb.Length > 0) sb.Append('.');
                sb.Append(seg);
            }
            return sb.ToString();
        }

        /// <summary>Label of a field whose data is broken; the fix repairs it.</summary>
        internal static string BrokenLabelText(string className) =>
            $"Broken ({(string.IsNullOrEmpty(className) ? "?" : className)}) → Fixed";

        private sealed class BrokenFields
        {
            public string Path;
            public long WriteTicks;
            public int MissingCount;
            public double CheckedAt;
            /// <summary>YAML field path → class of the broken record it points at.</summary>
            public readonly Dictionary<string, string> ByField = new();
            /// <summary>Property path of an entry → its answer (null: not broken), so a repaint builds no paths.</summary>
            public readonly Dictionary<string, string> ByProperty = new();
        }

        private const int MaxBrokenCache = 256;
        /// <summary>How often a cached answer is checked against the file and the missing-type data, in seconds.</summary>
        private const double BrokenRecheck = 1.0;
        private static readonly Dictionary<long, BrokenFields> _brokenCache = new();

        /// <summary>
        /// For the drawer of a missing <paramref name="entry"/>: the class of a broken record — unreadable in the file,
        /// or held in memory with empty data — that the entry or a field inside it points at. Cached per object and
        /// property; the cache is checked against the file's write time and the missing-type data at most once a second
        /// and dropped whenever this class repairs a file or clears a record, so a repaint does no work.
        /// </summary>
        internal static bool TryGetBrokenClass(SerializedProperty entry, out string className)
        {
            className = null;
            var target = entry?.serializedObject.targetObject;
            if (target == null) return false;

            var broken = BrokenFieldsOf(target);
            if (broken.ByField.Count == 0) return false;

            string propertyPath = entry.propertyPath;
            if (!broken.ByProperty.TryGetValue(propertyPath, out className))
            {
                string path = YamlFieldPath(entry.serializedObject, propertyPath);
                foreach (var pair in broken.ByField)
                {
                    string field = pair.Key;
                    if (field == path
                        || (field.Length > path.Length && field.StartsWith(path, StringComparison.Ordinal)
                                                       && (field[path.Length] == '.' || field[path.Length] == '[')))
                    {
                        className = pair.Value;
                        break;
                    }
                }
                broken.ByProperty[propertyPath] = className;
            }
            return className != null;
        }

        private static BrokenFields BrokenFieldsOf(UnityEngine.Object target)
        {
            long id = StableRefEditorUtility.GetStableId(target);
            double now = EditorApplication.timeSinceStartup;
            if (_brokenCache.TryGetValue(id, out var cached) && now - cached.CheckedAt < BrokenRecheck) return cached;

            string path = FilePathOf(target) ?? "";
            long ticks = 0;
            if (path.Length > 0)
                try { ticks = File.GetLastWriteTimeUtc(FileUtil.GetPhysicalPath(path)).Ticks; }
                catch (Exception) { }
            int missingCount = SerializationUtility.HasManagedReferencesWithMissingTypes(target)
                ? SerializationUtility.GetManagedReferencesWithMissingTypes(target).Length
                : 0;

            if (cached != null && cached.Path == path && cached.WriteTicks == ticks && cached.MissingCount == missingCount)
            {
                cached.CheckedAt = now;
                return cached;
            }

            if (_brokenCache.Count >= MaxBrokenCache) _brokenCache.Clear();
            var broken = new BrokenFields { Path = path, WriteTicks = ticks, MissingCount = missingCount, CheckedAt = now };
            _brokenCache[id] = broken;
            if (path.Length == 0) return broken;

            var document = ReadDocument(path);
            long fileId = FileIdOf(target);
            foreach (var record in FindUnreadable(document))
                if (record.ObjectFileId == fileId)
                    foreach (var field in record.Fields)
                        broken.ByField[field.Path] = record.ClassName;
            foreach (var record in FindEmptyMissingRecords(target, document))
                foreach (var field in record.Fields)
                    if (field.IsEntry)
                        broken.ByField[field.Path] = record.ClassName;
            return broken;
        }

        /// <summary>The asset file or scene <paramref name="target"/> is stored in; empty when it has none.</summary>
        private static string FilePathOf(UnityEngine.Object target) => EditorUtility.IsPersistent(target)
            ? AssetDatabase.GetAssetPath(target)
            : (target as Component)?.gameObject.scene.path;

        /// <summary>
        /// Reads a ScriptableObject that isn't dirty from disk again. Returns the reloaded object (the same instance
        /// id), or <see langword="null"/> when it can't be reloaded this way.
        /// </summary>
        internal static UnityEngine.Object ReloadAsset(UnityEngine.Object target)
        {
            if (target is not ScriptableObject || EditorUtility.IsDirty(target)) return null;
            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path)) return null;

            long id = StableRefEditorUtility.GetStableId(target);
            Resources.UnloadAsset(target);
            AssetDatabase.LoadAllAssetsAtPath(path);
            return StableRefEditorUtility.IdToObject(id);
        }

        /// <summary>Why <see cref="RepairAsset"/> left a file as it was.</summary>
        internal enum RepairResult
        {
            Repaired,
            NothingToRepair,
            Unsaved,
            Failed
        }

        /// <summary>
        /// Removes the unreadable records of the asset file at <paramref name="path"/> and reimports it (loaded objects
        /// of the file are updated in place). Refused while an object of the file has unsaved changes — the reimport
        /// would discard them. The removed records are logged.
        /// </summary>
        internal static RepairResult RepairAsset(string path)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj != null && EditorUtility.IsDirty(obj))
                    return RepairResult.Unsaved;

            var result = RepairFile(path);
            if (result == RepairResult.Repaired)
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            return result;
        }

        /// <summary>Removes the unreadable records of the file at <paramref name="path"/> without importing it.</summary>
        private static RepairResult RepairFile(string path)
        {
            string physical = FileUtil.GetPhysicalPath(path);
            string text;
            try { text = File.ReadAllText(physical); }
            catch (Exception) { return RepairResult.Failed; }
            if (!text.StartsWith("%YAML", StringComparison.Ordinal)) return RepairResult.NothingToRepair;

            var document = StableRefYamlRecords.Parse(text);
            var unreadable = FindUnreadable(document);
            if (unreadable.Count == 0) return RepairResult.NothingToRepair;

            try { File.WriteAllText(physical, document.Remove(unreadable), new UTF8Encoding(false)); }
            catch (Exception e)
            {
                Debug.LogError($"[StableRef] Could not write '{path}': {e.Message}");
                return RepairResult.Failed;
            }

            _brokenCache.Clear();
            Debug.LogWarning(DescribeRepair(path, unreadable), AssetDatabase.LoadMainAssetAtPath(path));
            return RepairResult.Repaired;
        }

        /// <summary>The log message for records removed from <paramref name="path"/>.</summary>
        internal static string DescribeRepair(string path, List<StableRefYamlRecords.Unreadable> removed)
        {
            var sb = new StringBuilder();
            sb.Append("[StableRef] '").Append(path).Append("': removed ").Append(removed.Count)
                .Append(removed.Count == 1 ? " record" : " records")
                .Append(" of missing classes that Unity saved without data and then couldn't read — every managed " +
                        "reference of the object was lost on load:");
            foreach (var record in removed)
            {
                sb.Append("\n• ").Append(record.TypeName).Append(" (assembly ").Append(record.Assembly).Append(')');
                foreach (var field in record.Fields)
                    sb.Append("\n    ").Append(field.Path).Append(" → ").Append(
                        !field.IsEntry ? "None"
                        : field.TypeId.Length == 0 ? "None (the entry has no stable id)"
                        : $"recreated from its snapshot by Fix (stable id '{field.TypeId}')");
            }
            sb.Append("\nIf the class was renamed or moved, add [UnityEngine.Scripting.APIUpdating.MovedFrom] to it " +
                      "with the old name before fixing — then Unity loads it and nothing is removed.");
            return sb.ToString();
        }

        /// <summary>
        /// Reads the open scenes at <paramref name="paths"/> from disk again — each is closed (the only open scene is
        /// replaced by an empty one), <paramref name="whileClosed"/> runs for its path (e.g. to repair the file without
        /// Unity asking to reload it) and the scene is reopened in its place in the hierarchy, active again if it was.
        /// Unsaved changes of these scenes are discarded: confirm with the user first.
        /// </summary>
        internal static void ReloadScenes(IReadOnlyList<string> paths, Action<string> whileClosed)
        {
            foreach (string path in paths)
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                bool active = SceneManager.GetActiveScene() == scene;
                bool single = SceneManager.sceneCount == 1;
                string nextPath = null;
                for (int i = 0; i < SceneManager.sceneCount - 1; i++)
                    if (SceneManager.GetSceneAt(i) == scene)
                        nextPath = SceneManager.GetSceneAt(i + 1).path;

                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else EditorSceneManager.CloseScene(scene, true);

                whileClosed?.Invoke(path);

                if (single)
                {
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    continue;
                }

                var reopened = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                var next = string.IsNullOrEmpty(nextPath) ? default : SceneManager.GetSceneByPath(nextPath);
                if (next.IsValid()) EditorSceneManager.MoveSceneBefore(reopened, next);
                if (active) SceneManager.SetActiveScene(reopened);
            }
        }

        /// <summary>Asks whether the open scenes at <paramref name="paths"/> may be read from disk again.</summary>
        internal static bool ConfirmSceneReload(IReadOnlyList<string> paths)
        {
            var lines = paths.Select(p => "• " + p + (SceneManager.GetSceneByPath(p).isDirty
                ? " — its unsaved changes will be discarded"
                : ""));
            return EditorUtility.DisplayDialog("Fix Missing StableRef Types",
                "Unity lost the managed references of objects in these open scenes — on load, because of records it " +
                "can't read (they are removed from the file first), or in memory after a domain reload — while the " +
                "files still hold them. Read the scenes from disk again?\n\n" + string.Join("\n", lines),
                "Reload Scenes", "Skip");
        }

        /// <summary>
        /// Before the entries of <paramref name="target"/> are recreated from snapshots: repairs its unreadable file or
        /// reads it from disk again when Unity lost its references (a scene only after the user confirms). Returns the
        /// object to fix — the reloaded one, which may be a new object in a scene — or <see langword="null"/> to leave
        /// it alone.
        /// </summary>
        internal static UnityEngine.Object RestoreBeforeFix(UnityEngine.Object target)
        {
            if (target == null) return null;
            bool inScene = !EditorUtility.IsPersistent(target);
            string path = FilePathOf(target);
            if (string.IsNullOrEmpty(path)) return target;

            var document = ReadDocument(path);
            bool unreadable = FindUnreadable(document).Count > 0;
            if (!unreadable && !IsLostInMemory(target, document))
            {
                // As Fix All: broken references of entries become None, before a save writes them unreadable.
                ClearEmptyRecords(target, FindEmptyMissingRecords(target, document).Select(r => r.Rid).ToList());
                return target;
            }

            var id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if (!inScene)
            {
                if (!unreadable) return ReloadAsset(target) ?? target;

                var result = RepairAsset(path);
                if (result == RepairResult.Unsaved)
                {
                    Debug.LogWarning($"[StableRef] '{path}' has records Unity can't read, but also unsaved changes — " +
                                     "save or revert it, then fix again.", target);
                    return null;
                }
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            }

            if (!ConfirmSceneReload(new[] { path })) return null;
            ReloadScenes(new[] { path }, unreadable ? new Action<string>(RepairClosedScene) : null);
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
        }

        /// <summary>
        /// Repairs the file of a scene that is closed at the moment (see <see cref="ReloadScenes"/>) and imports it.
        /// </summary>
        internal static void RepairClosedScene(string path)
        {
            if (RepairFile(path) == RepairResult.Repaired)
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
#endif
