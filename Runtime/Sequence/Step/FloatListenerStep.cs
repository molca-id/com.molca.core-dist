using UnityEngine;
using Molca.Events;
using Molca.Attributes;

namespace Molca.Sequence
{
    /// <summary>
    /// A step that listens to float events and completes when a target value is reached.
    /// Useful for progress tracking, health monitoring, or any float-based event listening.
    /// </summary>
    public class FloatListenerStep : Step
    {
        // Note: EventDispatcher is inherited from Step base class via [Inject]
        
        [Header("Listener Settings")]
        [Tooltip("The event name to listen for float values.")]
        public string eventName = "Progress.Updated";
        
        [Tooltip("The target float value to reach for completion.")]
        public float targetValue = 1f;
        
        [Tooltip("The current float value received from events.")]
        [SerializeField, ReadOnly] private float currentValue = 0f;
        
        [Tooltip("Whether to reset the value when the step is reset.")]
        public bool resetValueOnReset = true;
        
        [Tooltip("Whether to allow values below zero.")]
        public bool allowNegativeValues = false;
        
        [Tooltip("Whether to allow values to exceed the target.")]
        public bool allowExceedingTarget = false;
        
        [Tooltip("Comparison mode for determining completion.")]
        public ComparisonMode comparisonMode = ComparisonMode.GreaterThanOrEqual;
        
        [Header("Event Filtering")]
        [Tooltip("Whether to filter events by sender object.")]
        public bool filterBySender = false;
        
        [Tooltip("The specific sender object to listen for (if filtering is enabled).")]
        public Object targetSender;
        
        [Tooltip("Whether to accept any sender if target sender is null.")]
        public bool acceptAnySenderIfNull = true;
        
        [Header("Events")]
        [Tooltip("Fired when the float value changes.")]
        public UnityEngine.Events.UnityEvent<float> OnValueChanged;
        
        [Tooltip("Fired when the target value is reached.")]
        public UnityEngine.Events.UnityEvent OnTargetReached;
        
        public enum ComparisonMode
        {
            GreaterThanOrEqual,
            LessThanOrEqual,
            Equal,
            GreaterThan,
            LessThan
        }
        
        /// <summary>
        /// The current float value.
        /// </summary>
        public float CurrentValue => currentValue;
        
        /// <summary>
        /// The target value to reach.
        /// </summary>
        public float TargetValue
        {
            get => targetValue;
            set => targetValue = value;
        }
        
        /// <summary>
        /// Progress towards the target (0-1) for GreaterThanOrEqual mode.
        /// </summary>
        public float Progress => targetValue > 0 ? Mathf.Clamp01(currentValue / targetValue) : 0f;
        
        /// <summary>
        /// Whether the target has been reached based on comparison mode.
        /// </summary>
        public bool IsTargetReached
        {
            get
            {
                return comparisonMode switch
                {
                    ComparisonMode.GreaterThanOrEqual => currentValue >= targetValue,
                    ComparisonMode.LessThanOrEqual => currentValue <= targetValue,
                    ComparisonMode.Equal => Mathf.Approximately(currentValue, targetValue),
                    ComparisonMode.GreaterThan => currentValue > targetValue,
                    ComparisonMode.LessThan => currentValue < targetValue,
                    _ => false
                };
            }
        }
        
        /// <summary>
        /// Whether the value is at zero.
        /// </summary>
        public bool IsAtZero => Mathf.Approximately(currentValue, 0f);
        
        /// <summary>
        /// Whether the value equals the target value.
        /// </summary>
        public bool IsAtTarget => Mathf.Approximately(currentValue, targetValue);
        
        protected override void OnStepActivated()
        {
            base.OnStepActivated();
            
            // Reset value if requested
            if (resetValueOnReset)
            {
                SetValue(0f);
            }
            
            // Subscribe to the event
            SubscribeToEvent();
            
            // Check if already at target
            CheckForCompletion();
        }
        
