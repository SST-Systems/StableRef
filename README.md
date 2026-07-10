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

Unity's built-in `[SerializeReference]` stores the full assembly-qualified type name. If you rename or move a class, Unity loses the reference and the field becomes `null`. `StableRef` decouples the serialized identity from the class name by letting you assign a permanent ID via `[StableTypeId]`.

---

## Classes and attributes

| Type | Purpose |
|---|---|
| `StableRef<T>` | Serializable wrapper holding a single polymorphic reference of type `T`. |
| `StableRefList<T>` | Serializable list of `StableRef<T>` items. |
| `[StableTypeId("id")]` | Assigns a permanent ID to a class. Rename the class freely — Unity will still find it. |
| `[StableRefCategory("Path")]` | Groups the type under a submenu in the inspector selector. |

---

## Usage

### Declaring a stable type

```csharp
[Serializable]
[StableTypeId("my-package.damage-on-hit")]
[StableRefCategory("Combat")]
public class DamageOnHit : IEffect
{
    public int Amount;
}
```

The `[StableTypeId]` value must be unique across the project. Use a namespaced string to avoid collisions.

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

<p align="center">
  <img src="Documentation~/inspector.gif" alt="Adding a type via the typed dropdown" width="580">
</p>

---

## Auto-generated ID

`[StableTypeId]` is optional. If omitted, StableRef automatically uses the **MonoScript GUID** (the `guid` value from the `.meta` file) as the stable identifier. This means:

- **Class rename** — safe. The GUID is tied to the file, not the class name.
- **Script file rename or move** — also safe. Unity's meta file travels with the asset and its GUID does not change.
- **Deleting and recreating the file** — the reference is lost (resolves to `null`), but handled gracefully. The project continues to work; the missing type will appear in the Fix Missing Types report.

For types you plan to refactor heavily, an explicit `[StableTypeId]` is more reliable since it survives even if the script file is deleted and re-created.

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

---

## Copying and pasting

Unity's built-in **Copy Component** / **Paste Component Values** does not reliably handle `[SerializeReference]` data across different serialized documents (e.g. scene → prefab). It can leave behind a corrupted `managedReferences` entry, producing a console error like:

```
Could not update a managed instance value at property path 'managedReferences[...]', with value '...'
```

This error can persist across editor restarts and does **not** go away by reverting the component, because the corruption is already baked into the serialized file.

To move `StableRef<T>` / `StableRefList<T>` values around safely, use the built-in right-click menu instead of Unity's native component copy/paste:

| Menu item | Where to right-click | What it does |
|---|---|---|
| `StableRef/Copy` | A single `StableRef<T>` field | Copies the current value to an internal clipboard. |
| `StableRef/Paste` | A single `StableRef<T>` field of a compatible type | Creates a fresh managed reference in the target field. |
| `StableRef/Duplicate` | A `StableRef<T>` element inside a list | Inserts a copy right after that element. |
| `StableRef/Copy` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Copies all entries in the list. |
| `StableRef/Paste/Replace` or `StableRef/Paste/Append` | The `StableRefList<T>` header (or an array of `StableRef<T>`) | Replaces or appends the copied entries. |

This is safe across GameObjects, prefabs, and scenes: instead of copying raw serialized bytes, it rebuilds a brand-new managed reference directly in the destination document, so it never corrupts `managedReferences`.

When copying a component that contains `StableRef` fields between a scene and a prefab, use this menu for the StableRef fields specifically rather than Unity's native Copy Component / Paste Component Values.

> **Warning:** even with this menu available, stay cautious. `[SerializeReference]`-based fields (including `StableRef`/`StableRefList`) don't always copy or move as expected, even during trivial built-in Unity operations — Duplicate, drag & drop in the Hierarchy, applying/reverting prefab overrides, scene/prefab merges, and similar actions. Commit or back up your work before bulk changes, and double-check the result afterward.

---

## License

Distributed under the [MIT License](LICENSE.md). Free for personal and commercial use.

Author — **Egor Shesterikov**.
