using System.Threading;
using Molca.Editor.Validation;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Marks a renderer GameObject <b>Contribute GI</b> when <c>scene-lighting-budget</c> flagged it as
    /// missing the static flag in a baked scene (Sprint 55). Unity-Undo reversible.
    /// </summary>
    public sealed class ContributeGiFix : ISceneFix
    {
        /// <inheritdoc/>
        public string Id => "scene.contribute-gi";
        /// <inheritdoc/>
        public string Description => "Mark a renderer's GameObject Contribute GI so it participates in baked lighting.";
        /// <inheritdoc/>
        public string HandledCheckId => "scene-lighting-budget";
        /// <inheritdoc/>
        public FixReversibility Reversibility => FixReversibility.UnityUndo;

        /// <inheritdoc/>
        public SceneFixOutcome Apply(string target, bool dryRun, CancellationToken cancellationToken)
        {
            var go = SceneFixTargets.ResolveGameObject(target, out var error);
            if (go == null)
                return SceneFixOutcome.NotApplied(
                    $"Could not resolve a GameObject for '{target}': {error}. The realtime-light-count finding " +
                    "is a judgment call (rebake/reduce lights) with no mechanical fix.");

            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            if ((flags & StaticEditorFlags.ContributeGI) != 0)
                return SceneFixOutcome.NotApplied($"'{go.name}' already contributes GI.");

            if (dryRun)
                return new SceneFixOutcome(true, $"Would mark '{go.name}' Contribute GI.",
                    "Contribute GI: off", "Contribute GI: on");

            Undo.RecordObject(go, "Set Contribute GI");
            GameObjectUtility.SetStaticEditorFlags(go, flags | StaticEditorFlags.ContributeGI);
            EditorUtility.SetDirty(go);
            return new SceneFixOutcome(true, $"Marked '{go.name}' Contribute GI.",
                "Contribute GI: off", "Contribute GI: on");
        }
    }
}
