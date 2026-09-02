# StableRef

## 2.0.0 - 28.08.2026

### Breaking

- **Snapshot format v2.** `ValuesData` written by 2.0 starts with a `#v2` header; 1.x cannot read it. 2.0 still restores 1.x snapshots (including their enum-by-index encoding).
- **Fix All no longer wipes unresolvable entries.** An entry whose stable id doesn't resolve is skipped with a summary warning and keeps all its recovery data; discarding one is now an explicit action (right-click → **Clear Entry**).
- **The drawer no longer auto-clears TypeId** when it notices a value went null (`_hadValue` heuristic removed, along with `StableRefHandler.ClearHadValue`). Every built-in path that empties an entry clears its metadata explicitly; entries emptied by external code without metadata cleanup now show up in the Fix Missing Types scan instead of being silently wiped — use **Clear Entry** or `StableRefEntry.Clear` there.
- **Version-gated consumers must widen their range.** The Tri Inspector integration's asmdef `versionDefines` entry for `com.sst-systems.stableref` must change its expression from `[1.2.0,2.0)` to `[2.0.0,3.0)` — until it does, the `TRIINSPECTOR_STABLEREF` define stays off and the integration is simply not compiled.

### Added

- **`StableRefEntry`** — public entry-level API for inspector integrations and editor scripts: `Sync`, `Clear`, `IsMissing`, `TryRecreate`, `BuildMissingLabel`/`BuildMissingLabelText`, `MissingLabelColor`, and the serialized field-name constants. Integrations that reimplemented TypeId syncing, missing detection or entry clearing (e.g. the Tri Inspector layer's `SyncSerializedTypeId` / `IsMissingReference` / `ClearStableTypeId` and its held-value mirror) should migrate to these.
- **Recursive snapshot/restore.** Rename recovery now restores nested structs, arrays and lists (sizes and elements), hidden serialized fields, and `UnityEngine.Object` references anywhere in the value — previously only the value's top-level primitive fields and object references survived. StableRef entries nested inside a recovered value are recovered too: fixing runs in passes until nothing is left to recreate. Not captured (restored as defaults): `AnimationCurve`, `Gradient`, `Hash128`, `ExposedReference`, fixed buffers.
- **Clear Entry** context-menu item on missing entries — the deliberate way to discard an unresolvable entry and its recovery data.
- The Fix Missing Types scan detects entries with a stored TypeId and no value even when Unity's native missing-type flag is absent (ghost entries), and both tool windows have cancelable progress bars.

### Fixed

- **Duplicate** on a `StableRefList` element threw an exception (it wrote the managed reference onto the wrapper element itself) — the only kind of element the menu is offered on. It now duplicates correctly and stamps the copy's metadata.
- **Multi-object editing:** picking a type or **Set to None** applies value and metadata together per selected target — no more `Missing (X)` ghosts on secondary targets.
- **List paste** left the neighbor element's duplicated snapshot data (`TypeDisplayName`, `ObjectRefs`, `ValuesData`) on inserted elements; entries are fully reset and re-stamped.
- **GUID→`[StableTypeId]` migration** no longer depends on what touched the type first: the attribute id always wins for stamping, so stored ids stop flip-flopping between forms in version control (legacy GUID ids keep resolving).
- **Snapshot encoding** is culture-invariant (floats no longer break across OS locales), enums survive member reordering (underlying value instead of index), and `long`/`double` fields keep full precision.
- The missing-entry label of one field could leak onto the next drawn field after a right-click.
- All tool icons render on the light editor skin (dark-skin `d_` names were hardcoded).
- The selector window survives inspector rebuilds and its settings popup no longer calls into a destroyed window; the selector also filters out types `[SerializeReference]` can't hold (structs, `UnityEngine.Object` descendants, types without a parameterless constructor), Enter picks the first search match, and the row highlight follows keyboard navigation.
- Single StableRef entries nested inside values now appear in **Find Usages** (only nested lists did before).

### Changed

- **Fix All marks fixed scenes dirty instead of silently saving them** — save the scene yourself to persist the fix; fixed assets are saved individually (`SaveAssetIfDirty`) instead of a global `SaveAssets`.
- Per-field fix (the warning button) runs the same pass-based recovery as Fix All, scoped to that entry, and no longer force-reimports the asset. Unity's native missing-type records are only cleared once a target has no missing entries left.
- Both tool windows scan `Assets/` only (prefab scan previously included `Packages/`), and release loaded assets when the scan finishes.
- `StableRefList` drawer caches are bounded (evicted on selection change) and the broken-entry check is memoized per editor tick.

## 1.2.0 - 18.08.2026

### Added

- **`StableRefBackup`** — public entry point to the value snapshot an entry keeps next to its managed reference (`ValuesData`, `ObjectRefs`, `ObjectRefPaths`). `Capture` refreshes it from the current value, `Restore` replays it onto the value. The built-in drawer keeps maintaining the snapshot on its own, so nothing changes for ordinary usage; the API exists for inspector integrations that draw StableRef entries themselves and take over that responsibility — without it, a reference recovered after a rename would be restored from data captured before the last edits.

### Changed

- **`StableRefContextMenu` and `StableRefEditorUtility` are now public.** Both were internal, which left an inspector integration unable to reuse them: copy / paste / clear of entries and lists lives entirely in `StableRefContextMenu`, and the jump-to-script button next to every type selector lives in `StableRefEditorUtility.PingScript`. Reimplementing either outside the package would either drop the feature or duplicate it. No behaviour changed.

## 1.1.2 - 22.07.2026

### Added

- **Alt/Option-click recursively expands or collapses** a `StableRef` value or `StableRefList`, matching Unity's built-in foldout behaviour. Holding Alt while toggling a foldout now applies the same expanded state to every nested field, including `StableRef` / `StableRefList` elements deeper inside — previously only the clicked foldout reacted.

### Fixed

- Expanding or collapsing a foldout inside a `StableRefList` no longer leaves the inspector showing the old layout until an unrelated event redraws it. Unity's `ReorderableList` caches its height and only recomputes it on add/remove or a control-count change — not when a nested foldout resizes an element — and the drawer reused cached lists, so the stale height survived even a full repaint. Toggling any StableRef / StableRefList foldout now invalidates those cached lists so they re-measure immediately.

## 1.1.1 - 21.07.2026

### Changed

- asmdef rename

## 1.1.0 - 17.07.2026

Generic types in the selector, a code-friendly `StableRefList`, and faster type discovery.

- The type selector now offers **generic types** for closed generic fields: for `StableRef<ICondition<Unit>>` or `StableRefList<ICondition<Unit>>`, open generic definitions (`All<>`, `Any<>`, …) appear and are closed with the field's own arguments (`All<Unit>`). The interface may be implemented indirectly through a base class, and reordered or partially-fixed type parameters are inferred.
- Closed generic values get a **stable ID** too, composed from the open definition's ID plus its argument IDs, so `All<Unit>` and `All<Shape>` are distinct entries and both survive class renames. Each argument type still needs its own stable ID (its own file, or `[StableTypeId]`).
- `StableRefTypeRegistry` logs an error for duplicate stable IDs: two types sharing a `[StableTypeId]` are reported on every editor load / recompile.
- `StableRefList<T>` can now be **edited from code** with a familiar `List<T>`-style API: `Add`, `AddRange`, `Insert`, `Remove`, `RemoveAt`, `RemoveAll`, `Clear`, `Contains`, `IndexOf`, `Find`, `FindIndex`, `Exists`, plus `Values`/`ValueAt` and a public `Items` for the full `List` surface. Indexing and `foreach` still yield the `StableRef<T>` wrapper.
- New editor helper `StableRefSync.AssignIds(list)` / `AssignId(entry)` stamps stable IDs onto entries created from code — the inspector does this automatically when a field is drawn, so call it after building lists in an editor script (before saving).
- **Find Usages** and **Fix Missing Types** now render generic values correctly — clean `SR: All` labels and full nesting, where before they showed the raw ``All`1`` name and a collapsed hierarchy. Nested `StableRefList` nodes show the collection's field name instead of the backing `Items`.
- **Find Usages**: removed the type-filter dropdown and the parent-type prefix shown before each class (legacy noise). Every StableRef value is now tagged with a short `SR:` prefix to stand out.
- Both tool windows reset their search and scan results after a domain reload / recompile, so they no longer show stale or broken state — in particular **Fix Missing Types** no longer errors out after a rebuild.
- Type discovery now uses Unity's `TypeCache` instead of scanning every loaded assembly, so opening the selector and running the scans is faster on large projects.

## 1.0.2 - 12.07.2026

Bug fixes and editor UX polish.

- Type selector: the search field now takes keyboard focus the moment the dropdown opens, so you can start typing without clicking it first.
- `StableRefList`: fixed a freeze and assertion when adding an element while multiple objects were selected. Elements are now added per selected target instead of through the shared multi-object `SerializedObject`.
- Copy/paste and Duplicate now preserve `UnityEngine.Object` references nested inside deeper StableRef values (a `StableRef`/`StableRefList` within a value) instead of dropping them.
- **Find Usages** and **Fix Missing Types** only scan scenes that are already open in the editor (including unsaved/`Untitled` scenes and scenes outside `Assets/`) — neither one opens or closes scenes on your behalf. Open the scenes you want checked, then run the scan.
- **Fix All Missings** no longer opens or closes scenes either. If a broken reference was found in a scene that's since been closed, it's skipped (with a warning naming how many closed scenes were skipped) instead of being reopened just to save the fix — open the scene and re-scan to fix it. This removes the last place scene load/unload could happen as a side effect of using these tools.
- **Find Usages** always shows the Prefabs / Active Scenes / Scriptable Objects headers, with a "No usages found" line under any category that had no results, so it's clear which sources were searched.
- **`StableRefTypeRegistry`**: a type that fails to resolve to a stable ID (most commonly because it shares a script file with other classes, so `MonoScript.GetClass()` can't match it) is now cached as unresolved, mirroring the existing ID→type cache. Previously a failed lookup ran `AssetDatabase.FindAssets` again on every single OnGUI repaint, which caused severe lag in `StableRefList` — most noticeably while dragging to reorder or right after assigning a value — once several such classes piled up in one file.
- Type selector dropdown no longer lists types that can't get a stable ID, so it's no longer possible to create new broken/laggy references through the picker. Give each affected class its own file (filename matching the class name) or add `[StableTypeId]` to make it selectable again.
- A one-time `Debug.LogWarning` now points out exactly which type failed ID resolution and why, instead of silently hiding it from the picker.

## 1.0.1 - 10.07.2026

First public release under **SST Systems**.

A convenient, reliable wrapper over Unity's `[SerializeReference]` (SR) fields. Makes working with polymorphic serialized references stable and comfortable: a searchable typed inspector selector, safe copy/paste, and editor tooling to find and fix references. On top of that, references survive class renames — a stable type ID is stored alongside the object: `StableRef<T>`, `StableRefList<T>`, `[StableTypeId]`, `[StableRefCategory]`. Editor tools included — searchable type selector, Find Usages, Fix Missing Types.
