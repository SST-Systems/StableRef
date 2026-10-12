# SerializeReference Migration

**English** | [Русский](README.ru.md)

This sample switches a project from plain `[SerializeReference]` fields to StableRef — the code and the saved data —
with one button. It rewrites the field declarations in your scripts, waits for them to compile, moves the data of
every asset into the new layout, follows prefab overrides and stamps every entry's stable ID and snapshot.

```csharp
// before
[SerializeReference, Tooltip("On hit")] public IEffect effect = new Burn();
[SerializeReference] private List<IEffect> _effects = new();

// after
[Tooltip("On hit")] public StableRef<IEffect> effect = new(new Burn());
[SerializeField] private StableRefList<IEffect> _effects = new();
```

Only the type changes: the field keeps its name, its access and its other attributes, so its data stays under the
same key. A single reference becomes `StableRef<T>`, an array or list `StableRefList<T>` (an `IList<T>`). Code that
reads the value now needs `.Value` (`effect.Value.Apply()`, `effect.Set(new Burn())`) — the compiler lists those
places, and the data waits until they are fixed.

Why a tool is needed: Unity can't read the old data into the new field. It loads the field empty, keeps the old
reference records only in the file, and drops them the first time the asset is saved. So the migration works on the
files themselves, before anything saves them.

It ships as a sample rather than as part of the package: it is needed once per project, while switching.

## What's inside

- `SerializeReferenceMigrator` (namespace `SST.StableRef.Migration`) — the menu and the public API (`Analyze`, `Migrate`, `Run`).
- `MigrationCodeRewriter` — retypes the declarations, adds `SST.StableRef` to assembly definitions and `[RefTypeId]` to value types without a stable ID.
- `MigrationContinuation` — moves the data after the rewritten scripts compile.
- `MigrationScenes` — closes and reopens the open scenes, resyncs the converted ones.
- `MigrationYamlConverter`, `MigrationAssetIndex`, `MigrationFieldModel` — the text rewrite of one asset file, the types read from YAML, the fields that are StableRef.
- `SST.StableRef.Migration.Editor` — the editor assembly they live in.
- `Tests/` — EditMode tests (`SST.StableRef.Migration.Tests`, with test types in `SST.StableRef.Migration.TestTypes`): declaration rewrites, every kind of field in assets, a dry run, prefab variants, overrides of objects inside nested prefabs.

## Use it

1. Commit or back up the project. The migration rewrites scripts and assets under `Assets/`.
2. Import the sample: **Window → Package Manager → StableRef → Samples → SerializeReference Migration → Import**. If you installed StableRef by copying it into `Assets/`, copy the `Samples~/SerializeReferenceMigration` folder from the repository instead.
3. **Tools → StableRef → Migrate SerializeReference Data → Analyze (Dry Run)** lists the fields that will be rewritten, the `[RefTypeId]` and assembly references that will be added, and warnings. Nothing is written.
4. **Tools → StableRef → Migrate SerializeReference Data → Migrate…** You are asked to save modified scenes; open scenes are closed for the migration. The field types in the scripts are changed and Unity recompiles; fix the places that read the fields (`.Value`). Once the scripts compile, the data is moved, every converted scene is opened, resynced and saved, and the scenes you had open are reopened. The report goes to the console.

The compile errors after the rewrite are the code that uses the fields: reading a single reference needs `.Value`, assigning one `.Set(value)`, and a `List<T>` or array API that `StableRefList<T>` doesn't have (`.Length`, `AddRange`) needs replacing — fix them. Until then the old types stay loaded and the data stays readable; the data step runs by itself after the next successful compile, once the rewritten scripts are loaded (an editor started with compile errors and *Ignore* in the Safe Mode dialog runs the old scripts, so the data keeps waiting). A field you changed back by hand doesn't hold it up: its data is left as is and the report lists it. **Resume Pending Migration** in the same menu runs the data step right away, **Cancel Pending Migration** stops waiting.

Fields you already changed to `StableRef<T>` / `StableRefList<T>` by hand (same name, or with `[FormerlySerializedAs]`) are picked up by the data step as well.

After that the sample can be deleted.

To run the tests, open **Window → General → Test Runner → EditMode** and run `SST.StableRef.Migration.Tests`.

## What it does

### Code

