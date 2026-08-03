using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using Molca;
using Molca.Modals;

namespace Molca.App
{
    public class NumberInputKeyboard : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI inputDisplayText;
        [SerializeField] private Button[] numberButtons = new Button[10]; // 0-9
        [SerializeField] private Button backspaceButton;
        [SerializeField] private Button clearButton;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button decimalButton; // Optional for decimal numbers
        [SerializeField] private Button negativeButton; // Optional for negative numbers

        [Header("Settings")]
        [SerializeField] private string title = "Enter Number";
        [SerializeField] private string initialValue = "";
        [SerializeField] private bool allowDecimals = false;
        [SerializeField] private int maxDigits = 10;
        [SerializeField] private string confirmText = "OK";
        [SerializeField] private string cancelText = "Cancel";

        private string currentInput = "";
        public Action<string> onValueChanged;
        private Action<string> onConfirmCallback;
        private Action onCancelCallback;

        public void Setup(string title, string initialValue, bool allowDecimals, int maxDigits, 
                         string confirmText, string cancelText, 
                         Action<string> onConfirm, Action onCancel = null)
        {
            this.title = title ?? "Enter Number";
            this.initialValue = initialValue ?? "";
            this.allowDecimals = allowDecimals;
            this.maxDigits = Mathf.Max(1, maxDigits);
            this.confirmText = confirmText ?? "OK";
            this.cancelText = cancelText ?? "Cancel";
            
            onConfirmCallback = onConfirm;
            onCancelCallback = onCancel;

            // Initialize UI
            if (titleText != null) titleText.text = this.title;
            if (confirmButton != null)
            {
                var confirmLabel = confirmButton.GetComponentInChildren<TextMeshProUGUI>();
                if (confirmLabel != null) confirmLabel.text = this.confirmText;
            }
            if (cancelButton != null)
            {
                var cancelLabel = cancelButton.GetComponentInChildren<TextMeshProUGUI>();
                if (cancelLabel != null) cancelLabel.text = this.cancelText;
            }

            // Setup decimal button visibility
            if (decimalButton != null)
                decimalButton.gameObject.SetActive(allowDecimals);

            // Set initial value
            SetInputValue(this.initialValue);
        }

        private void Awake()
        {
            SetupButtonListeners();
        }

        private void SetupButtonListeners()
        {
            // Setup number buttons (0-9)
            for (int i = 0; i < numberButtons.Length; i++)
            {
                int number = i;
                if (numberButtons[i] != null)
                {
                    numberButtons[i].onClick.AddListener(() => OnNumberPressed(number));
                }
            }

            // Setup other buttons
            if (backspaceButton != null)
                backspaceButton.onClick.AddListener(OnBackspacePressed);
            
            if (clearButton != null)
                clearButton.onClick.AddListener(OnClearPressed);
            
            if (confirmButton != null)
                confirmButton.onClick.AddListener(OnConfirmPressed);
            
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelPressed);
            
            if (decimalButton != null)
                decimalButton.onClick.AddListener(OnDecimalPressed);

            if (negativeButton != null)
                negativeButton.onClick.AddListener(OnNegativePressed);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy

            // Clean up button listeners
            for (int i = 0; i < numberButtons.Length; i++)
            {
                if (numberButtons[i] != null)
                    numberButtons[i].onClick.RemoveAllListeners();
            }

            if (backspaceButton != null)
                backspaceButton.onClick.RemoveAllListeners();
            
            if (clearButton != null)
                clearButton.onClick.RemoveAllListeners();
            
            if (confirmButton != null)
                confirmButton.onClick.RemoveAllListeners();
            
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
            
            if (decimalButton != null)
                decimalButton.onClick.RemoveAllListeners();

            if (negativeButton != null)
                negativeButton.onClick.RemoveAllListeners();
        }

        private int GetNumericDigitCount()
        {
            // Count only numeric digits (0-9), excluding signs and decimal points
            string numericOnly = currentInput.Replace("-", "").Replace(".", "");
            return numericOnly.Length;
        }

        private void OnNumberPressed(int number)
        {
            if (GetNumericDigitCount() >= maxDigits)
                return;

            currentInput += number.ToString();
            UpdateInputDisplay();
            onValueChanged?.Invoke(currentInput);
        }

        private void OnDecimalPressed()
        {
            if (!allowDecimals || GetNumericDigitCount() >= maxDigits)
                return;

            // Don't allow multiple decimal points
            if (currentInput.Contains("."))
                return;

            // Don't allow decimal as first character
            if (currentInput.Length == 0)
                currentInput = "0";

            currentInput += ".";
            UpdateInputDisplay();
        }

        private void OnNegativePressed()
        {
            // Only block if adding negative sign would exceed digit limit
            // (but allow if we're just toggling an existing negative)
            if (GetNumericDigitCount() >= maxDigits && !currentInput.StartsWith("-"))
                return;

            if (string.IsNullOrEmpty(currentInput) || currentInput == "0")
            {
                currentInput = "-";
            }
            else if (currentInput.StartsWith("-"))
            {
                // Remove negative sign
                currentInput = currentInput.Substring(1);
                // If it becomes empty or just "0", reset to empty
                if (currentInput == "" || currentInput == "0")
                    currentInput = "";
            }
            else
            {
                // Add negative sign at the beginning
                currentInput = "-" + currentInput;
            }

            UpdateInputDisplay();
            onValueChanged?.Invoke(currentInput);
        }

        private void OnBackspacePressed()
        {
            if (currentInput.Length > 0)
            {
                currentInput = currentInput.Substring(0, currentInput.Length - 1);
                UpdateInputDisplay();
                onValueChanged?.Invoke(currentInput);
            }
        }

        private void OnClearPressed()
        {
            currentInput = "";
            UpdateInputDisplay();
            onValueChanged?.Invoke(currentInput);
        }

        private void OnConfirmPressed()
        {
            onConfirmCallback?.Invoke(currentInput);
            Close();
        }

        private void OnCancelPressed()
        {
            onCancelCallback?.Invoke();
            Close();
        }

        private void SetInputValue(string value)
        {
            currentInput = value ?? "";
            UpdateInputDisplay();
            onValueChanged?.Invoke(currentInput);
        }

        private void UpdateInputDisplay()
        {
            if (inputDisplayText != null)
            {
                inputDisplayText.text = string.IsNullOrEmpty(currentInput) ? "0" : currentInput;
            }
        }

        public override void Open(bool showNoButton = true)
        {
            base.Open(showNoButton);
            SetNoButtonVisible(showNoButton);
        }

        public override void SetNoButtonVisible(bool visible)
        {
            if (cancelButton != null)
                cancelButton.gameObject.SetActive(visible);
        }
    }
} 