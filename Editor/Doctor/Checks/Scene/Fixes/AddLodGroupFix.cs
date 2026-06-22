using System.Linq;
using System.Threading;
using Molca.Editor.Validation;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Adds an empty <see cref="LODGroup"/> scaffold (the renderer(s) as LOD0) to a high-poly renderer with
    /// none, flagged by <c>scene-structure</c> (Sprint 55). Conservative: it only scaffolds LOD0 and leaves
    /// authoring the reduced LODs to the user. Unity-Undo reversible.
    /// </summary>
    public sealed class AddLodGroupFix : ISceneFix
    {
        /// <inheritdoc/>
        public string Id => "scene.add-lodgroup";
        /// <inheritdoc/>
        public string Description => "Add a LODGroup (renderer as LOD0) to a high-poly renderer that has none.";
        /// <inheritdoc/>
        public string HandledCheckId => "scene-structure";
        /// <inheritdoc/>
        public FixReversibility Reversibility => FixReversibility.UnityUndo;

        /// <inheritdoc/>
        public SceneFixOutcome Apply(string target, bool dryRun, CancellationToken cancellationToken)
        {
            var go = SceneFixTargets.ResolveGameObject(target, out var error);
            if (go == null)
                return SceneFixOutcome.NotApplied(
                    $"Could not resolve a GameObject for '{target}': {error}. The object-count/hierarchy-depth " +
                    "findings are judgment calls with no mechanical fix.");

            if (go.GetComponentInParent<LODGroup>() != null)
                return SceneFixOutcome.NotApplied($"'{go.name}' is already covered by a LODGroup.");

            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true)
                .Where(r => r != null).ToArray();
            if (renderers.Length == 0)
                return SceneFixOutcome.NotApplied($"'{go.name}' has no renderers to scaffold a LODGroup from.");

            if (dryRun)
                return new SceneFixOutcome(true, $"Would add a LODGroup (LOD0 = {renderers.Length} renderer(s)) to '{go.name}'.",
                    "LODGroup: none", "LODGroup: LOD0 scaffold");

            var lodGroup = Undo.AddComponent<LODGroup>(go);
            lodGroup.SetLODs(new[] { new LOD(0.1f, renderers) });
            lodGroup.RecalculateBounds();
            EditorUtility.SetDirty(go);
            return new SceneFixOutcome(true,
                $"Added a LODGroup to '{go.name}' with {renderers.Length} renderer(s) as LOD0. Author the reduced LODs next.",
                "LODGroup: none", "LODGroup: LOD0 scaffold");
        }
    }
}
