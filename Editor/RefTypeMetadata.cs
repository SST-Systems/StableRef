#if UNITY_EDITOR
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// How a type is presented in the StableRef type selector. Returned by <see cref="IRefTypeMetadataProvider"/>;
    /// leave a field empty to keep the value of a later provider or the built-in one.
    /// </summary>
    public struct RefTypeMetadata
    {
        /// <summary>Name shown in the selector and on the field's button.</summary>
        public string DisplayName;

        /// <summary>Submenu path in the selector; <c>'/'</c> separates nested levels.</summary>
        public string Category;

        /// <summary>Tooltip of the type's row in the selector.</summary>
        public string Tooltip;

        /// <summary>Accent color of the type's row in the selector.</summary>
        public Color? Color;

        /// <summary>
        /// Position among the types of the same category: lower values come first, types with equal values are
        /// sorted by name. Unset counts as <c>0</c>.
        /// </summary>
        public int? SortOrder;
    }
}
#endif
