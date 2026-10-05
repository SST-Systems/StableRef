#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SST.StableRef
{
    /// <summary>
    /// Copy / paste and clear commands for StableRef entries and lists, both as the right-click menu of an entry
    /// and as the entries Unity appends to its own property context menu.
    /// </summary>
    /// <remarks>
    /// Public so an inspector integration that draws StableRef fields itself can open the same menu instead of
    /// leaving its users without copy / paste. <see cref="ShowDirectMenu"/> takes the <c>Value</c> property of an
    /// entry, <see cref="ShowListMenu"/> takes the backing array of a list.
    /// </remarks>
    public static class StableRefContextMenu
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            EditorApplication.contextualPropertyMenu -= AppendContextMenuItems;
            EditorApplication.contextualPropertyMenu += AppendContextMenuItems;
        }

        public static void ShowListMenu(SerializedProperty arrayProp)
        {
            bool hasItems = arrayProp.arraySize > 0;
            var listClip = StableRefClipboard.List;
            bool hasClip = listClip != null && listClip.Entries.Count > 0;
            bool clipFits = hasClip
                && StableRefPropertyUtils.IsListPasteCompatible(arrayProp, listClip.ElementBaseType);

            var menu = new GenericMenu();
            AddItem(menu, "Copy", hasItems, Bind(arrayProp, CopyList));
            AddItem(menu, "Paste/Replace", clipFits, Bind(arrayProp, p => PasteList(p, replace: true)));
            AddItem(menu, "Paste/Append", clipFits, Bind(arrayProp, p => PasteList(p, replace: false)));
            AddItem(menu, "Clear", hasItems, Bind(arrayProp, ClearList));
            menu.ShowAsContext();
        }

        public static void ShowDirectMenu(SerializedProperty property)
        {
            var prop = property.Copy();

            bool hasValue = prop.managedReferenceValue != null;
            bool clipFits = StableRefClipboard.HasValue
                && StableRefPropertyUtils.IsAssignable(prop, StableRefClipboard.ValueType);
            bool isInArray = StableRefPropertyUtils.TryGetParentArray(prop, out _, out _);

            var menu = new GenericMenu();
            AddItem(menu, "Copy", hasValue, Bind(prop, CopyValue));
            AddItem(menu, "Paste", clipFits, Bind(prop, PasteValue));

            if (isInArray)
            {
                AddItem(menu, "Paste as New Element", clipFits, Bind(prop, PasteAsNewElement));
                menu.AddSeparator("");
                AddItem(menu, "Duplicate Array Element", hasValue, Bind(prop, DuplicateValue));
                menu.AddItem(new GUIContent("Delete Array Element"), false, Bind(prop, DeleteElement));
            }

            if (hasValue || StableRefEntry.HoldsMissingData(prop.serializedObject, prop))
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Set to None"), false, Bind(prop, SetNone));
            }

            menu.ShowAsContext();
        }

        private static void AppendContextMenuItems(GenericMenu menu, SerializedProperty property)
        {
            if (property == null) return;

            if (property.propertyType == SerializedPropertyType.ManagedReference)
            {
                if (!StableRefPropertyUtils.IsStableRefValueField(property)) return;

                var prop = property.Copy();
                bool hasValue = prop.managedReferenceValue != null;
                bool clipFits = StableRefClipboard.HasValue
                    && StableRefPropertyUtils.IsAssignable(prop, StableRefClipboard.ValueType);
                bool isInArray = StableRefPropertyUtils.TryGetParentArray(prop, out _, out _);

                AddItem(menu, "StableRef/Copy", hasValue, Bind(prop, CopyValue));
                AddItem(menu, "StableRef/Paste", clipFits, Bind(prop, PasteValue));
                if (isInArray)
                {
                    AddItem(menu, "StableRef/Paste as New Element", clipFits, Bind(prop, PasteAsNewElement));
                    AddItem(menu, "StableRef/Duplicate Array Element", hasValue, Bind(prop, DuplicateValue));
                    menu.AddItem(new GUIContent("StableRef/Delete Array Element"), false, Bind(prop, DeleteElement));
                }
                if (hasValue || StableRefEntry.HoldsMissingData(prop.serializedObject, prop))
                    menu.AddItem(new GUIContent("StableRef/Set to None"), false, Bind(prop, SetNone));
                return;
            }
            
            if (StableRefPropertyUtils.IsStableRefList(property))
            {
                var items = StableRefPropertyUtils.GetStableRefListItems(property);
                if (items != null) AppendListMenuItems(menu, items);
                return;
            }

            if (property.isArray && StableRefPropertyUtils.IsStableRefArray(property))
            {
                AppendListMenuItems(menu, property.Copy());
                return;
            }
            
            if (property.propertyType == SerializedPropertyType.Generic
                && StableRefPropertyUtils.TryFindStableRefListChild(property, out var listChild))
            {
                AppendListMenuItems(menu, listChild);
            }
        }

        private static void AppendListMenuItems(GenericMenu menu, SerializedProperty arrayProp)
        {
            bool hasItems = arrayProp.arraySize > 0;
            var listClip = StableRefClipboard.List;
            bool hasClip = listClip != null && listClip.Entries.Count > 0;
            bool clipFits = hasClip
                && StableRefPropertyUtils.IsListPasteCompatible(arrayProp, listClip.ElementBaseType);

            AddItem(menu, "StableRef/Copy", hasItems, Bind(arrayProp, CopyList));
            AddItem(menu, "StableRef/Paste/Replace", clipFits, Bind(arrayProp, p => PasteList(p, replace: true)));
            AddItem(menu, "StableRef/Paste/Append", clipFits, Bind(arrayProp, p => PasteList(p, replace: false)));
            AddItem(menu, "StableRef/Clear", hasItems, Bind(arrayProp, ClearList));
        }

        /// <summary>
        /// Menu callbacks run after the menu closes, when the inspector may already have rebuilt (and disposed)
        /// the <see cref="SerializedObject"/> the menu was opened on — so capture the targets and the property
        /// path, and resolve a fresh property when the item is picked.
        /// </summary>
        private static GenericMenu.MenuFunction Bind(SerializedProperty property, Action<SerializedProperty> action)
        {
            var targets = property.serializedObject.targetObjects;
            string path = property.propertyPath;
            return () =>
            {
                var alive = Array.FindAll(targets, t => t != null);
                if (alive.Length == 0) return;

                var so = new SerializedObject(alive);
                so.Update();
                var prop = so.FindProperty(path);
                if (prop != null) action(prop);
            };
        }

        private static void AddItem(GenericMenu menu, string label, bool enabled, GenericMenu.MenuFunction action)
        {
            var content = new GUIContent(label);
            if (enabled) menu.AddItem(content, false, action);
            else menu.AddDisabledItem(content);
        }

        private static void CopyValue(SerializedProperty property)
        {
            StableRefClipboard.StoreValue(property.managedReferenceValue);
            StableRefClipboard.StoreValueObjectRefs(CollectObjectReferences(property));
        }

        private static void PasteValue(SerializedProperty property)
        {
            if (!StableRefClipboard.HasValue) return;
            if (!StableRefPropertyUtils.IsAssignable(property, StableRefClipboard.ValueType))
            {
                Debug.LogWarning($"[StableRef] Cannot paste '{StableRefClipboard.ValueType.Name}' " +
                                 $"into '{StableRefPropertyUtils.GetBaseType(property).Name}'.");
                return;
            }

            string path = property.propertyPath;
            foreach (var target in property.serializedObject.targetObjects)
            {
                var clone = StableRefClipboard.Deserialize(StableRefClipboard.Json);
                if (clone == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var prop = so.FindProperty(path);
                if (prop == null) continue;
                prop.managedReferenceValue = clone;
                prop.isExpanded = true;
                RestoreObjectReferences(prop, StableRefClipboard.ValueObjectRefs);

                var wrapper = StableRefEntry.FindWrapperOfValue(so, path);
                if (wrapper != null) StableRefEntry.Sync(wrapper);

                so.ApplyModifiedProperties();
            }

            StableRefMultiEdit.Invalidate();
        }

        /// <summary>
        /// Right-click handling for one list / array element: opens the element menu when an unused context click
        /// lands in <paramref name="rect"/>. Called before <c>EndProperty</c> of the element, so the click never
        /// falls through to Unity's own array-element menu (whose Duplicate shares the managed reference) or to
        /// the enclosing list's menu. Nested fields of the value keep their own menus — they consume the event first.
        /// </summary>
        internal static bool HandleElementContextClick(Rect rect, SerializedProperty valueProperty)
        {
            var ev = Event.current;
            if (ev.type != EventType.ContextClick || !rect.Contains(ev.mousePosition)) return false;
            if (valueProperty == null || valueProperty.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (!StableRefPropertyUtils.TryGetParentArray(valueProperty, out _, out _)) return false;

            ShowDirectMenu(valueProperty);
            ev.Use();
            return true;
        }

        private static void PasteAsNewElement(SerializedProperty property)
        {
            if (!StableRefClipboard.HasValue) return;
            if (!StableRefPropertyUtils.TryGetParentArray(property, out var array, out var index)) return;
            if (!StableRefPropertyUtils.IsAssignable(property, StableRefClipboard.ValueType)) return;

            string arrayPath = array.propertyPath;
            foreach (var target in property.serializedObject.targetObjects)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var arr = so.FindProperty(arrayPath);
                if (arr == null || index >= arr.arraySize) continue;

                var clone = StableRefClipboard.Deserialize(StableRefClipboard.Json);
                if (clone == null) continue;

                arr.InsertArrayElementAtIndex(index + 1);
                var inserted = arr.GetArrayElementAtIndex(index + 1);
                bool isEntry = inserted.propertyType != SerializedPropertyType.ManagedReference;
                if (isEntry) StableRefEntry.Clear(inserted);

                var valueProp = isEntry ? inserted.FindPropertyRelative(StableRefEntry.ValueFieldName) : inserted;
                if (valueProp == null || valueProp.propertyType != SerializedPropertyType.ManagedReference) continue;

                valueProp.managedReferenceValue = clone;
                valueProp.isExpanded = true;
                RestoreObjectReferences(valueProp, StableRefClipboard.ValueObjectRefs);
                if (isEntry) StableRefEntry.Sync(inserted);

                so.ApplyModifiedProperties();
            }

            StableRefMultiEdit.Invalidate();
            StableRefListDrawer.InvalidateCache();
        }

        private static void DeleteElement(SerializedProperty property)
        {
            if (!StableRefPropertyUtils.TryGetParentArray(property, out var array, out var index)) return;

            string arrayPath = array.propertyPath;
            string valuePath = property.propertyPath;
            foreach (var target in property.serializedObject.targetObjects)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var arr = so.FindProperty(arrayPath);
                if (arr == null || index >= arr.arraySize) continue;

                var valueProp = so.FindProperty(valuePath);
                bool missing = valueProp != null && valueProp.propertyType == SerializedPropertyType.ManagedReference
                               && (StableRefEntry.HoldsMissingData(so, valueProp)
                                   || StableRefEntry.PointsAtMissingType(valueProp));
                long oldId = missing ? valueProp.managedReferenceId : StableRefEditorUtility.ManagedRefIdNull;

                arr.DeleteArrayElementAtIndex(index);
                so.ApplyModifiedProperties();
                if (missing) StableRefEntry.ReleaseMissingData(target, new[] { oldId });
            }

            StableRefMultiEdit.Invalidate();
            StableRefListDrawer.InvalidateCache();
        }

        private static void DuplicateValue(SerializedProperty property)
        {
            if (!StableRefPropertyUtils.TryGetParentArray(property, out var array, out var index)) return;

            string arrayPath = array.propertyPath;
            string valuePath = property.propertyPath;
            bool expanded = property.isExpanded;

            foreach (var target in property.serializedObject.targetObjects)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var arr = so.FindProperty(arrayPath);
                var source = so.FindProperty(valuePath);
                if (arr == null || source == null || index >= arr.arraySize) continue;

                var value = source.managedReferenceValue;
                if (value == null) continue;
                var copy = StableRefClipboard.Deserialize(StableRefClipboard.Serialize(value));
                if (copy == null) continue;

                var originalSnapshot = arr.GetArrayElementAtIndex(index).Copy();

                arr.InsertArrayElementAtIndex(index + 1);
                var inserted = arr.GetArrayElementAtIndex(index + 1);

                var insertedValue = inserted.propertyType == SerializedPropertyType.ManagedReference
                    ? inserted
                    : inserted.FindPropertyRelative(StableRefEntry.ValueFieldName);
                if (insertedValue == null || insertedValue.propertyType != SerializedPropertyType.ManagedReference)
                    continue;

                insertedValue.managedReferenceValue = copy;
                insertedValue.isExpanded = expanded;

                CopyObjectReferences(originalSnapshot, inserted);

                if (!ReferenceEquals(inserted, insertedValue))
                    StableRefEntry.Sync(inserted);

                so.ApplyModifiedProperties();
            }

            StableRefMultiEdit.Invalidate();
            StableRefListDrawer.InvalidateCache();
        }

        private static void CopyObjectReferences(SerializedProperty source, SerializedProperty dest)
        {
            var srcIter = source.Copy();
            var dstIter = dest.Copy();
            var srcEnd = source.GetEndProperty();

            bool hasSrc = srcIter.Next(enterChildren: true);
            bool hasDst = dstIter.Next(enterChildren: true);

            while (hasSrc && hasDst && !SerializedProperty.EqualContents(srcIter, srcEnd))
            {
                if (srcIter.propertyType == SerializedPropertyType.ObjectReference)
                    dstIter.objectReferenceValue = srcIter.objectReferenceValue;

                bool enter = srcIter.hasChildren
                          && srcIter.propertyType != SerializedPropertyType.ObjectReference;

                hasSrc = srcIter.Next(enter);
                hasDst = dstIter.Next(enter);
            }
        }

        /// <summary>
        /// Replacing an entry whose type is missing (with None or another type) discards its recovery data — the
        /// class may only be missing for a moment (compile errors, switching branches), so it is confirmed first.
        /// Returns <see langword="true"/> when nothing on <paramref name="path"/> is missing or the user confirms.
        /// </summary>
        internal static bool ConfirmDiscardMissing(Object[] targets, string path, string replacement)
        {
            int count = 0;
            foreach (var target in targets)
            {
                if (target == null) continue;
                var so = new SerializedObject(target);
                var prop = so.FindProperty(path);
                if (prop != null && prop.propertyType == SerializedPropertyType.ManagedReference
                    && StableRefEntry.HoldsMissingData(so, prop)) count++;
            }
            if (count == 0) return true;

            return EditorUtility.DisplayDialog("Replace missing reference",
                $"The type of this reference can't be found{(count > 1 ? $" on {count} objects" : "")}. " +
                $"Setting it to {replacement} discards the data kept for recovering it.\n\n" +
                "If the class is only missing for a moment (compile errors, switching branches), cancel, " +
                "fix that and the reference comes back by itself (or via Fix Missing Types).",
                $"Replace with {replacement}", "Cancel");
        }

        private static void SetNone(SerializedProperty property)
        {
            string valuePath = property.propertyPath;
            var targets = property.serializedObject.targetObjects;
            if (!ConfirmDiscardMissing(targets, valuePath, "None")) return;

            foreach (var target in targets)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();

                var current = so.FindProperty(valuePath);
                if (current == null || current.propertyType != SerializedPropertyType.ManagedReference) continue;
                bool missing = StableRefEntry.HoldsMissingData(so, current);
                long oldId = current.managedReferenceId;

                var wrapper = StableRefEntry.FindWrapperOfValue(so, valuePath);
                if (wrapper != null)
                {
                    StableRefEntry.Clear(wrapper);
                }
                else
                {
                    var prop = so.FindProperty(valuePath);
                    if (prop == null) continue;
                    prop.managedReferenceValue = null;
                }

                var valueProp = so.FindProperty(valuePath);
                if (valueProp != null) valueProp.isExpanded = false;

                so.ApplyModifiedProperties();
                if (missing) StableRefEntry.ReleaseMissingData(target, new[] { oldId });
            }

            StableRefMultiEdit.Invalidate();
        }

        private static void CopyList(SerializedProperty arrayProp)
        {
            bool isStable = StableRefPropertyUtils.IsStableRefArray(arrayProp);

            var clip = new StableRefClipboard.ListClipboardData
            {
                ElementBaseType = isStable
                    ? StableRefPropertyUtils.GetStableRefValueBaseType(arrayProp)
                    : StableRefPropertyUtils.GetArrayElementBaseType(arrayProp)
            };

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var elem = arrayProp.GetArrayElementAtIndex(i);
                object value;
                SerializedProperty refProp;

                if (isStable)
                {
                    refProp = elem.FindPropertyRelative(StableRefEntry.ValueFieldName);
                    value = refProp?.managedReferenceValue;
                }
                else
                {
                    refProp = elem;
                    value = elem.managedReferenceValue;
                }

                if (value == null) continue;
                clip.Entries.Add(StableRefClipboard.Serialize(value));
                clip.ObjectRefs.Add(CollectObjectReferences(refProp));
            }

            StableRefClipboard.StoreList(clip);
        }

        private static void PasteList(SerializedProperty arrayProp, bool replace)
        {
            var clip = StableRefClipboard.List;
            if (clip == null || clip.Entries.Count == 0) return;

            bool isStable = StableRefPropertyUtils.IsStableRefArray(arrayProp);
            var targetBaseType = isStable
                ? StableRefPropertyUtils.GetStableRefValueBaseType(arrayProp)
                : StableRefPropertyUtils.GetArrayElementBaseType(arrayProp);

            if (!StableRefPropertyUtils.IsListPasteCompatible(arrayProp, clip.ElementBaseType)
                && (targetBaseType == null || clip.ElementBaseType == null
                    || (!targetBaseType.IsAssignableFrom(clip.ElementBaseType)
                        && !clip.ElementBaseType.IsAssignableFrom(targetBaseType))))
            {
                Debug.LogWarning($"[StableRef] Cannot paste list of '{clip.ElementBaseType?.Name}' " +
                                 $"into '{targetBaseType?.Name}'.");
                return;
            }

            string path = arrayProp.propertyPath;
            var targets = arrayProp.serializedObject.targetObjects;
            List<Object> releaseTargets = null;
            if (replace)
            {
                releaseTargets = TargetsWithMissingElements(targets, path, out int missingCount);
                if (!ConfirmDiscardMissingList(missingCount, releaseTargets.Count, "Replace List")) return;
            }

            foreach (var target in targets)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var arr = so.FindProperty(path);
                if (arr == null) continue;

                if (replace) arr.ClearArray();

                for (int i = 0; i < clip.Entries.Count; i++)
                {
                    var clone = StableRefClipboard.Deserialize(clip.Entries[i]);
                    if (clone == null) continue;

                    arr.InsertArrayElementAtIndex(arr.arraySize);
                    var inserted = arr.GetArrayElementAtIndex(arr.arraySize - 1);

                    SerializedProperty valueProp;
                    if (isStable)
                    {
                        StableRefEntry.Clear(inserted);
                        valueProp = inserted.FindPropertyRelative(StableRefEntry.ValueFieldName);
                        if (valueProp == null) continue;
                        valueProp.managedReferenceValue = clone;
                    }
                    else
                    {
                        valueProp = inserted;
                        inserted.managedReferenceValue = clone;
                    }

                    if (i < clip.ObjectRefs.Count)
                        RestoreObjectReferences(valueProp, clip.ObjectRefs[i]);

                    if (isStable)
                        StableRefEntry.Sync(inserted);
                }

                so.ApplyModifiedProperties();
            }

            ReleaseAfterListDiscard(releaseTargets);
        }

        private static void ClearList(SerializedProperty arrayProp)
        {
            string path = arrayProp.propertyPath;
            var targets = arrayProp.serializedObject.targetObjects;
            var releaseTargets = TargetsWithMissingElements(targets, path, out int missingCount);
            if (!ConfirmDiscardMissingList(missingCount, releaseTargets.Count, "Clear List")) return;

            foreach (var target in targets)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                so.Update();
                var arr = so.FindProperty(path);
                if (arr == null) continue;

                arr.ClearArray();
                so.ApplyModifiedProperties();
            }

            ReleaseAfterListDiscard(releaseTargets);
        }

        /// <summary>
        /// Targets whose list at <paramref name="arrayPath"/> holds at least one missing StableRef entry, and the
        /// total number of such entries — the recovery data a Clear or Paste/Replace of that list would discard.
        /// </summary>
        internal static List<Object> TargetsWithMissingElements(Object[] targets, string arrayPath, out int missingCount)
        {
            missingCount = 0;
            var result = new List<Object>();

            foreach (var target in targets)
            {
                if (target == null) continue;

                var so = new SerializedObject(target);
                var arr = so.FindProperty(arrayPath);
                if (arr == null || !arr.isArray) continue;

                int before = missingCount;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    var elem = arr.GetArrayElementAtIndex(i);
                    var value = elem.propertyType == SerializedPropertyType.ManagedReference
                        ? elem
                        : elem.FindPropertyRelative(StableRefEntry.ValueFieldName);
                    if (value == null || value.propertyType != SerializedPropertyType.ManagedReference) continue;
                    if (StableRefEntry.HoldsMissingData(so, value)) missingCount++;
                }

                if (missingCount > before) result.Add(target);
            }

            return result;
        }

        /// <summary>
        /// Clearing or replacing a list that holds missing entries discards their recovery data — confirmed first,
        /// like replacing a single missing entry. Returns <see langword="true"/> when nothing is missing or the user
        /// confirms.
        /// </summary>
        private static bool ConfirmDiscardMissingList(int missingCount, int objectCount, string action)
        {
            if (missingCount == 0) return true;

            return EditorUtility.DisplayDialog(action,
                $"{missingCount} entr{(missingCount != 1 ? "ies" : "y")} in this list " +
                $"{(objectCount > 1 ? $"(on {objectCount} objects) " : "")}" +
                $"{(missingCount != 1 ? "have types" : "has a type")} that can't be found. " +
                $"{action} discards the data kept for recovering {(missingCount != 1 ? "them" : "it")}.\n\n" +
                "If the classes are only missing for a moment (compile errors, switching branches), cancel, " +
                "fix that and the entries come back by themselves (or via Fix Missing Types).",
                action, "Cancel");
        }

        private static void ReleaseAfterListDiscard(List<Object> releaseTargets)
        {
            if (releaseTargets != null)
                foreach (var target in releaseTargets)
                    StableRefEntry.ReleaseMissingData(target);

            StableRefMultiEdit.Invalidate();
            StableRefListDrawer.InvalidateCache();
        }

        private static Dictionary<string, long> CollectObjectReferences(SerializedProperty root)
        {
            Dictionary<string, long> result = null;
            var iter = root.Copy();
            var end = root.GetEndProperty();
            string rootPath = root.propertyPath;

            bool hasProp = iter.Next(enterChildren: true);
            while (hasProp && !SerializedProperty.EqualContents(iter, end))
            {
                if (iter.propertyType == SerializedPropertyType.ObjectReference)
                {
                    long refId = StableRefEditorUtility.GetObjectReferenceId(iter);
                    if (refId != 0)
                    {
                        string full = iter.propertyPath;
                        if (full.Length > rootPath.Length + 1)
                        {
                            result ??= new Dictionary<string, long>();
                            result[full.Substring(rootPath.Length + 1)] = refId;
                        }
                    }
                }

                bool enter = iter.hasChildren
                          && iter.propertyType != SerializedPropertyType.ObjectReference;
                hasProp = iter.Next(enter);
            }

            return result;
        }

        private static void RestoreObjectReferences(SerializedProperty root, Dictionary<string, long> refs)
        {
            if (refs == null) return;
            string rootPath = root.propertyPath;
            var so = root.serializedObject;

            foreach (var (rel, id) in refs)
            {
                var obj = StableRefEditorUtility.IdToObject(id);
                if (obj == null) continue;

                var prop = so.FindProperty(rootPath + "." + rel);
                if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
                    prop.objectReferenceValue = obj;
            }
        }
    }
}
#endif