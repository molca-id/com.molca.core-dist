using System.Collections.Generic;
using System.Linq;
using Molca;
using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.UI
{
    /// <summary>
    /// Manages a group of ColorIDButton toggles for radio/multi toggle behavior
    /// </summary>
    [AddComponentMenu("Molca/UI/Color ID Button Group")]
    public class ColorIDButtonGroup : MonoBehaviour
    {
        [Header("Group Configuration")]
        [SerializeField] private bool allowMultipleSelection = false;
        [SerializeField] private bool allowSwitchOff = true;
        [SerializeField] private bool requireSelection = true;

        [Header("Group Events")]
        public UnityEvent<ColorIDButton> onButtonToggled;
        public UnityEvent<List<ColorIDButton>> onSelectionChanged;

        private List<ColorIDButton> buttons = new List<ColorIDButton>();

        public bool AllowMultipleSelection => allowMultipleSelection;
        public bool AllowSwitchOff => allowSwitchOff;
        public bool RequireSelection => requireSelection;

        public List<ColorIDButton> Buttons => buttons.ToList();
        public List<ColorIDButton> ActiveButtons => buttons.Where(b => b.IsOn).ToList();
        public ColorIDButton FirstActiveButton => buttons.FirstOrDefault(b => b.IsOn);

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();

                RegisterButtons();
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

        private void OnDestroy()
        {
            UnregisterAllButtons();
        }

        /// <summary>
        /// Registers all ColorIDButton children with this group
        /// </summary>
        private void RegisterButtons()
        {
            // Clear existing buttons
            UnregisterAllButtons();
            
            // Find all ColorIDButton components in children
            var foundButtons = GetComponentsInChildren<ColorIDButton>(true);
            
            foreach (var button in foundButtons)
            {
                if (button.IsToggleButton && !button.ExcludeFromGroup)
                {
                    RegisterButton(button);
                }
            }
        }

        /// <summary>
        /// Registers a single button with this group
        /// </summary>
        public void RegisterButton(ColorIDButton button)
        {
            if (button == null || !button.IsToggleButton || button.ExcludeFromGroup) return;
            
            if (!buttons.Contains(button))
            {
                buttons.Add(button);
                button.RegisterWithGroup(this);
            }
        }

        /// <summary>
        /// Unregisters a button from this group
        /// </summary>
        public void UnregisterButton(ColorIDButton button)
        {
            if (button == null) return;
            
            if (buttons.Contains(button))
            {
                buttons.Remove(button);
                button.UnregisterFromGroup();
            }
        }

        /// <summary>
        /// Unregisters all buttons from this group
        /// </summary>
        private void UnregisterAllButtons()
        {
            foreach (var button in buttons)
            {
                if (button != null)
                {
                    button.UnregisterFromGroup();
                }
            }
            buttons.Clear();
        }

        /// <summary>
        /// Called when a button in the group is toggled
        /// </summary>
        internal void OnButtonToggled(ColorIDButton toggledButton)
        {
            if (toggledButton == null || !buttons.Contains(toggledButton)) return;

            // Handle radio button behavior (single selection)
            if (!allowMultipleSelection)
            {
                // Turn off all other buttons
                foreach (var button in buttons)
                {
                    if (button != toggledButton && button.IsOn)
                    {
                        button.SetToggleState(false, false);
                    }
                }
            }

            // Handle switch off behavior
            if (!allowSwitchOff && !toggledButton.IsOn)
            {
                toggledButton.SetToggleState(true, false);
                return;
            }

            // Ensure at least one button is selected if required
            if (requireSelection && !allowMultipleSelection && !AnyButtonOn())
            {
                toggledButton.SetToggleState(true, false);
                return;
            }

            // Invoke events
            onButtonToggled?.Invoke(toggledButton);
            onSelectionChanged?.Invoke(ActiveButtons);
        }

        /// <summary>
        /// Ensures the group has a valid state on startup
        /// </summary>
        private void EnsureValidState()
        {
            if (buttons.Count == 0) return;

            // If multiple selection is not allowed and no button is on, select the first one
            if (!allowMultipleSelection && requireSelection && !AnyButtonOn())
            {
                buttons[0].SetToggleState(true, false);
            }

            // If multiple selection is not allowed and multiple buttons are on, keep only the first one
            if (!allowMultipleSelection)
            {
                var activeButtons = ActiveButtons;
                if (activeButtons.Count > 1)
                {
                    for (int i = 1; i < activeButtons.Count; i++)
                    {
                        activeButtons[i].SetToggleState(false, false);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if any button in the group is currently on
        /// </summary>
        private bool AnyButtonOn()
        {
            return buttons.Any(b => b.IsOn);
        }

        /// <summary>
        /// Sets all buttons in the group to off
        /// </summary>
        public void SetAllButtonsOff()
        {
            if (!allowSwitchOff) return;

            bool oldRequireSelection = requireSelection;
            requireSelection = false;

            foreach (var button in buttons)
            {
                button.SetToggleState(false, false);
            }

            requireSelection = oldRequireSelection;
            onSelectionChanged?.Invoke(ActiveButtons);
        }

        /// <summary>
        /// Sets all buttons in the group to on (only works with multiple selection)
        /// </summary>
        public void SetAllButtonsOn()
        {
            if (!allowMultipleSelection) return;

            foreach (var button in buttons)
            {
                button.SetToggleState(true, false);
            }

            onSelectionChanged?.Invoke(ActiveButtons);
        }

        /// <summary>
        /// Gets the first active button (useful for radio button groups)
        /// </summary>
        public ColorIDButton GetFirstActiveButton()
        {
            return FirstActiveButton;
        }

        /// <summary>
        /// Gets all active buttons (useful for multi-select groups)
        /// </summary>
        public List<ColorIDButton> GetActiveButtons()
        {
            return ActiveButtons;
        }

        /// <summary>
        /// Refreshes the button group (re-registers all buttons)
        /// </summary>
        [ContextMenu("Refresh Button Group")]
        public void RefreshButtonGroup()
        {
            RegisterButtons();
            EnsureValidState();
        }
    }
} 