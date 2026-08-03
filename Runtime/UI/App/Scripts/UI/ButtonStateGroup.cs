using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Molca;

namespace Molca.App.UI
{
    public class ButtonStateGroup : MonoBehaviour
    {
        [SerializeField]
        private bool allowSwitchOff;

        private List<ButtonState> _buttons = new List<ButtonState>();

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();
                foreach (var e in GetComponentsInChildren<ButtonState>(true))
                    if(!e.exludeFromGroup)
                        Register(e);
                EnsureValidState();
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

        public void Register(ButtonState buttonState)
        {
            if (_buttons.Contains(buttonState))
                return;

            buttonState.onClicked += NotifyButtonStateChanged;
            _buttons.Add(buttonState);
            EnsureValidState();
        }

        public void Unregister(ButtonState buttonState)
        {
            if (!_buttons.Contains(buttonState))
                return;

            buttonState.onClicked -= NotifyButtonStateChanged;
            _buttons.Remove(buttonState);
            EnsureValidState();
        }

        private void NotifyButtonStateChanged(ButtonState buttonState)
        {
            if(!buttonState.isOn)
            {
                if(!allowSwitchOff)
                    buttonState.isOn = true;
                return;
            }

            for (var i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i] == buttonState)
                    continue;

                _buttons[i].isOn = false;
            }
        }

        private void EnsureValidState()
        {
            if (!allowSwitchOff && !AnyButtonOn() && _buttons.Count != 0)
            {
                _buttons[0].isOn = true;
                NotifyButtonStateChanged(_buttons[0]);
            }

            IEnumerable<ButtonState> activeToggles = ActiveButtons();

            if (activeToggles.Count() > 1)
            {
                ButtonState firstActive = GetFirstActiveButton();

                foreach (ButtonState button in activeToggles)
                {
                    if (button == firstActive)
                    {
                        continue;
                    }
                    button.isOn = false;
                }
            }
        }

        private bool AnyButtonOn()
        {
            return _buttons.Find(x => x.isOn) != null;
        }

        public IEnumerable<ButtonState> ActiveButtons()
        {
            return _buttons.Where(x => x.isOn);
        }

        public ButtonState GetFirstActiveButton()
        {
            IEnumerable<ButtonState> activeToggles = ActiveButtons();
            return activeToggles.Count() > 0 ? activeToggles.First() : null;
        }

        public void SetAllButtonsOff()
        {
            bool oldAllowSwitchOff = allowSwitchOff;
            allowSwitchOff = true;

            for (var i = 0; i < _buttons.Count; i++)
                _buttons[i].isOn = false;

            allowSwitchOff = oldAllowSwitchOff;
        }
    }
}