        protected override void OnStepDeactivated()
        {
            base.OnStepDeactivated();
            
            // Unsubscribe from the event
            UnsubscribeFromEvent();
            
            // Reset value if requested
            if (resetValueOnReset)
            {
                SetValue(0f);
            }
        }
        
        private void SubscribeToEvent()
        {
            if (_eventDispatcher != null)
            {
                _eventDispatcher.RegisterEvent<object>(eventName, OnFloatEventReceived);
            }
            else
            {
                Debug.LogError($"FloatListenerStep '{gameObject.name}': EventDispatcher not available!", this);
            }
        }
        
        private void UnsubscribeFromEvent()
        {
            if (_eventDispatcher != null)
            {
                _eventDispatcher.UnregisterEvent<object>(eventName, OnFloatEventReceived);
            }
        }
        
        private void OnFloatEventReceived(object eventData)
        {
            // Check if we should filter by sender
            if (filterBySender && targetSender != null)
            {
                // Try to extract sender from event data
                // Soft casts: payloads are often boxed value types (float/int),
                // and a direct (Object) cast would throw InvalidCastException.
                if (eventData is System.Collections.Generic.Dictionary<string, object> dict)
                {
                    if (dict.TryGetValue("sender", out var sender) && sender as Object != targetSender)
                    {
                        return; // Filter out this event
                    }
                }
                else if (eventData as Object != targetSender)
                {
                    return; // Filter out this event
                }
            }
            
            // Extract float value from event data
            float floatValue = ExtractFloatValue(eventData);
            SetValue(floatValue);
        }
        
        private float ExtractFloatValue(object eventData)
        {
            if (eventData is float floatVal)
            {
                return floatVal;
            }
            else if (eventData is int intVal)
            {
                return intVal;
            }
            else if (eventData is double doubleVal)
            {
                return (float)doubleVal;
            }
            else if (eventData is System.Collections.Generic.Dictionary<string, object> dict)
            {
                // Try common keys for float values
                if (dict.TryGetValue("value", out var value))
                {
                    return ConvertToFloat(value);
                }
                else if (dict.TryGetValue("progress", out var progress))
                {
                    return ConvertToFloat(progress);
                }
                else if (dict.TryGetValue("amount", out var amount))
                {
                    return ConvertToFloat(amount);
                }
                else if (dict.TryGetValue("current", out var current))
                {
                    return ConvertToFloat(current);
                }
            }
            
            Debug.LogWarning($"FloatListenerStep '{gameObject.name}': Could not extract float value from event data: {eventData}", this);
            return 0f;
        }
        
        private float ConvertToFloat(object value)
        {
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is double d) return (float)d;
            if (value is string s && float.TryParse(s, out float parsed)) return parsed;
            return 0f;
        }
        
        /// <summary>
        /// Manually sets the float value.
        /// </summary>
        /// <param name="value">The new float value</param>
        public void SetValue(float value)
        {
            // Apply constraints
            if (!allowNegativeValues && value < 0f)
            {
                value = 0f;
            }
            
            if (!allowExceedingTarget && comparisonMode == ComparisonMode.GreaterThanOrEqual && value > targetValue)
            {
                value = targetValue;
            }
            
            // Only update if the value actually changed
            if (!Mathf.Approximately(currentValue, value))
            {
                float previousValue = currentValue;
                // Capture the pre-change state BEFORE mutating currentValue —
                // IsTargetReached reads currentValue, so sampling it after the
                // assignment would compare the new state to itself and the
                // reached-transition below could never fire.
                bool wasAtTarget = IsTargetReached;
                currentValue = value;

                // Fire events
                OnValueChanged?.Invoke(currentValue);
                _eventDispatcher?.DispatchEvent(EventConstants.Sequence.FloatListenerValueChanged, new { step = this, previousValue, currentValue, targetValue });

                // Check if target was reached
                if (!wasAtTarget && IsTargetReached)
                {
                    OnTargetReached?.Invoke();
                    _eventDispatcher?.DispatchEvent(EventConstants.Sequence.FloatListenerTargetReached, this);
                }
                
                // Check for completion
                CheckForCompletion();
                
                Debug.Log($"FloatListenerStep '{gameObject.name}': Value changed from {previousValue} to {currentValue} (target: {targetValue}, mode: {comparisonMode})", this);
            }
        }
        
