using System;
using System.Collections.Generic;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Event-driven change tracking for one <see cref="SequenceController"/>, shared by
    /// sequence views (visualizer tree, graph editor) so they refresh only when something
    /// actually changed instead of polling every repaint.
    /// </summary>
    /// <remarks>
    /// Three signals:
    /// <list type="bullet">
    /// <item><see cref="StepStatusChanged"/> — a tracked step's <see cref="Step.OnStatusChanged"/>
    /// fired (play mode); views invalidate that step only.</item>
    /// <item><see cref="StepListChanged"/> — the controller's runtime step list was (re)built
    /// or changed size (play mode).</item>
    /// <item><see cref="HierarchyChanged"/> — an edit-mode scene-hierarchy change affected the
    /// controller's subtree; changes elsewhere in the scene are ignored.</item>
    /// </list>
    /// Call <see cref="Dispose"/> (e.g. from <c>OnDisable</c>) to release editor callbacks.
    /// </remarks>
    public sealed class SequenceChangeTracker : IDisposable
    {
        private SequenceController _controller;
        private readonly List<Step> _attachedSteps = new List<Step>();
        private readonly Dictionary<Step, Action<StepStatus>> _handlers = new Dictionary<Step, Action<StepStatus>>();
        private List<Step> _knownEditorSteps;
        private int _lastRuntimeStepCount = -1;
        private bool _disposed;

        /// <summary>A tracked step changed status. Argument is the step (play mode only).</summary>
        public event Action<Step> StepStatusChanged;

        /// <summary>The controller's runtime step list appeared or changed size (play mode only).</summary>
        public event Action StepListChanged;

        /// <summary>An edit-mode hierarchy change touched the controller's subtree.</summary>
        public event Action HierarchyChanged;

        public SequenceChangeTracker()
        {
            EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Switches tracking to <paramref name="controller"/> (or stops tracking when <c>null</c>).
        /// Detaches from the previous controller's steps.
        /// </summary>
        public void SetController(SequenceController controller)
        {
            if (_controller == controller) return;
            DetachSteps();
            _controller = controller;
            _lastRuntimeStepCount = -1;
            _knownEditorSteps = null;
        }

        /// <summary>
        /// Records the edit-mode step list a view is currently displaying, used to scope
        /// <see cref="HierarchyChanged"/> to relevant changes. Pass the result of
        /// <see cref="StepHierarchyBuilder.BuildHierarchy"/> after each rebuild.
        /// </summary>
        public void SetKnownEditorSteps(List<Step> steps) => _knownEditorSteps = steps;

        /// <summary>
        /// Subscribes to <see cref="Step.OnStatusChanged"/> on every step in the list,
        /// replacing any previous subscriptions. Call when the runtime list (re)builds.
        /// </summary>
        public void AttachSteps(IReadOnlyList<Step> steps)
        {
            DetachSteps();
            if (steps == null) return;
            foreach (var step in steps)
            {
                if (step == null || _handlers.ContainsKey(step)) continue;
                var captured = step;
                Action<StepStatus> handler = _ => StepStatusChanged?.Invoke(captured);
                captured.OnStatusChanged += handler;
                _handlers[captured] = handler;
                _attachedSteps.Add(captured);
            }
            _lastRuntimeStepCount = steps.Count;
        }

        /// <summary>Unsubscribes from all tracked steps.</summary>
        public void DetachSteps()
        {
            foreach (var step in _attachedSteps)
            {
                if (step != null && _handlers.TryGetValue(step, out var handler))
                {
                    step.OnStatusChanged -= handler;
                }
            }
            _attachedSteps.Clear();
            _handlers.Clear();
        }

        /// <summary>Releases editor callbacks and step subscriptions.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
            EditorApplication.update -= OnEditorUpdate;
            DetachSteps();
        }

        private void OnEditorUpdate()
        {
            // Play mode only: detect the runtime step list being built or resized.
            // This is a cheap int compare per editor tick — the actual status changes
            // arrive through OnStatusChanged events, not polling.
            if (!Application.isPlaying || _controller == null) return;
            int count = _controller.Steps?.Count ?? -1;
            if (count == _lastRuntimeStepCount) return;
            _lastRuntimeStepCount = count;
            StepListChanged?.Invoke();
        }

        private void OnEditorHierarchyChanged()
        {
            if (Application.isPlaying || _controller == null) return;
            // Scope to the tracked controller: ignore scene changes that neither add/remove
            // steps under it nor alter their parent relationships.
            if (_knownEditorSteps != null &&
                !StepHierarchyBuilder.NeedsHierarchyRebuild(_controller, _knownEditorSteps))
            {
                return;
            }
            HierarchyChanged?.Invoke();
        }
    }
}
