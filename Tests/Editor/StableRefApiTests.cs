using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace SST.StableRef.Tests
{
    public class StableRefApiTests
    {
        private const string TempFolder = "Assets/__StableRefTestsTemp";

        private readonly List<Object> _created = new();

        private TestHolder NewHolder()
        {
            var holder = ScriptableObject.CreateInstance<TestHolder>();
            holder.hideFlags = HideFlags.HideAndDontSave;
            _created.Add(holder);
            return holder;
        }

        [TearDown]
        public void TearDown()
        {
            StableRefDrawing.ChildrenDrawer = null;
            foreach (var obj in _created)
                if (obj != null)
                    Object.DestroyImmediate(obj);
            _created.Clear();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void List_EnumeratesValues_AndWorksWithLinq()
        {
            var list = new StableRefList<ITestThing> { new AlphaThing(), new BetaThing(), new BetaThing { B = 3 } };

            int count = 0;
            foreach (ITestThing value in list)
            {
                Assert.IsNotNull(value);
                count++;
            }

            Assert.AreEqual(3, count);
            Assert.IsInstanceOf<BetaThing>(list[1]);
            Assert.AreEqual(2, list.OfType<BetaThing>().Count());
            Assert.AreEqual(3, ((BetaThing)list.Last()).B);
            IReadOnlyList<ITestThing> readOnly = list;
            Assert.AreEqual(3, readOnly.Count);
            Assert.AreEqual(3, list.ToArray().Length);
        }

        [Test]
        public void List_IndexerSetter_KeepsIdForSameType_ResetsForOtherType()
        {
            var list = new StableRefList<ITestThing> { new AlphaThing() };
            var entry = list.Items[0];
            entry.TypeId = "stableref-tests.alpha";
            entry.ValuesData = "#v2\nNumber=1";

            var sameType = new AlphaThing { Number = 2 };
            list[0] = sameType;
            Assert.AreSame(sameType, list[0]);
            Assert.AreSame(entry, list.Items[0], "the entry is reused");
            Assert.AreEqual("stableref-tests.alpha", entry.TypeId, "same type keeps its stable id");

            list[0] = new BetaThing();
            Assert.AreSame(entry, list.Items[0]);
            Assert.IsTrue(string.IsNullOrEmpty(entry.TypeId), "no id of the previous type left behind");
            Assert.IsTrue(string.IsNullOrEmpty(entry.ValuesData));
        }

        [Test]
        public void Set_ResetsMetadataOnlyWhenTypeChanges_AndKeepsMissingEntry()
        {
            var entry = new StableRef<ITestThing>(new AlphaThing()) { TypeId = "stableref-tests.alpha" };

            entry.BoxedValue = new AlphaThing();
            Assert.AreEqual("stableref-tests.alpha", entry.TypeId);

            entry.Set(new BetaThing());
            Assert.IsTrue(string.IsNullOrEmpty(entry.TypeId));

            var missing = new StableRef<ITestThing> { TypeId = "stableref-tests.alpha", ValuesData = "#v2\nNumber=1" };
            missing.Set(null);
            Assert.AreEqual("stableref-tests.alpha", missing.TypeId, "clearing an already empty (missing) entry keeps recovery data");
        }

        [Test]
        public void SetBoxedValues_ReusesEntriesByIndex()
        {
            var list = new StableRefList<ITestThing> { new AlphaThing(), new BetaThing(), new BetaThing() };
            list.Items[0].TypeId = "stableref-tests.alpha";
            list.Items[1].TypeId = "stableref-tests.beta";
            var first = list.Items[0];

            ((StableRefListBase)list).SetBoxedValues(new object[] { new AlphaThing(), new AlphaThing() });

            Assert.AreEqual(2, list.Count);
            Assert.AreSame(first, list.Items[0]);
            Assert.AreEqual("stableref-tests.alpha", list.Items[0].TypeId, "same type: id kept");
            Assert.IsTrue(string.IsNullOrEmpty(list.Items[1].TypeId), "type changed: id reset");
        }

        [Test]
        public void List_ForeachDoesNotAllocate()
        {
            var list = new StableRefList<ITestThing> { new AlphaThing(), new BetaThing() };
            int Iterate()
            {
                int n = 0;
                foreach (var _ in list) n++;
                return n;
            }

            Iterate();
            Assert.That(() => { Iterate(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void BoxedApi_ReadsAndWritesWithoutGenerics()
        {
            var holder = NewHolder();
            var refField = typeof(TestHolder).GetField(nameof(TestHolder.Ref));
            var listField = typeof(TestHolder).GetField(nameof(TestHolder.List));

            Assert.IsTrue(StableRefReflection.IsStableRef(refField.FieldType, out var valueType));
            Assert.AreEqual(typeof(ITestThing), valueType);
            Assert.IsTrue(StableRefReflection.IsStableRefList(listField.FieldType, out var elementType));
            Assert.AreEqual(typeof(ITestThing), elementType);

            var entry = (StableRefBase)refField.GetValue(holder);
            entry.BoxedValue = new BetaThing { B = 5 };
            Assert.AreEqual(5, ((BetaThing)holder.Ref.Value).B);
            Assert.Throws<ArgumentException>(() => entry.BoxedValue = "not a thing");

            var list = (StableRefListBase)listField.GetValue(holder);
            list.SetBoxedValues(new object[] { new AlphaThing(), null, new BetaThing() });
            Assert.AreEqual(3, holder.List.Count);
            Assert.IsNull(list.GetBoxed(1));
            Assert.Throws<ArgumentException>(() => list.SetBoxedValues(new object[] { new BetaThing(), 42 }));
            Assert.AreEqual(3, holder.List.Count, "a rejected batch leaves the list unchanged");
        }

        [Test]
        public void ShallowCopy_GivesNewWrapperOverSameValue()
        {
            var value = new BetaThing();
            var original = new StableRef<ITestThing>(value) { TypeId = "stableref-tests.beta" };

            var copy = original.ShallowCopy();
            copy.Value = new AlphaThing();

            Assert.AreSame(value, original.Value, "assigning on the copy leaves the original alone");
            Assert.AreEqual("stableref-tests.beta", original.ShallowCopy().TypeId);
        }

        [Test]
        public void Registry_ResolvesAssemblyLevelIds()
        {
            Assert.AreEqual("stableref-tests.mapped", StableRefTypeRegistry.GetOrAssignId(typeof(MappedThing)));
            Assert.AreEqual(typeof(MappedThing), StableRefTypeRegistry.GetType("stableref-tests.mapped"));

            var closed = typeof(MappedGeneric<MappedThing>);
            string closedId = StableRefTypeRegistry.GetOrAssignId(closed);
            Assert.AreEqual("stableref-tests.mapped-generic<stableref-tests.mapped>", closedId);
            Assert.AreEqual(closed, StableRefTypeRegistry.GetType(closedId));
        }

        [Test]
        public void Registry_DoesNotInheritRefTypeId_AndResyncDropsBorrowedId()
        {
            Assert.IsNull(StableRefTypeRegistry.GetOrAssignId(typeof(AlphaChild)), "a derived class has no id of its own");
            Assert.AreEqual("stableref-tests.alpha", StableRefTypeRegistry.GetOrAssignId(typeof(AlphaThing)));
            Assert.AreEqual(typeof(AlphaThing), StableRefTypeRegistry.GetType("stableref-tests.alpha"));

            // An entry stamped by an older version with the base class's id.
            var holder = NewHolder();
            holder.Ref.Value = new AlphaChild();
            holder.Ref.TypeId = "stableref-tests.alpha";

            Assert.AreEqual(1, StableRefResync.ResyncObject(holder));
            Assert.IsTrue(string.IsNullOrEmpty(holder.Ref.TypeId), "the borrowed id of the base class is dropped");
        }

        [Test]
        public void Resync_StampsCodeWrittenValues_AndIsIdempotent()
        {
            var holder = NewHolder();
            holder.Ref.Value = new AlphaThing { Inner = new StableRef<ITestThing>(new BetaThing()) };
            holder.List.Add(new MappedThing());

            Assert.AreEqual(3, StableRefResync.ResyncObject(holder));
            Assert.AreEqual("stableref-tests.alpha", holder.Ref.TypeId);
            Assert.AreEqual("stableref-tests.beta", ((AlphaThing)holder.Ref.Value).Inner.TypeId);
            Assert.AreEqual("stableref-tests.mapped", holder.List.Items[0].TypeId);
            StringAssert.Contains("Inner.TypeId=stableref-tests.beta", holder.Ref.ValuesData,
                "outer snapshot taken after the nested entry was stamped");

            Assert.AreEqual(0, StableRefResync.ResyncObject(holder), "nothing left to change");
        }

        [Test]
        public void Resync_RefreshesSnapshotAndReplacesStaleId()
        {
            var holder = NewHolder();
            holder.Ref.Value = new AlphaThing { Number = 1 };
            StableRefResync.ResyncObject(holder);

            ((AlphaThing)holder.Ref.Value).Number = 77;
            Assert.AreEqual(1, StableRefResync.ResyncObject(holder));
            StringAssert.Contains("Number=77", holder.Ref.ValuesData);

            holder.Ref.Value = new BetaThing();
            Assert.AreEqual(1, StableRefResync.ResyncObject(holder));
            Assert.AreEqual("stableref-tests.beta", holder.Ref.TypeId);
        }

        [Test]
        public void Resync_ClearsStaleIdForTypeWithoutId_AndKeepsMissingEntries()
        {
            var holder = NewHolder();
            holder.Ref.Value = new NoIdThing();
            holder.Ref.TypeId = "stableref-tests.alpha";
            holder.Ref.ValuesData = "#v2\nNumber=1";
            holder.List.Add(new BetaThing());
            holder.List.Items[0].TypeId = "stableref-tests.beta";
            holder.List.Items[0].Value = null;

            Assert.AreEqual(1, StableRefResync.ResyncObject(holder));
            Assert.IsTrue(string.IsNullOrEmpty(holder.Ref.TypeId), "the id of another type must not survive");
            Assert.IsTrue(string.IsNullOrEmpty(holder.Ref.ValuesData), "nor the snapshot of that type");
            Assert.AreEqual("stableref-tests.beta", holder.List.Items[0].TypeId, "missing entry untouched");
        }

        [Test]
        public void MetadataReport_CountsEveryEntryInBinaryFormat()
        {
            var holder = NewHolder();
            holder.Ref.Value = new BetaThing();
            holder.Ref.TypeId = "abc";
            holder.List.Add(new BetaThing());
            holder.List.Items[0].TypeId = "missing-id";
            holder.List.Items[0].Value = null;

            var size = StableRefMetadataReport.MeasureObject(holder);

            Assert.AreEqual(2, size.Entries, "empty and missing entries count too");
            // In builds only the ids: "abc" 4+4, "missing-id" 4+12.
            Assert.AreEqual(8 + 16, size.BuildBytes);
            // Editor-only per entry: two empty strings 4+4, two empty lists 4+4.
            Assert.AreEqual(16 + 16, size.EditorOnlyBytes);
            Assert.AreEqual(8, size.SnapshotBytes);
            Assert.AreEqual(12, StableRefMetadataReport.StringBuildBytes("абв"), "UTF-8 bytes, aligned to 4");
        }

        [Test]
        public void ScanTargets_IncludeSubAssets()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__StableRefTestsTemp");
            string path = TempFolder + "/Graph.asset";
            var main = ScriptableObject.CreateInstance<MissingHolder>();
            var node = ScriptableObject.CreateInstance<MissingHolder>();
            node.name = "Node A";
            AssetDatabase.CreateAsset(main, path);
            AssetDatabase.AddObjectToAsset(node, main);
            AssetDatabase.SaveAssetIfDirty(main);

            var targets = StableRefEditorUtility.LoadScanTargets(path);

            Assert.AreEqual(2, targets.Count, "the main asset and its sub-asset");
            Assert.AreSame(main, targets[0], "main asset first");
            Assert.AreSame(node, targets[1]);
            Assert.AreEqual("MissingHolder", StableRefEditorUtility.ScanTargetLabel(main));
            Assert.AreEqual("Node A (MissingHolder)", StableRefEditorUtility.ScanTargetLabel(node));
        }

        [Test]
        public void GetValueProperty_ReturnsManagedReferenceForBothShapes()
        {
            var holder = NewHolder();
            var so = new SerializedObject(holder);

            var wrapped = StableRefEditorUtility.GetValueProperty(so.FindProperty(nameof(TestHolder.Ref)));
            Assert.AreEqual("Ref.Value", wrapped.propertyPath);

            var plain = StableRefEditorUtility.GetValueProperty(so.FindProperty(nameof(TestHolder.Plain)));
            Assert.AreEqual("Plain", plain.propertyPath);

            Assert.IsNull(StableRefEditorUtility.GetValueProperty(so.FindProperty(nameof(TestHolder.Holders))));
        }

        [Test]
        public void MetadataProvider_OverridesSelectorPresentation()
        {
            var meta = StableRefTypeMetadata.Get(typeof(MetaThing));
            Assert.AreEqual("Pretty Meta", meta.DisplayName);
            Assert.AreEqual("Meta/Sub", meta.Category);
            Assert.AreEqual("tip", meta.Tooltip);
            Assert.AreEqual(Color.red, meta.Color);

            var fallback = StableRefTypeMetadata.Get(typeof(AlphaThing));
            Assert.AreEqual("AlphaThing", fallback.DisplayName);
            Assert.AreEqual("", fallback.Category);

            var holder = NewHolder();
            var valueProp = new SerializedObject(holder).FindProperty("Ref.Value");
            var entries = StableRefPropertyUtils.GetEntries(valueProp);
            var entry = entries.Single(e => e.Type == typeof(MetaThing));
            Assert.AreEqual("Pretty Meta", entry.Name);
            Assert.AreEqual("Meta/Sub", entry.Category);
            StringAssert.Contains("metathing", entry.SearchText, "still found by its class name");
            Assert.AreSame(entry, entries[0], "the lowest SortOrder comes first");
        }

        [Test]
        public void ChildrenDrawer_ProvidesHeightOfExpandedValue()
        {
            var holder = NewHolder();
            holder.Ref.Value = new BetaThing();
            var valueProp = new SerializedObject(holder).FindProperty("Ref.Value");
            valueProp.isExpanded = true;

            StableRefDrawing.ChildrenDrawer = new FixedHeightDrawer();
            float expected = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing + 100f;
            Assert.AreEqual(expected, StableRefSelectorField.GetHeight(valueProp, mixed: false), 0.001f);
            Assert.AreEqual(EditorGUIUtility.singleLineHeight, StableRefSelectorField.GetHeight(valueProp, mixed: true),
                0.001f, "never asked for a mixed field");
        }

        private sealed class FixedHeightDrawer : IStableRefChildrenDrawer
        {
            public float GetChildrenHeight(SerializedProperty valueProperty) => 100f;
            public void DrawChildren(Rect position, SerializedProperty valueProperty) { }
        }
    }
}
