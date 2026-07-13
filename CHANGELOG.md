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

## 1.0.1 - 10.07.2026

First public release under **SST Systems**.

A convenient, reliable wrapper over Unity's `[SerializeReference]` (SR) fields. Makes working with polymorphic serialized references stable and comfortable: a searchable typed inspector selector, safe copy/paste, and editor tooling to find and fix references. On top of that, references survive class renames — a stable type ID is stored alongside the object: `StableRef<T>`, `StableRefList<T>`, `[StableTypeId]`, `[StableRefCategory]`. Editor tools included — searchable type selector, Find Usages, Fix Missing Types.
