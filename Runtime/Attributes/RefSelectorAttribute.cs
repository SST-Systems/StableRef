using System;
using System.Diagnostics;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Editor-only marker that gives a plain <c>[SerializeReference]</c> field the StableRef type selector
    /// (searchable dropdown, categories, generic-interface candidates, copy / paste) without the
    /// <see cref="StableRef{T}"/> wrapper.
    /// </summary>
    /// <remarks>
    /// The field stays a bit-for-bit ordinary <c>[SerializeReference]</c>: no stable id and no recovery
    /// snapshot are stored next to the value, so there is zero per-instance serialized overhead. The attribute
    /// is <see cref="ConditionalAttribute">conditional</see> on <c>UNITY_EDITOR</c>, so it is not even emitted
    /// into player builds — handy for ECS authoring / baking code or any type that should stay free of
    /// StableRef at runtime.
    /// <para>
    /// <b>Use at your own risk.</b> Integrity is entirely on you: renaming or moving the value's class breaks
    /// the reference exactly as it would for a bare <c>[SerializeReference]</c>, and these fields are
    /// deliberately <b>not</b> covered by Find Usages or Fix Missing Types. Keep renames safe with Unity's
    /// <c>[MovedFrom]</c> (<c>UnityEngine.Scripting.APIUpdating</c>). Use <see cref="StableRef{T}"/> where you
    /// want references that survive refactors and are tracked by the tool windows.
    /// </para>
    /// <code>
    /// [SerializeReference, RefSelector] private IEffect _onPickup;
    /// [SerializeReference, RefSelector] private List&lt;IEffect&gt; _effects;
    /// </code>
    /// Apply it together with <c>[SerializeReference]</c>. On a list or array field the selector is drawn
    /// per element, so decorate the collection field itself.
    /// </remarks>
    [Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class RefSelectorAttribute : PropertyAttribute
    {
    }
}
