#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    public static class StableRefPropertyUtils
    {
        public sealed class TypeEntry
        {
            public Type Type;
            public string Name;
            public string FullPath;
            public string FullPathLower;
            /// <summary>
            /// Lower-case text the selector search matches: <see cref="FullPath"/>, plus the type's own name when a
            /// <see cref="IRefTypeMetadataProvider"/> shows it under another one.
            /// </summary>
            public string SearchText;
            public string Category;
            public string Tooltip;
            public Color? Color;
            public int SortOrder;
        }

        private const string ManagedRefPrefix = "managedReference<";

        private const BindingFlags FieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private struct PathResolution
        {
            public FieldInfo Field;
            public Type ValueType;
            public Type RawFieldType;
            public bool IsStableRefValue;
        }

        private static readonly Dictionary<(Type, string), PathResolution> _pathCache = new();
        private static readonly Dictionary<string, Type> _managedRefBaseCache = new();
        private static readonly Dictionary<(Type, bool), TypeEntry[]> _typeCache = new();

        public static bool IsStableRefValueField(SerializedProperty property)
        {
            return TryResolvePath(property, out var r) && r.IsStableRefValue;
        }

        private static readonly Dictionary<string, Type> _fullTypenameCache = new();
        private static Dictionary<string, Assembly> _assembliesByName;

        /// <summary>
        /// Type of the value held by a managed-reference property, or <see langword="null"/> when it is empty or its
        /// class can't be loaded — the same answer as <c>managedReferenceValue?.GetType()</c>, but read from
        /// <see cref="SerializedProperty.managedReferenceFullTypename"/>, so the value isn't deserialized.
        /// </summary>
        internal static Type GetManagedReferenceType(SerializedProperty property)
        {
            if (property.managedReferenceId == StableRefEditorUtility.ManagedRefIdNull) return null;

            string full = property.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(full)) return null;

            if (!_fullTypenameCache.TryGetValue(full, out var type))
                _fullTypenameCache[full] = type = ResolveFullTypename(full);
            return type ?? property.managedReferenceValue?.GetType();
        }

        /// <summary>Whether a managed-reference property holds a value (see <see cref="GetManagedReferenceType"/>).</summary>
        internal static bool HasManagedValue(SerializedProperty property) => GetManagedReferenceType(property) != null;

        private static Type ResolveFullTypename(string full)
        {
            int space = full.IndexOf(' ');
            if (space <= 0) return null;

            if (_assembliesByName == null)
            {
                _assembliesByName = new Dictionary<string, Assembly>(StringComparer.Ordinal);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    _assembliesByName[asm.GetName().Name] = asm;
            }

            if (!_assembliesByName.TryGetValue(full.Substring(0, space), out var assembly)) return null;
            try { return assembly.GetType(full.Substring(space + 1).Replace('/', '+'), throwOnError: false); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Whether a scan should step into <paramref name="property"/> while looking for StableRef entries: only
        /// generic (struct / class) properties and arrays of them can hold one — strings, object references, arrays of
        /// primitives and other built-in types are skipped without visiting their elements. Managed references are
        /// decided by the caller.
        /// </summary>
        internal static bool MayHoldEntries(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Generic) return false;
            if (!property.isArray) return true;
            if (property.arraySize == 0) return false;

            var element = property.GetArrayElementAtIndex(0).propertyType;
            return element == SerializedPropertyType.Generic || element == SerializedPropertyType.ManagedReference;
        }

        public static bool IsStableRefList(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Generic) return false;
            if (!TryResolvePath(property, out var r)) return false;
            return r.ValueType != null && typeof(StableRefListBase).IsAssignableFrom(r.ValueType);
        }

        public static SerializedProperty GetStableRefListItems(SerializedProperty property)
            => property.FindPropertyRelative(StableRefEntry.ListItemsFieldName);

        /// <summary>
        /// Sets <paramref name="property"/> and every descendant that has visible children to the same
        /// expanded state — the recursive foldout behaviour Unity applies when you Alt/Option-click a
        /// foldout arrow. Mirrors Unity's internal <c>EditorGUI.SetExpandedRecurse</c>, so nested
        /// StableRef / StableRefList fields (whose custom drawers read <c>isExpanded</c>) fold with it.
        /// </summary>
        public static void SetExpandedRecursive(SerializedProperty property, bool expanded)
        {
            if (property == null) return;

            var search = property.Copy();
            search.isExpanded = expanded;

            int depth = search.depth;
            while (search.NextVisible(true) && search.depth > depth)
            {
                if (search.hasVisibleChildren)
                    search.isExpanded = expanded;
            }
        }

        public static bool IsStableRefArray(SerializedProperty property)
        {
            if (property == null || !property.isArray) return false;
            if (!TryResolvePath(property, out var r) || r.RawFieldType == null) return false;
            var raw = r.RawFieldType;
            Type elemType = raw.IsArray
                ? raw.GetElementType()
                : raw.IsGenericType && raw.GetGenericTypeDefinition() == typeof(List<>)
                    ? raw.GetGenericArguments()[0]
                    : null;
            return elemType != null && typeof(StableRefBase).IsAssignableFrom(elemType);
        }
        
        public static Type GetStableRefValueBaseType(SerializedProperty arrayProp)
        {
            if (!TryResolvePath(arrayProp, out var r) || r.RawFieldType == null) return typeof(object);
            var raw = r.RawFieldType;

            if (raw.IsGenericType && raw.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elemType = raw.GetGenericArguments()[0];
                if (elemType.IsGenericType && elemType.GetGenericTypeDefinition() == typeof(StableRef<>))
                    return elemType.GetGenericArguments()[0];
            }
            return typeof(object);
        }

        public static Type GetBaseType(SerializedProperty property)
        {
            var key = property.managedReferenceFieldTypename;
            if (!string.IsNullOrEmpty(key))
            {
                if (_managedRefBaseCache.TryGetValue(key, out var cached)) return cached;
                var resolved = ResolveBaseTypeFromTypename(key, property.type);
                _managedRefBaseCache[key] = resolved;
                return resolved;
            }

            return ResolveBaseTypeFromTypename(null, property.type);
        }

        public static Type GetArrayElementBaseType(SerializedProperty arrayProp)
        {
            bool isStable = IsStableRefArray(arrayProp);

            if (arrayProp.arraySize > 0)
            {
                var first = arrayProp.GetArrayElementAtIndex(0);
                var refProp = isStable ? first.FindPropertyRelative(StableRefEntry.ValueFieldName) : first;
                if (refProp != null && refProp.propertyType == SerializedPropertyType.ManagedReference)
                {
                    var t = GetBaseType(refProp);
                    if (t != null && t != typeof(object)) return t;
                }
            }

            if (isStable) return GetStableRefValueBaseType(arrayProp);

            if (!TryResolvePath(arrayProp, out var r)) return typeof(object);

            var raw = r.RawFieldType;
            if (raw == null) return typeof(object);
            if (raw.IsGenericType && raw.GetGenericTypeDefinition() == typeof(List<>)) return raw.GetGenericArguments()[0];
            if (raw.IsArray) return raw.GetElementType();
            return typeof(object);
        }

        public static bool IsAssignable(SerializedProperty property, Type valueType)
        {
            if (valueType == null) return false;
            var baseType = GetBaseType(property);
            return baseType == typeof(object) || baseType.IsAssignableFrom(valueType);
        }

        public static bool IsManagedReferenceArray(SerializedProperty property)
        {
            if (property == null || !property.isArray) return false;
            var et = property.arrayElementType;
            return !string.IsNullOrEmpty(et) && et.StartsWith(ManagedRefPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// The array / list that <paramref name="property"/> is an element of — either the element itself
        /// (<c>…Array.data[i]</c>, a plain <c>[SerializeReference]</c> element) or the <c>Value</c> of a StableRef
        /// element (<c>…Array.data[i].Value</c>). A reference nested deeper inside an element's value — including a
        /// plain field that happens to be named <c>Value</c> on a non-StableRef element — is not an element of that
        /// array, so element commands (duplicate, delete, insert) never act on the outer list.
        /// </summary>
        public static bool TryGetParentArray(SerializedProperty property, out SerializedProperty array, out int index)
        {
            array = null;
            index = -1;

            string path = property.propertyPath;
            int arrayMarker = path.LastIndexOf(".Array.data[", StringComparison.Ordinal);
            if (arrayMarker < 0) return false;

            int openBracket = path.IndexOf('[', arrayMarker);
            int closeBracket = path.IndexOf(']', openBracket + 1);
            if (openBracket < 0 || closeBracket < 0) return false;

            string rest = path.Substring(closeBracket + 1);
            if (rest.Length != 0)
            {
                if (rest != "." + StableRefEntry.ValueFieldName) return false;
                if (StableRefEntry.FindWrapperOfValue(property.serializedObject, path) == null) return false;
            }

            string idxStr = path.Substring(openBracket + 1, closeBracket - openBracket - 1);
            if (!int.TryParse(idxStr, out index)) return false;

            string arrayPath = path.Substring(0, arrayMarker);
            array = property.serializedObject.FindProperty(arrayPath);
            return array != null && array.isArray;
        }
        
        public static bool TryFindStableRefListChild(SerializedProperty property, out SerializedProperty result)
        {
            result = null;
            var iter = property.Copy();
            var end = property.GetEndProperty();
            if (!iter.NextVisible(true)) return false;

            SerializedProperty match = null;
            int matchCount = 0;

            while (!SerializedProperty.EqualContents(iter, end))
            {
                if (IsStableRefList(iter))
                {
                    var items = GetStableRefListItems(iter);
                    if (items != null)
                    {
                        match = items;
                        matchCount++;
                        if (matchCount > 1) return false;
                    }
                }
                if (!iter.NextVisible(false)) break;
            }

            if (matchCount == 1) { result = match; return true; }
            return false;
        }
        
        public static bool IsListPasteCompatible(SerializedProperty arrayProp, Type sourceElementType)
        {
            if (sourceElementType == null) return false;
            var targetType = GetArrayElementBaseType(arrayProp);
            if (targetType == null) return false;
            return targetType == typeof(object)
                || targetType.IsAssignableFrom(sourceElementType)
                || sourceElementType.IsAssignableFrom(targetType);
        }

        public static TypeEntry[] GetEntries(SerializedProperty property)
            => GetEntries(property, requireStableId: true);

        /// <summary>
        /// Selector entries for <paramref name="property"/>: instantiable reference types assignable to its
        /// base type, plus open generics closed with the field's arguments. With
        /// <paramref name="requireStableId"/> types that have no stable id are left out (StableRef fields);
        /// without it every candidate is offered (<see cref="RefSelectorAttribute"/> fields).
        /// </summary>
        public static TypeEntry[] GetEntries(SerializedProperty property, bool requireStableId)
        {
            var baseType = GetBaseType(property);

            if (!(baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
                && TryGetValueFieldType(property, out var reflected)
                && reflected != null && reflected.IsGenericType && !reflected.IsGenericTypeDefinition
                && (baseType == typeof(object) || baseType.IsAssignableFrom(reflected) || reflected.IsAssignableFrom(baseType)))
                baseType = reflected;

            if (_typeCache.TryGetValue((baseType, requireStableId), out var cached)) return cached;

            var result = new List<TypeEntry>();

            var query = baseType.IsGenericType && !baseType.IsGenericTypeDefinition
                ? baseType.GetGenericTypeDefinition()
                : baseType;

            foreach (var t in TypeCache.GetTypesDerivedFrom(query))
            {
                if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition || !baseType.IsAssignableFrom(t)) continue;
                if (!IsInstantiableReferenceValue(t)) continue;
                if (requireStableId && StableRefTypeRegistry.GetOrAssignId(t) == null) continue;

                result.Add(CreateEntry(t));
            }
            if (baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
            {
                foreach (var closed in StableRefGenericUtils.CollectClosedGenericCandidates(baseType))
                {
                    if (!IsInstantiableReferenceValue(closed)) continue;
                    if (requireStableId && StableRefTypeRegistry.GetOrAssignId(closed) == null) continue;

                    result.Add(CreateEntry(closed));
                }
            }

            result.Sort((a, b) =>
            {
                int byOrder = a.SortOrder.CompareTo(b.SortOrder);
                return byOrder != 0 ? byOrder : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            var arr = result.ToArray();
            _typeCache[(baseType, requireStableId)] = arr;
            return arr;
        }

        private static TypeEntry CreateEntry(Type t)
        {
            var meta = StableRefTypeMetadata.Get(t);
            string fullPath = string.IsNullOrEmpty(meta.Category) ? meta.DisplayName : $"{meta.Category}/{meta.DisplayName}";
            string fullPathLower = fullPath.ToLowerInvariant();
            string typeName = StableRefGenericUtils.DisplayName(t);
            return new TypeEntry
            {
                Type = t,
                Name = meta.DisplayName,
                FullPath = fullPath,
                FullPathLower = fullPathLower,
                SearchText = typeName == meta.DisplayName ? fullPathLower : fullPathLower + "\n" + typeName.ToLowerInvariant(),
                Category = meta.Category,
                Tooltip = meta.Tooltip,
                Color = meta.Color,
                SortOrder = meta.SortOrder ?? 0
            };
        }

        /// <summary>
        /// True when <paramref name="t"/> can actually live in a <c>[SerializeReference]</c> field:
        /// a non-UnityEngine.Object reference type with a parameterless constructor.
        /// </summary>
        private static bool IsInstantiableReferenceValue(Type t)
        {
            if (t.IsValueType) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
            return t.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, Type.EmptyTypes, modifiers: null) != null;
        }

        private static bool TryGetValueFieldType(SerializedProperty property, out Type type)
        {
            type = null;
            if (!TryResolvePath(property, out var r)) return false;
            type = r.ValueType;
            return type != null;
        }

        private static readonly Dictionary<Type, bool> _mayContainStableRef = new();

        /// <summary>
        /// Fast, cached answer to "can serialized data of this type possibly contain a StableRef entry?"
        /// Walks the declared serializable field graph. Conservative: unknown or too-deep shapes
        /// (including any <c>[SerializeReference]</c> field, whose runtime contents are open-ended)
        /// count as true, so a false result is a safe reason to skip scanning an object.
        /// </summary>
        public static bool MayContainStableRef(Type rootType)
        {
            if (rootType == null) return false;
            if (_mayContainStableRef.TryGetValue(rootType, out var cached)) return cached;

            bool result = MayContainStableRefRecursive(rootType, new HashSet<Type>(), depth: 0);
            _mayContainStableRef[rootType] = result;
            return result;
        }

        private const int MayContainMaxDepth = 8;

        private static bool MayContainStableRefRecursive(Type type, HashSet<Type> visited, int depth)
        {
            if (depth > MayContainMaxDepth) return true;
            if (!visited.Add(type)) return false;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsDefined(typeof(NonSerializedAttribute), inherit: false)) continue;

                    bool serializeReference = field.IsDefined(typeof(UnityEngine.SerializeReference), inherit: false);
                    bool serialized = field.IsPublic
                                      || serializeReference
                                      || field.IsDefined(typeof(SerializeField), inherit: false);
                    if (!serialized) continue;
                    if (serializeReference) return true;

                    var fieldType = field.FieldType;
                    if (fieldType.IsArray) fieldType = fieldType.GetElementType();
                    else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                        fieldType = fieldType.GetGenericArguments()[0];
                    if (fieldType == null) continue;

                    if (typeof(StableRefBase).IsAssignableFrom(fieldType)) return true;
                    if (typeof(StableRefListBase).IsAssignableFrom(fieldType)) return true;

                    if (fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string)) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType)) continue;
                    if (!fieldType.IsSerializable) continue;

                    if (MayContainStableRefRecursive(fieldType, visited, depth + 1)) return true;
                }
            }

            return false;
        }

        public static Type[] SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                int loaderCount = ex.LoaderExceptions?.Length ?? 0;
                Debug.LogWarning(
                    $"[StableRef] Couldn't fully load types from assembly '{a.GetName().Name}'. " +
                    $"Loader exceptions: {loaderCount}. Returning the partial set so the dropdown still works.");
                if (ex.Types == null) return Array.Empty<Type>();

                int kept = 0;
                for (int i = 0; i < ex.Types.Length; i++)
                    if (ex.Types[i] != null) kept++;
                if (kept == 0) return Array.Empty<Type>();

                var result = new Type[kept];
                int idx = 0;
                for (int i = 0; i < ex.Types.Length; i++)
                    if (ex.Types[i] != null) result[idx++] = ex.Types[i];
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[StableRef] Couldn't load types from assembly '{a.GetName().Name}': {ex.Message}");
                return Array.Empty<Type>();
            }
        }
        
        private static bool TryResolvePath(SerializedProperty property, out PathResolution result)
        {
            result = default;
            var target = property.serializedObject?.targetObject;
            if (target == null) return false;

            var rootType = target.GetType();
            var key = (rootType, property.propertyPath);
            if (_pathCache.TryGetValue(key, out var cached))
            {
                result = cached;
                return cached.Field != null;
            }

            var sharedKey = (rootType, WithoutIndices(property.propertyPath));
            if (_pathCache.TryGetValue(sharedKey, out cached))
            {
                _pathCache[key] = cached;
                result = cached;
                return cached.Field != null;
            }

            string path = property.propertyPath.Replace(".Array.data[", "[");
            string[] segs = path.Split('.');

            FieldInfo field = null;
            Type currType = rootType;
            Type rawType = null;

            foreach (var raw in segs)
            {
                int bracket = raw.IndexOf('[');
                string name = bracket >= 0 ? raw.Substring(0, bracket) : raw;
                bool indexed = bracket >= 0;

                field = null;
                var t = currType;
                while (t != null && field == null)
                {
                    field = t.GetField(name, FieldFlags);
                    t = t.BaseType;
                }
                
                if (field == null && (currType.IsInterface || currType.IsAbstract))
                {
                    var query = currType.IsGenericType ? currType.GetGenericTypeDefinition() : currType;
                    foreach (var concreteType in TypeCache.GetTypesDerivedFrom(query))
                    {
                        if (concreteType.IsInterface) continue;

                        Type candidate;
                        if (concreteType.IsGenericTypeDefinition)
                        {
                            if (!StableRefGenericUtils.TryClose(concreteType, currType, out candidate)) continue;
                        }
                        else
                        {
                            if (concreteType.IsAbstract) continue;
                            if (!currType.IsAssignableFrom(concreteType)) continue;
                            candidate = concreteType;
                        }

                        var s = candidate;
                        while (s != null && field == null)
                        {
                            field = s.GetField(name, FieldFlags);
                            s = s.BaseType;
                        }
                        if (field != null) break;
                    }
                }
                
                if (field == null)
                {
                    _pathCache[key] = _pathCache[sharedKey] = default;
                    return false;
                }

                rawType = field.FieldType;
                currType = rawType;

                if (indexed)
                {
                    if (currType.IsArray) currType = currType.GetElementType();
                    else if (currType.IsGenericType && currType.GetGenericTypeDefinition() == typeof(List<>))
                        currType = currType.GetGenericArguments()[0];
                }
            }

            result = new PathResolution
            {
                Field = field,
                ValueType = currType,
                RawFieldType = rawType,
                IsStableRefValue = field != null
                    && field.Name == StableRefEntry.ValueFieldName
                    && field.DeclaringType != null
                    && field.DeclaringType.IsGenericType
                    && field.DeclaringType.GetGenericTypeDefinition() == typeof(StableRef<>)
            };
            _pathCache[key] = _pathCache[sharedKey] = result;
            return field != null;
        }

        /// <summary>
        /// <paramref name="propertyPath"/> with the element indices removed (<c>a.Array.data[3].Value</c> →
        /// <c>a.Array.data[].Value</c>): the resolution doesn't depend on them, so all elements share one cache entry.
        /// </summary>
        private static string WithoutIndices(string propertyPath)
        {
            int open = propertyPath.IndexOf('[');
            if (open < 0) return propertyPath;

            var sb = new System.Text.StringBuilder(propertyPath.Length);
            bool inIndex = false;
            foreach (char c in propertyPath)
            {
                if (c == '[') inIndex = true;
                else if (c == ']') inIndex = false;
                else if (inIndex) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static Type ResolveBaseTypeFromTypename(string managedRefTypename, string propertyType)
        {
            if (!string.IsNullOrEmpty(managedRefTypename))
            {
                int spaceIdx = managedRefTypename.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    string asmName = managedRefTypename.Substring(0, spaceIdx);
                    string typeName = managedRefTypename.Substring(spaceIdx + 1);
                    var asms = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < asms.Length; i++)
                    {
                        if (asms[i].GetName().Name != asmName) continue;
                        var t = asms[i].GetType(typeName);
                        if (t != null) return t;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(propertyType) && propertyType.StartsWith(ManagedRefPrefix, StringComparison.Ordinal))
            {
                string typeName = propertyType.Substring(
                    ManagedRefPrefix.Length,
                    propertyType.Length - ManagedRefPrefix.Length - 1);

                Type first = null;
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < asms.Length; ai++)
                {
                    var types = SafeGetTypes(asms[ai]);
                    for (int ti = 0; ti < types.Length; ti++)
                    {
                        if (types[ti].Name != typeName) continue;
                        if (first == null)
                        {
                            first = types[ti];
                        }
                        else if (first != types[ti] && _ambiguousShortNames.Add(typeName))
                        {
                            Debug.LogWarning(
                                $"[StableRef] Several types share the short name '{typeName}' " +
                                $"('{first.FullName}', '{types[ti].FullName}', ...). Using '{first.FullName}' " +
                                "as the field's base type — results may be wrong for the others.");
                        }
                    }
                }
                if (first != null) return first;
            }

            return typeof(object);
        }

        private static readonly HashSet<string> _ambiguousShortNames = new();
    }
}
#endif