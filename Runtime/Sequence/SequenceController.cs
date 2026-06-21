using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Events;
using Molca.Events;
using Molca.ReferenceSystem;
using Molca.Attributes;
using Molca.Telemetry;

namespace Molca.Sequence
{
    public enum SequenceState
    {
        Idle = 0,
        Running = 1,
        Paused = 2,
        Completed = 3
    }

    public class SequenceController : MonoBehaviour, IReferenceable<SequenceController>
    {
        [SerializeField, RefId] private string sequenceId;
        
        [Header("Initial Setup")]
        [SerializeField] private bool autoStart = false;
        [SerializeField] private float autoStartDelay = 0f;
        
        [Header("Sequence Events")]
        public UnityEvent OnSequenceStart;
        public UnityEvent OnSequenceFinish;
        public UnityEvent OnSequencePause;
        public UnityEvent OnSequenceResume;
        public UnityEvent<Step> OnStepChanged;
        
        // Dependencies
        [Inject] private EventDispatcher _eventDispatcher;
        [Inject] private ReferenceManager _referenceManager;

        // Optional: telemetry is emitted only when a TelemetrySubsystem is present and enabled.
        // Marked non-required so sequences run unchanged in projects without telemetry.
        [Inject(false)] private TelemetrySubsystem _telemetry;

        public string RefId { get => sequenceId; set => sequenceId = value; }
        public string RefType => "SequenceController";
        public string DisplayName => name;
        public bool AutoStart { get => autoStart; set => autoStart = value; }
        public float AutoStartDelay { get => autoStartDelay; set => autoStartDelay = value; }

        public Step CurrentStep { get; private set; }
        private List<Step> _steps;
        public IReadOnlyList<Step> Steps => _steps;
        
        private SequenceState _currentState = SequenceState.Idle;
        public SequenceState CurrentState => _currentState;
        
        public DateTime SequenceStartTime { get; private set; }
        public DateTime SequenceFinishTime { get; private set; }
        public TimeSpan TimeTaken => SequenceFinishTime - SequenceStartTime;
        
        public bool IsRunning => _currentState == SequenceState.Running;
        public bool IsPaused => _currentState == SequenceState.Paused;
        public bool IsActive => _currentState == SequenceState.Running || _currentState == SequenceState.Paused;


