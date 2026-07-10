#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace SST.StableRef
{
    public static class StableRefTypeRegistry
    {
        private static readonly Dictionary<string, Type> _idToType = new();
        private static readonly Dictionary<Type, string> _typeToId = new();
        
        private static readonly HashSet<string> _missingIds = new();

        public static string GetOrAssignId(Type type)
        {
            if (type == null) return null;

            if (_typeToId.TryGetValue(type, out var cached)) return cached;
            
            var attr = (StableTypeIdAttribute)Attribute.GetCustomAttribute(type, typeof(StableTypeIdAttribute));

            if (attr != null)
            {
                Register(attr.Id, type);
                return attr.Id;
            }

            var guids = AssetDatabase.FindAssets($"t:MonoScript {type.Name}");

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

            return null;
        }

        public static Type GetType(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (_idToType.TryGetValue(id, out var cached)) return cached;
            
            if (_missingIds.Contains(id)) return null;
            
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

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    var attr = (StableTypeIdAttribute)Attribute.GetCustomAttribute(t, typeof(StableTypeIdAttribute));
                    if (attr != null && attr.Id == id) { Register(id, t); return t; }
                }
            }
            
            _missingIds.Add(id);
            return null;
        }

        private static void Register(string id, Type type)
        {
            _idToType[id] = type;
            _typeToId[type] = id;
        }
    }
}
#endif