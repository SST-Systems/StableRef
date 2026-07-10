#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    internal static class StableRefClipboard
    {
        public static string Json { get; private set; }
        public static Type ValueType { get; private set; }

        public static bool HasValue => !string.IsNullOrEmpty(Json) && ValueType != null;

        public static Dictionary<string, int> ValueObjectRefs { get; private set; }

        public static void StoreValue(object value)
        {
            if (value == null)
            {
                Json = null; 
                ValueType = null;
                ValueObjectRefs = null; 
                return;
            }
            
            Json = Serialize(value);
            ValueType = value.GetType();
            ValueObjectRefs = null;
        }

        public static void StoreValueObjectRefs(Dictionary<string, int> refs)
            => ValueObjectRefs = refs;

        public sealed class ListClipboardData
        {
            public readonly List<string> Entries = new();
            public readonly List<Dictionary<string, int>> ObjectRefs = new();
            public Type ElementBaseType;
        }

        public static ListClipboardData List { get; private set; }

        public static void StoreList(ListClipboardData list) => List = list;

        private sealed class Wrapper : ScriptableObject
        {
            [SerializeReference] public object Value;
        }

        private static Wrapper _serializeWrapper;
        private static Wrapper _deserializeWrapper;

        private static Wrapper GetSerializeWrapper()
        {
            if (_serializeWrapper != null) return _serializeWrapper;
            _serializeWrapper = ScriptableObject.CreateInstance<Wrapper>();
            _serializeWrapper.hideFlags = HideFlags.HideAndDontSave;
            return _serializeWrapper;
        }

        private static Wrapper GetDeserializeWrapper()
        {
            if (_deserializeWrapper != null)
                UnityEngine.Object.DestroyImmediate(_deserializeWrapper);
            _deserializeWrapper = ScriptableObject.CreateInstance<Wrapper>();
            _deserializeWrapper.hideFlags = HideFlags.HideAndDontSave;
            return _deserializeWrapper;
        }

        public static string Serialize(object value)
        {
            if (value == null) return null;
            var w = GetSerializeWrapper();
            w.Value = value;
            try { return EditorJsonUtility.ToJson(w); }
            finally { w.Value = null; }
        }

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var w = GetDeserializeWrapper();
            EditorJsonUtility.FromJsonOverwrite(json, w);
            var result = w.Value;
            w.Value = null;
            return result;
        }
    }
}
#endif