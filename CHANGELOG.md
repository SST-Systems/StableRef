# StableRef

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
