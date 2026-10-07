using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SST.StableRef.Json;

namespace SST.StableRef.Tests
{
    public class StableRefJsonConverterTests
    {
        public interface IJsonThing { }

        public class Fire : IJsonThing
        {
            public int Damage = 5;
        }

        public class Ice : IJsonThing
        {
            public int Slow = 2;
        }

        /// <summary>A value whose members happen to share names with the wrapper's.</summary>
        public class Tagged : IJsonThing
        {
            public string TypeId;
            public int Value;
            public int Extra;
        }

        public class BareConfig
        {
            [JsonProperty("effect")] public IJsonThing Effect;
            public List<IJsonThing> Effects = new();
        }

        public class WrappedConfig
        {
            [JsonProperty("effect")] public StableRef<IJsonThing> Effect = new();
            public StableRefList<IJsonThing> Effects = new();
        }

        /// <summary>Stands in for a project converter that reads a custom "$Type" key for interface fields.</summary>
        private sealed class DollarTypeConverter : JsonConverter
        {
            public override bool CanConvert(Type t) => t.IsInterface || t.IsAbstract;
            public override bool CanWrite => false;
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
                => throw new NotSupportedException();

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var obj = JObject.Load(reader);
                var type = typeof(StableRefJsonConverterTests).GetNestedType((string)obj["$Type"]);
                obj.Remove("$Type");
                var instance = Activator.CreateInstance(type);
                using var objReader = obj.CreateReader();
                serializer.Populate(objReader, instance);
                return instance;
            }
        }

        private static JsonSerializerSettings Auto()
        {
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
            settings.Converters.Add(new StableRefJsonConverter());
            return settings;
        }

        [Test]
        public void Write_MatchesBareField()
        {
            var bare = new BareConfig { Effect = new Fire { Damage = 7 }, Effects = { new Ice(), null, new Fire() } };
            var wrapped = new WrappedConfig
            {
                Effect = new StableRef<IJsonThing>(new Fire { Damage = 7 }) { TypeId = "metadata stays out of JSON" },
                Effects = { new Ice(), null, new Fire() }
            };

            Assert.AreEqual(JsonConvert.SerializeObject(bare, Auto()), JsonConvert.SerializeObject(wrapped, Auto()));
        }

        [Test]
        public void RoundTrip_WithTypeNameHandlingAuto()
        {
            var json = JsonConvert.SerializeObject(
                new BareConfig { Effect = new Fire { Damage = 7 }, Effects = { new Ice { Slow = 4 }, null } }, Auto());

            var wrapped = JsonConvert.DeserializeObject<WrappedConfig>(json, Auto());

            Assert.AreEqual(7, ((Fire)wrapped.Effect.Value).Damage);
            Assert.AreEqual(2, wrapped.Effects.Count);
            Assert.AreEqual(4, ((Ice)wrapped.Effects[0]).Slow);
            Assert.IsNull(wrapped.Effects[1]);
            Assert.AreEqual(json, JsonConvert.SerializeObject(wrapped, Auto()));
        }

        [Test]
        public void Read_UsesConverterOfBaseType()
        {
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new DollarTypeConverter());
            settings.Converters.Add(new StableRefJsonConverter());

            var config = JsonConvert.DeserializeObject<WrappedConfig>(
                "{\"effect\":{\"$Type\":\"Fire\",\"Damage\":11},\"Effects\":[{\"$Type\":\"Ice\",\"Slow\":4}]}", settings);

