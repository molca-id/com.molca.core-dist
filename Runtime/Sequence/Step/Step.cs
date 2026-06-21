using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Events;
using Molca.Audio;
using Molca.Sequence.Auxiliary;
using Molca.ReferenceSystem;
using Molca.Attributes;
using UnityEngine.Serialization;

namespace Molca.Sequence
{
    public enum StepStatus
    {
        Inactive = 0,
        Active = 1,
        Completed = 2
    }
    
    /// <summary>
    /// Represents a single step in a sequence. It is complete only when its own conditions and all child steps are met.
    /// </summary>
    public class Step : MonoBehaviour, IReferenceable<Step>
    {
        [SerializeField, RefId] private string refId;

        [SerializeField] private int stepId;
        
        [SerializeReference] private List<StepAuxiliary> auxiliaries = new List<StepAuxiliary>();
        
        // Dependencies
        [Inject] protected EventDispatcher _eventDispatcher;
        [Inject] protected ReferenceManager _referenceManager;
        
        /// <summary>
        /// All auxiliaries attached to this step.
        /// </summary>
        public IReadOnlyList<StepAuxiliary> Auxiliaries => auxiliaries;
        
        /// <summary>
        /// The internal completion state of this specific step. A parent step is only fully complete when this is true AND all children are complete.
        /// </summary>
        private bool _isInternallyCompleted = false;
        
        /// <summary>
        /// Returns true if this step's internal condition is met, regardless of children.
        /// </summary>
        public bool IsInternallyCompleted => _isInternallyCompleted;
        
        /// <summary>
        /// A step is fully completed only when its own internal condition is met AND all of its direct children are also completed.
        /// </summary>
        public bool IsCompleted => _isInternallyCompleted && AreAllChildrenCompleted();

        public string RefId { get => refId; set => refId = value; }
        public string RefType => "Step";
        public string DisplayName => name;
public int StepId => stepId;

        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }

        [Header("Events")]
        [Tooltip("Fired when the SequenceController begins this step.")]
        public UnityEvent OnStepBegin;

        [Tooltip("Fired when the SequenceController concludes this step (either by completion or cancellation).")]
        public UnityEvent OnStepEnd;
        
        /// <summary>
        /// Fires whenever the runtime status of the step changes.
        /// </summary>
        public event Action<StepStatus> OnStatusChanged;

        /// <summary>
        /// The runtime status of the step, controlled by the SequenceController. Used for editor visuals.
        /// </summary>
        private StepStatus _currentStatus = StepStatus.Inactive;
        public StepStatus CurrentStatus
        {
            get => _currentStatus;
            set
            {
                if (_currentStatus == value) return;
                _currentStatus = value;
                OnStatusChanged?.Invoke(_currentStatus);
            }
        }
        
        private List<Step> _childrenSteps;
        public IReadOnlyList<Step> Children => _childrenSteps;

        private SequenceController _sequenceController;
        public SequenceController SequenceController
        {
            get
            {
                if (_sequenceController == null)
                {
                    _sequenceController = GetComponentInParent<SequenceController>();
                }
                return _sequenceController;
            }
        }
        
        private Step _parent;
        private bool _parentSearched = false;

        public Step Parent
        {
            get
            {
                if (!_parentSearched)
                {
                    if (transform.parent != null)
                    {
                        // Find the closest Step in parent transforms.
                        _parent = transform.parent.GetComponentInParent<Step>();
                    }
                    _parentSearched = true;
                }
                return _parent;
            }
            set
            {
                _parent = value;
                _parentSearched = true;
            }
        }

