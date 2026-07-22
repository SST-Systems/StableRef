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
            public string Category;
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
        private static readonly Dictionary<Type, TypeEntry[]> _typeCache = new();

        public static bool IsStableRefValueField(SerializedProperty property)
        {
            return TryResolvePath(property, out var r) && r.IsStableRefValue;
        }

        public static bool IsStableRefList(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Generic) return false;
            if (!TryResolvePath(property, out var r)) return false;
            return r.ValueType != null && typeof(StableRefListBase).IsAssignableFrom(r.ValueType);
        }

        public static SerializedProperty GetStableRefListItems(SerializedProperty property)
            => property.FindPropertyRelative("_items");

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
                var refProp = isStable ? first.FindPropertyRelative("Value") : first;
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
        {
            var baseType = GetBaseType(property);

            if (!(baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
                && TryGetValueFieldType(property, out var reflected)
                && reflected != null && reflected.IsGenericType && !reflected.IsGenericTypeDefinition
                && (baseType == typeof(object) || baseType.IsAssignableFrom(reflected) || reflected.IsAssignableFrom(baseType)))
                baseType = reflected;

            if (_typeCache.TryGetValue(baseType, out var cached)) return cached;

            var result = new List<TypeEntry>();

            var query = baseType.IsGenericType && !baseType.IsGenericTypeDefinition
                ? baseType.GetGenericTypeDefinition()
                : baseType;

            foreach (var t in TypeCache.GetTypesDerivedFrom(query))
            {
                if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition || !baseType.IsAssignableFrom(t)) continue;
                if (StableRefTypeRegistry.GetOrAssignId(t) == null) continue;

                var cat = t.GetCustomAttribute<StableRefCategoryAttribute>();
                string catS = cat?.Category ?? "";
                string fullPath = string.IsNullOrEmpty(catS) ? t.Name : $"{catS}/{t.Name}";

                result.Add(new TypeEntry
                {
                    Type = t,
                    Name = t.Name,
                    FullPath = fullPath,
                    FullPathLower = fullPath.ToLowerInvariant(),
                    Category = catS
                });
            }
            if (baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
            {
                foreach (var closed in StableRefGenericUtils.CollectClosedGenericCandidates(baseType))
                {
                    if (StableRefTypeRegistry.GetOrAssignId(closed) == null) continue;

                    var gcat = closed.GetCustomAttribute<StableRefCategoryAttribute>();
                    string gcatS = gcat?.Category ?? "";
                    string gname = StableRefGenericUtils.DisplayName(closed);
                    string gfullPath = string.IsNullOrEmpty(gcatS) ? gname : $"{gcatS}/{gname}";

                    result.Add(new TypeEntry
                    {
                        Type = closed,
                        Name = gname,
                        FullPath = gfullPath,
                        FullPathLower = gfullPath.ToLowerInvariant(),
                        Category = gcatS
                    });
                }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            var arr = result.ToArray();
            _typeCache[baseType] = arr;
            return arr;
        }

        private static bool TryGetValueFieldType(SerializedProperty property, out Type type)
        {
            type = null;
            if (!TryResolvePath(property, out var r)) return false;
            type = r.ValueType;
            return type != null;
        }

        public static Type[] SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                int loaderCount = ex.LoaderExceptions?.Length ?? 0;
                Debug.LogWarning(
                    $"[StableRefSelector] Couldn't fully load types from assembly '{a.GetName().Name}'. " +
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
                    $"[StableRefSelector] Couldn't load types from assembly '{a.GetName().Name}': {ex.Message}");
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
                    _pathCache[key] = default;
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
                    && field.Name == "Value"
                    && field.DeclaringType != null
                    && field.DeclaringType.IsGenericType
                    && field.DeclaringType.GetGenericTypeDefinition() == typeof(StableRef<>)
            };
            _pathCache[key] = result;
            return field != null;
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

                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < asms.Length; ai++)
                {
                    var types = SafeGetTypes(asms[ai]);
                    for (int ti = 0; ti < types.Length; ti++)
                        if (types[ti].Name == typeName) return types[ti];
                }
            }

            return typeof(object);
        }
    }
}
#endif