using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>Validation roll-up for a single <see cref="SequenceController"/>.</summary>
    public sealed class ControllerValidationSummary
    {
        /// <summary>Path of the scene the controller lives in.</summary>
        public string ScenePath;

        /// <summary>Controller GameObject name.</summary>
        public string ControllerName;

        /// <summary>Controller Ref Id.</summary>
        public string ControllerRefId;

        /// <summary>Number of steps under the controller.</summary>
        public int StepCount;

        /// <summary>Validation error count.</summary>
        public int ErrorCount;

        /// <summary>Validation warning count.</summary>
        public int WarningCount;

        /// <summary>True when the controller has no errors.</summary>
        public bool Valid => ErrorCount == 0;

        /// <summary>Findings, when the sweep was asked for detail; otherwise empty.</summary>
        public List<SequenceValidationFinding> Findings = new();
    }

    /// <summary>Aggregated result of a project-wide validation sweep.</summary>
    public sealed class SequenceSweepResult
    {
        /// <summary>Per-controller summaries.</summary>
        public List<ControllerValidationSummary> Controllers = new();

        /// <summary>Scene paths actually swept.</summary>
        public List<string> ScenesSwept = new();

        /// <summary>Total controllers validated.</summary>
        public int TotalControllers => Controllers.Count;

        /// <summary>Controllers with at least one error.</summary>
        public int InvalidControllers => Controllers.Count(c => !c.Valid);

        /// <summary>Sum of errors across all controllers.</summary>
        public int TotalErrors => Controllers.Sum(c => c.ErrorCount);

        /// <summary>Sum of warnings across all controllers.</summary>
        public int TotalWarnings => Controllers.Sum(c => c.WarningCount);
    }

    /// <summary>
    /// Runs the Sprint-37 <see cref="SequenceValidatorRegistry"/> across many controllers at once.
    /// Pure enumeration + aggregation — no new validation logic.
    /// </summary>
    public static class SequenceValidationSweep
    {
        /// <summary>
        /// Validates every <see cref="SequenceController"/> in the currently open scene(s). Side-effect-free
        /// (opens/closes nothing).
        /// </summary>
        /// <param name="includeFindings">When true, each summary carries its full findings.</param>
        /// <returns>The aggregated result.</returns>
        public static SequenceSweepResult SweepLoadedScenes(bool includeFindings = false)
        {
            var result = new SequenceSweepResult();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                result.ScenesSwept.Add(scene.path);
                SweepScene(scene, includeFindings, result);
            }
            return result;
        }

        /// <summary>
        /// Validates every controller in the named scenes, opening any that aren't already loaded
        /// (additively) and closing only those it opened. <b>This mutates editor scene state</b> — use it
        /// only for an explicit, user-initiated sweep, never from a side-effect-free context.
        /// </summary>
        /// <param name="scenePaths">Scene asset paths to sweep.</param>
        /// <param name="includeFindings">When true, each summary carries its full findings.</param>
        /// <returns>The aggregated result.</returns>
        public static SequenceSweepResult SweepScenes(IReadOnlyList<string> scenePaths, bool includeFindings = false)
        {
            var result = new SequenceSweepResult();
            if (scenePaths == null) return result;

            foreach (var path in scenePaths)
            {
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                    continue;

                var scene = SceneManager.GetSceneByPath(path);
                bool wasLoaded = scene.IsValid() && scene.isLoaded;
                if (!wasLoaded)
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);

                if (scene.isLoaded)
                {
                    result.ScenesSwept.Add(path);
                    SweepScene(scene, includeFindings, result);
                    if (!wasLoaded)
                        EditorSceneManager.CloseScene(scene, removeScene: true); // close only what we opened
                }
            }
            return result;
        }

        private static void SweepScene(Scene scene, bool includeFindings, SequenceSweepResult result)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var controller in root.GetComponentsInChildren<SequenceController>(true))
                {
                    var findings = SequenceValidatorRegistry.Run(controller);
                    var summary = new ControllerValidationSummary
                    {
                        ScenePath = scene.path,
                        ControllerName = controller.name,
                        ControllerRefId = controller.RefId,
                        StepCount = controller.GetComponentsInChildren<Step>(true).Length,
                        ErrorCount = findings.Count(f => f.Severity == SequenceValidationSeverity.Error),
                        WarningCount = findings.Count(f => f.Severity == SequenceValidationSeverity.Warning),
                    };
                    if (includeFindings) summary.Findings = findings;
                    result.Controllers.Add(summary);
                }
            }
        }
    }
}
