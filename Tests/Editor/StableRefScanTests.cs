using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef.Tests
{
    public class StableRefScanTests
    {
        private const string TempFolder = "Assets/__StableRefScanTestsTemp";
        private const string YamlHeader = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "StableRefScanTests");
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        private string WriteFile(string name, string content)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Test]
        public void Prefilter_KeepsEntries_EmptyLists_AndListOverrides()
        {
            string entry = WriteFile("entry.asset", YamlHeader +
                "--- !u!114 &1\nMonoBehaviour:\n  Ref:\n    TypeId: some.id\n    TypeDisplayName: Thing\n");
            string emptyList = WriteFile("emptyList.prefab", YamlHeader +
                "--- !u!114 &1\nMonoBehaviour:\n  _behaviour:\n    _items: []\n");
            string listOverride = WriteFile("variant.prefab", YamlHeader +
                "--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n    m_Modifications:\n" +
                "    - target: {fileID: 1, guid: 0123456789abcdef0123456789abcdef, type: 3}\n" +
                "      propertyPath: _behaviour._items.Array.data[0].Value\n      value: 42\n");
            string plain = WriteFile("plain.asset", YamlHeader +
                "--- !u!114 &1\nMonoBehaviour:\n  m_Name: Plain\n  items: []\n");
            string binary = WriteFile("binary.asset", "\u0000\u0001binary");

            Assert.IsTrue(StableRefEditorUtility.FileMayHoldEntries(entry), "entry metadata");
            Assert.IsTrue(StableRefEditorUtility.FileMayHoldEntries(emptyList), "empty StableRefList");
            Assert.IsTrue(StableRefEditorUtility.FileMayHoldEntries(listOverride), "override adding list elements");
            Assert.IsFalse(StableRefEditorUtility.FileMayHoldEntries(plain), "no StableRef data");
            Assert.IsTrue(StableRefEditorUtility.FileMayHoldEntries(binary), "binary files are always kept");
        }

        [Test]
        public void TrySaveAsset_ReportsAssetsUnitySavesAndRefuses()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__StableRefScanTestsTemp");

            var holder = ScriptableObject.CreateInstance<TestHolder>();
            string holderPath = TempFolder + "/Holder.asset";
            AssetDatabase.CreateAsset(holder, holderPath);
            holder.List.Add(new AlphaThing());
            EditorUtility.SetDirty(holder);
            Assert.IsTrue(StableRefEditorUtility.TrySaveAsset(holder), "a regular asset is saved");

            // A prefab with a missing script: Unity refuses to save it, logs an error and reimports the file, so the
            // object isn't even left dirty — TrySaveAsset must catch it before saving.
            string prefabPath = TempFolder + "/MissingScript.prefab";
            File.WriteAllText(prefabPath, YamlHeader +
                "--- !u!1 &100\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n" +
                "  - component: {fileID: 101}\n  - component: {fileID: 102}\n  m_Layer: 0\n  m_Name: MissingScript\n" +
                "  m_TagString: Untagged\n  m_IsActive: 1\n" +
                "--- !u!4 &101\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n  serializedVersion: 2\n" +
                "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n" +
                "  m_LocalScale: {x: 1, y: 1, z: 1}\n  m_Children: []\n  m_Father: {fileID: 0}\n" +
                "--- !u!114 &102\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n  m_Enabled: 1\n" +
                "  m_Script: {fileID: 11500000, guid: 00000000000000000000000000000abc, type: 3}\n");
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);

            string before = File.ReadAllText(prefabPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab);
            prefab.transform.localPosition = Vector3.one;
            EditorUtility.SetDirty(prefab.transform);

            Assert.IsFalse(StableRefEditorUtility.TrySaveAsset(prefab.transform), "a prefab with a missing script is not saved");
            Assert.AreEqual(before, File.ReadAllText(prefabPath), "the file is left as it was");
        }
    }
}