- Every `[SerializeReference]` field declared in a script under `Assets/` is retyped in place (scripts of packages are not touched — their assets would stay in the old layout): `T` → `StableRef<T>`, `T[]` / `List<T>` → `StableRefList<T>`. The name, the access modifiers and the other attributes stay; no field, property or `[FormerlySerializedAs]` is added.
- `[SerializeReference]` is removed from a public field and replaced with `[SerializeField]` on a non-public one (unless it already has it). `[field: SerializeReference]` auto-properties get `[field: SerializeField]` and keep their accessors. Attributes of plain references and array drawers (`[SubclassSelector]`, `[ListDrawerSettings]`…) are dropped. An initializer is wrapped (`= new Burn()` → `= new(new Burn())`). A field without one gets `= new()` in a plain class — an object made with `new` would otherwise hold a null wrapper and `.Value` would throw; components, ScriptableObjects (Unity creates the wrapper) and structs (no field initializers in C# 9) stay without one.
- `SST.StableRef` is added to the assembly definitions of the changed scripts, and `using SST.StableRef;` to the scripts.
- Value types without a stable ID — classes that share a file, so they can't use the script's GUID — get `[RefTypeId("Namespace.Type")]`. Only types the selector would offer get one (concrete classes with a parameterless constructor); for fields of type `object` none are added — add them by hand (reported).
- Left out: fields with `[RefSelector]` (plain references on purpose), fields of generic parameter types, fields in StableRef's own assemblies, declarations spread over several lines or declaring several fields at once (reported).

### Data

The data step is a text rewrite of the Unity YAML files under `Assets/` (all of them, except kinds that never hold
script data — materials, animation clips, presets…), file by file:

| Where | Before | After |
|---|---|---|
| `StableRef<T>` field | `field: {rid: N}` | `field: {Value: {rid: N}}` |
| `StableRefList<T>` field | `field: [- rid: N, …]` | `field: {_items: [- Value: {rid: N}, …]}` |
| prefab override, single | `field` | `field.Value` |
| prefab override, list size | `field.Array.size` | `field._items.Array.size` |
| prefab override, element | `field.Array.data[i]` | `field._items.Array.data[i].Value` |

- Fields are found in the main data of components and ScriptableObjects, inside nested `[Serializable]` classes and arrays of them, and inside the data of other `[SerializeReference]` values — a StableRef field of a class that is itself a referenced value is converted too.
- The reference records themselves are kept as they are, with their IDs: values, nested references and overrides addressed as `managedReferences[id]` stay valid.
- The type that owns an override path is read from the source prefab's YAML, including objects of nested prefabs that the file doesn't list, and from the override's own `managedReferences[id]` declarations. Nothing is loaded in the old layout.
- A key stored under a `[FormerlySerializedAs]` name is renamed to the field's name. Override paths are rewritten only when they lead to a StableRef field: overrides of other renamed fields are left to Unity.
- After the rewrite the changed files are reimported and resynced with `StableRefResync.ResyncAssets` (source prefabs before their variants), so the stable IDs and snapshots are written right away.

### Scenes

Scenes are converted like any other file — the objects' fields, the data of their references and the overrides of
prefab instances. Before the migration the open scenes are closed (you are asked to save modified ones): Unity holds
them in memory in the old layout, and after the recompile it would hold them with empty fields, so saving one would
lose its data. After the data step every converted scene is opened additively, resynced, saved and closed, and the
scenes you had open are reopened from the converted files. Scenes you opened while fixing the code are handled the
same way: once the scripts compile, Unity holds them with empty fields, so right before the data step they are closed
**without saving** and reopened converted — if one has unsaved changes, you are asked first (keeping them leaves the
scene unmigrated: don't save it, close it and run *Migrate…* again). StableRef's own tools never open scenes; the
migration does, as a one-time action you confirm.
- A file already in the new layout is left alone, so running it again changes nothing.

## Limitations

- **Code that uses the fields is not rewritten.** Reading a value needs `.Value`, assigning one `.Set(value)`; `List<T>` or array members `StableRefList<T>` doesn't have (`.Length`, `AddRange`, `ToArray`…) and assigning a `List<T>` / array need replacing. The compiler lists the places; the data waits for them, see above.
- **Editor code that finds a converted field by name** (`FindProperty("effect")`) now reaches the StableRef wrapper, whose value is `effect.Value`; the dry run and the report list such places.
- **Reflection on the field's type and custom drawers of the old field** need a manual look: the field's type is the wrapper now.
- **Assets with unsaved changes are skipped** by the data step; the report lists them. From the menu you are asked whether to discard unsaved in-memory changes instead.
- **When the API is called directly** (`SerializeReferenceMigrator.Run` / `Migrate`), open scenes are not closed: they are skipped and reported, and you close them and run again. The menu handles them.
- **Only the current `[SerializeReference]` format (version 2, `rid:`) is read.** Assets last saved by Unity 2020 or older (version 1, `id:` and `00000000:` keys) are reported as unrecognized: resave them in the current Unity first.
- **Binary assets are not supported**: the migration needs *Asset Serialization: Force Text*.
- **Presets** (`.preset`) of components with converted fields are not migrated: recreate them after the migration.
- **Prefab overrides of a source the index can't read** (a binary file, a missing prefab) are left as they are.
- **Editor scripts that save assets on load** could save one between the recompile and the data step and lose its data; check the report against version control.
