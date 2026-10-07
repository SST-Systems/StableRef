<img src="Documentation~/banner.png" width="900" alt="StableRef">

[![release](https://img.shields.io/github/v/release/SST-Systems/StableRef)](../../releases)
[![release date](https://img.shields.io/github/release-date/SST-Systems/StableRef)](../../releases)
[![last commit](https://img.shields.io/github/last-commit/SST-Systems/StableRef)](../../commits)
[![license](https://img.shields.io/github/license/SST-Systems/StableRef)](LICENSE.md)

**English** | [Русский](README.ru.md)

---

A convenient, reliable wrapper over Unity's `[SerializeReference]`.

StableRef makes working with polymorphic serialized references stable and comfortable: a searchable typed inspector selector, safe copy/paste, and editor tooling to find and fix references. On top of that, references survive class renames — a stable string type ID is stored alongside the object, so renaming or moving a class does not break existing serialized data.

## Table Of Contents

<details>
<summary>Details</summary>

- [Installation](#installation)
- [The problem it solves](#the-problem-it-solves)
- [Classes and attributes](#classes-and-attributes)
- [Usage](#usage)
  - [Declaring a stable type](#declaring-a-stable-type)
  - [Using StableRef\<T\> in a field](#using-stablereft-in-a-field)
  - [Using StableRefList\<T\>](#using-stablereflistt)
  - [Copying owners: MemberwiseClone](#copying-owners-memberwiseclone)
  - [Reflection and serializers](#reflection-and-serializers)
  - [Generic value types](#generic-value-types)
  - [Selector without a wrapper: \[RefSelector\]](#selector-without-a-wrapper-refselector)
- [Auto-generated ID](#auto-generated-id)
  - [Types you can't edit: RefTypeIdFor](#types-you-cant-edit-reftypeidfor)
- [Samples](#samples)
- [Editor tools](#editor-tools)
- [Inspector integration](#inspector-integration)
- [Copying and pasting](#copying-and-pasting)
- [License](#license)

</details>

---

## Installation

1. **.unitypackage** — [Releases](../../releases)
2. **UPM** — `Window → Package Manager` → `+` → `Add package from git URL`:
   `https://github.com/SST-Systems/StableRef.git`
   Append `#tag` to pin a version.
3. **Manual** — clone or download, copy to `Assets/`.

Unity 2021.3+

---

## The problem it solves

Unity's built-in `[SerializeReference]` stores the full assembly-qualified type name. If you rename or move a class, Unity loses the reference and the field becomes `null`. `StableRef` decouples the serialized identity from the class name by letting you assign a permanent ID via `[RefTypeId]`.

---

## Classes and attributes

| Type | Purpose |
|---|---|
| `StableRef<T>` | Serializable wrapper holding a single polymorphic reference of type `T`. |
| `StableRefList<T>` | Serializable list of rename-safe values: an `IList<T>` of the values, each stored in its own `StableRef<T>` entry. |
| `[RefTypeId("id")]` | Assigns a permanent ID to a class. Rename the class freely — Unity will still find it. |
| `[assembly: RefTypeIdFor(typeof(T), "id")]` | The same for a type you can't edit — another package, a DLL, generated code. |
| `[RefCategory("Path")]` | Groups the type under a submenu in the inspector selector. |
| `[RefSelector]` | Editor-only: the same selector on a plain `[SerializeReference]` field, with no wrapper and no rename protection. |

---

## Usage

### Declaring a stable type

```csharp
[Serializable]
[RefTypeId("my-package.damage-on-hit")]
[RefCategory("Combat")]
public class DamageOnHit : IEffect
{
    public int Amount;
}
```

The `[RefTypeId]` value must be unique across the project. Use a namespaced string to avoid collisions. The attribute is **not inherited**: a class derived from `DamageOnHit` needs its own `[RefTypeId]` (or its own script file, see [Auto-generated ID](#auto-generated-id)) — otherwise it has no stable ID and isn't offered in the selector.

### Using StableRef\<T\> in a field

```csharp
[Serializable]
public class ItemConfig : ScriptableObject
{
    public StableRef<IEffect> OnPickup;
}

// Reading and writing
if (config.OnPickup.HasValue)
    config.OnPickup.Value.Apply();

config.OnPickup.Set(new DamageOnHit { Amount = 5 });
config.OnPickup = new StableRef<IEffect>(new DamageOnHit { Amount = 5 });
```

`Set` keeps the entry's stable ID when the new value has the same type as the old one and resets the metadata when the type changes, so no ID of another type is left behind. Writing `.Value` directly works too but leaves the metadata as it was.

There are no implicit conversions between `StableRef<T>` and `T`: C# ignores user-defined conversions to and from interfaces, and `T` is usually an interface, so they would work for some fields and not for others. Use `.Value` and the constructor.

### Using StableRefList\<T\>

```csharp
[Serializable]
public class AbilityConfig : ScriptableObject
{
    public StableRefList<IEffect> Effects;
}

// Iteration — values, no allocation
foreach (var effect in config.Effects)
    effect?.Apply();
```

`StableRefList<T>` is an `IList<T>` and `IReadOnlyList<T>` of the values themselves: indexing, `foreach` and LINQ (`OfType`, `Any`, `Select`, ...) work with `T`, and the list can be passed wherever an `IEnumerable<T>` / `IReadOnlyList<T>` is expected. `foreach` uses a struct enumerator and doesn't allocate. The `List<T>`-like API is there too — `Add`, `AddRange`, `Insert`, `Remove`, `RemoveAt`, `RemoveAll`, `Clear`, `Contains`, `IndexOf`, `Find`, `ToArray`, and so on:

```csharp
config.Effects.Add(new DamageOnHit { Amount = 5 });
config.Effects.RemoveAll(e => e is DamageOnHit);
var damage = config.Effects.OfType<DamageOnHit>().Sum(d => d.Amount);
config.Effects[0] = new Heal();
var rewards = new StableRefList<IReward>(otherRewards);
```

Each value is stored in its own `StableRef<T>` entry, which keeps the stable ID and the snapshot; `Items` exposes those entries (`List<StableRef<T>>`). Assigning through the indexer reuses the entry like `Set`: a value of the same type keeps the stable ID, a value of another type resets it.

When you build a list from an **editor script** rather than the inspector, call `StableRefResync.ResyncObject(asset)` before saving so the new entries receive their stable IDs and snapshots (the inspector does this automatically when a field is drawn) — see [Reflection and serializers](#reflection-and-serializers).

<p align="center">
  <img src="Documentation~/inspector.gif" alt="Adding a type via the typed dropdown" width="580">
</p>

### Copying owners: MemberwiseClone

`StableRef<T>` and `StableRefList<T>` are classes. An owner cloned with `MemberwiseClone` shares them with the original, so `clone.OnPickup.Value = x` changes the original too — unlike a bare `[SerializeReference] T` field, where the same line only changes the clone. Give the clone its own wrappers:

```csharp
public Effect ShallowClone()
{
    var clone = (Effect)MemberwiseClone();
    clone.Modifier = Modifier.ShallowCopy();   // StableRef<T>: new wrapper, same value instance
    clone.Children = Children.ShallowCopy();   // StableRefList<T>: new entries, same value instances
    return clone;
}
```

Deep copies of the values are up to you: `JsonUtility` doesn't follow `[SerializeReference]`.

### Reflection and serializers

Code that only knows a field's `Type` at run time — reflection-based config loaders, custom serializers — can read and write StableRef fields without the generic argument:

```csharp
if (StableRefReflection.IsStableRef(field.FieldType, out var valueType))
{
    var entry = (StableRefBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    entry.BoxedValue = Convert(cell, valueType);          // type-checked, throws ArgumentException on mismatch
    field.SetValue(owner, entry);
}
else if (StableRefReflection.IsStableRefList(field.FieldType, out var elementType))
{
    var list = (StableRefListBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    list.SetBoxedValues(ConvertArray(cell, elementType)); // checks every element before changing the list
    field.SetValue(owner, list);
}
```

`StableRefBase` has `ValueBaseType`, `BoxedValue`, `HasValue`; `StableRefListBase` has `ElementType`, `Count`, `GetBoxed`, `SetBoxed`, `AddBoxed`, `SetBoxedValues`, `Clear`. `field.SetValue(owner, value)` with a bare value doesn't work — reflection never applies conversions.

`BoxedValue`, `SetBoxed` and `SetBoxedValues` reuse entries like `Set` (`SetBoxedValues` by index), so writing a config whose types didn't change keeps the stable IDs.

**Writing into assets in the editor.** Code can't capture the snapshot, and for a value of a new type it can't stamp the ID either — the inspector does that when the field is drawn. A tool that writes values into assets in the editor (a config importer, a baker, a reserialize tool) should call `StableRefResync.ResyncObject(asset)` on every changed object before saving, or run **Tools → StableRef → Resync All** afterwards (see [Editor tools](#editor-tools)). Otherwise the snapshot keeps describing the previous values, and recovery after a class rename would bring those back.

**Newtonsoft.Json:** a ready converter ships as a sample — [Newtonsoft.Json Converter](Samples~/NewtonsoftJson/README.md).

### Generic value types

The selector also supports closed generic element types. For a field like `StableRefList<ICondition<Unit>>`, open generic definitions that satisfy it (e.g. `All<TContext>`, `Any<TContext>`) are offered and closed with the field's own argument (`All<Unit>`). Each type used as a generic argument needs its own stable ID — its own file, or `[RefTypeId]` — just like any other StableRef type.

### Selector without a wrapper: [RefSelector]

When you want only the inspector selector — no wrapper type, no stored id, nothing extra in serialized data or in the build — mark a plain `[SerializeReference]` field with `[RefSelector]`:

```csharp
using SST.StableRef;

public class EffectAuthoring : MonoBehaviour
{
    [SerializeReference, RefSelector] private IEffect _onPickup;
    [SerializeReference, RefSelector] private List<IEffect> _effects;
}
```

The field stays an ordinary `[SerializeReference]`: code reads `_onPickup` directly (no `.Value`), and the attribute is `[Conditional("UNITY_EDITOR")]`, so it is not even emitted into player builds. This is useful for ECS authoring/baking code, for existing `[SerializeReference]` fields you don't want to migrate, and for large lists where per-entry metadata isn't worth it. The selector offers every instantiable type, including types without a stable id; copy/paste from the context menu works as usual.

> **Use at your own risk.** `[RefSelector]` fields have **no rename protection**: renaming or moving the value's class breaks the reference exactly as with a bare `[SerializeReference]`. They are deliberately **not** covered by **Find Usages** or **Fix Missing Types**. A field whose class is gone simply shows an empty selector (`None`) and can be replaced without confirmation. To rename a class safely, add Unity's `[MovedFrom]` (`UnityEngine.Scripting.APIUpdating`) to it. If you need refactor-proof, tracked references, use `StableRef<T>`.

---

## Auto-generated ID

`[RefTypeId]` is optional. If omitted, StableRef automatically uses the **MonoScript GUID** (the `guid` value from the `.meta` file) as the stable identifier. This means:

- **Class rename** — safe. The GUID is tied to the file, not the class name.
- **Script file rename or move** — also safe. Unity's meta file travels with the asset and its GUID does not change.
- **Deleting and recreating the file** — the reference is lost (resolves to `null`), but handled gracefully. The project continues to work; the missing type will appear in the Fix Missing Types report.

For types you plan to refactor heavily, an explicit `[RefTypeId]` is more reliable since it survives even if the script file is deleted and re-created.

Switching a type from an auto-generated ID to an explicit `[RefTypeId]` is safe and does **not** create missing references. The explicit ID takes priority, and existing references migrate automatically — the stored ID is rewritten from the MonoScript GUID to your custom ID the next time the field is drawn in the inspector, or for all assets at once with **Tools → StableRef → Resync All**. Until then the old GUID still resolves (the script file is unchanged), so nothing goes missing. If you're adding the attribute specifically to prepare for a heavy refactor (deleting and recreating the file), run Resync All first so the new ID is locked in.

> **Important:** don't put multiple classes in a single script file. Automatic ID generation relies on the MonoScript GUID, which is assigned to the file rather than the class — with multiple classes per file, ID generation will not work correctly.

### Types you can't edit: RefTypeIdFor

A type from another package, a precompiled DLL or a source generator can't take `[RefTypeId]`, and often has no MonoScript of its own. Give it an ID at assembly level, in any assembly of your project:

```csharp
using SST.StableRef;

[assembly: RefTypeIdFor(typeof(ThirdParty.Effects.Burn), "my-game.third-party.burn")]
[assembly: RefTypeIdFor(typeof(ThirdParty.Conditions.All<>), "my-game.third-party.all")]
```

- **Priority:** `[RefTypeId]` on the type → `RefTypeIdFor` → MonoScript GUID. Mapping a type that already has `[RefTypeId]` is an error; the attribute wins.
- **Generics:** map the open definition (`All<>`); closed types get a composite ID from the definition and their arguments, as usual.
- **Uniqueness:** the same rules as for `[RefTypeId]`. An ID used twice (by mappings, or by a mapping and a `[RefTypeId]`), or a type mapped to two IDs, is logged as an error on load.
- Switching such a type from its GUID to a mapped ID is safe, exactly like adding `[RefTypeId]` (see above).
- The attribute is editor-only metadata (`[Conditional("UNITY_EDITOR")]`); nothing reaches player builds.

For a source-generated `partial` class you can also put `[RefTypeId]` on your own `partial` declaration of it.

---

## Samples

Import via **Window → Package Manager → StableRef → Samples**. Each sample has its own README with the setup and a description of how it works.

| Sample | What it shows |
|---|---|
| [Newtonsoft.Json Converter](Samples~/NewtonsoftJson/README.md) | `StableRef<T>` / `StableRefList<T>` fields read and write exactly the JSON of bare `T` / `List<T>` fields — for remote configs, saves and server payloads; JSON written without the converter stays readable. Requires Newtonsoft.Json; includes EditMode tests. |

---

## Editor tools

All tools are available under **Tools → StableRef** in the Unity menu bar.

**What the tools scan:** prefabs and ScriptableObject assets under `Assets/` — including ScriptableObjects stored as sub-assets inside another asset file (graph nodes, Timeline clips, `StateMachineBehaviour`s), which are listed under their file in the *Scriptable Objects* group as `Name (Type)` — and the scenes that are already open. Packages and closed scenes are not scanned.

**Find Usages** (`Tools/StableRef/Find Usages`) — scans prefabs, active scenes, and scriptable objects to show every place a selected type is used. Also accessible via right-click on a script asset: `Assets/Find StableRef Usages`.

<p align="center">
  <img src="Documentation~/find-usages.gif" alt="Find Usages window" width="640">
</p>

**Fix Missing Types** (`Tools/StableRef/Fix Missing Types`) — scans the project for StableRef fields that contain an ID that no longer maps to any known type. Useful after a refactor to find broken references before they become silent data loss.

<p align="center">
  <img src="Documentation~/fix-missing.gif" alt="Fix Missing Types window" width="560">
</p>

**Resync All** (`Tools/StableRef/Resync All`) — brings the stable ID and snapshot of every StableRef entry in ScriptableObjects and prefabs under `Assets/` and in the open scenes in line with its current value. The inspector does this while you edit; run Resync All after values were written by code — importers, generators, `field.Value = ...` in editor scripts — so a later rename of their classes can be recovered (an entry without an ID can't be, and one with the ID of a previous value would be recovered as the wrong type). Values are never changed and missing entries are left to Fix Missing Types. Changed assets are saved; changed scenes are marked dirty. In prefab instances and variants only overridden entries are touched, so no overrides are created for metadata. The console lists value types that still have no stable ID. From code: `StableRefResync.ResyncObject(target)` / `StableRefEntry.Refresh(entry)`.

**Metadata Size Report** (`Tools/StableRef/Metadata Size Report`) — estimates the size of the StableRef metadata: what reaches player builds (the stable IDs — serialized bytes and managed memory once loaded) and what stays in the assets only (display names and snapshots), per entry and for the largest assets. It is computed from Unity's binary format (a string is 4 bytes of length plus UTF-8 aligned to 4, an object reference 12 bytes; on the managed heap a string is about 22 + 2 bytes per character), not measured from a build, and is printed to the console.

**Metadata in builds.** Only the stable ID (`TypeId`) and the value go into player builds. The display name and the snapshot (`TypeDisplayName`, `ObjectRefs`, `ObjectRefPaths`, `ValuesData`) are editor-only fields: they are kept in your assets for recovery, copy/paste and these tools, and Unity leaves them out of builds, so they cost no build size or memory. Code that reads or writes them must be inside `#if UNITY_EDITOR`. Rebuild AssetBundles built with `DisableWriteTypeTree` together with the player, as with any change of serialized fields.

**What recovery restores.** Each entry keeps a snapshot of its value next to the managed reference. After a rename or re-creation, recovery restores nested structs, arrays and lists, hidden serialized fields, `UnityEngine.Object` references anywhere in the value, and StableRef entries nested inside it (fixed in passes). Not captured — these come back as the new instance's defaults: `AnimationCurve`, `Gradient`, `Hash128`, `ExposedReference`, fixed buffers.

Entries whose ID cannot be resolved are **skipped and kept** by Fix All (restore the type or its `[RefTypeId]` and re-run); discard one deliberately by picking None (or another type) in its selector, **Set to None** in its right-click menu, or deleting the list element. A missing entry never locks its field or its `StableRefList`; replacing it asks for confirmation first, since a class can also be missing just for a moment (compile errors, switching branches).

---

## Inspector integration

For projects with their own inspector framework (IMGUI):

- **Drawing the fields of values.** By default the fields inside a StableRef value are drawn with `EditorGUI.PropertyField`. Implement `IStableRefChildrenDrawer` and assign it to `StableRefDrawing.ChildrenDrawer` (from an `[InitializeOnLoad]` type) to route them through your framework, so its attributes work inside StableRef values too. It is called only for an expanded field holding a value of a single type; the selector, missing entries, multi-object editing and the context menu stay with StableRef. When the height of what your drawer draws changes because of its own state (foldouts, tabs, conditional fields), call `StableRefDrawing.InvalidateLayout()` — StableRef lists cache their height and would otherwise overlap until the selection changes. Edit values through `EditorGUI` controls on the property, so `GUI.changed` is set — StableRef refreshes the entry's recovery snapshot on that signal; a value written to the object directly keeps the old snapshot until the next change or Resync.
- **Type names, categories, tooltips, colors and order in the selector.** Implement `IRefTypeMetadataProvider` (parameterless constructor, found automatically) to map your own attributes. Providers are asked in `Order`; empty fields fall back to the type name and `[RefCategory]`. `SortOrder` orders types within a category (lower first, then by name).
- **Code that finds fields by name.** `StableRefEditorUtility.GetValueProperty(property)` returns the managed reference of both a `StableRef<T>` (its `Value`) and a plain `[SerializeReference]` field, so a drawer that reads `managedReferenceValue` keeps working when a field becomes a `StableRef<T>`.

```csharp
[InitializeOnLoad]
static class MyInspectorStableRefBridge
{
    static MyInspectorStableRefBridge() => StableRefDrawing.ChildrenDrawer = new MyChildrenDrawer();
}

sealed class MyChildrenDrawer : IStableRefChildrenDrawer
{
    public float GetChildrenHeight(SerializedProperty value) => MyInspector.GetChildrenHeight(value);
    public void DrawChildren(Rect position, SerializedProperty value) => MyInspector.DrawChildren(position, value);
}

// Runtime assembly — your own attribute on the value types
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EffectInfoAttribute : Attribute
{
    public readonly string Name;
    public readonly string Category;
    public string Tooltip;
    public int Priority;

    public EffectInfoAttribute(string name, string category = null)
    {
        Name = name;
        Category = category;
    }
}

[Serializable, RefTypeId("my-game.burn")]
[EffectInfo("Burn", "Damage/Over Time", Tooltip = "Deals damage every second", Priority = 10)]
public class Burn : IEffect { public float DamagePerSecond; }

// Editor assembly — map it to the selector
sealed class EffectInfoMetadata : IRefTypeMetadataProvider
{
    public int Order => 0;

    public bool TryGetMetadata(Type type, out RefTypeMetadata metadata)
    {
        var info = type.GetCustomAttribute<EffectInfoAttribute>();
        metadata = info == null
            ? default
            : new RefTypeMetadata
            {
                DisplayName = info.Name,
                Category = info.Category,
                Tooltip = info.Tooltip,
                SortOrder = -info.Priority   // higher priority first
            };
        return info != null;
    }
}
```

---

## Copying and pasting

Unity's built-in **Copy Component** / **Paste Component Values** does not reliably handle `[SerializeReference]` data across different serialized documents (e.g. scene → prefab). It can leave behind a corrupted `managedReferences` entry, producing a console error like:

```
Could not update a managed instance value at property path 'managedReferences[...]', with value '...'
```

This error can persist across editor restarts and does **not** go away by reverting the component, because the corruption is already baked into the serialized file.

To move `StableRef<T>` / `StableRefList<T>` values around safely, use the built-in right-click menu instead of Unity's native component copy/paste. Right-clicking a list element opens the menu of **that element** (in a `StableRefList<T>` anywhere on its row); right-clicking the list header opens the menu of the whole list. The same element commands work in `List<StableRef<T>>`, `StableRef<T>[]` and `[RefSelector]` lists — there Unity draws the rows itself, so right-click the element's field:

| Menu item | Where to right-click | What it does |
|---|---|---|
| `StableRef/Copy` | A single `StableRef<T>` field | Copies the current value to an internal clipboard. |
| `StableRef/Paste` | A single `StableRef<T>` field of a compatible type | Creates a fresh managed reference in the target field. |
| `Paste as New Element` | A list element (in `StableRefList<T>`: any part of its row — type button, drag handle, row padding) | Inserts the copied value as a new element right after it. |
| `Duplicate Array Element` | A list element | Inserts a deep copy right after that element (Unity's own Duplicate would share the same managed reference between both elements). |
| `Delete Array Element` | A list element | Removes that element. |
| `StableRef/Copy` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Copies all entries in the list. |
| `StableRef/Paste/Replace` or `StableRef/Paste/Append` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Replaces or appends the copied entries. |

This is safe across GameObjects, prefabs, and scenes: instead of copying raw serialized bytes, it rebuilds a brand-new managed reference directly in the destination document, so it never corrupts `managedReferences`.

When copying a component that contains `StableRef` fields between a scene and a prefab, use this menu for the StableRef fields specifically rather than Unity's native Copy Component / Paste Component Values.

> **Warning:** even with this menu available, stay cautious. `[SerializeReference]`-based fields (including `StableRef`/`StableRefList`) don't always copy or move as expected, even during trivial built-in Unity operations — Duplicate, drag & drop in the Hierarchy, applying/reverting prefab overrides, scene/prefab merges, and similar actions. Commit or back up your work before bulk changes, and double-check the result afterward.

---

## License

Distributed under the [MIT License](LICENSE.md). Free for personal and commercial use.

Author — **Egor Shesterikov**.
