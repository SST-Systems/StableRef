using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SST.StableRef.Json
{
    /// <summary>
    /// Runtime helpers on top of <see cref="StableRefJsonConverter"/> for data that comes as JSON — config from a
    /// server, save files, mods.
    /// </summary>
    public static class StableRefJson
    {
        /// <summary>
        /// True when two objects hold the same data as JSON sees it: both are serialized through the same serializer
        /// and the resulting trees are compared with <see cref="JToken.DeepEquals(JToken, JToken)"/> (property order
        /// doesn't matter, array order does). Use it to apply incoming data only when it differs from what is already
        /// loaded: deserialize the incoming JSON first and compare the two objects, so members the JSON omits or has
        /// in excess don't count as changes.
        /// </summary>
        /// <remarks>
        /// <para>The comparison runs on values only — stable ids and snapshots are editor metadata, absent from JSON
        /// and from players. A value of another type inside a <see cref="StableRef{T}"/> or
        /// <see cref="StableRefList{T}"/> counts as a change: with <see cref="TypeNameHandling.None"/> the settings are
        /// compared with <see cref="TypeNameHandling.Auto"/>, so the type of a polymorphic value is part of the JSON
        /// even when the members of both types serialize the same. Root objects of different types are never equal.</para>
        /// <para>Members your settings don't write (<c>[JsonIgnore]</c>, <see cref="NullValueHandling.Ignore"/>, a
        /// converter that skips data) are not compared. Floating-point values are compared as written, so a value that
        /// doesn't survive a JSON round trip unchanged is reported as a change.</para>
        /// </remarks>
        /// <param name="a">First object; may be <see langword="null"/>.</param>
        /// <param name="b">Second object; may be <see langword="null"/>.</param>
        /// <param name="settings">
        /// The settings the data is read with, including <see cref="StableRefJsonConverter"/>; <see langword="null"/>
        /// for defaults with the converter added.
        /// </param>
        public static bool DataEquals(object a, object b, JsonSerializerSettings settings = null)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;

            var serializer = JsonSerializer.Create(settings);
            if (settings == null) serializer.Converters.Add(new StableRefJsonConverter());
            if (serializer.TypeNameHandling == TypeNameHandling.None) serializer.TypeNameHandling = TypeNameHandling.Auto;

            return JToken.DeepEquals(JToken.FromObject(a, serializer), JToken.FromObject(b, serializer));
        }

        /// <summary>
        /// Same as <see cref="DataEquals(object, object, JsonSerializerSettings)"/> through a serializer you already
        /// have, used as is: set its <see cref="JsonSerializer.TypeNameHandling"/> to <see cref="TypeNameHandling.Auto"/>
        /// (or register a converter that writes the type) for a change of type inside a StableRef to be seen.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is null.</exception>
        public static bool DataEquals(object a, object b, JsonSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;

            return JToken.DeepEquals(JToken.FromObject(a, serializer), JToken.FromObject(b, serializer));
        }
    }
}
