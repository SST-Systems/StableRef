using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef.Tests
{
    public class StableRefEntryTests
    {
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
            foreach (var obj in _created)
                if (obj != null)
                    Object.DestroyImmediate(obj);
            _created.Clear();
        }

        private static AlphaThing MakeFilledAlpha(Object direct, Object nestedRef)
        {
            return new AlphaThing
            {
                Number = 7,
                BigNumber = long.MaxValue,
                Precise = 3.14159265358979,
                Text = "a=b\nc%d",
                Kind = TestKind.C,
                Tint = new Color(0.1f, 0.2f, 0.3f, 0.4f),
                Pos = new Vector3(1.5f, -2.25f, 3f),
                Ints = new List<int> { 1, 2, 3 },
                Nested = new NestedData
                {
                    Name = "nested",
                    Weights = new[] { 0.5f, 1.25f },
                    Ref = nestedRef
                },
                Direct = direct,
                Inner = new StableRef<ITestThing> { Value = new BetaThing { B = 42 } }
            };
        }

        [Test]
        public void Recreate_RestoresRecursiveData_AndNestedStableRef()
        {
            var holder = NewHolder();
            var direct = NewHolder();
            var nestedRef = NewHolder();

            var so = new SerializedObject(holder);
            so.FindProperty("Ref.Value").managedReferenceValue = MakeFilledAlpha(direct, nestedRef);
            so.ApplyModifiedProperties();

            so.Update();
            Assert.IsTrue(StableRefEntry.Sync(so.FindProperty("Ref.Value.Inner")), "inner Sync should stamp");
            Assert.IsTrue(StableRefEntry.Sync(so.FindProperty("Ref")), "outer Sync should stamp");
            so.ApplyModifiedProperties();

            string valuesData = new SerializedObject(holder).FindProperty("Ref.ValuesData").stringValue;
            StringAssert.StartsWith("#v2", valuesData, "v2 snapshot header expected");
            StringAssert.Contains("Ints.Array.size", valuesData, "array sizes must be captured");

            so.Update();
            so.FindProperty("Ref.Value").managedReferenceValue = null;
            so.ApplyModifiedProperties();

            so.Update();
            Assert.IsTrue(StableRefEntry.IsMissing(so.FindProperty("Ref")));
            Assert.IsTrue(StableRefEntry.TryRecreate(so.FindProperty("Ref")));
            so.ApplyModifiedProperties();

            var alpha = holder.Ref.Value as AlphaThing;
            Assert.NotNull(alpha, "outer value recreated");
            Assert.AreEqual(7, alpha.Number);
            Assert.AreEqual(long.MaxValue, alpha.BigNumber);
            Assert.AreEqual(3.14159265358979, alpha.Precise, 1e-12);
            Assert.AreEqual("a=b\nc%d", alpha.Text);
            Assert.AreEqual(TestKind.C, alpha.Kind);
            Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), alpha.Tint);
            Assert.AreEqual(new Vector3(1.5f, -2.25f, 3f), alpha.Pos);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, alpha.Ints);
            Assert.AreEqual("nested", alpha.Nested.Name);
            CollectionAssert.AreEqual(new[] { 0.5f, 1.25f }, alpha.Nested.Weights);
            Assert.AreSame(direct, alpha.Direct, "top-level object ref restored");
            Assert.AreSame(nestedRef, alpha.Nested.Ref, "nested object ref restored");

            Assert.AreEqual("stableref-tests.beta", alpha.Inner.TypeId,
                "nested entry metadata restored by the outer snapshot");
            Assert.IsNull(alpha.Inner.Value, "nested value needs its own fix pass");

            int fixedCount = StableRefMissingTypesWindow.FixTarget(holder, out int unresolved);
            Assert.AreEqual(0, unresolved);
            Assert.GreaterOrEqual(fixedCount, 1);

            var beta = (holder.Ref.Value as AlphaThing)?.Inner.Value as BetaThing;
            Assert.NotNull(beta, "nested StableRef recreated by fix pass");
            Assert.AreEqual(42, beta.B, "nested value data restored from its own snapshot");
        }

        [Test]
        public void Recreate_ReadsLegacyV1Snapshot_WithEnumByIndex()
        {
            var holder = NewHolder();
            var so = new SerializedObject(holder);
            so.FindProperty("Ref.TypeId").stringValue = "stableref-tests.alpha";
            so.FindProperty("Ref.ValuesData").stringValue = "Number=7\nKind=2";
            so.ApplyModifiedProperties();

            so.Update();
            Assert.IsTrue(StableRefEntry.TryRecreate(so.FindProperty("Ref")));
            so.ApplyModifiedProperties();

            var alpha = holder.Ref.Value as AlphaThing;
            Assert.NotNull(alpha);
            Assert.AreEqual(7, alpha.Number);
            Assert.AreEqual(TestKind.C, alpha.Kind, "legacy enum encoding is by enumValueIndex");
        }

        [Test]
        public void TryRecreate_LeavesUnresolvableEntryUntouched()
        {
            var holder = NewHolder();
            var so = new SerializedObject(holder);
            so.FindProperty("Ref.TypeId").stringValue = "stableref-tests.does-not-exist";
            so.FindProperty("Ref.ValuesData").stringValue = "#v2\nNumber=1";
            so.ApplyModifiedProperties();

            so.Update();
            Assert.IsFalse(StableRefEntry.TryRecreate(so.FindProperty("Ref")));
            so.ApplyModifiedProperties();

            so.Update();
            Assert.AreEqual("stableref-tests.does-not-exist", so.FindProperty("Ref.TypeId").stringValue);
            Assert.AreEqual("#v2\nNumber=1", so.FindProperty("Ref.ValuesData").stringValue);
        }

        [Test]
        public void Clear_ResetsValueAndAllMetadata()
        {
            var holder = NewHolder();
            var so = new SerializedObject(holder);
            so.FindProperty("Ref.Value").managedReferenceValue = new BetaThing { B = 5 };
            so.ApplyModifiedProperties();
            so.Update();
            StableRefEntry.Sync(so.FindProperty("Ref"));
            so.ApplyModifiedProperties();

            so.Update();
            StableRefEntry.Clear(so.FindProperty("Ref"));
            so.ApplyModifiedProperties();

            so.Update();
            Assert.IsNull(holder.Ref.Value);
            Assert.AreEqual(string.Empty, so.FindProperty("Ref.TypeId").stringValue);
            Assert.AreEqual(string.Empty, so.FindProperty("Ref.TypeDisplayName").stringValue);
            Assert.AreEqual(string.Empty, so.FindProperty("Ref.ValuesData").stringValue);
            Assert.AreEqual(0, so.FindProperty("Ref.ObjectRefs").arraySize);
            Assert.AreEqual(0, so.FindProperty("Ref.ObjectRefPaths").arraySize);
        }

        [Test]
        public void Registry_AttributeIdStaysCanonical_AfterGuidLookup()
        {
            string path = AssetDatabase.FindAssets("t:MonoScript AttrFileThing")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => AssetDatabase.LoadAssetAtPath<MonoScript>(p)?.GetClass() == typeof(AttrFileThing));
            Assert.NotNull(path, "AttrFileThing script asset must be findable");
            string guid = AssetDatabase.AssetPathToGUID(path);

            Assert.AreEqual(typeof(AttrFileThing), StableRefTypeRegistry.GetType(guid),
                "legacy GUID id must keep resolving");
            Assert.AreEqual("stableref-tests.attr-file", StableRefTypeRegistry.GetOrAssignId(typeof(AttrFileThing)),
                "attribute id must win for stamping even after a GUID lookup");
            Assert.AreEqual(typeof(AttrFileThing), StableRefTypeRegistry.GetType("stableref-tests.attr-file"));
        }

        [Test]
        public void Registry_FileBackedTypeGetsItsMonoScriptGuid()
        {
            string id = StableRefTypeRegistry.GetOrAssignId(typeof(GuidBackedThing));
            Assert.NotNull(id);
            Assert.AreEqual(32, id.Length);
            Assert.AreEqual(typeof(GuidBackedThing), StableRefTypeRegistry.GetType(id));
        }

        [Test]
        public void GetEntries_ExcludesTypesSerializeReferenceCannotHold()
        {
            var holder = NewHolder();
            var so = new SerializedObject(holder);
            var entries = StableRefPropertyUtils.GetEntries(so.FindProperty("Ref.Value"));
            var types = entries.Select(e => e.Type).ToArray();

            CollectionAssert.Contains(types, typeof(AlphaThing));
            CollectionAssert.Contains(types, typeof(BetaThing));
            CollectionAssert.Contains(types, typeof(AttrFileThing));
            CollectionAssert.Contains(types, typeof(GuidBackedThing));
            CollectionAssert.DoesNotContain(types, typeof(StructThing));
            CollectionAssert.DoesNotContain(types, typeof(MonoThing));
            CollectionAssert.DoesNotContain(types, typeof(NoCtorThing));
        }

        [Test]
        public void Snapshot_IsCultureInvariant()
        {
            var prevCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                var holder = NewHolder();
                var so = new SerializedObject(holder);
                so.FindProperty("Ref.Value").managedReferenceValue =
                    new AlphaThing { Pos = new Vector3(1.25f, 0f, 0f) };
                so.ApplyModifiedProperties();

                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                so.Update();
                StableRefEntry.Sync(so.FindProperty("Ref"));
                so.ApplyModifiedProperties();

                string valuesData = new SerializedObject(holder).FindProperty("Ref.ValuesData").stringValue;
                StringAssert.Contains("1.25", valuesData);
                StringAssert.DoesNotContain("1,25", valuesData);

                so.Update();
                so.FindProperty("Ref.Value").managedReferenceValue = null;
                so.ApplyModifiedProperties();

                Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
                so.Update();
                Assert.IsTrue(StableRefEntry.TryRecreate(so.FindProperty("Ref")));
                so.ApplyModifiedProperties();

                Assert.AreEqual(1.25f, ((AlphaThing)holder.Ref.Value).Pos.x);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prevCulture;
            }
        }

        [Test]
        public void DuplicateValue_CopiesStableRefListElement()
        {
            var holder = NewHolder();
            holder.List.Add(new BetaThing { B = 7 });

            var so = new SerializedObject(holder);
            so.Update();
            StableRefEntry.Sync(so.FindProperty("List._items.Array.data[0]"));
            so.ApplyModifiedProperties();

            so.Update();
            var valueProp = so.FindProperty("List._items.Array.data[0].Value");
            var duplicate = typeof(StableRefContextMenu).GetMethod(
                "DuplicateValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(duplicate);
            duplicate.Invoke(null, new object[] { valueProp });

            Assert.AreEqual(2, holder.List.Count, "duplicate inserted");
            var first = holder.List[0].Value as BetaThing;
            var second = holder.List[1].Value as BetaThing;
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.AreEqual(7, second.B, "duplicated data");
            Assert.AreNotSame(first, second, "independent instances");
            Assert.AreEqual("stableref-tests.beta", holder.List[1].TypeId, "duplicate metadata stamped");
        }
    }
}