        private void OnValidate()
        {
            // Ensure stepId is not empty
            if (string.IsNullOrEmpty(refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#if UNITY_EDITOR
            // A prefab instance inherits the asset's id; give each placement a fresh one.
            else if (ReferenceGenerator.IsInheritedPrefabId(this, refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#endif

            // Validate auxiliaries and remove invalid ones
            for (int i = auxiliaries.Count - 1; i >= 0; i--)
            {
                var auxiliary = auxiliaries[i];
                if (auxiliary == null)
                {
                    // Don't remove invalid auxiliaries automatically.
                    // The StepEditor will provide an option to remove them manually.
                    continue;
                }

                auxiliary.BindOwnerFromStep(this);
                // Don't set _isInitialized here — that happens in Initialize() at runtime.
            }
        }

        /// <summary>
        /// Ensures every auxiliary has <see cref="StepAuxiliary.Step"/> and <see cref="StepAuxiliary.gameObject"/> set from this step.
        /// Matches <see cref="OnValidate"/> binding; safe to call from custom property drawers when nested SerializeReference UI runs without revalidating the Step.
        /// </summary>
        public void EnsureAuxiliaryOwnerReferences()
        {
            foreach (var auxiliary in auxiliaries)
                auxiliary?.BindOwnerFromStep(this);
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
        }

        /// <summary>
        /// Unregisters this step from the <see cref="ReferenceManager"/>. Subclasses
        /// that need teardown must override and call <c>base.OnDestroy()</c> —
        /// declaring a new private OnDestroy would hide this one and Unity would
        /// only invoke the derived method.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_referenceManager != null)
            {
                _referenceManager.Unregister(this);
            }
        }

        /// <summary>
        /// Dont use this method to update the step, use UpdateStep instead.
        /// </summary>
        protected virtual void Update(){}

        /// <summary>
        /// Validates if an auxiliary type is still valid and resolvable.
        /// This helps catch cases where SerializeReference points to types that no longer exist.
        /// </summary>
        public bool IsAuxiliaryTypeValid(StepAuxiliary auxiliary)
        {
            if (auxiliary == null) return false;

            try
            {
                var auxiliaryType = auxiliary.GetType();

                // Check if the type is still valid by ensuring it's assignable to StepAuxiliary
                if (!typeof(StepAuxiliary).IsAssignableFrom(auxiliaryType))
                {
                    return false;
                }

                // Check if the type is abstract (shouldn't happen but good to validate)
                if (auxiliaryType.IsAbstract)
                {
                    return false;
                }

                // Try to create an instance to ensure the type is properly resolvable
                // This will catch cases where the script was deleted or moved
                var testInstance = System.Activator.CreateInstance(auxiliaryType);
                if (testInstance == null)
                {
                    return false;
                }

                return true;
            }
            catch (System.Exception)
            {
                // If we can't create an instance or there's any other issue, consider it invalid
                return false;
            }
        }

        public virtual void Initialize(string sequenceId)
        {
            // Find direct children Step components to manage them.
            _childrenSteps = GetComponentsInChildren<Step>(true)
                .Where(s => s.transform.parent == transform).ToList();

            var dialogPlayer = GetComponent<DialogAudioPlayer>();
            if (dialogPlayer != null)
            {
                OnStepBegin.AddListener(dialogPlayer.PlayDialog);
            }

            // Initialize all auxiliaries that aren't already initialized
            // Use the full Initialize method for runtime initialization
            foreach (var auxiliary in auxiliaries)
            {
                if (auxiliary != null)
                {
                    auxiliary.Initialize(this);
                }
            }
        }
        
        /// <summary>
        /// Marks this step's internal condition as complete.
        /// The step will not be considered fully completed by the SequenceController until all its children are also completed.
        /// </summary>
        public void Complete()
        {
            if (_isInternallyCompleted) return;

            // Respect subclass completion gating — without this check, CanComplete()
            // overrides are never consulted and any caller can force-complete the step.
            if (!CanComplete())
            {
                Debug.Log($"Step '{gameObject.name}' cannot complete yet (CanComplete returned false).", this);
                return;
            }

            CompleteInternal();
        }

        /// <summary>
        /// Marks this step's internal condition as complete, bypassing the
        /// <see cref="CanComplete"/> gate. Use this only when a step must be forced
        /// complete regardless of its subclass completion conditions (e.g. an editor
        /// override or an authoring tool). Prefer <see cref="Complete"/> for normal flow.
        /// </summary>
        /// <remarks>
        /// Like <see cref="Complete"/>, this is a no-op if the step is already internally
        /// completed. Children are still required for the step to be considered
        /// <see cref="IsCompleted">fully completed</see>.
        /// </remarks>
        public void ForceComplete()
        {
            if (_isInternallyCompleted) return;

            Debug.LogWarning($"Step '{gameObject.name}' force-completed, bypassing CanComplete().", this);
            CompleteInternal();
        }

        /// <summary>
        /// Performs the actual completion work shared by <see cref="Complete"/> and
        /// <see cref="ForceComplete"/>. Callers are responsible for the
        /// already-completed guard and any <see cref="CanComplete"/> gating.
        /// </summary>
        private void CompleteInternal()
        {
            Debug.Log($"Step '{gameObject.name}' marked its internal condition as complete.", this);
            _isInternallyCompleted = true;

            // Call the virtual completion method
            OnStepCompleted();

            // Call auxiliary components' OnStepCompleted method
            foreach (var auxiliary in auxiliaries)
            {
                if (auxiliary != null && auxiliary.IsActive)
                {
                    try
                    {
                        auxiliary.OnStepCompleted();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"Error executing OnStepCompleted on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                    }
                }
            }

            // Dispatch step completion event
            if (_eventDispatcher != null)
            {
                _eventDispatcher.DispatchEvent("Step.Completed", this);

                // Check if this step is now fully completed
                if (IsCompleted)
                {
                    _eventDispatcher.DispatchEvent("Step.FullyCompleted", this);
                }
            }
        }

        /// <summary>
        /// Resets this step and all its children to their initial, uncompleted state.
        /// </summary>
        public void ResetStep()
        {
            _isInternallyCompleted = false;
            CurrentStatus = StepStatus.Inactive;

            // Call auxiliary components' OnStepReset method
            foreach (var auxiliary in auxiliaries)
            {
                if (auxiliary != null && auxiliary.IsActive)
                {
                    try
                    {
                        auxiliary.OnStepReset();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"Error executing OnStepReset on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                    }
                }
            }

            // Recursively reset children so the entire branch is ready to run again.
            if (_childrenSteps != null)
            {
                foreach (var child in _childrenSteps)
                {
                    child.ResetStep();
                }
            }
        }
        
        /// <summary>
        /// Checks if all direct child steps are marked as completed.
        /// </summary>
        private bool AreAllChildrenCompleted()
        {
            // If there are no children, this condition is automatically met.
            return _childrenSteps == null || !_childrenSteps.Any() || 
                _childrenSteps.All(step => step.IsCompleted || !step.gameObject.activeInHierarchy);
        }

        /// <summary>
        /// Checks if this step has any incomplete children.
        /// </summary>
        /// <returns>True if there are incomplete children, false otherwise.</returns>
        public bool HasIncompleteChildren()
        {
            return _childrenSteps != null && _childrenSteps.Any() && 
                _childrenSteps.Any(step => !step.IsCompleted && step.gameObject.activeInHierarchy);
        }
        
        /// <summary>
        /// Override this method to implement custom completion logic.
        /// </summary>
        /// <returns>True if the step's internal condition is met, false otherwise.</returns>
        protected virtual bool CanComplete()
        {
            return true;
        }

        /// <summary>
        /// Public probe for the protected <see cref="CanComplete"/> gate, so editor tooling
        /// (e.g. the Sequence Visualizer) can show whether a step is currently blocked from
        /// completing without subclassing it.
        /// </summary>
        /// <returns>The current value of <see cref="CanComplete"/>.</returns>
        /// <remarks>Additive accessor — does not change completion behavior.</remarks>
        public bool CanCompleteNow() => CanComplete();

        /// <summary>
        /// Human-readable explanation of why this step cannot complete yet, surfaced by
        /// editor tooling next to the completion state. Override in subclasses whose
        /// <see cref="CanComplete"/> gates on a condition (e.g. "waiting for input",
        /// "timer 1.2s remaining"). The base returns <c>null</c>, meaning "no specific
        /// reason" — callers fall back to a generic message.
        /// </summary>
        /// <returns>A short reason string, or <c>null</c> when there is none.</returns>
        /// <remarks>Additive hook — does not change completion behavior.</remarks>
        public virtual string GetCompletionBlockReason() => null;
        
        /// <summary>
        /// Override this method to implement custom step behavior when it becomes active.
        /// </summary>
        protected virtual void OnStepActivated()
        {
            // Override in derived classes to add custom behavior
        }
        
        /// <summary>
        /// Override this method to implement custom step behavior when it becomes inactive.
        /// </summary>
        protected virtual void OnStepDeactivated()
        {
            // Override in derived classes to add custom behavior
        }
        
        /// <summary>
        /// Override this method to implement custom step behavior when it's completed.
        /// </summary>
        protected virtual void OnStepCompleted()
        {
            // Override in derived classes to add custom behavior
        }
        
        /// <summary>
        /// Override this method to implement custom step behavior when the sequence is paused.
        /// </summary>
        protected virtual void OnStepPaused()
        {
            // Override in derived classes to add custom behavior
        }
        
        /// <summary>
        /// Override this method to implement custom step behavior when the sequence is resumed.
        /// </summary>
        protected virtual void OnStepResumed()
        {
            // Override in derived classes to add custom behavior
        }

        /// <summary>
        /// Sets the current status of the step. This is called by the SequenceController.
        /// </summary>
        public virtual void SetStatus(StepStatus status)
        {
            if (CurrentStatus == status) return;
            
            var previousStatus = CurrentStatus;
            CurrentStatus = status;

            if (status == StepStatus.Active)
            {
                StartTime = DateTime.Now;
            }
            else if (previousStatus == StepStatus.Active)
            {
                EndTime = DateTime.Now;
            }
            
            // Call appropriate virtual methods and events based on status change
            if (status == StepStatus.Active && previousStatus != StepStatus.Active && !_isInternallyCompleted)
            {
                OnStepActivated();
                foreach (var auxiliary in auxiliaries)
                {
                    if (auxiliary != null && auxiliary.IsActive)
                    {
                        auxiliary.OnStepBegin();
                    }
                }
                OnStepBegin?.Invoke();
            }
            else if (status != StepStatus.Active && previousStatus == StepStatus.Active)
            {
                OnStepDeactivated();
                foreach (var auxiliary in auxiliaries)
                {
                    if (auxiliary != null && auxiliary.IsActive)
                    {
                        try
                        {
                            auxiliary.OnStepEnd();
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"Error executing OnStepEnd on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                        }
                    }
                }
                OnStepEnd?.Invoke();
            }
        }
        
        /// <summary>
        /// Add an auxiliary to this step.
        /// </summary>
        /// <param name="auxiliary">The auxiliary to add</param>
        public void AddAuxiliary(StepAuxiliary auxiliary)
        {
            if (auxiliary != null && !auxiliaries.Contains(auxiliary))
            {
                auxiliaries.Add(auxiliary);
                // Don't initialize here - initialization happens during Step.Initialize()
                // auxiliary.Initialize(this);
            }
        }
        
        /// <summary>
        /// Remove an auxiliary from this step.
        /// </summary>
        /// <param name="auxiliary">The auxiliary to remove</param>
        public void RemoveAuxiliary(StepAuxiliary auxiliary)
        {
            if (auxiliary != null && auxiliaries.Remove(auxiliary))
            {
                auxiliary.OnRemoved();
            }
        }
        
        /// <summary>
        /// Remove an auxiliary by index.
        /// </summary>
        /// <param name="index">Index of the auxiliary to remove</param>
        public void RemoveAuxiliaryAt(int index)
        {
            if (index >= 0 && index < auxiliaries.Count)
            {
                var auxiliary = auxiliaries[index];
                auxiliaries.RemoveAt(index);
                auxiliary?.OnRemoved();
            }
        }

        public T GetAuxiliary<T>() where T : StepAuxiliary
        {
            return auxiliaries.FirstOrDefault(a => a != null && a is T) as T ?? throw new Exception($"No auxiliary of type {typeof(T).Name} found on step {gameObject.name}");
        }

        public bool HasAuxiliary<T>() where T : StepAuxiliary
        {
            return auxiliaries.Any(a => a != null && a is T);
        }
        
        /// <summary>
        /// Called by SequenceController when the sequence is paused.
        /// This propagates the pause event to the step and all its auxiliaries.
        /// </summary>
        internal void NotifyPause()
        {
            // Call the virtual method for custom step behavior
            OnStepPaused();
            
            // Notify all auxiliaries
            foreach (var auxiliary in auxiliaries)
            {
                if (auxiliary != null && auxiliary.IsActive)
                {
                    try
                    {
                        auxiliary.OnStepPause();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"Error executing OnStepPause on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                    }
                }
            }
        }
        
        /// <summary>
        /// Called by SequenceController when the sequence is resumed.
        /// This propagates the resume event to the step and all its auxiliaries.
        /// </summary>
        internal void NotifyResume()
        {
            // Call the virtual method for custom step behavior
            OnStepResumed();
            
            // Notify all auxiliaries
            foreach (var auxiliary in auxiliaries)
            {
                if (auxiliary != null && auxiliary.IsActive)
                {
                    try
                    {
                        auxiliary.OnStepResume();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"Error executing OnStepResume on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                    }
                }
            }
        }
        
        /// <summary>
        /// Called by SequenceController every frame to update the step and its auxiliaries.
        /// This is controlled by the sequence state (won't be called when paused).
        /// Override this to add custom update logic in derived step classes.
        /// </summary>
        public virtual void UpdateStep()
        {
            // Only update auxiliaries when step is active
            if (CurrentStatus == StepStatus.Active)
            {
                foreach (var auxiliary in auxiliaries)
                {
                    if (auxiliary != null && auxiliary.IsActive)
                    {
                        try
                        {
                            auxiliary.OnStepUpdate();
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"Error executing OnStepUpdate on auxiliary {auxiliary.GetType().Name}: {e.Message}", this);
                        }
                    }
                }
            }
        }
    }
} 