using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

namespace Molca.App
{
    // Helper class for individual option UI
    public class SelectionOptionUI : MonoBehaviour
    {
        [SerializeField] private Toggle toggle;
        [SerializeField] private TextMeshProUGUI displayText;
        [SerializeField] private TextMeshProUGUI descriptionText;
        [SerializeField] private GameObject disabledOverlay;

        private SelectionModal.SelectionOption option;
        private SelectionModal.SelectionType selectionType;
        private Action<SelectionModal.SelectionOption, bool> onChanged;

        public void Initialize(SelectionModal.SelectionOption option, SelectionModal.SelectionType selectionType, 
                             Action<SelectionModal.SelectionOption, bool> onChanged)
        {
            this.option = option;
            this.selectionType = selectionType;
            this.onChanged = onChanged;

            // Setup toggle
            if (toggle != null)
            {
                toggle.isOn = option.isSelected;
                toggle.interactable = option.isEnabled;
                toggle.onValueChanged.AddListener(OnToggleChanged);
                
                // Set toggle type based on selection type
                var toggleGroup = toggle.GetComponent<ToggleGroup>();
                if (toggleGroup != null)
                {
                    toggleGroup.enabled = (selectionType == SelectionModal.SelectionType.Single);
                }
            }

            // Setup text
            if (displayText != null)
                displayText.text = option.displayText;
            
            if (descriptionText != null)
            {
                descriptionText.text = option.description;
                descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(option.description));
            }

            // Setup disabled state
            if (disabledOverlay != null)
                disabledOverlay.SetActive(!option.isEnabled);
        }

        private void OnToggleChanged(bool isOn)
        {
            onChanged?.Invoke(option, isOn);
        }

        private void OnDestroy()
        {
            if (toggle != null)
                toggle.onValueChanged.RemoveListener(OnToggleChanged);
        }
    }
} 