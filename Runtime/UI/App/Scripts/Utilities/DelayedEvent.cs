using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    public class DelayedEvent : MonoBehaviour
    {
        [Tooltip("If true, the event will be invoked on enable")]
        [SerializeField] private bool invokeOnEnable = false;
        [Tooltip("The delay before the event is invoked")]
        [SerializeField] private float delay = 0f;
        [Tooltip("If true, destroy the component after the event is invoked")]
        [SerializeField] private bool destroyAfterInvoke = false;
        [Tooltip("If true, destroy the game object instead of the component after the event is invoked")]
        [SerializeField] private bool destroyGameObject = false;

        public UnityEvent onDelayedEvent;

        private void OnEnable()
        {
            if (invokeOnEnable)
            {
                InvokeEvent();
            }
        }

        public async void InvokeEvent()
        {
            try
            {
                await Awaitable.WaitForSecondsAsync(delay);
                onDelayedEvent.Invoke();
                if (destroyAfterInvoke)
                {
                    Destroy(destroyGameObject ? gameObject : this);
                }
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}