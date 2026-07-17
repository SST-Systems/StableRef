#if UNITY_EDITOR
namespace SST.StableRef
{
    /// <summary>
    /// Stamps stable ids onto StableRef entries built from code. The inspector does this automatically
    /// when a field is drawn; call these after populating a list in an editor script (before saving) so
    /// code-authored entries survive type renames even if the object is never inspected.
    /// </summary>
    public static class StableRefSync
    {
        /// <summary>Stamps TypeId / TypeDisplayName onto one entry from its current value.</summary>
        public static void AssignId<T>(StableRef<T> entry) where T : class
        {
            var value = entry?.Value;
            if (value == null) return;

            var id = StableRefTypeRegistry.GetOrAssignId(value.GetType());
            if (id == null) return;

            entry.TypeId = id;
            entry.TypeDisplayName = StableRefGenericUtils.DisplayName(value.GetType());
        }

        /// <summary>Stamps ids onto every entry of <paramref name="list"/>.</summary>
        public static void AssignIds<T>(StableRefList<T> list) where T : class
        {
            if (list == null) return;

            var items = list.Items;
            for (int i = 0; i < items.Count; i++)
                AssignId(items[i]);
        }
    }
}
#endif
