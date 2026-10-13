#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Answers <see cref="MigrationUsageFixer"/>'s questions about types from the loaded assemblies — the types as they
    /// were before the retype, so a retyped field still has its value's type.
    /// </summary>
    /// <remarks>Plain reflection, no Unity API. A name that matches several types in the file's namespaces is unknown.</remarks>
    public sealed class MigrationTypeModel : MigrationUsageFixer.ITypeModel
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                             BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<string, Type> Keywords = new(StringComparer.Ordinal)
        {
            ["object"] = typeof(object), ["string"] = typeof(string), ["bool"] = typeof(bool), ["byte"] = typeof(byte),
            ["sbyte"] = typeof(sbyte), ["char"] = typeof(char), ["short"] = typeof(short), ["ushort"] = typeof(ushort),
            ["int"] = typeof(int), ["uint"] = typeof(uint), ["long"] = typeof(long), ["ulong"] = typeof(ulong),
            ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal)
        };

        private readonly Dictionary<string, Type> _byFullName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Type>> _byName = new(StringComparer.Ordinal);
        private readonly HashSet<(Type, string)> _retyped = new();
        private readonly Dictionary<(string, string), Type> _resolved = new();

        /// <param name="retyped">The retyped single-reference fields: declaring type and the name used in code.</param>
        /// <param name="assemblies">Where types are looked up; the current domain's assemblies when <c>null</c>.</param>
        public MigrationTypeModel(IEnumerable<(Type DeclaringType, string Name)> retyped, IEnumerable<Assembly> assemblies = null)
        {
            foreach (var (type, name) in retyped)
                if (type != null) _retyped.Add((Definition(type), name));

            foreach (var assembly in assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (var type in types)
                {
                    if (type.FullName == null) continue;
                    string fullName = type.FullName.Replace('+', '.');
                    if (!_byFullName.ContainsKey(fullName)) _byFullName[fullName] = type;
                    if (!_byName.TryGetValue(type.Name, out var list)) _byName[type.Name] = list = new List<Type>();
                    list.Add(type);
                }
            }
        }

        public object ResolveType(string name, IReadOnlyList<string> namespaces)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = string.Join(";", namespaces);
            if (_resolved.TryGetValue((name, key), out var cached)) return cached;
            var type = Resolve(name.Replace(" ", ""), namespaces);
            _resolved[(name, key)] = type;
            return type;
        }

        private Type Resolve(string name, IReadOnlyList<string> namespaces)
        {
            if (name.StartsWith("global::", StringComparison.Ordinal)) name = name.Substring("global::".Length);
            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                var underlying = Resolve(name.Substring(0, name.Length - 1), namespaces);
                return underlying is { IsValueType: true } ? typeof(Nullable<>).MakeGenericType(underlying) : underlying;
            }
            if (name.EndsWith("]", StringComparison.Ordinal))
            {
                int open = name.LastIndexOf('[');
                if (open <= 0) return null;
                var element = Resolve(name.Substring(0, open), namespaces);
                int rank = name.Length - open - 1;
                return element == null ? null : rank == 1 ? element.MakeArrayType() : element.MakeArrayType(rank);
            }
            if (name.StartsWith("(", StringComparison.Ordinal))
            {
                // A tuple of unnamed elements: (A, B) → ValueTuple<A, B>.
                if (!name.EndsWith(")", StringComparison.Ordinal)) return null;
                var elements = SplitTopLevel(name.Substring(1, name.Length - 2)).Select(e => Resolve(e, namespaces)).ToArray();
                if (elements.Length < 2 || elements.Length > 7 || elements.Any(e => e == null)) return null;
                var tuple = typeof(ValueTuple).Assembly.GetType("System.ValueTuple`" + elements.Length);
                return tuple?.MakeGenericType(elements);
            }
            if (Keywords.TryGetValue(name, out var keyword)) return keyword;

            int angle = name.IndexOf('<');
            if (angle > 0 && name.EndsWith(">", StringComparison.Ordinal))
            {
                var arguments = SplitTopLevel(name.Substring(angle + 1, name.Length - angle - 2)).ToList();
                var definition = Find(name.Substring(0, angle) + "`" + arguments.Count, namespaces);
                if (definition == null || !definition.IsGenericTypeDefinition) return null;
                var resolved = arguments.Select(a => Resolve(a, namespaces)).ToArray();
                if (resolved.Any(a => a == null)) return null;
                try { return definition.MakeGenericType(resolved); }
                catch (ArgumentException) { return null; }
            }
            return Find(name, namespaces);
        }

        /// <summary>A type named <c>Name</c>, <c>Ns.Name</c> or <c>Outer.Inner</c> (generic definitions as <c>Name`N</c>) seen from <paramref name="namespaces"/>.</summary>
        private Type Find(string name, IReadOnlyList<string> namespaces)
        {
            // The nearest namespace wins, as in C#: an enclosing namespace before the using directives.
            foreach (string ns in namespaces)
            {
                if (_byFullName.TryGetValue(ns.Length == 0 ? name : ns + "." + name, out var type)) return type;
            }

            // A nested type seen from its outer type, or a name that is unique in the domain.
            int dot = name.LastIndexOf('.');
            string simple = dot < 0 ? name : name.Substring(dot + 1);
            if (!_byName.TryGetValue(simple, out var candidates)) return null;
            var matching = candidates.Where(t => dot < 0 || t.FullName.Replace('+', '.').EndsWith("." + name, StringComparison.Ordinal)).ToList();
            return matching.Count == 1 ? matching[0] : null;
        }

        public object MemberType(object type, string member)
        {
            foreach (var t in Hierarchy(type as Type))
            {
                var field = t.GetField(member, Members);
                if (field != null) return ValueType(t, member, field.FieldType);
                PropertyInfo property;
                try { property = t.GetProperty(member, Members); }
                catch (AmbiguousMatchException) { property = t.GetProperties(Members).FirstOrDefault(p => p.Name == member && p.GetIndexParameters().Length == 0); }
                if (property != null) return ValueType(t, member, property.PropertyType);
            }
            return null;
        }

        /// <summary>A retyped member read before the retype took effect (or after): the value's type, as the code means it.</summary>
        private Type ValueType(Type declaring, string member, Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(StableRef<>) && _retyped.Contains((Definition(declaring), member)))
                return type.GetGenericArguments()[0];
            return type;
        }

        public object CallType(object type, string method, int argumentCount, IReadOnlyList<object> typeArguments)
        {
            var returns = new HashSet<Type>();
            foreach (var candidate in Methods(type as Type, method, argumentCount))
            {
                var m = candidate;
                if (typeArguments.Count > 0)
                {
                    if (!m.IsGenericMethodDefinition || m.GetGenericArguments().Length != typeArguments.Count) continue;
                    try { m = m.MakeGenericMethod(typeArguments.Cast<Type>().ToArray()); }
                    catch (ArgumentException) { continue; }
                }
                else if (m.IsGenericMethodDefinition && m.ReturnType.ContainsGenericParameters) continue;
                if (m.ReturnType == typeof(void)) continue;
                returns.Add(m.ReturnType);
            }
            return returns.Count == 1 ? returns.First() : null;
        }

        public object ElementType(object type)
        {
            if (type is not Type t) return null;
            if (t.IsArray) return t.GetElementType();
            if (t == typeof(string)) return typeof(char);
            var enumerables = Hierarchy(t).Concat(t.GetInterfaces())
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0]).Distinct().ToList();
            if (enumerables.Count == 1) return enumerables[0];
            var indexer = Hierarchy(t).SelectMany(h => h.GetProperties(Members)).FirstOrDefault(p => p.GetIndexParameters().Length == 1);
            return indexer?.PropertyType;
        }

        public object IndexedType(object type)
        {
            if (type is not Type t) return null;
            if (t.IsArray) return t.GetElementType();
            if (t == typeof(string)) return typeof(char);
            var indexers = Hierarchy(t).Concat(t.IsInterface ? t.GetInterfaces() : Type.EmptyTypes)
                .SelectMany(h => h.GetProperties(Members)).Where(p => p.GetIndexParameters().Length == 1 && p.GetMethod is { IsPublic: true })
                .Select(p => p.PropertyType).Distinct().ToList();
            return indexers.Count == 1 ? indexers[0] : ElementType(type);
        }

        public object Sequence(object element) => element is Type t ? typeof(IEnumerable<>).MakeGenericType(t) : null;

        public object DeconstructedType(object type, int index, int count)
        {
            if (type is not Type t || index < 0 || index >= count) return null;
            if (t.IsGenericType && t.FullName != null && t.FullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
            {
                var elements = t.GetGenericArguments();
                return count == elements.Length && index < 7 ? elements[index] : null;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && count == 2)
                return t.GetGenericArguments()[index];
            var deconstruct = Hierarchy(t).SelectMany(h => h.GetMethods(Members))
                .Where(m => m.Name == "Deconstruct" && !m.IsStatic && m.GetParameters().Length == count && m.GetParameters().All(p => p.IsOut))
                .Select(m => m.GetParameters()[index].ParameterType.GetElementType()).Distinct().ToList();
            return deconstruct.Count == 1 ? deconstruct[0] : null;
        }

        public bool IsRetyped(object type, string member) =>
            Hierarchy(type as Type).Any(t => _retyped.Contains((Definition(t), member)));

        public MigrationUsageFixer.ArgumentUse Argument(object type, string method, int index, int argumentCount)
        {
            if (type is not Type t) return MigrationUsageFixer.ArgumentUse.Unknown;
            IEnumerable<MethodBase> candidates = method == null
                ? t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(c => Accepts(c, argumentCount))
                : Methods(t, method, argumentCount);

            bool any = false, value = false, wrapper = false;
            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                Type parameter;
                if (index < parameters.Length && !(index == parameters.Length - 1 && IsParams(parameters[index]) && argumentCount != parameters.Length))
                    parameter = parameters[index].ParameterType;
                else if (parameters.Length > 0 && IsParams(parameters[parameters.Length - 1]))
                    parameter = parameters[parameters.Length - 1].ParameterType.GetElementType();
                else continue;
                if (parameter == null) continue;
                if (parameter.IsByRef) parameter = parameter.GetElementType();
                any = true;
                if (parameter.IsGenericParameter || parameter == typeof(object)) value = true;
                else if (typeof(StableRefBase).IsAssignableFrom(parameter) || typeof(StableRefListBase).IsAssignableFrom(parameter)) wrapper = true;
            }
            if (!any) return MigrationUsageFixer.ArgumentUse.Unknown;
            if (wrapper) return MigrationUsageFixer.ArgumentUse.Wrapper;
            return value ? MigrationUsageFixer.ArgumentUse.Value : MigrationUsageFixer.ArgumentUse.Typed;
        }

        private static IEnumerable<MethodInfo> Methods(Type type, string name, int argumentCount)
        {
            if (type == null) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var types = Hierarchy(type);
            if (type.IsInterface) types = types.Concat(type.GetInterfaces()).Append(typeof(object));
            foreach (var t in types)
            {
                foreach (var m in t.GetMethods(Members))
                {
                    // An override or a hiding method is the same call as the one it replaces.
                    if (m.Name != name || !Accepts(m, argumentCount) || !seen.Add(m.ToString())) continue;
                    yield return m;
                }
            }
        }

        private static bool Accepts(MethodBase method, int argumentCount)
        {
            var parameters = method.GetParameters();
            int required = parameters.Count(p => !p.IsOptional && !IsParams(p));
            bool variadic = parameters.Length > 0 && IsParams(parameters[parameters.Length - 1]);
            return argumentCount >= required && (variadic || argumentCount <= parameters.Length);
        }

        private static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), false);

        /// <summary>The type and its base types, generic ones as constructed.</summary>
        private static IEnumerable<Type> Hierarchy(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                yield return t;
        }

        private static Type Definition(Type type) => type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;

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
    }
}
#endif
