using System;

namespace SST.StableRef
{
    /// <summary>
    /// Assigns a permanent, stable identifier to a type you can't put <see cref="RefTypeIdAttribute"/> on — a type
    /// from another package, a precompiled DLL or generated code. Declared once per type at assembly level, in any
    /// assembly of your project: <c>[assembly: RefTypeIdFor(typeof(ThirdParty.Effect), "my-game.third-party.effect")]</c>.
    /// </summary>
    /// <remarks>
    /// Resolution order of a type's id: <see cref="RefTypeIdAttribute"/> on the type itself, then this attribute,
    /// then the MonoScript GUID. The same rules as for <see cref="RefTypeIdAttribute"/> apply: the id must be unique
    /// across the project and never change once in use. A clash — two types under one id, or one type mapped to two
    /// ids — is logged as an error, and the type keeps the id found first; an id already taken by a
    /// <see cref="RefTypeIdAttribute"/> stays with that type and the mapping is ignored. Read only by the editor; the
    /// attribute is stripped from player builds.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class RefTypeIdForAttribute : Attribute
    {
        /// <summary>The type the id is assigned to.</summary>
        public Type Type { get; }

        /// <summary>The stable identifier stored in serialized data and used to resolve the type.</summary>
        public string Id { get; }

        /// <summary>Declares the stable identifier of <paramref name="type"/>.</summary>
        /// <param name="type">The type to identify; closed generic types are not supported (ids of their
        /// definition and arguments are combined automatically).</param>
        /// <param name="id">A project-unique, namespaced string that must never change once in use.</param>
        public RefTypeIdForAttribute(Type type, string id)
        {
            Type = type;
            Id = id;
        }
    }
}
