using System.Threading;
using Molca.Editor.Validation;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Enables GPU Instancing on a shared material flagged by <c>scene-instancing-budget</c> (Sprint 55) —
    /// the cheapest draw-call win for a material used by many renderers. Unity-Undo reversible.
    /// </summary>
    public sealed class EnableInstancingFix : ISceneFix
    {
        /// <inheritdoc/>
        public string Id => "scene.enable-instancing";
        /// <inheritdoc/>
        public string Description => "Enable GPU Instancing on a shared material to cut draw calls.";
        /// <inheritdoc/>
        public string HandledCheckId => "scene-instancing-budget";
        /// <inheritdoc/>
        public FixReversibility Reversibility => FixReversibility.UnityUndo;

        /// <inheritdoc/>
        public SceneFixOutcome Apply(string target, bool dryRun, CancellationToken cancellationToken)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(target);
            if (mat == null)
                return SceneFixOutcome.NotApplied(
                    $"No material asset at '{target}'. Only per-material instancing findings are fixable; " +
                    "the scene material/mesh-count finding is a judgment call (share assets) with no mechanical fix.");
            if (mat.enableInstancing)
                return SceneFixOutcome.NotApplied($"Material '{mat.name}' already has GPU Instancing enabled.");

            if (dryRun)
                return new SceneFixOutcome(true, $"Would enable GPU Instancing on '{mat.name}'.",
                    "GPU Instancing: off", "GPU Instancing: on");

            Undo.RecordObject(mat, "Enable GPU Instancing");
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return new SceneFixOutcome(true, $"Enabled GPU Instancing on '{mat.name}'.",
                "GPU Instancing: off", "GPU Instancing: on");
        }
    }
}
