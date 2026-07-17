using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Events;

namespace Molca.Sequence
{
    public class InputStep : Step
    {
        [Header("Input Settings")]
        [SerializeField] private InputActionReference inputAction;
        [SerializeField] private bool requirePerformed = true;
        
        [Header("Events")]
        [SerializeField] private UnityEvent OnInputPerformed;
        
        private bool _inputPerformed = false;
        
        protected override void OnStepActivated()
        {
            // Subscribe to input action events when step becomes active
            if (inputAction != null && inputAction.action != null)
            {
                if (requirePerformed)
                {
                    inputAction.action.performed += OnActionPerformed;
                }
                else
                {
                    inputAction.action.started += OnActionStarted;
                }
            }
        }
        
        protected override void OnStepDeactivated()
        {
            // Unsubscribe from input action events when step becomes inactive
            if (inputAction != null && inputAction.action != null)
            {
                if (requirePerformed)
                {
                    inputAction.action.performed -= OnActionPerformed;
                }
                else
                {
                    inputAction.action.started -= OnActionStarted;
                }
            }
            
            // Reset input performed flag
            _inputPerformed = false;
        }
        
        private void OnActionPerformed(InputAction.CallbackContext context)
        {
            if (CurrentStatus == StepStatus.Active && !_inputPerformed)
            {
                _inputPerformed = true;
                OnInputPerformed?.Invoke();
                Complete();
            }
        }
        
        private void OnActionStarted(InputAction.CallbackContext context)
        {
            if (CurrentStatus == StepStatus.Active && !_inputPerformed)
            {
                _inputPerformed = true;
                OnInputPerformed?.Invoke();
                Complete();
            }
        }
        
        protected override bool CanComplete()
        {
            // Complete() is only called from the input handlers, which set
            // _inputPerformed first — so the gate opens exactly when input arrived.
            return _inputPerformed;
        }
        
        protected override void OnDestroy()
        {
            // Clean up event subscriptions
            if (inputAction != null && inputAction.action != null)
            {
                inputAction.action.performed -= OnActionPerformed;
                inputAction.action.started -= OnActionStarted;
                inputAction.action.Disable();
            }
            base.OnDestroy();
        }
    }
} 