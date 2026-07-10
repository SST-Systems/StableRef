using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Non-generic base for <see cref="StableRefList{T}"/>, used by the editor tooling to recognize
    /// StableRef lists through a common type. Not intended for direct use.
    /// </summary>
    [Serializable]
    public abstract class StableRefListBase { }

    /// <summary>
    /// Serializable, iterable collection of <see cref="StableRef{T}"/> items — the list counterpart of
    /// <see cref="StableRef{T}"/> for fields that hold many rename-safe polymorphic references.
    /// </summary>
    /// <typeparam name="T">
    /// Base type (usually an interface or abstract class) shared by the elements' values.
    /// </typeparam>
    /// <remarks>
    /// Declare it as a serialized field, e.g. <c>public StableRefList&lt;IEffect&gt; Effects;</c>, and
    /// enumerate it with <c>foreach</c>. Each element is a <see cref="StableRef{T}"/>, so read the actual
    /// value via <see cref="StableRef{T}.Value"/> and null-check it. The list is read-only from code —
    /// entries are added and reordered through the inspector; the exposed members are for iteration and
    /// lookup only.
    /// </remarks>
    [Serializable]
    public sealed class StableRefList<T> : StableRefListBase, IEnumerable<StableRef<T>> where T : class
    {
        [SerializeField] private List<StableRef<T>> _items = new();

        /// <summary>Number of elements in the list; <c>0</c> when uninitialized.</summary>
        public int Count => _items?.Count ?? 0;

        /// <summary>Gets the <see cref="StableRef{T}"/> at <paramref name="index"/>.</summary>
        /// <param name="index">Zero-based index, in range <c>[0, <see cref="Count"/>)</c>.</param>
        public StableRef<T> this[int index] => _items[index];

        /// <summary>
        /// Returns a struct enumerator over the elements. Lazily initializes the backing list so
        /// iterating a freshly created list never throws.
        /// </summary>
        public List<StableRef<T>>.Enumerator GetEnumerator()
            => (_items ??= new List<StableRef<T>>()).GetEnumerator();

        IEnumerator<StableRef<T>> IEnumerable<StableRef<T>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}