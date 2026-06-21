using UnityEngine;
using UnityEngine.Events;
using Molca.Events;
using Molca.Attributes;

namespace Molca.Sequence
{
    /// <summary>
    /// A step that tracks an integer counter towards a target value.
    /// Automatically completes when the target is reached.
    /// </summary>
    public class IntCounterStep : Step
    {
        // Note: EventDispatcher is inherited from Step base class via [Inject]
        
        [Header("Counter Settings")]
        [Tooltip("The target value to reach for completion.")]
        public int targetValue = 1;
        
        [Tooltip("The current counter value.")]
        [SerializeField, ReadOnly] private int currentValue = 0;
        
        [Tooltip("Whether to reset the counter when the step is reset.")]
        public bool resetCounterOnReset = true;
        
        [Tooltip("Whether to allow the counter to go below zero.")]
        public bool allowNegativeValues = false;
        
        [Tooltip("Whether to allow the counter to exceed the target value.")]
        public bool allowExceedingTarget = false;
        
        [Header("Events")]
        [Tooltip("Fired when the counter value changes.")]
        public UnityEvent<int> OnCounterChanged;
        
        [Tooltip("Fired when the counter reaches the target value.")]
        public UnityEvent OnTargetReached;
        
        /// <summary>
        /// The current counter value.
        /// </summary>
        public int CurrentValue => currentValue;
        
        /// <summary>
        /// The target value to reach.
        /// </summary>
        public int TargetValue => targetValue;
        
        /// <summary>
        /// Progress towards the target (0-1).
        /// </summary>
        public float Progress => targetValue > 0 ? Mathf.Clamp01((float)currentValue / targetValue) : 0f;
        
        /// <summary>
        /// Whether the target has been reached.
        /// </summary>
        public bool IsTargetReached => currentValue >= targetValue;
        
        /// <summary>
        /// Whether the counter is at zero.
        /// </summary>
        public bool IsAtZero => currentValue == 0;
        
        /// <summary>
        /// Whether the counter is at the target value.
        /// </summary>
        public bool IsAtTarget => currentValue == targetValue;
        
        /// <summary>
        /// Whether the counter has exceeded the target value.
        /// </summary>
        public bool HasExceededTarget => currentValue > targetValue;
        
        protected override void OnStepActivated()
        {
            base.OnStepActivated();
            
            // Reset counter if requested
            if (resetCounterOnReset)
            {
                SetCounter(0);
            }
            
            // Check if already at target
            CheckForCompletion();
        }
        
        /// <summary>
        /// Increments the counter by the specified amount.
        /// </summary>
        /// <param name="amount">Amount to increment (default: 1)</param>
        public void Increment(int amount = 1)
        {
            if (amount <= 0) return;
            
            int newValue = currentValue + amount;
            SetCounter(newValue);
        }
        
        /// <summary>
        /// Decrements the counter by the specified amount.
        /// </summary>
        /// <param name="amount">Amount to decrement (default: 1)</param>
        public void Decrement(int amount = 1)
        {
            if (amount <= 0) return;
            
            int newValue = currentValue - amount;
            SetCounter(newValue);
        }
        
        /// <summary>
        /// Sets the counter to a specific value.
        /// </summary>
        /// <param name="value">The new counter value</param>
        public void SetCounter(int value)
        {
            // Apply constraints
            if (!allowNegativeValues && value < 0)
            {
                value = 0;
            }
            
            if (!allowExceedingTarget && value > targetValue)
            {
                value = targetValue;
            }
            
            // Only update if the value actually changed
            if (currentValue != value)
            {
                int previousValue = currentValue;
                currentValue = value;
                
                // Fire events
                OnCounterChanged?.Invoke(currentValue);
                _eventDispatcher?.DispatchEvent("IntCounterStep.ValueChanged", new { step = this, previousValue, currentValue, targetValue });
                
                // Check if target was reached
                if (previousValue < targetValue && currentValue >= targetValue)
                {
                    OnTargetReached?.Invoke();
                    _eventDispatcher?.DispatchEvent("IntCounterStep.TargetReached", this);
                }
                
                // Check for completion
                CheckForCompletion();
                
                Debug.Log($"IntCounterStep '{gameObject.name}': Counter changed from {previousValue} to {currentValue} (target: {targetValue})", this);
            }
        }
        
        /// <summary>
        /// Resets the counter to zero.
        /// </summary>
        public void ResetCounter()
        {
            SetCounter(0);
        }
        
        /// <summary>
        /// Sets the counter to the target value.
        /// </summary>
        public void SetToTarget()
        {
            SetCounter(targetValue);
        }
        
        /// <summary>
        /// Manually completes the step by setting the counter to the target value.
        /// </summary>
        public void CompleteStep()
        {
            SetToTarget();
        }
        
        /// <summary>
        /// Checks if the step should be completed based on the current counter value.
        /// </summary>
        private void CheckForCompletion()
        {
            if (IsTargetReached && !IsCompleted)
            {
                Complete();
            }
        }
        
        /// <summary>
        /// Override to provide custom completion logic.
        /// </summary>
        /// <returns>True if the step's internal condition is met, false otherwise.</returns>
        protected override bool CanComplete()
        {
            return IsTargetReached;
        }
        
        /// <summary>
        /// Override to implement custom step behavior when it's completed.
        /// </summary>
        protected override void OnStepCompleted()
        {
            base.OnStepCompleted();
            Debug.Log($"IntCounterStep '{gameObject.name}': Target reached! Counter: {currentValue}/{targetValue}", this);
        }
        
        /// <summary>
        /// Override to implement custom step behavior when it's reset.
        /// </summary>
        protected override void OnStepDeactivated()
        {
            base.OnStepDeactivated();
            
            // Reset counter if requested
            if (resetCounterOnReset)
            {
                SetCounter(0);
            }
        }
        
        /// <summary>
        /// Gets a formatted string representation of the counter progress.
        /// </summary>
        /// <returns>Formatted string like "5/10" or "Current: 5, Target: 10"</returns>
        public string GetProgressString()
        {
            return $"{currentValue}/{targetValue}";
        }
        
        /// <summary>
        /// Gets a detailed string representation of the counter state.
        /// </summary>
        /// <returns>Detailed string with current, target, and progress information</returns>
        public string GetDetailedProgressString()
        {
            float progressPercent = Progress * 100f;
            return $"Current: {currentValue}, Target: {targetValue}, Progress: {progressPercent:F1}%";
        }
        
        #region Editor Helpers
        
        /// <summary>
        /// Editor helper to test the counter functionality.
        /// </summary>
        [ContextMenu("Test Increment")]
        private void TestIncrement()
        {
            if (Application.isPlaying)
            {
                Increment();
            }
        }
        
        /// <summary>
        /// Editor helper to test the counter functionality.
        /// </summary>
        [ContextMenu("Test Decrement")]
        private void TestDecrement()
        {
            if (Application.isPlaying)
            {
                Decrement();
            }
        }
        
        /// <summary>
        /// Editor helper to test the counter functionality.
        /// </summary>
        [ContextMenu("Test Set to Target")]
        private void TestSetToTarget()
        {
            if (Application.isPlaying)
            {
                SetToTarget();
            }
        }
        
        /// <summary>
        /// Editor helper to test the counter functionality.
        /// </summary>
        [ContextMenu("Test Reset Counter")]
        private void TestResetCounter()
        {
            if (Application.isPlaying)
            {
                ResetCounter();
            }
        }
        
        #endregion
    }
} 