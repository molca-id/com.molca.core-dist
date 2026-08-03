using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Molca.App.Utilities
{
    public class InputHelper : MonoBehaviour
    {
        [SerializeField] private InputActionReference inputAction;
        [SerializeField] private UnityEvent onInputActionStarted;
        [SerializeField] private UnityEvent onInputActionPerformed;
        [SerializeField] private UnityEvent onInputActionCanceled;

        private void Awake()
        {
            inputAction.action.started += OnInputActionStarted;
            inputAction.action.performed += OnInputActionPerformed;
            inputAction.action.canceled += OnInputActionCanceled;
        }

        private void OnInputActionStarted(InputAction.CallbackContext context)
        {
            onInputActionStarted?.Invoke();
        }

        private void OnInputActionPerformed(InputAction.CallbackContext context)
        {
            onInputActionPerformed?.Invoke();
        }

        private void OnInputActionCanceled(InputAction.CallbackContext context)
        {
            onInputActionCanceled?.Invoke();
        }

        private void OnDestroy()
        {
            inputAction.action.started -= OnInputActionStarted;
            inputAction.action.performed -= OnInputActionPerformed;
            inputAction.action.canceled -= OnInputActionCanceled;
        }
    }
}