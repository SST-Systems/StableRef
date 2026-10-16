# SerializeReference Migration

**English** | [Русский](README.ru.md)

This sample switches a project from plain `[SerializeReference]` fields to StableRef — the code and the saved data —
with one button. It rewrites the field declarations in your scripts, waits for them to compile, then moves the data
of every asset, prefab and scene into the new layout.

> **It is a migration helper, not a guarantee.** The code that uses the fields is fixed as text, by rules: a `.Reference`
> can be missed or put where it doesn't belong, and some such places still compile — `effect != null` would silently
> test the wrapper instead of the value. Review every changed script before committing, go through the warnings in
> the console, and test the game.

```csharp
// before
[SerializeReference, Tooltip("On hit")] public IEffect effect = new Burn();
[SerializeReference] private List<IEffect> _effects = new();

// after
[Tooltip("On hit")] public StableRef<IEffect> effect = new(new Burn());
[SerializeField] private StableRefList<IEffect> _effects = new();
```

Only the type changes: the field keeps its name, access and other attributes, so its data stays under the same key.
Code that reads the value gets `.Reference` (`effect.Reference.Apply()`, `effect.Set(new Burn())`).

Why a tool is needed: Unity can't read the old data into the new field — it loads the field empty and drops the old
data on the next save. So the migration rewrites the files themselves, before anything saves them.

It ships as a sample because it is needed once per project.

## What's inside

- `SerializeReferenceMigrator` (namespace `SST.StableRef.Migration`) — the menu and the public API (`Analyze`, `Migrate`, `Run`).
- `MigrationCodeRewriter`, `MigrationUsageFixer` — rewrite the field declarations and fix the code that uses them.
- `MigrationContinuation`, `MigrationScenes` — run the data step after the scripts compile, handle open scenes.
- `MigrationYamlConverter` and helpers — the data conversion of asset files.
- `Tests/` — EditMode tests (`SST.StableRef.Migration.Tests`).

## Use it

1. Commit or back up the project. The migration rewrites scripts and assets under `Assets/`.
2. Import the sample: **Window → Package Manager → StableRef → Samples → SerializeReference Migration → Import**.
3. **Tools → StableRef → Migrate SerializeReference Data → Analyze (Dry Run)** lists what will change. Nothing is written.
4. **Tools → StableRef → Migrate SerializeReference Data → Migrate…** You are asked to save modified scenes. The scripts are rewritten and recompiled, compile errors are fixed automatically where possible, then the data is moved. The report goes to the console.
5. **Review the changes before committing.** Go through the diff of every changed script and the warnings in the report — they are the places the migration couldn't decide on.

If compile errors remain, fix them by hand: the data step runs by itself after the next successful compile.
**Resume Pending Migration** in the same menu runs it right away, **Cancel Pending Migration** stops waiting.

Fields you already changed to StableRef by hand are picked up by the data step as well. Running the migration again
changes nothing. After that the sample can be deleted.

## What it does

### Code

- Every `[SerializeReference]` field in scripts under `Assets/` is retyped in place: `T` → `StableRef<T>`, `T[]` / `List<T>` → `StableRefList<T>`. No extra fields or properties are added.
- `[SerializeReference]` is removed from public fields and replaced with `[SerializeField]` on non-public ones. Fields get `= new()` (or their initializer is wrapped) so the wrapper is never `null`.
- Code that reads the fields gets `.Reference` — both where it would stop compiling and where it would compile but check the wrapper instead of the value (`effect != null`, `effect is Burn`, `effect.GetType()`…). Writes become `.Set(…)`.
- A use is changed only when it surely refers to the retyped field; doubtful places are reported as warnings.
- Assembly definitions get a reference to `SST.StableRef`, scripts get `using SST.StableRef;`, and value types that share a file get a `[RefTypeId]`.
- Skipped (and reported): `[RefSelector]` fields, fields of generic parameter types, declarations spanning several lines or declaring several fields.

### Data

All Unity YAML files under `Assets/` are rewritten as text: field data and prefab overrides are moved into the
StableRef layout, while the referenced objects themselves stay as they are. Then the changed assets are reimported
and get their stable IDs and snapshots.

### Scenes

Open scenes are closed before the migration (you are asked to save them) and reopened afterwards. Every converted
scene is opened, resynced and saved once. If you opened scenes while fixing compile errors, they are reopened
converted — you are asked first if they have unsaved changes.

## Limitations

- **Not every use of the fields can be fixed automatically**, e.g. `Sort` or `Array.Resize` on a former list — the remaining compile errors and warnings are listed in the console.
- **Editor code that finds a field by name** (`FindProperty("effect")`), reflection on the field's type and custom drawers of the old field need a manual look.
- **Assets with unsaved changes are skipped**; the report lists them.
- **Text serialization is required** (*Asset Serialization: Force Text*). Assets last saved in Unity 2020 or older must be resaved first.
- **Presets** (`.preset`) are not migrated — recreate them afterwards.
- **When the API is called directly**, open scenes are skipped — close them and run again. The menu handles them.
