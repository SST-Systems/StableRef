using System;

namespace SST.StableRef
{
    /// <summary>
    /// Type checks for code that works with fields by <see cref="Type"/> — reflection-based config loaders and
    /// serializers. Pair it with <see cref="StableRefBase.BoxedValue"/> and the boxed methods of
    /// <see cref="StableRefListBase"/> to read and write StableRef fields without the generic argument.
    /// </summary>
    /// <example>
    /// <code>
    /// if (StableRefReflection.IsStableRef(field.FieldType, out var valueType))
    /// {
    ///     var wrapper = (StableRefBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    ///     wrapper.BoxedValue = Convert(cell, valueType);
    ///     field.SetValue(owner, wrapper);
    /// }
    /// </code>
    /// </example>
    public static class StableRefReflection
    {
        /// <summary>
        /// True when <paramref name="type"/> is a <see cref="StableRef{T}"/>; <paramref name="valueType"/> is its
        /// <c>T</c>.
        /// </summary>
        public static bool IsStableRef(Type type, out Type valueType)
        {
            valueType = GetArgumentOf(type, typeof(StableRef<>));
            return valueType != null;
        }

        /// <summary>
        /// True when <paramref name="type"/> is a <see cref="StableRefList{T}"/>; <paramref name="elementType"/> is
        /// its <c>T</c>.
        /// </summary>
        public static bool IsStableRefList(Type type, out Type elementType)
        {
            elementType = GetArgumentOf(type, typeof(StableRefList<>));
            return elementType != null;
        }

        private static Type GetArgumentOf(Type type, Type definition)
        {
            if (type == null || !type.IsGenericType || type.IsGenericTypeDefinition) return null;
            return type.GetGenericTypeDefinition() == definition ? type.GetGenericArguments()[0] : null;
        }
    }
}
