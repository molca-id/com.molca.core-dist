using UnityEngine;

namespace Molca.Sequence
{
    public class ConditionalStep : Step
    {
        [Header("Conditional Branching")]
        [SerializeField] private string conditionDescription;
        [SerializeField] private Step trueStep;  // Step to execute if condition is true
        [SerializeField] private Step falseStep; // Step to execute if condition is false
        
        private bool condition;
        private bool conditionEvaluated = false;

        public override void Initialize(string sequenceId)
        {
            base.Initialize(sequenceId);
            if (trueStep != null) trueStep.gameObject.SetActive(false);
            if (falseStep != null) falseStep.gameObject.SetActive(false);
        }

        /// <summary>
        /// Sets the condition and evaluates the branch
        /// </summary>
        /// <param name="condition">True to execute trueStep, false to execute falseStep</param>
        public void SetCondition(bool condition)
        {
            this.condition = condition;
            EvaluateCondition();
        }

        /// <summary>
        /// Evaluates the condition and enables/disables the appropriate child steps
        /// </summary>
        private void EvaluateCondition()
        {
            if (conditionEvaluated) return;
            
            conditionEvaluated = true;
            
            // Enable the appropriate path and disable the other
            if (trueStep != null) trueStep.gameObject.SetActive(condition);
            if (falseStep != null) falseStep.gameObject.SetActive(!condition);
            
            // Mark this conditional step as internally completed
            Complete();
        }

        protected override void OnStepActivated()
        {
            // NOTE: no CurrentStatus guard here — SetStatus() assigns the new status
            // BEFORE calling OnStepActivated, so checking for Active would always
            // early-return and re-activation would never reset the branch state
            // (same fix as BranchingStep).

            // Reset the condition evaluation when the step becomes active
            conditionEvaluated = false;
            
            // Re-enable both paths initially
            if (trueStep != null) trueStep.gameObject.SetActive(true);
            if (falseStep != null) falseStep.gameObject.SetActive(true);
        }

        protected override bool CanComplete()
        {
            // Can complete once the condition has been evaluated
            return conditionEvaluated;
        }
    }
} 