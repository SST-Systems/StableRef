#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// <b>Tools/StableRef/Metadata Size Report</b> — estimates the size of the StableRef metadata: what reaches player
    /// builds (the stable id — serialized bytes and managed memory once loaded) and what stays in the assets only
    /// (display name and snapshot, which are editor-only fields).
    /// </summary>
    /// <remarks>
    /// An estimate from the binary format, not a build measurement: a string is a 4-byte length plus UTF-8 bytes
    /// aligned to 4, an object reference is 12 bytes; on the managed heap a string costs about 22 + 2 bytes per char.
    /// Scans ScriptableObjects and prefabs under <c>Assets/</c> and the open scenes, empty and missing entries
    /// included; read-only.
    /// </remarks>
    internal static class StableRefMetadataReport
    {
        internal struct Size
        {
            public long Entries;
            /// <summary>Serialized bytes in player builds (the stable id).</summary>
            public long BuildBytes;
            /// <summary>Managed memory in player builds once loaded (the stable id).</summary>
            public long BuildHeapBytes;
            /// <summary>Serialized bytes of the editor-only fields — in the assets, not in builds.</summary>
            public long EditorOnlyBytes;
            /// <summary>The part of <see cref="EditorOnlyBytes"/> taken by snapshots (<c>ValuesData</c>).</summary>
            public long SnapshotBytes;

            public void Add(Size other)
            {
                Entries += other.Entries;
                BuildBytes += other.BuildBytes;
                BuildHeapBytes += other.BuildHeapBytes;
                EditorOnlyBytes += other.EditorOnlyBytes;
                SnapshotBytes += other.SnapshotBytes;
            }
        }

        private const int TopCount = 15;
        private const long ObjectRefBytes = 12;

        [MenuItem("Tools/StableRef/Metadata Size Report", priority = 300)]
        private static void Run()
        {
            var total = default(Size);
            var perAsset = new Dictionary<string, Size>();
            StableRefEditorUtility.ScanProject("Measuring StableRef metadata…", source =>
            {
                var size = default(Size);
                foreach (var target in source.Targets)
                    size.Add(MeasureObject(target));
                if (size.Entries == 0) return;

                total.Add(size);
                perAsset[source.Path] = size;
            });

            EditorUtility.UnloadUnusedAssetsImmediate();
            Debug.Log(BuildText(total, perAsset));
        }

        /// <summary>Metadata size of every StableRef entry on <paramref name="target"/>, nested ones included.</summary>
        internal static Size MeasureObject(UnityEngine.Object target)
        {
            var size = default(Size);
            if (target is not MonoBehaviour && target is not ScriptableObject) return size;
            if (!StableRefPropertyUtils.MayContainStableRef(target.GetType())) return size;

            var so = new SerializedObject(target);
            foreach (var path in StableRefResync.CollectEntryPaths(so, includeEmpty: true))
            {
                var entry = so.FindProperty(path);
                if (entry != null) size.Add(MeasureEntry(entry));
            }
            return size;
        }

        /// <summary>Metadata size of one entry (the <see cref="StableRef{T}"/> wrapper property).</summary>
        internal static Size MeasureEntry(SerializedProperty entry)
        {
            var size = new Size { Entries = 1 };

            string typeId = entry.FindPropertyRelative(StableRefEntry.TypeIdFieldName)?.stringValue;
            size.BuildBytes = StringBuildBytes(typeId);
            size.BuildHeapBytes = StringHeapBytes(typeId);

            size.SnapshotBytes = StringBuildBytes(entry.FindPropertyRelative(StableRefEntry.ValuesDataFieldName)?.stringValue);
            size.EditorOnlyBytes = size.SnapshotBytes
                + StringBuildBytes(entry.FindPropertyRelative(StableRefEntry.TypeDisplayNameFieldName)?.stringValue);

            var refs = entry.FindPropertyRelative(StableRefEntry.ObjectRefsFieldName);
            size.EditorOnlyBytes += 4 + (refs?.arraySize ?? 0) * ObjectRefBytes;

            var paths = entry.FindPropertyRelative(StableRefEntry.ObjectRefPathsFieldName);
            size.EditorOnlyBytes += 4;
            for (int i = 0; i < (paths?.arraySize ?? 0); i++)
                size.EditorOnlyBytes += StringBuildBytes(paths.GetArrayElementAtIndex(i).stringValue);

            return size;
        }

        internal static long StringBuildBytes(string s)
        {
            long n = string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);
            return 4 + ((n + 3) & ~3L);
        }

        internal static long StringHeapBytes(string s) => string.IsNullOrEmpty(s) ? 0 : 22 + 2L * s.Length;

        private static string BuildText(Size total, Dictionary<string, Size> perAsset)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[StableRef] Metadata size report (estimate): {total.Entries} entries in {perAsset.Count} assets/scenes.");
            sb.AppendLine($"  In player builds (stable ids): {Kb(total.BuildBytes)} serialized, ~{Kb(total.BuildHeapBytes)} managed heap with everything loaded");
            sb.AppendLine($"  Editor-only, kept in the assets: {Kb(total.EditorOnlyBytes)} (snapshots, ValuesData: {Kb(total.SnapshotBytes)})");
            if (total.Entries > 0)
                sb.AppendLine($"  Per entry: {total.BuildBytes / total.Entries} B in builds, {total.EditorOnlyBytes / total.Entries} B editor-only");

            sb.AppendLine($"Largest {Mathf.Min(TopCount, perAsset.Count)} (in builds / editor-only):");
            foreach (var pair in perAsset.OrderByDescending(p => p.Value.BuildBytes + p.Value.EditorOnlyBytes).Take(TopCount))
                sb.AppendLine($"  {Kb(pair.Value.BuildBytes),9} / {Kb(pair.Value.EditorOnlyBytes),9}  {pair.Value.Entries,6} entries  {pair.Key}");

            return sb.ToString();
        }

        private static string Kb(long bytes) => $"{bytes / 1024.0:0.0} KB";
    }
}
#endif
