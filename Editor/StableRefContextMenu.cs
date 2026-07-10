#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    internal static class StableRefContextMenu
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
            AddItem(menu, "Copy", hasItems, () => CopyList(arrayProp));
            AddItem(menu, "Paste/Replace", clipFits, () => PasteList(arrayProp, replace: true));
            AddItem(menu, "Paste/Append", clipFits, () => PasteList(arrayProp, replace: false));
            AddItem(menu, "Clear", hasItems, () => ClearList(arrayProp));
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
            AddItem(menu, "Copy", hasValue, () => CopyValue(prop));
            AddItem(menu, "Paste", clipFits, () => PasteValue(prop));
            AddItem(menu, "Duplicate", hasValue && isInArray, () => DuplicateValue(prop));

            if (hasValue)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Set to None"), false, () => SetNone(prop));
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

                AddItem(menu, "StableRef/Copy", hasValue, () => CopyValue(prop));
                AddItem(menu, "StableRef/Paste", clipFits, () => PasteValue(prop));
                AddItem(menu, "StableRef/Duplicate", hasValue && isInArray, () => DuplicateValue(prop));
                if (hasValue)
                    menu.AddItem(new GUIContent("StableRef/Set to None"), false, () => SetNone(prop));
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

            AddItem(menu, "StableRef/Copy", hasItems, () => CopyList(arrayProp));
            AddItem(menu, "StableRef/Paste/Replace", clipFits, () => PasteList(arrayProp, replace: true));
            AddItem(menu, "StableRef/Paste/Append", clipFits, () => PasteList(arrayProp, replace: false));
            AddItem(menu, "StableRef/Clear", hasItems, () => ClearList(arrayProp));
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
                Debug.LogWarning($"[StableRefSelector] Cannot paste '{StableRefClipboard.ValueType.Name}' " +
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
                so.ApplyModifiedProperties();
            }
        }

        private static void DuplicateValue(SerializedProperty property)
        {
            if (!StableRefPropertyUtils.TryGetParentArray(property, out var array, out var index)) return;

            var value = property.managedReferenceValue;
            if (value == null) return;

            string json = StableRefClipboard.Serialize(value);
            var copy = StableRefClipboard.Deserialize(json);
            if (copy == null) return;

            var so = property.serializedObject;
            so.Update();

            var originalSnapshot = array.GetArrayElementAtIndex(index).Copy();

            array.InsertArrayElementAtIndex(index + 1);
            var inserted = array.GetArrayElementAtIndex(index + 1);
            inserted.managedReferenceValue = copy;
            inserted.isExpanded = property.isExpanded;

            CopyObjectReferences(originalSnapshot, inserted);

            so.ApplyModifiedProperties();
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
                          && srcIter.propertyType != SerializedPropertyType.ObjectReference
                          && srcIter.propertyType != SerializedPropertyType.ManagedReference;

                hasSrc = srcIter.Next(enter);
                hasDst = dstIter.Next(enter);
            }
        }

        private static void SetNone(SerializedProperty property)
        {
            var so = property.serializedObject;
            so.Update();
            property.managedReferenceValue = null;
            property.isExpanded = false;
            so.ApplyModifiedProperties();
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
                    refProp = elem.FindPropertyRelative("Value");
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
                Debug.LogWarning($"[StableRefSelector] Cannot paste list of '{clip.ElementBaseType?.Name}' " +
                                 $"into '{targetBaseType?.Name}'.");
                return;
            }

            string path = arrayProp.propertyPath;
            foreach (var target in arrayProp.serializedObject.targetObjects)
            {
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
                        var typeIdP = inserted.FindPropertyRelative("TypeId");
                        if (typeIdP != null) typeIdP.stringValue = string.Empty;
                        valueProp = inserted.FindPropertyRelative("Value");
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
                }

                so.ApplyModifiedProperties();
            }
        }

        private static void ClearList(SerializedProperty arrayProp)
        {
            var so = arrayProp.serializedObject;
            so.Update();
            arrayProp.ClearArray();
            so.ApplyModifiedProperties();
        }

        private static Dictionary<string, int> CollectObjectReferences(SerializedProperty root)
        {
            Dictionary<string, int> result = null;
            var iter = root.Copy();
            var end = root.GetEndProperty();
            string rootPath = root.propertyPath;

            bool hasProp = iter.Next(enterChildren: true);
            while (hasProp && !SerializedProperty.EqualContents(iter, end))
            {
                if (iter.propertyType == SerializedPropertyType.ObjectReference
                    && iter.objectReferenceInstanceIDValue != 0)
                {
                    string full = iter.propertyPath;
                    if (full.Length > rootPath.Length + 1)
                    {
                        result ??= new Dictionary<string, int>();
                        result[full.Substring(rootPath.Length + 1)] = iter.objectReferenceInstanceIDValue;
                    }
                }

                bool enter = iter.hasChildren
                          && iter.propertyType != SerializedPropertyType.ObjectReference
                          && iter.propertyType != SerializedPropertyType.ManagedReference;
                hasProp = iter.Next(enter);
            }

            return result;
        }

        private static void RestoreObjectReferences(SerializedProperty root, Dictionary<string, int> refs)
        {
            if (refs == null) return;
            string rootPath = root.propertyPath;
            var so = root.serializedObject;

            foreach (var (rel, id) in refs)
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                if (obj == null) continue;

                var prop = so.FindProperty(rootPath + "." + rel);
                if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
                    prop.objectReferenceValue = obj;
            }
        }
    }
}
#endif