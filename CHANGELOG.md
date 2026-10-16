# StableRef

## 4.3.0 - 16.10.2026

### Added

- **The selector can group types by namespace.** Types without a category are now listed under their namespace (*Namespace Categories* in the selector gear, on by default; turn it off for the former flat list): a category from `[RefCategory]` or an `IRefTypeMetadataProvider` always wins, the namespace is used only when there is none, `A.B` as `A/B`; nested types use their outermost type's namespace, types in the global namespace stay at the top level. The button label follows *Category in Label* as before.

- **Fix Missing Types marks broken data and fixes it before a save makes the file unreadable.** A file Unity can't read (see *Fixed*) is produced whenever an object holding Unity's missing-type record with *empty* data (the missing class had no serialized fields; `data: ` and `data: {}` both load that way) is saved — Unity even asserts `objData.m_YAMLNode == nullptr` while writing it. The scan now lists a StableRef entry that points at such a record on its field as `SR: Broken (Gone) → Fixed`, greyed like the missing rows — also an entry without a stable id, which wasn't listed before (plain `[SerializeReference]` fields stay out of the tool's scope). Fix All sets these references to None without asking, once the other entries are fixed: it clears exactly these records (`SerializationUtility.ClearManagedReferenceWithMissingType`) — a record held nothing but the class name; an entry keeps its stable id and snapshot, so it stays missing (`Missing (Gone) → None`) and is recreated by Fix once the class is back. Fix All no longer saves an asset that would still write such a record (e.g. of a plain field) — it is reported instead. A record no field uses is dropped by Unity on save and isn't reported.
- **The inspector labels broken data the same way.** A missing entry that is or holds such a field — in memory, or in a file that is already unreadable — shows `Broken (Class) → Fixed` instead of `Missing (…) → …`, and its warning button fixes it as Fix All does. The lookup runs only for missing entries and is cached per object until its file or its missing-type data changes, so a repaint reads nothing.

### Fixed

- **Fix Missing Types repairs a file Unity can't read, instead of "fixing" it forever.** When the class of a `[SerializeReference]` value goes missing and the class has no serialized fields, Unity saves its record without the `data:` line — and can't load such a record back: the object loses *every* managed reference on load (or fails to load at all), with no error and no missing-type data. Fix All recreated all the entries from their snapshots and saved, but Unity kept the unreadable block as it was, so the file never changed and the references were lost again on the next load. Now the scan (whose parallel text prefilter notes which files have a record without `data:` at all — only those, and files whose objects have something missing, are parsed on the main thread) lists the fields pointing at such records under the file, each in the branch of its field as `SR: Broken (Class) → Fixed`; Fix All (and the warning button of a field in such a file) removes them, sets the references to them to `rid: -2` (adding the null record when the object had none — without it Unity logs an error), reimports the file — the object then loads whole from the file — and only then recreates what is still missing from snapshots. Each removed record is logged with its class, assembly and the fields that pointed at it: an entry with a stable id is recreated from its snapshot, one without (or a plain `[SerializeReference]` field) becomes None; the message suggests `[MovedFrom]` to keep a renamed class instead. A class Unity can still load — by name or through `[MovedFrom]` — is never removed. An asset with unsaved changes isn't touched (the reimport would discard them); binary files can't be checked.
- **Objects whose references Unity lost in memory after a domain reload are read from disk again instead of being recreated.** An object that holds Unity's missing-type data loses all its managed references and that data on the *next* domain reload ("A scripted class has a different serialization layout"), while its file is intact; the window then listed every entry as missing, and Fix All recreated all of them from snapshots. Now an object that holds no value at all, has no missing-type data and no unsaved changes while its file has records for it is recognized: a ScriptableObject is read from disk during the scan, so only the really missing types are listed; an open scene is reloaded by Fix All (or the field's button) after a confirmation that names the scenes and warns about unsaved changes — declining leaves the scene alone and warns not to save it. Reloading keeps the scene's place in the hierarchy and the active scene. This is the only case in which StableRef's tools reload a scene.
- **Setting a broken entry to None by hand no longer leaves its file to break on save.** Unity reads a field whose class is missing as -2, so the release after a discard couldn't find that field's record by id, and kept it whenever another entry of the object was still missing; Unity then wrote the hidden reference back and, for a class without data, the file became unreadable. The release now finds such a record through the object's file and clears it once no entry pointing at it is still missing — after None / another type in the selector, **Set to None**, removing the element, or a fix.
- **The project scan visits a file whose objects can't be loaded.** `LoadMainAssetAtPath` returns null for some files with an unreadable record, and the scan skipped such files silently; it now visits them with no targets, so their text is still checked.

## 4.2.6 - 14.10.2026

### Breaking

- **The Newtonsoft.Json Converter sample: `StableRefJsonConverter(bool replaceLists)` is removed, lists are always replaced.** Reading a `StableRefList<T>` now replaces its content like a `T[]` field, regardless of `ObjectCreationHandling`; it used to follow `List<T>` semantics and append unless `ObjectCreationHandling.Replace` or `replaceLists: true` was set. Lists migrated from `[SerializeReference]` were almost always arrays, and populating an existing object (e.g. remote JSON read straight into a config asset) appended and duplicated elements — the default `ObjectCreationHandling.Auto` means appending and can't be told apart from a deliberate choice. Elements that keep their type still keep their stable ID. Replace `new StableRefJsonConverter(replaceLists: true)` with `new StableRefJsonConverter()`; code that relied on appending has to add the elements itself.

