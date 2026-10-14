using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace SST.StableRef.Json
{
    /// <summary>
    /// Newtonsoft.Json converter for <see cref="StableRef{T}"/> and <see cref="StableRefList{T}"/>: a field holding
    /// a wrapper reads and writes exactly the JSON of the same field declared as a bare <c>T</c> / <c>T[]</c>.
    /// The stable id and snapshot are editor metadata and never appear in JSON.
    /// </summary>
    /// <remarks>
    /// <para>Register it once in your settings: <c>settings.Converters.Add(new StableRefJsonConverter());</c>.</para>
    /// <para>The value is written and read through the calling <see cref="JsonSerializer"/> with <c>T</c> as the
    /// declared type, so <see cref="TypeNameHandling.Auto"/>, converters registered for <c>T</c> and
    /// <see cref="JsonSerializer.Populate(JsonReader, object)"/> into an existing value behave as for a bare field.
    /// A <c>null</c> token empties the wrapper or the list — unlike a bare field, the field keeps its instance, so
    /// code reading it never meets <see langword="null"/>. Reading a list always replaces its content, like a
    /// <c>T[]</c> field, regardless of <see cref="JsonSerializer.ObjectCreationHandling"/> — populating an existing
    /// object never appends (lists migrated from <c>[SerializeReference]</c> were almost always arrays, and the default
    /// <see cref="ObjectCreationHandling.Auto"/> would append and duplicate elements).</para>
    /// <para>Values are assigned through <see cref="StableRefBase.BoxedValue"/> and the boxed list methods, so
    /// entries are reused: an entry whose value keeps its type keeps its stable id. The snapshot can only be
    /// refreshed by the editor — after deserializing into assets in the editor, run
    /// <c>StableRefResync.ResyncObject</c> on them before saving.</para>
    /// <para>Member-level settings on the wrapper field itself (<c>[JsonProperty(TypeNameHandling = ...)]</c>,
    /// <c>ItemTypeNameHandling</c>, <c>ItemConverterType</c>) are not visible to a converter and don't apply.</para>
    /// <para>JSON written by Newtonsoft without this converter (the wrapper's own fields:
    /// <c>{"TypeId": ..., "Value": ...}</c>, plus <c>TypeDisplayName</c>, <c>ObjectRefs</c>, <c>ObjectRefPaths</c>
    /// and <c>ValuesData</c> when written in the editor) is still read: its <c>Value</c> is taken. An object counts as
    /// such a wrapper when it has both <c>TypeId</c> and <c>Value</c> and no members other than the wrapper's
    /// (<c>$type</c> / <c>$id</c> allowed), so a value type whose only fields are named <c>TypeId</c> and
    /// <c>Value</c> would be mistaken for one.</para>
    /// </remarks>
    public sealed class StableRefJsonConverter : JsonConverter
    {
        private const string LegacyValue = "Value";
        private const string LegacyTypeId = "TypeId";

        private static readonly HashSet<string> _legacyMembers = new(StringComparer.Ordinal)
        {
            LegacyValue, LegacyTypeId, "TypeDisplayName", "ObjectRefs", "ObjectRefPaths", "ValuesData", "$type", "$id"
        };

        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
            => StableRefReflection.IsStableRef(objectType, out _) || StableRefReflection.IsStableRefList(objectType, out _);

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            switch (value)
            {
                case null:
                    writer.WriteNull();
                    return;
                case StableRefBase entry:
                    serializer.Serialize(writer, entry.BoxedValue, entry.ValueBaseType);
                    return;
                case StableRefListBase list:
                    writer.WriteStartArray();
                    for (int i = 0; i < list.Count; i++)
                        serializer.Serialize(writer, list.GetBoxed(i), list.ElementType);
                    writer.WriteEndArray();
                    return;
                default:
                    throw new JsonSerializationException(
                        $"[StableRef] {nameof(StableRefJsonConverter)} can't write '{value.GetType().FullName}'.");
            }
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (StableRefReflection.IsStableRefList(objectType, out var elementType))
                return ReadList(reader, objectType, elementType, existingValue as StableRefListBase, serializer);

            return ReadEntry(reader, objectType, existingValue as StableRefBase, serializer);
        }

        private static StableRefBase ReadEntry(JsonReader reader, Type objectType, StableRefBase existing,
            JsonSerializer serializer)
        {
            var entry = existing ?? (StableRefBase)Activator.CreateInstance(objectType);
            if (reader.TokenType == JsonToken.Null)
            {
                entry.BoxedValue = null;
                return entry;
            }

            var token = UnwrapLegacy(JToken.Load(reader));
            if (token.Type == JTokenType.Null)
            {
                entry.BoxedValue = null;
                return entry;
            }

            // Populating a scratch wrapper's Value member runs Newtonsoft's member logic for a field of type T:
            // $type handling, converters for T and reuse of the existing instance — the bare-field behaviour.
            // The result is then assigned through BoxedValue, which keeps the entry's metadata consistent.
            var valueProperty = FindValueProperty(serializer, objectType);
            if (valueProperty != null)
            {
                var scratch = (StableRefBase)Activator.CreateInstance(objectType);
                scratch.BoxedValue = entry.BoxedValue;
                var holder = new JObject { [valueProperty.PropertyName] = token };
                using var holderReader = holder.CreateReader();
                serializer.Populate(holderReader, scratch);
                entry.BoxedValue = scratch.BoxedValue;
            }
            else
            {
                entry.BoxedValue = token.ToObject(entry.ValueBaseType, serializer);
            }

            return entry;
        }

        private static StableRefListBase ReadList(JsonReader reader, Type objectType, Type elementType,
            StableRefListBase existing, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                var emptied = existing ?? (StableRefListBase)Activator.CreateInstance(objectType);
                emptied.Clear();
                return emptied;
            }

            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonSerializationException(
                    $"[StableRef] Expected a JSON array for '{objectType.Name}', got {reader.TokenType}. Path '{reader.Path}'.");

            var values = new List<object>();
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType == JsonToken.Comment) continue;

                if (reader.TokenType == JsonToken.StartObject)
                {
                    var token = UnwrapLegacy(JToken.Load(reader));
                    values.Add(token.Type == JTokenType.Null ? null : token.ToObject(elementType, serializer));
                }
                else
                {
                    values.Add(serializer.Deserialize(reader, elementType));
                }
            }

            // Replacing reuses the existing list and its entries by index, so unchanged types keep their stable ids.
            var list = existing ?? (StableRefListBase)Activator.CreateInstance(objectType);
            list.SetBoxedValues(values);
            return list;
        }

        private static JsonProperty FindValueProperty(JsonSerializer serializer, Type objectType)
        {
            if (serializer.ContractResolver.ResolveContract(objectType) is not JsonObjectContract contract) return null;
            foreach (var property in contract.Properties)
                if (property.UnderlyingName == LegacyValue && property.Writable && !property.Ignored)
                    return property;
            return null;
        }

        private static JToken UnwrapLegacy(JToken token)
        {
            if (token is not JObject obj
                || !obj.TryGetValue(LegacyValue, out var value)
                || !obj.ContainsKey(LegacyTypeId))
                return token;

            foreach (var property in obj.Properties())
                if (!_legacyMembers.Contains(property.Name))
                    return token;
            return value;
        }
    }
}
