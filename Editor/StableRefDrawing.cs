#if UNITY_EDITOR
namespace SST.StableRef
{
    /// <summary>
    /// Extension points of the StableRef inspector for other inspector frameworks.
    /// </summary>
    /// <remarks>
    /// Type names, categories, tooltips and colors in the selector come from
    /// <see cref="IRefTypeMetadataProvider"/> implementations, which are found automatically.
    /// </remarks>
    public static class StableRefDrawing
    {
        /// <summary>
        /// Draws the children of values in every StableRef selector field (<see cref="StableRef{T}"/>,
        /// <see cref="StableRefList{T}"/> elements, <see cref="RefSelectorAttribute"/> fields);
        /// <see langword="null"/> (the default) draws them with <c>EditorGUI.PropertyField</c>. Set it from an
        /// <c>[InitializeOnLoad]</c> type or an <c>[InitializeOnLoadMethod]</c>; there is one drawer, the last
        /// assignment wins.
        /// </summary>
        public static IStableRefChildrenDrawer ChildrenDrawer
        {
            get => _childrenDrawer;
            set
            {
                _childrenDrawer = value;
                InvalidateLayout();
            }
        }

        private static IStableRefChildrenDrawer _childrenDrawer;

        /// <summary>
        /// Makes StableRef lists measure their elements again on the next repaint. A list caches its height and
        /// re-measures only when StableRef itself changes the layout (its own foldouts, adding or removing elements);
        /// call this when an <see cref="IStableRefChildrenDrawer"/> changes the height of what it draws — its own
        /// foldouts, tabs, conditionally shown fields — or the list elements overlap until the selection changes.
        /// </summary>
        public static void InvalidateLayout() => StableRefListDrawer.InvalidateCache();
    }
}
#endif
