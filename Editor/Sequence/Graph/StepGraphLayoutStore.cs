using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Molca.Editor.Graph
{
    /// <summary>
    /// Editor-only persistence for sequence-graph node positions. Positions are keyed by the
    /// controller's and step's <c>RefId</c> (never a GameObject reference or a runtime field on
    /// <see cref="Molca.Sequence.Step"/> — the step asset is a protected zone), and stored as JSON
    /// under <c>ProjectSettings/Molca/</c> so they survive domain reloads and editor restarts and
    /// can be committed alongside the project.
    /// </summary>
    /// <remarks>
    /// Sprint 8.2 store: the graph window seeds node positions from here on open and writes back
    /// when nodes are moved. Keyed by Ref Id so it is robust to renames and re-parenting. File I/O
    /// failures are logged and degrade gracefully to auto-layout — they never throw into the editor.
    /// </remarks>
    public static class StepGraphLayoutStore
    {
        private static StepGraphLayoutData _cache;

        private static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private static string FilePath =>
            Path.Combine(ProjectRoot, "ProjectSettings", "Molca", "SequenceGraphLayout.json");

        private static StepGraphLayoutData Data => _cache ??= LoadFromDisk();

        /// <summary>
        /// Loads the persisted positions for one controller, keyed by step Ref Id.
        /// </summary>
        /// <param name="controllerRefId">The controller's Ref Id.</param>
        /// <returns>A map from step Ref Id to position; empty if nothing is stored.</returns>
        public static Dictionary<string, Vector2> Load(string controllerRefId)
        {
            var result = new Dictionary<string, Vector2>();
            if (string.IsNullOrEmpty(controllerRefId)) return result;

            var controller = Data.controllers.FirstOrDefault(c => c.controllerRefId == controllerRefId);
            if (controller == null) return result;

            foreach (var entry in controller.steps)
            {
                if (!string.IsNullOrEmpty(entry.stepRefId))
                    result[entry.stepRefId] = new Vector2(entry.x, entry.y);
            }
            return result;
        }

        /// <summary>
        /// Persists the positions for one controller, replacing any previously stored entries.
        /// </summary>
        /// <param name="controllerRefId">The controller's Ref Id.</param>
        /// <param name="positionsByStepRefId">Step Ref Id → position. Empty Ref Ids are skipped.</param>
        public static void Save(string controllerRefId, IReadOnlyDictionary<string, Vector2> positionsByStepRefId)
        {
            if (string.IsNullOrEmpty(controllerRefId) || positionsByStepRefId == null) return;

            var controller = Data.controllers.FirstOrDefault(c => c.controllerRefId == controllerRefId);
            if (controller == null)
            {
                controller = new ControllerLayout { controllerRefId = controllerRefId };
                Data.controllers.Add(controller);
            }

            controller.steps.Clear();
            foreach (var kvp in positionsByStepRefId)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                controller.steps.Add(new StepPosition { stepRefId = kvp.Key, x = kvp.Value.x, y = kvp.Value.y });
            }

            WriteToDisk();
        }

        private static StepGraphLayoutData LoadFromDisk()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var data = JsonUtility.FromJson<StepGraphLayoutData>(json);
                    if (data != null)
                    {
                        data.controllers ??= new List<ControllerLayout>();
                        return data;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SequenceGraph] Could not read layout store at '{FilePath}': {e.Message}");
            }
            return new StepGraphLayoutData();
        }

        private static void WriteToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonUtility.ToJson(Data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SequenceGraph] Could not write layout store at '{FilePath}': {e.Message}");
            }
        }

        // --- Serializable backing model (JsonUtility requires concrete [Serializable] types) ---

        [Serializable]
        private sealed class StepGraphLayoutData
        {
            public List<ControllerLayout> controllers = new List<ControllerLayout>();
        }

        [Serializable]
        private sealed class ControllerLayout
        {
            public string controllerRefId;
            public List<StepPosition> steps = new List<StepPosition>();
        }

        [Serializable]
        private sealed class StepPosition
        {
            public string stepRefId;
            public float x;
            public float y;
        }
    }
}
