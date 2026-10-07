#if UNITY_EDITOR
using System;

namespace SST.StableRef
{
    /// <summary>
    /// Supplies how a type is presented in the StableRef type selector — name, category, tooltip, color, sort
    /// order — from another source than <see cref="RefCategoryAttribute"/>, e.g. a third-party attribute already
    /// used in the project.
    /// </summary>
    /// <remarks>
    /// Implementations are found through <c>TypeCache</c> and created once per domain reload, so the class needs a
    /// parameterless constructor and no registration. Providers are asked in ascending <see cref="Order"/>; for each
    /// field of <see cref="RefTypeMetadata"/> the first non-empty value wins, and fields no provider fills fall back
    /// to the built-in ones (the type's display name, <see cref="RefCategoryAttribute"/>). Results are cached per type.
    /// Presentation only — ids, resolution and serialized data are unaffected.
    /// </remarks>
    public interface IRefTypeMetadataProvider
    {
        /// <summary>Lower values are asked first.</summary>
        int Order { get; }

        /// <summary>Metadata for <paramref name="type"/>; return <see langword="false"/> to leave it to others.</summary>
        bool TryGetMetadata(Type type, out RefTypeMetadata metadata);
    }
}
#endif
