#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Retypes the declarations of plain <c>[SerializeReference]</c> fields in the project's scripts in place — same
    /// name, same access, so the data stays under the same key:
    /// <code>
    /// [SerializeReference] public IEffect effect;           →  public StableRef&lt;IEffect&gt; effect;
    /// [SerializeReference] private List&lt;IEffect&gt; _effects; →  [SerializeField] private StableRefList&lt;IEffect&gt; _effects;
    /// </code>
    /// An array or list becomes a <c>StableRefList&lt;T&gt;</c>. Code that uses the field is not touched: where it reads
    /// the value it needs <c>.Value</c>, which the compiler points out. The rewriter also
    /// adds a reference to <c>SST.StableRef</c> to the assembly definitions of the changed scripts and a
    /// <c>[RefTypeId]</c> to value types that have no stable id, so a later rename of their class is safe.
    /// </summary>
    public static class MigrationCodeRewriter
    {
        /// <summary>The plan or result of a code rewrite.</summary>
        public sealed class Result
        {
            /// <summary>New content per script or assembly definition path.</summary>
            public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
            public int ConvertedFields;
            public int AddedTypeIds;
            public readonly List<string> Changes = new();
            public readonly List<string> Warnings = new();
            /// <summary>
            /// The retyped fields, as <c>Assembly|Owner.FullName|field</c>: the data step waits until the loaded scripts
            /// declare them as StableRef (see <see cref="MigrationContinuation"/>).
            /// </summary>
            public readonly List<string> ExpectedFields = new();
            public bool Changed => Files.Count > 0;
        }

        /// <summary>A field declaration rewritten in one script.</summary>
        public sealed class Declaration
        {
            public int Start;
            public int End;
            public List<string> Lines;
            public bool IsList;
        }

        private const string StableRefAssembly = "SST.StableRef";

        private static readonly HashSet<string> DroppedAttributes = new(StringComparer.Ordinal)
        {
            // The field becomes a wrapper: attributes of plain [SerializeReference] fields and array drawers don't apply.
            "HideReferenceObjectPicker", "SerializeReferenceDropdown", "SubclassSelector", "RefSelector", "ListDrawerSettings"
        };

        private static readonly Regex FieldRegex = new(
            @"^(?<mods>(?:(?:public|private|protected|internal|new|readonly)\s+)*)(?<type>[A-Za-z_][\w\.]*(?:<.+>)?(?:\[\])?)\s+(?<name>[A-Za-z_]\w*)\s*(?:=\s*(?<init>.+?))?\s*;\s*(?<comment>//.*)?$",
            RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new(
            @"^(?<mods>(?:(?:public|private|protected|internal|new)\s+)*)(?<type>[A-Za-z_][\w\.]*(?:<.+>)?(?:\[\])?)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*get;\s*(?<setmods>(?:(?:private|protected|internal)\s+)*)set;\s*\}\s*(?:=\s*(?<init>.+?)\s*;)?\s*(?<comment>//.*)?$",
            RegexOptions.Compiled);
        private static readonly Regex UsingRegex = new(@"^using\s+[\w\.]+\s*;", RegexOptions.Compiled);

        #region Plan

        /// <summary>
        /// Plans the rewrite of every <c>[SerializeReference]</c> field declared in a script under one of
        /// <paramref name="roots"/>. Fields with <c>[RefSelector]</c> (kept as plain references on purpose), fields
        /// of generic parameter types and fields of StableRef's own assemblies are left out.
        /// </summary>
        public static Result Plan(IReadOnlyList<string> roots)
        {
            var result = new Result();
            var byScript = new Dictionary<string, List<FieldInfo>>(StringComparer.Ordinal);
            var valueTypes = new HashSet<Type>();
            var projectAssemblies = ProjectAssemblies(roots);

            foreach (var field in TypeCache.GetFieldsWithAttribute<SerializeReference>())
            {
                var owner = field.DeclaringType;
                if (owner == null || field.IsStatic) continue;
                string assembly = owner.Assembly.GetName().Name;
                if (assembly.StartsWith(StableRefAssembly, StringComparison.Ordinal)) continue;
                if (field.IsDefined(typeof(RefSelectorAttribute), true)) continue;

                var valueType = ValueType(field.FieldType, out _);
                if (valueType == null || valueType.ContainsGenericParameters || valueType.IsValueType
                    || typeof(UnityEngine.Object).IsAssignableFrom(valueType))
                    continue;

                var script = StableRefEditorUtility.FindScript(owner);
                string path = script != null ? AssetDatabase.GetAssetPath(script) : null;
                if (string.IsNullOrEmpty(path))
                {
                    if (projectAssemblies.Contains(assembly))
                        result.Warnings.Add($"{owner.FullName}.{field.Name}: the script declaring it wasn't found — left as is");
                    continue;
                }
                if (!roots.Any(r => path.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal))) continue;

                if (!byScript.TryGetValue(path, out var fields)) byScript[path] = fields = new List<FieldInfo>();
                fields.Add(field);
                valueTypes.Add(valueType);
            }

            var changedScripts = new List<string>();
            foreach (var pair in byScript.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string text = File.ReadAllText(pair.Key);
                string rewritten = RewriteScript(pair.Key, text, pair.Value, result);
                if (rewritten == text) continue;
                result.Files[pair.Key] = rewritten;
                changedScripts.Add(pair.Key);
            }

            if (changedScripts.Count > 0)
            {
                AddTypeIds(TypeIdCandidates(valueTypes, result.Warnings), roots, result);
                AddAssemblyReferences(changedScripts.Concat(result.Files.Keys).Distinct().ToList(), result);
                WarnAboutPropertyPaths(roots, result);
            }

            return result;
        }

        /// <summary>An entry of <see cref="Result.ExpectedFields"/>.</summary>
        public static string ExpectedField(Type owner, string fieldName) =>
            $"{owner.Assembly.GetName().Name}|{owner.FullName}|{fieldName}";

        /// <summary>Writes the planned files.</summary>
        public static void Apply(Result result)
        {
            foreach (var pair in result.Files)
            {
                var bytes = File.ReadAllBytes(pair.Key);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                File.WriteAllText(pair.Key, pair.Value, new UTF8Encoding(bom));
            }
        }

        /// <summary>Assemblies compiled from scripts under the roots.</summary>
        private static HashSet<string> ProjectAssemblies(IReadOnlyList<string> roots)
        {
            return new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => a.sourceFiles.Any(f => roots.Any(r => f.Replace('\\', '/').StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal))))
                .Select(a => a.name), StringComparer.Ordinal);
        }

        /// <summary>The referenced type of a single field, or the element type of an array / <c>List&lt;T&gt;</c>.</summary>
        private static Type ValueType(Type fieldType, out bool isList)
        {
            isList = false;
            if (fieldType.IsArray)
            {
                isList = true;
                return fieldType.GetArrayRank() == 1 ? fieldType.GetElementType() : null;
            }
            if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
            {
                isList = true;
                return fieldType.GetGenericArguments()[0];
            }
            return fieldType;
        }

        #endregion

        #region Declarations

        private static string RewriteScript(string path, string text, List<FieldInfo> fields, Result result)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var declarations = new List<Declaration>();

            foreach (var field in fields)
            {
                bool isBackingField = field.Name.StartsWith("<", StringComparison.Ordinal);
                string name = isBackingField ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name;
                if (TryRewriteDeclaration(lines, name, isBackingField, NeedsInitializer(field.DeclaringType), out var declaration, out string problem))
                {
                    declarations.Add(declaration);
                    result.ConvertedFields++;
                    result.Changes.Add($"{path}: {field.DeclaringType?.Name}.{name} → {(declaration.IsList ? "StableRefList" : "StableRef")}");
                    if (field.DeclaringType != null)
                        result.ExpectedFields.Add(ExpectedField(field.DeclaringType, field.Name));
                }
                else
                {
                    result.Warnings.Add($"{path}: {field.DeclaringType?.Name}.{name} — {problem}; left as is");
                }
            }

            if (declarations.Count == 0) return text;
            foreach (var declaration in declarations.OrderByDescending(d => d.Start))
            {
                lines.RemoveRange(declaration.Start, declaration.End - declaration.Start);
                lines.InsertRange(declaration.Start, declaration.Lines);
            }

            EnsureUsings(lines, "SST.StableRef");
            return string.Join(newLine, lines);
        }

        /// <summary>
        /// Finds the one-line declaration of <paramref name="name"/> marked <c>[SerializeReference]</c> (attributes may
        /// sit on the lines above it) and retypes it in place: the type becomes <c>StableRef&lt;T&gt;</c> /
        /// <c>StableRefList&lt;T&gt;</c>, <c>[SerializeReference]</c> is removed — or becomes <c>[SerializeField]</c>
        /// where the field isn't public — and the name, the other attributes and the layout stay. With
        /// <paramref name="addInitializer"/> a declaration without an initializer gets <c>= new()</c>.
        /// </summary>
        public static bool TryRewriteDeclaration(List<string> lines, string name, bool isProperty, bool addInitializer,
            out Declaration declaration, out string problem)
        {
            declaration = null;
            problem = "declaration not found (only one-line declarations are supported)";
            var candidates = new List<(int Start, int Line, List<(string Text, bool FieldTarget)> Attributes, Match Match, string Indent)>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.IndexOf(name, StringComparison.Ordinal) < 0) continue;

                string indent = line.Substring(0, line.Length - line.TrimStart().Length);
                if (!TrySplitAttributes(line.TrimStart(), out var attributes, out string rest)) continue;
                var match = (isProperty ? PropertyRegex : FieldRegex).Match(rest);
                if (!match.Success || match.Groups["name"].Value != name) continue;

                int start = i;
                while (start > 0 && TrySplitAttributes(lines[start - 1].Trim(), out var above, out string remainder)
                       && above.Count > 0 && (remainder.Length == 0 || remainder.StartsWith("//", StringComparison.Ordinal)))
                {
                    attributes.InsertRange(0, above);
                    start--;
                }

                bool serializeReference = attributes.Any(a => AttributeName(a.Text) == "SerializeReference" && a.FieldTarget == isProperty);
                if (serializeReference) candidates.Add((start, i, attributes, match, indent));
            }

            if (candidates.Count == 0) return false;
            if (candidates.Count > 1)
            {
                problem = "declared more than once in the file";
                return false;
            }

            var (first, declLine, attrs, m, _) = candidates[0];
            string type = m.Groups["type"].Value;
            bool isList = TryElementType(type, out string element);
            bool isPublic = Regex.IsMatch(m.Groups["mods"].Value, @"\bpublic\b");
            // An auto-property's backing field is private whatever the property's access.
            bool needsSerializeField = (isProperty || !isPublic)
                                       && !attrs.Any(a => AttributeName(a.Text) == "SerializeField" && a.FieldTarget == isProperty);

            var replacement = new List<string>();
            for (int i = first; i <= declLine; i++)
            {
                string line = lines[i];
                string indent = line.Substring(0, line.Length - line.TrimStart().Length);
                TrySplitAttributes(line.TrimStart(), out var items, out string rest);
                string attributeText = line.TrimStart().Substring(0, line.TrimStart().Length - rest.Length);

                bool touched = false;
                var kept = new List<(string Text, bool FieldTarget)>();
                foreach (var item in items)
                {
                    string attributeName = AttributeName(item.Text);
                    if (attributeName == "SerializeReference" && item.FieldTarget == isProperty)
                    {
                        touched = true;
                        if (!needsSerializeField) continue;
                        int at = item.Text.LastIndexOf("SerializeReference", StringComparison.Ordinal);
                        kept.Add((item.Text.Substring(0, at) + "SerializeField" + item.Text.Substring(at + "SerializeReference".Length), item.FieldTarget));
                        needsSerializeField = false;
                    }
                    else if (DroppedAttributes.Contains(attributeName))
                    {
                        touched = true;
                    }
                    else
                    {
                        kept.Add(item);
                    }
                }

                if (i == declLine) rest = RetypeDeclaration(rest, isProperty, isList, element, addInitializer);
                if (!touched)
                {
                    replacement.Add(indent + attributeText + rest);
                    continue;
                }
                if (kept.Count == 0 && rest.Length == 0) continue;
                replacement.Add(indent + JoinAttributes(kept) + (kept.Count > 0 && rest.Length > 0 ? " " : "") + rest);
            }

            declaration = new Declaration { Start = first, End = declLine + 1, Lines = replacement, IsList = isList };
            problem = null;
            return true;
        }

        /// <summary>
        /// Whether a retyped field of <paramref name="owner"/> needs <c>= new()</c>: a plain class created with
        /// <c>new</c> would leave the wrapper null. Unity creates it for components and ScriptableObjects, and a struct
        /// can't have field initializers in C# 9.
        /// </summary>
        public static bool NeedsInitializer(Type owner) =>
            owner != null && !owner.IsValueType && !typeof(UnityEngine.Object).IsAssignableFrom(owner);

        /// <summary>
        /// Replaces the type of a declaration matched by the field / property regex, wraps its initializer, or adds
        /// <c>= new()</c> when it has none and <paramref name="addInitializer"/> is set.
        /// </summary>
        private static string RetypeDeclaration(string declaration, bool isProperty, bool isList, string element, bool addInitializer)
        {
            var m = (isProperty ? PropertyRegex : FieldRegex).Match(declaration);
            var type = m.Groups["type"];
            var init = m.Groups["init"];
            string result = declaration;
            if (init.Success)
            {
                result = result.Substring(0, init.Index) + Initializer(init.Value.Trim(), isList) + result.Substring(init.Index + init.Length);
            }
            else if (addInitializer && !isProperty)
            {
                int at = m.Groups["name"].Index + m.Groups["name"].Length;
                result = result.Substring(0, at) + " = new()" + result.Substring(at);
            }
            else if (addInitializer)
            {
                var comment = m.Groups["comment"];
                int at = result.LastIndexOf('}', comment.Success ? comment.Index : result.Length - 1) + 1;
                result = result.Substring(0, at) + " = new();" + result.Substring(at);
            }
            string wrapper = isList ? $"StableRefList<{element}>" : $"StableRef<{type.Value}>";
            return result.Substring(0, type.Index) + wrapper + result.Substring(type.Index + type.Length);
        }

        private static string JoinAttributes(List<(string Text, bool FieldTarget)> items)
        {
            var groups = new List<string>();
            for (int i = 0; i < items.Count;)
            {
                bool target = items[i].FieldTarget;
                var group = new List<string>();
                for (; i < items.Count && items[i].FieldTarget == target; i++) group.Add(items[i].Text);
                groups.Add("[" + (target ? "field: " : "") + string.Join(", ", group) + "]");
            }
            return string.Join(" ", groups);
        }

        private static bool TryElementType(string type, out string element)
        {
            element = null;
            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                element = type.Substring(0, type.Length - 2);
                return true;
            }
            var list = Regex.Match(type, @"^(?:System\.Collections\.Generic\.)?List<(.+)>$");
            if (!list.Success) return false;
            element = list.Groups[1].Value;
            return true;
        }

        private static string Initializer(string init, bool isList)
        {
            if (init == null || init == "null" || init == "default") return "new()";
            if (!isList) return $"new({init})";
            // A target-typed collection initializer fills the StableRefList itself (it has Add).
            if (Regex.IsMatch(init, @"^new\s*\(\s*\)\s*\{")) return init;
            // An empty collection needs no values; anything else is copied in.
            if (Regex.IsMatch(init, @"^new\s*(?:List<[^>]*>)?\s*\(\s*\)$") || Regex.IsMatch(init, @"^new\s+[\w\.<>]+\s*\[\s*0\s*\]$")
                || Regex.IsMatch(init, @"^(?:System\.)?Array\.Empty<.*>\(\)$") || init == "new()" || init == "[]")
                return "new()";
            return $"new({init})";
        }

        /// <summary>
        /// Splits leading attribute lists off a line: <c>[A, B(x)] [field: C] rest</c>. Commas, brackets and quotes
        /// inside arguments are respected; the <c>field:</c> target applies to its whole list.
        /// </summary>
        private static bool TrySplitAttributes(string text, out List<(string Text, bool FieldTarget)> attributes, out string rest)
        {
            attributes = new List<(string, bool)>();
            rest = text;
            int i = 0;
            while (i < text.Length && text[i] == '[')
            {
                int depth = 0, itemStart = i + 1;
                bool fieldTarget = false, closed = false;
                char quote = '\0';
                var items = new List<string>();
                for (int j = i; j < text.Length; j++)
                {
                    char c = text[j];
                    if (quote != '\0')
                    {
                        if (c == '\\') j++;
                        else if (c == quote) quote = '\0';
                        continue;
                    }
                    if (c == '"' || c == '\'') quote = c;
                    else if (c == '[' || c == '(') depth++;
                    else if (c == ']' || c == ')')
                    {
                        depth--;
                        if (depth == 0 && c == ']')
                        {
                            items.Add(text.Substring(itemStart, j - itemStart));
                            i = j + 1;
                            closed = true;
                            break;
                        }
                    }
                    else if (c == ',' && depth == 1)
                    {
                        items.Add(text.Substring(itemStart, j - itemStart));
                        itemStart = j + 1;
                    }
                }
                if (!closed) return false;

                if (items.Count > 0 && items[0].TrimStart().StartsWith("field:", StringComparison.Ordinal))
                {
                    fieldTarget = true;
                    items[0] = items[0].TrimStart().Substring("field:".Length);
                }
                foreach (string item in items)
                    if (item.Trim().Length > 0) attributes.Add((item.Trim(), fieldTarget));

                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }

            rest = text.Substring(i);
            return true;
        }

        private static string AttributeName(string attribute)
        {
            int paren = attribute.IndexOf('(');
            string name = (paren < 0 ? attribute : attribute.Substring(0, paren)).Trim();
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            return name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
        }

        /// <summary>Adds the usings after the last one outside any <c>#if</c>, or at the top of the file.</summary>
        public static void EnsureUsings(List<string> lines, params string[] namespaces)
        {
            int depth = 0, insertAt = 0;
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal)) depth++;
                else if (line.StartsWith("#endif", StringComparison.Ordinal)) depth--;
                else if (UsingRegex.IsMatch(line) && depth == 0)
                {
                    // A using inside #if doesn't count: the branch where it is off would lose it.
                    present.Add(line.Substring(6).TrimEnd(';').Trim());
                    insertAt = i + 1;
                }
                else if (line.StartsWith("namespace", StringComparison.Ordinal) || line.Contains("class ") || line.Contains("struct "))
                {
                    break;
                }
            }

            foreach (string ns in namespaces.Reverse())
                if (!present.Contains(ns)) lines.Insert(insertAt, $"using {ns};");
        }

        #endregion

        #region Stable ids

        /// <summary>
        /// The types that can be values of fields of <paramref name="valueTypes"/> — the ones the selector offers:
        /// concrete, non-generic reference types with a parameterless constructor, not compiler-generated, not
        /// <c>UnityEngine.Object</c>. Fields of type <c>object</c> are skipped with a warning: every class of the project
        /// would qualify.
        /// </summary>
        public static HashSet<Type> TypeIdCandidates(IEnumerable<Type> valueTypes, List<string> warnings)
        {
            var result = new HashSet<Type>();
            foreach (var valueType in valueTypes)
            {
                if (valueType == typeof(object))
                {
                    warnings.Add("fields of type object: [RefTypeId] is not added to their values automatically — add it by " +
                                 "hand to the classes they hold that share a file with other classes");
                    continue;
                }
                if (IsValueCandidate(valueType)) result.Add(valueType);
                foreach (var derived in TypeCache.GetTypesDerivedFrom(valueType))
                    if (IsValueCandidate(derived)) result.Add(derived);
            }
            return result;
        }

        private static bool IsValueCandidate(Type type) =>
            !type.IsValueType && !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters
            && !typeof(UnityEngine.Object).IsAssignableFrom(type)
            && !type.Name.StartsWith("<", StringComparison.Ordinal)
            && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
            && type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null;

        /// <summary>
        /// Gives a <c>[RefTypeId]</c> to every possible value (<see cref="TypeIdCandidates"/>) that has no stable id yet —
        /// a class sharing its file with others, which can't use the script's GUID. The id is its full name.
        /// </summary>
        private static void AddTypeIds(HashSet<Type> concrete, IReadOnlyList<string> roots, Result result)
        {
            foreach (var group in concrete
                         .Where(t => t.GetCustomAttribute<RefTypeIdAttribute>(false) == null && StableRefTypeRegistry.GetOrAssignId(t) == null)
                         .Select(t => (Type: t, Script: StableRefEditorUtility.FindScript(t)))
                         .Where(x => x.Script != null)
                         .GroupBy(x => AssetDatabase.GetAssetPath(x.Script)))
            {
                string path = group.Key;
                if (!roots.Any(r => path.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal))) continue;

                string text = result.Files.TryGetValue(path, out var planned) ? planned : File.ReadAllText(path);
                string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
                var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
                bool changed = false;
                foreach (var (type, _) in group)
                {
                    string shortName = type.Name;
                    int tick = shortName.IndexOf('`');
                    if (tick >= 0) shortName = shortName.Substring(0, tick);
                    var declarations = lines.Select((l, i) => (l, i))
                        .Where(x => Regex.IsMatch(x.l, $@"\b(class|struct|record)\s+{Regex.Escape(shortName)}\b"))
                        .ToList();
                    if (declarations.Count != 1)
                    {
                        result.Warnings.Add($"{path}: couldn't place [RefTypeId] on {type.FullName} — add it by hand");
                        continue;
                    }

                    string id = type.FullName.Replace('+', '.');
                    var (line, index) = declarations[0];
                    string pad = line.Substring(0, line.Length - line.TrimStart().Length);
                    lines.Insert(index, $"{pad}[RefTypeId(\"{id}\")]");
                    result.AddedTypeIds++;
                    result.Changes.Add($"{path}: [RefTypeId(\"{id}\")] on {type.Name}");
                    changed = true;
                }

                if (!changed) continue;
                EnsureUsings(lines, "SST.StableRef");
                result.Files[path] = string.Join(newLine, lines);
            }
        }

        #endregion

        #region Assembly definitions

        /// <summary>Adds <c>SST.StableRef</c> to the references of the assembly definitions of the changed scripts.</summary>
        private static void AddAssemblyReferences(List<string> scripts, Result result)
        {
            string stableRefAsmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(StableRefAssembly);
            string stableRefGuid = stableRefAsmdef != null ? AssetDatabase.AssetPathToGUID(stableRefAsmdef) : null;

            foreach (string asmdef in scripts
                         .Select(CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath)
                         .Where(p => !string.IsNullOrEmpty(p))
                         .Distinct())
            {
                string text = File.ReadAllText(asmdef);
                string updated = AddReference(text, StableRefAssembly, stableRefGuid);
                if (updated == text) continue;
                result.Files[asmdef] = updated;
                result.Changes.Add($"{asmdef}: references {StableRefAssembly}");
            }
        }

        /// <summary>
        /// Adds the assembly to the <c>references</c> of an assembly definition, by GUID when the file already refers
        /// to assemblies by GUID, by name otherwise; unchanged when it is already referenced.
        /// </summary>
        public static string AddReference(string asmdef, string assemblyName, string guid)
        {
            var match = Regex.Match(asmdef, @"""references""\s*:\s*\[(?<items>[^\]]*)\]");
            if (!match.Success) return asmdef;

            var items = Regex.Matches(match.Groups["items"].Value, @"""([^""]*)""").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            if (items.Contains(assemblyName) || (guid != null && items.Contains("GUID:" + guid))) return asmdef;

            bool byGuid = guid != null && items.Any(i => i.StartsWith("GUID:", StringComparison.Ordinal));
            items.Add(byGuid ? "GUID:" + guid : assemblyName);

            string newLine = asmdef.Contains("\r\n") ? "\r\n" : "\n";
            string list = "\"references\": [" + newLine + string.Join("," + newLine, items.Select(i => $"        \"{i}\"")) + newLine + "    ]";
            return asmdef.Substring(0, match.Index) + list + asmdef.Substring(match.Index + match.Length);
        }

        #endregion

        #region Warnings

        /// <summary>
        /// Editor code that finds a converted field by name (<c>FindProperty("effect")</c>) now reaches the StableRef
        /// wrapper, whose value is the <c>Value</c> child (<c>effect.Value</c>); such places are listed for a manual check.
        /// </summary>
        private static void WarnAboutPropertyPaths(IReadOnlyList<string> roots, Result result)
        {
            var names = new HashSet<string>(result.Changes
                .Select(c => Regex.Match(c, @"\.(\w+) → StableRef"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value), StringComparer.Ordinal);
            if (names.Count == 0) return;

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);
                    if (text.IndexOf("FindProperty", StringComparison.Ordinal) < 0) continue;
                    foreach (Match m in Regex.Matches(text, @"FindProperty(?:Relative)?\(\s*""(\w+)"""))
                        if (names.Contains(m.Groups[1].Value))
                            result.Warnings.Add($"{file.Replace('\\', '/')}: FindProperty(\"{m.Groups[1].Value}\") — the field is a StableRef wrapper now, its value is the Value child; check this path");
                }
            }
        }

        #endregion
    }
}
#endif
