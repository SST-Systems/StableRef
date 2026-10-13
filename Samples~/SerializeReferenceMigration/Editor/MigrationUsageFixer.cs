#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Fixes the code that uses fields retyped to <c>StableRef&lt;T&gt;</c> / <c>StableRefList&lt;T&gt;</c>, as text: the
    /// compiler errors such a retype causes (<see cref="FixDiagnostics"/>), and the uses the compiler accepts but that now
    /// mean something else (<see cref="FixSilentUses"/>) — <c>field != null</c>, <c>field is null</c> and
    /// <c>field.GetType()</c> test the wrapper, <c>field.Value</c> reads the wrapper's value instead of the value's own
    /// <c>Value</c>.
    /// </summary>
    /// <remarks>
    /// Works on the text alone, no Unity API: comments, strings and preprocessor lines are masked out first
    /// (<see cref="Mask"/>), then expressions are found around the place a diagnostic points at. Only shapes that can be
    /// read reliably are rewritten; everything else is returned as left for a manual fix.
    /// </remarks>
    public static class MigrationUsageFixer
    {
        /// <summary>A compiler message at a 1-based line and column.</summary>
        public struct Diagnostic
        {
            public string Code;
            public int Line;
            public int Column;
            public string Message;

            public Diagnostic(string code, int line, int column, string message)
            {
                Code = code;
                Line = line;
                Column = column;
                Message = message;
            }

            public override string ToString() => $"({Line},{Column}): {Code}: {Message}";
        }

        /// <summary>A retyped field as the silent-use check sees it in one file.</summary>
        public sealed class Field
        {
            /// <summary>The name used in code (the property's name for an auto-property).</summary>
            public string Name;
            public bool IsList;
            /// <summary>Unqualified uses (<c>name</c>, <c>this.name</c>) in this file are this field.</summary>
            public bool OwnFile;
            /// <summary>No other member in the project has this name: qualified uses (<c>x.name</c>) are this field.</summary>
            public bool UniqueName;
            /// <summary>
            /// Members of the value type that the wrapper has too (<c>Value</c>, <c>HasValue</c>…): <c>name.Member</c>
            /// compiles but reaches the wrapper's member.
            /// </summary>
            public HashSet<string> SharedMembers = new(StringComparer.Ordinal);
        }

        private struct Edit
        {
            public int Position;
            public int Remove;
            public string Insert;
        }

        /// <summary>The diagnostics <see cref="FixDiagnostics"/> knows; others are never touched.</summary>
        public static readonly HashSet<string> HandledCodes = new(StringComparer.Ordinal)
        {
            "CS1061", "CS0029", "CS0266", "CS1503", "CS0030", "CS0039", "CS1579", "CS0173", "CS8121", "CS0019", "CS0184"
        };

        private const string ValueSuffix = ".Value";

        #region Compiler errors

        /// <summary>
        /// Applies the fixes the diagnostics of one file call for and returns the new text (the same instance when nothing
        /// changed). Only diagnostics about a <c>StableRef&lt;T&gt;</c> / <c>StableRefList&lt;T&gt;</c> value are fixed —
        /// a read gets <c>.Value</c>, an assignment <c>.Set(…)</c> / <c>.Value =</c> / <c>new(…)</c>, a list
        /// <c>.Length</c> becomes <c>.Count</c> — plus <c>CS0184</c> (<c>field is Type</c>, a warning) on one of
        /// <paramref name="fieldNames"/>.
        /// </summary>
        /// <param name="needsLinq">Set when a fix calls <c>ToList()</c>: the file needs <c>using System.Linq;</c>.</param>
        public static string FixDiagnostics(string text, IEnumerable<Diagnostic> diagnostics, ICollection<string> fieldNames,
            List<string> changes, List<Diagnostic> unfixed, out bool needsLinq)
        {
            needsLinq = false;
            string masked = Mask(text);
            var lineStarts = LineStarts(text);
            var edits = new List<Edit>();

            foreach (var diagnostic in diagnostics)
            {
                if (!HandledCodes.Contains(diagnostic.Code)) continue;
                int offset = Offset(lineStarts, diagnostic.Line, diagnostic.Column, text.Length);
                int before = edits.Count;
                bool fixedIt = offset >= 0 && TryFix(diagnostic, masked, text, offset, fieldNames, edits, ref needsLinq);
                if (fixedIt && edits.Count > before)
                    changes.Add($"({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code}: {Describe(text, edits, before)}");
                else if (!fixedIt && MentionsWrapper(diagnostic.Message))
                    unfixed.Add(diagnostic);
            }

            return Apply(text, edits);
        }

        private static bool TryFix(Diagnostic d, string m, string text, int offset, ICollection<string> names,
            List<Edit> edits, ref bool needsLinq)
        {
            var quoted = Quoted(d.Message);
            switch (d.Code)
            {
                case "CS1061":
                {
                    // 'StableRef<T>' does not contain a definition for 'Member'… — reported at the member's name.
                    if (quoted.Count < 2) return false;
                    var kind = WrapperKind(quoted[0]);
                    string member = quoted[1];
                    if (kind == Kind.None) return false;
                    if (m[offset] == '.' && offset > 0 && m[offset - 1] == '?' && WordAt(m, SkipWs(m, offset + 1)) == member)
                        offset = SkipWs(m, offset + 1);
                    if (WordAt(m, offset) != member) return false;
                    int dot = SkipWsBack(m, offset);
                    if (dot == 0 || m[dot - 1] != '.') return false;
                    if (kind == Kind.List)
                    {
                        if (member != "Length") return false;
                        Add(edits, offset, member.Length, "Count");
                        return true;
                    }
                    if (dot >= 2 && m[dot - 2] == '?') Add(edits, dot - 2, 2, ".Value?.");
                    else Add(edits, offset, 0, "Value.");
                    return true;
                }
                case "CS0029":
                case "CS0266":
                case "CS1503":
                case "CS0030":
                case "CS0039":
                {
                    // Cannot (implicitly) convert (type | from) 'A' to 'B'.
                    if (quoted.Count < 2) return false;
                    var from = WrapperKind(quoted[0]);
                    var to = WrapperKind(quoted[1]);
                    if (from == Kind.Single)
                    {
                        int start = offset;
                        if (d.Code == "CS0030")
                        {
                            // (T)field — reported at the cast; the operand follows the parenthesized type.
                            if (m[offset] != '(') return false;
                            int close = MatchClose(m, offset);
                            if (close < 0) return false;
                            start = SkipWs(m, close);
                        }
                        return FixRead(m, start, names, edits);
                    }
                    if (from == Kind.List && to == Kind.None && d.Code != "CS0030" && d.Code != "CS0039")
                    {
                        string target = SimpleName(quoted[1]);
                        bool array = target.EndsWith("[]", StringComparison.Ordinal);
                        bool list = target.StartsWith("List<", StringComparison.Ordinal);
                        if (!array && !list) return false;
                        string word = WordBefore(m, SkipWsBack(m, offset));
                        if (word == "ref" || word == "out") return false;
                        int end = ChainEnd(m, offset, out _);
                        if (end < 0 || !ClosesExpression(m, end)) return false;
                        Add(edits, end, 0, array ? ".ToArray()" : ".ToList()");
                        needsLinq |= list;
                        return true;
                    }
                    if (to != Kind.None && from == Kind.None && d.Code != "CS1503")
                        return FixWrite(m, text, offset, to == Kind.List, edits);
                    // `cond ? field : other` converted as a whole: the message names no wrapper (the conditional is
                    // target-typed), so only branches that are a retyped field are touched.
                    return from == Kind.None && to == Kind.None && d.Code != "CS0030" && d.Code != "CS0039"
                           && FixConditional(m, offset, names, edits);
                }
                case "CS1579":
                    // foreach statement cannot operate on variables of type 'StableRef<T>'.
                    return quoted.Count > 0 && WrapperKind(quoted[0]) == Kind.Single && FixRead(m, offset, names, edits);
                case "CS0173":
                    // Type of conditional expression cannot be determined… — reported at the condition.
                    return quoted.Any(q => WrapperKind(q) == Kind.Single) && FixConditional(m, offset, names, edits);
                case "CS8121":
                    // An expression of type 'StableRef<T>' cannot be handled by a pattern of type 'B' — reported at the pattern.
                    return quoted.Count > 0 && WrapperKind(quoted[0]) == Kind.Single && FixPatternSubject(m, offset, edits);
                case "CS0019":
                {
                    // Operator 'op' cannot be applied to operands of type 'A' and 'B' — reported at the left operand.
                    if (quoted.Count < 3) return false;
                    string op = quoted[0];
                    bool left = WrapperKind(quoted[1]) == Kind.Single, right = WrapperKind(quoted[2]) == Kind.Single;
                    if (!left && !right) return false;
                    int opAt = FindOperator(m, offset, op);
                    if (opAt < 0) return false;
                    bool any = false;
                    if (left)
                    {
                        int end = ChainEnd(m, offset, out _);
                        if (end < 0 || SkipWs(m, end) != opAt) return false;
                        Add(edits, end, 0, ValueSuffix);
                        any = true;
                    }
                    if (right)
                    {
                        int start = SkipWs(m, opAt + op.Length);
                        int end = ChainEnd(m, start, out _);
                        if (end < 0 || !EndsExpression(m, end)) return any;
                        Add(edits, end, 0, ValueSuffix);
                        any = true;
                    }
                    return any;
                }
                case "CS0184":
                {
                    // The given expression is never of the provided type — `field is Type`, a warning only.
                    int end = ChainEnd(m, offset, out string last);
                    if (end < 0 || last == null || !names.Contains(last) || WordAt(m, SkipWs(m, end)) != "is") return false;
                    Add(edits, end, 0, ValueSuffix);
                    return true;
                }
            }
            return false;
        }

        /// <summary>A read of a wrapper starting at <paramref name="start"/>: <c>.Value</c> after it, or in a conditional's branches.</summary>
        private static bool FixRead(string m, int start, ICollection<string> names, List<Edit> edits)
        {
            if (SplitConditional(m, start, out _, out _, out _)) return FixConditional(m, start, names, edits);
            int end = ChainEnd(m, start, out _);
            if (end < 0 || !ClosesExpression(m, end)) return false;
            Add(edits, end, 0, ValueSuffix);
            return true;
        }

        /// <summary>
        /// <c>cond ? a : b</c> starting at <paramref name="start"/> (the compiler points at the condition): every branch
        /// that is just one of <paramref name="names"/> gets <c>.Value</c>.
        /// </summary>
        private static bool FixConditional(string m, int start, ICollection<string> names, List<Edit> edits)
        {
            if (!SplitConditional(m, start, out int question, out int colon, out int end)) return false;
            bool any = false;
            foreach (var (from, to) in new[] { (question + 1, colon), (colon + 1, end) })
            {
                int s = SkipWs(m, from), e = SkipWsBack(m, to);
                int chainEnd = ChainEnd(m, s, out string last);
                if (chainEnd != e || last == null || !names.Contains(last)) continue;
                Add(edits, e, 0, ValueSuffix);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// An assignment of a plain value to a wrapper, reported at the right-hand side: a statement becomes
        /// <c>x.Set(value);</c> (or <c>x.Value = …</c> when the value spans lines), an object-initializer member
        /// <c>x = new(value)</c> (one line only), any other assignment <c>x.Value = …</c>. A list gets <c>new(values)</c>.
        /// </summary>
        private static bool FixWrite(string m, string text, int position, bool isList, List<Edit> edits)
        {
            int eq = AssignmentBefore(m, position);
            if (eq < 0) return false;
            int offset = SkipWs(m, eq);
            int lhsEnd = SkipWsBack(m, eq - 1);
            int lhsStart = ChainStart(m, lhsEnd);
            if (lhsStart < 0 || ChainEnd(m, lhsStart, out _) != lhsEnd) return false;

            int terminator = ExpressionEnd(m, offset);
            int rhsEnd = SkipWsBack(m, terminator);
            if (rhsEnd <= offset) return false;
            bool singleLine = m.IndexOf('\n', offset, rhsEnd - offset) < 0;
            char after = terminator < m.Length ? m[terminator] : '\0';
            int before = SkipWsBack(m, lhsStart);
            char prev = before > 0 ? m[before - 1] : '\0';
            string prevWord = WordBefore(m, before);
            bool statement = after == ';' && (prev == '\0' || prev == ';' || prev == '{' || prev == '}' || prev == ')' || prevWord == "else");
            bool initializer = (after == ',' || after == '}') && (prev == '{' || prev == ',');

            if (!isList && statement && singleLine)
            {
                Add(edits, lhsEnd, offset - lhsEnd, ".Set(");
                Add(edits, rhsEnd, 0, ")");
                return true;
            }
            if (!isList && !initializer)
            {
                Add(edits, lhsEnd, 0, ValueSuffix);
                return true;
            }
            if (!singleLine || (!statement && !initializer)) return false;
            string value = text.Substring(offset, rhsEnd - offset);
            Add(edits, offset, rhsEnd - offset, isList ? ListInitializer(value) : value == "null" ? "new()" : $"new({value})");
            return true;
        }

        /// <summary>
        /// The index right after the <c>=</c> of the simple assignment whose right-hand side contains
        /// <paramref name="position"/> (the compiler points at the value, or at a branch of a conditional value); -1 when
        /// there is none before the statement / argument / initializer member starts.
        /// </summary>
        private static int AssignmentBefore(string m, int position)
        {
            int depth = 0;
            for (int i = position - 1; i >= 0; i--)
            {
                char c = m[i];
                if (c == ')' || c == ']' || c == '}') { depth++; continue; }
                if (c == '(' || c == '[' || c == '{')
                {
                    if (depth-- == 0) return -1;
                    continue;
                }
                if (depth > 0) continue;
                if (c == ';' || c == ',') return -1;
                if (c == '=' && !(i + 1 < m.Length && (m[i + 1] == '=' || m[i + 1] == '>'))
                    && !(i > 0 && "=!<>+-*/%&|^?".IndexOf(m[i - 1]) >= 0))
                    return i + 1;
            }
            return -1;
        }

        /// <summary>
        /// A pattern on a wrapper: <c>x is Pattern</c>, <c>case Pattern:</c> of <c>switch (x)</c>, or an arm of
        /// <c>x switch { … }</c> — the subject before <c>is</c> / inside <c>switch ( )</c> / before <c>switch</c> gets
        /// <c>.Value</c>.
        /// </summary>
        private static bool FixPatternSubject(string m, int offset, List<Edit> edits)
        {
            // Walk back over the pattern (and earlier patterns joined by `or` / `and`) to what introduced it.
            int depth = 0, limit = Math.Max(0, offset - 2000);
            for (int p = offset; p > limit;)
            {
                p = SkipWsBack(m, p);
                if (p == 0) break;
                char c = m[p - 1];
                if (IsIdentifierChar(c))
                {
                    string word = WordBefore(m, p);
                    if (depth == 0 && word == "is")
                    {
                        int end = SkipWsBack(m, p - 2);
                        if (end == 0 || !(IsIdentifierChar(m[end - 1]) || m[end - 1] == ')' || m[end - 1] == ']')) return false;
                        Add(edits, end, 0, ValueSuffix);
                        return true;
                    }
                    if (depth == 0 && word == "case") return FixSwitchStatementSubject(m, p, edits);
                    while (p > 0 && IsIdentifierChar(m[p - 1])) p--;
                    continue;
                }
                if (c == ')' || c == ']' || c == '}') { depth++; p--; continue; }
                if (c == '(' || c == '[' || c == '{')
                {
                    if (depth > 0) { depth--; p--; continue; }
                    if (c == '{') return FixSwitchExpressionSubject(m, p - 1, edits);
                    if (c == '[') return false;
                    p--;
                    continue;
                }
                if (depth == 0 && c == ',')
                {
                    int open = UnmatchedOpenBrace(m, p - 1);
                    return open >= 0 && FixSwitchExpressionSubject(m, open, edits);
                }
                if (depth == 0 && (c == ';' || (c == '>' && p > 1 && m[p - 2] == '='))) return false;
                p--;
            }
            return false;
        }

        /// <summary><c>switch (subject) { … case Pattern:</c> — <paramref name="casePosition"/> is right after <c>case</c>.</summary>
        private static bool FixSwitchStatementSubject(string m, int casePosition, List<Edit> edits)
        {
            int open = UnmatchedOpenBrace(m, casePosition - "case".Length);
            if (open < 0) return false;
            int beforeBrace = SkipWsBack(m, open);
            if (beforeBrace == 0 || m[beforeBrace - 1] != ')') return false;
            int close = beforeBrace - 1;
            int openParen = MatchOpen(m, close);
            if (openParen < 0 || WordBefore(m, SkipWsBack(m, openParen)) != "switch") return false;
            Add(edits, SkipWsBack(m, close), 0, ValueSuffix);
            return true;
        }

        /// <summary><c>subject switch { Pattern => … }</c> — <paramref name="brace"/> is the arms' <c>{</c>.</summary>
        private static bool FixSwitchExpressionSubject(string m, int brace, List<Edit> edits)
        {
            int beforeBrace = SkipWsBack(m, brace);
            if (WordBefore(m, beforeBrace) != "switch") return false;
            int subjectEnd = SkipWsBack(m, beforeBrace - "switch".Length);
            if (subjectEnd == 0) return false;
            Add(edits, subjectEnd, 0, ValueSuffix);
            return true;
        }

        #endregion

        #region Silent uses

        /// <summary>
        /// Finds the uses of retyped fields that still compile but now test or read the wrapper — <c>x == null</c>,
        /// <c>x != null</c>, <c>null == x</c>, <c>x is …</c>, <c>x as T</c>, <c>ReferenceEquals(x, null)</c>,
        /// <c>x.GetType()</c>, <c>x.Equals(…)</c>, <c>x.Member</c> where the value type has a member the wrapper has too
        /// (<c>Value</c>) — and inserts <c>.Value</c>. A use is fixed only when the name surely means the field
        /// (<see cref="Field.OwnFile"/> for <c>x</c> / <c>this.x</c> and the file declares no local of that name,
        /// <see cref="Field.UniqueName"/> for <c>a.x</c>); other such uses are added to <paramref name="review"/>.
        /// Lists are skipped: a <c>StableRefList&lt;T&gt;</c> is never null, which is what the old checks guarded.
        /// </summary>
        public static string FixSilentUses(string text, IReadOnlyDictionary<string, Field> fields, List<string> changes,
            List<string> review)
        {
            string m = Mask(text);
            var lineStarts = LineStarts(text);
            var edits = new List<Edit>();
            HashSet<string> localNames = null;

            for (int i = 0; i < m.Length; i++)
            {
                if (!IsIdentifierStart(m[i]) || (i > 0 && (IsIdentifierChar(m[i - 1]) || m[i - 1] == '@'))) continue;
                int end = i;
                while (end < m.Length && IsIdentifierChar(m[end])) end++;
                string name = m.Substring(i, end - i);
                int start = i;
                i = end - 1;
                if (!fields.TryGetValue(name, out var field) || field.IsList) continue;

                string pattern = SilentPattern(m, start, end, field, out int insertAt, out int remove, out string insert);
                if (pattern == null) continue;

                int before = SkipWsBack(m, start);
                bool qualified = before > 0 && m[before - 1] == '.';
                bool viaThis = qualified && WordBefore(m, SkipWsBack(m, before - 1)) is "this" or "base";
                bool sure;
                if (!qualified || viaThis)
                {
                    if (!field.OwnFile) continue;
                    localNames ??= DeclaredLocals(m, fields);
                    sure = !localNames.Contains(name);
                }
                else
                {
                    sure = field.UniqueName;
                }

                string where = $"({LineOf(lineStarts, start) + 1}) {Snippet(text, start, end)}";
                if (!sure)
                {
                    review.Add($"{where} — {pattern} on '{name}' may test the StableRef wrapper now; if '{name}' is the retyped field, add .Value");
                    continue;
                }
                Add(edits, insertAt, remove, insert);
                changes.Add($"{where} — {pattern}: .Value added");
            }

            return Apply(text, edits);
        }

        /// <summary>The silent pattern around the identifier at [start, end), and the edit that fixes it.</summary>
        private static string SilentPattern(string m, int start, int end, Field field, out int insertAt, out int remove, out string insert)
        {
            insertAt = end;
            remove = 0;
            insert = ValueSuffix;

            // A declaration (`IEffect effect`, `var effect`, `effect =>`) or a call of a method with the same name.
            int next = SkipWs(m, end);
            if (next < m.Length && m[next] == '(') return null;
            if (IsDeclarationName(m, start, end)) return null;
            if (Next(m, next, "=>")) return null;

            if (next < m.Length && m[next] == '=' && !Next(m, next, "==") && !Next(m, next, "=>")
                && WordAt(m, SkipWs(m, next + 1)) == "null" && ClosesExpression(m, SkipWs(m, next + 1) + 4))
                return "= null";
            if (next < m.Length && m[next] == '.' && !Next(m, next, ".."))
            {
                string member = WordAt(m, SkipWs(m, next + 1));
                if (member == "GetType" || member == "Equals" || field.SharedMembers.Contains(member)) return $".{member}";
                return null;
            }
            if (Next(m, next, "?.") )
            {
                string member = WordAt(m, SkipWs(m, next + 2));
                if (member == "GetType" || member == "Equals" || field.SharedMembers.Contains(member))
                {
                    insertAt = next;
                    remove = 2;
                    insert = ".Value?.";
                    return $"?.{member}";
                }
                return null;
            }
            if ((Next(m, next, "==") || Next(m, next, "!=")) && WordAt(m, SkipWs(m, next + 2)) == "null")
                return m.Substring(next, 2) + " null";
            string word = WordAt(m, next);
            if (word == "is" || word == "as") return word;

            int chainStart = ChainStart(m, end);
            int before = SkipWsBack(m, chainStart >= 0 ? chainStart : start);
            if (before >= 2 && m[before - 1] == '=' && (m[before - 2] == '=' || m[before - 2] == '!')
                && WordBefore(m, SkipWsBack(m, before - 2)) == "null")
                return "null " + m.Substring(before - 2, 2);
            if (before > 0 && m[before - 1] == '(' && WordBefore(m, SkipWsBack(m, before - 1)) == "ReferenceEquals")
                return "ReferenceEquals";
            return null;
        }

        /// <summary>Names of <paramref name="fields"/> that the file also declares as a local, parameter or another member.</summary>
        private static HashSet<string> DeclaredLocals(string m, IReadOnlyDictionary<string, Field> fields)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < m.Length; i++)
            {
                if (!IsIdentifierStart(m[i]) || (i > 0 && IsIdentifierChar(m[i - 1]))) continue;
                int end = i;
                while (end < m.Length && IsIdentifierChar(m[end])) end++;
                string name = m.Substring(i, end - i);
                if (fields.ContainsKey(name) && !result.Contains(name))
                {
                    int next = SkipWs(m, end);
                    // The field's own declaration is the one whose type is the wrapper.
                    bool declaration = IsDeclarationName(m, i, end) && !DeclaredAsWrapper(m, i);
                    bool lambda = Next(m, next, "=>");
                    if (declaration || lambda) result.Add(name);
                }
                i = end - 1;
            }
            return result;
        }

        private static bool DeclaredAsWrapper(string m, int nameStart)
        {
            int p = SkipWsBack(m, nameStart);
            if (p == 0 || m[p - 1] != '>') return false;
            int open = MatchAngleOpen(m, p - 1);
            if (open < 0) return false;
            string type = WordBefore(m, SkipWsBack(m, open));
            return type == "StableRef" || type == "StableRefList";
        }

        /// <summary>Whether the identifier at [start, end) is declared there: preceded by a type and followed by <c>= ; , ) in</c>.</summary>
        private static bool IsDeclarationName(string m, int start, int end)
        {
            int next = SkipWs(m, end);
            bool closes = next >= m.Length || "=;,)".IndexOf(m[next]) >= 0 || WordAt(m, next) == "in";
            if (!closes || Next(m, next, "==")) return false;
            int p = SkipWsBack(m, start);
            if (p == 0) return false;
            char c = m[p - 1];
            if (c == '>' || c == ']' || c == '?') return true;
            if (!IsIdentifierChar(c)) return false;
            string word = WordBefore(m, p);
            return word.Length > 0 && !Keywords.Contains(word);
        }

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "return", "is", "as", "in", "out", "ref", "new", "case", "when", "and", "or", "not", "throw", "await", "yield",
            "else", "typeof", "sizeof", "nameof", "default", "using", "this", "base", "goto", "params", "readonly"
        };

        #endregion

        #region Masking

        /// <summary>
        /// The text with comments, the contents of string and character literals and preprocessor lines replaced by
        /// spaces (line breaks kept), so offsets and lines stay the same and only code is left to read. The holes of an
        /// interpolated string stay, as parenthesized expressions.
        /// </summary>
        public static string Mask(string text)
        {
            var c = text.ToCharArray();
            int n = c.Length;
            bool lineStart = true;
            for (int i = 0; i < n;)
            {
                char ch = c[i];
                if (ch == '\n') { lineStart = true; i++; continue; }
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (lineStart && ch == '#')
                {
                    while (i < n && c[i] != '\n') c[i++] = ' ';
                    continue;
                }
                lineStart = false;
                if (ch == '/' && i + 1 < n && c[i + 1] == '/')
                {
                    while (i < n && c[i] != '\n') c[i++] = ' ';
                    continue;
                }
                if (ch == '/' && i + 1 < n && c[i + 1] == '*')
                {
                    c[i++] = ' ';
                    c[i++] = ' ';
                    while (i < n && !(c[i] == '*' && i + 1 < n && c[i + 1] == '/')) Blank(c, i++);
                    if (i < n) { c[i++] = ' '; c[i++] = ' '; }
                    continue;
                }
                if (ch == '"' || ((ch == '@' || ch == '$') && StringPrefix(c, i, out _)))
                {
                    i = SkipString(c, i);
                    continue;
                }
                if (ch == '\'')
                {
                    int j = i + 1;
                    while (j < n && c[j] != '\'' && c[j] != '\n') j += c[j] == '\\' ? 2 : 1;
                    for (int k = i + 1; k < j && k < n; k++) Blank(c, k);
                    i = Math.Min(j + 1, n);
                    continue;
                }
                i++;
            }
            return new string(c);
        }

        /// <summary>Whether a string literal starts at <paramref name="i"/> with an <c>@</c> / <c>$</c> prefix.</summary>
        private static bool StringPrefix(char[] c, int i, out int quote)
        {
            quote = i;
            while (quote < c.Length && (c[quote] == '@' || c[quote] == '$') && quote - i < 2) quote++;
            return quote > i && quote < c.Length && c[quote] == '"';
        }

        /// <summary>Blanks a string literal starting at <paramref name="i"/> (prefix included) and returns the index after it.</summary>
        private static int SkipString(char[] c, int i)
        {
            int n = c.Length;
            bool verbatim = false, interpolated = false;
            while (c[i] != '"')
            {
                verbatim |= c[i] == '@';
                interpolated |= c[i] == '$';
                c[i++] = ' ';
            }
            i++;
            while (i < n)
            {
                char ch = c[i];
                if (ch == '"')
                {
                    if (verbatim && i + 1 < n && c[i + 1] == '"') { c[i++] = ' '; c[i++] = ' '; continue; }
                    return i + 1;
                }
                if (!verbatim && ch == '\n') return i;
                if (!verbatim && ch == '\\') { Blank(c, i++); if (i < n) Blank(c, i++); continue; }
                if (interpolated && ch == '{')
                {
                    if (i + 1 < n && c[i + 1] == '{') { c[i++] = ' '; c[i++] = ' '; continue; }
                    // A hole stays code, as a parenthesized expression; its alignment and format are blanked.
                    c[i++] = '(';
                    int depth = 0;
                    bool format = false;
                    while (i < n)
                    {
                        char h = c[i];
                        if (!format && (h == '"' || ((h == '@' || h == '$') && StringPrefix(c, i, out _)))) { i = SkipString(c, i); continue; }
                        if (h == '(' || h == '[' || h == '{') depth++;
                        else if (h == ')' || h == ']') depth--;
                        else if (h == '}' && depth-- == 0) { c[i++] = ')'; break; }
                        else if (depth == 0 && (h == ':' || h == ',')) format = true;
                        if (format) Blank(c, i);
                        i++;
                    }
                    continue;
                }
                Blank(c, i++);
            }
            return i;
        }

        private static void Blank(char[] c, int i)
        {
            if (c[i] != '\n' && c[i] != '\r') c[i] = ' ';
        }

        #endregion

        #region Expressions

        private enum Kind { None, Single, List }

        private static Kind WrapperKind(string type)
        {
            type = SimpleName(type);
            if (type.StartsWith("StableRef<", StringComparison.Ordinal)) return Kind.Single;
            if (type.StartsWith("StableRefList<", StringComparison.Ordinal)) return Kind.List;
            return Kind.None;
        }

        private static bool MentionsWrapper(string message) => Quoted(message).Any(q => WrapperKind(q) != Kind.None);

        /// <summary>The type name without its namespace: <c>System.Collections.Generic.List&lt;X&gt;</c> → <c>List&lt;X&gt;</c>.</summary>
        private static string SimpleName(string type)
        {
            int generic = type.IndexOf('<');
            int dot = type.LastIndexOf('.', generic < 0 ? type.Length - 1 : generic);
            return dot < 0 ? type : type.Substring(dot + 1);
        }

        /// <summary>
        /// The quoted parts of a compiler message: <c>'…'</c> in English, <c>"…"</c> or <c>«…»</c> in localized compilers.
        /// </summary>
        private static List<string> Quoted(string message)
        {
            var result = new List<string>();
            for (int i = 0; i < message.Length; i++)
            {
                char open = message[i];
                char close = open == '«' ? '»' : open;
                if (open != '\'' && open != '"' && open != '«') continue;
                int end = message.IndexOf(close, i + 1);
                if (end < 0) break;
                result.Add(message.Substring(i + 1, end - i - 1));
                i = end;
            }
            return result;
        }

        /// <summary>
        /// The end of a primary expression starting at <paramref name="start"/>: an identifier or a parenthesized
        /// expression followed by member accesses (<c>.x</c>, <c>?.x</c>), calls, indexers and <c>!</c>; -1 when none
        /// starts there. <paramref name="last"/> is the last member's name, <c>null</c> when it ends with a call or indexer.
        /// </summary>
        private static int ChainEnd(string m, int start, out string last)
        {
            last = null;
            int n = m.Length, i = start;
            if (i >= n) return -1;
            if (m[i] == '(')
            {
                i = MatchClose(m, i);
                if (i < 0) return -1;
            }
            else if (IsIdentifierStart(m[i]))
            {
                while (i < n && IsIdentifierChar(m[i])) i++;
                last = m.Substring(start, i - start);
                if (Keywords.Contains(last) && last != "this" && last != "base") return -1;
            }
            else return -1;

            while (true)
            {
                int j = SkipWs(m, i);
                int dot = Next(m, j, "?.") ? j + 2 : j < n && m[j] == '.' && !Next(m, j, "..") ? j + 1 : -1;
                if (dot > 0)
                {
                    int k = SkipWs(m, dot);
                    if (k >= n || !IsIdentifierStart(m[k])) break;
                    int e = k;
                    while (e < n && IsIdentifierChar(m[e])) e++;
                    last = m.Substring(k, e - k);
                    i = e;
                    continue;
                }
                if (j < n && (m[j] == '(' || m[j] == '['))
                {
                    int close = MatchClose(m, j);
                    if (close < 0) break;
                    last = null;
                    i = close;
                    continue;
                }
                if (j < n && m[j] == '!' && !Next(m, j, "!="))
                {
                    i = j + 1;
                    continue;
                }
                break;
            }
            return i;
        }

        /// <summary>The start of a member-access chain ending at <paramref name="end"/>; -1 when none ends there.</summary>
        private static int ChainStart(string m, int end)
        {
            int i = end;
            while (true)
            {
                if (i == 0) return -1;
                char c = m[i - 1];
                if (c == ')' || c == ']')
                {
                    int open = MatchOpen(m, i - 1);
                    if (open < 0) return -1;
                    i = open;
                    int p = SkipWsBack(m, i);
                    if (p > 0 && (IsIdentifierChar(m[p - 1]) || m[p - 1] == ')' || m[p - 1] == ']')) { i = p; continue; }
                    return c == ')' ? i : -1;
                }
                if (c == '!') { i--; continue; }
                if (!IsIdentifierChar(c)) return -1;
                int s = i;
                while (s > 0 && IsIdentifierChar(m[s - 1])) s--;
                int before = SkipWsBack(m, s);
                if (before > 0 && m[before - 1] == '.' && !(before > 1 && m[before - 2] == '.'))
                {
                    i = SkipWsBack(m, before > 1 && m[before - 2] == '?' ? before - 2 : before - 1);
                    continue;
                }
                return s;
            }
        }

        /// <summary>Whether an expression may end at <paramref name="end"/>: what follows is no part of a longer operand.</summary>
        private static bool EndsExpression(string m, int end)
        {
            int next = SkipWs(m, end);
            if (next >= m.Length) return true;
            char c = m[next];
            if (";,)]}:".IndexOf(c) >= 0 || "=!<>+-*/%&|^?".IndexOf(c) >= 0) return true;
            string word = WordAt(m, next);
            return word is "is" or "as" or "switch" or "when";
        }

        /// <summary>Whether a whole operand ends at <paramref name="end"/>: a terminator, <c>:</c>, <c>is</c>, <c>as</c> or <c>switch</c> follows.</summary>
        private static bool ClosesExpression(string m, int end)
        {
            int next = SkipWs(m, end);
            if (next >= m.Length || ";,)]}:".IndexOf(m[next]) >= 0) return true;
            return WordAt(m, next) is "is" or "as" or "switch";
        }

        private static bool IsConditionalMark(string m, int i) =>
            i < m.Length && m[i] == '?' && !(i + 1 < m.Length && (m[i + 1] == '.' || m[i + 1] == '?' || m[i + 1] == '['));

        /// <summary>Splits <c>cond ? a : b</c> starting at <paramref name="start"/> at its top level.</summary>
        private static bool SplitConditional(string m, int start, out int question, out int colon, out int end)
        {
            question = colon = -1;
            int depth = 0, nested = 0;
            for (int i = start; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}')
                {
                    if (--depth < 0) { end = i; return colon > 0; }
                    continue;
                }
                if (depth > 0) continue;
                if (c == ';' || c == ',') { end = i; return colon > 0; }
                if (c == '?' && i > 0 && m[i - 1] == '?') continue;
                if (IsConditionalMark(m, i))
                {
                    if (question < 0) question = i;
                    else if (colon < 0) nested++;
                    continue;
                }
                if (c == ':' && question >= 0 && !Next(m, i, "::"))
                {
                    if (nested > 0) nested--;
                    else if (colon < 0) colon = i;
                    else { end = i; return true; }
                }
            }
            end = m.Length;
            return colon > 0;
        }

        /// <summary>The index of the terminator (<c>; ,</c> or an unmatched closing bracket) of an expression starting at <paramref name="start"/>.</summary>
        private static int ExpressionEnd(string m, int start)
        {
            int depth = 0;
            for (int i = start; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (--depth < 0) return i; }
                else if (depth == 0 && (c == ';' || c == ',')) return i;
            }
            return m.Length;
        }

        /// <summary>The operator <paramref name="op"/> at the top level of the binary expression starting at <paramref name="start"/>.</summary>
        private static int FindOperator(string m, int start, string op)
        {
            int depth = 0;
            for (int i = start; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (--depth < 0) return -1; }
                else if (depth == 0 && (c == ';' || c == ',')) return -1;
                else if (depth == 0 && Next(m, i, op) && !(i + op.Length < m.Length && "=?".IndexOf(m[i + op.Length]) >= 0 && op != "??="))
                    return i;
            }
            return -1;
        }

        private static int MatchClose(string m, int open)
        {
            int depth = 0;
            for (int i = open; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == ']' || c == '}') && --depth == 0) return i + 1;
            }
            return -1;
        }

        private static int MatchOpen(string m, int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                char c = m[i];
                if (c == ')' || c == ']' || c == '}') depth++;
                else if ((c == '(' || c == '[' || c == '{') && --depth == 0) return i;
            }
            return -1;
        }

        private static int MatchAngleOpen(string m, int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                char c = m[i];
                if (c == '>') depth++;
                else if (c == '<' && --depth == 0) return i;
                else if (c == ';' || c == '{' || c == '}' || c == '(' || c == ')') return -1;
            }
            return -1;
        }

        /// <summary>The <c>{</c> that encloses <paramref name="position"/>; -1 when there is none.</summary>
        private static int UnmatchedOpenBrace(string m, int position)
        {
            int depth = 0;
            for (int i = position - 1; i >= 0; i--)
            {
                char c = m[i];
                if (c == ')' || c == ']' || c == '}') depth++;
                else if (c == '(' || c == '[' || c == '{')
                {
                    if (depth == 0) return c == '{' ? i : -1;
                    depth--;
                }
            }
            return -1;
        }

        #endregion

        #region Text helpers

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int SkipWs(string m, int i)
        {
            while (i < m.Length && char.IsWhiteSpace(m[i])) i++;
            return i;
        }

        /// <summary>The index after the last non-whitespace character before <paramref name="i"/>.</summary>
        private static int SkipWsBack(string m, int i)
        {
            while (i > 0 && char.IsWhiteSpace(m[i - 1])) i--;
            return i;
        }

        private static bool Next(string m, int i, string token) =>
            i >= 0 && i + token.Length <= m.Length && string.CompareOrdinal(m, i, token, 0, token.Length) == 0;

        /// <summary>The identifier starting at <paramref name="i"/>, or an empty string.</summary>
        private static string WordAt(string m, int i)
        {
            if (i < 0 || i >= m.Length || !IsIdentifierStart(m[i]) || (i > 0 && IsIdentifierChar(m[i - 1]))) return "";
            int end = i;
            while (end < m.Length && IsIdentifierChar(m[end])) end++;
            return m.Substring(i, end - i);
        }

        /// <summary>The identifier ending at <paramref name="end"/>, or an empty string.</summary>
        private static string WordBefore(string m, int end)
        {
            int start = end;
            while (start > 0 && IsIdentifierChar(m[start - 1])) start--;
            return start < end && IsIdentifierStart(m[start]) ? m.Substring(start, end - start) : "";
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static int Offset(List<int> lineStarts, int line, int column, int length)
        {
            if (line < 1 || line > lineStarts.Count || column < 1) return -1;
            int offset = lineStarts[line - 1] + column - 1;
            return offset < length ? offset : -1;
        }

        private static int LineOf(List<int> lineStarts, int offset)
        {
            int index = lineStarts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        private static string Snippet(string text, int start, int end)
        {
            int from = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            int to = text.IndexOf('\n', end);
            if (to < 0) to = text.Length;
            string line = text.Substring(from, to - from).Trim();
            return line.Length > 120 ? line.Substring(0, 117) + "…" : line;
        }

        private static string ListInitializer(string value)
        {
            string v = value.Trim();
            if (v == "null" || v == "default" || v == "new()" || v == "[]") return "new()";
            string compact = new string(v.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if ((compact.StartsWith("newList<", StringComparison.Ordinal) && compact.EndsWith(">()", StringComparison.Ordinal))
                || (compact.StartsWith("new", StringComparison.Ordinal) && compact.EndsWith("[0]", StringComparison.Ordinal))
                || compact.StartsWith("Array.Empty<", StringComparison.Ordinal) || compact.StartsWith("System.Array.Empty<", StringComparison.Ordinal))
                return "new()";
            return $"new({v})";
        }

        private static void Add(List<Edit> edits, int position, int remove, string insert) =>
            edits.Add(new Edit { Position = position, Remove = remove, Insert = insert });

        /// <summary>Applies the edits from the end; duplicates (the same fix asked for twice) and overlaps are dropped.</summary>
        private static string Apply(string text, List<Edit> edits)
        {
            if (edits.Count == 0) return text;
            var sorted = edits
                .GroupBy(e => (e.Position, e.Remove, e.Insert)).Select(g => g.First())
                .OrderByDescending(e => e.Position).ThenByDescending(e => e.Remove)
                .ToList();
            var result = new StringBuilder(text);
            int limit = int.MaxValue;
            foreach (var edit in sorted)
            {
                if (edit.Position + edit.Remove > limit) continue;
                result.Remove(edit.Position, edit.Remove);
                result.Insert(edit.Position, edit.Insert);
                limit = edit.Position;
            }
            return result.ToString();
        }

        private static string Describe(string text, List<Edit> edits, int from)
        {
            var parts = new List<string>();
            for (int i = from; i < edits.Count; i++)
            {
                var e = edits[i];
                parts.Add(e.Remove > 0 ? $"'{text.Substring(e.Position, e.Remove)}' → '{e.Insert}'" : $"+'{e.Insert}'");
            }
            return string.Join(", ", parts);
        }

        #endregion
    }
}
#endif
