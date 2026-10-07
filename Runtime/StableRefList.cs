using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace SST.StableRef
{
    /// <summary>
    /// Non-generic base for <see cref="StableRefList{T}"/>: the common type the editor tooling recognizes, and the
    /// surface for code that only knows the field's <see cref="Type"/> at run time (reflection-based loaders,
    /// serializers).
    /// </summary>
    [Serializable]
    public abstract class StableRefListBase
    {
        /// <summary>The declared element type — <c>T</c> of the <see cref="StableRefList{T}"/>.</summary>
        public abstract Type ElementType { get; }

        /// <summary>Number of elements.</summary>
        public abstract int Count { get; }

        /// <summary>The value at <paramref name="index"/> as <see cref="object"/>.</summary>
        public abstract object GetBoxed(int index);

        /// <summary>
        /// Replaces the value at <paramref name="index"/>, like the indexer. Throws <see cref="ArgumentException"/>
        /// when <paramref name="value"/> is not assignable to <see cref="ElementType"/>.
        /// </summary>
        public abstract void SetBoxed(int index, object value);

        /// <summary>Appends <paramref name="value"/>; type-checked like <see cref="SetBoxed"/>.</summary>
        public abstract void AddBoxed(object value);

        /// <summary>
        /// Replaces the whole content with <paramref name="values"/> (<see langword="null"/> clears the list).
        /// Every element is type-checked before the list is changed. Existing entries are reused by index like the
        /// indexer does, so an element whose value keeps its type keeps its stable id.
        /// </summary>
        public abstract void SetBoxedValues(IEnumerable values);

        /// <summary>Removes all elements.</summary>
        public abstract void Clear();
    }

    /// <summary>
    /// Serializable list of rename-safe polymorphic values — the list counterpart of <see cref="StableRef{T}"/>.
    /// Works as an <see cref="IList{T}"/> of the values themselves; each element is stored in its own
    /// <see cref="StableRef{T}"/> entry.
    /// </summary>
    /// <typeparam name="T">
    /// Base type (usually an interface or abstract class) shared by the elements' values.
    /// </typeparam>
    /// <remarks>
    /// Declare it as a serialized field, e.g. <c>public StableRefList&lt;IEffect&gt; Effects;</c>. Indexing,
    /// <c>foreach</c> and LINQ yield <typeparamref name="T"/>: <c>foreach (var effect in config.Effects)</c> uses a
    /// struct enumerator and does not allocate. Writing through the indexer (<c>list[i] = value</c>) reuses the entry
    /// with <see cref="StableRef{T}.Set"/> semantics: its stable id is kept when the value's type is unchanged and
    /// reset when it changes. The entries themselves — with their stable id and snapshot — are in <see cref="Items"/>.
    /// </remarks>
    [Serializable]
    public sealed class StableRefList<T> : StableRefListBase, IList<T>, IReadOnlyList<T> where T : class
    {
        [SerializeField] private List<StableRef<T>> _items = new();

        /// <summary>Creates an empty list.</summary>
        [Preserve]
        public StableRefList() { }

        /// <summary>Creates a list holding <paramref name="values"/>, each in a new entry.</summary>
        public StableRefList(IEnumerable<T> values) => AddRange(values);

        /// <summary>
        /// The underlying entries — the stable id and snapshot of each element, and the full <c>List</c> API
        /// (Sort, GetRange, ...) over them. Lazily created, so it is never <see langword="null"/>.
        /// </summary>
        public List<StableRef<T>> Items => _items ??= new List<StableRef<T>>();

        /// <summary>Number of elements in the list; <c>0</c> when uninitialized.</summary>
        public override int Count => _items?.Count ?? 0;

        /// <summary>
        /// The value at <paramref name="index"/>. Setting it reuses the entry (see <see cref="StableRef{T}.Set"/>):
        /// the stable id survives a value of the same type and is reset for a different type.
        /// </summary>
        public T this[int index]
        {
            get => Items[index]?.Value;
            set
            {
                var items = Items;
                if (items[index] == null) items[index] = new StableRef<T>(value);
                else items[index].Set(value);
            }
        }

        bool ICollection<T>.IsReadOnly => false;

        /// <inheritdoc/>
        public override Type ElementType => typeof(T);

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

        /// <summary>First value matching <paramref name="match"/>, or <see langword="null"/>.</summary>
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

        /// <summary>Appends <paramref name="value"/> in a new entry.</summary>
        public void Add(T value) => Items.Add(new StableRef<T>(value));

        /// <summary>Appends every value in <paramref name="values"/>.</summary>
        public void AddRange(IEnumerable<T> values)
        {
            if (values == null) return;
            foreach (var v in values) Items.Add(new StableRef<T>(v));
        }

        /// <summary>Inserts <paramref name="value"/> at <paramref name="index"/>.</summary>
        public void Insert(int index, T value) => Items.Insert(index, new StableRef<T>(value));

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
        public override void Clear() => Items.Clear();

        /// <summary>Copies the values to <paramref name="array"/> starting at <paramref name="arrayIndex"/>.</summary>
        public void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            var items = Items;
            if (arrayIndex < 0 || arrayIndex + items.Count > array.Length)
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            for (int i = 0; i < items.Count; i++)
                array[arrayIndex + i] = items[i]?.Value;
        }

        /// <summary>The values as a new array.</summary>
        public T[] ToArray()
        {
            var result = new T[Count];
            if (result.Length > 0) CopyTo(result, 0);
            return result;
        }

        /// <summary>
        /// A new list with new entries holding the same value instances (metadata copied). See
        /// <see cref="StableRef{T}.ShallowCopy"/>: a <see cref="object.MemberwiseClone"/> of the owner would share
        /// this list with the original.
        /// </summary>
        public StableRefList<T> ShallowCopy()
        {
            var copy = new StableRefList<T>();
            var items = Items;
            var target = copy.Items;
            target.Capacity = items.Count;
            for (int i = 0; i < items.Count; i++)
                target.Add(items[i]?.ShallowCopy() ?? new StableRef<T>());
            return copy;
        }

        /// <inheritdoc/>
        public override object GetBoxed(int index) => this[index];

        /// <inheritdoc/>
        public override void SetBoxed(int index, object value) => this[index] = StableRef<T>.Cast(value);

        /// <inheritdoc/>
        public override void AddBoxed(object value) => Add(StableRef<T>.Cast(value));

        /// <inheritdoc/>
        public override void SetBoxedValues(IEnumerable values)
        {
            var typed = new List<T>();
            if (values != null)
                foreach (var v in values)
                    typed.Add(StableRef<T>.Cast(v));

            var items = Items;
            if (items.Count > typed.Count) items.RemoveRange(typed.Count, items.Count - typed.Count);
            for (int i = 0; i < typed.Count; i++)
            {
                if (i < items.Count) this[i] = typed[i];
                else items.Add(new StableRef<T>(typed[i]));
            }
        }

        /// <summary>
        /// Returns a struct enumerator over the values — <c>foreach</c> over the list does not allocate. Lazily
        /// initializes the backing list so iterating a freshly created list never throws.
        /// </summary>
        public Enumerator GetEnumerator() => new(Items);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Allocation-free enumerator over the values of a <see cref="StableRefList{T}"/>.</summary>
        public struct Enumerator : IEnumerator<T>
        {
            private readonly List<StableRef<T>> _items;
            private List<StableRef<T>>.Enumerator _inner;

            internal Enumerator(List<StableRef<T>> items)
            {
                _items = items;
                _inner = items.GetEnumerator();
            }

            /// <summary>The current value (<see langword="null"/> for an empty entry).</summary>
            public T Current => _inner.Current?.Value;

            object IEnumerator.Current => Current;

            /// <summary>Advances to the next value.</summary>
            public bool MoveNext() => _inner.MoveNext();

            void IEnumerator.Reset() => _inner = _items.GetEnumerator();

            /// <summary>Releases the enumerator.</summary>
            public void Dispose() => _inner.Dispose();
        }
    }
}
