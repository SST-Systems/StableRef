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
    /// Declare it as a serialized field, e.g. <c>public StableRefList&lt;IEffect&gt; Effects;</c>. It can
    /// be authored in the inspector and also manipulated from code with a familiar <c>List&lt;T&gt;</c>-like
    /// API: <see cref="Add"/>, <see cref="Insert"/>, <see cref="Remove"/>, <see cref="RemoveAt"/>,
    /// <see cref="Clear"/>, <see cref="Contains"/>, <see cref="IndexOf"/>, <see cref="Find"/>, etc.
    /// Indexing and <c>foreach</c> yield the <see cref="StableRef{T}"/> wrapper (read the value via
    /// <see cref="StableRef{T}.Value"/>); the value-oriented helpers (<see cref="ValueAt"/>,
    /// <see cref="Values"/>) and all query/mutation methods work directly with <typeparamref name="T"/>.
    /// </remarks>
    [Serializable]
    public sealed class StableRefList<T> : StableRefListBase, IEnumerable<StableRef<T>> where T : class
    {
        [SerializeField] private List<StableRef<T>> _items = new();

        /// <summary>
        /// The underlying list of <see cref="StableRef{T}"/> wrappers — exposed for the full standard
        /// <c>List&lt;T&gt;</c> API (Sort, GetRange, ...). Lazily created, so it is never <c>null</c>.
        /// Prefer the value-level helpers (<see cref="Add"/>, <see cref="Contains"/>, ...) for everyday use.
        /// </summary>
        public List<StableRef<T>> Items => _items ??= new List<StableRef<T>>();

        /// <summary>Number of elements in the list; <c>0</c> when uninitialized.</summary>
        public int Count => _items?.Count ?? 0;

        /// <summary>The <see cref="StableRef{T}"/> wrapper at <paramref name="index"/>.</summary>
        public StableRef<T> this[int index] => Items[index];

        /// <summary>The value at <paramref name="index"/> — shortcut for <c>this[index].Value</c>.</summary>
        public T ValueAt(int index) => Items[index]?.Value;

        /// <summary>Iterates the wrapped values (each element's <see cref="StableRef{T}.Value"/>).</summary>
        public IEnumerable<T> Values
        {
            get
            {
                var items = Items;
                for (int i = 0; i < items.Count; i++)
                    yield return items[i]?.Value;
            }
        }

        /// <summary>True if any element's value equals <paramref name="value"/>.</summary>
        public bool Contains(T value) => IndexOf(value) >= 0;

        /// <summary>Index of the first element whose value equals <paramref name="value"/>, or <c>-1</c>.</summary>
        public int IndexOf(T value)
        {
            var items = Items;
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < items.Count; i++)
                if (cmp.Equals(items[i]?.Value, value)) return i;
            return -1;
        }

        /// <summary>First value matching <paramref name="match"/>, or <c>null</c>.</summary>
        public T Find(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            var items = Items;
            for (int i = 0; i < items.Count; i++)
            {
                var v = items[i]?.Value;
                if (match(v)) return v;
            }
            return null;
        }

        /// <summary>Index of the first value matching <paramref name="match"/>, or <c>-1</c>.</summary>
        public int FindIndex(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            var items = Items;
            for (int i = 0; i < items.Count; i++)
                if (match(items[i]?.Value)) return i;
            return -1;
        }

        /// <summary>True if any value matches <paramref name="match"/>.</summary>
        public bool Exists(Predicate<T> match) => FindIndex(match) >= 0;

        /// <summary>Appends <paramref name="value"/>, wrapping it in a new <see cref="StableRef{T}"/>.</summary>
        public void Add(T value) => Items.Add(new StableRef<T> { Value = value });

        /// <summary>Appends every value in <paramref name="values"/>.</summary>
        public void AddRange(IEnumerable<T> values)
        {
            if (values == null) return;
            foreach (var v in values) Items.Add(new StableRef<T> { Value = v });
        }

        /// <summary>Inserts <paramref name="value"/> at <paramref name="index"/>.</summary>
        public void Insert(int index, T value) => Items.Insert(index, new StableRef<T> { Value = value });

        /// <summary>Removes the first element whose value equals <paramref name="value"/>; returns whether one was removed.</summary>
        public bool Remove(T value)
        {
            int i = IndexOf(value);
            if (i < 0) return false;
            Items.RemoveAt(i);
            return true;
        }

        /// <summary>Removes the element at <paramref name="index"/>.</summary>
        public void RemoveAt(int index) => Items.RemoveAt(index);

        /// <summary>Removes every element whose value matches <paramref name="match"/>; returns the count removed.</summary>
        public int RemoveAll(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            return Items.RemoveAll(it => match(it?.Value));
        }

        /// <summary>Removes all elements.</summary>
        public void Clear() => Items.Clear();

        /// <summary>
        /// Returns a struct enumerator over the <see cref="StableRef{T}"/> wrappers. Lazily initializes
        /// the backing list so iterating a freshly created list never throws.
        /// </summary>
        public List<StableRef<T>>.Enumerator GetEnumerator() => Items.GetEnumerator();

        IEnumerator<StableRef<T>> IEnumerable<StableRef<T>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}