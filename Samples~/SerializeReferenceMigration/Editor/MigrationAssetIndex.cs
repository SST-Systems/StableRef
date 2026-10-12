#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Types read straight from Unity YAML: the script of a <c>MonoBehaviour</c> / <c>ScriptableObject</c> document and
    /// the types of its <c>[SerializeReference]</c> records. Prefab overrides are rewritten from this index, so the
    /// migrator never loads an asset whose data Unity would read in the old shape.
    /// </summary>
    public sealed class MigrationAssetIndex
    {
        private sealed class Doc
        {
            public long FileId;
            public int ClassId;
            public bool Stripped;
            public string ScriptGuid;
            public long ScriptFileId;
            public readonly Dictionary<long, Type> RefTypes = new();
            /// <summary>For a stripped document: the object it stands for.</summary>
            public string SourceGuid;
            public long SourceFileId;
            /// <summary>For a <c>PrefabInstance</c> document: the instantiated prefab.</summary>
            public string InstanceSourceGuid;
        }

        private sealed class FileIndex
        {
            public readonly Dictionary<long, Doc> Docs = new();
            public readonly List<Doc> Instances = new();
        }

        private const long FileIdMask = 0x7FFFFFFFFFFFFFFF;
        private const int MaxNesting = 16;
        private const int MonoBehaviourClassId = 114;
        private const int PrefabInstanceClassId = 1001;
        private const string RidPrefix = "    - rid: ";
        private const string TypePrefix = "      type: ";
        private const string ScriptPrefix = "  m_Script: ";
        private const string SourceObjectPrefix = "  m_CorrespondingSourceObject: ";
        private const string SourcePrefabPrefix = "  m_SourcePrefab: ";

        private static readonly Regex HeaderRegex = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);

        private static readonly Dictionary<string, Dictionary<long, Type>> _scriptTypes = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

        private readonly Dictionary<string, FileIndex> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, long), Doc> _resolved = new();

        /// <summary>The script class of the document at <paramref name="fileId"/> in the asset with <paramref name="guid"/>.</summary>
        public Type ComponentType(string guid, long fileId)
        {
            var doc = Resolve(guid, fileId, 0);
            return doc == null ? null : ScriptType(doc.ScriptGuid, doc.ScriptFileId);
        }

        /// <summary>The type of the <c>[SerializeReference]</c> record <paramref name="rid"/> of that document.</summary>
        public Type ReferenceType(string guid, long fileId, long rid)
        {
            var doc = Resolve(guid, fileId, 0);
            return doc != null && doc.RefTypes.TryGetValue(rid, out var type) ? type : null;
        }

        /// <summary>The script class behind an <c>m_Script</c> reference.</summary>
        public static Type ScriptType(string guid, long fileId)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!_scriptTypes.TryGetValue(guid, out var scripts))
            {
                // A .cs file holds one MonoScript, a DLL one per class: all of them are indexed at once.
                scripts = new Dictionary<long, Type>();
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (var script in AssetDatabase.LoadAllAssetsAtPath(path).OfType<MonoScript>())
                    {
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out _, out long localId))
                            scripts[localId] = script.GetClass();
                    }
                }
                _scriptTypes[guid] = scripts;
            }

            return scripts.TryGetValue(fileId, out var type) ? type : null;
        }

        /// <summary>Parses <c>{fileID: N, guid: G, type: T}</c>.</summary>
        public static bool TryParseObjectReference(string text, out long fileId, out string guid)
        {
            fileId = 0;
            guid = null;
            const string FileIdKey = "{fileID: ", GuidKey = ", guid: ";
            if (text == null || !text.StartsWith(FileIdKey, StringComparison.Ordinal)) return false;
            int comma = text.IndexOf(',', FileIdKey.Length);
            if (comma < 0 || !long.TryParse(text.Substring(FileIdKey.Length, comma - FileIdKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId)) return false;
            if (string.CompareOrdinal(text, comma, GuidKey, 0, GuidKey.Length) != 0) return false;
            int start = comma + GuidKey.Length;
            int end = text.IndexOf(',', start);
            if (end < 0) end = text.IndexOf('}', start);
            if (end <= start) return false;
            guid = text.Substring(start, end - start);
            return true;
        }

        /// <summary>Parses the type of a reference record: <c>{class: Outer/Inner, ns: Namespace, asm: Assembly}</c>.</summary>
        public static Type ParseReferenceRecordType(string text)
        {
            const string ClassKey = "{class: ", NsKey = ", ns: ", AsmKey = ", asm: ";
            text = text?.Trim();
            if (text == null || !text.StartsWith(ClassKey, StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
                return null;
            int ns = text.IndexOf(NsKey, ClassKey.Length, StringComparison.Ordinal);
            int asm = ns < 0 ? -1 : text.IndexOf(AsmKey, ns + NsKey.Length, StringComparison.Ordinal);
            if (asm < 0) return null;
            return ReferenceRecordType(
                text.Substring(ClassKey.Length, ns - ClassKey.Length),
                text.Substring(ns + NsKey.Length, asm - ns - NsKey.Length),
                text.Substring(asm + AsmKey.Length, text.Length - 1 - asm - AsmKey.Length));
        }

        private static Type ReferenceRecordType(string className, string ns, string assembly)
        {
            className = Unquote(className);
            ns = Unquote(ns);
            if (string.IsNullOrEmpty(className)) return null;
            return ResolveType(assembly, string.IsNullOrEmpty(ns) ? className : ns + "." + className);
        }

        /// <summary>The type named in a prefab override declaration: <c>"Assembly Namespace.Outer/Inner"</c>.</summary>
        public static Type DeclaredReferenceType(string value)
        {
            value = Unquote(value);
            int space = value.IndexOf(' ');
            return space <= 0 ? null : ResolveType(value.Substring(0, space), value.Substring(space + 1));
        }

        private static Type ResolveType(string assembly, string fullName)
        {
            assembly = Unquote(assembly);
            fullName = fullName.Replace('/', '+');
            string key = assembly + "|" + fullName;
            if (_types.TryGetValue(key, out var cached)) return cached;

            var type = Type.GetType(fullName + ", " + assembly, false);
            if (type == null)
            {
                foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (candidate.GetName().Name != assembly) continue;
                    type = candidate.GetType(fullName, false);
                    if (type != null) break;
                }
            }

            return _types[key] = type;
        }

        private static string Unquote(string text)
        {
            text = text?.Trim() ?? "";
            return text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[text.Length - 1] == text[0]
                ? text.Substring(1, text.Length - 2)
                : text;
        }

        private Doc Resolve(string guid, long fileId, int depth)
        {
            if (depth > MaxNesting || string.IsNullOrEmpty(guid)) return null;
            if (_resolved.TryGetValue((guid, fileId), out var cached)) return cached;
            _resolved[(guid, fileId)] = null; // guards against cycles
            return _resolved[(guid, fileId)] = ResolveUncached(guid, fileId, depth);
        }

        private Doc ResolveUncached(string guid, long fileId, int depth)
        {
            var file = FileOf(guid);
            if (file == null) return null;

            if (file.Docs.TryGetValue(fileId, out var doc))
            {
                if (!doc.Stripped) return doc.ClassId == MonoBehaviourClassId ? doc : null;
                return Resolve(doc.SourceGuid, doc.SourceFileId, depth + 1);
            }

            // An object of a nested prefab instance that the file doesn't list: Unity derives its id from the instance
            // and the source object, (instance ^ source) & mask, so the source id is recovered the same way.
            foreach (var instance in file.Instances)
            {
                long source = (fileId ^ instance.FileId) & FileIdMask;
                var found = Resolve(instance.InstanceSourceGuid, source, depth + 1)
                            ?? Resolve(instance.InstanceSourceGuid, source | ~FileIdMask, depth + 1);
                if (found != null) return found;
            }

            return null;
        }

        private FileIndex FileOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (_files.TryGetValue(guid, out var cached)) return cached;

            FileIndex index = null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string text = File.ReadAllText(path);
                if (text.StartsWith("%YAML", StringComparison.Ordinal)) index = Parse(text);
            }

            return _files[guid] = index;
        }

        private static FileIndex Parse(string text)
        {
            var index = new FileIndex();
            Doc doc = null;
            long pendingRid = 0;
            bool hasPendingRid = false;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length < 4 || (line[0] != '-' && line[0] != ' ')) continue;
                var header = line[0] == '-' ? HeaderRegex.Match(line) : Match.Empty;
                if (header.Success)
                {
                    doc = new Doc
                    {
                        ClassId = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture),
                        FileId = long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture),
                        Stripped = header.Groups[3].Success
                    };
                    index.Docs[doc.FileId] = doc;
                    if (doc.ClassId == PrefabInstanceClassId) index.Instances.Add(doc);
                    hasPendingRid = false;
                    continue;
                }

                if (doc == null) continue;

                if (line.StartsWith(ScriptPrefix, StringComparison.Ordinal))
                {
                    if (TryParseObjectReference(line.Substring(ScriptPrefix.Length), out long id, out string guid))
                    {
                        doc.ScriptFileId = id;
                        doc.ScriptGuid = guid;
                    }
                }
                else if (line.StartsWith(SourceObjectPrefix, StringComparison.Ordinal))
                {
                    if (TryParseObjectReference(line.Substring(SourceObjectPrefix.Length), out long id, out string guid))
                    {
                        doc.SourceFileId = id;
                        doc.SourceGuid = guid;
                    }
                }
                else if (line.StartsWith(SourcePrefabPrefix, StringComparison.Ordinal))
                {
                    if (TryParseObjectReference(line.Substring(SourcePrefabPrefix.Length), out _, out string guid))
                        doc.InstanceSourceGuid = guid;
                }
                else if (line.StartsWith(RidPrefix, StringComparison.Ordinal))
                {
                    hasPendingRid = long.TryParse(line.Substring(RidPrefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pendingRid);
                }
                else if (hasPendingRid && line.StartsWith(TypePrefix, StringComparison.Ordinal))
                {
                    var type = ParseReferenceRecordType(line.Substring(TypePrefix.Length));
                    if (type != null) doc.RefTypes[pendingRid] = type;
                    hasPendingRid = false;
                }
            }

            return index;
        }
    }
}
#endif