            Assert.AreEqual(11, ((Fire)config.Effect.Value).Damage);
            Assert.AreEqual(4, ((Ice)config.Effects[0]).Slow);
        }

        [Test]
        public void Populate_ReusesExistingValueLikeBareField()
        {
            var existing = new Fire { Damage = 1 };
            var wrapped = new WrappedConfig { Effect = new StableRef<IJsonThing>(existing) };
            var bare = new BareConfig { Effect = new Fire { Damage = 1 } };
            var bareExisting = bare.Effect;

            JsonConvert.PopulateObject("{\"effect\":{\"Damage\":42}}", wrapped, Auto());
            JsonConvert.PopulateObject("{\"effect\":{\"Damage\":42}}", bare, Auto());

            Assert.AreEqual(ReferenceEquals(bare.Effect, bareExisting), ReferenceEquals(wrapped.Effect.Value, existing));
            Assert.AreEqual(42, ((Fire)wrapped.Effect.Value).Damage);
        }

        [Test]
        public void List_FollowsObjectCreationHandling()
        {
            var config = new WrappedConfig { Effects = { new Fire() } };
            JsonConvert.PopulateObject("{\"Effects\":[{\"$type\":\"" + Name<Ice>() + "\"}]}", config, Auto());
            Assert.AreEqual(2, config.Effects.Count, "appends by default, like List<T>");

            var replace = Auto();
            replace.ObjectCreationHandling = ObjectCreationHandling.Replace;
            JsonConvert.PopulateObject("{\"Effects\":[{\"$type\":\"" + Name<Ice>() + "\"}]}", config, replace);
            Assert.AreEqual(1, config.Effects.Count);
        }

        [Test]
        public void ReplaceLists_PopulateReplacesLikeArray_AndKeepsIdsOfSameType()
        {
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
            settings.Converters.Add(new StableRefJsonConverter(replaceLists: true));

            var config = new WrappedConfig { Effects = { new Fire(), new Ice() } };
            config.Effects.Items[0].TypeId = "fire-id";
            config.Effects.Items[1].TypeId = "ice-id";

            JsonConvert.PopulateObject(
                "{\"Effects\":[{\"$type\":\"" + Name<Fire>() + "\",\"Damage\":9},{\"$type\":\"" + Name<Fire>() + "\"}]}",
                config, settings);

            Assert.AreEqual(2, config.Effects.Count, "replaced, not appended");
            Assert.AreEqual(9, ((Fire)config.Effects[0]).Damage);
            Assert.AreEqual("fire-id", config.Effects.Items[0].TypeId, "same type keeps its stable id");
            Assert.IsTrue(string.IsNullOrEmpty(config.Effects.Items[1].TypeId), "changed type drops the old id");
        }

        [Test]
        public void Populate_KeepsIdOfSameType_DropsIdOfOtherType()
        {
            var config = new WrappedConfig { Effect = new StableRef<IJsonThing>(new Fire()) { TypeId = "fire-id" } };

            JsonConvert.PopulateObject("{\"effect\":{\"Damage\":3}}", config, Auto());
            Assert.AreEqual("fire-id", config.Effect.TypeId);

            JsonConvert.PopulateObject("{\"effect\":{\"$type\":\"" + Name<Ice>() + "\"}}", config, Auto());
            Assert.IsInstanceOf<Ice>(config.Effect.Value);
            Assert.IsTrue(string.IsNullOrEmpty(config.Effect.TypeId));
        }

        [Test]
        public void Null_EmptiesWrapper()
        {
            var config = JsonConvert.DeserializeObject<WrappedConfig>("{\"effect\":null}", Auto());
            Assert.IsNotNull(config.Effect);
            Assert.IsNull(config.Effect.Value);
        }

        [Test]
        public void Null_EmptiesListAndKeepsInstance()
        {
            var config = new WrappedConfig { Effects = { new Fire() } };
            var list = config.Effects;

            JsonConvert.PopulateObject("{\"Effects\":null}", config, Auto());

            Assert.AreSame(list, config.Effects);
            Assert.AreEqual(0, config.Effects.Count);
            Assert.IsNotNull(JsonConvert.DeserializeObject<WrappedConfig>("{\"Effects\":null}", Auto()).Effects);
        }

        [Test]
        public void Read_AcceptsWrapperShapeWrittenWithoutConverter()
        {
            var json = "{\"effect\":{\"TypeId\":\"x\",\"TypeDisplayName\":\"Fire\",\"ValuesData\":\"#v2\"," +
                       "\"Value\":{\"$type\":\"" + Name<Fire>() + "\",\"Damage\":3}}}";
            var config = JsonConvert.DeserializeObject<WrappedConfig>(json, Auto());
            Assert.AreEqual(3, ((Fire)config.Effect.Value).Damage);
        }

        [Test]
        public void Read_AcceptsWrapperShapeWithoutEditorOnlyFields()
        {
            // A player build has no editor-only fields, so without the converter only TypeId and Value are written;
            // lists from 3.x were arrays of such wrappers.
            var json = "{\"effect\":{\"TypeId\":\"x\",\"Value\":{\"$type\":\"" + Name<Fire>() + "\",\"Damage\":3}}," +
                       "\"Effects\":[{\"TypeId\":\"y\",\"Value\":{\"$type\":\"" + Name<Ice>() + "\",\"Slow\":6}}]}";
            var config = JsonConvert.DeserializeObject<WrappedConfig>(json, Auto());
            Assert.AreEqual(3, ((Fire)config.Effect.Value).Damage);
            Assert.AreEqual(6, ((Ice)config.Effects[0]).Slow);
        }

        [Test]
        public void Read_DoesNotUnwrapValueWithOtherMembers()
        {
            var json = "{\"effect\":{\"$type\":\"" + Name<Tagged>() + "\",\"TypeId\":\"t\",\"Value\":1,\"Extra\":2}}";
            var tagged = JsonConvert.DeserializeObject<WrappedConfig>(json, Auto()).Effect.Value as Tagged;
            Assert.NotNull(tagged);
            Assert.AreEqual(1, tagged.Value);
            Assert.AreEqual(2, tagged.Extra);
        }

        private static string Name<T>() => $"{typeof(T).FullName}, {typeof(T).Assembly.GetName().Name}";
    }
}
