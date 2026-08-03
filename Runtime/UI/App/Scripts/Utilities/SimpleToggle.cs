using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    public class SimpleToggle : MonoBehaviour
    {
        public bool state;
        public bool callEventOnAwake;

        public UnityEvent<bool> onStateChanged;
        public UnityEvent<bool> onStateChangedInvert;
        public UnityEvent onStateTrue;
        public UnityEvent onStateFalse;

        private void Awake()
        {
            if (callEventOnAwake)
                CallEvent();
        }

        public void Toggle(bool callEvent = true)
        {
            SetState(!state, callEvent);
        }

        public void SetState(bool value, bool callEvent = true)
        {
            if (state == value)
                return;

            state = value;
            CallEvent();
        }

        private void CallEvent()
        {
            onStateChanged?.Invoke(state);
            onStateChangedInvert?.Invoke(!state);
            if (state)
                onStateTrue?.Invoke();
            else
                onStateFalse?.Invoke();
        }
    }
}