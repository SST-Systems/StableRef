using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace SST.StableRef
{
    /// <summary>
    /// Non-generic base for <see cref="StableRef{T}"/> that holds the serialized data
    /// shared by every reference regardless of its value type.
    /// </summary>
    /// <remarks>
    /// Declare fields as <see cref="StableRef{T}"/>; this type is the common surface for the editor tooling and
    /// for code that only knows the field's <see cref="Type"/> at run time — reflection-based config loaders,
    /// serializers. <see cref="BoxedValue"/> and <see cref="ValueBaseType"/> read and write the value without the
    /// generic argument. The public fields are populated by Unity's serializer and by the StableRef property drawer;
    /// treat them as serialized state rather than an API you write to by hand.
    /// <para>Only <see cref="TypeId"/> (and the value) reach player builds. The display name and the snapshot
    /// (<c>TypeDisplayName</c>, <c>ObjectRefs</c>, <c>ObjectRefPaths</c>, <c>ValuesData</c>) are editor-only fields:
    /// they stay in the assets and serve recovery, copy/paste and the editor tools, but Unity leaves them out of
    /// builds, so they cost nothing at run time. Code that touches them must be inside <c>#if UNITY_EDITOR</c>.</para>
    /// </remarks>
    [Serializable]
    public abstract class StableRefBase
    {
        /// <summary>
        /// Stable identifier of the concrete value type. Comes from <see cref="RefTypeIdAttribute"/>
        /// when present, otherwise from the value type's MonoScript GUID. This is what survives a
        /// class rename and lets the reference be resolved back to the correct type.
        /// </summary>
        [SerializeField] public string TypeId;

#if UNITY_EDITOR
        /// <summary>
        /// Human-readable name of the value type, cached for display in the inspector and editor
        /// tools. Purely cosmetic — resolution always relies on <see cref="TypeId"/>. Editor-only.
        /// </summary>
        [SerializeField] public string TypeDisplayName;

        /// <summary>
        /// Flattened <see cref="UnityEngine.Object"/> references contained in the value, extracted so
        /// they survive serialization independently of the managed reference. Correlated with
        /// <see cref="ObjectRefPaths"/> by index. Editor-only.
        /// </summary>
        [SerializeField] public List<UnityEngine.Object> ObjectRefs = new();

        /// <summary>
        /// Property paths (relative to the value) of each entry in <see cref="ObjectRefs"/>, used to
        /// re-bind those object references back onto the value after deserialization or a copy/paste. Editor-only.
        /// </summary>
        [SerializeField] public List<string> ObjectRefPaths = new();

        /// <summary>
        /// Serialized snapshot of the value's plain (non-<see cref="UnityEngine.Object"/>) data, used
        /// by recovery and the copy/paste tooling to reconstruct the value. Editor-only.
        /// </summary>
        [SerializeField] public string ValuesData;
#endif

        /// <summary>The declared value type — <c>T</c> of the <see cref="StableRef{T}"/>.</summary>
        public abstract Type ValueBaseType { get; }

        /// <summary>
        /// The value as <see cref="object"/>, for code without the generic argument. The setter checks the type
        /// (throws <see cref="ArgumentException"/> when the value is not assignable to <see cref="ValueBaseType"/>)
        /// and keeps the metadata consistent like <see cref="StableRef{T}.Set"/>.
        /// </summary>
        public abstract object BoxedValue { get; set; }

        /// <summary>True when a value is assigned.</summary>
        public bool HasValue => BoxedValue != null;

        /// <summary>Clears the stable id, display name and snapshot — they described a value that is gone.</summary>
        private protected void ResetMetadata()
        {
            TypeId = string.Empty;
#if UNITY_EDITOR
            TypeDisplayName = string.Empty;
            ObjectRefs?.Clear();
            ObjectRefPaths?.Clear();
            ValuesData = string.Empty;
#endif
        }
    }

    /// <summary>
    /// Serializable wrapper around a single polymorphic <c>[SerializeReference]</c> value of type
    /// <typeparamref name="T"/> that keeps working after the concrete type is renamed or moved.
    /// </summary>
    /// <typeparam name="T">
    /// Base type (usually an interface or abstract class) of the value the field can hold.
    /// Concrete implementations should carry a <see cref="RefTypeIdAttribute"/> so their identity
    /// is decoupled from the class name.
    /// </typeparam>
    /// <remarks>
    /// Unity's built-in <c>[SerializeReference]</c> stores the assembly-qualified type name, so renaming
    /// a class nulls the field. <see cref="StableRef{T}"/> stores a stable <see cref="StableRefBase.TypeId"/>
    /// alongside the value and resolves the reference through it, so the data survives refactors.
    /// Declare it directly as a serialized field, e.g. <c>public StableRef&lt;IEffect&gt; OnPickup;</c>,
    /// and read the value through <see cref="Value"/>. From code, assign with <see cref="Set"/> (or
    /// <c>field = new StableRef&lt;IEffect&gt;(v)</c>): it keeps the stable id when the value's type is unchanged and
    /// drops it when the type changes. Writing <see cref="Value"/> directly leaves the metadata as it was.
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

        /// <summary>Creates an empty reference.</summary>
        [Preserve]
        public StableRef() { }

        /// <summary>Creates a reference holding <paramref name="value"/>.</summary>
        public StableRef(T value) => Value = value;

        /// <inheritdoc/>
        public override Type ValueBaseType => typeof(T);

        /// <inheritdoc/>
        public override object BoxedValue
        {
            get => Value;
            set => Set(Cast(value));
        }

        /// <summary>
        /// Assigns <paramref name="value"/> and keeps the metadata consistent with it: when the value's type is the
        /// same as before, the stable id and snapshot are kept; when it differs (or the value is cleared), they are
        /// reset, so no id of another type is left behind.
        /// </summary>
        /// <remarks>
        /// Code can't capture the snapshot (or, for a new type, the id) — the inspector does that when the field is
        /// drawn. After values were written from code in the editor, run <c>StableRefResync.ResyncObject</c> on the
        /// changed objects (or Tools/StableRef/Resync All) before saving. Setting <see langword="null"/> on an entry
        /// whose type is missing keeps its recovery data.
        /// </remarks>
        public void Set(T value)
        {
            var previousType = Value?.GetType();
            Value = value;
            if (value?.GetType() != previousType) ResetMetadata();
        }

        /// <summary>
        /// A new wrapper holding the same value instance, with the metadata copied (it describes that same value).
        /// Use it where an owner is cloned with <see cref="object.MemberwiseClone"/>: the clone otherwise shares
        /// this wrapper, so assigning <see cref="Value"/> on the clone would change the original too.
        /// </summary>
        public StableRef<T> ShallowCopy() => new()
        {
            Value = Value,
            TypeId = TypeId,
#if UNITY_EDITOR
            TypeDisplayName = TypeDisplayName,
            ObjectRefs = ObjectRefs != null ? new List<UnityEngine.Object>(ObjectRefs) : new List<UnityEngine.Object>(),
            ObjectRefPaths = ObjectRefPaths != null ? new List<string>(ObjectRefPaths) : new List<string>(),
            ValuesData = ValuesData
#endif
        };

        /// <summary>The value's <see cref="object.ToString"/>, or <c>None</c> when empty.</summary>
        public override string ToString() => Value?.ToString() ?? "None";

        internal static T Cast(object value)
        {
            if (value == null) return null;
            if (value is T typed) return typed;
            throw new ArgumentException(
                $"[StableRef] A value of type '{value.GetType().FullName}' can't be assigned to StableRef<{typeof(T).FullName}>.",
                nameof(value));
        }
    }
}
