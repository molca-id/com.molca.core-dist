using System.Linq;
using UnityEngine;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Enables/disables components when step events occur.
    /// Attach this to the same GameObject as a Step component for automatic event hooking.
    /// </summary>
    [AuxiliaryMenu("Utility/Component Toggler")]
    public class StepComponentToggler : StepAuxiliary
    {
        [System.Serializable]
        public class ComponentToggle
        {
            [Tooltip("The component to toggle.")]
            public Component component;
            
            [Tooltip("Whether to enable or disable the component.")]
            public bool enable = true;
            
            [Tooltip("When to apply this toggle (Begin, End, or Both).")]
            public ToggleTiming timing = ToggleTiming.Begin;
        }
        
        public enum ToggleTiming
        {
            Begin,
            End,
            Both
        }
        
        [Header("Component Toggle Settings")]
        [Tooltip("Components to toggle. Use each entry's Timing (Begin / End / Both) to control when it is applied.")]
        public ComponentToggle[] toggles;
        
        [Header("Toggle Options")]
        [Tooltip("If true, will restore original component states when the step ends.")]
        public bool restoreOnEnd = false;
        
        [Tooltip("If true, will also toggle components when the step is reset.")]
        public bool toggleOnReset = true;
        
        private ComponentToggle[] _allToggles;
        private bool[] _originalStates;
        
        private void Awake()
        {
            // Collect all toggles and their original states
            CollectAllToggles();
        }
        
        public override void OnStepBegin()
        {
            ApplyTogglesWithTiming(ToggleTiming.Begin);
        }
        
        public override void OnStepEnd()
        {
            if (restoreOnEnd)
            {
                RestoreOriginalStates();
            }
            else
            {
                ApplyTogglesWithTiming(ToggleTiming.End);
            }
        }
        
        public override void OnStepCompleted()
        {
            // Optional: Add specific behavior when step is completed
            // Currently using OnStepEnd for all end-of-step logic
        }
        
        public override void OnStepReset()
        {
            if (toggleOnReset)
            {
                RestoreOriginalStates();
            }
        }
        
        private void CollectAllToggles()
        {
            if (toggles == null)
            {
                _allToggles = System.Array.Empty<ComponentToggle>();
            }
            else
            {
                _allToggles = toggles.Where(toggle => toggle != null && toggle.component != null).ToArray();
            }
            
            // Store original states
            _originalStates = new bool[_allToggles.Length];
            for (int i = 0; i < _allToggles.Length; i++)
            {
                _originalStates[i] = IsComponentEnabled(_allToggles[i].component);
            }
        }
        
        private void ApplyToggles(ComponentToggle[] toggles)
        {
            if (toggles == null) return;
            
            foreach (var toggle in toggles)
            {
                if (toggle != null && toggle.component != null)
                {
                    SetComponentEnabled(toggle.component, toggle.enable);
                }
            }
        }
        
        private void ApplyTogglesWithTiming(ToggleTiming timing)
        {
            if (_allToggles == null) return;
            
            foreach (var toggle in _allToggles)
            {
                if (toggle.timing == timing || toggle.timing == ToggleTiming.Both)
                {
                    SetComponentEnabled(toggle.component, toggle.enable);
                }
            }
        }
        
        private void RestoreOriginalStates()
        {
            if (_allToggles == null || _originalStates == null) return;
            
            for (int i = 0; i < _allToggles.Length && i < _originalStates.Length; i++)
            {
                if (_allToggles[i] != null && _allToggles[i].component != null)
                {
                    SetComponentEnabled(_allToggles[i].component, _originalStates[i]);
                }
            }
        }
        
        private bool IsComponentEnabled(Component component)
        {
            if (component == null) return false;
            
            // Handle different component types
            if (component is Behaviour behaviour)
            {
                return behaviour.enabled;
            }
            else if (component is Renderer renderer)
            {
                return renderer.enabled;
            }
            else if (component is Collider collider)
            {
                return collider.enabled;
            }
            else if (component is AudioSource audioSource)
            {
                return audioSource.enabled;
            }
            else if (component is Light light)
            {
                return light.enabled;
            }
            
            // For other component types, try to use reflection
            var enabledProperty = component.GetType().GetProperty("enabled");
            if (enabledProperty != null && enabledProperty.PropertyType == typeof(bool))
            {
                return (bool)enabledProperty.GetValue(component);
            }
            
            return true; // Default to enabled if we can't determine
        }
        
        private void SetComponentEnabled(Component component, bool enabled)
        {
            if (component == null) return;
            
            // Handle different component types
            if (component is Behaviour behaviour)
            {
                behaviour.enabled = enabled;
            }
            else if (component is Renderer renderer)
            {
                renderer.enabled = enabled;
            }
            else if (component is Collider collider)
            {
                collider.enabled = enabled;
            }
            else if (component is AudioSource audioSource)
            {
                audioSource.enabled = enabled;
            }
            else if (component is Light light)
            {
                light.enabled = enabled;
            }
            else
            {
                // For other component types, try to use reflection
                var enabledProperty = component.GetType().GetProperty("enabled");
                if (enabledProperty != null && enabledProperty.PropertyType == typeof(bool))
                {
                    enabledProperty.SetValue(component, enabled);
                }
            }
        }
        
        /// <summary>
        /// Manually reset the component toggler to original states.
        /// </summary>
        public void ResetToggler()
        {
            RestoreOriginalStates();
        }
        
        /// <summary>
        /// Manually toggle a specific component.
        /// </summary>
        public void ToggleComponent(Component component, bool enabled)
        {
            SetComponentEnabled(component, enabled);
        }
        
        /// <summary>
        /// Manually toggle multiple components.
        /// </summary>
        public void ToggleComponents(ComponentToggle[] toggles)
        {
            ApplyToggles(toggles);
        }
    }
} 