#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SST.StableRef
{
    /// <summary>
    /// Reads the managed-reference records (<c>references: RefIds:</c>) of a text-serialized Unity file and removes the
    /// ones Unity can't read back. Pure text, no Unity API.
    /// </summary>
    /// <remarks>
    /// Unity writes the record of a class it can't load with its data as it was read; when that data is empty (a class
    /// without serialized fields) the record is written without its <c>data:</c> line — and a record of a missing class
    /// without <c>data:</c> makes Unity drop every managed reference of the object on the next load, with no error and
    /// no missing-type data. Removing the record (references to it become <c>rid: -2</c>, i.e. null) makes the object
    /// load again; the record held nothing but the class name.
    /// </remarks>
    internal static class StableRefYamlRecords
    {
        internal const long RidNull = -2;
        private const long RidUnknown = -1;

        /// <summary>A record of a missing class that Unity can't read back, and the fields that point at it.</summary>
        internal sealed class Unreadable
        {
            /// <summary>File id of the object the record belongs to (the <c>&amp;id</c> of its YAML document).</summary>
            public long ObjectFileId;
            public long Rid;
            public string ClassName;
            public string Namespace;
            public string Assembly;
            public readonly List<FieldUse> Fields = new();

            public string TypeName => string.IsNullOrEmpty(Namespace) ? ClassName : Namespace + "." + ClassName;
        }

        /// <summary>A field holding a reference to a removed record.</summary>
        internal struct FieldUse
        {
            /// <summary>Display path of the field, e.g. <c>Reference.Condition</c> or <c>List[2]</c>.</summary>
            public string Path;
            /// <summary>
            /// The <c>TypeId</c> of the StableRef entry the field is the value of; empty when the entry has none,
            /// <see langword="null"/> for a plain <c>[SerializeReference]</c> field.
            /// </summary>
            public string TypeId;

            public bool IsEntry => TypeId != null;
        }

        private sealed class Record
        {
            public long Rid;
            public string ClassName;
            public string Namespace;
            public string Assembly;
            public bool HasData;
            public int Start;
            public int End;
            public int DataLine = -1;
        }

        private struct Use
        {
            public int Line;
            public long Rid;
        }

        private sealed class Block
        {
            public long FileId;
            public int Start;
            public int End;
            public int RefIdsLine;
            public readonly List<Record> Records = new();
            public readonly List<Use> Uses = new();
        }

        /// <summary>A parsed file: its lines (with any <c>\r</c> kept) and its object blocks that have records.</summary>
        internal sealed class Document
        {
            internal static readonly Document Empty = new(Array.Empty<string>());

            internal readonly string[] Lines;
            private readonly List<Block> _blocks = new();

            internal Document(string[] lines)
            {
                Lines = lines;
                Parse();
            }

            /// <summary>
            /// Whether the object with <paramref name="fileId"/> has records of non-null references in the file —
            /// i.e. the file holds values for it.
            /// </summary>
            internal bool HasRecords(long fileId)
            {
                foreach (var block in _blocks)
                {
                    if (block.FileId != fileId) continue;
                    foreach (var r in block.Records)
                        if (IsReal(r)) return true;
                }
                return false;
            }

            /// <summary>The fields of the object with <paramref name="fileId"/> that point at its record <paramref name="rid"/>.</summary>
            internal List<FieldUse> FieldsUsing(long fileId, long rid)
            {
                var result = new List<FieldUse>();
                foreach (var block in _blocks)
                {
                    if (block.FileId != fileId) continue;
                    foreach (var use in block.Uses)
                        if (use.Rid == rid)
                            result.Add(DescribeUse(block, use.Line));
                }
                return result;
            }

            /// <summary>
            /// Records of classes <paramref name="resolves"/> (class, namespace, assembly) rejects that have no
            /// <c>data:</c> line, with the fields pointing at them.
            /// </summary>
            internal List<Unreadable> FindUnreadable(Func<string, string, string, bool> resolves)
            {
                var result = new List<Unreadable>();
                foreach (var block in _blocks)
                foreach (var r in block.Records)
                {
                    if (r.HasData || !IsReal(r) || resolves(r.ClassName, r.Namespace, r.Assembly)) continue;

                    var unreadable = new Unreadable
                    {
                        ObjectFileId = block.FileId,
                        Rid = r.Rid,
                        ClassName = r.ClassName,
                        Namespace = r.Namespace,
                        Assembly = r.Assembly
                    };
                    foreach (var use in block.Uses)
                        if (use.Rid == r.Rid)
                            unreadable.Fields.Add(DescribeUse(block, use.Line));
                    result.Add(unreadable);
                }
                return result;
            }

            /// <summary>
            /// Text with the records in <paramref name="remove"/> deleted and every reference to them in their object
            /// set to <c>rid: -2</c>.
            /// </summary>
            internal string Remove(List<Unreadable> remove)
            {
                var deleted = new HashSet<int>();
                var replaced = new Dictionary<int, string>();
                var nullRecordAfter = new Dictionary<int, string>();

                foreach (var block in _blocks)
                {
                    bool nulled = false;
                    foreach (var r in block.Records)
                    {
                        if (!Contains(remove, block.FileId, r.Rid)) continue;
                        for (int i = r.Start; i < r.End; i++) deleted.Add(i);
                        foreach (var use in block.Uses)
                            if (use.Rid == r.Rid)
                            {
                                replaced[use.Line] = NullReference(Lines[use.Line], r.Rid);
                                nulled = true;
                            }
                    }

                    // Unity logs an error for a null reference without the null record.
                    if (nulled && !block.Records.Exists(r => r.Rid == RidNull))
                    {
                        string pad = new(' ', Indent(Lines[block.RefIdsLine]));
                        string eol = Lines[block.RefIdsLine].EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
                        nullRecordAfter[block.RefIdsLine] =
                            pad + "- rid: -2" + eol + "\n" + pad + "  type: {class: , ns: , asm: }" + eol;
                    }
                }

                var sb = new StringBuilder();
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (deleted.Contains(i)) continue;
                    sb.Append(replaced.TryGetValue(i, out var line) ? line : Lines[i]);
                    if (i < Lines.Length - 1) sb.Append('\n');
                    if (nullRecordAfter.TryGetValue(i, out var record)) sb.Append(record).Append('\n');
                }
                return sb.ToString();
            }

            private static bool Contains(List<Unreadable> list, long fileId, long rid)
            {
                foreach (var u in list)
                    if (u.ObjectFileId == fileId && u.Rid == rid) return true;
                return false;
            }

            private static bool IsReal(Record r) =>
                r.Rid != RidNull && r.Rid != RidUnknown && !string.IsNullOrEmpty(r.ClassName);

            private void Parse()
            {
                Block block = null;
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (!Lines[i].StartsWith("--- ", StringComparison.Ordinal)) continue;
                    if (block != null) Close(block, i);
                    block = new Block { FileId = ParseFileId(Lines[i]), Start = i };
                }
                if (block != null) Close(block, Lines.Length);
            }

            private void Close(Block block, int end)
            {
                block.End = end;
                int refIds = -1;
                for (int i = block.Start + 1; i < end; i++)
                    if (Content(Lines[i]) == "RefIds:")
                    {
                        refIds = i;
                        break;
                    }
                if (refIds < 0) return;
                block.RefIdsLine = refIds;

                var headers = new HashSet<int>();
                int indent = Indent(Lines[refIds]);
                Record record = null;
                for (int i = refIds + 1; i < end; i++)
                {
                    string content = Content(Lines[i]);
                    if (content.Length == 0) continue;
                    int ind = Indent(Lines[i]);
                    if (ind < indent || (ind == indent && !content.StartsWith("- ", StringComparison.Ordinal))) break;

                    if (ind == indent)
                    {
                        record = null;
                        if (!TryParseRid(content.Substring(2), out long rid)) continue;
                        record = new Record { Rid = rid, Start = i, End = i + 1 };
                        block.Records.Add(record);
                        headers.Add(i);
                        continue;
                    }

                    if (record == null) continue;
                    record.End = i + 1;
                    if (ind != indent + 2) continue;
                    if (content.StartsWith("type:", StringComparison.Ordinal)) ParseType(content, record);
                    else if (content == "data:" || content.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        record.HasData = true;
                        record.DataLine = i;
                    }
                }

                if (block.Records.Count == 0) return;
                _blocks.Add(block);

                for (int i = block.Start + 1; i < end; i++)
                {
                    if (headers.Contains(i)) continue;
                    if (TryParseUse(Content(Lines[i]), out long rid) && rid != RidNull && rid != RidUnknown)
                        block.Uses.Add(new Use { Line = i, Rid = rid });
                }
            }

            private FieldUse DescribeUse(Block block, int line)
            {
                var visited = new HashSet<long>();
                string typeId = null;
                string path = FieldPath(block, line, visited, ref typeId, leaf: true);
                return new FieldUse { Path = path, TypeId = typeId };
            }

            /// <summary>Display path of the field at <paramref name="line"/>, through the records that hold it.</summary>
            private string FieldPath(Block block, int line, HashSet<long> visited, ref string typeId, bool leaf)
            {
                Record owner = null;
                foreach (var r in block.Records)
                    if (r.DataLine >= 0 && line > r.DataLine && line < r.End)
                    {
                        owner = r;
                        break;
                    }

                int stop = owner?.DataLine ?? block.Start + 1;
                var segments = Climb(line, stop, out int keyLine);
                string entryTypeId = keyLine >= 0 && segments.Count > 0 && segments[0] == StableRefEntry.ValueFieldName
                    ? FindSiblingTypeId(keyLine, stop)
                    : null;
                if (entryTypeId != null) segments.RemoveAt(0);
                if (leaf) typeId = entryTypeId;

                string local = JoinSegments(segments);
                if (owner == null) return local;

                string prefix = null;
                if (visited.Add(owner.Rid))
                    foreach (var use in block.Uses)
                        if (use.Rid == owner.Rid)
                        {
                            string ignored = null;
                            prefix = FieldPath(block, use.Line, visited, ref ignored, leaf: false);
                            break;
                        }
                prefix ??= "(" + owner.ClassName + ")";
                return local.Length == 0 ? prefix
                    : local[0] == '[' ? prefix + local
                    : prefix + "." + local;
            }

            /// <summary>
            /// Keys from the reference at <paramref name="from"/> up to (not including) line <paramref name="stop"/>,
            /// leaf first; a sequence element appends <c>[index]</c> to its sequence's key. <paramref name="keyLine"/>
            /// is the line of the leaf key.
            /// </summary>
            private List<string> Climb(int from, int stop, out int keyLine)
            {
                var segments = new List<string>();
                keyLine = -1;

                string first = Content(Lines[from]);
                int level = Indent(Lines[from]);
                bool firstItem = first.StartsWith("- ", StringComparison.Ordinal);
                string firstBody = firstItem ? first.Substring(2) : first;
                if (!firstBody.StartsWith("rid:", StringComparison.Ordinal))
                {
                    segments.Add(KeyOf(firstBody));
                    keyLine = from;
                }

                int sequence = firstItem ? level : -1;
                int index = 0;
                bool needKey = keyLine < 0 && !firstItem;

                for (int m = from - 1; m > stop; m--)
                {
                    string content = Content(Lines[m]);
                    if (content.Length == 0) continue;
                    int ind = Indent(Lines[m]);
                    bool item = content.StartsWith("- ", StringComparison.Ordinal);
                    string body = item ? content.Substring(2) : content;

                    if (sequence >= 0)
                    {
                        if (ind > sequence) continue;
                        if (item && ind == sequence)
                        {
                            index++;
                            continue;
                        }

                        segments.Add(KeyOf(body) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                        sequence = item ? ind : -1;
                        index = 0;
                        level = ind;
                        continue;
                    }

                    if (ind >= level) continue;

                    if (item)
                    {
                        if (ind + 2 < level)
                        {
                            segments.Add(KeyOf(body));
                            if (needKey) keyLine = m;
                        }
                        needKey = false;
                        sequence = ind;
                        index = 0;
                        level = ind;
                        continue;
                    }

                    segments.Add(KeyOf(body));
                    if (needKey) keyLine = m;
                    needKey = false;
                    level = ind;
                }

                return segments;
            }

            /// <summary>
            /// The <c>TypeId</c> written before the <c>Value</c> key at <paramref name="valueLine"/> in the same mapping,
            /// or <see langword="null"/> when there is none (a plain field named <c>Value</c>).
            /// </summary>
            private string FindSiblingTypeId(int valueLine, int stop)
            {
                bool item = Content(Lines[valueLine]).StartsWith("- ", StringComparison.Ordinal);
                int level = Indent(Lines[valueLine]) + (item ? 2 : 0);
                const string Key = StableRefEntry.TypeIdFieldName + ":";

                for (int m = valueLine - 1; m > stop; m--)
                {
                    string content = Content(Lines[m]);
                    if (content.Length == 0) continue;
                    int ind = Indent(Lines[m]);
                    bool isItem = content.StartsWith("- ", StringComparison.Ordinal);
                    if (isItem && ind + 2 == level)
                        return content.StartsWith("- " + Key, StringComparison.Ordinal)
                            ? content.Substring(2 + Key.Length).Trim()
                            : null;
                    if (ind < level) return null;
                    if (ind == level && content.StartsWith(Key, StringComparison.Ordinal))
                        return content.Substring(Key.Length).Trim();
                }
                return null;
            }
        }

        /// <summary>Parses <paramref name="text"/>; a file without records parses to an empty document.</summary>
        internal static Document Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("RefIds:", StringComparison.Ordinal) < 0) return Document.Empty;
            return new Document(text.Split('\n'));
        }

        private static string JoinSegments(List<string> leafFirst)
        {
            var sb = new StringBuilder();
            for (int i = leafFirst.Count - 1; i >= 0; i--)
            {
                string segment = leafFirst[i];
                if (segment.StartsWith(StableRefEntry.ListItemsFieldName + "[", StringComparison.Ordinal))
                    segment = segment.Substring(StableRefEntry.ListItemsFieldName.Length);
                if (sb.Length > 0 && segment[0] != '[') sb.Append('.');
                sb.Append(segment);
            }
            return sb.ToString();
        }

        private static string NullReference(string line, long rid)
        {
            string ridText = rid.ToString(CultureInfo.InvariantCulture);
            string inline = "{rid: " + ridText + "}";
            if (line.IndexOf(inline, StringComparison.Ordinal) >= 0) return line.Replace(inline, "{rid: -2}");

            int at = line.LastIndexOf("rid: " + ridText, StringComparison.Ordinal);
            return line.Substring(0, at) + "rid: -2" + line.Substring(at + 5 + ridText.Length);
        }

        private static bool TryParseUse(string content, out long rid)
        {
            rid = 0;
            string body = content.StartsWith("- ", StringComparison.Ordinal) ? content.Substring(2) : content;
            if (body.StartsWith("rid:", StringComparison.Ordinal)) return TryParseRid(body, out rid);

            int at = content.IndexOf("{rid: ", StringComparison.Ordinal);
            if (at < 0) return false;
            int end = content.IndexOf('}', at);
            return end > at && long.TryParse(content.Substring(at + 6, end - at - 6), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out rid);
        }

        private static bool TryParseRid(string body, out long rid)
        {
            rid = 0;
            return body.StartsWith("rid:", StringComparison.Ordinal)
                   && long.TryParse(body.Substring(4).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                       out rid);
        }

        private static void ParseType(string content, Record record)
        {
            int open = content.IndexOf('{');
            int close = content.LastIndexOf('}');
            if (open < 0 || close <= open) return;
            string inner = content.Substring(open + 1, close - open - 1);

            int asm = inner.LastIndexOf(", asm:", StringComparison.Ordinal);
            int ns = asm < 0 ? -1 : inner.LastIndexOf(", ns:", asm, StringComparison.Ordinal);
            if (ns < 0 || !inner.StartsWith("class:", StringComparison.Ordinal)) return;

            record.ClassName = Unquote(inner.Substring(6, ns - 6));
            record.Namespace = Unquote(inner.Substring(ns + 5, asm - ns - 5));
            record.Assembly = Unquote(inner.Substring(asm + 6));
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        private static long ParseFileId(string header)
        {
            int at = header.IndexOf('&');
            if (at < 0) return 0;
            int end = at + 1;
            while (end < header.Length && (char.IsDigit(header[end]) || (end == at + 1 && header[end] == '-'))) end++;
            return long.TryParse(header.Substring(at + 1, end - at - 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long id) ? id : 0;
        }

        private static string KeyOf(string body)
        {
            int colon = body.IndexOf(':');
            return colon >= 0 ? body.Substring(0, colon) : body;
        }

        private static int Indent(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private static string Content(string line) => line.Trim(' ', '\r', '\t');
    }
}
#endif
