using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    public class ToggleColor : MonoBehaviour
    {
        public Color onColor;
        public Color offColor;

        public UnityEvent<Color> onColorChanged;
        public UnityEvent onStateOn;
        public UnityEvent onStateOff;

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set
            {
                _isOn = value;
                onColorChanged?.Invoke(_isOn ? onColor : offColor);
                if (value) onStateOn?.Invoke();
                else onStateOff?.Invoke();
            }
        }

        public void Toggle()
        {
            IsOn = !_isOn;
        }
    }
}