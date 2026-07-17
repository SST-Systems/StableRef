#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
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

            var attr = (StableTypeIdAttribute)Attribute.GetCustomAttribute(type, typeof(StableTypeIdAttribute));

            if (attr != null)
            {
                Register(attr.Id, type);
                return attr.Id;
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
            Debug.LogWarning($"[StableRef] '{type.Name}' needs its own file to get a stable ID (or add [StableTypeId]).");
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

            var path = AssetDatabase.GUIDToAssetPath(id);

            if (!string.IsNullOrEmpty(path))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null)
                {
                    var t = script.GetClass();
                    if (t != null) { Register(id, t); return t; }
                }
            }

            foreach (var t in TypeCache.GetTypesWithAttribute<StableTypeIdAttribute>())
            {
                var attr = (StableTypeIdAttribute)Attribute.GetCustomAttribute(t, typeof(StableTypeIdAttribute));
                if (attr != null && attr.Id == id) { Register(id, t); return t; }
            }

            _missingIds.Add(id);
            return null;
        }

        private static void Register(string id, Type type)
        {
            _idToType[id] = type;
            _typeToId[type] = id;
            _missingTypes.Remove(type);
            _missingIds.Remove(id);
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