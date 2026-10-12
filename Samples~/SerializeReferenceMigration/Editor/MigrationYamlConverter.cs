#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Rewrites one Unity YAML file from the <c>[SerializeReference]</c> layout to the StableRef layout, as text:
    /// <list type="bullet">
    /// <item><c>field: {rid: N}</c> of a field that is now <c>StableRef&lt;T&gt;</c> → <c>field: {Value: {rid: N}}</c>;</item>
    /// <item><c>field: [- rid: N]</c> of a field that is now <c>StableRefList&lt;T&gt;</c> →
    /// <c>field: {_items: [- Value: {rid: N}]}</c>;</item>
    /// <item>the same inside nested classes, arrays of them and the data of other <c>[SerializeReference]</c> records;</item>
    /// <item>prefab override paths: <c>field</c> → <c>field.Value</c>, <c>list.Array.size</c> →
    /// <c>list._items.Array.size</c>, <c>list.Array.data[i]</c> → <c>list._items.Array.data[i].Value</c>.</item>
    /// </list>
    /// The reference records themselves are kept as they are, so every value, nested reference and override by
    /// <c>managedReferences[rid]</c> stays valid. A key stored under a <c>[FormerlySerializedAs]</c> alias is renamed
    /// to the field's name. Entry metadata (stable id, snapshot) is left empty for Resync to fill.
    /// </summary>
    public sealed class MigrationYamlConverter
    {
        /// <summary>Result for one file.</summary>
        public sealed class Result
        {
            public string Text;
            public int ConvertedFields;
            public int RewrittenOverrides;
            public readonly List<string> Warnings = new();
            /// <summary>What changed, one line per field or override path.</summary>
            public readonly List<string> Changes = new();
            public bool Changed => ConvertedFields > 0 || RewrittenOverrides > 0;
        }

        private readonly struct Edit
        {
            public readonly int Start;
            public readonly int End;
            public readonly List<string> Lines;

            public Edit(int start, int end, List<string> lines)
            {
                Start = start;
                End = end;
                Lines = lines;
            }
        }

        private const int MonoBehaviourClassId = 114;
        private const int PrefabInstanceClassId = 1001;

        private static readonly Regex HeaderRegex = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);
        private static readonly Regex RidLineRegex = new(@"^\s*rid: (-?\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex RidItemRegex = new(@"^\s*- rid: (-?\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex InlineRidRegex = new(@"^\{rid: (-?\d+)\}$", RegexOptions.Compiled);

        private readonly MigrationAssetIndex _index;
        private string[] _lines;
        private List<Edit> _edits;
        private Result _result;
        private string _path;

        public MigrationYamlConverter(MigrationAssetIndex index)
        {
            _index = index;
        }

        /// <summary>Converts <paramref name="text"/>, the content of the file at <paramref name="path"/>.</summary>
        public Result Convert(string path, string text)
        {
            _path = path;
            _result = new Result { Text = text };
            _edits = new List<Edit>();

            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            _lines = text.Replace("\r\n", "\n").Split('\n');

            var headers = new List<(int Line, int ClassId, bool Stripped)>();
            for (int i = 0; i < _lines.Length; i++)
            {
                if (!_lines[i].StartsWith("--- !u!", StringComparison.Ordinal)) continue;
                var match = HeaderRegex.Match(_lines[i]);
                if (match.Success) headers.Add((i, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[3].Success));
            }

            for (int h = 0; h < headers.Count; h++)
            {
                var (line, classId, stripped) = headers[h];
                int end = h + 1 < headers.Count ? headers[h + 1].Line : _lines.Length;
                if (stripped || line + 1 >= end) continue;

                // The document body: "ClassName:" at column 0, its fields at column 2.
                if (classId == MonoBehaviourClassId) VisitBehaviour(line + 2, end);
                else if (classId == PrefabInstanceClassId) VisitPrefabInstance(line + 2, end);
            }

            if (_edits.Count == 0) return _result;

            var lines = _lines.ToList();
            foreach (var edit in _edits.OrderByDescending(e => e.Start))
            {
                lines.RemoveRange(edit.Start, edit.End - edit.Start);
                lines.InsertRange(edit.Start, edit.Lines);
            }

            _result.Text = string.Join(newLine, lines);
            return _result;
        }

        #region Data

        private void VisitBehaviour(int start, int end)
        {
            Type scriptType = null;
            int referencesLine = -1;
            foreach (var (line, key, inline) in Keys(start, end, 2))
            {
                if (key == "m_Script")
                {
                    if (MigrationAssetIndex.TryParseObjectReference(inline, out long scriptId, out string scriptGuid))
                        scriptType = MigrationAssetIndex.ScriptType(scriptGuid, scriptId);
                }
                else if (key == "references")
                {
                    referencesLine = line;
                }
            }

            if (scriptType != null && MigrationFieldModel.MayContainStableRef(scriptType))
                VisitMapping(start, end, 2, scriptType);

            if (referencesLine < 0) return;
            int referencesEnd = BlockEnd(referencesLine, end, 2);
            foreach (var (line, key, _) in Keys(referencesLine + 1, referencesEnd, 4))
            {
                if (key != "RefIds") continue;
                int refIdsEnd = BlockEnd(line, referencesEnd, 4);
                foreach (var (itemStart, itemEnd) in Items(line + 1, refIdsEnd, 4))
                    VisitRecord(itemStart, itemEnd);
            }
        }

        private void VisitRecord(int start, int end)
        {
            Type type = null;
            int dataLine = -1;
            foreach (var (line, key, inline) in Keys(start, end, 6))
            {
                if (key == "type")
                {
                    type = MigrationAssetIndex.ParseReferenceRecordType(inline);
                }
                else if (key == "data")
                {
                    dataLine = line;
                }
            }

            if (dataLine < 0 || type == null || !MigrationFieldModel.MayContainStableRef(type)) return;
            VisitMapping(dataLine + 1, BlockEnd(dataLine, end, 6), 8, type);
        }

        private void VisitMapping(int start, int end, int column, Type type)
        {
            var keys = Keys(start, end, column).ToList();
            foreach (var (line, key, inline) in keys)
            {
                var field = MigrationFieldModel.Find(type, key);
                if (field == null) continue;
                int blockEnd = BlockEnd(line, end, column);

                bool isStableRef = field.Kind == MigrationFieldModel.FieldKind.StableRef
                                   || field.Kind == MigrationFieldModel.FieldKind.StableRefList;
                if (isStableRef && key != field.Name && keys.Any(k => k.Key == field.Name))
                {
                    Warn(type, key, $"is stored under both its old name and '{field.Name}' — left as is");
                    continue;
                }

                switch (field.Kind)
                {
                    case MigrationFieldModel.FieldKind.StableRef:
                        ConvertSingle(line, blockEnd, column, key, inline, field, type);
                        break;
                    case MigrationFieldModel.FieldKind.StableRefList:
                        ConvertList(line, blockEnd, column, key, inline, field, type);
                        break;
                    case MigrationFieldModel.FieldKind.Nested:
                        if (MigrationFieldModel.MayContainStableRef(field.NestedType))
                            VisitMapping(line + 1, blockEnd, column + 2, field.NestedType);
                        break;
                    case MigrationFieldModel.FieldKind.NestedArray:
                        if (!MigrationFieldModel.MayContainStableRef(field.NestedType)) break;
                        foreach (var (itemStart, itemEnd) in Items(line + 1, blockEnd, column))
                            VisitMapping(itemStart, itemEnd, column + 2, field.NestedType);
                        break;
                }
            }
        }

        private void ConvertSingle(int line, int blockEnd, int column, string key, string inline,
            MigrationFieldModel.FieldModel field, Type owner)
        {
            long rid;
            if (inline.Length > 0)
            {
                var match = InlineRidRegex.Match(inline);
                if (!match.Success)
                {
                    if (inline != "{}") Warn(owner, key, $"unexpected value '{inline}' — left as is");
                    return;
                }
                rid = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                var content = ContentLines(line + 1, blockEnd);
                if (IsNewShape(content, column + 2)) return;
                if (content.Count != 1 || Spaces(_lines[content[0]]) != column + 2 || !RidLineRegex.IsMatch(_lines[content[0]]))
                {
                    Warn(owner, key, content.Any(i => RidItemRegex.IsMatch(_lines[i]))
                        ? "holds a list, but the field is a single StableRef<T> — use StableRefList<T>; left as is"
                        : "unrecognized layout — left as is");
                    return;
                }
                rid = long.Parse(RidLineRegex.Match(_lines[content[0]]).Groups[1].Value, CultureInfo.InvariantCulture);
            }

            AddFieldEdit(line, blockEnd, new List<string>
            {
                KeyLine(line, field.Name),
                Pad(column + 2) + StableRefEntry.ValueFieldName + ":",
                Pad(column + 4) + "rid: " + rid.ToString(CultureInfo.InvariantCulture)
            }, owner, key);
        }

        private void ConvertList(int line, int blockEnd, int column, string key, string inline,
            MigrationFieldModel.FieldModel field, Type owner)
        {
            if (inline.Length > 0)
            {
                if (inline == "[]")
                {
                    AddFieldEdit(line, blockEnd, new List<string>
                    {
                        KeyLine(line, field.Name),
                        Pad(column + 2) + StableRefEntry.ListItemsFieldName + ": []"
                    }, owner, key);
                }
                else if (inline != "{}")
                {
                    Warn(owner, key, $"unexpected value '{inline}' — left as is");
                }
                return;
            }

            var content = ContentLines(line + 1, blockEnd);
            if (IsNewShape(content, column + 2)) return;

            var rids = new List<long>();
            foreach (int i in content)
            {
                var match = RidItemRegex.Match(_lines[i]);
                if (!match.Success || Spaces(_lines[i]) != column)
                {
                    Warn(owner, key, content.Count == 1 && RidLineRegex.IsMatch(_lines[content[0]])
                        ? "holds a single reference, but the field is a StableRefList<T> — use StableRef<T>; left as is"
                        : "unrecognized layout — left as is");
                    return;
                }
                rids.Add(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            var lines = new List<string>
            {
                KeyLine(line, field.Name),
                Pad(column + 2) + StableRefEntry.ListItemsFieldName + (rids.Count == 0 ? ": []" : ":")
            };
            foreach (long rid in rids)
            {
                lines.Add(Pad(column + 2) + "- " + StableRefEntry.ValueFieldName + ":");
                lines.Add(Pad(column + 6) + "rid: " + rid.ToString(CultureInfo.InvariantCulture));
            }

            AddFieldEdit(line, blockEnd, lines, owner, key);
        }

        /// <summary>The block already holds entry fields (<c>TypeId</c>, <c>Value</c>, <c>_items</c>…): migrated before.</summary>
        private bool IsNewShape(List<int> content, int column) =>
            content.Any(i => TryKey(_lines[i], column, out string key, out _, out _)
                             && (key == StableRefEntry.TypeIdFieldName || key == StableRefEntry.ValueFieldName
                                 || key == StableRefEntry.ListItemsFieldName || key == StableRefEntry.TypeDisplayNameFieldName));

        #endregion

        #region Prefab overrides

        private void VisitPrefabInstance(int start, int end)
        {
            foreach (var (modificationLine, key, _) in Keys(start, end, 2))
            {
                if (key != "m_Modification") continue;
                int modificationEnd = BlockEnd(modificationLine, end, 2);
                foreach (var (listLine, listKey, _) in Keys(modificationLine + 1, modificationEnd, 4))
                {
                    if (listKey != "m_Modifications") continue;
                    var items = Items(listLine + 1, BlockEnd(listLine, modificationEnd, 4), 4).ToList();
                    RewriteModifications(items);
                }
            }
        }

        private void RewriteModifications(List<(int Start, int End)> items)
        {
            var modifications = new List<(string Guid, long FileId, int PathLine, string Path, string Value)>();
            foreach (var (start, end) in items)
            {
                string guid = null, path = null, value = null;
                long fileId = 0;
                int pathLine = -1;
                foreach (var (line, key, inline) in Keys(start, end, 6))
                {
                    switch (key)
                    {
                        case "target":
                            if (!MigrationAssetIndex.TryParseObjectReference(inline, out fileId, out guid)) guid = null;
                            break;
                        case "propertyPath":
                            path = inline;
                            pathLine = line;
                            break;
                        case "value":
                            value = inline;
                            break;
                    }
                }
                if (guid != null && pathLine >= 0) modifications.Add((guid, fileId, pathLine, path, value));
            }

            // References added by the overrides themselves: managedReferences[rid] = "Assembly Namespace.Type".
            var declared = new Dictionary<(string, long, long), Type>();
            foreach (var m in modifications)
            {
                if (!TryParseReferencePath(Unquote(m.Path), out long rid, out string rest) || rest != null) continue;
                var type = MigrationAssetIndex.DeclaredReferenceType(m.Value ?? "");
                if (type != null) declared[(m.Guid, m.FileId, rid)] = type;
            }

            foreach (var m in modifications)
            {
                string path = Unquote(m.Path);
                string rewritten = RewritePath(m.Guid, m.FileId, path, declared);
                if (rewritten == null || rewritten == path) continue;

                string original = _lines[m.PathLine];
                int keyAt = original.IndexOf("propertyPath:", StringComparison.Ordinal);
                bool quoted = m.Path.Length > 0 && (m.Path[0] == '\'' || m.Path[0] == '"');
                _edits.Add(new Edit(m.PathLine, m.PathLine + 1, new List<string>
                {
                    original.Substring(0, keyAt) + "propertyPath: " + (quoted ? "'" + rewritten + "'" : rewritten)
                }));
                _result.RewrittenOverrides++;
                _result.Changes.Add($"override {path} → {rewritten}");
            }
        }

        private string RewritePath(string guid, long fileId, string path, Dictionary<(string, long, long), Type> declared)
        {
            bool isReference = TryParseReferencePath(path, out long rid, out string rest);
            if (isReference && rest == null) return null; // a declaration
            if (!isReference) rest = path;

            int dot = rest.IndexOf('.');
            if (!MigrationFieldModel.MayLeadToStableRef(dot < 0 ? rest : rest.Substring(0, dot))) return null;

            Type type;
            if (!isReference) type = _index.ComponentType(guid, fileId);
            else if (!declared.TryGetValue((guid, fileId, rid), out type)) type = _index.ReferenceType(guid, fileId, rid);
            if (type == null) return null;

            var segments = rest.Split('.').ToList();
            if (!RewriteSegments(type, segments)) return null;
            return (isReference ? "managedReferences[" + rid.ToString(CultureInfo.InvariantCulture) + "]." : "") + string.Join(".", segments);
        }

        /// <summary><c>managedReferences[rid]</c> (rest <c>null</c>) or <c>managedReferences[rid].rest</c>.</summary>
        private static bool TryParseReferencePath(string path, out long rid, out string rest)
        {
            const string Prefix = "managedReferences[";
            rid = 0;
            rest = null;
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            int close = path.IndexOf(']', Prefix.Length);
            if (close < 0 || !long.TryParse(path.Substring(Prefix.Length, close - Prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out rid)) return false;
            if (close + 1 == path.Length) return true;
            if (path[close + 1] != '.') return false;
            rest = path.Substring(close + 2);
            return true;
        }

        /// <summary>
        /// Rewrites a path to the old layout of a field that is StableRef now. Aliases of the fields on the way are
        /// renamed along with it, but a path that reaches no StableRef field is left as it is: overrides of other
        /// renamed fields are not the migration's concern.
        /// </summary>
        private static bool RewriteSegments(Type type, List<string> segments)
        {
            var renamed = new List<(int Index, string Name)>();
            int i = 0;
            while (i < segments.Count && type != null)
            {
                var field = MigrationFieldModel.Find(type, segments[i]);
                if (field == null) return false;
                if (segments[i] != field.Name) renamed.Add((i, field.Name));

                bool isArray = i + 2 < segments.Count && segments[i + 1] == "Array";
                switch (field.Kind)
                {
                    case MigrationFieldModel.FieldKind.StableRef:
                        if (i + 1 != segments.Count) return false;
                        segments.Add(StableRefEntry.ValueFieldName);
                        Rename(segments, renamed);
                        return true;

                    case MigrationFieldModel.FieldKind.StableRefList:
                        // Only the size or a whole element: a path into an element's value is written via managedReferences.
                        if (!isArray || i + 3 != segments.Count) return false;
                        bool element = IsArrayData(segments[i + 2]);
                        if (!element && segments[i + 2] != "size") return false;
                        segments.Insert(i + 1, StableRefEntry.ListItemsFieldName);
                        if (element) segments.Add(StableRefEntry.ValueFieldName);
                        Rename(segments, renamed);
                        return true;

                    case MigrationFieldModel.FieldKind.Nested:
                        type = field.NestedType;
                        i++;
                        break;

                    case MigrationFieldModel.FieldKind.NestedArray:
                        if (!isArray || !IsArrayData(segments[i + 2])) return false;
                        type = field.NestedType;
                        i += 3;
                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool IsArrayData(string segment) =>
            segment.StartsWith("data[", StringComparison.Ordinal) && segment.EndsWith("]", StringComparison.Ordinal);

        private static void Rename(List<string> segments, List<(int Index, string Name)> renamed)
        {
            foreach (var (index, name) in renamed) segments[index] = name;
        }

        #endregion

        #region Unity YAML layout

        /// <summary>
        /// Keys of the mapping at <paramref name="column"/> in [start, end). The first line of a sequence item
        /// (<c>- key: value</c>) counts as a key line at the column after the dash.
        /// </summary>
        private IEnumerable<(int Line, string Key, string Inline)> Keys(int start, int end, int column)
        {
            for (int i = start; i < end; i++)
            {
                if (TryKey(_lines[i], column, out string key, out string inline, out _))
                    yield return (i, key, inline);
            }
        }

        private static bool TryKey(string line, int column, out string key, out string inline, out int keyStart)
        {
            key = inline = null;
            keyStart = Spaces(line);
            if (keyStart >= line.Length) return false;
            if (string.CompareOrdinal(line, keyStart, "- ", 0, 2) == 0) keyStart += 2;
            if (keyStart != column || keyStart >= line.Length) return false;

            // "key:" or "key: value"; a plain key never starts with a quote, a bracket or a comment.
            char first = line[keyStart];
            if (first == '\'' || first == '"' || first == '{' || first == '[' || first == '#' || first == ',' || first == ' ')
                return false;
            int colon = line.IndexOf(':', keyStart);
            while (colon >= 0 && colon + 1 < line.Length && line[colon + 1] != ' ')
                colon = line.IndexOf(':', colon + 1);
            if (colon <= keyStart) return false;

            key = line.Substring(keyStart, colon - keyStart);
            inline = colon + 1 < line.Length ? line.Substring(colon + 1).Trim() : "";
            return true;
        }

        /// <summary>
        /// End (exclusive) of the block of the key at <paramref name="line"/>: deeper lines, and the items of a sequence
        /// written at the key's own column. Trailing blank lines are not part of it.
        /// </summary>
        private int BlockEnd(int line, int end, int column)
        {
            int j = line + 1;
            while (j < end)
            {
                string text = _lines[j];
                if (IsBlank(text))
                {
                    j++;
                    continue;
                }
                int spaces = Spaces(text);
                if (spaces < column) break;
                if (spaces == column && string.CompareOrdinal(text, spaces, "- ", 0, 2) != 0) break;
                j++;
            }

            while (j - 1 > line && IsBlank(_lines[j - 1])) j--;
            return j;
        }

        /// <summary>Items of the sequence written at <paramref name="column"/> in [start, end).</summary>
        private IEnumerable<(int Start, int End)> Items(int start, int end, int column)
        {
            int itemStart = -1;
            for (int i = start; i < end; i++)
            {
                string text = _lines[i];
                if (IsBlank(text) || Spaces(text) != column || string.CompareOrdinal(text, column, "- ", 0, 2) != 0) continue;
                if (itemStart >= 0) yield return (itemStart, i);
                itemStart = i;
            }
            if (itemStart >= 0) yield return (itemStart, end);
        }

        private List<int> ContentLines(int start, int end)
        {
            var result = new List<int>();
            for (int i = start; i < end; i++)
                if (!IsBlank(_lines[i])) result.Add(i);
            return result;
        }

        private string KeyLine(int line, string name)
        {
            TryKey(_lines[line], ColumnOf(_lines[line]), out _, out _, out int keyStart);
            return _lines[line].Substring(0, keyStart) + name + ":";
        }

        private static int ColumnOf(string line)
        {
            int spaces = Spaces(line);
            return string.CompareOrdinal(line, spaces, "- ", 0, 2) == 0 ? spaces + 2 : spaces;
        }

        private void AddFieldEdit(int start, int end, List<string> lines, Type owner, string key)
        {
            _edits.Add(new Edit(start, end, lines));
            _result.ConvertedFields++;
            _result.Changes.Add($"field {owner.Name}.{key}");
        }

        private void Warn(Type owner, string key, string message) =>
            _result.Warnings.Add($"{_path}: {owner.Name}.{key} {message}");

        private static string Unquote(string text)
        {
            text = text?.Trim() ?? "";
            return text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[text.Length - 1] == text[0]
                ? text.Substring(1, text.Length - 2)
                : text;
        }

        private static int Spaces(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

        private static string Pad(int count) => new(' ', count);

        #endregion
    }
}
#endif