## 4.2.5 - 14.10.2026

### Changed

- **The READMEs are shorter.** The package README and both samples' READMEs kept every section but lost most implementation detail (scan internals, keyboard maps, size formulas, the full catalogue of migration rewrites) that users had to read through to find how to use a feature. That detail lives in the XML docs and the code.
- **The Newtonsoft.Json Converter sample follows the usual package layout.** The converter's assembly moved to `Runtime/`, its tests to `Tests/Editor/`, as in the SerializeReference Migration sample. File GUIDs are unchanged; a re-import puts the sample in a new version folder as usual.

## 4.2.4 - 13.10.2026

### Fixed

- **The SerializeReference Migration sample gives a retyped field of a component or ScriptableObject `= new()` too.** It was added only in plain classes, on the assumption that Unity creates the wrapper for components and ScriptableObjects. It does so only when it deserializes them: an object made in code (`AddComponent`, `ScriptableObject.CreateInstance`) and used before it is serialized held a null wrapper, and the first `.Value` threw a `NullReferenceException` where the old field had simply been null. Now every class gets the initializer; for a loaded asset Unity overwrites the field anyway. Struct fields still stay without one (C# 9 has no field initializers in structs).

## 4.2.3 - 13.10.2026

### Changed

- **The SerializeReference Migration sample gives every read of a retyped field `.Value` before the first compile, not only the silent ones.** Before the retype every read was the value, so where the field is surely the retyped one (its receiver's type declares it) the read keeps the value: `x.Apply()`, `x?.State`, `IEffect e = x`, `return x`, `Use(x)` — also when the call's parameter can't be found. Fewer compile rounds are left, and the compiler's messages only fix what the type couldn't be read for. A local initialized from the field (`var r = o.effect;`) now gets the value instead of the wrapper. Left alone: writes (`x = …`, turned into `.Set(…)` by the compiler's messages), the wrapper's own members, a parameter that takes a StableRef, `nameof(x)`, `ref` / `out` and named arguments, a type named like the field.

### Fixed

- **A property pattern on a retyped field matched the wrapper.** `o is { effect: null }` kept compiling but tested the wrapper, which is never null, so the condition silently became false. It is now `o is { effect: { Value: null } }` (C# 9 has no `effect.Value:`) when the pattern's type — written before it, the subject of `is` / `switch`, or the member of an enclosing pattern — declares the retyped field; a pattern of an unknown type is listed.
- **An anonymous type kept the field's member name.** `new { o.effect }` would have become `new { o.effect.Value }` — a member named `Value`, breaking every `x.effect` after it; it is now `new { effect = o.effect.Value }`.
- **More reads are recognized:** an extension method of the value called on the wrapper (`CS1929`), indexers by their own type (a dictionary's value, not its pair), switch-expression arms (`SrOwner so when …`), generic calls along a chain (`x.OfType<T>()`), types nested in the enclosing class; `=> name` and `a ?? name` are no longer taken for declarations. A list value over several lines in an object initializer is wrapped as it is (`new(` … `)`).

## 4.2.2 - 13.10.2026

### Added

- **The SerializeReference Migration sample tells a retyped field from another of the same name by the type of what precedes it.** The silent uses (`!= null`, `is`…) were fixed only by name: `x.effect` in another script counted as the field only when no other type had a member called `effect`, and an unqualified `effect` only in the declaring class's scripts and only when the file had no local of that name anywhere. On a real project that left most uses as warnings to check by hand, and a parameter of the same name in one method blocked the whole file. Now `MigrationTypeModel` reads the type of the receiver from the code — declarations, `var` initializers (a chain, `new`, `as`, a cast), `foreach` elements, deconstructions, LINQ lambda parameters, members and calls along a chain (base classes and generic bases included), static types — and checks it against the loaded (still old) types: the use is fixed only when that type declares the retyped field, and fields of the same name in other types are left alone. A local or parameter of the same name hides the field in its own scope only. Where the type can't be read, the former rule applies.
- **More silent uses are fixed:** `x == other` / `other != x`, `x ?? other`, `x switch { … }`, `switch (x)`, `x.GetHashCode()`, `x` passed to a parameter of type `object` or a type parameter (methods and constructors; a call whose parameter can't be found is listed), and the same uses of a local the wrapper was put in (`var r = o.effect; if (r == null)`).
- **More compile errors are fixed:** a property pattern on a wrapper (`x is { Power: > 0 }`, `CS0117`) gets `.Value` on its subject, `{ Length: … }` of a list becomes `{ Count: … }`; a conditional converting a list to an array or `List<T>` as a whole (reported at its condition) converts its branches.

## 4.2.1 - 13.10.2026

### Added

- **The SerializeReference Migration sample fixes the code that uses the retyped fields.** On a real project the "one button" stopped at hundreds of compile errors, fixed by hand one by one, and at code that compiled but now did something else. Now:
  - after each compile while the data waits, the errors about a `StableRef<T>` / `StableRefList<T>` are fixed as text and the scripts recompile, round after round, until they compile or a round fixes nothing (each round reaches the assemblies that depend on the fixed ones). A read gets `.Value`, an assignment `.Set(value)` (`.Value =` for a value over several lines, `new(value)` in an object initializer — multi-line values there are left alone), `.Length` of a list becomes `.Count`, a list read as an array `.ToArray()`. Where the compiler reports something other than the expression is handled: a conditional is reported at its condition (only the branches that are the field get `.Value`), a pattern of `is` / `switch` at the pattern (the subject before `is`, inside `switch ( )` or before `switch` gets it), `x?.M` at the dot. Localized compiler messages are read too; what can't be fixed is listed in the console;
  - uses the compiler accepts but that now test or read the wrapper get `.Value` before the first compile: `field != null`, `null == field`, `field is null` / `is { }` / `is T`, `field as T`, `field = null`, `ReferenceEquals(field, …)`, `field.GetType()`, `field.Equals(…)`, and `field.Value` / `field.HasValue` when the value type has its own member of that name — `field.Value` would silently become the wrapper's value instead of the value's `Value`. Only uses where the name surely means the field are changed (the declaring class's scripts without a local of that name, or a name no other field or property has); the others are listed as warnings;
  - an assembly definition under `Assets/` whose code now touches a StableRef type without referencing `SST.StableRef` (`CS0012`) gets the reference; *Analyze* lists the assembly definitions that reference an assembly with StableRef types in its public API — a package's, or one with retyped public fields — but not `SST.StableRef`.
- **`StableRefTypeRegistry.TryGetId(type, out id)`** resolves a stable id like `GetOrAssignId` without logging a warning for a type that has none — for tools that only check which types need one.

### Fixed

- **The migration retypes a field marked `[field: SerializeReference]`.** The `field:` target was taken as "auto-property only", so on a plain field such a declaration wasn't recognized and was reported as "declaration not found (only one-line declarations are supported)". On a field the target changes nothing and is accepted either way (on an auto-property it is still required); the other attributes keep their `field:` target. A declaration that is found but whose `[SerializeReference]` isn't recognized is now reported as such, with its attributes, instead of as a missing declaration.
- ***Analyze (Dry Run)* of the migration no longer floods the console with "has no stable ID".** It checked every possible value type through `GetOrAssignId`, which warns for each type without an id — the very types the migration is about to give a `[RefTypeId]`. It uses `TryGetId` now; the warning is still logged once per type when an entry actually needs the id.

## 4.2.0 - 12.10.2026

### Added

- **SerializeReference Migration sample** (`Samples~/SerializeReferenceMigration`, menu *Tools → StableRef → Migrate SerializeReference Data*) switches a project from plain `[SerializeReference]` fields to StableRef with one button. Doing it by hand lost the data: Unity loads the old layout into the new field as empty, keeps the reference records only in the file and drops them the first time the asset is saved. *Migrate* now:
  - changes the type of the fields in the scripts in place — `T` → `StableRef<T>`, `T[]` / `List<T>` → `StableRefList<T>`, `[SerializeReference]` removed or, on a non-public field, replaced with `[SerializeField]` — keeping the names, so the data stays under the same keys (code that reads the fields then needs `.Value`); adds `SST.StableRef` to the assembly definitions and `[RefTypeId]` to value types without a stable ID;
  - after the scripts compile, rewrites the assets under `Assets/` as text: the field layout in components, ScriptableObjects, nested classes and other referenced values, and the paths of prefab overrides, including overrides of objects inside nested prefabs. The reference records are kept with their IDs;
  - reimports and resyncs the changed files, so the stable IDs and snapshots are written right away; every converted scene is opened additively, resynced and saved, and the scenes that were open — closed for the migration, since Unity would otherwise hold them in the old layout — are reopened.

  Until the code that uses the fields is fixed and the scripts compile, the data waits in the old layout — also across an editor restart into the last compiled scripts; *Resume Pending Migration* / *Cancel Pending Migration* take over by hand. *Analyze (Dry Run)* lists every change without writing. Fields already retyped by hand are migrated by the data step alone. On a project with 4,600 asset files the data step takes about 7 seconds.
- **`StableRefResync.ResyncAssets(paths)`** resyncs and saves a given set of ScriptableObject assets and prefabs, in the given order, the way *Resync All* does — for tools that rewrite known files and need their entries stamped without scanning the whole project.
- **`StableRefJson.DataEquals(a, b, settings)` in the Newtonsoft.Json Converter sample** compares the data of two objects at runtime, to apply a config from the server only when it changed. Stable IDs and snapshots can't serve for this: they are editor-only, stripped from players, and JSON from a server has none. Both objects are serialized with your settings and the JSON trees compared; a value of another type inside a StableRef counts as a change even when both types write the same members (`TypeNameHandling.None` is compared as `Auto`).

### Fixed

- **Resync All writes entries that were already synced in memory but not saved.** It compared the entries in memory, and if the inspector or an editor script had already stamped them without saving, it found nothing to update and saved nothing: the file kept the empty or stale metadata, and every later run reported nothing to do. Now an asset whose entries are in sync but which is dirty is saved too — with any other unsaved change it holds, as a regular save would; the console lists such assets.
- **The type selector opens fast the first time after a recompile.** Each candidate type without `[RefTypeId]` was looked up with its own `AssetDatabase.FindAssets` name search — milliseconds per type, so a field whose base type has hundreds of implementations took seconds to open after every domain reload. The script GUIDs now come from one index of all MonoScripts built on first use (about 40 ms for 3,000 scripts); it gives the same IDs as before (checked on 11,000 types). The selector also draws only the rows in view and filters / regroups them without quadratic lookups, so typing in a long list stays responsive.

## 4.1.2 - 09.10.2026

### Fixed

- **The search fields of Find Usages and Fix Missing Types scroll with the caret.** They were drawn with the runtime `GUILayout.TextField`, which in a docked editor window didn't follow the caret: text longer than the field was cut off and the view stayed on its beginning while typing at the end. Both windows now use the editor's own toolbar search field (`UnityEditor.IMGUI.Controls.SearchField`), built on the editor text field, with its built-in cancel button instead of a separate `✕`; the field is also wider (220 px), and Ctrl+F focuses it.

### Changed

- **Find Usages and Fix Missing Types navigate the keyboard like the type selector.** The arrows only worked once the result tree itself had focus, so after typing a filter you had to click into the list first. Now ↑ ↓ PageUp PageDown Home End also work from the search field. → expands a collapsed node, otherwise jumps down to the next node with children. ← collapses an expanded node, otherwise jumps to the parent (at the top level, to the previous group). Alt makes expand / collapse recursive. Enter toggles a node with children and acts like a click on any other row. While the focused search field holds text, ← → Home End move the caret in the field.

## 4.1.1 - 08.10.2026

### Fixed

- ***Assets → Find StableRef Usages* reads the script's assembly name without cutting off part of it.** Unity reports the name with a `.dll` suffix, and the menu removed it as a file extension, that is, everything after the last dot. If an editor version reported the name without the suffix, `SST.StableRef.Editor` would become `SST.StableRef`, and the menu would wait for the declaration index of the wrong assembly: matches by script name would appear only a moment after the scan instead of together with it. Now only a `.dll` suffix is removed.

## 4.1.0 - 08.10.2026

### Added

- **Deep Search in the type selector** (gear button, off by default, `StableRefSelectorWindow.DeepSearch`). With it, the search also matches the name of the script file that declares each type, so a file holding several classes, or a class named unlike its file, is found by the file name. The file names come from an index of the source files of the types' assemblies, built in the background once per domain reload. Typing never waits for the index: until it is ready, only type names match.
- **`StableRefEditorUtility.FindScript(Type)`** returns the script that declares a type (the lookup behind the find-script button, now public; `PingScript` wraps it), or `null` for types without source, such as those from a precompiled DLL.

### Fixed

- **The find-script button pings classes in differently named files, several classes per file, nested types and generic types.** It only accepted a script whose `MonoScript.GetClass()` was the type itself, and Unity associates one class with each file, so the magnifier next to the selector and a click on a type in Find Usages did nothing for a `Data` class declared in its `Condition.cs`, for a second class in a file, for `Window.Payload` or for `Foo<T>`. It now tries the file named after the outermost class (without the `` `N `` suffix) and then falls back to the source files of the type's assembly, indexed once per assembly until the next domain reload. When several files declare the name, the one with the type's namespace wins.
- **Find Usages covers scripts that declare several types.** *Assets → Find StableRef Usages* searched for the one class `MonoScript.GetClass()` returned. For a script without a class named like its file, such as a `Condition.cs` holding a `ConditionProcessor` and a `ConditionData`, that is just one of its classes, often not the one used in assets, so nothing was found. It now searches by the script's name, and the search field matches the name of the script that declares each value's type as well as the row text, so all types of that file are listed. The menu item is validated by a text check only, so right-clicking a script never waits for type lookups.
- **Resync All, Fix All and the inspector's fix button no longer report prefabs Unity refuses to save as updated.** Unity doesn't save a prefab with a missing script: it logs an error and reloads the file. Nothing reached the disk, yet every run reported the same entries as updated again. Such prefabs are no longer saved at all, which also avoids the console error. They are listed in the console and not counted as updated or fixed.

### Changed

- **Project scans load and walk only what can hold StableRef data.** Find Usages, Fix Missing Types, Resync All and the Metadata Size Report loaded every prefab and ScriptableObject under `Assets/` and walked every serialized property of every component. In a project with 4,400 such files, of which 900 held StableRef data, Find Usages took 90 seconds. Now:
  - With text serialization, files are checked as text first, in parallel, and only those containing StableRef data are loaded: an entry, a `StableRefList` (even an empty one) or an override of list items. Prefabs that instantiate such a prefab are loaded too, because variants and nested prefabs show their source's entries and can add elements to a list that is empty in the source. Binary files are loaded as before.
  - Find Usages skips components and assets whose type can't hold a StableRef, as the other tools already did.
  - Property walks skip strings, object references and arrays of primitives and built-in types.
  - Values are checked through their serialized type name instead of being deserialized.
  - Field resolution is cached for all elements of an array at once, and display labels per object.

  Results are the same as before.
- **Find Usages and Fix Missing Types draw only the visible rows.** Every repaint walked and laid out the whole expanded tree, so scrolling stuttered with tens of thousands of results. Rows now have a fixed height and only those in view are drawn. Only the top groups are expanded after a scan, the keyboard works as in the Hierarchy (arrows, Home / End, PageUp / PageDown, Enter), and the search also matches asset, object and field names: a match is shown with everything under it, and the path to it is expanded while the search is active. The search runs once typing pauses for a moment, not on every keystroke, and re-matching tens of thousands of rows allocates nothing. Double-clicking a value in Find Usages opens its script.
- **Removed `StableRefEditorUtility.FoldoutStyle`, `HeaderStyle`, `EnsureStyles` and `OverrideTextColors`.** They only styled the old foldout trees of the tool windows, nothing in StableRef uses them any more, and they hold no StableRef logic. Code that used them can make the same styles itself: `new GUIStyle(EditorStyles.foldout)` / `new GUIStyle(EditorStyles.foldoutHeader)`, with `normal.textColor` copied to the `onNormal`, `focused`, `onFocused`, `active` and `onActive` states.

## 4.0.1 - 07.10.2026

### Changed

- **The Newtonsoft.Json Converter sample has its own README in English and Russian** (`Samples~/NewtonsoftJson/README.md`, `README.ru.md`): setup, how writing and reading work, how JSON written without the converter is recognized, limitations. The package README now lists samples in a *Samples* table and links there instead of describing the converter inline, so the description travels with the imported sample.

## 4.0.0 - 07.10.2026

### Breaking

- **`StableRefList<T>` is now a list of the values, not of the wrappers.** It implements `IList<T>` and `IReadOnlyList<T>`: the indexer returns and sets `T`, and `foreach` / LINQ yield `T` (through a struct enumerator — `foreach` doesn't allocate). Before, they yielded `StableRef<T>`, so value-level LINQ such as `list.OfType<Damage>()` or `list.Any(e => e is Heal)` compiled but silently matched nothing. The entries are still available through `Items`. Both kinds of `IEnumerable<>` can't be offered at once — C# then can't infer LINQ's type argument and no LINQ call on the list compiles — so the wrapper enumeration is gone. Every affected call site is a compile error — the one silent change is JSON written without a converter, see the next item. Migration:
  - `list[i].Value` → `list[i]`; `list[i].TypeId` (and other metadata) → `list.Items[i].TypeId`;
  - `foreach (var r in list) Use(r.Value)` → `foreach (var v in list) Use(v)`;
  - LINQ over wrappers (`list.Select(r => r.Value)`, `list.Where(r => r.Value is X)`) → over values (`list`, `list.Where(v => v is X)`), or `list.Items.…` where you really need the entries;
  - `list[i].Value = v` → `list[i] = v` (reuses the entry; keeps its stable id when the type is unchanged, resets it otherwise).
- **`[RefTypeId]` is no longer inherited — a derived class needs its own id.** The attribute was inheritable and the registry read it with inheritance, so every class derived from a `[RefTypeId]` class without its own attribute got its base's id: its entries were stamped with that id, the id resolved to whichever of the classes was looked up last (a missing base-class entry could be recreated as the derived class and vice versa), and after renaming the derived class recovery brought back the base class. Now only the attribute declared on the class itself counts. Derived classes without their own `[RefTypeId]` fall back to their MonoScript GUID — or, when they share a file, have no stable id and disappear from the `StableRef` selector until they get one. Migration: give such classes their own `[RefTypeId]` (or their own file), then run **Tools → StableRef → Resync All** once — entries stamped with a base class's id get the right id, or lose the borrowed one when the class has none.
- **Serializers now see `StableRefList<T>` as a collection of the values.** Newtonsoft.Json without a converter (and any serializer that handles `IEnumerable<T>`) used to write the list as an array of wrapper objects (`[{"TypeId": …, "Value": …}]`) and now writes an array of the values, like a `List<T>`. JSON written the old way no longer reads correctly without a converter; the Newtonsoft.Json Converter sample reads both shapes. This doesn't show up as a compile error — check saved JSON before upgrading.
- **`StableRefList<T>.ValueAt` and `Values` removed.** They existed only because the list enumerated wrappers; use `list[i]` and the list itself.
- **`StableRefSync` removed — use `StableRefResync.ResyncObject`.** `AssignId` / `AssignIds` only stamped the id: no snapshot, no nested entries, and an id of a previous type stayed when the new type had none, so recovery could recreate the wrong type. `ResyncObject` (or Tools/StableRef/Resync All) does all of that. Migration: `StableRefSync.AssignIds(asset.List)` / `AssignId(asset.Field)` → `StableRefResync.ResyncObject(asset)` once per changed object, before saving.
- **`StableRefBase` and `StableRefListBase` got abstract members** (`ValueBaseType` / `BoxedValue`; `ElementType` / `Count` / `GetBoxed` / `SetBoxed` / `AddBoxed` / `SetBoxedValues` / `Clear`). Only code that derives its own classes from these bases is affected — `StableRef<T>` and `StableRefList<T>` implement them.
- **The snapshot fields are editor-only: `TypeDisplayName`, `ObjectRefs`, `ObjectRefPaths` and `ValuesData` no longer exist in player builds.** They are needed only by recovery, copy/paste and the editor tools, yet every entry carried them into builds — about 130 bytes of serialized data and 400 bytes of managed memory per entry, more with large snapshots, for a project with thousands of entries megabytes that nothing reads at run time; `ObjectRefs` of a stale snapshot could even pull unused assets into a build. Unity serializes them in the editor and leaves them out of builds, so assets are unaffected and keep their recovery data. Migration: code that reads or writes these fields outside the editor must move inside `#if UNITY_EDITOR`; AssetBundles built with `DisableWriteTypeTree` must be rebuilt together with the player. `TypeId` and the value stay in builds.
- **Version-gated consumers must widen their range.** An asmdef that gates a define on `com.sst-systems.stableref` through `versionDefines` must change its expression from `[3.0.0,4.0)` to `[4.0.0,5.0)`.

**Serialized data is unaffected:** no serialized member was renamed, retyped or removed from the assets, so 3.x assets load as they are and nothing needs re-saving.

### Added

- **Access without generics, for reflection and serializers.** `StableRefBase.BoxedValue` (type-checked, throws `ArgumentException` on a mismatch), `ValueBaseType` and `HasValue`; `StableRefListBase.ElementType`, `Count`, `GetBoxed`, `SetBoxed`, `AddBoxed`, `SetBoxedValues` (checks every element before changing the list), `Clear`; and `StableRefReflection.IsStableRef(Type, out valueType)` / `IsStableRefList(Type, out elementType)`. A reflection-based config loader can now fill StableRef fields: `field.SetValue(owner, value)` with a bare value can't work, since reflection never applies conversions.
- **Newtonsoft.Json Converter sample — `StableRefJsonConverter`.** With it a `StableRef<T>` / `StableRefList<T>` field reads and writes exactly the JSON of the same field declared as a bare `T` / `List<T>`, including `TypeNameHandling.Auto`, converters registered for `T`, `Populate` into an existing value and `ObjectCreationHandling` for lists; the stable id and snapshot never reach JSON. A `null` token empties the wrapper or the list — the field keeps its instance, so it is never `null` after loading. Without it Newtonsoft wrote the wrapper's own fields (including `ObjectRefs` with `UnityEngine.Object`s) and read bare-field JSON into an empty wrapper. JSON written that old way is still read, from the editor (all wrapper fields) and from player builds (only `TypeId` and `Value`). Shipped as a package sample (Package Manager → StableRef → Samples → Import), so the package itself has no Newtonsoft dependency; the imported `SST.StableRef.Newtonsoft` assembly needs Newtonsoft.Json in the project (the `com.unity.nuget.newtonsoft-json` package or any `Newtonsoft.Json.dll`) and brings its EditMode tests along. Register it with `settings.Converters.Add(new StableRefJsonConverter())`; pass `replaceLists: true` for fields that used to be `T[]` — Newtonsoft always replaces arrays, while `List<T>` semantics would make `Populate` into an existing object append and duplicate elements.
- **`[assembly: RefTypeIdFor(typeof(T), "id")]`** — a stable id for a type you can't edit: from another package, a precompiled DLL, generated code. Priority: `[RefTypeId]` on the type, then `RefTypeIdFor`, then the MonoScript GUID. Open generic definitions can be mapped; clashing ids or a type mapped twice are logged as errors, and an id already taken by a `[RefTypeId]` stays with that type. Editor-only (`[Conditional("UNITY_EDITOR")]`).
- **Tools/StableRef/Resync All** (`StableRefResync`, `StableRefEntry.Refresh`). The id and snapshot of an entry are written by the inspector when the field is drawn, so values written by code — importers, generators, `field.Value = …` — stayed without an id (a later class rename couldn't be recovered) or kept the id of the previous value (recovery would recreate the wrong type). Resync All brings every entry in ScriptableObjects and prefabs under `Assets/` and in the open scenes in line with its value: nested entries first, values untouched, missing entries left to Fix Missing Types, inherited entries of prefab instances and variants skipped so no overrides are created. Changed assets are saved one by one, scenes are marked dirty; types still without a stable id are listed in the console.
- **Tools/StableRef/Metadata Size Report.** Estimates the size of the metadata kept next to every value: what reaches player builds (the stable ids — serialized bytes and managed memory with everything loaded) and what stays in the assets only (display names and snapshots), per entry and for the largest assets. Computed from the binary format over ScriptableObjects and prefabs under `Assets/` and the open scenes, including empty and missing entries; read-only.
- **Inspector integration hooks (IMGUI).** `StableRefDrawing.ChildrenDrawer` (`IStableRefChildrenDrawer`) draws the fields inside values through another inspector framework, so its attributes work inside StableRef values; it is used only for an expanded value of a single type, so the selector, missing entries, multi-object editing and the context menu stay with StableRef. `IRefTypeMetadataProvider` supplies type names, categories, tooltips, colors and sort order for the selector from other attributes (found automatically, cached per type; rows show the tooltip and a color accent, and a renamed type is still found by its class name). `StableRefDrawing.InvalidateLayout()` makes StableRef lists re-measure when such a drawer changes its own height (foldouts, tabs). `StableRefEditorUtility.GetValueProperty` returns the managed reference of both a `StableRef<T>` and a plain `[SerializeReference]` field, for drawers that find fields by name.
- **Metadata-aware writes: `StableRef<T>.Set(value)`.** Keeps the stable id and snapshot when the new value has the same type and resets them when the type changes (or a present value is cleared), so no id of another type is left behind; clearing an entry that is already empty keeps a missing entry's recovery data. `BoxedValue`, the list indexer, `SetBoxed` and `SetBoxedValues` (by index) reuse entries the same way, so re-applying configs from code keeps the ids. Code still can't capture snapshots: tools that write into assets in the editor should call `StableRefResync.ResyncObject` before saving.
- **Construction and copying.** `new StableRef<T>(value)`, `new StableRefList<T>(values)`, `StableRefList<T>.ToArray()` and `CopyTo`, `ToString()`. `StableRef<T>.ShallowCopy()` / `StableRefList<T>.ShallowCopy()` give an owner cloned with `MemberwiseClone` its own wrappers — otherwise the clone shares them and `clone.Field.Value = x` changes the original too. No implicit conversions between `StableRef<T>` and `T`: C# ignores user-defined conversions to and from interfaces, and `T` is usually one.

### Fixed

- **Looking at a prefab instance no longer creates overrides.** The inspector stamps an entry's stable id when it draws the field; for an entry inherited from the prefab whose id was outdated (code-written, a legacy GUID id, an id fixed by this release), that wrote the id onto the instance — a prefab override, a dirty scene and an undo step just from selecting the object. Entries a prefab instance or variant doesn't override are now left to their source prefab (stamped when that prefab is drawn, or by Resync All), the rule Resync All already followed; the same applies to multi-object editing.
- **Find Usages, Fix Missing Types, Resync All and the Metadata Size Report also scan sub-assets.** They loaded only the main object of each file, so StableRef entries in ScriptableObjects stored inside another asset — graph nodes, Timeline clips, `StateMachineBehaviour`s — were never listed, fixed or resynced, and Fix Missing Types reported nothing while such entries were broken. Sub-assets are shown under their file in the *Scriptable Objects* group as `Name (Type)`.

### Changed

- **The missing-id warning names the type in full and lists every fix** (`[RefTypeId]`, its own file, `RefTypeIdFor`).

## 3.0.0 - 05.10.2026

### Breaking

- **Attributes renamed: `[StableTypeId]` → `[RefTypeId]`, `[StableRefCategory]` → `[RefCategory]`** (`StableTypeIdAttribute` → `RefTypeIdAttribute`, `StableRefCategoryAttribute` → `RefCategoryAttribute`). With `[RefSelector]` added, the attributes describe the referenced type for both selectors, not only `StableRef<T>`, so they share one `Ref…` prefix. The old names are removed — replace them in code (a project-wide find & replace of `StableTypeId` → `RefTypeId` and `StableRefCategory` → `RefCategory` is enough). **Serialized data is unaffected:** ids are stored by value, so every existing reference keeps resolving and no asset needs re-saving.
- **Version-gated consumers must widen their range.** An asmdef that gates a define on `com.sst-systems.stableref` through `versionDefines` must change its expression from `[2.0.0,3.0)` to `[3.0.0,4.0)`.

### Added

- **`[RefSelector]` — the selector without the wrapper.** Mark a plain `[SerializeReference]` field with `[RefSelector]` (drawn by the new public `RefSelectorDrawer`) to get the searchable type selector (categories, generic candidates, copy/paste) with no `StableRef<T>`, no stored id and no snapshot — the field stays a bare `[SerializeReference]`, and the attribute is `[Conditional("UNITY_EDITOR")]`, so nothing reaches player builds. Meant for ECS authoring/baking code, existing `[SerializeReference]` fields and large lists. It is opt-in at your own risk: no rename protection, and such fields are intentionally not covered by Find Usages or Fix Missing Types; a field whose class is gone simply shows an empty selector (`None`) and is replaced without confirmation. Use `[MovedFrom]` for safe renames.
- **Full keyboard navigation in the selector.** Categories are now part of the keyboard selection, and the arrows follow Unity's own tree views (Hierarchy / Project): `→` expands a collapsed category, otherwise jumps down to the next category; `←` collapses an expanded category, otherwise jumps to the parent (`Alt` for recursive); `Enter` picks the selected type or toggles the selected category, plus `PageUp` / `PageDown` / `Home` / `End`. The selector opens with the current type selected and scrolled into view, typing a search selects the first match, and the selection survives collapsing, clearing the search and resizing. Keys are handled before the search field, which used to swallow the arrow keys while focused.
- **Element commands in lists.** Right-clicking a list element now opens the menu of that element instead of falling through to the whole list's menu: **Copy**, **Paste**, **Paste as New Element** (insert below), **Duplicate Array Element**, **Delete Array Element**, **Set to None**. In a `StableRefList<T>` this works anywhere on the element's row — type button, drag handle, row padding; in `List<StableRef<T>>`, `StableRef<T>[]` and `[RefSelector]` lists, whose rows Unity draws itself, right-click the element's field. The same commands are also under **StableRef/** in Unity's own property menu. Duplicate makes a deep copy (Unity's own array Duplicate would share one managed reference between both elements); nested fields of a value keep their own menus.
- **`StableRefPropertyUtils.GetEntries(property, requireStableId)`** — selector entries with or without the stable-id requirement.

### Fixed

- **Element commands acted on the wrong list for nested references.** `StableRefPropertyUtils.TryGetParentArray` took the last `.Array.data[` of the path, so a `StableRef` nested inside a list element's value counted as an element of the outer list — **Duplicate** on it inserted a copy into the outer list. It also accepted any field named `Value` as an element, so on a `List<Holder>` with a plain `[SerializeReference] Value` field, **Delete** removed the whole `Holder`. Only the element itself, or the `Value` of a StableRef element, counts now.
- **Duplicate on a multi-selection shared one copy between objects.** Duplicating a list element wrote through the shared multi-object `SerializedObject`, assigning the same new instance to every selected object. Each object now gets its own deep copy of its own element.
- **Multi-object editing wrote the first object's metadata onto every selected object.** With several objects selected, the drawer stamped `TypeId` and refreshed the snapshot through the shared `SerializedObject`, so the first target's id, `ObjectRefs` and `ValuesData` were copied onto the others — after a class rename those objects would have been recovered with the first object's type and field values. Metadata is now synced and captured per target (one undo step with the edit).
- **Mixed types in a multi-selection are shown as `—`.** The selector button used to show the first selected object's type even when the others held different types (or none / a missing type), inviting an accidental overwrite. It now shows `—` and doesn't expand the first object's fields; picking a type still applies to every target. Applies to `StableRef<T>`, `StableRefList<T>` elements and `[RefSelector]` fields. The per-target check is cached and refreshed on selection change, undo/redo and edits, not on every repaint.
- **Icons on the light editor skin used the dark-skin art.** `StableRefEditorUtility.Icon` only fell back to the other variant when the requested one failed to load, but `d_` textures load fine on the light skin too — so the find-script (magnifier) button, the selector's settings gear and the scene / GameObject icons in the tool windows drew light-gray glyphs on light-gray buttons, barely visible. The variant is now picked from the current skin, whichever name the caller passes.
- **The "contains SerializeReference types which are missing" warning goes away right after a fix or discard.** Unity's native missing-type data used to be cleared only after Fix Missing Types; the clear now also runs after **Set to None**, a replace in the selector, **Delete Array Element**, the list's **−** button and the list menu's **Clear** / **Paste/Replace** on a missing entry, so the inspector warning no longer lingers until a domain reload. It never runs while a StableRef entry on the object is still missing, and removing a healthy list element never triggers it. Note that Unity doesn't expose which field points at a missing record, so the native data of plain `[SerializeReference]` / `[RefSelector]` fields whose class is missing at that moment, and of StableRef entries that were never stamped with a `TypeId`, is cleared along with it — restore their classes (or use `[MovedFrom]`) before fixing or discarding entries on the same object.

### Changed

- **One selector field for every drawer.** The popup / foldout / children / context-menu field is now a single internal component used by both the `StableRef<T>` / `StableRefList<T>` drawer and the `[RefSelector]` drawer; they only add what is specific to them. The missing-entry label is passed per call instead of through static fields shared between drawers (the hand-off pattern behind the 2.0 "label leaks onto the next field" bug). No public API changed.
- **A missing entry no longer locks its `StableRefList` or its own selector.** Previously one entry whose type couldn't be resolved disabled the whole list (no add / remove / reorder) and its selector. The list lock dated back to the 1.x `_hadValue` heuristic, which wiped the `TypeId` of entries that changed index — removed in 2.0, so reordering no longer endangers recovery data. A missing entry is now discarded like any other value: pick None or another type in its selector, **Set to None** in its context menu, or delete the element — replacing it asks for confirmation first, because the class may only be missing for a moment.
- **Clear Entry removed from the context menu.** It duplicated **Set to None** under a less obvious name; **Set to None** (and None in the selector) now also works on missing entries, with the confirmation above. `StableRefEntry.Clear` is unchanged.
- **Clearing or replacing a list with missing entries asks first.** The list menu's **Clear** and **Paste/Replace** used to discard missing StableRef entries — and the data kept for recovering them — without a word. They now show how many entries would be lost and ask for confirmation, like replacing a single missing entry; lists without missing entries clear and paste as before. **Clear** also writes through one `SerializedObject` per selected object instead of the shared one.
- **Picking the type a field already holds keeps its value.** Choosing the current type (or None on an empty field) in the selector used to replace the value with a fresh default instance, silently resetting its fields; it is now a no-op for that object. With several objects holding different types the selector opens with nothing selected, so Enter can't apply the first object's type to the rest by accident.
- **The per-field fix button repairs every selected object**, not only the first one, when several objects are selected.
- **The selector button label is cached per type** instead of reflecting `[RefCategory]` on every repaint.
- **Consistent internals and logs.** Editor code refers to serialized members only through the `StableRefEntry.*FieldName` constants, and every log message uses the `[StableRef]` prefix (some still said `[StableRefSelector]`).
- **Context-menu commands resolve their property when picked.** They used to hold the `SerializedProperty` of the moment the menu opened, which the inspector may have disposed by the time an item is chosen; they now capture the targets and the property path.

## 2.0.1 - 17.09.2026

### Fixed

- **Unity 6000.5+ compatibility (InstanceID → EntityId).** Unity 6000.4 replaced the 32-bit `InstanceID` with the 64-bit `EntityId`, and 6000.5 turned the old `Object.GetInstanceID()`, `SerializedProperty.objectReferenceInstanceIDValue` and `EditorUtility.InstanceIDToObject` APIs into hard compile errors. The editor code now routes those through a version-gated shim (`StableRefEditorUtility.GetStableId` / `GetObjectReferenceId` / `IdToObject`, guarded on `UNITY_6000_5_OR_NEWER`): the EntityId path compiles on 6000.5+, the InstanceID path on older editors, so the package builds cleanly from 2021.3 through 6000.6+. In-session id maps widened from `int` to `long`; nothing serialized changed.

## 2.0.0 - 28.08.2026

### Breaking

- **Snapshot format v2.** `ValuesData` written by 2.0 starts with a `#v2` header; 1.x cannot read it. 2.0 still restores 1.x snapshots (including their enum-by-index encoding).
- **Fix All no longer wipes unresolvable entries.** An entry whose stable id doesn't resolve is skipped with a summary warning and keeps all its recovery data; discarding one is now an explicit action (right-click → **Clear Entry**).
- **The drawer no longer auto-clears TypeId** when it notices a value went null (`_hadValue` heuristic removed, along with `StableRefHandler.ClearHadValue`). Every built-in path that empties an entry clears its metadata explicitly; entries emptied by external code without metadata cleanup now show up in the Fix Missing Types scan instead of being silently wiped — use **Clear Entry** or `StableRefEntry.Clear` there.
- **Version-gated consumers must widen their range.** Any asmdef that gates a define on `com.sst-systems.stableref` through `versionDefines` must change its expression from `[1.2.0,2.0)` to `[2.0.0,3.0)` — until it does, the define stays off and that code is simply not compiled.

### Added

- **`StableRefEntry`** — public entry-level API for inspector integrations and editor scripts: `Sync`, `Clear`, `IsMissing`, `TryRecreate`, `BuildMissingLabel`/`BuildMissingLabelText`, `MissingLabelColor`, and the serialized field-name constants. Custom drawers that reimplemented TypeId syncing, missing detection or entry clearing should migrate to these.
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
