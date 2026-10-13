# SerializeReference Migration

**English** | [Русский](README.ru.md)

This sample switches a project from plain `[SerializeReference]` fields to StableRef — the code and the saved data —
with one button. It rewrites the field declarations in your scripts, waits for them to compile, moves the data of
every asset into the new layout, follows prefab overrides and stamps every entry's stable ID and snapshot.

> **It is a migration helper, not a guarantee.** The code that uses the fields is fixed as text, by rules: a `.Value`
> can be missed or put where it doesn't belong, and some such places still compile — a check like `effect != null`
> silently tests the wrapper instead of the value, and the game behaves differently without any error. Review every
> changed script in version control before committing, go through the warnings in the console, and test the game.

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
reads the value now needs `.Value` (`effect.Value.Apply()`, `effect.Set(new Burn())`): the migration adds it where
the compiler reports an error, and where the code would compile but test the wrapper instead of the value
(`effect != null`); what it can't fix is listed, and the data waits until the scripts compile.

Why a tool is needed: Unity can't read the old data into the new field. It loads the field empty, keeps the old
reference records only in the file, and drops them the first time the asset is saved. So the migration works on the
files themselves, before anything saves them.

It ships as a sample rather than as part of the package: it is needed once per project, while switching.

## What's inside

- `SerializeReferenceMigrator` (namespace `SST.StableRef.Migration`) — the menu and the public API (`Analyze`, `Migrate`, `Run`).
- `MigrationCodeRewriter` — retypes the declarations, adds `SST.StableRef` to assembly definitions and `[RefTypeId]` to value types without a stable ID.
- `MigrationUsageFixer` — fixes the code that uses the retyped fields, as text: by the compiler's messages, and the uses the compiler accepts but that now test the wrapper.
- `MigrationContinuation` — moves the data after the rewritten scripts compile.
- `MigrationScenes` — closes and reopens the open scenes, resyncs the converted ones.
- `MigrationYamlConverter`, `MigrationAssetIndex`, `MigrationFieldModel` — the text rewrite of one asset file, the types read from YAML, the fields that are StableRef.
- `SST.StableRef.Migration.Editor` — the editor assembly they live in.
- `Tests/` — EditMode tests (`SST.StableRef.Migration.Tests`, with test types in `SST.StableRef.Migration.TestTypes`): declaration rewrites, fixes of the uses, every kind of field in assets, a dry run, prefab variants, overrides of objects inside nested prefabs.

## Use it

1. Commit or back up the project. The migration rewrites scripts and assets under `Assets/`.
2. Import the sample: **Window → Package Manager → StableRef → Samples → SerializeReference Migration → Import**. If you installed StableRef by copying it into `Assets/`, copy the `Samples~/SerializeReferenceMigration` folder from the repository instead.
3. **Tools → StableRef → Migrate SerializeReference Data → Analyze (Dry Run)** lists the fields that will be rewritten, the uses that would test the wrapper and will get `.Value`, the `[RefTypeId]` and assembly references that will be added, and warnings. Nothing is written.
4. **Tools → StableRef → Migrate SerializeReference Data → Migrate…** You are asked to save modified scenes; open scenes are closed for the migration. The field types in the scripts are changed and Unity recompiles; the code that uses the fields is fixed by the compiler's messages, round after round. Once the scripts compile, the data is moved, every converted scene is opened, resynced and saved, and the scenes you had open are reopened. The report goes to the console.
5. **Review the changes before committing.** Go through the diff of every changed script: each added `.Value`, `.Set(…)`, `new(…)`, `.ToArray()` — and the uses of the retyped fields the migration didn't touch (search the field names). Check the warnings in the report: they are the places it couldn't decide on. A missed or misplaced `.Value` may still compile and only show up as wrong behaviour at runtime.

The compile errors after the rewrite are the code that uses the fields. After each compile the migration fixes the ones it can read — reading a single reference gets `.Value`, assigning one `.Set(value)` (`.Value =` when the value spans several lines), an object-initializer member `= new(value)`, a list's `.Length` becomes `.Count`, a list read as an array `.ToArray()` — and recompiles, until the scripts compile or a round fixes nothing. Each round also reaches the assemblies that depend on the fixed ones (they aren't compiled while their dependencies fail). An assembly definition under `Assets/` that now touches a StableRef type without referencing `SST.StableRef` (`CS0012`) gets the reference. What is left is listed in the console — fix it by hand. Until then the old types stay loaded and the data stays readable; the data step runs by itself after the next successful compile, once the rewritten scripts are loaded (an editor started with compile errors and *Ignore* in the Safe Mode dialog runs the old scripts, so the data keeps waiting). A field you changed back by hand doesn't hold it up: its data is left as is and the report lists it. **Resume Pending Migration** in the same menu runs the data step right away, **Cancel Pending Migration** stops waiting.

Fields you already changed to `StableRef<T>` / `StableRefList<T>` by hand (same name, or with `[FormerlySerializedAs]`) are picked up by the data step as well.

After that the sample can be deleted.

To run the tests, open **Window → General → Test Runner → EditMode** and run `SST.StableRef.Migration.Tests`.

## What it does

### Code

