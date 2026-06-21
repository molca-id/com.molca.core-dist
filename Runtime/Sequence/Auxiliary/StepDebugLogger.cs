using UnityEngine;
using Molca.Sequence.Auxiliary;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Example auxiliary that logs step events for debugging purposes.
    /// Demonstrates the new auxiliary system capabilities.
    /// </summary>
    [AuxiliaryMenu("Utility/Debug Logger")]
    public class StepDebugLogger : StepAuxiliary
    {
        [Header("Debug Settings")]
        [SerializeField] private bool logStepBegin = true;
        [SerializeField] private bool logStepEnd = true;
        [SerializeField] private bool logStepCompleted = true;
        [SerializeField] private bool logStepReset = true;
        [SerializeField] private string customPrefix = "";
        
        private string _stepName;
        private string _auxiliaryName;
        
        protected override void OnInitialize()
        {
            _stepName = Step != null ? Step.name : "Unknown";
            _auxiliaryName = GetType().Name;
            
            Debug.Log($"[{_auxiliaryName}] Initialized for step: {_stepName}");
        }
        
        public override void OnStepBegin()
        {
            if (!logStepBegin) return;
            
            string prefix = string.IsNullOrEmpty(customPrefix) ? "" : $"[{customPrefix}] ";
            Debug.Log($"{prefix}[{_auxiliaryName}] Step Begin: {_stepName}", Step);
        }
        
        public override void OnStepEnd()
        {
            if (!logStepEnd) return;
            
            string prefix = string.IsNullOrEmpty(customPrefix) ? "" : $"[{customPrefix}] ";
            Debug.Log($"{prefix}[{_auxiliaryName}] Step End: {_stepName}", Step);
        }
        
        public override void OnStepCompleted()
        {
            if (!logStepCompleted) return;
            
            string prefix = string.IsNullOrEmpty(customPrefix) ? "" : $"[{customPrefix}] ";
            Debug.Log($"{prefix}[{_auxiliaryName}] Step Completed: {_stepName}", Step);
        }
        
        public override void OnStepReset()
        {
            if (!logStepReset) return;
            
            string prefix = string.IsNullOrEmpty(customPrefix) ? "" : $"[{customPrefix}] ";
            Debug.Log($"{prefix}[{_auxiliaryName}] Step Reset: {_stepName}", Step);
        }
    }
}
