#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// What the migrator needs to know about the serialized fields of a type: which of them are StableRef fields, and
    /// which plain fields (nested classes, arrays of them) can lead to one. Fields are looked up by their YAML key —
    /// the field name or any <see cref="FormerlySerializedAsAttribute"/> alias.
    /// </summary>
    internal static class MigrationFieldModel
    {
        public enum FieldKind
        {
            Other,
            StableRef,
            StableRefList,
            /// <summary>A plain <c>[Serializable]</c> class or struct stored inline.</summary>
            Nested,
            /// <summary>An array or <c>List&lt;T&gt;</c> of plain <c>[Serializable]</c> classes or structs.</summary>
            NestedArray,
            /// <summary>A <c>[SerializeReference]</c> field: its value lives in the references block.</summary>
            ManagedReference
        }

        public sealed class FieldModel
        {
            public FieldInfo Field;
            public FieldKind Kind;
            /// <summary>The nested type for <see cref="FieldKind.Nested"/> and <see cref="FieldKind.NestedArray"/>.</summary>
            public Type NestedType;
            public string Name => Field.Name;
        }

        private const BindingFlags DeclaredFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, Dictionary<string, FieldModel>> _fields = new();
        private static readonly Dictionary<Type, bool> _mayContain = new();
        private static HashSet<string> _leadingKeys;

        /// <summary>The serialized field stored under <paramref name="yamlKey"/>, or <c>null</c>.</summary>
        public static FieldModel Find(Type type, string yamlKey)
        {
            if (type == null || string.IsNullOrEmpty(yamlKey)) return null;
            return FieldsOf(type).TryGetValue(yamlKey, out var field) ? field : null;
        }

        /// <summary>
        /// Whether the inline data of <paramref name="type"/> can hold a StableRef field, directly or through nested
        /// classes and arrays of them. Values behind <c>[SerializeReference]</c> don't count: they are stored in the
        /// references block and checked by their own type.
        /// </summary>
        public static bool MayContainStableRef(Type type)
        {
            if (type == null) return false;
            if (_mayContain.TryGetValue(type, out bool cached)) return cached;
            _mayContain[type] = false; // guards against recursive types
            bool result = FieldsOf(type).Values.Distinct().Any(f =>
                f.Kind == FieldKind.StableRef || f.Kind == FieldKind.StableRefList
                || ((f.Kind == FieldKind.Nested || f.Kind == FieldKind.NestedArray) && MayContainStableRef(f.NestedType)));
            return _mayContain[type] = result;
        }

        /// <summary>
        /// Whether some type has a field stored under <paramref name="yamlKey"/> that is a StableRef field or leads to
        /// one. A cheap filter: prefab override paths whose first segment fails it are skipped without resolving types.
        /// </summary>
        public static bool MayLeadToStableRef(string yamlKey) => LeadingKeys().Contains(yamlKey);

        private static HashSet<string> LeadingKeys()
        {
            if (_leadingKeys != null) return _leadingKeys;

            // Only assemblies that reference StableRef, directly or through another such assembly, can declare
            // a StableRef field or a field of a type that holds one.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
            var related = new HashSet<string>(StringComparer.Ordinal) { typeof(StableRefBase).Assembly.GetName().Name };
            bool grown = true;
            while (grown)
            {
                grown = false;
                foreach (var assembly in assemblies)
                {
                    string name = assembly.GetName().Name;
                    if (related.Contains(name)) continue;
                    if (assembly.GetReferencedAssemblies().Any(r => related.Contains(r.Name)))
                        grown |= related.Add(name);
                }
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies.Where(a => related.Contains(a.GetName().Name)))
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var type in types)
                {
                    if (type.IsGenericTypeDefinition || type.IsInterface) continue;
                    foreach (var pair in FieldsOf(type))
                    {
                        var field = pair.Value;
                        if (field.Kind == FieldKind.StableRef || field.Kind == FieldKind.StableRefList
                            || ((field.Kind == FieldKind.Nested || field.Kind == FieldKind.NestedArray)
                                && MayContainStableRef(field.NestedType)))
                            keys.Add(pair.Key);
                    }
                }
            }

            return _leadingKeys = keys;
        }

        private static Dictionary<string, FieldModel> FieldsOf(Type type)
        {
            if (_fields.TryGetValue(type, out var cached)) return cached;

            var result = new Dictionary<string, FieldModel>(StringComparer.Ordinal);
            for (var t = type; t != null && t != typeof(object) && t != typeof(MonoBehaviour)
                                && t != typeof(ScriptableObject) && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(DeclaredFields))
                {
                    if (!IsSerialized(field)) continue;
                    var model = new FieldModel { Field = field, Kind = Classify(field, out var nested), NestedType = nested };
                    result.TryAdd(field.Name, model);
                    foreach (var alias in field.GetCustomAttributes<FormerlySerializedAsAttribute>())
                        result.TryAdd(alias.oldName, model);
                }
            }

            return _fields[type] = result;
        }

        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral || field.IsNotSerialized) return false;
            return field.IsPublic
                   || field.IsDefined(typeof(SerializeField), true)
                   || field.IsDefined(typeof(SerializeReference), true);
        }

        private static FieldKind Classify(FieldInfo field, out Type nested)
        {
            nested = null;
            var type = field.FieldType;
            if (field.IsDefined(typeof(SerializeReference), true)) return FieldKind.ManagedReference;
            if (StableRefReflection.IsStableRef(type, out _)) return FieldKind.StableRef;
            if (StableRefReflection.IsStableRefList(type, out _)) return FieldKind.StableRefList;

            var element = ElementType(type);
            if (element != null)
            {
                if (!IsPlainSerializable(element)) return FieldKind.Other;
                nested = element;
                return FieldKind.NestedArray;
            }

            if (!IsPlainSerializable(type)) return FieldKind.Other;
            nested = type;
            return FieldKind.Nested;
        }

        private static Type ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return type.GetGenericArguments()[0];
            return null;
        }

        private static bool IsPlainSerializable(Type type) =>
            !type.IsPrimitive && !type.IsEnum && type != typeof(string) && !type.IsAbstract && !type.IsInterface
            && !type.IsGenericTypeDefinition && !typeof(UnityEngine.Object).IsAssignableFrom(type)
            && type.IsSerializable;
    }
}
#endif
