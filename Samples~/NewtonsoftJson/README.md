# Newtonsoft.Json Converter

**English** | [Русский](README.ru.md)

This sample is a Newtonsoft.Json converter for StableRef fields. With it, a `StableRef<T>` / `StableRefList<T>` field
reads and writes **exactly the JSON of the same field declared as a bare `T` / `List<T>`** — switching a field to
StableRef doesn't change your JSON, so remote configs, saves and server payloads keep working.

Without it, Newtonsoft writes the wrapper's own fields (the type ID and editor metadata) instead of the value.

It ships as a sample so that StableRef itself has no Newtonsoft dependency.

## What's inside

- `Runtime/` — the `SST.StableRef.Newtonsoft` assembly (all platforms):
  - `StableRefJsonConverter` (namespace `SST.StableRef.Json`) — the converter for every `StableRef<T>` and `StableRefList<T>`.
  - `StableRefJson.DataEquals(a, b, settings)` — whether two objects hold the same data (see [Comparing data](#comparing-data)).
- `Tests/Editor/` — EditMode tests (`SST.StableRef.Newtonsoft.Tests`).

## Set it up

1. The project needs Newtonsoft.Json: the `com.unity.nuget.newtonsoft-json` package or any `Newtonsoft.Json.dll`. Without it the imported sample doesn't compile.
2. Import the sample: **Window → Package Manager → StableRef → Samples → Newtonsoft.Json Converter → Import**. Re-import it after upgrading StableRef.
3. Register the converter wherever you build your serializer settings:

```csharp
using SST.StableRef.Json;

var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
settings.Converters.Add(new StableRefJsonConverter());

// { "effect": { "$type": "...", "Damage": 7 }, "Effects": [ ... ] } — same as for IEffect / List<IEffect>
var json = JsonConvert.SerializeObject(config, settings);
```

## How it works

### Writing

Only the value is written, through your serializer, exactly as for a bare field: `$type`, converters for `T` and your
settings all apply. An empty wrapper is written as `null`, a list as an array of its values.

### Reading a field

The value is read the way Newtonsoft reads a bare `T` field (`$type`, converters, `Populate` all work). `null`
empties the wrapper, but the field itself is never set to `null`.

### Reading a list

Each element is read as a `T`. The list's content is always replaced, like a `T[]` field's, whatever
`ObjectCreationHandling` says: populating an existing object (for example, a config asset from remote JSON) never
appends elements and never duplicates them. Elements that keep their type keep their stable ID.

### JSON written without the converter

Data written before the converter was registered stays readable: an object that looks like a serialized wrapper is
recognized, and its value is taken.

### Comparing data

`StableRefJson.DataEquals(a, b, settings)` compares the data of two objects as JSON — for example, to apply a config
from the server only when it changed. Works in player builds.

```csharp
var incoming = JsonConvert.DeserializeObject<GameConfig>(serverJson, settings);
if (!StableRefJson.DataEquals(current, incoming, settings))
    Apply(incoming);
```

Deserialize first and compare objects, not the raw JSON, so key order and default values don't count as changes. A
value of another type inside a StableRef counts as a change. Compare the plain data class rather than a
`ScriptableObject` holding it.

## Limitations

- Attributes on the wrapper field itself (`[JsonProperty(TypeNameHandling = ...)]`, `ItemConverterType`) don't apply; set them in `JsonSerializerSettings` or on the base type.
- After deserializing into assets in the editor, call `StableRefResync.ResyncObject(asset)` before saving or run **Tools → StableRef → Resync All** — see [Reflection and serializers](../../README.md#reflection-and-serializers).
