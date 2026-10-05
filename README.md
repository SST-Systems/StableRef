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
  - [Using StableRef\<T\> in a field](#using-stablefrt-in-a-field)
  - [Using StableRefList\<T\>](#using-stablereflistt)
  - [Generic value types](#generic-value-types)
  - [Selector without a wrapper: \[RefSelector\]](#selector-without-a-wrapper-refselector)
  - [Keyboard navigation in the selector](#keyboard-navigation-in-the-selector)
- [Auto-generated ID](#auto-generated-id)
- [Editor tools](#editor-tools)
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
| `StableRefList<T>` | Serializable list of `StableRef<T>` items. |
| `[RefTypeId("id")]` | Assigns a permanent ID to a class. Rename the class freely — Unity will still find it. |
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

The `[RefTypeId]` value must be unique across the project. Use a namespaced string to avoid collisions.

### Using StableRef\<T\> in a field

```csharp
[Serializable]
public class ItemConfig : ScriptableObject
{
    public StableRef<IEffect> OnPickup;
}
```

### Using StableRefList\<T\>

```csharp
[Serializable]
public class AbilityConfig : ScriptableObject
{
    public StableRefList<IEffect> Effects;
}

// Iteration
foreach (var stableRef in config.Effects)
{
    var effect = stableRef?.Value;
    if (effect != null)
        effect.Apply();
}
```

`StableRefList<T>` also works like a `List<T>` from code — `Add`, `Insert`, `Remove`, `RemoveAt`, `RemoveAll`, `Clear`, `Contains`, `IndexOf`, `Find`, and so on:

```csharp
config.Effects.Add(new DamageOnHit { Amount = 5 });
config.Effects.RemoveAll(e => e is DamageOnHit);
```

Indexing and `foreach` yield the `StableRef<T>` wrapper (read the value via `.Value`); the query and mutation helpers work with `T` directly. `Items` exposes the underlying `List<StableRef<T>>` for the full `List` API.

When you build a list from an **editor script** rather than the inspector, call `StableRefSync.AssignIds(list)` before saving so the new entries receive their stable IDs (the inspector does this automatically when a field is drawn).

<p align="center">
  <img src="Documentation~/inspector.gif" alt="Adding a type via the typed dropdown" width="580">
</p>

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

### Keyboard navigation in the selector

The selector can be driven without the mouse; the arrows work like in Unity's Hierarchy and Project windows. It opens with the current type selected, and the search field keeps focus, so you can type to filter at any time.

| Key | Action |
|---|---|
| `↑` / `↓`, `PageUp` / `PageDown` | Move the selection over types and categories |
| `→` | Expand the selected collapsed category; otherwise jump down to the next category |
| `←` | Collapse the selected expanded category; otherwise jump to the parent category (at the top level — to the previous one) |
| `Alt` + `→` / `←` | Expand / collapse the category with everything below it |
| `Enter` | Pick the selected type (or toggle the selected category) |
| `Home` / `End` | First / last row |
| `Esc` | Close without changes |

While a search is typed the list is flat: `Enter` picks the highlighted match (the first one by default), and `←` / `→` / `Home` / `End` edit the search text.

---

## Auto-generated ID

`[RefTypeId]` is optional. If omitted, StableRef automatically uses the **MonoScript GUID** (the `guid` value from the `.meta` file) as the stable identifier. This means:

- **Class rename** — safe. The GUID is tied to the file, not the class name.
- **Script file rename or move** — also safe. Unity's meta file travels with the asset and its GUID does not change.
- **Deleting and recreating the file** — the reference is lost (resolves to `null`), but handled gracefully. The project continues to work; the missing type will appear in the Fix Missing Types report.

For types you plan to refactor heavily, an explicit `[RefTypeId]` is more reliable since it survives even if the script file is deleted and re-created.

Switching a type from an auto-generated ID to an explicit `[RefTypeId]` is safe and does **not** create missing references. The explicit ID takes priority, and existing references migrate automatically — the stored ID is rewritten from the MonoScript GUID to your custom ID the next time the field is drawn in the inspector (or when you call `StableRefSync`). Until then the old GUID still resolves (the script file is unchanged), so nothing goes missing. If you're adding the attribute specifically to prepare for a heavy refactor (deleting and recreating the file), re-save the affected assets first so the new ID is locked in.

> **Important:** don't put multiple classes in a single script file. Automatic ID generation relies on the MonoScript GUID, which is assigned to the file rather than the class — with multiple classes per file, ID generation will not work correctly.

---

## Editor tools

All tools are available under **Tools → StableRef** in the Unity menu bar.

**Find Usages** (`Tools/StableRef/Find Usages`) — scans prefabs, active scenes, and scriptable objects to show every place a selected type is used. Also accessible via right-click on a script asset: `Assets/Find StableRef Usages`.

<p align="center">
  <img src="Documentation~/find-usages.gif" alt="Find Usages window" width="640">
</p>

**Fix Missing Types** (`Tools/StableRef/Fix Missing Types`) — scans the project for StableRef fields that contain an ID that no longer maps to any known type. Useful after a refactor to find broken references before they become silent data loss.

<p align="center">
  <img src="Documentation~/fix-missing.gif" alt="Fix Missing Types window" width="560">
</p>

**What recovery restores.** Each entry keeps a snapshot of its value next to the managed reference. After a rename or re-creation, recovery restores nested structs, arrays and lists, hidden serialized fields, `UnityEngine.Object` references anywhere in the value, and StableRef entries nested inside it (fixed in passes). Not captured — these come back as the new instance's defaults: `AnimationCurve`, `Gradient`, `Hash128`, `ExposedReference`, fixed buffers.

Entries whose ID cannot be resolved are **skipped and kept** by Fix All (restore the type or its `[RefTypeId]` and re-run); discard one deliberately by picking None (or another type) in its selector, **Set to None** in its right-click menu, or deleting the list element. A missing entry never locks its field or its `StableRefList`; replacing it asks for confirmation first, since a class can also be missing just for a moment (compile errors, switching branches).

---

## Copying and pasting

Unity's built-in **Copy Component** / **Paste Component Values** does not reliably handle `[SerializeReference]` data across different serialized documents (e.g. scene → prefab). It can leave behind a corrupted `managedReferences` entry, producing a console error like:

```
Could not update a managed instance value at property path 'managedReferences[...]', with value '...'
```

This error can persist across editor restarts and does **not** go away by reverting the component, because the corruption is already baked into the serialized file.

To move `StableRef<T>` / `StableRefList<T>` values around safely, use the built-in right-click menu instead of Unity's native component copy/paste. Right-clicking a list element (anywhere on its row) opens the menu of **that element**; right-clicking the list header opens the menu of the whole list. The same element commands work in `List<StableRef<T>>`, `StableRef<T>[]` and `[RefSelector]` lists:

| Menu item | Where to right-click | What it does |
|---|---|---|
| `StableRef/Copy` | A single `StableRef<T>` field | Copies the current value to an internal clipboard. |
| `StableRef/Paste` | A single `StableRef<T>` field of a compatible type | Creates a fresh managed reference in the target field. |
| `Paste as New Element` | Any part of a list element (type button, drag handle, row padding) | Inserts the copied value as a new element right after it. |
| `Duplicate Array Element` | Any part of a list element | Inserts a deep copy right after that element (Unity's own Duplicate would share the same managed reference between both elements). |
| `Delete Array Element` | Any part of a list element | Removes that element. |
| `StableRef/Copy` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Copies all entries in the list. |
| `StableRef/Paste/Replace` or `StableRef/Paste/Append` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Replaces or appends the copied entries. |

This is safe across GameObjects, prefabs, and scenes: instead of copying raw serialized bytes, it rebuilds a brand-new managed reference directly in the destination document, so it never corrupts `managedReferences`.

When copying a component that contains `StableRef` fields between a scene and a prefab, use this menu for the StableRef fields specifically rather than Unity's native Copy Component / Paste Component Values.

> **Warning:** even with this menu available, stay cautious. `[SerializeReference]`-based fields (including `StableRef`/`StableRefList`) don't always copy or move as expected, even during trivial built-in Unity operations — Duplicate, drag & drop in the Hierarchy, applying/reverting prefab overrides, scene/prefab merges, and similar actions. Commit or back up your work before bulk changes, and double-check the result afterward.

---

## License

Distributed under the [MIT License](LICENSE.md). Free for personal and commercial use.

Author — **Egor Shesterikov**.
