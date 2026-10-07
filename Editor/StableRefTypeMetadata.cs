#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Resolved selector metadata per type: the <see cref="IRefTypeMetadataProvider"/> implementations in
    /// <see cref="IRefTypeMetadataProvider.Order"/>, then the built-in display name and <see cref="RefCategoryAttribute"/>.
    /// Cached per type for the domain's lifetime.
    /// </summary>
    internal static class StableRefTypeMetadata
    {
        private static readonly Dictionary<Type, RefTypeMetadata> _cache = new();
        private static List<IRefTypeMetadataProvider> _providers;

        internal static RefTypeMetadata Get(Type type)
        {
            if (_cache.TryGetValue(type, out var cached)) return cached;

            var result = default(RefTypeMetadata);
            foreach (var provider in Providers)
            {
                RefTypeMetadata m;
                try
                {
                    if (!provider.TryGetMetadata(type, out m)) continue;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StableRef] {provider.GetType().FullName} failed for '{type.FullName}': {e.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(result.DisplayName)) result.DisplayName = m.DisplayName;
                if (string.IsNullOrEmpty(result.Category)) result.Category = m.Category;
                if (string.IsNullOrEmpty(result.Tooltip)) result.Tooltip = m.Tooltip;
                if (!result.Color.HasValue) result.Color = m.Color;
                if (!result.SortOrder.HasValue) result.SortOrder = m.SortOrder;
            }

            if (string.IsNullOrEmpty(result.DisplayName)) result.DisplayName = StableRefGenericUtils.DisplayName(type);
            if (string.IsNullOrEmpty(result.Category)) result.Category = type.GetCustomAttribute<RefCategoryAttribute>()?.Category ?? "";
            if (result.Category.Length > 0) result.Category = result.Category.Trim('/');

            return _cache[type] = result;
        }

        private static List<IRefTypeMetadataProvider> Providers => _providers ??= CreateProviders();

        private static List<IRefTypeMetadataProvider> CreateProviders()
        {
            var result = new List<IRefTypeMetadataProvider>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<IRefTypeMetadataProvider>())
            {
                if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
                try
                {
                    result.Add((IRefTypeMetadataProvider)Activator.CreateInstance(t, nonPublic: true));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StableRef] Couldn't create metadata provider '{t.FullName}': {e.Message}");
                }
            }

            result.Sort((a, b) => a.Order.CompareTo(b.Order));
            return result;
        }
    }
}
#endif
