using System.Linq;
using UnityEngine;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Toggles GameObjects when a step begins or ends.
    /// Attach this to the same GameObject as a Step component for automatic event hooking.
    /// </summary>
    [AuxiliaryMenu("Utility/GameObject Toggler")]
    public class StepGameObjectToggler : StepAuxiliary
    {
        [Header("GameObject Toggle Settings")]
        [Tooltip("GameObjects to enable when the step begins.")]
        public GameObject[] enableOnBegin;
        
        [Tooltip("GameObjects to disable when the step begins.")]
        public GameObject[] disableOnBegin;
        
        [Tooltip("GameObjects to enable when the step ends.")]
        public GameObject[] enableOnEnd;
        
        [Tooltip("GameObjects to disable when the step ends.")]
        public GameObject[] disableOnEnd;
        
        [Header("Toggle Options")]
        [Tooltip("If true, will also toggle GameObjects when the step is reset.")]
        public bool toggleOnReset = true;
        
        [Tooltip("If true, will restore original states when the step ends.")]
        public bool restoreOnEnd = false;
        
        private bool[] _originalStates;
        private GameObject[] _allTrackedObjects;
        
        private void Awake()
        {
            // Collect all tracked objects and their original states
            CollectTrackedObjects();
        }
        
        public override void OnStepBegin()
        {
            ToggleGameObjects(enableOnBegin, true);
            ToggleGameObjects(disableOnBegin, false);
        }
        
        public override void OnStepCompleted()
        {
            if (restoreOnEnd)
            {
                RestoreOriginalStates();
            }
            else
            {
                ToggleGameObjects(enableOnEnd, true);
                ToggleGameObjects(disableOnEnd, false);
            }
        }
        
        public override void OnStepReset()
        {
            if (toggleOnReset)
            {
                RestoreOriginalStates();
            }
        }
        
        private void CollectTrackedObjects()
        {
            // Combine all arrays and remove duplicates
            var allObjects = new System.Collections.Generic.List<GameObject>();
            
            if (enableOnBegin != null) allObjects.AddRange(enableOnBegin);
            if (disableOnBegin != null) allObjects.AddRange(disableOnBegin);
            if (enableOnEnd != null) allObjects.AddRange(enableOnEnd);
            if (disableOnEnd != null) allObjects.AddRange(disableOnEnd);
            
            // Remove duplicates and null entries
            _allTrackedObjects = allObjects.Distinct().Where(obj => obj != null).ToArray();
            
            // Store original states
            _originalStates = new bool[_allTrackedObjects.Length];
            for (int i = 0; i < _allTrackedObjects.Length; i++)
            {
                _originalStates[i] = _allTrackedObjects[i].activeSelf;
            }
        }
        
        private void ToggleGameObjects(GameObject[] objects, bool state)
        {
            if (objects == null) return;
            
            foreach (var obj in objects)
            {
                if (obj != null)
                {
                    obj.SetActive(state);
                }
            }
        }
        
        private void RestoreOriginalStates()
        {
            if (_allTrackedObjects == null || _originalStates == null) return;
            
            for (int i = 0; i < _allTrackedObjects.Length && i < _originalStates.Length; i++)
            {
                if (_allTrackedObjects[i] != null)
                {
                    _allTrackedObjects[i].SetActive(_originalStates[i]);
                }
            }
        }
        
        /// <summary>
        /// Manually reset the toggler to original states.
        /// </summary>
        public void ResetToggler()
        {
            RestoreOriginalStates();
        }
        
        /// <summary>
        /// Manually toggle specific GameObjects.
        /// </summary>
        public void ToggleObjects(GameObject[] objects, bool state)
        {
            ToggleGameObjects(objects, state);
        }
    }
} 