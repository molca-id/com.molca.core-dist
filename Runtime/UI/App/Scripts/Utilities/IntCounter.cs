using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    /// <summary>
    /// A simple integer counter utility class for tracking counts in VR applications.
    /// </summary>
    public class IntCounter : MonoBehaviour
    {
        [Header("Counter Settings")]
        [SerializeField] private int initialValue = 0;
        [SerializeField] private int minValue = int.MinValue;
        [SerializeField] private int maxValue = int.MaxValue;
        
        private int currentValue;
        
        [Header("Events")]
        /// <summary>
        /// Event triggered when the counter value changes.
        /// </summary>
        [SerializeField] private UnityEvent<int> onValueChanged = new UnityEvent<int>();
        
        /// <summary>
        /// Event triggered when the counter reaches its maximum value.
        /// </summary>
        [SerializeField] private UnityEvent onMaxValueReached = new UnityEvent();
        
        /// <summary>
        /// Event triggered when the counter reaches its minimum value.
        /// </summary>
        [SerializeField] private UnityEvent onMinValueReached = new UnityEvent();
        
        /// <summary>
        /// Current value of the counter.
        /// </summary>
        public int Value
        {
            get => currentValue;
            private set
            {
                if (currentValue != value)
                {
                    currentValue = Mathf.Clamp(value, minValue, maxValue);
                    onValueChanged?.Invoke(currentValue);
                    
                    if (currentValue == maxValue)
                        onMaxValueReached?.Invoke();
                    else if (currentValue == minValue)
                        onMinValueReached?.Invoke();
                }
            }
        }
        
        private void Awake()
        {
            currentValue = initialValue;
        }
        
        /// <summary>
        /// Increments the counter by 1.
        /// </summary>
        public void Increment()
        {
            Value++;
        }
        
        /// <summary>
        /// Increments the counter by the specified amount.
        /// </summary>
        /// <param name="amount">Amount to increment by</param>
        public void Increment(int amount)
        {
            Value += amount;
        }
        
        /// <summary>
        /// Decrements the counter by 1.
        /// </summary>
        public void Decrement()
        {
            Value--;
        }
        
        /// <summary>
        /// Decrements the counter by the specified amount.
        /// </summary>
        /// <param name="amount">Amount to decrement by</param>
        public void Decrement(int amount)
        {
            Value -= amount;
        }
        
        /// <summary>
        /// Resets the counter to its initial value.
        /// </summary>
        public void Reset()
        {
            Value = initialValue;
        }
        
        /// <summary>
        /// Sets the counter to a specific value.
        /// </summary>
        /// <param name="newValue">New value to set</param>
        public void SetValue(int newValue)
        {
            Value = newValue;
        }
        
        /// <summary>
        /// Checks if the counter is at its maximum value.
        /// </summary>
        /// <returns>True if at max value, false otherwise</returns>
        public bool IsAtMax()
        {
            return currentValue == maxValue;
        }
        
        /// <summary>
        /// Checks if the counter is at its minimum value.
        /// </summary>
        /// <returns>True if at min value, false otherwise</returns>
        public bool IsAtMin()
        {
            return currentValue == minValue;
        }
        
        /// <summary>
        /// Gets the current value as a string.
        /// </summary>
        /// <returns>Current value as string</returns>
        public override string ToString()
        {
            return currentValue.ToString();
        }
        
        /// <summary>
        /// Public accessor for the OnValueChanged event.
        /// </summary>
        public UnityEvent<int> OnValueChanged => onValueChanged;
        
        /// <summary>
        /// Public accessor for the OnMaxValueReached event.
        /// </summary>
        public UnityEvent OnMaxValueReached => onMaxValueReached;
        
        /// <summary>
        /// Public accessor for the OnMinValueReached event.
        /// </summary>
        public UnityEvent OnMinValueReached => onMinValueReached;
    }
} 