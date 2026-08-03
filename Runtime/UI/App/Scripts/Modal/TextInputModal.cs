using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using Molca;
using Molca.Modals;

namespace Molca.App
{
    public class TextInputModal : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI descriptionText;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private TextMeshProUGUI errorText;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button clearButton;

        [Header("Settings")]
        [SerializeField] private string title = "Enter Text";
        [SerializeField] private string description = "";
        [SerializeField] private string placeholder = "Enter text here...";
        [SerializeField] private string initialValue = "";
        [SerializeField] private int maxLength = 100;
        [SerializeField] private bool allowEmpty = false;
        [SerializeField] private string confirmText = "OK";
        [SerializeField] private string cancelText = "Cancel";
        [SerializeField] private string clearText = "Clear";

        private Action<string> onConfirmCallback;
        private Action onCancelCallback;
        private Func<string, bool> validationCallback;

        public void Setup(string title, string description, string placeholder, string initialValue,
                         int maxLength, bool allowEmpty, string confirmText, string cancelText,
                         Action<string> onConfirm, Action onCancel = null, Func<string, bool> validation = null)
        {
            this.title = title ?? "Enter Text";
            this.description = description ?? "";
            this.placeholder = placeholder ?? "Enter text here...";
            this.initialValue = initialValue ?? "";
            this.maxLength = Mathf.Max(1, maxLength);
            this.allowEmpty = allowEmpty;
            this.confirmText = confirmText ?? "OK";
            this.cancelText = cancelText ?? "Cancel";
            
            onConfirmCallback = onConfirm;
            onCancelCallback = onCancel;
            validationCallback = validation;

            // Initialize UI
            if (titleText != null) titleText.text = this.title;
            if (descriptionText != null) 
            {
                descriptionText.text = this.description;
                descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(this.description));
            }
            
            if (inputField != null)
            {
                inputField.characterLimit = this.maxLength;
                inputField.text = this.initialValue;
                
                // Set placeholder
                if (inputField.placeholder != null)
                {
                    var placeholderText = inputField.placeholder as TextMeshProUGUI;
                    if (placeholderText != null)
                        placeholderText.text = this.placeholder;
                }
            }

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

            if (clearButton != null)
            {
                var clearLabel = clearButton.GetComponentInChildren<TextMeshProUGUI>();
                if (clearLabel != null) clearLabel.text = clearText;
            }

            // Clear error text
            if (errorText != null) errorText.text = "";

            // Setup input validation
            if (inputField != null)
            {
                inputField.onValueChanged.AddListener(OnInputChanged);
                inputField.onSubmit.AddListener(OnSubmit);
            }
        }

        private void Awake()
        {
            SetupButtonListeners();
        }

        private void SetupButtonListeners()
        {
            if (confirmButton != null)
                confirmButton.onClick.AddListener(OnConfirmPressed);
            
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelPressed);
            
            if (clearButton != null)
                clearButton.onClick.AddListener(OnClearPressed);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy
            if (inputField != null)
            {
                inputField.onValueChanged.RemoveListener(OnInputChanged);
                inputField.onSubmit.RemoveListener(OnSubmit);
            }

            if (confirmButton != null)
                confirmButton.onClick.RemoveAllListeners();
            
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
            
            if (clearButton != null)
                clearButton.onClick.RemoveAllListeners();
        }

        private void OnInputChanged(string value)
        {
            // Clear error when user starts typing
            if (errorText != null && !string.IsNullOrEmpty(errorText.text))
                errorText.text = "";

            // Update confirm button state
            UpdateConfirmButtonState();
        }

        private void OnSubmit(string value)
        {
            OnConfirmPressed();
        }

        private void OnConfirmPressed()
        {
            var inputValue = inputField != null ? inputField.text : "";

            // Validate input
            if (!ValidateInput(inputValue))
                return;

            onConfirmCallback?.Invoke(inputValue);
            Close();
        }

        private void OnCancelPressed()
        {
            onCancelCallback?.Invoke();
            Close();
        }

        private void OnClearPressed()
        {
            if (inputField != null)
                inputField.text = "";
        }

        private bool ValidateInput(string input)
        {
            // Check if empty is allowed
            if (!allowEmpty && string.IsNullOrWhiteSpace(input))
            {
                ShowError("Please enter some text.");
                return false;
            }

            // Check max length
            if (input.Length > maxLength)
            {
                ShowError($"Text is too long. Maximum {maxLength} characters allowed.");
                return false;
            }

            // Custom validation
            if (validationCallback != null && !validationCallback(input))
            {
                ShowError("Invalid input. Please check your text.");
                return false;
            }

            return true;
        }

        private void ShowError(string errorMessage)
        {
            if (errorText != null)
                errorText.text = errorMessage;
        }

        private void UpdateConfirmButtonState()
        {
            if (confirmButton != null)
            {
                var inputValue = inputField != null ? inputField.text : "";
                bool isValid = allowEmpty || !string.IsNullOrWhiteSpace(inputValue);
                
                if (validationCallback != null)
                    isValid = isValid && validationCallback(inputValue);

                confirmButton.interactable = isValid;
            }
        }

        public override void Open(bool showNoButton = true)
        {
            base.Open(showNoButton);
            SetNoButtonVisible(showNoButton);
            
            // Focus on input field
            if (inputField != null)
            {
                inputField.Select();
                inputField.ActivateInputField();
            }
        }

        public override void SetNoButtonVisible(bool visible)
        {
            if (cancelButton != null)
                cancelButton.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Static method to show the text input modal
        /// </summary>
        public static TextInputModal Show(string title = "Enter Text", 
                                        string description = "",
                                        string placeholder = "Enter text here...",
                                        string initialValue = "",
                                        int maxLength = 100,
                                        bool allowEmpty = false,
                                        string confirmText = "OK", 
                                        string cancelText = "Cancel",
                                        Action<string> onConfirm = null, 
                                        Action onCancel = null,
                                        Func<string, bool> validation = null)
        {
            var modalManager = RuntimeManager.GetSubsystem<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found. Cannot show TextInputModal.");
                return null;
            }

            var modalPrefab = Resources.Load<TextInputModal>("TextInputModal");
            if (modalPrefab == null)
            {
                Debug.LogError("TextInputModal prefab not found in Resources folder.");
                return null;
            }

            var modal = modalManager.ShowModal(modalPrefab) as TextInputModal;
            if (modal != null)
            {
                modal.Setup(title, description, placeholder, initialValue, maxLength, allowEmpty, 
                           confirmText, cancelText, onConfirm, onCancel, validation);
            }

            return modal;
        }
    }
} 