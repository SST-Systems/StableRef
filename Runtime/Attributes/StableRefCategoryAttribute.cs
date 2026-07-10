using System;

namespace SST.StableRef
{
    /// <summary>
    /// Groups a type under a submenu in the inspector's StableRef type selector, so long type lists
    /// stay organized. Purely a UI hint — it has no effect on serialization or type resolution.
    /// </summary>
    /// <remarks>
    /// Use <c>'/'</c> in the category to create nested submenus, e.g. <c>[StableRefCategory("Combat/Damage")]</c>.
    /// Types without this attribute appear at the top level of the selector.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class StableRefCategoryAttribute : Attribute
    {
        /// <summary>Submenu path shown in the selector; <c>'/'</c> separates nested levels.</summary>
        public string Category { get; }

        /// <summary>Declares the selector submenu the type is listed under.</summary>
        /// <param name="category">Submenu path, e.g. <c>"Combat"</c> or <c>"Combat/Damage"</c>.</param>
        public StableRefCategoryAttribute(string category) => Category = category;
    }
}