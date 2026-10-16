#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Switches plain <c>[SerializeReference]</c> fields to StableRef together with their data: rewrites the
    /// declarations in the project's scripts (<see cref="MigrationCodeRewriter"/>), and once they compile moves the
    /// saved data into the new layout — <c>StableRef&lt;T&gt;</c> for a single reference, <c>StableRefList&lt;T&gt;</c>
    /// for an array or list — and stamps the stable ids and snapshots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity can't read the old data into the new field and drops the orphaned reference records the first time such
    /// an asset is saved, so the data migration works on the files: <see cref="MigrationYamlConverter"/> rewrites the
    /// field layout and prefab override paths as text, keeping every reference record and its id. The changed files
    /// are reimported and resynced with <see cref="StableRefResync.ResyncAssets"/>.
    /// </para>
    /// <para>
    /// The code goes first: if it doesn't compile, the old types stay loaded and the data stays readable; the data step
    /// (<see cref="MigrationContinuation"/>) runs after the next successful compile. Fields already retyped by hand are
    /// migrated by the data step alone.
    /// </para>
    /// </remarks>
    public static class SerializeReferenceMigrator
    {
        /// <summary>Result of a run.</summary>
        public sealed class Report
        {
            /// <summary>Whether files were written (<c>false</c> for a dry run or a cancelled run).</summary>
            public bool Applied;
            public int ScannedFiles;
            public int ConvertedFields;
            public int RewrittenOverrides;
            /// <summary>Files that need (dry run) or got the migration.</summary>
            public readonly List<string> ChangedFiles = new();
            /// <summary>Files that need the migration but were left untouched, with the reason.</summary>
            public readonly List<string> SkippedFiles = new();
            /// <summary>Converted scenes whose entries could not be resynced: open them and run Resync All.</summary>
            public readonly List<string> ScenesToResync = new();
            /// <summary>Entries resynced in converted scenes.</summary>
            public int ResyncedSceneEntries;
            public readonly List<string> Warnings = new();
            /// <summary>Per file, what changed (or would change): converted fields and rewritten override paths.</summary>
            public readonly List<string> Details = new();
            /// <summary>The resync of the converted assets; empty for a dry run.</summary>
            public StableRefResync.Report Resync;
            /// <summary>The code part of the run: rewritten declarations, added ids and references, warnings.</summary>
            public string CodeSummary;

            public override string ToString()
            {
                var text = new StringBuilder();
                if (!string.IsNullOrEmpty(CodeSummary)) text.AppendLine(CodeSummary);
                text.AppendLine(Applied
                    ? $"[StableRef] Migration: converted {ConvertedFields} field(s) and {RewrittenOverrides} override path(s) in {ChangedFiles.Count} file(s), scanned {ScannedFiles}."
                    : $"[StableRef] Migration (dry run): {ConvertedFields} field(s) and {RewrittenOverrides} override path(s) to convert in {ChangedFiles.Count} file(s), scanned {ScannedFiles}.");
                if (Applied)
                {
                    text.AppendLine($"Resync: updated {Resync.UpdatedEntries} entr{(Resync.UpdatedEntries == 1 ? "y" : "ies")} in {Resync.UpdatedObjects} asset object(s) and {ResyncedSceneEntries} in scenes.");
                    if (Resync.UnsavedAssets?.Count > 0)
                        text.AppendLine("Unity didn't save (a prefab with a missing script can't be saved):\n  " + string.Join("\n  ", Resync.UnsavedAssets));
                    if (Resync.TypesWithoutId?.Count > 0)
                        text.AppendLine("Value types without a stable id — add [RefTypeId]:\n  " +
                                        string.Join("\n  ", Resync.TypesWithoutId.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal)));
                }
                AppendList(text, "Changes", Details);
                AppendList(text, "Skipped", SkippedFiles);
                AppendList(text, "Scenes not resynced — open them and run Tools/StableRef/Resync All", ScenesToResync);
                AppendList(text, "Warnings", Warnings);
                return text.ToString();
            }

            private static void AppendList(StringBuilder text, string title, List<string> items)
            {
                if (items.Count == 0) return;
                text.AppendLine($"{title} ({items.Count}):");
                foreach (string item in items) text.AppendLine("  " + item);
            }
        }

        private const string MenuRoot = "Tools/StableRef/Migrate SerializeReference Data/";
        private const string Title = "StableRef Migration";

        /// <summary>Kinds of files that never hold MonoBehaviour / ScriptableObject data or prefab instances.</summary>
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".meta", ".cs", ".mat", ".anim", ".physicMaterial", ".physicsMaterial2D", ".renderTexture", ".cubemap",
            ".flare", ".mask", ".mixer", ".overrideController", ".shadervariants", ".guiskin", ".fontsettings",
            ".terrainlayer", ".spriteatlas", ".spriteatlasv2", ".giparams", ".lighting", ".preset"
        };

        /// <summary>Files read and converted at a time: holding the text of every scene and prefab at once can take gigabytes.</summary>
        private const int BatchSize = 256;

        private static readonly byte[] YamlHeader = Encoding.ASCII.GetBytes("%YAML");

        private sealed class ConvertedFile
        {
            public string Path;
            public MigrationYamlConverter.Result Result;
        }

        /// <summary>
        /// Folders whose scripts are rewritten — the same folder the data step covers, so no script is rewritten whose
        /// assets would be left in the old layout. Scripts of packages are never touched.
        /// </summary>
        private static readonly string[] CodeRoots = { "Assets" };

        [MenuItem(MenuRoot + "Analyze (Dry Run)", priority = 400)]
        private static void AnalyzeMenu() => Log(Analyze());

        [MenuItem(MenuRoot + "Analyze (Dry Run)", validate = true)]
        [MenuItem(MenuRoot + "Migrate…", validate = true)]
        private static bool CanRun() => !EditorApplication.isPlayingOrWillChangePlaymode && !MigrationContinuation.IsPending;

        [MenuItem(MenuRoot + "Migrate…", priority = 401)]
        private static void MigrateMenu()
        {
            var code = MigrationCodeRewriter.Plan(CodeRoots);
            string message = code.Changed
                ? $"Changes the type of {code.ConvertedFields} [SerializeReference] field(s) in the scripts to StableRef<T> / " +
                  "StableRefList<T> (same names). Code that uses such a field gets .Reference where the compiler reports it " +
                  "(and where it would silently test the wrapper); what is left is listed for a manual fix. " +
                  "Once the scripts compile, the saved data is moved into the new layout, prefab overrides are rewritten " +
                  "and stable ids and snapshots are stamped.\n\n"
                : "No [SerializeReference] fields left to rewrite in the scripts. Moves the data of fields already " +
                  "changed to StableRef<T> / StableRefList<T> into the new layout, rewrites prefab overrides and stamps " +
                  "stable ids and snapshots.\n\n";
            message += "Open scenes are closed for the migration (you are asked to save them) and reopened afterwards; " +
                       "converted scenes are opened one by one, resynced and saved.\n\n";
            if (!EditorUtility.DisplayDialog(Title, message + "Files are rewritten — commit or back up the project first.",
                    "Migrate", "Cancel"))
                return;

            if (!code.Changed)
            {
                var scenes = MigrationScenes.Close();
                if (scenes == null) return;
                var report = Run(FindCandidateFiles(), apply: true, interactive: true);
                MigrationScenes.Restore(scenes, report.Warnings);
                Log(report);
                return;
            }

            string summary = CodeSummary(code, applied: true);
            if (!MigrationContinuation.Begin(summary, code.ExpectedFields, code.FieldNames)) return;
            MigrationCodeRewriter.Apply(code);
            Debug.Log(summary + "\nWaiting for the scripts to compile; the data is migrated right after. Code that uses a " +
                      "retyped field is fixed by the compiler errors, round by round; what can't be fixed that way is " +
                      "listed — fix it by hand, the data waits for the next successful compile.");
            AssetDatabase.Refresh();
        }

        /// <summary>Reports what would change in the scripts and in the data, without writing anything.</summary>
        public static Report Analyze()
        {
            var report = Run(FindCandidateFiles(), apply: false);
            var code = MigrationCodeRewriter.Plan(CodeRoots);
            report.CodeSummary = CodeSummary(code, applied: false) +
                                 (code.Changed ? "\nThe data of these fields is analyzed once the scripts are rewritten and compiled." : "");
            return report;
        }

        /// <summary>
        /// Moves the data of fields already changed to StableRef in code, in every candidate file under <c>Assets/</c>.
        /// Rewriting the scripts needs a recompile in between, so it is only available from the menu.
        /// </summary>
        /// <param name="interactive">Ask before discarding unsaved in-memory changes of affected assets; otherwise
        /// such assets are skipped.</param>
        public static Report Migrate(bool interactive = false) => Run(FindCandidateFiles(), apply: true, interactive);

        private static string CodeSummary(MigrationCodeRewriter.Result code, bool applied)
        {
            var text = new StringBuilder();
            text.AppendLine(applied
                ? $"[StableRef] Migration: rewrote {code.ConvertedFields} field(s), fixed {code.FixedUses} use(s) that would test the wrapper, added {code.AddedTypeIds} [RefTypeId], {code.Files.Count} file(s) changed."
                : $"[StableRef] Migration (dry run): {code.ConvertedFields} field(s) to rewrite in the scripts, {code.FixedUses} use(s) that would test the wrapper to fix, {code.AddedTypeIds} [RefTypeId] to add, {code.Files.Count} file(s) to change.");
            if (code.Changes.Count > 0) text.AppendLine("Code (" + code.Changes.Count + "):\n  " + string.Join("\n  ", code.Changes));
            if (code.Warnings.Count > 0) text.AppendLine("Code warnings (" + code.Warnings.Count + "):\n  " + string.Join("\n  ", code.Warnings));
            return text.ToString().TrimEnd();
        }

        /// <summary>Migrates (or with <paramref name="apply"/> off, analyzes) the given asset files.</summary>
        public static Report Run(IEnumerable<string> assetPaths, bool apply, bool interactive = false)
        {
            var report = new Report();
            var converted = Convert(assetPaths, report);
            if (converted == null) return report;

            if (apply) converted = ExcludeBlocked(converted, report, interactive);
            foreach (var file in converted)
            {
                report.ConvertedFields += file.Result.ConvertedFields;
                report.RewrittenOverrides += file.Result.RewrittenOverrides;
                report.ChangedFiles.Add(file.Path);
                report.Details.Add(file.Path);
                report.Details.AddRange(file.Result.Changes.Select(c => "  " + c));
            }
            if (!apply || converted.Count == 0) return report;

            foreach (var file in converted)
                File.WriteAllText(file.Path, file.Result.Text, new UTF8Encoding(false));

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var file in converted)
                    AssetDatabase.ImportAsset(file.Path, ImportAssetOptions.ForceUpdate);
            }
            finally { AssetDatabase.StopAssetEditing(); }

            var assets = converted.Select(c => c.Path).Where(p => !IsScene(p)).ToList();
            report.ScenesToResync.AddRange(converted.Select(c => c.Path).Where(IsScene));
            report.Resync = StableRefResync.ResyncAssets(OrderBySource(assets));
            report.Applied = true;
            MigrationScenes.ResyncConverted(report);
            return report;
        }

        /// <summary>
        /// Files under <c>Assets/</c> that may hold MonoBehaviour / ScriptableObject data or prefab instances: every file
        /// except kinds that never do (materials, animation clips, presets…). Binary and non-YAML files are dropped
        /// when read, by their header.
        /// </summary>
        public static List<string> FindCandidateFiles()
        {
            return Directory.EnumerateFiles("Assets", "*", SearchOption.AllDirectories)
                .Where(p => !SkippedExtensions.Contains(Path.GetExtension(p)))
                .Select(p => p.Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        private static List<ConvertedFile> Convert(IEnumerable<string> assetPaths, Report report)
        {
            var paths = assetPaths.ToList();
            var converter = new MigrationYamlConverter(new MigrationAssetIndex());
            var converted = new List<ConvertedFile>();
            var progress = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                EditorUtility.DisplayProgressBar(Title, "Looking for [SerializeReference] data…", 0f);
                for (int batch = 0; batch < paths.Count; batch += BatchSize)
                {
                    int count = Math.Min(BatchSize, paths.Count - batch);
                    var texts = ReadCandidates(paths, batch, count, report);
                    for (int k = 0; k < count; k++)
                    {
                        int i = batch + k;
                        // Redrawing the bar for every file costs more than converting most of them.
                        if (progress.ElapsedMilliseconds > 100)
                        {
                            progress.Restart();
                            if (EditorUtility.DisplayCancelableProgressBar(Title, paths[i], (float)i / paths.Count))
                                return null;
                        }
                        if (texts[k] == null) continue;

                        var result = converter.Convert(paths[i], texts[k]);
                        report.Warnings.AddRange(result.Warnings);
                        if (result.Changed) converted.Add(new ConvertedFile { Path = paths[i], Result = result });
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            return converted;
        }

        /// <summary>
        /// Reads <paramref name="count"/> files from <paramref name="offset"/> in parallel (no Unity API off the main
        /// thread) and keeps the text of Unity YAML files that hold reference records or prefab overrides; other entries
        /// are <c>null</c>. Only the header of other files is read.
        /// </summary>
        private static string[] ReadCandidates(List<string> paths, int offset, int count, Report report)
        {
            var texts = new string[count];
            int yaml = 0;
            System.Threading.Tasks.Parallel.For(0, count, k =>
            {
                string text;
                try
                {
                    if (!HasYamlHeader(paths[offset + k])) return;
                    text = File.ReadAllText(paths[offset + k]);
                }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }

                System.Threading.Interlocked.Increment(ref yaml);
                if (text.Contains("rid: ") || text.Contains("propertyPath:")) texts[k] = text;
            });
            report.ScannedFiles += yaml;
            return texts;
        }

        private static bool HasYamlHeader(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16);
            var header = new byte[YamlHeader.Length];
            int read = 0, n;
            while (read < header.Length && (n = stream.Read(header, read, header.Length - read)) > 0) read += n;
            return read == header.Length && header.SequenceEqual(YamlHeader);
        }

        /// <summary>
        /// Leaves out files Unity holds in memory in a form the rewrite can't replace: open scenes, the prefab in Prefab
        /// Mode, and assets with unsaved changes — those changes were made on data already read in the old layout.
        /// </summary>
        private static List<ConvertedFile> ExcludeBlocked(List<ConvertedFile> converted, Report report, bool interactive)
        {
            var openScenes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < SceneManager.sceneCount; i++)
                openScenes.Add(SceneManager.GetSceneAt(i).path);
            string prefabStage = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath;

            var dirty = converted.Where(c => !IsScene(c.Path) && HasUnsavedChanges(c.Path)).Select(c => c.Path).ToList();
            bool discard = dirty.Count > 0 && interactive && EditorUtility.DisplayDialog(Title,
                "These assets have unsaved changes in memory, made on data Unity read in the old layout. Discard them " +
                "and migrate the files, or skip these assets?\n\n" + string.Join("\n", dirty.Take(15)) +
                (dirty.Count > 15 ? $"\n… and {dirty.Count - 15} more" : ""),
                "Discard and migrate", "Skip them");

            var result = new List<ConvertedFile>();
            foreach (var item in converted)
            {
                string reason = null;
                if (openScenes.Contains(item.Path)) reason = "the scene is open — close it and run again";
                else if (item.Path == prefabStage) reason = "the prefab is open in Prefab Mode — close it and run again";
                else if (dirty.Contains(item.Path))
                {
                    if (discard) DiscardInMemory(item.Path);
                    else reason = "unsaved changes in memory — save or revert them and run again";
                }

                if (reason == null) result.Add(item);
                else report.SkippedFiles.Add($"{item.Path}: {reason}");
            }

            return result;
        }

        private static bool HasUnsavedChanges(string path)
        {
            if (!AssetDatabase.IsMainAssetAtPathLoaded(path)) return false;
            return AssetDatabase.LoadAllAssetsAtPath(path).Any(o => o != null && EditorUtility.IsDirty(o));
        }

        private static void DiscardInMemory(string path)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj == null) continue;
                EditorUtility.ClearDirty(obj);
                if (obj is not GameObject && obj is not Component) Resources.UnloadAsset(obj);
            }
        }

        /// <summary>Non-prefab assets first, then prefabs with each source prefab before the prefabs that use it.</summary>
        private static List<string> OrderBySource(List<string> paths)
        {
            var prefabs = new HashSet<string>(paths.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)));
            var ordered = paths.Where(p => !prefabs.Contains(p)).ToList();
            var visited = new HashSet<string>();

            void Visit(string path)
            {
                if (!visited.Add(path)) return;
                foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                    if (prefabs.Contains(dependency)) Visit(dependency);
                ordered.Add(path);
            }

            foreach (string prefab in paths.Where(prefabs.Contains)) Visit(prefab);
            return ordered;
        }

        private static bool IsScene(string path) => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);

        internal static void Log(Report report)
        {
            string text = report.ToString();
            if (report.Warnings.Count > 0 || report.SkippedFiles.Count > 0 || report.Resync.UnsavedAssets?.Count > 0)
                Debug.LogWarning(text);
            else
                Debug.Log(text);
        }
    }
}
#endif
