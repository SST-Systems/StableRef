using System;

namespace SST.StableRef
{
    /// <summary>
    /// Assigns a permanent, stable identifier to a type used as a <see cref="StableRef{T}"/> value.
    /// The class can then be renamed or moved freely without breaking existing serialized references.
    /// </summary>
    /// <remarks>
    /// Optional: if omitted, StableRef falls back to the type's MonoScript GUID as the identifier. Provide
    /// an explicit id for types you expect to refactor heavily, since it survives even if the script file
    /// is deleted and re-created. The id must be unique across the project — use a namespaced string
    /// (e.g. <c>"my-package.damage-on-hit"</c>) to avoid collisions.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public sealed class StableTypeIdAttribute : Attribute
    {
        /// <summary>The stable identifier stored in serialized data and used to resolve the type.</summary>
        public string Id { get; }

        /// <summary>Declares the type's stable identifier.</summary>
        /// <param name="id">A project-unique, namespaced string that must never change once in use.</param>
        public StableTypeIdAttribute(string id) => Id = id;
    }
}