using System;
using System.Collections.Generic;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Non-generic base for <see cref="StableRef{T}"/> that holds the serialized data
    /// shared by every reference regardless of its value type.
    /// </summary>
    /// <remarks>
    /// This type exists so the editor tooling and custom property drawers can operate on any
    /// <see cref="StableRef{T}"/> through a common, non-generic surface. It is not meant to be
    /// used directly in your own code — declare fields as <see cref="StableRef{T}"/> instead.
    /// The public fields are populated by Unity's serializer and by the StableRef property drawer;
    /// treat them as serialized state rather than an API you write to by hand.
    /// </remarks>
    [Serializable]
    public abstract class StableRefBase
    {
        /// <summary>
        /// Stable identifier of the concrete value type. Comes from <see cref="StableTypeIdAttribute"/>
        /// when present, otherwise from the value type's MonoScript GUID. This is what survives a
        /// class rename and lets the reference be resolved back to the correct type.
        /// </summary>
        [SerializeField] public string TypeId;

        /// <summary>
        /// Human-readable name of the value type, cached for display in the inspector and editor
        /// tools. Purely cosmetic — resolution always relies on <see cref="TypeId"/>.
        /// </summary>
        [SerializeField] public string TypeDisplayName;

        /// <summary>
        /// Flattened <see cref="UnityEngine.Object"/> references contained in the value, extracted so
        /// they survive serialization independently of the managed reference. Correlated with
        /// <see cref="ObjectRefPaths"/> by index.
        /// </summary>
        [SerializeField] public List<UnityEngine.Object> ObjectRefs = new();

        /// <summary>
        /// Property paths (relative to the value) of each entry in <see cref="ObjectRefs"/>, used to
        /// re-bind those object references back onto the value after deserialization or a copy/paste.
        /// </summary>
        [SerializeField] public List<string> ObjectRefPaths = new();

        /// <summary>
        /// Serialized snapshot of the value's plain (non-<see cref="UnityEngine.Object"/>) data, used
        /// by the copy/paste tooling to reconstruct the value in a different serialized document.
        /// </summary>
        [SerializeField] public string ValuesData;
    }

    /// <summary>
    /// Serializable wrapper around a single polymorphic <c>[SerializeReference]</c> value of type
    /// <typeparamref name="T"/> that keeps working after the concrete type is renamed or moved.
    /// </summary>
    /// <typeparam name="T">
    /// Base type (usually an interface or abstract class) of the value the field can hold.
    /// Concrete implementations should carry a <see cref="StableTypeIdAttribute"/> so their identity
    /// is decoupled from the class name.
    /// </typeparam>
    /// <remarks>
    /// Unity's built-in <c>[SerializeReference]</c> stores the assembly-qualified type name, so renaming
    /// a class nulls the field. <see cref="StableRef{T}"/> stores a stable <see cref="StableRefBase.TypeId"/>
    /// alongside the value and resolves the reference through it, so the data survives refactors.
    /// Declare it directly as a serialized field, e.g. <c>public StableRef&lt;IEffect&gt; OnPickup;</c>,
    /// and read the value through <see cref="Value"/>.
    /// </remarks>
    [Serializable]
    public sealed class StableRef<T> : StableRefBase where T : class
    {
        /// <summary>
        /// The wrapped polymorphic value. May be <see langword="null"/> if nothing is assigned or if the
        /// stored type can no longer be resolved (for example after a script file was deleted); check for
        /// <see langword="null"/> before use. Unresolved entries are surfaced by the Fix Missing Types tool.
        /// </summary>
        [SerializeReference] public T Value;
    }
}