- Every `[SerializeReference]` field declared in a script under `Assets/` is retyped in place (scripts of packages are not touched — their assets would stay in the old layout): `T` → `StableRef<T>`, `T[]` / `List<T>` → `StableRefList<T>`. The name, the access modifiers and the other attributes stay; no field, property or `[FormerlySerializedAs]` is added.
- `[SerializeReference]` is removed from a public field and replaced with `[SerializeField]` on a non-public one (unless it already has it). `[field: SerializeReference]` auto-properties get `[field: SerializeField]` and keep their accessors. Attributes of plain references and array drawers (`[SubclassSelector]`, `[ListDrawerSettings]`…) are dropped. An initializer is wrapped (`= new Burn()` → `= new(new Burn())`). A field without an initializer gets `= new()` — otherwise an object made in code (with `new`, and a component or ScriptableObject made with `AddComponent` / `CreateInstance` before it is serialized) would have a null wrapper and `.Value` would throw; for a loaded asset Unity overwrites the field anyway. Only struct fields stay without one (no field initializers in C# 9).
- `SST.StableRef` is added to the assembly definitions of the changed scripts, and `using SST.StableRef;` to the scripts.
- Every read of a retyped field gets `.Value` before the first compile — the ones that would stop compiling (`effect.Apply()`, `effect?.Power`, `IEffect e = effect`, `return effect`, `cond ? effect : null`, `Use(effect)`, a call of an extension method of the value) and the ones that would compile but test or read the wrapper: `effect != null`, `null == effect`, `effect is null` / `is { }` / `is Burn`, `effect as Burn`, `effect = null`, `effect ??= …`, `ReferenceEquals(effect, …)`, `effect.GetType()`, `effect.Equals(…)`, `effect.GetHashCode()`, `effect == other` / `other != effect`, `effect ?? other`, `effect switch { … }`, `switch (effect)`, `effect` passed to a parameter of type `object` or a type parameter (`Inject(object)`, `Open(type, object payload)`, `Foo<T>(T value)`, constructors too), and `effect.Value` / `effect.HasValue` when the value type has a member of that name — `effect.Value` would now be the wrapper's value, not the value's own `Value` (it becomes `effect.Value.Value`). Left alone: writes (`effect = …`, which the next step turns into `.Set(…)` by the compiler's messages), the wrapper's own members (`effect.Set(…)`, `effect.TypeId`), a parameter that takes a StableRef, `nameof(effect)`, `ref` / `out` arguments, named arguments (`effect: …`), a type named like the field (`AdCost AdCost`). A local initialized from the field (`var r = o.effect;`) gets the value. A property pattern on the field's type matches the value (`o is { effect: null }` → `o is { effect: { Value: null } }` — C# 9 has no `effect.Value:`), and an anonymous type keeps the member's name (`new { o.effect }` → `new { effect = o.effect.Value }`).
- A use is changed only when the name surely means the field. A local or parameter of the same name hides the field in its own scope only (method, block, lambda, `foreach`). The type of what precedes the name (`x` in `x.effect`, the enclosing class for `effect` / `this.effect`) is read from the code and checked against the loaded (still old) types: a declaration (`Owner x`, a parameter, `out Owner x`, `is Owner x`), `var x = …` (a chain, `new`, `as`, a cast), a `foreach` element, a deconstruction (`var (a, b) = …`, tuples, `KeyValuePair`, `Deconstruct`), a LINQ lambda parameter (`Where(x => …)`, `Any`, `First`, `Aggregate`…), members and calls along a chain (base classes included, generic bases such as `Data` of `Processor<TData>`, `OfType<T>()`, indexers), static types. The field counts as retyped only when that type (or its base) declares it — fields of the same name in other types are left alone. Where the type can't be read, the former rule applies: `effect` / `this.effect` in the scripts of the declaring class, `x.effect` when no other field or property of any type has that name; other such uses are listed as warnings (as are calls whose parameter can't be found). Lists are left alone: a `StableRefList<T>` is never null, which is what such checks guarded; a single field named like a list of another type is told apart by its type.
- Assembly definitions that reference an assembly whose public API has StableRef types — a package's, or one with retyped public fields — but not `SST.StableRef` are listed: code there that uses those members fails with `CS0012`.
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

- **Uses of the fields are fixed only where they can be read reliably.** Lists of the remaining compile errors go to the console: e.g. `List<T>` / array members `StableRefList<T>` doesn't have (`Sort`, `ConvertAll`, `Array.Resize`), a value passed where the method now needs the wrapper, or a multi-line value in an object initializer. Silent uses (`!= null`, `is`, passing to `object`) are changed only where the name surely means the field; where the receiver's type can't be read from the text (an expression beyond a chain of members and calls, a tuple with named elements, an extension method, a call whose parameter can't be found) the use is listed as a warning — check them.
- **Editor code that finds a converted field by name** (`FindProperty("effect")`) now reaches the StableRef wrapper, whose value is `effect.Value`; the dry run and the report list such places.
- **Reflection on the field's type and custom drawers of the old field** need a manual look: the field's type is the wrapper now.
- **Assets with unsaved changes are skipped** by the data step; the report lists them. From the menu you are asked whether to discard unsaved in-memory changes instead.
- **When the API is called directly** (`SerializeReferenceMigrator.Run` / `Migrate`), open scenes are not closed: they are skipped and reported, and you close them and run again. The menu handles them.
- **Only the current `[SerializeReference]` format (version 2, `rid:`) is read.** Assets last saved by Unity 2020 or older (version 1, `id:` and `00000000:` keys) are reported as unrecognized: resave them in the current Unity first.
- **Binary assets are not supported**: the migration needs *Asset Serialization: Force Text*.
- **Presets** (`.preset`) of components with converted fields are not migrated: recreate them after the migration.
- **Prefab overrides of a source the index can't read** (a binary file, a missing prefab) are left as they are.
- **Editor scripts that save assets on load** could save one between the recompile and the data step and lose its data; check the report against version control.
