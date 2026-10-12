#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SST.StableRef.Migration
{
    /// <summary>
    /// Scenes during a migration. Open scenes are closed first — Unity holds them in memory in the old layout, and with
    /// the new types loaded it would hold them with empty fields — and reopened from the converted files afterwards.
    /// Converted scenes are opened one by one, resynced, saved and closed, so their entries get stable ids and
    /// snapshots like the assets do.
    /// </summary>
    /// <remarks>
    /// StableRef's own tools never open scenes; the migration does, because it is a one-time action confirmed in a
    /// dialog and its job is to leave every file finished.
    /// </remarks>
    internal static class MigrationScenes
    {
        /// <summary>The scenes that were open, to reopen them later.</summary>
        [Serializable]
        public sealed class State
        {
            public List<string> Paths = new();
            public List<bool> Loaded = new();
            public int Active = -1;
        }

        /// <summary>
        /// Asks to save modified scenes, leaves Prefab Mode, remembers the open scenes and replaces them with an empty
        /// one. <c>null</c> when the user cancels the save.
        /// </summary>
        public static State Close()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;
            StageUtility.GoToMainStage();
            return Replace();
        }

        /// <summary>
        /// Before the data step after a recompile: closes the scenes opened while the scripts were being fixed —
        /// <b>without saving</b>. The domain reload has read them into the new types, so their fields hold nothing and
        /// saving one would lose its data; they are reopened from the converted files by <see cref="Restore"/>. Unsaved
        /// changes are discarded only after the user confirms; <c>null</c> when they keep them (the scenes stay open
        /// and the data step skips them). Prefab Mode is left alone (the data step skips that prefab).
        /// </summary>
        public static State CloseWithoutSaving()
        {
            var dirty = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty) dirty.Add(string.IsNullOrEmpty(scene.path) ? "Untitled" : scene.path);
            }

            if (dirty.Count > 0 && !EditorUtility.DisplayDialog("StableRef Migration",
                    "These open scenes have unsaved changes made before the scripts compiled. Unity has just read them " +
                    "into the new field types, so saving them would lose their [SerializeReference] data.\n\n" +
                    string.Join("\n", dirty) + "\n\nDiscard the changes, migrate the scenes and reopen them, or keep " +
                    "them open and unmigrated (don't save them; close them and run Migrate… again)?",
                    "Discard and migrate", "Keep them open"))
                return null;

            return Replace();
        }

        /// <summary>Remembers the open scenes and replaces them with an empty one, discarding unsaved changes.</summary>
        private static State Replace()
        {
            var state = new State();
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            {
                if (string.IsNullOrEmpty(setup.path)) continue;
                if (setup.isActive) state.Active = state.Paths.Count;
                state.Paths.Add(setup.path);
                state.Loaded.Add(setup.isLoaded);
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return state;
        }

        /// <summary>Reopens the scenes, unless another scene has been opened or edited meanwhile.</summary>
        public static void Restore(State state, List<string> warnings)
        {
            if (state == null || state.Paths.Count == 0) return;
            bool untouched = SceneManager.sceneCount == 1 && string.IsNullOrEmpty(SceneManager.GetSceneAt(0).path)
                                                          && !SceneManager.GetSceneAt(0).isDirty;
            if (!untouched)
            {
                warnings.Add("The scenes open before the migration were not reopened (another scene is open now): " +
                                    string.Join(", ", state.Paths));
                return;
            }

            var setups = state.Paths
                .Select((path, i) => new SceneSetup
                    { path = path, isLoaded = i >= state.Loaded.Count || state.Loaded[i], isActive = i == state.Active })
                .Where(s => File.Exists(s.path))
                .ToArray();
            if (setups.Length == 0) return;
            if (!setups.Any(s => s.isActive)) setups[0].isActive = true;
            EditorSceneManager.RestoreSceneManagerSetup(setups);
        }

        /// <summary>
        /// Opens each converted scene additively, resyncs its entries, saves it if anything changed and closes it.
        /// Scenes that can't be handled stay in <see cref="SerializeReferenceMigrator.Report.ScenesToResync"/>.
        /// </summary>
        public static void ResyncConverted(SerializeReferenceMigrator.Report report)
        {
            var scenes = report.ScenesToResync.ToList();
            if (scenes.Count == 0) return;

            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    string path = scenes[i];
                    EditorUtility.DisplayProgressBar("StableRef Migration", "Resyncing " + path, (float)i / scenes.Count);
                    if (SceneManager.GetSceneByPath(path).IsValid())
                    {
                        report.Warnings.Add($"{path}: the scene is open — run Tools/StableRef/Resync All for it");
                        continue;
                    }

                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    try
                    {
                        int updated = 0;
                        foreach (var root in scene.GetRootGameObjects())
                        foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                            if (component != null) updated += StableRefResync.ResyncObject(component);

                        if (updated > 0)
                        {
                            EditorSceneManager.MarkSceneDirty(scene);
                            if (!EditorSceneManager.SaveScene(scene))
                            {
                                report.Warnings.Add($"{path}: Unity didn't save the resynced scene");
                                continue;
                            }
                        }

                        report.ResyncedSceneEntries += updated;
                        report.ScenesToResync.Remove(path);
                    }
                    finally { EditorSceneManager.CloseScene(scene, true); }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
#endif
