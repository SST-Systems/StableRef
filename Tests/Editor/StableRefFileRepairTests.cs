using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Files Unity can't read back (records of missing classes saved without data) and objects whose references
    /// Unity lost in memory: the YAML parsing and repair, and the asset side of Fix Missing Types.
    /// </summary>
    public class StableRefFileRepairTests
    {
        private const string TempFolder = "Assets/__StableRefTestsTemp";
        private const string Asm = "SST.StableRef.Editor.Tests";
        private const string Ns = "SST.StableRef.Tests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        private const string SyntheticYaml =
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &-42\nMonoBehaviour:\n  m_Name: X\n" +
            "  Plain:\n  - rid: 5\n  - rid: 9\n  Inline: {rid: 9}\n" +
            "  List:\n    _items:\n    - TypeId: a.id\n      TypeDisplayName: A\n      Value:\n        rid: 1\n" +
            "    - TypeId: gone.id\n      TypeDisplayName: Gone\n      Value:\n        rid: 9\n" +
            "  references:\n    version: 2\n    RefIds:\n" +
            "    - rid: -2\n      type: {class: , ns: , asm: }\n" +
            "    - rid: 1\n      type: {class: Holder, ns: N, asm: A}\n      data:\n        Children:\n          _items:\n" +
            "          - TypeId: \n            Value:\n              rid: 2\n          - TypeId: x\n            Value:\n" +
            "              rid: 9\n        Deep:\n          Inner:\n            rid: 9\n" +
            "    - rid: 2\n      type: {class: Gone, ns: N, asm: A}\n      data: \n" +
            "    - rid: 5\n      type: {class: 'Generic`1[[System.Int32, mscorlib]]', ns: N, asm: A}\n" +
            "    - rid: 9\n      type: {class: Gone, ns: N, asm: A}\n" +
            "--- !u!114 &7\nMonoBehaviour:\n  Other:\n    rid: 9\n  references:\n    version: 2\n    RefIds:\n" +
            "    - rid: 9\n      type: {class: Kept, ns: N, asm: A}\n      data:\n        X: 1\n";

        private static bool ResolvesAllButGone(string className, string ns, string asm) => className != "Gone";

        [Test]
        public void YamlRecords_FindsMissingClassesWithoutData_AndTheFieldsUsingThem()
        {
            var document = StableRefYamlRecords.Parse(SyntheticYaml);
            var unreadable = document.FindUnreadable(ResolvesAllButGone);

            Assert.AreEqual(1, unreadable.Count, "a record with data: (rid 2) and a resolvable class (rid 5) are readable");
            var record = unreadable[0];
            Assert.AreEqual(-42, record.ObjectFileId);
            Assert.AreEqual(9, record.Rid);
            Assert.AreEqual("N.Gone", record.TypeName);

            var fields = record.Fields.ToDictionary(f => f.Path, f => f.TypeId);
            CollectionAssert.AreEquivalent(
                new[] { "Plain[1]", "Inline", "List[1]", "List[0].Children[1]", "List[0].Deep.Inner" }, fields.Keys);
            Assert.IsNull(fields["Plain[1]"], "plain [SerializeReference] list element");
            Assert.AreEqual("gone.id", fields["List[1]"]);
            Assert.AreEqual("x", fields["List[0].Children[1]"]);
            Assert.IsNull(fields["List[0].Deep.Inner"]);

            Assert.IsTrue(document.HasRecords(-42));
            Assert.IsTrue(document.HasRecords(7));
            Assert.IsFalse(document.HasRecords(8));
        }

        [Test]
        public void YamlRecords_Remove_DropsTheRecordsAndNullsTheirUsesInTheirObjectOnly()
        {
            var text = SyntheticYaml.Replace("\n", "\r\n");
            var document = StableRefYamlRecords.Parse(text);
            string repaired = document.Remove(document.FindUnreadable(ResolvesAllButGone));

            Assert.AreEqual(text.Split('\r').Length - 2, repaired.Split('\r').Length, "two lines removed, CRLF kept");
            StringAssert.Contains("  - rid: 5\r\n  - rid: -2\r\n  Inline: {rid: -2}\r\n", repaired);
            StringAssert.Contains("      Value:\r\n        rid: -2\r\n", repaired);
            StringAssert.Contains("          Inner:\r\n            rid: -2\r\n", repaired);
            StringAssert.DoesNotContain("    - rid: 9\r\n      type: {class: Gone", repaired);
            StringAssert.Contains("  Other:\r\n    rid: 9\r\n", repaired, "another object's record of the same id is untouched");
            StringAssert.Contains("type: {class: Gone, ns: N, asm: A}\r\n      data: \r\n", repaired, "a record with data stays");

            var again = StableRefYamlRecords.Parse(repaired);
            CollectionAssert.IsEmpty(again.FindUnreadable(ResolvesAllButGone));
        }

        [Test]
        public void YamlRecords_Remove_AddsTheNullRecordWhenTheObjectHadNone()
        {
            const string Text = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  F:\n    rid: 3\n  references:\n" +
                                "    version: 2\n    RefIds:\n    - rid: 3\n      type: {class: Gone, ns: N, asm: A}\n";
            var document = StableRefYamlRecords.Parse(Text);

            Assert.AreEqual(
                "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  F:\n    rid: -2\n  references:\n    version: 2\n" +
                "    RefIds:\n    - rid: -2\n      type: {class: , ns: , asm: }\n",
                document.Remove(document.FindUnreadable(ResolvesAllButGone)));
        }

        [Test]
        public void TypeResolves_ByNameAndThroughMovedFrom()
        {
            Assert.IsTrue(StableRefFileRepair.TypeResolves(nameof(AlphaThing), Ns, Asm));
            Assert.IsTrue(StableRefFileRepair.TypeResolves("OuterOfThing/NestedThing", Ns, Asm), "nested class");
            Assert.IsTrue(StableRefFileRepair.TypeResolves("MappedGeneric`1[[System.Int32, mscorlib]]", Ns, Asm), "generic");
            Assert.IsTrue(StableRefFileRepair.TypeResolves("OldMovedThing", Ns, Asm), "[MovedFrom] old name");
            Assert.IsFalse(StableRefFileRepair.TypeResolves("GoneThing", Ns, Asm));
            Assert.IsFalse(StableRefFileRepair.TypeResolves(nameof(AlphaThing), Ns, "No.Such.Assembly"));
        }

        private static string WriteHolder(string name, string fields, string references, bool import = true)
        {
            var probe = ScriptableObject.CreateInstance<MissingHolder>();
            string scriptGuid = AssetDatabase.AssetPathToGUID(
                AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(probe)));
            Object.DestroyImmediate(probe);

            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__StableRefTestsTemp");
            string path = TempFolder + "/" + name + ".asset";
            File.WriteAllText(path,
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n" +
                "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n" +
                "  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" +
                $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n  m_Name: {name}\n" +
                "  m_EditorClassIdentifier: \n" + fields +
                "  references:\n    version: 2\n    RefIds:\n    - rid: -2\n      type: {class: , ns: , asm: }\n" +
                references);
            if (import) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static string Entry(string name, string typeId, string displayName, string valuesData, long rid, int indent) =>
            Indent(indent) + name + ":\n" +
            Indent(indent + 2) + "TypeId: " + typeId + "\n" +
            Indent(indent + 2) + "TypeDisplayName: " + displayName + "\n" +
            Indent(indent + 2) + "ObjectRefs: []\n" +
            Indent(indent + 2) + "ObjectRefPaths: []\n" +
            Indent(indent + 2) + "ValuesData: " + valuesData + "\n" +
            Indent(indent + 2) + "Value:\n" +
            Indent(indent + 4) + "rid: " + rid + "\n";

        private static string Indent(int n) => new(' ', n);

        private static string UnreadableAsset(string innerTypeId, string innerValuesData, string name = "Unreadable",
            bool import = true) =>
            WriteHolder(name,
                "  A:\n    rid: -2\n  B:\n    rid: -2\n" +
                Entry("Ref", "stableref-tests.alpha", "AlphaThing", "'#v2'", 1000, 2),
                $"    - rid: 1000\n      type: {{class: AlphaThing, ns: {Ns}, asm: {Asm}}}\n      data:\n" +
                "        Number: 5\n" +
                Entry("Inner", innerTypeId, "Gone", innerValuesData, 1001, 8) +
                $"    - rid: 1001\n      type: {{class: GoneThing, ns: {Ns}, asm: {Asm}}}\n", import);

        [Test]
        public void RepairAsset_RemovesTheUnreadableRecord_AndTheObjectLoadsWhole()
        {
            string path = UnreadableAsset("", "");
            // Depending on the file, Unity loads such an object without its references or not at all (as here).
            var before = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assume.That(before == null || before.Ref.Value == null, "Unity can't read the object");
            Assert.AreEqual(1, StableRefFileRepair.FindUnreadable(StableRefFileRepair.ReadDocument(path)).Count);

            Assert.AreEqual(StableRefFileRepair.RepairResult.Repaired, StableRefFileRepair.RepairAsset(path));

            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            var alpha = asset.Ref.Value as AlphaThing;
            Assert.NotNull(alpha, "the loaded object is updated by the reimport");
            Assert.AreEqual(5, alpha.Number);
            Assert.IsNull(alpha.Inner.Value, "an entry without a stable id becomes None");
            StringAssert.DoesNotContain("GoneThing", File.ReadAllText(path));
            Assert.AreEqual(StableRefFileRepair.RepairResult.NothingToRepair, StableRefFileRepair.RepairAsset(path));
        }

        [Test]
        public void RepairAsset_ThenFix_RecreatesAnEntryWithAStableIdFromItsSnapshot()
        {
            string path = UnreadableAsset("stableref-tests.beta", "'#v2\n\n          B=7'");
            Assert.AreEqual(StableRefFileRepair.RepairResult.Repaired, StableRefFileRepair.RepairAsset(path));

            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.AreEqual(1, StableRefMissingTypesWindow.FixTarget(asset, out int unresolved));
            Assert.AreEqual(0, unresolved);
            Assert.IsTrue(StableRefEditorUtility.TrySaveAsset(asset));

            Resources.UnloadAsset(asset);
            asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            var inner = ((AlphaThing)asset.Ref.Value).Inner.Value as BetaThing;
            Assert.NotNull(inner, "the fix survives a reload from disk");
            Assert.AreEqual(7, inner.B);
        }

        [Test]
        public void RepairAsset_IsRefusedWhileTheAssetHasUnsavedChanges()
        {
            string path = WriteHolder("Unsaved", "  A:\n    rid: -2\n  B:\n    rid: -2\n", "");
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            EditorUtility.SetDirty(asset);
            UnreadableAsset("", "", "Unsaved", import: false);

            Assert.AreEqual(StableRefFileRepair.RepairResult.Unsaved, StableRefFileRepair.RepairAsset(path));
            StringAssert.Contains("GoneThing", File.ReadAllText(path));
        }

        [Test]
        public void ScanPrefilter_FlagsOnlyFilesWithARecordWithoutData()
        {
            bool Flagged(string text) => StableRefEditorUtility.HasRecordWithoutData(System.Text.Encoding.UTF8.GetBytes(text));

            Assert.IsTrue(Flagged(SyntheticYaml));
            Assert.IsTrue(Flagged(SyntheticYaml.Replace("\n", "\r\n")));
            Assert.IsFalse(Flagged(
                "  RefIds:\r\n  - rid: -2\r\n    type: {class: , ns: , asm: }\r\n" +
                "  - rid: 1\r\n    type: {class: A, ns: N, asm: X}\r\n    data: \r\n" +
                "  - rid: 2\r\n    type: {class: B, ns: N, asm: X}\r\n    data:\r\n      V: 1\r\n"), "the null record has no data");
            Assert.IsTrue(Flagged("  - rid: 1\n    type: {class: A, ns: N, asm: X}"), "at the end of the file");
        }

        [Test]
        public void YamlRecords_FieldsUsing_ListsTheUsesOfOneRecordInOneObject()
        {
            var document = StableRefYamlRecords.Parse(SyntheticYaml);

            var fields = document.FieldsUsing(-42, 1).ToDictionary(f => f.Path, f => f.TypeId);
            CollectionAssert.AreEquivalent(new[] { "List[0]" }, fields.Keys);
            Assert.AreEqual("a.id", fields["List[0]"]);
            Assert.AreEqual(5, document.FieldsUsing(-42, 9).Count);
            Assert.AreEqual(1, document.FieldsUsing(7, 9).Count);
            CollectionAssert.IsEmpty(document.FieldsUsing(8, 9));
        }

        private const string GoneType = "type: {class: GoneThing, ns: " + Ns + ", asm: " + Asm + "}";

        /// <summary>
        /// <c>Ref</c> (an entry with a stable id) and <c>A</c> (a plain field) point at records of a missing class with
        /// empty data (<c>data: </c> and <c>data: {}</c> both load that way); <c>B</c> at one with data.
        /// </summary>
        private static string EmptyRecordAsset(bool plainEmpty = true) =>
            WriteHolder("Empty", $"  A:\n    rid: {(plainEmpty ? 1001 : -2)}\n  B:\n    rid: 1002\n" +
                              Entry("Ref", "gone.id", "Gone", "'#v2'", 1000, 2),
                $"    - rid: 1000\n      {GoneType}\n      data: \n" +
                $"    - rid: 1001\n      {GoneType}\n      data: {{}}\n" +
                $"    - rid: 1002\n      {GoneType}\n      data:\n        X: 1\n");

        [Test]
        public void EmptyMissingRecord_OfAnEntry_IsFound()
        {
            string path = EmptyRecordAsset();
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.NotNull(asset);

            var empty = StableRefFileRepair.FindEmptyMissingRecords(asset, StableRefFileRepair.ReadDocument(path));
            Assert.AreEqual(1, empty.Count, "the plain field's empty record (rid 1001) and the record with data aren't listed");
            Assert.AreEqual(1000, empty[0].Rid);
            Assert.AreEqual("GoneThing", empty[0].ClassName);
            Assert.AreEqual("Ref", empty[0].Fields.Single().Path);
            Assert.AreEqual("gone.id", empty[0].Fields.Single().TypeId);
        }

        private static string[] MissingLabels(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            var empty = StableRefFileRepair.FindEmptyMissingRecords(asset, StableRefFileRepair.ReadDocument(path));
            var collect = typeof(StableRefMissingTypesWindow).GetMethod("CollectMissingRefs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(collect);
            var infos = (System.Collections.IEnumerable)collect.Invoke(null, new object[] { asset, empty });
            return infos.Cast<object>().Select(i => (string)i.GetType().GetField("Label").GetValue(i)).ToArray();
        }

        [Test]
        public void EmptyMissingRecord_IsListedOnItsField_AsBroken()
        {
            CollectionAssert.AreEqual(new[] { "Broken (Gone) → Fixed" },
                MissingLabels(EmptyRecordAsset(plainEmpty: false)), "an entry with a stable id");

            string nested = WriteHolder("Nested", "  A:\n    rid: -2\n  B:\n    rid: -2\n" +
                                                  Entry("Ref", "stableref-tests.alpha", "AlphaThing", "'#v2'", 1000, 2),
                $"    - rid: 1000\n      type: {{class: AlphaThing, ns: {Ns}, asm: {Asm}}}\n      data:\n        Number: 5\n" +
                Entry("Inner", "", "", "", 1001, 8) +
                $"    - rid: 1001\n      {GoneType}\n      data: \n");
            CollectionAssert.AreEqual(new[] { "Broken (GoneThing) → Fixed" }, MissingLabels(nested),
                "a nested entry without a stable id, not listed as missing otherwise");
        }

        [Test]
        public void Drawer_LabelsABrokenEntry_InMemoryAndInAnUnreadableFile()
        {
            string empty = EmptyRecordAsset(plainEmpty: false);
            var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<MissingHolder>(empty));
            Assert.IsTrue(StableRefFileRepair.TryGetBrokenClass(so.FindProperty("Ref"), out string cls));
            Assert.AreEqual("GoneThing", cls);
            Assert.AreEqual("Broken (GoneThing) → Fixed", StableRefFileRepair.BrokenLabelText(cls));

            // The object stays in memory after a save made its file unreadable; the next domain reload drops its
            // references and its missing-type data (simulated) — the drawer then finds the broken field in the file.
            string path = WriteHolder("Saved", "  A:\n    rid: -2\n  B:\n    rid: -2\n" +
                                               Entry("Ref", "stableref-tests.alpha", "AlphaThing", "'#v2'", 1000, 2),
                $"    - rid: 1000\n      type: {{class: AlphaThing, ns: {Ns}, asm: {Asm}}}\n      data:\n        Number: 5\n" +
                Entry("Inner", "", "", "", 1001, 8) +
                $"    - rid: 1001\n      {GoneType}\n      data: \n");
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            EditorUtility.SetDirty(asset);
            LogAssert.ignoreFailingMessages = true;
            AssetDatabase.SaveAssetIfDirty(asset);
            Assume.That(StableRefFileRepair.FindUnreadable(StableRefFileRepair.ReadDocument(path)).Count, Is.EqualTo(1));
            SerializationUtility.ClearAllManagedReferencesWithMissingTypes(asset);
            asset.Ref.Value = null;

            so = new SerializedObject(asset);
            Assert.IsTrue(StableRefFileRepair.TryGetBrokenClass(so.FindProperty("Ref"), out cls),
                "an entry holding the broken field (Ref.Inner) in the file");
            Assert.AreEqual("GoneThing", cls);
        }

        [Test]
        public void EmptyMissingRecord_SavedAsIs_MakesTheFileUnreadable()
        {
            string path = EmptyRecordAsset();
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            EditorUtility.SetDirty(asset);
            // Unity asserts 'objData.m_YAMLNode == nullptr' while writing such a record.
            LogAssert.ignoreFailingMessages = true;
            AssetDatabase.SaveAssetIfDirty(asset);

            // Unity's behaviour this check guards against: the record is written without its data: line.
            var unreadable = StableRefFileRepair.FindUnreadable(StableRefFileRepair.ReadDocument(path));
            CollectionAssert.AreEquivalent(new long[] { 1000, 1001 }, unreadable.Select(u => u.Rid));
        }

        /// <summary>
        /// <c>Ref</c> points at a missing class without data, <c>Other</c> (also missing) at one with data — so the full
        /// release of missing-type data can't happen while <c>Other</c> waits for its class.
        /// </summary>
        private static string TwoMissingEntriesAsset() =>
            WriteHolder("TwoMissing", "  A:\n    rid: -2\n  B:\n    rid: -2\n" +
                                      Entry("Ref", "gone.id", "Gone", "'#v2'", 1000, 2) +
                                      Entry("Other", "gone2.id", "Gone2", "'#v2'", 1001, 2),
                $"    - rid: 1000\n      {GoneType}\n      data: \n" +
                $"    - rid: 1001\n      {GoneType}\n      data:\n        X: 1\n");

        private static long[] MissingIds(Object o) =>
            SerializationUtility.GetManagedReferencesWithMissingTypes(o).Select(m => m.referenceId).ToArray();

        [Test]
        public void EmptyMissingRecord_Cleared_TheFileStaysReadable_AndTheEntryStaysMissing()
        {
            string path = TwoMissingEntriesAsset();
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            var empty = StableRefFileRepair.FindEmptyMissingRecords(asset, StableRefFileRepair.ReadDocument(path));

            Assert.AreEqual(1, StableRefFileRepair.ClearEmptyRecords(asset, empty.Select(r => r.Rid).ToList()));
            Assert.IsTrue(EditorUtility.IsDirty(asset));
            Assert.AreEqual(0, StableRefFileRepair.ClearEmptyRecords(asset, new long[] { 1000 }), "already cleared");
            CollectionAssert.AreEquivalent(new long[] { 1001 }, MissingIds(asset), "Other's record with data is kept");
            Assert.IsFalse(StableRefFileRepair.HoldsEmptyMissingRecord(asset));
            Assert.IsTrue(StableRefEditorUtility.TrySaveAsset(asset));
            CollectionAssert.IsEmpty(StableRefFileRepair.FindUnreadable(StableRefFileRepair.ReadDocument(path)));

            Resources.UnloadAsset(asset);
            asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.NotNull(asset, "the file loads");
            Assert.AreEqual("gone.id", asset.Ref.TypeId, "the entry keeps its stable id, so it stays missing and fixable");

            var so = new SerializedObject(asset);
            Assert.IsTrue(StableRefEntry.IsMissing(so.FindProperty("Ref")), "still a missing entry, as when a type is gone");
            CollectionAssert.AreEqual(new[] { "Missing (Gone) → None", "Missing (Gone2) → None" }, MissingLabels(path),
                "listed like any missing entry");
            Assert.AreEqual(0, StableRefMissingTypesWindow.FixTarget(asset, out int unresolved));
            Assert.AreEqual(2, unresolved, "Fix skips them until the types are back");
        }

        [Test]
        public void EmptyMissingRecord_Cleared_KeepsAPlainFieldsRecordWithData()
        {
            // Ref has no stable id, so once its record is gone no entry is missing — the full release must not follow.
            string path = WriteHolder("KeepPlain", "  A:\n    rid: 1001\n  B:\n    rid: -2\n" + Entry("Ref", "", "", "", 1000, 2),
                $"    - rid: 1000\n      {GoneType}\n      data: \n" +
                $"    - rid: 1001\n      {GoneType}\n      data:\n        X: 1\n");
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);

            Assert.AreEqual(1, StableRefFileRepair.ClearEmptyRecords(asset, new long[] { 1000 }));
            CollectionAssert.AreEquivalent(new long[] { 1001 }, MissingIds(asset), "A's data is kept");
        }

        [Test]
        public void EmptyMissingRecord_OfAPlainField_IsNotCleared_ButBlocksTheSave()
        {
            string path = EmptyRecordAsset();
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.AreEqual(1, StableRefFileRepair.ClearEmptyRecords(asset, new long[] { 1000 }));

            Assert.IsTrue(StableRefFileRepair.HoldsEmptyMissingRecord(asset), "A's record is left as it is");
        }

        [Test]
        public void EmptyMissingRecord_IsReleased_WhenItsEntryIsSetToNoneByHand()
        {
            string path = TwoMissingEntriesAsset();
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);

            // What the selector does for None: clear the entry, then release with the id the field reads (-2).
            var so = new SerializedObject(asset);
            StableRefEntry.Clear(so.FindProperty("Ref"));
            so.ApplyModifiedProperties();
            StableRefEntry.ReleaseMissingData(asset, new[] { StableRefEditorUtility.ManagedRefIdNull });

            CollectionAssert.AreEquivalent(new long[] { 1001 }, MissingIds(asset), "Other still waits for its class");
            Assert.IsFalse(StableRefFileRepair.HoldsEmptyMissingRecord(asset));
            Assert.IsTrue(StableRefEditorUtility.TrySaveAsset(asset));
            CollectionAssert.IsEmpty(StableRefFileRepair.FindUnreadable(StableRefFileRepair.ReadDocument(path)));

            Resources.UnloadAsset(asset);
            asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            Assert.NotNull(asset, "the file loads");
            Assert.AreEqual("gone2.id", asset.Other.TypeId);
            CollectionAssert.AreEquivalent(new long[] { 1001 }, MissingIds(asset), "Other's data is kept");
        }

        [Test]
        public void LostInMemory_IsDetected_AndReadingTheAssetFromDiskBringsItBack()
        {
            string path = WriteHolder("Lost", "  A:\n    rid: -2\n  B:\n    rid: -2\n" +
                    Entry("Ref", "stableref-tests.beta", "BetaThing", "'#v2'", 1000, 2),
                $"    - rid: 1000\n      type: {{class: BetaThing, ns: {Ns}, asm: {Asm}}}\n      data:\n        B: 3\n");
            var asset = AssetDatabase.LoadAssetAtPath<MissingHolder>(path);
            var document = StableRefFileRepair.ReadDocument(path);
            Assert.IsFalse(StableRefFileRepair.IsLostInMemory(asset, document), "a healthy object");

            // What a domain reload leaves of an object that held missing-type data: no value, nothing dirty.
            asset.Ref.Value = null;
            Assert.IsFalse(EditorUtility.IsDirty(asset));
            Assert.IsTrue(StableRefFileRepair.IsLostInMemory(asset, document));

            var reloaded = (MissingHolder)StableRefFileRepair.ReloadAsset(asset);
            Assert.NotNull(reloaded);
            Assert.AreEqual(3, ((BetaThing)reloaded.Ref.Value).B);
            Assert.IsFalse(StableRefFileRepair.IsLostInMemory(reloaded, document));
        }
    }
}
