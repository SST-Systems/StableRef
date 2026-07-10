# StableRef

## 1.0.1 - 10.07.2026

First public release under **SST Systems**.

A convenient, reliable wrapper over Unity's `[SerializeReference]` (SR) fields. Makes working with polymorphic serialized references stable and comfortable: a searchable typed inspector selector, safe copy/paste, and editor tooling to find and fix references. On top of that, references survive class renames — a stable type ID is stored alongside the object: `StableRef<T>`, `StableRefList<T>`, `[StableTypeId]`, `[StableRefCategory]`. Editor tools included — searchable type selector, Find Usages, Fix Missing Types.