        /// <summary>
        /// Manually completes the step by setting the value to reach the target.
        /// </summary>
        public void CompleteStep()
        {
            switch (comparisonMode)
            {
                case ComparisonMode.GreaterThanOrEqual:
                case ComparisonMode.GreaterThan:
                    SetValue(targetValue);
                    break;
                case ComparisonMode.LessThanOrEqual:
                case ComparisonMode.LessThan:
                    SetValue(targetValue);
                    break;
                case ComparisonMode.Equal:
                    SetValue(targetValue);
                    break;
            }
        }
        
        /// <summary>
        /// Checks if the step should be completed based on the current value and comparison mode.
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
            Debug.Log($"FloatListenerStep '{gameObject.name}': Target reached! Value: {currentValue}, Target: {targetValue}, Mode: {comparisonMode}", this);
        }
        
        /// <summary>
        /// Gets a formatted string representation of the current state.
        /// </summary>
        /// <returns>Formatted string with current value, target, and comparison mode</returns>
        public string GetStatusString()
        {
            string comparisonSymbol = comparisonMode switch
            {
                ComparisonMode.GreaterThanOrEqual => ">=",
                ComparisonMode.LessThanOrEqual => "<=",
                ComparisonMode.Equal => "==",
                ComparisonMode.GreaterThan => ">",
                ComparisonMode.LessThan => "<",
                _ => "?"
            };
            
            return $"Value: {currentValue:F2} {comparisonSymbol} {targetValue:F2}";
        }
        
        /// <summary>
        /// Gets a detailed string representation of the current state.
        /// </summary>
        /// <returns>Detailed string with current value, target, comparison mode, and completion status</returns>
        public string GetDetailedStatusString()
        {
            return $"{GetStatusString()}, Completed: {IsTargetReached}, Event: {eventName}";
        }
        
        /// <summary>
        /// Changes the event name to listen to.
        /// </summary>
        /// <param name="newEventName">The new event name</param>
        public void ChangeEventName(string newEventName)
        {
            if (string.IsNullOrEmpty(newEventName)) return;
            
            // Unsubscribe from current event
            UnsubscribeFromEvent();
            
            // Change event name
            eventName = newEventName;
            
            // Subscribe to new event if step is active
            if (CurrentStatus == StepStatus.Active)
            {
                SubscribeToEvent();
            }
        }
        
        /// <summary>
        /// Changes the target value.
        /// </summary>
        /// <param name="newTargetValue">The new target value</param>
        public void ChangeTargetValue(float newTargetValue)
        {
            targetValue = newTargetValue;
            CheckForCompletion();
        }
        
        /// <summary>
        /// Changes the comparison mode.
        /// </summary>
        /// <param name="newMode">The new comparison mode</param>
        public void ChangeComparisonMode(ComparisonMode newMode)
        {
            comparisonMode = newMode;
            CheckForCompletion();
        }
        
        #region Editor Helpers
        
        /// <summary>
        /// Editor helper to test the float listener functionality.
        /// </summary>
        [ContextMenu("Test Set Value")]
        private void TestSetValue()
        {
            if (Application.isPlaying)
            {
                SetValue(targetValue);
            }
        }
        
        /// <summary>
        /// Editor helper to test the float listener functionality.
        /// </summary>
        [ContextMenu("Test Reset Value")]
        private void TestResetValue()
        {
            if (Application.isPlaying)
            {
                SetValue(0f);
            }
        }
        
        /// <summary>
        /// Editor helper to test the float listener functionality.
        /// </summary>
        [ContextMenu("Test Complete Step")]
        private void TestCompleteStep()
        {
            if (Application.isPlaying)
            {
                CompleteStep();
            }
        }
        
        #endregion
    }
} 