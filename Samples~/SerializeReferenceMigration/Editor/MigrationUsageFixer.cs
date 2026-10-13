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
            /// <summary>A field of this name is a list (somewhere).</summary>
            public bool IsList;
            /// <summary>A field of this name is a single reference too: a list elsewhere doesn't rule a use out.</summary>
            public bool AlsoSingle;
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
            "CS1061", "CS0117", "CS0029", "CS0266", "CS1503", "CS0030", "CS0039", "CS1579", "CS0173", "CS8121", "CS0019", "CS0184"
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
                case "CS0117":
                {
                    // 'StableRef<T>' does not contain a definition for 'Member' — a property pattern
                    // (`x is { Member: … }`), reported at the member's name.
                    if (quoted.Count < 2 || WordAt(m, offset) != quoted[1]) return false;
                    var kind = WrapperKind(quoted[0]);
                    if (kind == Kind.List)
                    {
                        if (quoted[1] != "Length") return false;
                        Add(edits, offset, quoted[1].Length, "Count");
                        return true;
                    }
                    if (kind != Kind.Single) return false;
                    int brace = UnmatchedOpen(m, offset);
                    return brace >= 0 && m[brace] == '{' && FixPatternSubject(m, brace, edits);
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
                        string conversion = array ? ".ToArray()" : ".ToList()";
                        if (SplitConditional(m, offset, out int question, out int colon, out int conditionalEnd))
                        {
                            // `cond ? o.items : null` converted as a whole — reported at the condition: the branches convert.
                            bool converted = false;
                            foreach (var (branchStart, branchEnd) in new[] { (question + 1, colon), (colon + 1, conditionalEnd) })
                            {
                                int s = SkipWs(m, branchStart), e = SkipWsBack(m, branchEnd);
                                if (WordAt(m, s) is "null" or "default" or "new" || ChainEnd(m, s, out _) != e) continue;
                                Add(edits, e, 0, conversion);
                                converted = true;
                            }
                            needsLinq |= converted && list;
                            return converted;
                        }
                        string word = WordBefore(m, SkipWsBack(m, offset));
                        if (word == "ref" || word == "out") return false;
                        int end = ChainEnd(m, offset, out _);
                        if (end < 0 || !ClosesExpression(m, end)) return false;
                        Add(edits, end, 0, conversion);
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
        /// What the silent-use check asks about types. The answers come from the types loaded before the retype (the
        /// editor's reflection, <see cref="MigrationTypeModel"/>); a type is opaque here and <c>null</c> means unknown.
        /// </summary>
        public interface ITypeModel
        {
            /// <summary>
            /// The type written as <paramref name="name"/> (<c>Foo</c>, <c>Ns.Foo</c>, <c>Outer.Inner</c>,
            /// <c>List&lt;Foo&gt;</c>, <c>Foo[]</c>, <c>Foo`1</c> for a generic definition) in a file that sees
            /// <paramref name="namespaces"/>.
            /// </summary>
            object ResolveType(string name, IReadOnlyList<string> namespaces);

            /// <summary>The type of the field or property <paramref name="member"/> of <paramref name="type"/> or its base types.</summary>
            object MemberType(object type, string member);

            /// <summary>
            /// The return type of <paramref name="method"/> of <paramref name="type"/> called with
            /// <paramref name="argumentCount"/> arguments and the explicit <paramref name="typeArguments"/> (none when
            /// empty), when every such overload returns the same type.
            /// </summary>
            object CallType(object type, string method, int argumentCount, IReadOnlyList<object> typeArguments);

            /// <summary>What indexing or enumerating <paramref name="type"/> gives.</summary>
            object ElementType(object type);

            /// <summary><c>IEnumerable&lt;element&gt;</c>.</summary>
            object Sequence(object element);

            /// <summary>
            /// The type of part <paramref name="index"/> of <paramref name="count"/> when <paramref name="type"/> is
            /// deconstructed (<c>var (a, b) = x;</c>): a tuple's element, a <c>KeyValuePair</c>'s key or value, a
            /// <c>Deconstruct</c> method's <c>out</c> parameter.
            /// </summary>
            object DeconstructedType(object type, int index, int count);

            /// <summary>Whether <paramref name="member"/> of <paramref name="type"/> (or of its base types) is a retyped single-reference field.</summary>
            bool IsRetyped(object type, string member);

            /// <summary>
            /// How argument <paramref name="index"/> of <paramref name="argumentCount"/> of a call of
            /// <paramref name="method"/> on <paramref name="type"/> (a constructor of <paramref name="type"/> when
            /// <paramref name="method"/> is <c>null</c>) takes a value.
            /// </summary>
            ArgumentUse Argument(object type, string method, int index, int argumentCount);
        }

        /// <summary>What a parameter makes of a wrapper passed to it.</summary>
        public enum ArgumentUse
        {
            /// <summary>No overload was found.</summary>
            Unknown,
            /// <summary><c>object</c> or a type parameter: the wrapper passes silently where the value was meant.</summary>
            Value,
            /// <summary>A StableRef type: the wrapper is meant.</summary>
            Wrapper,
            /// <summary>The value's type: passing the wrapper is a compile error, fixed by the diagnostics.</summary>
            Typed
        }

        private enum Certainty { Unknown, Field, NotField }

        /// <summary>
        /// Finds the uses of retyped fields that still compile but now test or read the wrapper and inserts
        /// <c>.Value</c>: <c>x == null</c>, <c>x != y</c>, <c>null == x</c>, <c>x is …</c>, <c>x as T</c>, <c>x ?? y</c>,
        /// <c>x switch</c>, <c>switch (x)</c>, <c>ReferenceEquals(x, …)</c>, <c>x.GetType()</c>, <c>x.Equals(…)</c>,
        /// <c>x.GetHashCode()</c>, <c>x.Member</c> where the value type has a member the wrapper has too (<c>Value</c>),
        /// <c>x</c> passed where a parameter is <c>object</c> or a type parameter, and the same uses of a local that
        /// holds the wrapper (<c>var x = o.field;</c>).
        /// </summary>
        /// <remarks>
        /// A use is fixed only when the name surely means the field. A local or parameter of the same name hides it in
        /// its scope. With a <paramref name="model"/>, the type of what precedes the name (<c>a</c> in <c>a.x</c>, the
        /// enclosing class for <c>x</c> / <c>this.x</c>) is read from the code — declarations, <c>var</c> initializers,
        /// <c>foreach</c> elements, LINQ lambda parameters, members, calls and indexers — and the field is the retyped
        /// one only when that type declares it. When the type can't be read: <see cref="Field.OwnFile"/> for <c>x</c>
        /// / <c>this.x</c>, <see cref="Field.UniqueName"/> for <c>a.x</c>; the rest is added to
        /// <paramref name="review"/>. Lists are skipped: a <c>StableRefList&lt;T&gt;</c> is never null, which is what
        /// the old checks guarded.
        /// </remarks>
        public static string FixSilentUses(string text, IReadOnlyDictionary<string, Field> fields, List<string> changes,
            List<string> review, ITypeModel model = null)
        {
            var scope = new FileScope(Mask(text), model);
            string m = scope.M;
            var lineStarts = LineStarts(text);
            var edits = new List<Edit>();
            var aliases = WrapperAliases(scope, fields);

            for (int i = 0; i < m.Length; i++)
            {
                if (!IsIdentifierStart(m[i]) || (i > 0 && (IsIdentifierChar(m[i - 1]) || m[i - 1] == '@'))) continue;
                int end = i;
                while (end < m.Length && IsIdentifierChar(m[end])) end++;
                string name = m.Substring(i, end - i);
                int start = i;
                i = end - 1;

                Alias alias = null;
                if (!fields.TryGetValue(name, out var field) || (field.IsList && model == null))
                {
                    alias = aliases.FirstOrDefault(a => a.Name == name && start > a.Declared && start < a.ScopeEnd);
                    if (alias == null || IsQualified(m, start)) continue;
                    field = alias.Field;
                }

                string pattern = SilentPattern(scope, start, end, field, out int insertAt, out int remove, out string insert, out bool unresolvedCall);
                if (pattern == null) continue;

                var certainty = alias != null ? Certainty.Field : Decide(scope, start, name, field);
                if (certainty == Certainty.NotField) continue;

                string where = $"({LineOf(lineStarts, start) + 1}) {Snippet(text, start, end)}";
                if (certainty == Certainty.Unknown)
                {
                    if (!unresolvedCall)
                        review.Add($"{where} — {pattern} on '{name}' may test the StableRef wrapper now; if '{name}' is the retyped field, add .Value");
                    continue;
                }
                if (unresolvedCall)
                {
                    review.Add($"{where} — '{name}' is {pattern}, whose parameter couldn't be read; if it takes object or a type parameter, add .Value");
                    continue;
                }
                Add(edits, insertAt, remove, insert);
                changes.Add($"{where} — {pattern}: .Value added");
            }

            return Apply(text, edits);
        }

        /// <summary>Whether the field named at <paramref name="start"/> is the retyped one.</summary>
        private static Certainty Decide(FileScope s, int start, string name, Field field)
        {
            string m = s.M;
            int before = SkipWsBack(m, start);
            // A name that is a list somewhere is told apart by the type that declares it only.
            if (!IsQualified(m, start))
            {
                if (FindLocal(s, start, name) != null) return Certainty.NotField;
                var enclosing = s.Model != null ? EnclosingType(s, start) : null;
                if (enclosing != null) return s.Model.IsRetyped(enclosing, name) ? Certainty.Field : Certainty.NotField;
                return field.OwnFile && !field.IsList ? Certainty.Field : Certainty.NotField;
            }

            int receiverEnd = SkipWsBack(m, before > 1 && m[before - 2] == '?' ? before - 2 : before - 1);
            int receiverStart = ChainStart(m, receiverEnd);
            string receiver = receiverStart >= 0 ? m.Substring(receiverStart, receiverEnd - receiverStart).Trim() : null;
            if (receiver is "this" or "base")
            {
                var enclosing = s.Model != null ? EnclosingType(s, start) : null;
                if (enclosing != null) return s.Model.IsRetyped(enclosing, name) ? Certainty.Field : Certainty.NotField;
                return field.OwnFile && !field.IsList ? Certainty.Field : Certainty.NotField;
            }
            if (s.Model != null && receiverStart >= 0)
            {
                var type = TypeOfChain(s, receiverStart, receiverEnd, 0);
                if (type != null) return s.Model.IsRetyped(type, name) ? Certainty.Field : Certainty.NotField;
            }
            if (field.IsList) return field.AlsoSingle ? Certainty.Unknown : Certainty.NotField;
            return field.UniqueName ? Certainty.Field : Certainty.Unknown;
        }

        /// <summary>Whether the identifier at <paramref name="start"/> follows a <c>.</c> / <c>?.</c>.</summary>
        private static bool IsQualified(string m, int start)
        {
            int before = SkipWsBack(m, start);
            return before > 0 && m[before - 1] == '.' && !(before > 1 && m[before - 2] == '.');
        }

        /// <summary>The silent pattern around the identifier at [start, end), and the edit that fixes it.</summary>
        /// <param name="unresolvedCall">The name is an argument of a call whose parameter couldn't be read.</param>
        private static string SilentPattern(FileScope s, int start, int end, Field field, out int insertAt, out int remove,
            out string insert, out bool unresolvedCall)
        {
            string m = s.M;
            insertAt = end;
            remove = 0;
            insert = ValueSuffix;
            unresolvedCall = false;

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
                if (ObjectMembers.Contains(member) || field.SharedMembers.Contains(member)) return $".{member}";
                return null;
            }
            if (Next(m, next, "?."))
            {
                string member = WordAt(m, SkipWs(m, next + 2));
                if (ObjectMembers.Contains(member) || field.SharedMembers.Contains(member))
                {
                    insertAt = next;
                    remove = 2;
                    insert = ".Value?.";
                    return $"?.{member}";
                }
                return null;
            }
            if (Next(m, next, "==") || Next(m, next, "!="))
                return WordAt(m, SkipWs(m, next + 2)) == "null" ? m.Substring(next, 2) + " null" : m.Substring(next, 2);
            if (Next(m, next, "??") && !Next(m, next, "??=")) return "??";
            string word = WordAt(m, next);
            if (word is "is" or "as" or "switch") return word;

            int chainStart = ChainStart(m, end);
            int before = SkipWsBack(m, chainStart >= 0 ? chainStart : start);
            if (before >= 2 && m[before - 1] == '=' && (m[before - 2] == '=' || m[before - 2] == '!')
                && !(before >= 3 && "=<>".IndexOf(m[before - 3]) >= 0))
                return WordBefore(m, SkipWsBack(m, before - 2)) == "null" ? "null " + m.Substring(before - 2, 2) : m.Substring(before - 2, 2);
            return ArgumentPattern(s, chainStart >= 0 ? chainStart : start, end, out unresolvedCall);
        }

        /// <summary>Members of <c>object</c> the wrapper answers for itself instead of the value; <c>ToString</c> is the value's.</summary>
        private static readonly HashSet<string> ObjectMembers = new(StringComparer.Ordinal) { "GetType", "Equals", "GetHashCode" };

        /// <summary>
        /// The expression [start, end) as a whole argument of a call: <c>switch (x)</c>, <c>ReferenceEquals(x, …)</c>,
        /// or a parameter that takes <c>object</c> / a type parameter (asked from the model).
        /// </summary>
        private static string ArgumentPattern(FileScope s, int start, int end, out bool unresolved)
        {
            unresolved = false;
            string m = s.M;
            int after = SkipWs(m, end);
            if (after >= m.Length || (m[after] != ',' && m[after] != ')')) return null;
            int before = SkipWsBack(m, start);
            if (before == 0 || (m[before - 1] != '(' && m[before - 1] != ',')) return null;
            int open = m[before - 1] == '(' ? before - 1 : UnmatchedOpen(m, before - 1);
            if (open < 0 || m[open] != '(') return null;

            int nameEnd = SkipWsBack(m, open);
            if (nameEnd == 0) return null;
            string method = WordBefore(m, nameEnd);
            if (method == "switch") return m[after] == ')' ? "switch" : null;
            if (method == "ReferenceEquals") return "ReferenceEquals";
            if (method.Length == 0 || NotCalls.Contains(method)) return null;

            int typeArgsEnd = nameEnd;
            if (m[nameEnd - 1] == '>')
            {
                int angle = MatchAngleOpen(m, nameEnd - 1);
                if (angle < 0) return null;
                typeArgsEnd = SkipWsBack(m, angle);
                method = WordBefore(m, typeArgsEnd);
                if (method.Length == 0) return null;
            }
            if (s.Model == null) return null;

            int methodStart = typeArgsEnd - method.Length;
            int close = MatchClose(m, open);
            if (close < 0) return null;
            int index = TopLevelCommas(m, open + 1, start);
            int count = ArgumentCount(m, open, close);

            object type;
            string call;
            int p = SkipWsBack(m, methodStart);
            if (WordBefore(m, p) == "new")
            {
                type = s.Model.ResolveType(m.Substring(methodStart, nameEnd - methodStart).Replace(" ", ""), s.Namespaces);
                call = $"an argument of new {method}(…)";
                method = null;
            }
            else if (p > 0 && m[p - 1] == '.')
            {
                int receiverEnd = SkipWsBack(m, p > 1 && m[p - 2] == '?' ? p - 2 : p - 1);
                int receiverStart = ChainStart(m, receiverEnd);
                type = receiverStart >= 0 ? TypeOfChain(s, receiverStart, receiverEnd, 0) : null;
                call = $"an argument of {method}(…)";
            }
            else
            {
                // A local delegate (`condition(x)`) or a method of the enclosing type.
                var callee = FindLocal(s, methodStart, method);
                type = callee != null ? TypeOfLocal(s, callee, 0) : EnclosingType(s, open);
                call = $"an argument of {method}(…)";
                if (callee != null) method = "Invoke";
            }

            switch (type != null ? s.Model.Argument(type, method, index, count) : ArgumentUse.Unknown)
            {
                case ArgumentUse.Value:
                    return call;
                case ArgumentUse.Unknown:
                    unresolved = true;
                    return call;
                default:
                    return null;
            }
        }

        /// <summary>Words followed by parentheses that aren't calls.</summary>
        private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
        {
            "if", "while", "for", "foreach", "using", "lock", "catch", "return", "nameof", "typeof", "sizeof", "default",
            "when", "fixed", "checked", "unchecked", "await", "throw", "base", "this", "is", "and", "or", "not", "in"
        };

        #region Wrapper aliases

        private sealed class Alias
        {
            public string Name;
            public Field Field;
            public int Declared;
            public int ScopeEnd;
        }

        /// <summary>
        /// Locals that hold the wrapper of a retyped field: <c>var x = o.field;</c> (with the field surely the retyped
        /// one) — their silent uses are the field's.
        /// </summary>
        private static List<Alias> WrapperAliases(FileScope s, IReadOnlyDictionary<string, Field> fields)
        {
            var result = new List<Alias>();
            string m = s.M;
            for (int i = m.IndexOf("var", StringComparison.Ordinal); i >= 0; i = m.IndexOf("var", i + 3, StringComparison.Ordinal))
            {
                if (WordAt(m, i) != "var") continue;
                int nameStart = SkipWs(m, i + 3);
                string name = WordAt(m, nameStart);
                if (name.Length == 0) continue;
                int eq = SkipWs(m, nameStart + name.Length);
                if (eq >= m.Length || m[eq] != '=' || Next(m, eq, "==") || Next(m, eq, "=>")) continue;
                int valueStart = SkipWs(m, eq + 1);
                int valueEnd = ChainEnd(m, valueStart, out string last);
                if (valueEnd < 0 || last == null || SkipWs(m, valueEnd) >= m.Length || m[SkipWs(m, valueEnd)] != ';') continue;
                if (!fields.TryGetValue(last, out var field)) continue;
                if (Decide(s, valueEnd - last.Length, last, field) != Certainty.Field) continue;
                int scopeEnd = ScopeEnd(m, nameStart);
                if (scopeEnd < 0) continue;
                result.Add(new Alias { Name = name, Field = field, Declared = nameStart + name.Length, ScopeEnd = scopeEnd });
            }
            return result;
        }

        #endregion

        #region Locals and scopes

        private enum LocalKind { Typed, VarValue, VarForeach, LambdaParameter, Deconstructed, Unknown }

        /// <summary>A local, parameter or lambda parameter, as declared.</summary>
        private sealed class Local
        {
            public LocalKind Kind;
            /// <summary>The written type (<see cref="LocalKind.Typed"/>, a typed lambda parameter).</summary>
            public string Type;
            /// <summary>The initializer or <c>foreach</c> collection.</summary>
            public int ValueStart, ValueEnd;
            /// <summary>Where the lambda starts (its parameter or <c>(</c>) and the parameter's index.</summary>
            public int Lambda, Index;
            /// <summary>A deconstruction's part count, and whether its value is a <c>foreach</c> collection.</summary>
            public int Count;
            public bool InForeach;
        }

        /// <summary>
        /// The local, parameter or lambda parameter named <paramref name="name"/> whose scope holds
        /// <paramref name="position"/>, nearest first; <c>null</c> when the name isn't one (a member, a type…).
        /// </summary>
        private static Local FindLocal(FileScope s, int position, string name)
        {
            string m = s.M;
            int limit = TypeBodyStart(m, position);
            for (int d = LastIndexIn(m, name, limit + 1, position); d > limit; d = LastIndexIn(m, name, limit + 1, d + name.Length - 1))
            {
                int end = d + name.Length;
                if (WordAt(m, d) != name || IsQualified(m, d)) continue;
                var local = Declaration(m, d, end);
                if (local == null) continue;
                int scopeEnd = local.Kind == LocalKind.LambdaParameter && local.Lambda == d ? LambdaBodyEnd(m, SkipWs(m, end)) : ScopeEnd(m, d);
                if (scopeEnd >= position) return local;
            }
            return null;
        }

        /// <summary>The declaration the identifier at [start, end) makes, if it makes one.</summary>
        private static Local Declaration(string m, int start, int end)
        {
            int next = SkipWs(m, end);
            int before = SkipWsBack(m, start);
            char prev = before > 0 ? m[before - 1] : '\0';

            // `x => …`
            if (Next(m, next, "=>") && (prev == '(' || prev == ',' || prev == '=' || prev == '\0' || prev == '?' || prev == ':'
                                        || WordBefore(m, before) == "return"))
                return new Local { Kind = LocalKind.LambdaParameter, Lambda = start, Index = 0 };

            // `var (a, b) = x;`, `foreach (var (a, b) in xs)`
            int open = UnmatchedOpen(m, start);
            if (open >= 0 && m[open] == '(' && (prev == '(' || prev == ',') && next < m.Length && (m[next] == ',' || m[next] == ')')
                && WordBefore(m, SkipWsBack(m, open)) == "var")
            {
                int close = MatchClose(m, open);
                int after = close > 0 ? SkipWs(m, close) : -1;
                if (after < 0) return null;
                var local = new Local
                {
                    Kind = LocalKind.Deconstructed, Index = TopLevelCommas(m, open + 1, start), Count = ArgumentCount(m, open, close)
                };
                if (after < m.Length && m[after] == '=' && !Next(m, after, "==") && !Next(m, after, "=>"))
                {
                    local.ValueStart = SkipWs(m, after + 1);
                    local.ValueEnd = ExpressionEnd(m, after + 1);
                    return local;
                }
                if (WordAt(m, after) == "in")
                {
                    int foreachOpen = UnmatchedOpen(m, open);
                    int foreachClose = foreachOpen >= 0 ? MatchClose(m, foreachOpen) : -1;
                    if (foreachClose < 0) return null;
                    local.ValueStart = SkipWs(m, after + 2);
                    local.ValueEnd = foreachClose - 1;
                    local.InForeach = true;
                    return local;
                }
                return new Local { Kind = LocalKind.Unknown };
            }

            // `(a, b) => …` / `(A a, B b) => …`
            if (open >= 0 && m[open] == '(')
            {
                int close = MatchClose(m, open);
                if (close > 0 && Next(m, SkipWs(m, close), "=>") && SkipWs(m, end) < m.Length && (m[next] == ',' || m[next] == ')')
                    && IsParameterList(m, open, close))
                {
                    string type = prev == '(' || prev == ',' ? null : TypeBefore(m, start);
                    return new Local { Kind = LocalKind.LambdaParameter, Type = type, Lambda = open, Index = TopLevelCommas(m, open + 1, start) };
                }
            }

            bool pattern = false;
            if (!IsDeclarationName(m, start, end))
            {
                // `is Foo foo &&`, `case Foo foo when`, `out Foo foo`
                if (TypeBefore(m, start, out int typeStart) == null) return null;
                string word = WordBefore(m, SkipWsBack(m, typeStart));
                if (word != "is" && word != "case" && word != "out") return null;
                pattern = true;
            }
            string typeText = TypeBefore(m, start);
            if (typeText == null) return null;
            if (typeText != "var") return new Local { Kind = LocalKind.Typed, Type = typeText };
            if (!pattern && next < m.Length && m[next] == '=')
                return new Local { Kind = LocalKind.VarValue, ValueStart = SkipWs(m, next + 1), ValueEnd = ExpressionEnd(m, next + 1) };
            if (WordAt(m, next) == "in")
            {
                int collection = SkipWs(m, next + 2);
                int foreachOpen = UnmatchedOpen(m, start);
                int foreachClose = foreachOpen >= 0 ? MatchClose(m, foreachOpen) : -1;
                if (foreachClose > 0) return new Local { Kind = LocalKind.VarForeach, ValueStart = collection, ValueEnd = foreachClose - 1 };
            }
            return new Local { Kind = LocalKind.Unknown };
        }

        /// <summary>Whether ( … ) holds lambda parameters only: names, each optionally after a type.</summary>
        private static bool IsParameterList(string m, int open, int close)
        {
            for (int i = open + 1; i < close - 1; i++)
            {
                char c = m[i];
                if (!(IsIdentifierChar(c) || char.IsWhiteSpace(c) || c == ',' || c == '.' || c == '<' || c == '>' || c == '[' || c == ']' || c == '?'))
                    return false;
            }
            return true;
        }

        /// <summary>The type written right before the identifier at <paramref name="start"/> (<c>Ns.List&lt;A&gt;[]?</c>), without spaces.</summary>
        private static string TypeBefore(string m, int start) => TypeBefore(m, start, out _);

        private static string TypeBefore(string m, int start, out int typeStart)
        {
            typeStart = -1;
            int end = SkipWsBack(m, start);
            int i = end;
            while (true)
            {
                if (i > 0 && m[i - 1] == '?') i = SkipWsBack(m, i - 1);
                while (i > 0 && m[i - 1] == ']')
                {
                    int open = MatchOpen(m, i - 1);
                    if (open < 0 || m.Substring(open + 1, i - open - 2).Trim(',', ' ').Length > 0) return null;
                    i = SkipWsBack(m, open);
                }
                if (i > 0 && m[i - 1] == '>')
                {
                    int open = TypeArgumentsOpen(m, i - 1);
                    if (open < 0) return null;
                    i = SkipWsBack(m, open);
                }
                string word = WordBefore(m, i);
                if (word.Length == 0 || (Keywords.Contains(word) && word != "var")) return null;
                i -= word.Length;
                int dot = SkipWsBack(m, i);
                if (dot > 0 && m[dot - 1] == '.' && !(dot > 1 && m[dot - 2] == '.'))
                {
                    i = SkipWsBack(m, dot - 1);
                    continue;
                }
                break;
            }
            if (i == end) return null;
            typeStart = i;
            return new string(m.Substring(i, end - i).Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        /// <summary>
        /// The <c>&lt;</c> of the type arguments whose <c>&gt;</c> is at <paramref name="close"/>; tuples inside
        /// (<c>List&lt;(int, A)&gt;</c>) are part of them. -1 when the text there is no type.
        /// </summary>
        private static int TypeArgumentsOpen(string m, int close)
        {
            int angles = 0, parens = 0;
            for (int i = close; i >= 0; i--)
            {
                char c = m[i];
                if (c == '>') angles++;
                else if (c == '<' && --angles == 0) return parens == 0 ? i : -1;
                else if (c == ')') parens++;
                else if (c == '(') { if (--parens < 0) return -1; }
                else if (!(IsIdentifierChar(c) || char.IsWhiteSpace(c) || c == '.' || c == ',' || c == '[' || c == ']' || c == '?')) return -1;
            }
            return -1;
        }

        /// <summary>
        /// Where the scope of a name declared at <paramref name="position"/> ends: its block, the body of the method or
        /// lambda it's a parameter of, the statement of a <c>foreach</c> / <c>for</c> / <c>using</c> / <c>catch</c>; -1
        /// for a member of a type.
        /// </summary>
        private static int ScopeEnd(string m, int position)
        {
            int open = UnmatchedOpen(m, position);
            if (open < 0) return -1;
            if (m[open] == '{') return IsTypeBody(m, open) ? -1 : MatchClose(m, open);
            if (m[open] == '[') return -1;

            int close = MatchClose(m, open);
            if (close < 0) return -1;
            int after = SkipWs(m, close);
            if (Next(m, after, "=>")) return LambdaBodyEnd(m, after);
            if (after < m.Length && m[after] == '{') return MatchClose(m, after);
            string keyword = WordBefore(m, SkipWsBack(m, open));
            if (keyword is "foreach" or "for" or "using" or "catch" or "fixed")
                return after < m.Length && m[after] == '{' ? MatchClose(m, after) : ExpressionEnd(m, after) + 1;
            // A pattern or `out` variable in a condition or an argument: the enclosing block.
            int block = UnmatchedBrace(m, open);
            return block >= 0 && !IsTypeBody(m, block) ? MatchClose(m, block) : -1;
        }

        /// <summary>The end of the lambda or expression body whose <c>=&gt;</c> is at <paramref name="arrow"/>.</summary>
        private static int LambdaBodyEnd(string m, int arrow)
        {
            int body = SkipWs(m, arrow + 2);
            return body < m.Length && m[body] == '{' ? MatchClose(m, body) : ExpressionEnd(m, body);
        }

        /// <summary>The nearest unmatched <c>( [ {</c> before <paramref name="position"/>; -1 when there is none.</summary>
        private static int UnmatchedOpen(string m, int position)
        {
            int depth = 0;
            for (int i = position - 1; i >= 0; i--)
            {
                char c = m[i];
                if (c == ')' || c == ']' || c == '}') depth++;
                else if (c == '(' || c == '[' || c == '{')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        /// <summary>
        /// The nearest unmatched <c>{</c> before <paramref name="position"/>, through any parentheses (a lambda block
        /// inside call arguments); -1 when there is none.
        /// </summary>
        private static int UnmatchedBrace(string m, int position)
        {
            int depth = 0;
            for (int i = position - 1; i >= 0; i--)
            {
                char c = m[i];
                if (c == '}') depth++;
                else if (c == '{' && depth-- == 0) return i;
            }
            return -1;
        }

        /// <summary>The last <paramref name="token"/> that lies within [from, to); -1 when there is none.</summary>
        private static int LastIndexIn(string m, string token, int from, int to)
        {
            for (int i = to - token.Length; i >= from; i--)
                if (Next(m, i, token)) return i;
            return -1;
        }

        private static readonly System.Text.RegularExpressions.Regex TypeHeader =
            new(@"\b(class|struct|interface|record)\s+(@?\w+)\s*(<[^{;]*?>)?", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The declaration header before <paramref name="brace"/>: the text back to the previous <c>; { }</c>.</summary>
        private static string HeaderBefore(string m, int brace)
        {
            int depth = 0, i = brace - 1;
            for (; i >= 0; i--)
            {
                char c = m[i];
                if (c == ')' || c == ']') depth++;
                else if (c == '(' || c == '[') { if (--depth < 0) break; }
                else if (depth == 0 && (c == ';' || c == '{' || c == '}')) break;
            }
            return m.Substring(i + 1, brace - i - 1);
        }

        private static bool IsTypeBody(string m, int brace) => TypeHeader.IsMatch(HeaderBefore(m, brace));

        /// <summary>The <c>{</c> of the innermost type body around <paramref name="position"/>; -1 at file level.</summary>
        private static int TypeBodyStart(string m, int position)
        {
            for (int brace = UnmatchedBrace(m, position); brace >= 0; brace = UnmatchedBrace(m, brace))
                if (IsTypeBody(m, brace)) return brace;
            return -1;
        }

        /// <summary>Commas at the top level of [from, to).</summary>
        private static int TopLevelCommas(string m, int from, int to)
        {
            int depth = 0, count = 0;
            for (int i = from; i < to; i++)
            {
                char c = m[i];
                if (c == '(' || c == '[' || c == '{' || c == '<') depth++;
                else if (c == ')' || c == ']' || c == '}' || (c == '>' && !(i > 0 && m[i - 1] == '='))) depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        /// <summary>The number of arguments between the parentheses at <paramref name="open"/> and <paramref name="close"/> (right after <c>)</c>).</summary>
        private static int ArgumentCount(string m, int open, int close) =>
            SkipWs(m, open + 1) >= close - 1 ? 0 : TopLevelCommas(m, open + 1, close - 1) + 1;

        #endregion

        #region Types

        /// <summary>Per-file state of the silent-use check.</summary>
        private sealed class FileScope
        {
            public readonly string M;
            public readonly ITypeModel Model;
            private IReadOnlyList<string> _namespaces;
            private readonly Dictionary<int, object> _enclosing = new();

            public FileScope(string masked, ITypeModel model)
            {
                M = masked;
                Model = model;
            }

            /// <summary>
            /// The namespaces the file sees, nearest first: its namespace declarations with their parents, its
            /// <c>using</c> directives, the global namespace.
            /// </summary>
            public IReadOnlyList<string> Namespaces => _namespaces ??= ReadNamespaces(M);

            public object Enclosing(int brace, Func<object> resolve)
            {
                if (!_enclosing.TryGetValue(brace, out var type)) _enclosing[brace] = type = resolve();
                return type;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex UsingDirective =
            new(@"^\s*(?:global\s+)?using\s+(?!static\b)([\w.]+)\s*;", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex NamespaceDeclaration =
            new(@"\bnamespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static IReadOnlyList<string> ReadNamespaces(string m)
        {
            var result = new List<string>();
            foreach (var match in NamespaceDeclaration.Matches(m).Cast<System.Text.RegularExpressions.Match>().Reverse())
            {
                for (string ns = match.Groups[1].Value; ns.Length > 0; ns = ns.IndexOf('.') >= 0 ? ns.Substring(0, ns.LastIndexOf('.')) : "")
                    result.Add(ns);
            }
            foreach (System.Text.RegularExpressions.Match match in UsingDirective.Matches(m))
                result.Add(match.Groups[1].Value);
            result.Add("");
            return result.Distinct().ToList();
        }

        /// <summary>The type whose body holds <paramref name="position"/> (<c>Outer.Inner</c>, <c>Foo`1</c>), as the model knows it.</summary>
        private static object EnclosingType(FileScope s, int position)
        {
            if (s.Model == null) return null;
            int brace = TypeBodyStart(s.M, position);
            if (brace < 0) return null;
            return s.Enclosing(brace, () =>
            {
                var names = new List<string>();
                for (int b = brace; b >= 0; b = UnmatchedBrace(s.M, b))
                {
                    var match = TypeHeader.Match(HeaderBefore(s.M, b));
                    if (!match.Success) continue;
                    string name = match.Groups[2].Value.TrimStart('@');
                    if (match.Groups[3].Success) name += "`" + (TopLevelCommas(match.Groups[3].Value, 1, match.Groups[3].Value.Length - 1) + 1);
                    names.Insert(0, name);
                }
                return names.Count > 0 ? s.Model.ResolveType(string.Join(".", names), s.Namespaces) : null;
            });
        }

        /// <summary>LINQ methods that keep the element type of the sequence they're called on.</summary>
        private static readonly HashSet<string> SameSequence = new(StringComparer.Ordinal)
        {
            "Where", "Take", "Skip", "TakeWhile", "SkipWhile", "TakeLast", "SkipLast", "OrderBy", "OrderByDescending",
            "ThenBy", "ThenByDescending", "Distinct", "DistinctBy", "Reverse", "AsEnumerable", "Concat", "Union", "Intersect",
            "Except", "DefaultIfEmpty", "ToList", "ToArray", "Append", "Prepend", "ToHashSet"
        };

        /// <summary>LINQ methods that give one element of the sequence they're called on.</summary>
        private static readonly HashSet<string> OneElement = new(StringComparer.Ordinal)
        {
            "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault",
            "MinBy", "MaxBy"
        };

        /// <summary>Methods whose lambda gets the elements of the sequence they're called on as its first parameter.</summary>
        private static readonly HashSet<string> ElementLambda = new(StringComparer.Ordinal)
        {
            "Where", "Select", "SelectMany", "Any", "All", "Count", "LongCount", "First", "FirstOrDefault", "Last",
            "LastOrDefault", "Single", "SingleOrDefault", "Sum", "Min", "Max", "Average", "MinBy", "MaxBy", "OrderBy",
            "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy", "ToDictionary", "ToLookup", "TakeWhile",
            "SkipWhile", "DistinctBy", "Foreach", "ForEach", "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
            "Exists", "TrueForAll", "RemoveAll", "TryFirst", "TryLast"
        };

        /// <summary>The type of the expression [start, end): a member-access chain, maybe parenthesized; <c>null</c> when unknown.</summary>
        private static object TypeOfChain(FileScope s, int start, int end, int depth)
        {
            var model = s.Model;
            string m = s.M;
            if (model == null || depth > 8) return null;
            int i = SkipWs(m, start);
            if (i >= end) return null;
            object type;

            if (m[i] == '(')
            {
                int close = MatchClose(m, i);
                if (close < 0 || close > end) return null;
                type = TypeOfParenthesized(s, i, close, depth);
                i = close;
            }
            else
            {
                string head = WordAt(m, i);
                if (head.Length == 0) return null;
                int headEnd = i + head.Length;
                int after = SkipWs(m, headEnd);
                if (head is "this" or "base")
                {
                    type = EnclosingType(s, i);
                    i = headEnd;
                }
                else if (after < end && (m[after] == '(' || TypeArguments(m, after, out _) >= 0))
                {
                    type = CallType(s, EnclosingType(s, i), head, after, out i, depth);
                }
                else
                {
                    type = TypeOfName(s, i, head, depth);
                    i = headEnd;
                    if (type == null)
                    {
                        // A type, maybe namespace-qualified: Ns.Type.Member
                        string qualified = head;
                        for (int j = i; ; )
                        {
                            type = model.ResolveType(qualified, s.Namespaces);
                            if (type != null) { i = j; break; }
                            int dot = SkipWs(m, j);
                            if (dot >= end || m[dot] != '.') return null;
                            string part = WordAt(m, SkipWs(m, dot + 1));
                            if (part.Length == 0) return null;
                            qualified += "." + part;
                            j = SkipWs(m, dot + 1) + part.Length;
                        }
                    }
                }
            }

            while (type != null)
            {
                int j = SkipWs(m, i);
                if (j >= end) return type;
                if (m[j] == '!') { i = j + 1; continue; }
                int dot = Next(m, j, "?.") ? j + 2 : m[j] == '.' ? j + 1 : -1;
                if (dot > 0)
                {
                    int k = SkipWs(m, dot);
                    string member = WordAt(m, k);
                    if (member.Length == 0) return null;
                    int e = k + member.Length;
                    int a = SkipWs(m, e);
                    if (a < end && (m[a] == '(' || TypeArguments(m, a, out _) >= 0))
                    {
                        type = CallType(s, type, member, a, out i, depth);
                        continue;
                    }
                    type = model.MemberType(type, member);
                    i = e;
                    continue;
                }
                if (m[j] == '[')
                {
                    int close = MatchClose(m, j);
                    if (close < 0) return null;
                    type = model.ElementType(type);
                    i = close;
                    continue;
                }
                return null;
            }
            return null;
        }

        /// <summary><c>(x as T)</c>, <c>((T)x)</c>, <c>(x)</c>.</summary>
        private static object TypeOfParenthesized(FileScope s, int open, int close, int depth)
        {
            string m = s.M;
            int inner = SkipWs(m, open + 1), innerEnd = SkipWsBack(m, close - 1);
            int asAt = LastIndexIn(m, " as ", inner, innerEnd);
            if (asAt > inner && UnmatchedOpen(m, asAt) == open)
                return s.Model.ResolveType(m.Substring(asAt + 4, innerEnd - asAt - 4).Replace(" ", ""), s.Namespaces);
            if (m[inner] == '(')
            {
                int castClose = MatchClose(m, inner);
                if (castClose > 0 && castClose < innerEnd)
                {
                    string cast = m.Substring(inner + 1, castClose - inner - 2).Replace(" ", "");
                    var castType = s.Model.ResolveType(cast, s.Namespaces);
                    if (castType != null) return castType;
                }
            }
            return ChainEnd(m, inner, out _) == innerEnd ? TypeOfChain(s, inner, innerEnd, depth + 1) : null;
        }

        /// <summary>
        /// The type a call gives: <paramref name="method"/> called on <paramref name="receiver"/> with the arguments
        /// (and type arguments) at <paramref name="at"/>; <paramref name="next"/> is right after its <c>)</c>.
        /// </summary>
        private static object CallType(FileScope s, object receiver, string method, int at, out int next, int depth)
        {
            string m = s.M;
            next = at;
            var typeArguments = new List<object>();
            int open = at;
            int typeArgsEnd = TypeArguments(m, at, out string typeArgsText);
            if (typeArgsEnd >= 0)
            {
                foreach (string arg in SplitTopLevel(typeArgsText))
                    typeArguments.Add(s.Model.ResolveType(arg, s.Namespaces));
                open = SkipWs(m, typeArgsEnd);
            }
            if (open >= m.Length || m[open] != '(') return null;
            int close = MatchClose(m, open);
            if (close < 0) return null;
            next = close;
            if (receiver == null || typeArguments.Any(t => t == null)) return null;

            var element = s.Model.ElementType(receiver);
            if (element != null)
            {
                if ((method == "OfType" || method == "Cast") && typeArguments.Count == 1) return s.Model.Sequence(typeArguments[0]);
                if (SameSequence.Contains(method) && typeArguments.Count == 0) return s.Model.Sequence(element);
                if (OneElement.Contains(method) && typeArguments.Count == 0) return element;
            }
            return s.Model.CallType(receiver, method, ArgumentCount(m, open, close), typeArguments);
        }

        /// <summary>
        /// Explicit type arguments of a call at <paramref name="at"/> (<c>&lt;A, B&gt;(</c>): the index after <c>&gt;</c>,
        /// or -1 when the <c>&lt;</c> there is no such list.
        /// </summary>
        private static int TypeArguments(string m, int at, out string text)
        {
            text = null;
            if (at >= m.Length || m[at] != '<') return -1;
            int depth = 0;
            for (int i = at; i < m.Length; i++)
            {
                char c = m[i];
                if (c == '<') depth++;
                else if (c == '>')
                {
                    if (--depth > 0) continue;
                    int after = SkipWs(m, i + 1);
                    if (after >= m.Length || m[after] != '(') return -1;
                    text = new string(m.Substring(at + 1, i - at - 1).Where(ch => !char.IsWhiteSpace(ch)).ToArray());
                    return i + 1;
                }
                else if (!(IsIdentifierChar(c) || c == '.' || c == ',' || c == '[' || c == ']' || c == '?' || char.IsWhiteSpace(c)))
                    return -1;
            }
            return -1;
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0, from = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(from, i - from);
                    from = i + 1;
                }
            }
            yield return text.Substring(from);
        }

        /// <summary>The type of the name at <paramref name="position"/>: a local or parameter, else a member of the enclosing type.</summary>
        private static object TypeOfName(FileScope s, int position, string name, int depth)
        {
            var local = FindLocal(s, position, name);
            if (local != null) return TypeOfLocal(s, local, depth);
            var enclosing = EnclosingType(s, position);
            return enclosing != null ? s.Model.MemberType(enclosing, name) : null;
        }

        private static object TypeOfLocal(FileScope s, Local local, int depth)
        {
            string m = s.M;
            switch (local.Kind)
            {
                case LocalKind.Typed:
                    return s.Model.ResolveType(local.Type, s.Namespaces);
                case LocalKind.VarValue:
                    return TypeOfValue(s, local.ValueStart, SkipWsBack(m, local.ValueEnd), depth + 1);
                case LocalKind.VarForeach:
                {
                    var collection = TypeOfValue(s, local.ValueStart, SkipWsBack(m, local.ValueEnd), depth + 1);
                    return collection != null ? s.Model.ElementType(collection) : null;
                }
                case LocalKind.LambdaParameter:
                    return local.Type != null ? s.Model.ResolveType(local.Type, s.Namespaces) : TypeOfLambdaParameter(s, local, depth + 1);
                case LocalKind.Deconstructed:
                {
                    var value = TypeOfValue(s, local.ValueStart, SkipWsBack(m, local.ValueEnd), depth + 1);
                    if (local.InForeach && value != null) value = s.Model.ElementType(value);
                    return value != null ? s.Model.DeconstructedType(value, local.Index, local.Count) : null;
                }
                default:
                    return null;
            }
        }

        /// <summary>The type of a <c>var</c> initializer: <c>new T(…)</c>, <c>x as T</c>, <c>(T)x</c> or a chain.</summary>
        private static object TypeOfValue(FileScope s, int start, int end, int depth)
        {
            string m = s.M;
            if (WordAt(m, start) == "new")
            {
                int typeStart = SkipWs(m, start + 3);
                int open = typeStart;
                while (open < end && m[open] != '(' && m[open] != '{' && m[open] != '[') open++;
                string type = m.Substring(typeStart, open - typeStart).Replace(" ", "");
                return type.Length > 0 && open < end && m[open] != '[' ? s.Model.ResolveType(type, s.Namespaces) : null;
            }
            int asAt = LastIndexIn(m, " as ", start, end);
            if (asAt > start && UnmatchedOpen(m, asAt) == UnmatchedOpen(m, start))
                return s.Model.ResolveType(m.Substring(asAt + 4, end - asAt - 4).Replace(" ", ""), s.Namespaces);
            if (m[start] == '(')
            {
                int close = MatchClose(m, start);
                if (close > 0 && close < end && ChainEnd(m, SkipWs(m, close), out _) == end)
                {
                    var cast = s.Model.ResolveType(m.Substring(start + 1, close - start - 2).Replace(" ", ""), s.Namespaces);
                    if (cast != null) return cast;
                }
            }
            return ChainEnd(m, start, out _) == end ? TypeOfChain(s, start, end, depth) : null;
        }

        /// <summary>The element type of the sequence a LINQ-like call is made on, for its lambda's parameter.</summary>
        private static object TypeOfLambdaParameter(FileScope s, Local local, int depth)
        {
            string m = s.M;
            int open = UnmatchedOpen(m, local.Lambda);
            if (open < 0 || m[open] != '(') return null;
            int nameEnd = SkipWsBack(m, open);
            if (nameEnd > 0 && m[nameEnd - 1] == '>')
            {
                int angle = MatchAngleOpen(m, nameEnd - 1);
                if (angle < 0) return null;
                nameEnd = SkipWsBack(m, angle);
            }
            string method = WordBefore(m, nameEnd);
            int p = SkipWsBack(m, nameEnd - method.Length);
            if (method.Length == 0 || p == 0 || m[p - 1] != '.') return null;
            int receiverEnd = SkipWsBack(m, p > 1 && m[p - 2] == '?' ? p - 2 : p - 1);
            int receiverStart = ChainStart(m, receiverEnd);
            if (receiverStart < 0) return null;
            var receiver = TypeOfChain(s, receiverStart, receiverEnd, depth);
            var element = receiver != null ? s.Model.ElementType(receiver) : null;
            if (element == null) return null;

            int argument = TopLevelCommas(m, open + 1, local.Lambda);
            if (method == "Aggregate")
                return argument == 0 || local.Index == 1 ? element : null;
            return ElementLambda.Contains(method) && local.Index == 0 ? element : null;
        }

        #endregion

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
