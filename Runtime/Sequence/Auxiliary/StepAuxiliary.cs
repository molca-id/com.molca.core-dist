using UnityEngine;
using Molca.Sequence;
using System;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Attribute to indicate that an auxiliary should be drawn as a single property
    /// instead of having its individual fields drawn separately.
    /// This allows custom property drawers to work properly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class CustomAuxiliaryDrawerAttribute : Attribute
    {
    }

    /// <summary>
    /// Base class for step auxiliary components that provide additional functionality to steps.
    /// Similar to SRP render features, these can be easily added/removed and are automatically managed.
    /// </summary>
    [Serializable]
    public abstract class StepAuxiliary
    {
        [Header("Auxiliary Settings")]
        [SerializeField] private bool enabled = true;
        
        protected Step _step;
        protected bool _isInitialized = false;
        protected GameObject _gameObject;
        
        /// <summary>
        /// Reference to the Step component this auxiliary belongs to.
        /// </summary>
        public Step Step => _step;
        
        /// <summary>
        /// Reference to the GameObject this auxiliary is associated with.
        /// </summary>
        public GameObject gameObject => _gameObject;
        
        /// <summary>
        /// Whether this auxiliary is enabled and should execute.
        /// </summary>
        public bool IsEnabled => enabled;
        
        /// <summary>
        /// Whether this auxiliary has been properly initialized and is enabled.
        /// </summary>
        public bool IsActive => _isInitialized && enabled;

        /// <summary>
        /// Sets <see cref="_step"/> and <see cref="_gameObject"/> from the owning step.
        /// Same binding as <see cref="Step"/> validation: does not set <see cref="_isInitialized"/> or call <see cref="OnInitialize"/>.
        /// Call from custom property drawers, importers, or other editor paths where auxiliaries are used before <see cref="Initialize"/> runs.
        /// </summary>
        public void BindOwnerFromStep(Step owner)
        {
            if (owner == null) return;
            _step = owner;
            _gameObject = owner.gameObject;
        }
        
        /// <summary>
        /// Initialize the auxiliary component with a reference to its parent Step and GameObject.
        /// This is called automatically by the Step component.
        /// </summary>
        /// <param name="step">The Step component this auxiliary belongs to.</param>
        public void Initialize(Step step)
        {
            if (_isInitialized)
            {
                // Already initialized, skip
                return;
            }
            
            _step = step;
            _gameObject = step.gameObject;
            _isInitialized = true;
            
            // Call the virtual initialization method
            OnInitialize();
        }
        
        /// <summary>
        /// Called after the auxiliary is initialized. Override this for custom initialization logic.
        /// </summary>
        protected virtual void OnInitialize() { }
        
        /// <summary>
        /// Called when the step becomes active (begins execution).
        /// </summary>
        public abstract void OnStepBegin();
        
        /// <summary>
        /// Called when the step becomes inactive (stops execution).
        /// This can happen when the step is cancelled, skipped, or reset.
        /// </summary>
        public virtual void OnStepEnd() { }
        
        /// <summary>
        /// Called when the step's internal objective is completed.
        /// Note: The step may still be active if it has incomplete children.
        /// </summary>
        public abstract void OnStepCompleted();
        
        /// <summary>
        /// Called when the step is reset to its initial state.
        /// </summary>
        public virtual void OnStepReset() { }
        
        /// <summary>
        /// Called when the sequence is paused while this step is active.
        /// </summary>
        public virtual void OnStepPause() { }
        
        /// <summary>
        /// Called when the sequence is resumed while this step is active.
        /// </summary>
        public virtual void OnStepResume() { }
        
        /// <summary>
        /// Called every frame while the step is active. Override for continuous behavior.
        /// </summary>
        public virtual void OnStepUpdate() { }
        
        /// <summary>
        /// Enable or disable this auxiliary at runtime.
        /// </summary>
        /// <param name="enabled">Whether to enable the auxiliary</param>
        public virtual void SetEnabled(bool enabled)
        {
            this.enabled = enabled;
        }
        
        /// <summary>
        /// Called when the auxiliary is being removed. Override for cleanup.
        /// </summary>
        public virtual void OnRemoved()
        {
            _isInitialized = false;
            _step = null;
            _gameObject = null;
        }
        
        /// <summary>
        /// Get a component from the associated GameObject.
        /// </summary>
        /// <typeparam name="T">Type of component to get</typeparam>
        /// <returns>The component, or null if not found</returns>
        protected T GetComponent<T>() where T : Component
        {
            return _gameObject != null ? _gameObject.GetComponent<T>() : null;
        }
        
        /// <summary>
        /// Get components from the associated GameObject.
        /// </summary>
        /// <typeparam name="T">Type of components to get</typeparam>
        /// <returns>Array of components</returns>
        protected T[] GetComponents<T>() where T : Component
        {
            return _gameObject != null ? _gameObject.GetComponents<T>() : new T[0];
        }
        
        /// <summary>
        /// Find a child GameObject by name.
        /// </summary>
        /// <param name="name">Name of the child to find</param>
        /// <returns>The child GameObject, or null if not found</returns>
        protected GameObject FindChild(string name)
        {
            if (_gameObject == null) return null;
            
            var child = _gameObject.transform.Find(name);
            return child != null ? child.gameObject : null;
        }
        
        /// <summary>
        /// Find a child GameObject by name recursively.
        /// </summary>
        /// <param name="name">Name of the child to find</param>
        /// <returns>The child GameObject, or null if not found</returns>
        protected GameObject FindChildRecursive(string name)
        {
            if (_gameObject == null) return null;
            
            var child = _gameObject.transform.Find(name);
            if (child != null) return child.gameObject;
            
            // Search recursively
            foreach (Transform t in _gameObject.transform)
            {
                var result = FindChildRecursiveInTransform(t, name);
                if (result != null) return result;
            }
            
            return null;
        }
        
        private GameObject FindChildRecursiveInTransform(Transform parent, string name)
        {
            if (parent.name == name) return parent.gameObject;
            
            foreach (Transform child in parent)
            {
                var result = FindChildRecursiveInTransform(child, name);
                if (result != null) return result;
            }
            
            return null;
        }
    }
}