# Newtonsoft.Json Converter

**English** | [Русский](README.ru.md)

This sample is a Newtonsoft.Json converter for StableRef fields. With it, a `StableRef<T>` / `StableRefList<T>` field
reads and writes **exactly the JSON of the same field declared as a bare `T` / `List<T>`** — switching a field to
StableRef doesn't change your JSON, so remote configs, saves and server payloads keep working.

Without it, Newtonsoft sees `StableRef<T>` as an ordinary class: it writes the wrapper's own fields (`TypeId` and
`Value`, in the editor also `ValuesData`, `ObjectRefs` and the rest of the snapshot) instead of the value, and reads a
value written for a bare field into nothing. The stable ID and the snapshot are editor metadata — they don't belong in
JSON.

It ships as a sample rather than as part of the package, so StableRef itself has no Newtonsoft dependency.

## What's inside

- `StableRefJsonConverter` (namespace `SST.StableRef.Json`) — the converter; handles every `StableRef<T>` and `StableRefList<T>`.
- `SST.StableRef.Newtonsoft` — the runtime assembly it lives in (all platforms, auto-referenced); references `Newtonsoft.Json.dll` by name.
- `Tests/` — EditMode tests (`SST.StableRef.Newtonsoft.Tests`): JSON parity with bare fields, `TypeNameHandling.Auto`, converters for `T`, `Populate`, list handling, stable IDs, `null`, JSON written without the converter.

## Set it up

1. The project needs Newtonsoft.Json: the `com.unity.nuget.newtonsoft-json` package or any `Newtonsoft.Json.dll` (for example one shipped with an SDK). Without it the imported sample doesn't compile — install Newtonsoft or delete the sample folder.
2. Import the sample: **Window → Package Manager → StableRef → Samples → Newtonsoft.Json Converter → Import**. It is copied to `Assets/Samples/StableRef/<version>/Newtonsoft.Json Converter/`. If you installed StableRef by copying it into `Assets/`, copy the `Samples~/NewtonsoftJson` folder from the repository instead. The copy doesn't update itself — re-import it after upgrading StableRef.
3. Register the converter wherever you build your serializer settings:

```csharp
using SST.StableRef.Json;

var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
settings.Converters.Add(new StableRefJsonConverter());

// { "effect": { "$type": "...", "Damage": 7 }, "Effects": [ ... ] } — same as for IEffect / List<IEffect>
var json = JsonConvert.SerializeObject(config, settings);
```

For fields that used to be `T[]`, create it as `new StableRefJsonConverter(replaceLists: true)` — see [Reading a list](#reading-a-list).

To run the tests, open **Window → General → Test Runner → EditMode** and run `SST.StableRef.Newtonsoft.Tests`.

## How it works

### Writing

Only the value is written, through *your* serializer, with `T` as the declared type — so it behaves exactly like a
bare field: `TypeNameHandling.Auto` adds `$type` when the runtime type differs from `T`, converters registered for `T`
are used, and an empty wrapper is written as `null`. A list is written as a JSON array of its values. The stable ID and
the snapshot are never written.

### Reading a field

- `null` empties the wrapper. Unlike a bare field, the field keeps its wrapper instance, so code reading it never meets `null`.
- Anything else is read the way Newtonsoft reads a bare `T` field: the converter populates the `Value` member of a scratch wrapper, so `$type`, converters for `T` and reuse of the existing value instance (`Populate`, `ObjectCreationHandling`) work exactly as for a bare field.
- The result is assigned through `BoxedValue`, with `StableRef<T>.Set` semantics: a value of the same type keeps the entry's stable ID and snapshot, a value of another type resets them.

### Reading a list

- `null` clears the list; the field keeps its instance.
- Each array element is read as a `T`, so `$type` and converters for `T` apply.
- An existing list is appended to, like a `List<T>`, unless `ObjectCreationHandling` is `Replace` or the converter was created with `replaceLists: true` — then its content is replaced. Replacing reuses the entries by index (`SetBoxedValues`), so elements whose type didn't change keep their stable IDs.
- `replaceLists` exists for fields that used to be `T[]`: Newtonsoft always replaces arrays, so `Populate` into an existing object never duplicated their elements, while `List<T>` semantics would append and duplicate them.

### JSON written without the converter

Data written before the converter was registered stays readable. An object that has both `TypeId` and `Value` and no
members other than the wrapper's (`TypeDisplayName`, `ObjectRefs`, `ObjectRefPaths`, `ValuesData`, `$type`, `$id`) is
treated as a wrapper, and its `Value` is taken. This covers JSON written in the editor (all wrapper fields), in player
builds (only `TypeId` and `Value`) and 3.x lists written as arrays of wrappers. A value type whose only fields happen
to be named `TypeId` and `Value` would be mistaken for a wrapper.

## Limitations

- Member-level settings on the wrapper field itself (`[JsonProperty(TypeNameHandling = ...)]`, `ItemTypeNameHandling`, `ItemConverterType`) don't reach a converter and don't apply; set them in `JsonSerializerSettings` or on the base type.
- Code can't capture the recovery snapshot — or, for a value of a new type, the stable ID; the inspector does that when the field is drawn. After deserializing into assets in the editor, call `StableRefResync.ResyncObject(asset)` before saving or run **Tools → StableRef → Resync All** — see [Reflection and serializers](../../README.md#reflection-and-serializers).
