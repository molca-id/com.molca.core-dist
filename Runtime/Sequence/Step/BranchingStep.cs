using UnityEngine;

namespace Molca.Sequence
{
    public class BranchingStep : Step
    {
        [Header("Branching Options")]
        [SerializeField] private string branchDescription;
        [SerializeField] private Step[] branchSteps; // Array of possible branch steps
        
        private int selectedBranchIndex = -1;
        private bool branchEvaluated = false;

        public override void Initialize(string sequenceId)
        {
            base.Initialize(sequenceId);
            
            // Initially disable all branch steps
            if (branchSteps != null)
            {
                foreach (var step in branchSteps)
                {
                    if (step != null)
                    {
                        step.gameObject.SetActive(false);
                    }
                }
            }
        }

        /// <summary>
        /// Sets the branch index and evaluates the branch
        /// </summary>
        /// <param name="branchIndex">Index of the step to activate (0-based). Must be within range of branchSteps array.</param>
        public void SetBranch(int branchIndex)
        {
            if (branchSteps == null || branchIndex < 0 || branchIndex >= branchSteps.Length)
            {
                Debug.LogError($"BranchingStep '{gameObject.name}': Invalid branch index {branchIndex}. Valid range is 0-{branchSteps?.Length - 1 ?? 0}", this);
                return;
            }
            
            this.selectedBranchIndex = branchIndex;
            EvaluateBranch();
        }

        /// <summary>
        /// Evaluates the branch and enables/disables the appropriate child steps
        /// </summary>
        private void EvaluateBranch()
        {
            if (branchEvaluated) return;
            
            branchEvaluated = true;
            
            // Disable all branch steps first
            if (branchSteps != null)
            {
                for (int i = 0; i < branchSteps.Length; i++)
                {
                    if (branchSteps[i] != null)
                    {
                        branchSteps[i].gameObject.SetActive(i == selectedBranchIndex);
                    }
                }
            }
            
            // Mark this branching step as internally completed
            Complete();
        }

        protected override void OnStepActivated()
        {
            // Note: Step.SetStatus assigns CurrentStatus before calling this hook,
            // so guarding on CurrentStatus == Active here would always bail out and
            // the reset below would never run (re-activation kept the stale branch).

            // Reset the branch evaluation when the step becomes active
            branchEvaluated = false;
            selectedBranchIndex = -1;
            
            // Re-enable all paths initially
            if (branchSteps != null)
            {
                foreach (var step in branchSteps)
                {
                    if (step != null)
                    {
                        step.gameObject.SetActive(true);
                    }
                }
            }
        }

        protected override bool CanComplete()
        {
            // Can complete once the branch has been evaluated
            return branchEvaluated;
        }

        /// <summary>
        /// Gets the currently selected branch index
        /// </summary>
        public int GetSelectedBranchIndex()
        {
            return selectedBranchIndex;
        }

        /// <summary>
        /// Gets the number of available branches
        /// </summary>
        public int GetBranchCount()
        {
            return branchSteps?.Length ?? 0;
        }

        /// <summary>
        /// Gets the step at the specified branch index
        /// </summary>
        public Step GetBranchStep(int index)
        {
            if (branchSteps == null || index < 0 || index >= branchSteps.Length)
            {
                return null;
            }
            return branchSteps[index];
        }
    }
} 