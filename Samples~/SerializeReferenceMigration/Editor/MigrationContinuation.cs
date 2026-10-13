#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// The second half of a migration that rewrote scripts: after they compile and the domain reloads, moves the data
    /// and reopens the scenes closed for the recompile. The state lives in a file under <c>Library/</c>, so it also
    /// survives an editor restart; while the scripts don't compile, the old types stay loaded and the data stays
    /// readable, and the migration waits for the next successful compile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data step runs only once the rewritten scripts are loaded (<see cref="AreRewrittenScriptsLoaded"/>): an editor
    /// started with compile errors (Safe Mode → Ignore) loads the last assemblies that compiled, in which the fields are
    /// still plain <c>[SerializeReference]</c>, and a run there would find nothing to convert and end the migration.
    /// </para>
    /// <para>
    /// While it waits, every compile's messages go to <see cref="MigrationCodeRewriter.FixCompilerMessages"/>: the code
    /// that uses the retyped fields is fixed by what the compiler reports, and the scripts recompile, round after round
    /// (each round reaches the assemblies that depend on the ones fixed before), until they compile or a round fixes
    /// nothing — what is left is listed for a manual fix.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class MigrationContinuation
    {
        [Serializable]
        private sealed class Pending
        {
            public MigrationScenes.State Scenes = new();
            public string CodeSummary;
            public List<string> ExpectedFields = new();
            public List<string> FieldNames = new();
            /// <summary>Uses fixed by compiler messages so far.</summary>
            public List<string> FixedUses = new();
            public int FixRounds;
            /// <summary>Uses were fixed after a successful compile: the data waits for one more.</summary>
            public bool AwaitRecompile;
        }

        private const string PendingPath = "Library/StableRefMigration.pending.json";
        private const string MenuRoot = "Tools/StableRef/Migrate SerializeReference Data/";
        private const int MaxFixRounds = 30;
        private static readonly string[] CodeRoots = { "Assets" };

        /// <summary>Messages of the compile in progress; a failed compile keeps this domain, a successful one reloads it.</summary>
        private static readonly List<CompilerMessage> _messages = new();

        static MigrationContinuation()
        {
            CompilationPipeline.compilationStarted += _ => _messages.Clear();
            CompilationPipeline.assemblyCompilationFinished += (_, messages) => { if (IsPending) _messages.AddRange(messages); };
            CompilationPipeline.compilationFinished += _ => FixUses();
            if (File.Exists(PendingPath)) EditorApplication.delayCall += () => Continue(force: false);
        }

        /// <summary>Whether the code was rewritten and the data still waits for a successful compile.</summary>
        public static bool IsPending => File.Exists(PendingPath);

        /// <summary>
        /// Closes the open scenes and remembers them, with the fields the data step waits for and their names in code
        /// (to fix their uses by compiler messages).
        /// </summary>
        public static bool Begin(string codeSummary, List<string> expectedFields, List<string> fieldNames)
        {
            var scenes = MigrationScenes.Close();
            if (scenes == null) return false;
            Save(new Pending { Scenes = scenes, CodeSummary = codeSummary, ExpectedFields = expectedFields, FieldNames = fieldNames });
            return true;
        }

        /// <summary>
        /// After a compile, fixes the uses of the retyped fields the compiler reported. With errors the domain stays and
        /// the scripts are refreshed right away; after a successful compile the fixes (warnings only) wait for the reload,
        /// and <see cref="Continue"/> recompiles before moving the data.
        /// </summary>
        private static void FixUses()
        {
            if (!IsPending || _messages.Count == 0) return;
            var messages = _messages.ToList();
            _messages.Clear();
            bool failed = messages.Any(m => m.type == CompilerMessageType.Error);

            var pending = Load();
            var changes = new List<string>();
            var unfixed = new List<string>();
            int written = pending.FixRounds < MaxFixRounds
                ? MigrationCodeRewriter.FixCompilerMessages(messages, pending.FieldNames, CodeRoots, changes, unfixed)
                : 0;

            if (written == 0)
            {
                if (failed && unfixed.Count > 0)
                    Debug.LogWarning($"[StableRef] Migration: {unfixed.Count} place(s) that use the retyped fields need a manual fix " +
                                     "(the data waits for the scripts to compile):\n  " + string.Join("\n  ", unfixed.Distinct()));
                return;
            }

            pending.FixRounds++;
            pending.FixedUses.AddRange(changes);
            pending.AwaitRecompile |= !failed;
            Save(pending);
            Debug.Log($"[StableRef] Migration: fixed {changes.Count} use(s) of the retyped fields by the compiler messages " +
                      $"(round {pending.FixRounds}), recompiling:\n  " + string.Join("\n  ", changes));
            if (failed) EditorApplication.delayCall += AssetDatabase.Refresh;
        }

        /// <summary>
        /// The entries of <paramref name="expectedFields"/> (<c>Assembly|Owner.FullName|field</c>) whose field the
        /// loaded scripts don't declare as <c>StableRef&lt;T&gt;</c> / <c>StableRefList&lt;T&gt;</c> yet.
        /// </summary>
        public static List<string> MissingFields(IEnumerable<string> expectedFields)
        {
            var missing = new List<string>();
            foreach (string entry in expectedFields)
            {
                string[] parts = entry.Split('|');
                var owner = parts.Length == 3 ? Type.GetType(parts[1] + ", " + parts[0], false) : null;
                var field = owner?.GetField(parts[2], System.Reflection.BindingFlags.Instance
                                                      | System.Reflection.BindingFlags.Public
                                                      | System.Reflection.BindingFlags.NonPublic);
                if (field == null || (!StableRefReflection.IsStableRef(field.FieldType, out _)
                                      && !StableRefReflection.IsStableRefList(field.FieldType, out _)))
                    missing.Add(parts.Length == 3 ? parts[1] + "." + parts[2] : entry);
            }
            return missing;
        }

        [MenuItem(MenuRoot + "Resume Pending Migration", priority = 420)]
        private static void ResumeMenu()
        {
            var missing = MissingFields(Load().ExpectedFields);
            if (missing.Count > 0 && !EditorUtility.DisplayDialog("StableRef Migration",
                    "The loaded scripts don't declare these rewritten fields as StableRef yet:\n\n" + Excerpt(missing) +
                    "\n\nTheir data is not moved if you continue now. Continue anyway?", "Continue", "Cancel"))
                return;
            Continue(force: true);
        }

        [MenuItem(MenuRoot + "Cancel Pending Migration", priority = 421)]
        private static void CancelMenu()
        {
            if (!EditorUtility.DisplayDialog("StableRef Migration",
                    "Stop waiting for the scripts to compile? The scripts stay rewritten; the data stays in the old layout " +
                    "until you run Migrate… again, and Unity drops it the first time such an asset is saved.",
                    "Stop waiting", "Keep waiting"))
                return;

            var pending = Load();
            File.Delete(PendingPath);
            var warnings = new List<string>();
            MigrationScenes.Restore(pending.Scenes, warnings);
            foreach (string warning in warnings) Debug.LogWarning("[StableRef] Migration: " + warning);
        }

        [MenuItem(MenuRoot + "Resume Pending Migration", validate = true)]
        [MenuItem(MenuRoot + "Cancel Pending Migration", validate = true)]
        private static bool CanResume() => IsPending && !EditorApplication.isPlayingOrWillChangePlaymode;

        private static void Continue(bool force)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += () => Continue(force);
                return;
            }
            if (!File.Exists(PendingPath)) return;

            var pending = Load();
            if (pending.AwaitRecompile && !force)
            {
                pending.AwaitRecompile = false;
                Save(pending);
                AssetDatabase.Refresh();
                // A recompile reloads the domain and runs this again; without one the data step goes on below.
                if (EditorApplication.isCompiling)
                {
                    EditorApplication.delayCall += () => Continue(force: false);
                    return;
                }
            }
            var missing = MissingFields(pending.ExpectedFields);
            if (!force && !AreRewrittenScriptsLoaded(pending.ExpectedFields, missing))
            {
                // Runs again after the next domain reload, which follows the next successful compile.
                Debug.LogWarning("[StableRef] Migration: the data waits for the rewritten scripts to compile. Fix the " +
                                 $"compile errors, or use {MenuRoot}Resume Pending Migration / Cancel Pending Migration.");
                return;
            }
            File.Delete(PendingPath);

            // Scenes opened while the code was being fixed now hold empty fields: close them unsaved, reopen them
            // converted. If none were opened, the scenes closed by Begin are reopened.
            var reopened = MigrationScenes.CloseWithoutSaving();
            var scenes = reopened == null ? null : reopened.Paths.Count > 0 ? reopened : pending.Scenes;

            SerializeReferenceMigrator.Report report = null;
            try
            {
                report = SerializeReferenceMigrator.Run(SerializeReferenceMigrator.FindCandidateFiles(), apply: true, interactive: true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[StableRef] Migration: moving the data failed. Nothing has saved the affected assets yet — " +
                               $"fix the cause and run {MenuRoot}Migrate… again.");
            }

            var warnings = report?.Warnings ?? new List<string>();
            MigrationScenes.Restore(scenes, warnings);
            if (report == null)
            {
                foreach (string warning in warnings) Debug.LogWarning("[StableRef] Migration: " + warning);
                return;
            }

            if (missing.Count > 0)
                report.Warnings.Add("These rewritten fields are not StableRef in the compiled scripts, their data was not " +
                                    "moved (run Migrate… again once they are):\n    " + string.Join("\n    ", missing));
            report.CodeSummary = pending.CodeSummary;
            if (pending.FixedUses.Count > 0)
                report.CodeSummary += $"\nFixed by the compiler messages ({pending.FixedUses.Count}):\n  " + string.Join("\n  ", pending.FixedUses);
            SerializeReferenceMigrator.Log(report);
        }

        /// <summary>
        /// Whether the scripts loaded now are the rewritten ones: the last compile succeeded and at least one rewritten
        /// field (of <paramref name="expectedFields"/>, <paramref name="missing"/> of them not StableRef) is StableRef.
        /// Old assemblies — a failed compile, or an editor started into the last compiled scripts — have none, and the
        /// data must wait. A field still missing after that was changed back by hand: the data step leaves it alone.
        /// </summary>
        public static bool AreRewrittenScriptsLoaded(ICollection<string> expectedFields, ICollection<string> missing) =>
            !EditorUtility.scriptCompilationFailed && (expectedFields.Count == 0 || missing.Count < expectedFields.Count);

        private static void Save(Pending pending) => File.WriteAllText(PendingPath, JsonUtility.ToJson(pending, true));

        private static Pending Load()
        {
            try { return JsonUtility.FromJson<Pending>(File.ReadAllText(PendingPath)) ?? new Pending(); }
            catch (Exception) { return new Pending(); }
        }

        private static string Excerpt(List<string> items) =>
            string.Join("\n", items.GetRange(0, Math.Min(items.Count, 15))) +
            (items.Count > 15 ? $"\n… and {items.Count - 15} more" : "");
    }
}
#endif
