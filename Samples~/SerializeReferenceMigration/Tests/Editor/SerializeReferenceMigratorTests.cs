using System.Globalization;
using System.IO;
using System.Threading;
using NUnit.Framework;
using SST.StableRef.Migration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Assets written in the layout of plain <c>[SerializeReference]</c> fields (as before the fields became StableRef)
    /// are migrated: values, nested references and prefab overrides survive, entries get their ids.
    /// </summary>
    public class SerializeReferenceMigratorTests
    {
        private const string TempFolder = "Assets/__StableRefMigrationTestsTemp";
        private const string Assembly = "SST.StableRef.Migration.TestTypes";
        private const string Ns = "SST.StableRef.Tests";
        private const string YamlHeader = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__StableRefMigrationTestsTemp");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void Asset_FieldsAreConverted_ValuesKept_EntriesStamped()
        {
            string path = Write("Holder.asset", AssetYaml());
            Assert.AreEqual(0, AssetDatabase.LoadAssetAtPath<MigrationTestAsset>(path).List.Count,
                "Unity can't read the old layout into the new field");

            var report = SerializeReferenceMigrator.Run(new[] { path }, apply: true);

            Assert.IsTrue(report.Applied);
            CollectionAssert.IsEmpty(report.Warnings);
            CollectionAssert.AreEqual(new[] { path }, report.ChangedFiles);
            Assert.AreEqual(11, report.ConvertedFields);

            var asset = AssetDatabase.LoadAssetAtPath<MigrationTestAsset>(path);
            var single = (MigrationThingA)asset.Single.Value;
            Assert.AreEqual(5, single.Number);
            Assert.AreEqual("child", ((MigrationThingB)single.Child.Value).Name);
            Assert.IsNull(asset.Empty.Value);
            Assert.AreEqual(new[] { "a", "b" }, new[] { Name(asset.List[0]), Name(asset.List[1]) });
            Assert.AreEqual("renamed", Name(asset.Renamed[0]));
            Assert.AreEqual(0, asset.NoItems.Count);
            Assert.AreEqual("nested", Name(asset.Nested.Items[0]));
            Assert.AreEqual(2, asset.NestedArray.Count);
            Assert.AreEqual("group", Name(asset.NestedArray[0].Items[0]));
            Assert.AreEqual(0, asset.NestedArray[1].Items.Count);
            Assert.AreEqual("plain-child", Name(((MigrationThingA)asset.Plain).Child.Value));
            Assert.AreEqual("property", Name(asset.Property[0]));

            string text = File.ReadAllText(path);
            StringAssert.Contains("TypeId: stableref-tests.migration.thing-a", text, "entries are resynced");
            StringAssert.Contains("TypeId: stableref-tests.migration.thing-b", text);
            StringAssert.DoesNotContain("oldRenamed", text, "the alias key is renamed to the field");

            var again = SerializeReferenceMigrator.Run(new[] { path }, apply: true);
            CollectionAssert.IsEmpty(again.ChangedFiles, "a migrated file is left alone");
        }

        [Test]
        public void Numbers_AreWrittenTheSameInEveryCulture()
        {
            string path = Write("Holder.asset", AssetYaml());
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "−";
            var previous = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = culture;
            try
            {
                var result = new MigrationYamlConverter(new MigrationAssetIndex()).Convert(path, File.ReadAllText(path));
                Assert.IsTrue(result.Changed);
                StringAssert.Contains("  Empty:\n    Value:\n      rid: -2", result.Text, "a null reference keeps the YAML minus");
                StringAssert.DoesNotContain("−", result.Text);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Test]
        public void DirtyAsset_IsSkipped_AndNotCounted()
        {
            string path = Write("Holder.asset", AssetYaml());
            string before = File.ReadAllText(path);
            EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<MigrationTestAsset>(path));

            var report = SerializeReferenceMigrator.Run(new[] { path }, apply: true);

            Assert.IsFalse(report.Applied);
            Assert.AreEqual(1, report.SkippedFiles.Count);
            Assert.AreEqual(0, report.ConvertedFields, "a skipped file's fields are not counted as converted");
            CollectionAssert.IsEmpty(report.ChangedFiles);
            CollectionAssert.IsEmpty(report.Details);
            Assert.AreEqual(before, File.ReadAllText(path));
        }

        [Test]
        public void Continuation_WaitsForTheRewrittenFields()
        {
            CollectionAssert.IsEmpty(MigrationContinuation.MissingFields(new[]
            {
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.Single)),
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.List))
            }), "StableRef and StableRefList fields are loaded");

            Assert.AreEqual(2, MigrationContinuation.MissingFields(new[]
            {
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.Plain)),
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), "_notCompiledYet")
            }).Count, "a field still plain [SerializeReference], or not declared yet, is missing");

            var oldScripts = new[] { MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.Plain)) };
            Assert.IsFalse(MigrationContinuation.AreRewrittenScriptsLoaded(oldScripts, MigrationContinuation.MissingFields(oldScripts)),
                "no rewritten field is StableRef: the old assemblies are loaded, the data waits");

            var partly = new[]
            {
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.Single)),
                MigrationCodeRewriter.ExpectedField(typeof(MigrationTestAsset), nameof(MigrationTestAsset.Plain))
            };
            Assert.IsTrue(MigrationContinuation.AreRewrittenScriptsLoaded(partly, MigrationContinuation.MissingFields(partly)),
                "a field changed back by hand doesn't keep the migration waiting on every recompile");
        }

        [Test]
        public void Analyze_ReportsWithoutWriting()
        {
            string path = Write("Holder.asset", AssetYaml());
            string before = File.ReadAllText(path);

            var report = SerializeReferenceMigrator.Run(new[] { path }, apply: false);

            Assert.IsFalse(report.Applied);
            Assert.AreEqual(11, report.ConvertedFields);
            Assert.AreEqual(before, File.ReadAllText(path));
        }

        [Test]
        public void Prefab_OverridesOfVariant_AreRewritten()
        {
            string basePath = Write("Base.prefab", BasePrefabYaml(ScriptGuid<MigrationTestBehaviour>()));
            string baseGuid = AssetDatabase.AssetPathToGUID(basePath);
            string variantPath = Write("Variant.prefab", VariantYaml(baseGuid));

            var report = SerializeReferenceMigrator.Run(new[] { basePath, variantPath }, apply: true);

            CollectionAssert.IsEmpty(report.Warnings);
            Assert.AreEqual(4, report.RewrittenOverrides);

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(basePath).GetComponent<MigrationTestBehaviour>();
            Assert.AreEqual(1, ((MigrationThingA)source.Single.Value).Number);
            Assert.AreEqual(2, source.List.Count);
            Assert.IsNull(source.Other.Value);

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).GetComponent<MigrationTestBehaviour>();
            Assert.AreEqual(9, ((MigrationThingA)variant.Single.Value).Number, "override inside an inherited value");
            Assert.AreEqual(3, variant.List.Count, "override of the list size");
            Assert.AreEqual("a", Name(variant.List[0]));
            var added = (MigrationThingA)variant.List[2];
            Assert.AreEqual(7, added.Number, "element added by the variant");
            Assert.AreEqual("variant-child", Name(added.Child.Value), "StableRef inside an added value");
            Assert.AreEqual("s", Name(variant.Other.Value), "single reference set by the variant");

            string text = File.ReadAllText(variantPath);
            StringAssert.Contains("propertyPath: List._items.Array.data[2].Value", text);
            StringAssert.Contains("propertyPath: Other.Value", text);
        }

        [Test]
        public void Prefab_OverrideOfNestedPrefabObject_IsRewritten()
        {
            // Saved through Unity, so the nested object ids are the real ones; the outer prefab doesn't list the
            // nested component, and the migrator has to derive its source from the instance id.
            var source = new GameObject("Inner");
            var behaviour = source.AddComponent<MigrationTestBehaviour>();
            behaviour.List.Add(new MigrationThingB { Name = "a" });
            behaviour.List.Add(new MigrationThingB { Name = "b" });
            string innerPath = TempFolder + "/Inner.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, innerPath);
            Object.DestroyImmediate(source);

            var outer = new GameObject("Outer");
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(innerPath));
            nested.transform.SetParent(outer.transform);
            string outerPath = TempFolder + "/Outer.prefab";
            PrefabUtility.SaveAsPrefabAsset(outer, outerPath);
            Object.DestroyImmediate(outer);

            var nestedComponent = AssetDatabase.LoadAssetAtPath<GameObject>(outerPath).GetComponentInChildren<MigrationTestBehaviour>();
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(nestedComponent, out string outerGuid, out long nestedId));
            StringAssert.DoesNotContain("&" + nestedId, File.ReadAllText(outerPath), "the outer prefab doesn't list it");

            string variantPath = Write("OuterVariant.prefab", YamlHeader +
                "--- !u!1001 &300\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n" +
                "  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications:\n" +
                $"    - target: {{fileID: {nestedId}, guid: {outerGuid}, type: 3}}\n      propertyPath: List.Array.size\n" +
                "      value: 1\n      objectReference: {fileID: 0}\n" +
                "    m_RemovedComponents: []\n    m_RemovedGameObjects: []\n    m_AddedGameObjects: []\n" +
                "    m_AddedComponents: []\n" +
                $"  m_SourcePrefab: {{fileID: 100100000, guid: {outerGuid}, type: 3}}\n");

            var report = SerializeReferenceMigrator.Run(new[] { variantPath }, apply: true);

            Assert.AreEqual(1, report.RewrittenOverrides);
            StringAssert.Contains("propertyPath: List._items.Array.size", File.ReadAllText(variantPath));
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).GetComponentInChildren<MigrationTestBehaviour>();
            Assert.AreEqual(1, variant.List.Count);
        }

        [Test]
        public void Scene_IsConverted_ResyncedAndSaved()
        {
            // The same objects as the source prefab, written as a scene that isn't open.
            string path = Write("Scene.unity", BasePrefabYaml(ScriptGuid<MigrationTestBehaviour>()));

            var report = SerializeReferenceMigrator.Run(new[] { path }, apply: true);

            CollectionAssert.IsEmpty(report.Warnings);
            CollectionAssert.IsEmpty(report.ScenesToResync, "the scene was opened, resynced and saved");
            Assert.Greater(report.ResyncedSceneEntries, 0);
            string text = File.ReadAllText(path);
            StringAssert.Contains("TypeId: stableref-tests.migration.thing-a", text);
            StringAssert.Contains("TypeId: stableref-tests.migration.thing-b", text);

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var behaviour = scene.GetRootGameObjects()[0].GetComponent<MigrationTestBehaviour>();
                Assert.AreEqual(1, ((MigrationThingA)behaviour.Single.Value).Number);
                Assert.AreEqual(new[] { "a", "b" }, new[] { Name(behaviour.List[0]), Name(behaviour.List[1]) });
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static string Name(IMigrationThing thing) => ((MigrationThingB)thing).Name;

        private static string Write(string name, string yaml)
        {
            string path = TempFolder + "/" + name;
            File.WriteAllText(path, yaml);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static string ScriptGuid<T>()
        {
            foreach (string guid in AssetDatabase.FindAssets(typeof(T).Name + " t:MonoScript"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script != null && script.GetClass() == typeof(T)) return guid;
            }
            Assert.Fail($"No script for {typeof(T).Name}");
            return null;
        }

        private static string Record(long rid, string type, params string[] data)
        {
            string text = $"    - rid: {rid}\n      type: {{class: {type}, ns: {Ns}, asm: {Assembly}}}\n      data:\n";
            foreach (string line in data) text += "        " + line + "\n";
            return text;
        }

        private static string Header(string script, string name, long fileId = 11400000, long gameObject = 0) =>
            $"--- !u!114 &{fileId}\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n" +
            "  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n" +
            $"  m_GameObject: {{fileID: {gameObject}}}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" +
            $"  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}\n  m_Name: {name}\n  m_EditorClassIdentifier: \n";

        private static string AssetYaml()
        {
            return YamlHeader + Header(ScriptGuid<MigrationTestAsset>(), "Holder") +
                   "  Single:\n    rid: 1000\n" +
                   "  Empty:\n    rid: -2\n" +
                   "  List:\n  - rid: 1002\n  - rid: 1003\n" +
                   "  oldRenamed:\n  - rid: 1004\n" +
                   "  NoItems: []\n" +
                   "  Nested:\n    Items:\n    - rid: 1005\n" +
                   "  NestedArray:\n  - Items:\n    - rid: 1006\n  - Items: []\n" +
                   "  Plain:\n    rid: 1007\n" +
                   "  <Property>k__BackingField:\n  - rid: 1009\n" +
                   "  references:\n    version: 2\n    RefIds:\n" +
                   "    - rid: -2\n      type: {class: , ns: , asm: }\n" +
                   Record(1000, nameof(MigrationThingA), "Number: 5", "Child:", "  rid: 1001") +
                   Record(1001, nameof(MigrationThingB), "Name: child") +
                   Record(1002, nameof(MigrationThingB), "Name: a") +
                   Record(1003, nameof(MigrationThingB), "Name: b") +
                   Record(1004, nameof(MigrationThingB), "Name: renamed") +
                   Record(1005, nameof(MigrationThingB), "Name: nested") +
                   Record(1006, nameof(MigrationThingB), "Name: group") +
                   Record(1007, nameof(MigrationThingA), "Number: 7", "Child:", "  rid: 1008") +
                   Record(1008, nameof(MigrationThingB), "Name: plain-child") +
                   Record(1009, nameof(MigrationThingB), "Name: property");
        }

        private static string BasePrefabYaml(string script) =>
            YamlHeader +
            "--- !u!1 &100\nGameObject:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n" +
            "  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  serializedVersion: 6\n  m_Component:\n" +
            "  - component: {fileID: 101}\n  - component: {fileID: 102}\n  m_Layer: 0\n  m_Name: Base\n" +
            "  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n" +
            "--- !u!4 &101\nTransform:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n" +
            "  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 100}\n" +
            "  serializedVersion: 2\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n" +
            "  m_LocalScale: {x: 1, y: 1, z: 1}\n  m_ConstrainProportionsScale: 0\n  m_Children: []\n" +
            "  m_Father: {fileID: 0}\n  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n" +
            Header(script, "", 102, 100) +
            "  Single:\n    rid: 2000\n" +
            "  Other:\n    rid: -2\n" +
            "  List:\n  - rid: 2001\n  - rid: 2002\n" +
            "  references:\n    version: 2\n    RefIds:\n" +
            "    - rid: -2\n      type: {class: , ns: , asm: }\n" +
            Record(2000, nameof(MigrationThingA), "Number: 1", "Child:", "  rid: -2") +
            Record(2001, nameof(MigrationThingB), "Name: a") +
            Record(2002, nameof(MigrationThingB), "Name: b");

        private static string VariantYaml(string baseGuid)
        {
            string Modification(long target, string path, string value) =>
                $"    - target: {{fileID: {target}, guid: {baseGuid}, type: 3}}\n      propertyPath: {path}\n" +
                $"      value: {value}\n      objectReference: {{fileID: 0}}\n";

            return YamlHeader +
                   "--- !u!1001 &300\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n" +
                   "  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications:\n" +
                   Modification(100, "m_Name", "Variant") +
                   Modification(102, "List.Array.size", "3") +
                   Modification(102, "List.Array.data[2]", "3000") +
                   Modification(102, "'managedReferences[3000]'", $"{Assembly} {Ns}.{nameof(MigrationThingA)}") +
                   Modification(102, "managedReferences[3000].Number", "7") +
                   Modification(102, "managedReferences[3000].Child", "3001") +
                   Modification(102, "'managedReferences[3001]'", $"{Assembly} {Ns}.{nameof(MigrationThingB)}") +
                   Modification(102, "managedReferences[3001].Name", "variant-child") +
                   Modification(102, "managedReferences[2000].Number", "9") +
                   Modification(102, "Other", "3002") +
                   Modification(102, "'managedReferences[3002]'", $"{Assembly} {Ns}.{nameof(MigrationThingB)}") +
                   Modification(102, "managedReferences[3002].Name", "s") +
                   "    m_RemovedComponents: []\n    m_RemovedGameObjects: []\n    m_AddedGameObjects: []\n" +
                   "    m_AddedComponents: []\n" +
                   $"  m_SourcePrefab: {{fileID: 100100000, guid: {baseGuid}, type: 3}}\n";
        }
    }
}
