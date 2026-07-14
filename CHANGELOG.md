# StableRef

## 1.0.2 - 12.07.2026

Bug fixes and editor UX polish.

- Type selector: the search field now takes keyboard focus the moment the dropdown opens, so you can start typing without clicking it first.
- **Fix Missing Types**: fixed a `NullReferenceException` when broken references lived in scenes other than the one currently open. Those scenes are opened and closed during the scan; the report now builds from data captured while each scene is loaded instead of dereferencing objects that were destroyed on close.
- `StableRefList`: fixed a freeze and assertion when adding an element while multiple objects were selected. Elements are now added per selected target instead of through the shared multi-object `SerializedObject`.
- Copy/paste and Duplicate now preserve `UnityEngine.Object` references nested inside deeper StableRef values (a `StableRef`/`StableRefList` within a value) instead of dropping them.
- **Find Usages** now scans every scene in the project, not only the ones currently open. Unopened scenes are loaded additively for the scan and closed again afterwards. Which object a result pings is resolved at click time: if the result's scene is open right now it selects the live object (even if that scene was closed during the scan), otherwise it pings the scene asset. This also removes ambiguity between different scenes that share the same name.
- **Fix Missing Types** and **Find Usages** now use the same scene-scanning behaviour: both also cover open unsaved/`Untitled` scenes (and scenes outside `Assets/`), and both ping the exact object for scenes that are already open, falling back to the scene asset only for scenes that were opened just for the scan.
- **Find Usages** always shows the Prefabs / Scenes / Scriptable Objects headers, with a "No usages found" line under any category that had no results, so it's clear which sources were searched.
- **`StableRefTypeRegistry`**: a type that fails to resolve to a stable ID (most commonly because it shares a script file with other classes, so `MonoScript.GetClass()` can't match it) is now cached as unresolved, mirroring the existing ID→type cache. Previously a failed lookup ran `AssetDatabase.FindAssets` again on every single OnGUI repaint, which caused severe lag in `StableRefList` — most noticeably while dragging to reorder or right after assigning a value — once several such classes piled up in one file.
- Type selector dropdown no longer lists types that can't get a stable ID, so it's no longer possible to create new broken/laggy references through the picker. Give each affected class its own file (filename matching the class name) or add `[StableTypeId]` to make it selectable again.
- A one-time `Debug.LogWarning` now points out exactly which type failed ID resolution and why, instead of silently hiding it from the picker.
- **Find Usages** and **Fix Missing Types** now have a scene-scan mode dropdown next to their scan button: *Don't Scan Scenes*, *Scan Open Scenes*, or *Scan All Scenes*. Scanning every scene is what's slow on projects with a lot of scene content (each unopened scene is additively loaded and closed), so this lets you skip scenes entirely or limit the scan to whatever's already open. The choice is shared between both windows and remembered between sessions.

## 1.0.1 - 10.07.2026

First public release under **SST Systems**.

A convenient, reliable wrapper over Unity's `[SerializeReference]` (SR) fields. Makes working with polymorphic serialized references stable and comfortable: a searchable typed inspector selector, safe copy/paste, and editor tooling to find and fix references. On top of that, references survive class renames — a stable type ID is stored alongside the object: `StableRef<T>`, `StableRefList<T>`, `[StableTypeId]`, `[StableRefCategory]`. Editor tools included — searchable type selector, Find Usages, Fix Missing Types.
