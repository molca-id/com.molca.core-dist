using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;

namespace Molca.Editor
{
    /// <summary>
    /// Edit-mode step CRUD controls. All mutations go through
    /// <see cref="StepEditingService"/>; this partial only owns the UI and the
    /// window-state cleanup (selection, caches, inspector) around each operation.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        private void AddNewStep(System.Type stepType, Step parentStep)
        {
            if (_selectedController == null)
            {
                Debug.LogError("Cannot add step: No SequenceController selected.");
                return;
            }

            var newStep = StepEditingService.AddStep(_selectedController, stepType, parentStep);
            if (newStep == null) return;

            _selection.Select(newStep);
            SyncSelectionToUnity();

            _hierarchyDirty = true;
            Repaint();
        }

        private void RemoveStep(Step step)
        {
            RemoveSteps(new[] { step });
        }

        private void RemoveSteps(IReadOnlyList<Step> steps)
        {
            if (steps == null || steps.Count == 0) return;

            foreach (var step in steps)
            {
                _selection.RemoveWithDescendants(step);
            }
            if (_selection.Count == 0 && _stepEditor != null)
            {
                UnityEngine.Object.DestroyImmediate(_stepEditor);
                _stepEditor = null;
            }

            StepEditingService.RemoveSteps(steps);
            _hierarchyDirty = true;
            Repaint();
        }

        private void ChangeStepType(Step oldStep, System.Type newType)
        {
            var newStep = StepEditingService.ChangeStepType(oldStep, newType);
            if (newStep == null || newStep == oldStep) return;

            _selection.Replace(oldStep, newStep);
            SyncSelectionToUnity();
            _hierarchyDirty = true;
            ClearCaches(); // The type name has changed
            if (_stepEditor != null) UnityEngine.Object.DestroyImmediate(_stepEditor); // Force inspector rebuild
            Repaint();
        }

        /// <summary>
        /// Converts every step in the current selection to <paramref name="newType"/> as one
        /// undo group, then remaps the selection onto the replacement components.
        /// </summary>
        private void ChangeStepTypes(System.Type newType)
        {
            var converted = StepEditingService.ChangeStepTypes(new List<Step>(_selection.Selected), newType);
            if (converted.Count == 0) return;

            foreach (var pair in converted)
            {
                _selection.Replace(pair.Key, pair.Value);
            }
            SyncSelectionToUnity();
            _hierarchyDirty = true;
            ClearCaches(); // Type names changed
            if (_stepEditor != null) UnityEngine.Object.DestroyImmediate(_stepEditor); // Force inspector rebuild
            Repaint();
        }

        /// <summary>
        /// Duplicates the current selection (subtree + fresh Ref Ids) via
        /// <see cref="StepEditingService.DuplicateSteps"/> and selects the clones.
        /// </summary>
        private void DuplicateSelectedSteps()
        {
            if (_selection.Count == 0) return;

            var clones = StepEditingService.DuplicateSteps(new List<Step>(_selection.Selected));
            if (clones.Count == 0) return;

            _selection.SelectMany(clones, clones[clones.Count - 1]);
            SyncSelectionToUnity();
            _hierarchyDirty = true;
            ClearCaches();
            Repaint();
        }

        private void FindAllStepTypes()
        {
            if (_stepTypes != null) return;

            // TypeCache is editor-native and avoids scanning every loaded assembly.
            _stepTypes = TypeCache.GetTypesDerivedFrom<Step>()
                .Where(type => !type.IsAbstract)
                .Concat(new[] { typeof(Step) }) // base Step itself is a valid concrete step
                .OrderBy(t => t.Name)
                .ToList();
        }
    }
}