        private void OnValidate()
        {
            // Ensure sequenceId is not empty
            if (string.IsNullOrEmpty(sequenceId))
            {
                sequenceId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#if UNITY_EDITOR
            // A prefab instance inherits the asset's id; give each placement a fresh one.
            else if (ReferenceGenerator.IsInheritedPrefabId(this, sequenceId))
            {
                sequenceId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#endif
        }

        private void Update()
        {
            // Only update the current step when the sequence is running (not paused)
            if (_currentState == SequenceState.Running && CurrentStep != null)
            {
                CurrentStep.UpdateStep();
            }
        }

        private void OnDestroy()
        {
            // Unregister event listeners
            if (_eventDispatcher != null)
            {
                _eventDispatcher.UnregisterEvent<Step>("Step.Completed", OnStepCompleted);
                _eventDispatcher.UnregisterEvent<Step>("Step.FullyCompleted", OnStepFullyCompleted);
            }

            if (_referenceManager != null)
            {
                _referenceManager.Unregister(this);
            }
        }

        private async void Start()
        {
            await RuntimeManager.WaitForInitialization();
            
            // Manual injection if needed (auto-injection happens after RuntimeManager.IsReady)
            if (_eventDispatcher == null || _referenceManager == null)
            {
                RuntimeManager.InjectDependencies(this);
            }
            
            if (_referenceManager != null)
            {
                _referenceManager.Register(this);
            }
            InitializeSequence();
            
            if (autoStart)
            {
                await Awaitable.WaitForSecondsAsync(autoStartDelay);
                StartSequence();
            }
        }

        private void InitializeSequence()
        {
            // Collect all active Step components from children (includes sub-steps of ParallelStep).
            // Sub-steps are skipped in sequential logic (advancement, next step) but remain in _steps for full listing.
            _steps = GetComponentsInChildren<Step>(true)
                .Where(s => s.gameObject.activeInHierarchy && s.enabled)
                .ToList();
            
            // Initialize each step
            foreach (var step in _steps)
            {
                step.Initialize(sequenceId);
            }
            
            // Register for step completion events
            if (_eventDispatcher != null)
            {
                _eventDispatcher.RegisterEvent<Step>("Step.Completed", OnStepCompleted);
                _eventDispatcher.RegisterEvent<Step>("Step.FullyCompleted", OnStepFullyCompleted);
            }
        }

        public async void StartSequence()
        {
            await RuntimeManager.WaitForInitialization();

            // Steps are populated by InitializeSequence during Start; bound the wait so a
            // controller with no steps logs the warning instead of polling forever, and
            // stop polling if this object is destroyed in the meantime.
            const float stepsWaitTimeoutSeconds = 5f;
            float waited = 0f;
            try
            {
                while ((_steps == null || !_steps.Any()) && waited < stepsWaitTimeoutSeconds)
                {
                    await Awaitable.WaitForSecondsAsync(.2f, destroyCancellationToken);
                    waited += .2f;
                }
            }
            catch (OperationCanceledException)
            {
                return; // destroyed while waiting
            }

            if (_steps == null || !_steps.Any())
            {
                Debug.LogWarning("No active Step components found in children. Sequence will not run.", this);
                return;
            }

            SequenceStartTime = DateTime.Now;
            _currentState = SequenceState.Running;

            Debug.Log($"Starting sequence: {RefId}", this);
            OnSequenceStart?.Invoke();
            TrackSequence("sequence.started");

            // Reset all steps before starting
            foreach (var step in _steps)
            {
                step.ResetStep();
            }

            CurrentStep = null;
            AdvanceToNextStep();
        }

        /// <summary>
        /// Marks the current step as complete and advances to the next one.
        /// Call this from a UnityEvent when a user action fulfills the step's requirement.
        /// </summary>
        public void CompleteCurrentStep()
        {
            if (CurrentStep != null && !CurrentStep.IsCompleted)
            {
                Debug.Log($"Completing step via manager: {CurrentStep.name}", CurrentStep);
                CurrentStep.Complete();
            }
        }

        /// <summary>
        /// Event handler for when a step is internally completed
        /// </summary>
        private void OnStepCompleted(Step completedStep)
        {
            if (_currentState != SequenceState.Running) return;
            if (!_steps.Contains(completedStep)) return;
            // Sub-steps of ParallelStep are completed by the parent; skip advancement for them
            if (IsSubStepOfParallelStep(completedStep))
                return;

            // Always check for advancement when any step completes
            CheckStepAdvancement();

            // Always update parent status chain when any step completes
            UpdateParentStatusChain(completedStep);
        }

        /// <summary>
        /// Updates the status of all parent steps in the chain when a step completes
        /// </summary>
        private void UpdateParentStatusChain(Step step)
        {
            var parent = step.Parent;
            while (parent != null)
            {
                if (parent.IsInternallyCompleted && parent.IsCompleted && parent.CurrentStatus != StepStatus.Completed)
                {
                    parent.SetStatus(StepStatus.Completed);
                }
                parent = parent.Parent;
            }
        }

        /// <summary>
        /// Event handler for when a step is fully completed
        /// </summary>
        private void OnStepFullyCompleted(Step completedStep)
        {
            if (_currentState != SequenceState.Running) return;
            
            // Only respond to completion of steps that belong to this sequence
            if (!_steps.Contains(completedStep)) return;
            
            // Only respond to completion of the current step
            if (completedStep == CurrentStep)
            {
                CheckStepAdvancement();
            }
        }

        /// <summary>
        /// Checks if we should advance to the next step and does so if needed
        /// </summary>
        public void CheckStepAdvancement()
        {
            if (_currentState != SequenceState.Running) return;

            // If the current step is marked as complete, advance to the next step
            if (CurrentStep != null && CurrentStep.IsCompleted)
            {
                AdvanceToNextStep();
            }
            // Check if we should advance from internally completed parent to children
            else if (CurrentStep != null && ShouldAdvanceFromParentToChildren())
            {
                AdvanceToNextStep();
            }
            
            // Update parent step statuses and check if any parents are now fully completed
            if (CurrentStep != null)
            {
                EnsureActiveParentsRemainActive();
                CheckParentCompletion();
            }
        }

        /// <summary>
        /// Checks if any parent steps are now fully completed and updates their status
        /// </summary>
        private void CheckParentCompletion()
        {
            if (CurrentStep == null) return;
            
            var parent = CurrentStep.Parent;
            while (parent != null)
            {
                // If parent is internally completed and all children are completed, mark parent as completed
                if (parent.IsInternallyCompleted && parent.IsCompleted && parent.CurrentStatus != StepStatus.Completed)
                {
                    parent.SetStatus(StepStatus.Completed);
                }
                parent = parent.Parent;
            }
        }

        private void AdvanceToNextStep()
        {
            if (_currentState != SequenceState.Running) return;

            // Find the next active step
            var nextStep = FindNextActiveStep();
            
            if (nextStep != null)
            {
                // End the current step if it exists
                if (CurrentStep != null)
                {
                    var newStatus = CurrentStep.IsCompleted ? StepStatus.Completed : StepStatus.Inactive;
                    CurrentStep.SetStatus(newStatus);
                    TrackStepCompleted(CurrentStep);
                }

                // Start the new step
                CurrentStep = nextStep;
                CurrentStep.SetStatus(StepStatus.Active);
                TrackStepStarted(CurrentStep);

                OnStepChanged?.Invoke(CurrentStep);
                
                Debug.Log($"Advanced to step: {CurrentStep.name}", CurrentStep);
                
                // If the next step is already internally completed, advance again
                if (CurrentStep.IsInternallyCompleted)
                {
                    AdvanceToNextStep();
                }
            }
            else
            {
                // No more steps, finish the sequence
                FinishSequence();
            }
        }

        private Step FindNextActiveStep()
        {
            // If we don't have a current step, start with the first one (skip sub-steps of ParallelStep)
            if (CurrentStep == null)
            {
                return GetActiveSteps().FirstOrDefault(s => !IsSubStepOfParallelStep(s));
            }

            // Find the next step in the hierarchy
            return FindNextStepInHierarchy(CurrentStep);
        }

        private Step FindNextStepInHierarchy(Step step)
        {
            // If the current step is internally completed and has children, advance to the first incomplete child
            // Do not descend into ParallelStep's children; the controller only tracks the ParallelStep as current
            if (step.IsInternallyCompleted && step.HasIncompleteChildren() && !(step is ParallelStep))
            {
                var firstIncompleteChild = step.Children.FirstOrDefault(child => 
                    child.gameObject.activeInHierarchy && child.enabled && !child.IsCompleted);
                if (firstIncompleteChild != null)
                {
                    return firstIncompleteChild;
                }
            }

            // First, try to find the next sibling
            var parent = step.Parent;
            if (parent != null)
            {
                var siblings = parent.Children;
                var currentIndex = siblings.Select((s, i) => new { Step = s, Index = i })
                                         .FirstOrDefault(x => x.Step == step)?.Index ?? -1;
                
                if (currentIndex >= 0 && currentIndex < siblings.Count - 1)
                {
                    // Find the next active sibling (skip sub-steps of ParallelStep)
                    for (int i = currentIndex + 1; i < siblings.Count; i++)
                    {
                        if (siblings[i].gameObject.activeInHierarchy && siblings[i].enabled
                            && !IsSubStepOfParallelStep(siblings[i]))
                        {
                            return siblings[i];
                        }
                    }
                }
            }
            
            // If no next sibling, try to find the next step in the flat list (skip sub-steps of ParallelStep)
            var activeSteps = GetActiveSteps();
            var currentIndexInSteps = activeSteps.IndexOf(step);
            if (currentIndexInSteps >= 0 && currentIndexInSteps < activeSteps.Count - 1)
            {
                for (int i = currentIndexInSteps + 1; i < activeSteps.Count; i++)
                {
                    var candidate = activeSteps[i];
                    if (!IsSubStepOfParallelStep(candidate))
                        return candidate;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Returns true if the step is a direct child of a ParallelStep (a sub-step managed by that parent).
        /// Sub-steps are included in _steps but skipped in all sequential logic (advancement, next step).
        /// </summary>
        private static bool IsSubStepOfParallelStep(Step step)
        {
            return step?.Parent is ParallelStep;
        }

        /// <summary>
        /// Gets all currently active and enabled steps
        /// </summary>
        private List<Step> GetActiveSteps()
        {
            return _steps.Where(s => s.gameObject.activeInHierarchy && s.enabled).ToList();
        }

        private void EnsureActiveParentsRemainActive()
        {
            if (CurrentStep == null) return;
            
            // Ensure all parent steps remain active
            var parent = CurrentStep.Parent;
            while (parent != null)
            {
                if (parent.CurrentStatus != StepStatus.Active)
                {
                    parent.SetStatus(StepStatus.Active);
                }
                parent = parent.Parent;
            }
        }

        private bool ShouldAdvanceFromParentToChildren()
        {
            // If the current step is internally completed but has incomplete children,
            // we should advance to the first incomplete child
            return CurrentStep.IsInternallyCompleted && CurrentStep.HasIncompleteChildren();
        }

        private void FinishSequence()
        {
            if (_currentState != SequenceState.Running) return;
            
            _currentState = SequenceState.Completed;
            SequenceFinishTime = DateTime.Now;
            
            Debug.Log($"Finished sequence: {RefId} in {TimeTaken.TotalSeconds:F2} seconds", this);
            OnSequenceFinish?.Invoke();

            // Reset current step
            if (CurrentStep != null)
            {
                var newStatus = CurrentStep.IsCompleted ? StepStatus.Completed : StepStatus.Inactive;
                CurrentStep.SetStatus(newStatus);
                TrackStepCompleted(CurrentStep);
                CurrentStep = null;
            }

            TrackSequence("sequence.completed", TimeTaken.TotalSeconds);
        }

        public void StopSequence()
        {
            if (_currentState == SequenceState.Idle) return;
            
            _currentState = SequenceState.Idle;
            
            if (CurrentStep != null)
            {
                CurrentStep.SetStatus(StepStatus.Inactive);
                CurrentStep = null;
            }
            
            Debug.Log($"Stopped sequence: {RefId}", this);
        }

        public void PauseSequence()
        {
            if (_currentState != SequenceState.Running) return;
            
            _currentState = SequenceState.Paused;
            
            Debug.Log($"Paused sequence: {RefId}", this);
            OnSequencePause?.Invoke();
            
            // Notify current step about pause
            if (CurrentStep != null)
            {
                CurrentStep.NotifyPause();
            }
        }

        public void ResumeSequence()
        {
            if (_currentState != SequenceState.Paused) return;
            
            _currentState = SequenceState.Running;
            
            Debug.Log($"Resumed sequence: {RefId}", this);
            OnSequenceResume?.Invoke();

            // Notify current step about resume
            if (CurrentStep != null)
            {
                CurrentStep.NotifyResume();
            }

            // Steps can complete while paused (OnStepCompleted ignores events when
            // not Running), so re-check advancement or the sequence stalls forever.
            CheckStepAdvancement();
        }

        public void RestartSequence()
        {
            StopSequence();
            StartSequence();
        }

        #region Telemetry Emission

        // All emitters are no-ops when no TelemetrySubsystem is injected (telemetry absent/disabled).

        private void TrackSequence(string eventName, double? durationSeconds = null)
        {
            if (_telemetry == null) return;
            var props = new Dictionary<string, object>
            {
                { "sequenceId", RefId },
                { "sequenceName", DisplayName },
            };
            if (durationSeconds.HasValue) props["durationSeconds"] = durationSeconds.Value;
            _telemetry.Track(eventName, props);
        }

        private void TrackStepStarted(Step step)
        {
            if (_telemetry == null || step == null) return;
            _telemetry.Track("sequence.step_started", new Dictionary<string, object>
            {
                { "sequenceId", RefId },
                { "stepRefId", step.RefId },
                { "stepName", step.DisplayName },
                { "stepId", step.StepId },
            });
        }

        private void TrackStepCompleted(Step step)
        {
            if (_telemetry == null || step == null) return;
            _telemetry.Track("sequence.step_completed", new Dictionary<string, object>
            {
                { "sequenceId", RefId },
                { "stepRefId", step.RefId },
                { "stepName", step.DisplayName },
                { "stepId", step.StepId },
                { "completed", step.IsCompleted },
                { "durationSeconds", (step.EndTime - step.StartTime).TotalSeconds },
            });
        }

        #endregion
    }
} 