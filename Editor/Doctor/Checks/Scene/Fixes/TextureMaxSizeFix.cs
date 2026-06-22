using System.IO;
using System.Threading;
using Molca.Editor.Mcp;
using Molca.Editor.Validation;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Halves a texture's <see cref="TextureImporter.maxTextureSize"/> when <c>scene-texture-budget</c>
    /// flagged it as oversized (Sprint 55). Reverts via a <see cref="McpUndoStack"/> snapshot of the
    /// <c>.meta</c> (the importer setting lives there), not Unity Undo. The reimport honors cancellation.
    /// </summary>
    public sealed class TextureMaxSizeFix : ISceneFix
    {
        private const int MinTextureSize = 32;

        /// <inheritdoc/>
        public string Id => "scene.reduce-texture-size";
        /// <inheritdoc/>
        public string Description => "Reduce an oversized texture's max import size by one step (halve).";
        /// <inheritdoc/>
        public string HandledCheckId => "scene-texture-budget";
        /// <inheritdoc/>
        public FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public SceneFixOutcome Apply(string target, bool dryRun, CancellationToken cancellationToken)
        {
            if (AssetImporter.GetAtPath(target) is not TextureImporter importer)
                return SceneFixOutcome.NotApplied(
                    $"No TextureImporter at '{target}'. The scene texture-memory total finding is a judgment " +
                    "call (reduce/compress textures) with no single mechanical fix.");

            var current = importer.maxTextureSize;
            var next = Mathf.Max(MinTextureSize, current / 2);
            if (next >= current)
                return SceneFixOutcome.NotApplied($"'{Path.GetFileName(target)}' is already at the minimum max size ({current}).");

            if (dryRun)
                return new SceneFixOutcome(true, $"Would reduce '{Path.GetFileName(target)}' max size {current}→{next}.",
                    $"maxTextureSize: {current}", $"maxTextureSize: {next}");

            if (cancellationToken.IsCancellationRequested)
                return SceneFixOutcome.NotApplied("Cancelled before reimport.");

            // Snapshot the .meta (where maxTextureSize is serialized) so the reimport is revertible.
            var undoId = McpUndoStack.Snapshot(target + ".meta", "molca_scene_fix",
                $"Reduce {Path.GetFileName(target)} maxTextureSize {current}→{next}");

            importer.maxTextureSize = next;
            importer.SaveAndReimport();

            return new SceneFixOutcome(true,
                $"Reduced '{Path.GetFileName(target)}' max size {current}→{next}. Revert with molca_undo_last_action.",
                $"maxTextureSize: {current}", $"maxTextureSize: {next}", undoId);
        }
    }
}
