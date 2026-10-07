using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Element commands of the context menu, multi-object editing and the release of Unity's native
    /// missing-type data.
    /// </summary>
    public class StableRefElementCommandTests
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

        [SetUp]
        public void SetUp() => StableRefMultiEdit.Invalidate();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _created)
                if (obj != null)
                    Object.DestroyImmediate(obj);
            _created.Clear();
            StableRefMultiEdit.Invalidate();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        private static void Menu(string command, SerializedProperty property)
        {
            var method = typeof(StableRefContextMenu).GetMethod(command, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method, command);
            try
            {
                method.Invoke(null, new object[] { property });
            }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        private static SerializedObject Shared(params Object[] targets) => new(targets);

        private static void SyncList(TestHolder holder)
        {
            var so = new SerializedObject(holder);
            for (int i = 0; i < holder.List.Count; i++)
                StableRefEntry.Sync(so.FindProperty($"List._items.Array.data[{i}]"));
            so.ApplyModifiedProperties();
        }

        private static void SyncRef(TestHolder holder)
        {
            var so = new SerializedObject(holder);
            StableRefEntry.Sync(so.FindProperty("Ref"));
            so.ApplyModifiedProperties();
        }

        [Test]
        public void TryGetParentArray_MatchesEveryListShape()
        {
            var holder = NewHolder();
            holder.List.Add(new BetaThing());
            holder.RawList.Add(new StableRef<ITestThing> { Value = new BetaThing() });
            holder.Arr = new[] { new StableRef<ITestThing> { Value = new BetaThing() } };
            holder.PlainList.Add(new NoIdThing());
            var so = new SerializedObject(holder);

            foreach (var (path, arrayPath) in new[]
                     {
                         ("List._items.Array.data[0].Value", "List._items"),
                         ("RawList.Array.data[0].Value", "RawList"),
                         ("Arr.Array.data[0].Value", "Arr"),
                         ("PlainList.Array.data[0]", "PlainList")
                     })
            {
                Assert.IsTrue(StableRefPropertyUtils.TryGetParentArray(so.FindProperty(path), out var array, out int index), path);
                Assert.AreEqual(arrayPath, array.propertyPath, path);
                Assert.AreEqual(0, index, path);
            }

            Assert.IsFalse(StableRefPropertyUtils.TryGetParentArray(so.FindProperty("Ref.Value"), out _, out _));
            Assert.IsFalse(StableRefPropertyUtils.TryGetParentArray(so.FindProperty("Plain"), out _, out _));
        }

        [Test]
        public void TryGetParentArray_PlainFieldNamedValue_IsNotAnElement()
        {
            var holder = NewHolder();
            holder.Holders.Add(new PlainValueHolder { Value = new NoIdThing() });
            holder.Holders.Add(new PlainValueHolder { Value = new NoIdThing() });
            var so = new SerializedObject(holder);

            var value = so.FindProperty("Holders.Array.data[0].Value");
            Assert.IsFalse(StableRefPropertyUtils.TryGetParentArray(value, out _, out _),
                "a [SerializeReference] field named Value on a plain element is not a StableRef element");

            Menu("DeleteElement", value);
            Assert.AreEqual(2, holder.Holders.Count, "Delete must not remove the outer element");
        }

        [Test]
        public void Duplicate_MultiSelection_EachTargetCopiesItsOwnElement()
        {
            var a = NewHolder();
            a.List.Add(new BetaThing { B = 1 });
            SyncList(a);
            var b = NewHolder();
            b.List.Add(new BetaThing { B = 2 });
            SyncList(b);

            Menu("DuplicateValue", Shared(a, b).FindProperty("List._items.Array.data[0].Value"));

            Assert.AreEqual(2, a.List.Count);
            Assert.AreEqual(2, b.List.Count);
            Assert.AreEqual(1, ((BetaThing)a.List[1]).B);
            Assert.AreEqual(2, ((BetaThing)b.List[1]).B, "b duplicated its own value, not a's");
            Assert.AreNotSame(a.List[0], a.List[1]);
            Assert.AreNotSame(a.List[1], b.List[1], "no instance shared between objects");
            Assert.AreEqual("stableref-tests.beta", b.List.Items[1].TypeId);
        }

        [Test]
        public void Duplicate_WorksInEveryListShape_AsDeepCopy()
        {
            var holder = NewHolder();
            holder.RawList.Add(new StableRef<ITestThing> { Value = new BetaThing { B = 5 } });
            holder.Arr = new[] { new StableRef<ITestThing> { Value = new BetaThing { B = 6 } } };
            holder.PlainList.Add(new NoIdThing { N = 7 });

            Menu("DuplicateValue", new SerializedObject(holder).FindProperty("RawList.Array.data[0].Value"));
            Menu("DuplicateValue", new SerializedObject(holder).FindProperty("Arr.Array.data[0].Value"));
            Menu("DuplicateValue", new SerializedObject(holder).FindProperty("PlainList.Array.data[0]"));

            Assert.AreEqual(5, ((BetaThing)holder.RawList[1].Value).B);
            Assert.AreNotSame(holder.RawList[0].Value, holder.RawList[1].Value);
            Assert.AreEqual("stableref-tests.beta", holder.RawList[1].TypeId);
            Assert.AreEqual(6, ((BetaThing)holder.Arr[1].Value).B);
            Assert.AreNotSame(holder.Arr[0].Value, holder.Arr[1].Value);
            Assert.AreEqual(7, ((NoIdThing)holder.PlainList[1]).N);
            Assert.AreNotSame(holder.PlainList[0], holder.PlainList[1],
                "Unity's own Duplicate would share the managed reference");
        }

        [Test]
        public void Duplicate_KeepsObjectReferences()
        {
            var holder = NewHolder();
            var referenced = NewHolder();
            holder.List.Add(new AlphaThing { Number = 9, Direct = referenced });
            SyncList(holder);

            Menu("DuplicateValue", new SerializedObject(holder).FindProperty("List._items.Array.data[0].Value"));

            var copy = (AlphaThing)holder.List[1];
            Assert.AreEqual(9, copy.Number);
            Assert.AreSame(referenced, copy.Direct);
        }

        [Test]
        public void PasteAsNewElement_InsertsBelow_PerTarget()
        {
            var a = NewHolder();
            a.List.Add(new AlphaThing());
            a.List.Add(new AlphaThing());
            SyncList(a);
            var b = NewHolder();
            b.List.Add(new AlphaThing());
            b.List.Add(new AlphaThing());
            SyncList(b);

            StableRefClipboard.StoreValue(new BetaThing { B = 11 });
            Menu("PasteAsNewElement", Shared(a, b).FindProperty("List._items.Array.data[0].Value"));

            Assert.AreEqual(3, a.List.Count);
            Assert.AreEqual(3, b.List.Count);
            Assert.IsInstanceOf<BetaThing>(a.List[1], "inserted right below the clicked element");
            Assert.IsInstanceOf<AlphaThing>(a.List[2]);
            Assert.AreEqual(11, ((BetaThing)b.List[1]).B);
            Assert.AreNotSame(a.List[1], b.List[1]);
            Assert.AreEqual("stableref-tests.beta", a.List.Items[1].TypeId);
            Assert.IsFalse(string.IsNullOrEmpty(a.List.Items[1].TypeDisplayName));
        }

        [Test]
        public void PasteAsNewElement_IgnoresIncompatibleClipboard()
        {
            var holder = NewHolder();
            holder.List.Add(new AlphaThing());
            SyncList(holder);

            StableRefClipboard.StoreValue(new System.Text.StringBuilder());
            Menu("PasteAsNewElement", new SerializedObject(holder).FindProperty("List._items.Array.data[0].Value"));

            Assert.AreEqual(1, holder.List.Count);
        }

        [Test]
        public void DeleteElement_RemovesFromEveryTarget()
        {
            var a = NewHolder();
            a.List.Add(new AlphaThing());
            a.List.Add(new BetaThing());
            SyncList(a);
            var b = NewHolder();
            b.List.Add(new AlphaThing());
            b.List.Add(new BetaThing());
            SyncList(b);

            Menu("DeleteElement", Shared(a, b).FindProperty("List._items.Array.data[0].Value"));

            Assert.AreEqual(1, a.List.Count);
            Assert.AreEqual(1, b.List.Count);
            Assert.IsInstanceOf<BetaThing>(b.List[0]);
            Assert.AreEqual("stableref-tests.beta", b.List.Items[0].TypeId, "metadata moves with the element");
        }

        [Test]
        public void ClearList_HealthyList_ClearsEveryTarget()
        {
            var a = NewHolder();
            a.List.Add(new AlphaThing());
            a.List.Add(new BetaThing());
            SyncList(a);
            var b = NewHolder();
            b.List.Add(new BetaThing());
            SyncList(b);

            Menu("ClearList", Shared(a, b).FindProperty("List._items"));

            Assert.AreEqual(0, a.List.Count);
            Assert.AreEqual(0, b.List.Count);
        }

        [Test]
        public void TargetsWithMissingElements_CountsOnlyMissingEntries()
        {
            var broken = NewHolder();
            broken.List.Add(new BetaThing());
            broken.List.Add(new BetaThing());
            broken.List.Add(new BetaThing());
            SyncList(broken);
            broken.List.Items[0].Value = null; // missing: stable id kept, value gone
            broken.List.Items[2].Value = null;

            var healthy = NewHolder();
            healthy.List.Add(new BetaThing());
            healthy.List.Add(null);
            SyncList(healthy);

            var targets = StableRefContextMenu.TargetsWithMissingElements(
                new Object[] { broken, healthy }, "List._items", out int missing);

            CollectionAssert.AreEqual(new Object[] { broken }, targets);
            Assert.AreEqual(2, missing, "an empty element without a stable id is not missing");
        }

        [Test]
        public void SetNone_ClearsValueAndMetadata_OnEveryTarget()
        {
            var a = NewHolder();
            a.Ref.Value = new AlphaThing { Number = 1 };
            SyncRef(a);
            var b = NewHolder();
            b.Ref.Value = new BetaThing { B = 2 };
            SyncRef(b);

            Menu("SetNone", Shared(a, b).FindProperty("Ref.Value"));

            foreach (var holder in new[] { a, b })
            {
                Assert.IsNull(holder.Ref.Value);
                Assert.IsTrue(string.IsNullOrEmpty(holder.Ref.TypeId));
                Assert.IsTrue(string.IsNullOrEmpty(holder.Ref.ValuesData));
            }
        }

        [Test]
        public void IsMixed_ComparesTypesNotValues()
        {
            bool Mixed(ITestThing x, ITestThing y)
            {
                StableRefMultiEdit.Invalidate();
                var a = NewHolder();
                a.Plain = x;
                var b = NewHolder();
                b.Plain = y;
                return StableRefMultiEdit.IsMixed(Shared(a, b).FindProperty("Plain"));
            }

            Assert.IsTrue(Mixed(new AlphaThing(), new BetaThing()));
            Assert.IsTrue(Mixed(new AlphaThing(), null));
            Assert.IsFalse(Mixed(new BetaThing { B = 1 }, new BetaThing { B = 2 }));
            Assert.IsFalse(Mixed(null, null));

            var single = NewHolder();
            single.Plain = new AlphaThing();
            Assert.IsFalse(StableRefMultiEdit.IsMixed(new SerializedObject(single).FindProperty("Plain")));
        }

        [Test]
        public void IsMixed_WithWrapper_StampsEachTargetsOwnId()
        {
            var a = NewHolder();
            a.Ref.Value = new AlphaThing();
            var b = NewHolder();
            b.Ref.Value = new BetaThing();

            Assert.IsTrue(StableRefMultiEdit.IsMixed(Shared(a, b).FindProperty("Ref.Value"), "Ref"));
            Assert.AreEqual("stableref-tests.alpha", a.Ref.TypeId);
            Assert.AreEqual("stableref-tests.beta", b.Ref.TypeId, "b must not receive a's id");
        }

        [Test]
        public void IsMixed_DistinguishesMissingEntriesOnlyWithWrapper()
        {
            var a = NewHolder();
            a.Ref.TypeId = "stableref-tests.gone-a";
            var b = NewHolder();
            b.Ref.TypeId = "stableref-tests.gone-b";
            var shared = Shared(a, b);

            Assert.IsFalse(StableRefMultiEdit.IsMixed(shared.FindProperty("Ref.Value")), "both values are None");
            Assert.IsTrue(StableRefMultiEdit.IsMixed(shared.FindProperty("Ref.Value"), "Ref"),
                "the cached plain answer must not be reused for the wrapper check");
        }

        [Test]
        public void MultiCapture_RecoversEachTargetWithItsOwnValues()
        {
            var a = NewHolder();
            a.Ref.Value = new BetaThing { B = 111 };
            var b = NewHolder();
            b.Ref.Value = new BetaThing { B = 222 };
            var shared = Shared(a, b);

            StableRefMultiEdit.IsMixed(shared.FindProperty("Ref.Value"), "Ref");
            StableRefMultiEdit.CaptureAllTargets(shared.FindProperty("Ref"));
            StringAssert.Contains("111", a.Ref.ValuesData);
            StringAssert.DoesNotContain("222", a.Ref.ValuesData);
            StringAssert.Contains("222", b.Ref.ValuesData);

            foreach (var holder in new[] { a, b })
            {
                var so = new SerializedObject(holder);
                so.FindProperty("Ref.Value").managedReferenceValue = null;
                so.ApplyModifiedProperties();
                so.Update();
                Assert.IsTrue(StableRefEntry.IsMissing(so.FindProperty("Ref")));
                Assert.IsTrue(StableRefEntry.TryRecreate(so.FindProperty("Ref")));
                so.ApplyModifiedProperties();
            }

            Assert.AreEqual(111, ((BetaThing)a.Ref.Value).B);
            Assert.AreEqual(222, ((BetaThing)b.Ref.Value).B);
        }

        private MissingHolder LoadMissingAsset(string fields, string references)
        {
            var probe = ScriptableObject.CreateInstance<MissingHolder>();
            string scriptGuid = AssetDatabase.AssetPathToGUID(
                AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(probe)));
            Object.DestroyImmediate(probe);
            Assert.IsFalse(string.IsNullOrEmpty(scriptGuid), "MissingHolder needs its own script file");

            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__StableRefTestsTemp");
            string path = TempFolder + "/Missing.asset";
            File.WriteAllText(path,
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n" +
                "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n" +
                "  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" +
                $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n  m_Name: Missing\n" +
                "  m_EditorClassIdentifier: \n" + fields +
                "  references:\n    version: 2\n    RefIds:\n" + references);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.NotNull(asset);
            Assert.IsTrue(SerializationUtility.HasManagedReferencesWithMissingTypes(asset), "test asset has a missing type");
            return asset;
        }

        private static string MissingRef(long rid) =>
            $"    - rid: {rid}\n      type: {{class: GoneThing, ns: SST.StableRef.Tests, asm: SST.StableRef.Editor.Tests}}\n" +
            "      data:\n        X: 1\n";

        [Test]
        public void RemoveButton_ReleasesMissingDataOnlyWhenRemovingAMissingEntry()
        {
            var holder = NewHolder();
            holder.List.Add(new BetaThing());
            holder.List.Add(new BetaThing());
            SyncList(holder);
            holder.List.Items[0].Value = null; // a missing entry: stable id kept, value gone

            var so = new SerializedObject(holder);
            var list = new UnityEditorInternal.ReorderableList(so, so.FindProperty("List._items"));
            var collect = typeof(StableRefListDrawer).GetMethod("TargetsRemovingMissing", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(collect);

            list.index = 1;
            CollectionAssert.IsEmpty((List<Object>)collect.Invoke(null, new object[] { list }), "healthy element");

            list.index = 0;
            CollectionAssert.AreEqual(new Object[] { holder }, (List<Object>)collect.Invoke(null, new object[] { list }));
        }

        [Test]
        public void ReleaseMissingData_ClearsOnceNothingNeedsIt()
        {
            var asset = LoadMissingAsset(
                "  A:\n    rid: 1000\n  B:\n    rid: -2\n  Ref:\n    TypeId: \n    TypeDisplayName: \n" +
                "    ObjectRefs: []\n    ObjectRefPaths: []\n    ValuesData: \n    Value:\n      rid: -2\n",
                MissingRef(1000));

            var so = new SerializedObject(asset);
            so.FindProperty("A").managedReferenceValue = null;
            so.ApplyModifiedProperties();
            StableRefEntry.ReleaseMissingData(asset, new long[] { 1000 });

            Assert.IsFalse(SerializationUtility.HasManagedReferencesWithMissingTypes(asset));
        }
    }
}
