using UnityEngine;

namespace Molca.Sequence
{
    public class DelayStep : Step
    {
        [SerializeField] private float delayInSeconds = 1f;
        private float elapsedTime = 0f;
        private bool isPaused = false;

        protected override void OnStepActivated()
        {
            elapsedTime = 0f;
            isPaused = false;
        }

        public override void UpdateStep()
        {
            base.UpdateStep();
            
            // Only count time if not paused and step is active
            if (!isPaused && CurrentStatus == StepStatus.Active)
            {
                elapsedTime += Time.deltaTime;
                
                if (elapsedTime >= delayInSeconds)
                {
                    Complete();
                }
            }
        }

        protected override void OnStepPaused()
        {
            isPaused = true;
        }

        protected override void OnStepResumed()
        {
            isPaused = false;
        }

        protected override void OnStepDeactivated()
        {
            elapsedTime = 0f;
            isPaused = false;
        }
    }
} 