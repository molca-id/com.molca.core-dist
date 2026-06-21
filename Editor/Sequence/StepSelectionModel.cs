using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// GUI-free selection model for sequence authoring views (visualizer tree, graph editor).
    /// Owns the selected-step list, the anchor used for shift-range selection, and the
    /// round-trip guard for reconciling with Unity's global <c>Selection</c>.
    /// </summary>
    /// <remarks>
    /// The model never touches <c>UnityEditor.Selection</c> itself — views push to Unity
    /// (calling <see cref="MarkSyncingToUnity"/> when they do) and feed Unity selection
    /// changes back through <see cref="ReconcileFromUnity"/>. The primary step is always
    /// the last element of <see cref="Selected"/>.
    /// </remarks>
    public sealed class StepSelectionModel
    {
        private readonly List<Step> _selected = new List<Step>();
        private bool _syncingToUnity;

        /// <summary>Currently selected steps, in selection order. Primary is last.</summary>
        public IReadOnlyList<Step> Selected => _selected;

        /// <summary>Number of selected steps.</summary>
        public int Count => _selected.Count;

        /// <summary>The primary selected step (last selected), or <c>null</c> if none.</summary>
        public Step Primary => _selected.Count > 0 ? _selected[_selected.Count - 1] : null;

        /// <summary>The anchor step used as the fixed end of a shift-range selection.</summary>
        public Step Anchor { get; private set; }

        /// <summary>Raised after any mutation of the selection.</summary>
        public event Action SelectionChanged;

        /// <summary>Returns whether <paramref name="step"/> is currently selected.</summary>
        public bool IsSelected(Step step) => step != null && _selected.Contains(step);

        /// <summary>
        /// Applies a click with the given modifiers: plain = replace, ctrl/cmd = toggle,
        /// shift = range from <see cref="Anchor"/> within <paramref name="visibleOrder"/>.
        /// </summary>
        /// <param name="step">The clicked step.</param>
        /// <param name="shift">Shift modifier held.</param>
        /// <param name="ctrlOrCmd">Ctrl (Windows) or Cmd (macOS) modifier held.</param>
        /// <param name="visibleOrder">
        /// Steps in display order, used only for shift-range selection. May be <c>null</c>
        /// when <paramref name="shift"/> is <c>false</c>.
        /// </param>
        public void Click(Step step, bool shift, bool ctrlOrCmd, IReadOnlyList<Step> visibleOrder)
        {
            if (step == null) return;
            if (shift && Anchor != null)
            {
                RangeSelectTo(step, visibleOrder);
            }
            else if (ctrlOrCmd)
            {
                Toggle(step);
            }
            else
            {
                Select(step);
            }
        }

        /// <summary>Replaces the selection with a single step and makes it the anchor.</summary>
        public void Select(Step step)
        {
            if (step == null) return;
            _selected.Clear();
            _selected.Add(step);
            Anchor = step;
            RaiseChanged();
        }

        /// <summary>Adds the step if unselected, removes it if selected; the step becomes the anchor either way.</summary>
        public void Toggle(Step step)
        {
            if (step == null) return;
            int idx = _selected.IndexOf(step);
            if (idx >= 0)
                _selected.RemoveAt(idx);
            else
                _selected.Add(step);
            Anchor = step;
            RaiseChanged();
        }

        /// <summary>
        /// Selects the contiguous range between <see cref="Anchor"/> and <paramref name="step"/>
        /// within <paramref name="visibleOrder"/>. Falls back to a single-step selection when
        /// either end is not present in the list. The clicked step becomes the new anchor.
        /// </summary>
        /// <param name="step">The step at the moving end of the range.</param>
        /// <param name="visibleOrder">Steps in display order (filtered + expanded), as the view draws them.</param>
        public void RangeSelectTo(Step step, IReadOnlyList<Step> visibleOrder)
        {
            if (step == null) return;
            int anchorIdx = visibleOrder != null && Anchor != null ? IndexOf(visibleOrder, Anchor) : -1;
            int stepIdx = visibleOrder != null ? IndexOf(visibleOrder, step) : -1;
            _selected.Clear();
            if (anchorIdx >= 0 && stepIdx >= 0)
            {
                int start = Mathf.Min(anchorIdx, stepIdx);
                int end = Mathf.Max(anchorIdx, stepIdx);
                for (int i = start; i <= end; i++)
                    _selected.Add(visibleOrder[i]);
            }
            else
            {
                _selected.Add(step);
            }
            Anchor = step;
            RaiseChanged();
        }

        /// <summary>
        /// Replaces the selection with the given steps.
        /// </summary>
        /// <param name="steps">Steps to select, in order. Null entries are skipped.</param>
        /// <param name="primary">
        /// Optional step to force as primary; it is moved to the end of the list when present
        /// in <paramref name="steps"/>.
        /// </param>
        public void SelectMany(IEnumerable<Step> steps, Step primary = null)
        {
            _selected.Clear();
            if (steps != null)
            {
                foreach (var s in steps)
                {
                    if (s != null && !_selected.Contains(s)) _selected.Add(s);
                }
            }
            if (primary != null && _selected.Remove(primary))
            {
                _selected.Add(primary);
            }
            Anchor = Primary;
            RaiseChanged();
        }

        /// <summary>Clears the selection and the anchor.</summary>
        public void Clear()
        {
            if (_selected.Count == 0 && Anchor == null) return;
            _selected.Clear();
            Anchor = null;
            RaiseChanged();
        }

        /// <summary>
        /// Substitutes <paramref name="newStep"/> for <paramref name="oldStep"/> in place
        /// (used when a step component is recreated, e.g. change-type). When the old step is
        /// not selected, the selection is replaced by the new step.
        /// </summary>
        public void Replace(Step oldStep, Step newStep)
        {
            if (newStep == null) return;
            int idx = _selected.IndexOf(oldStep);
            if (idx >= 0)
                _selected[idx] = newStep;
            else
            {
                _selected.Clear();
                _selected.Add(newStep);
            }
            if (Anchor == oldStep || Anchor == null) Anchor = newStep;
            RaiseChanged();
        }

        /// <summary>
        /// Removes <paramref name="root"/> and every selected step under its transform
        /// (call before the step's GameObject is destroyed).
        /// </summary>
        public void RemoveWithDescendants(Step root)
        {
            if (root == null) return;
            int removed = _selected.RemoveAll(s => s == root || (s != null && s.transform.IsChildOf(root.transform)));
            if (removed == 0) return;
            if (Anchor == root || (Anchor != null && Anchor.transform.IsChildOf(root.transform)))
                Anchor = Primary;
            RaiseChanged();
        }

        /// <summary>Drops destroyed (Unity fake-null) entries from the selection.</summary>
        public void PruneDestroyed()
        {
            int removed = _selected.RemoveAll(s => s == null);
            if (Anchor == null) Anchor = Primary;
            if (removed > 0) RaiseChanged();
        }

        /// <summary>
        /// Flags that the owning view is about to push this selection into Unity's
        /// <c>Selection</c>, so the resulting selection-changed callback must not
        /// overwrite the model (preserves order and multi-select).
        /// </summary>
        public void MarkSyncingToUnity() => _syncingToUnity = true;

        /// <summary>
        /// Consumes the round-trip guard set by <see cref="MarkSyncingToUnity"/>.
        /// Returns <c>true</c> exactly once per mark; the caller should skip
        /// reconciliation for that callback.
        /// </summary>
        public bool TryConsumeUnitySyncGuard()
        {
            if (!_syncingToUnity) return false;
            _syncingToUnity = false;
            return true;
        }

        /// <summary>
        /// Adopts a selection that originated in Unity (hierarchy click, etc.).
        /// </summary>
        /// <param name="steps">Steps resolved from Unity's selected objects, already filtered to the relevant controller.</param>
        /// <param name="activeStep">The step on Unity's active GameObject, if any; it becomes primary.</param>
        /// <returns><c>true</c> if the selection was applied; <c>false</c> when <paramref name="steps"/> is null or empty (model unchanged).</returns>
        public bool ReconcileFromUnity(IReadOnlyList<Step> steps, Step activeStep)
        {
            if (steps == null || steps.Count == 0) return false;
            _selected.Clear();
            if (activeStep != null && steps.Count > 1)
            {
                foreach (var s in steps)
                {
                    if (s != null && s != activeStep && !_selected.Contains(s)) _selected.Add(s);
                }
                _selected.Add(activeStep);
            }
            else
            {
                foreach (var s in steps)
                {
                    if (s != null && !_selected.Contains(s)) _selected.Add(s);
                }
            }
            Anchor = Primary;
            RaiseChanged();
            return true;
        }

        private static int IndexOf(IReadOnlyList<Step> list, Step step)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == step) return i;
            }
            return -1;
        }

        private void RaiseChanged() => SelectionChanged?.Invoke();
    }
}
