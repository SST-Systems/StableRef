#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Maps value types to their stable ids and back. Id priority: <see cref="RefTypeIdAttribute"/> on the type,
    /// then an assembly-level <see cref="RefTypeIdForAttribute"/>, then the MonoScript GUID (the script file must
    /// declare exactly that class). Closed generic types get a composite id built from their definition and
    /// arguments. Lookups are cached, misses included.
    /// </summary>
    public static class StableRefTypeRegistry
    {
        private static readonly Dictionary<string, Type> _idToType = new();
        private static readonly Dictionary<Type, string> _typeToId = new();

        private static readonly HashSet<string> _missingIds = new();
        private static readonly HashSet<Type> _missingTypes = new();

        public static string GetOrAssignId(Type type)
        {
            if (type == null) return null;

            if (_typeToId.TryGetValue(type, out var cached)) return cached;

            if (_missingTypes.Contains(type)) return null;

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var defId = GetOrAssignId(type.GetGenericTypeDefinition());
                if (defId == null) { _missingTypes.Add(type); return null; }

                var args = type.GetGenericArguments();
                var argIds = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    argIds[i] = GetOrAssignId(args[i]);
                    if (argIds[i] == null) { _missingTypes.Add(type); return null; }
                }

                var composite = BuildGenericId(defId, argIds);
                Register(composite, type);
                return composite;
            }

            var attr = GetOwnIdAttribute(type);

            if (attr != null)
            {
                Register(attr.Id, type);
                return attr.Id;
            }

            if (AssemblyIds.TypeToId.TryGetValue(type, out var mapped))
            {
                Register(mapped, type);
                return mapped;
            }

            var searchName = type.Name;
            int tick = searchName.IndexOf('`');
            if (tick >= 0) searchName = searchName.Substring(0, tick);

            var guids = AssetDatabase.FindAssets($"t:MonoScript {searchName}");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null && script.GetClass() == type)
                {
                    Register(guid, type);
                    return guid;
                }
            }

            _missingTypes.Add(type);
            Debug.LogWarning(
                $"[StableRef] '{type.FullName}' has no stable ID: add [RefTypeId], move it to its own file, or map it " +
                "with [assembly: RefTypeIdFor(...)].");
            return null;
        }

        public static Type GetType(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (_idToType.TryGetValue(id, out var cached)) return cached;

            if (_missingIds.Contains(id)) return null;

            if (TryParseGenericId(id, out var defId, out var argIds))
            {
                var def = GetType(defId);
                if (def == null || !def.IsGenericTypeDefinition
                    || def.GetGenericArguments().Length != argIds.Length)
                { _missingIds.Add(id); return null; }

                var typeArgs = new Type[argIds.Length];
                for (int i = 0; i < argIds.Length; i++)
                {
                    typeArgs[i] = GetType(argIds[i]);
                    if (typeArgs[i] == null) { _missingIds.Add(id); return null; }
                }

                try
                {
                    var closed = def.MakeGenericType(typeArgs);
                    Register(id, closed);
                    return closed;
                }
                catch { _missingIds.Add(id); return null; }
            }

            var path = LooksLikeGuid(id) ? AssetDatabase.GUIDToAssetPath(id) : null;

            if (!string.IsNullOrEmpty(path))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null)
                {
                    var t = script.GetClass();
                    if (t != null) { Register(id, t); return t; }
                }
            }

            foreach (var t in TypeCache.GetTypesWithAttribute<RefTypeIdAttribute>())
            {
                var attr = GetOwnIdAttribute(t);
                if (attr != null && attr.Id == id) { Register(id, t); return t; }
            }

            if (AssemblyIds.IdToType.TryGetValue(id, out var mappedType))
            {
                Register(id, mappedType);
                return mappedType;
            }

            _missingIds.Add(id);
            return null;
        }

        /// <summary>
        /// The <see cref="RefTypeIdAttribute"/> declared on <paramref name="type"/> itself — never one of a base class,
        /// or every derived class would share its base's id.
        /// </summary>
        private static RefTypeIdAttribute GetOwnIdAttribute(Type type)
            => (RefTypeIdAttribute)Attribute.GetCustomAttribute(type, typeof(RefTypeIdAttribute), inherit: false);

        private static void Register(string id, Type type)
        {
            _idToType[id] = type;

            string canonical = CanonicalId(type, id);
            if (canonical != id) _idToType[canonical] = type;
            _typeToId[type] = canonical;

            _missingTypes.Remove(type);
            _missingIds.Remove(id);
        }

        private static string CanonicalId(Type type, string fallback)
        {
            if (type.IsGenericType && !type.IsGenericTypeDefinition) return fallback;

            var attr = GetOwnIdAttribute(type);
            if (!string.IsNullOrEmpty(attr?.Id)) return attr.Id;
            return AssemblyIds.TypeToId.TryGetValue(type, out var mapped) ? mapped : fallback;
        }

        private sealed class AssemblyIdMap
        {
            public readonly Dictionary<Type, string> TypeToId = new();
            public readonly Dictionary<string, Type> IdToType = new();
        }

        private static AssemblyIdMap _assemblyIds;
        private static AssemblyIdMap AssemblyIds => _assemblyIds ??= BuildAssemblyIdMap();

        private static AssemblyIdMap BuildAssemblyIdMap()
        {
            var map = new AssemblyIdMap();
            var attributeIds = new Dictionary<string, Type>();
            foreach (var type in TypeCache.GetTypesWithAttribute<RefTypeIdAttribute>())
            {
                var attr = GetOwnIdAttribute(type);
                if (!string.IsNullOrEmpty(attr?.Id) && !attributeIds.ContainsKey(attr.Id)) attributeIds[attr.Id] = type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                object[] attrs;
                try { attrs = assembly.GetCustomAttributes(typeof(RefTypeIdForAttribute), inherit: false); }
                catch (Exception) { continue; }

                foreach (RefTypeIdForAttribute attr in attrs)
                {
                    string where = $"[assembly: RefTypeIdFor] in '{assembly.GetName().Name}'";

                    if (attr.Type == null || string.IsNullOrEmpty(attr.Id))
                    {
                        Debug.LogError($"[StableRef] {where} needs a type and a non-empty id.");
                        continue;
                    }

                    if (attr.Type.IsGenericType && !attr.Type.IsGenericTypeDefinition)
                    {
                        Debug.LogError(
                            $"[StableRef] {where} maps the closed generic type '{attr.Type.FullName}'. Map its definition " +
                            "and arguments instead — the closed type's id is combined from theirs.");
                        continue;
                    }

                    var own = GetOwnIdAttribute(attr.Type);
                    if (own != null)
                    {
                        Debug.LogError(
                            $"[StableRef] {where} maps '{attr.Type.FullName}' to \"{attr.Id}\", but the type already has " +
                            $"[RefTypeId(\"{own.Id}\")], which wins. Remove the mapping.");
                        continue;
                    }

                    if (map.TypeToId.TryGetValue(attr.Type, out var existingId))
                    {
                        if (existingId != attr.Id)
                            Debug.LogError(
                                $"[StableRef] '{attr.Type.FullName}' is mapped to both \"{existingId}\" and \"{attr.Id}\" by " +
                                $"[assembly: RefTypeIdFor]. Keeping \"{existingId}\".");
                        continue;
                    }

                    if (attributeIds.TryGetValue(attr.Id, out var attributeOwner))
                    {
                        Debug.LogError(
                            $"[StableRef] Duplicate id \"{attr.Id}\": [RefTypeId] on '{attributeOwner.FullName}' and " +
                            $"{where} for '{attr.Type.FullName}'. IDs must be unique; the [RefTypeId] wins.");
                        continue;
                    }

                    if (map.IdToType.TryGetValue(attr.Id, out var existingType))
                    {
                        Debug.LogError(
                            $"[StableRef] Duplicate id \"{attr.Id}\" in [assembly: RefTypeIdFor] for '{existingType.FullName}' " +
                            $"and '{attr.Type.FullName}'. IDs must be unique.");
                        continue;
                    }

                    map.TypeToId[attr.Type] = attr.Id;
                    map.IdToType[attr.Id] = attr.Type;
                }
            }

            return map;
        }

        [InitializeOnLoadMethod]
        private static void WarnDuplicateStableIds()
        {
            var byId = new Dictionary<string, Type>();
            foreach (var type in TypeCache.GetTypesWithAttribute<RefTypeIdAttribute>())
            {
                var attr = GetOwnIdAttribute(type);
                if (attr == null || string.IsNullOrEmpty(attr.Id)) continue;

                if (byId.TryGetValue(attr.Id, out var first))
                {
                    Debug.LogError(
                        $"[StableRef] Duplicate [RefTypeId(\"{attr.Id}\")] on '{first.FullName}' and '{type.FullName}'. IDs must be unique.");
                }
                else
                {
                    byId[attr.Id] = type;
                }
            }

            _ = AssemblyIds; // builds the map now, so mapping errors are logged on load
        }

        private static bool LooksLikeGuid(string id)
        {
            if (id.Length != 32) return false;
            foreach (char c in id)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                    return false;
            return true;
        }

        private const char GenOpen = '<';
        private const char GenClose = '>';
        private const char GenSep = ',';

        private static string BuildGenericId(string defId, string[] argIds)
            => defId + GenOpen + string.Join(GenSep.ToString(), argIds) + GenClose;

        private static bool TryParseGenericId(string id, out string defId, out string[] argIds)
        {
            defId = null;
            argIds = null;

            int lt = id.IndexOf(GenOpen);
            if (lt <= 0 || id[id.Length - 1] != GenClose) return false;

            defId = id.Substring(0, lt);
            string inner = id.Substring(lt + 1, id.Length - lt - 2);
            argIds = SplitTopLevel(inner);
            return argIds.Length > 0;
        }

        private static string[] SplitTopLevel(string s)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == GenOpen) depth++;
                else if (c == GenClose) depth--;
                else if (c == GenSep && depth == 0)
                {
                    result.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(s.Substring(start));
            return result.ToArray();
        }
    }
}
#endif