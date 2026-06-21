using System.Linq;
using UnityEngine;
using Molca.Attributes;

namespace Molca.Sequence
{
    /// <summary>
    /// A step that can activate all direct child steps together or on demand. Sub-steps can be completed in any order.
    /// The ParallelStep completes automatically when all sub-steps are completed.
    /// Sub-steps are untracked by SequenceController; only this step is the current step.
    /// </summary>
    public class ParallelStep : Step
    {
        [InfoBox("This step is a parent step that manages direct child steps. Sub-steps can be completed in any order. The ParallelStep completes automatically when all sub-steps are completed. Sub-steps are untracked by SequenceController; only this step is the current step.")]
        [SerializeField] private bool autoComplete = true;

        [Tooltip("When true, all active child steps become Active as soon as this step becomes Active. When false, call ActivateAllChildren or ActivateChild from code or UnityEvents while this step is Active.")]
        [SerializeField] private bool activateChildrenOnBegin = true;

        public override void Initialize(string sequenceId)
        {
            base.Initialize(sequenceId);

            if (_eventDispatcher != null)
            {
                _eventDispatcher.RegisterEvent<Step>("Step.Completed", OnChildStepCompleted);
            }
        }

        protected override void OnDestroy()
        {
            if (_eventDispatcher != null)
            {
                _eventDispatcher.UnregisterEvent<Step>("Step.Completed", OnChildStepCompleted);
            }
            base.OnDestroy();
        }

        protected override void OnStepCompleted()
        {
            base.OnStepCompleted();
            foreach (var child in Children)
            {
                if (child != null)
                {
                    child.Complete();
                }
            }
        }

        private void OnChildStepCompleted(Step completedStep)
        {
            if (Children == null || !Children.Contains(completedStep))
                return;

            // Sub-steps are untracked by the controller, so they never get SetStatus(Completed) from it.
            // SetStatus already fires OnStepEnd for an Active -> Completed transition;
            // invoking it manually here fired the event twice per sub-step.
            completedStep.SetStatus(StepStatus.Completed);

            if ((autoComplete || IsInternallyCompleted) && AreAllChildrenCompleted())
                Complete();
        }

        /// <summary>
        /// Activates every eligible direct child step. Only has effect while this ParallelStep is Active.
        /// Skips children that are already Active or Completed, or inactive/disabled in the hierarchy.
        /// </summary>
        public void ActivateAllChildren()
        {
            if (CurrentStatus != StepStatus.Active || Children == null)
                return;

            foreach (var child in Children)
                TryActivateChildStep(child);
        }

        /// <summary>
        /// Activates one direct child step. Only has effect while this ParallelStep is Active and the step is a direct child.
        /// </summary>
        public void ActivateChild(Step child)
        {
            if (CurrentStatus != StepStatus.Active || Children == null || child == null || !Children.Contains(child))
                return;

            TryActivateChildStep(child);
        }

        private static void TryActivateChildStep(Step child)
        {
            if (child == null || child.IsCompleted || child.CurrentStatus == StepStatus.Active)
                return;
            if (!child.gameObject.activeInHierarchy || !child.enabled)
                return;

            child.SetStatus(StepStatus.Active);
        }

        private bool AreAllChildrenCompleted()
        {
            return Children == null || !Children.Any() ||
                Children.All(child => child.IsCompleted || !child.gameObject.activeInHierarchy);
        }

        public override void SetStatus(StepStatus status)
        {
            base.SetStatus(status);

            if (Children == null)
                return;

            if (status == StepStatus.Active && activateChildrenOnBegin)
            {
                foreach (var child in Children)
                    TryActivateChildStep(child);
            }
            else if (status == StepStatus.Inactive || status == StepStatus.Completed)
            {
                foreach (var child in Children)
                {
                    if (child != null)
                    {
                        // SetStatus fires OnStepEnd itself when leaving Active.
                        var childStatus = child.IsCompleted ? StepStatus.Completed : StepStatus.Inactive;
                        child.SetStatus(childStatus);
                    }
                }
            }
        }

        public override void UpdateStep()
        {
            base.UpdateStep();

            if (Children == null)
                return;

            foreach (var child in Children)
            {
                if (child != null && child.gameObject.activeInHierarchy && child.enabled)
                {
                    child.UpdateStep();
                }
            }
        }
    }
}
