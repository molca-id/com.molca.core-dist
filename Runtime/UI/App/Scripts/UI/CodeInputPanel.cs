using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Molca;
using Molca.Modals;
using UnityEngine.Events;
using Molca.Localization;

namespace Molca.App.UI
{
    public class CodeInputPanel : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private string modalKey = "NumberInputKeyboard";
        [SerializeField] private string placeholderText = "0";
        [SerializeField] private string expectedCode = "12345";
        [SerializeField] private LocalizedValue title;
        [SerializeField] private LocalizedValue errorMessage;

        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI errorText;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI[] digitFields = new TextMeshProUGUI[5]; // 5 digit fields as shown in image
        [SerializeField] private Button sendButton;
        [SerializeField] private Button cancelButton; // Cancel button

        [Header("Events")]
        public bool autoDismiss = true;
        public UnityEvent onCodeValidated;
        public UnityEvent onCodeCancelled;

        private int codeLength;
        private string currentCode = "";
        private Transform originalParent;
        private Vector3 originalPosition;
        private Button[] digitButtons; // Buttons for each digit field
        private NumberInputKeyboard activeKeyboard;
        private LocalizedValueBinding titleBinding;
        private LocalizedValueBinding errorBinding;

        private void Awake()
        {
            originalParent = transform.parent;
            originalPosition = transform.localPosition;

            codeLength = digitFields.Length;
            digitButtons = new Button[codeLength];
            for (int i = 0; i < codeLength; i++)
            {
                digitButtons[i] = digitFields[i].GetComponentInParent<Button>();
            }
            SetupButtonListeners();
        }

        private async void Start()
        {
            try
            {
                titleBinding = title?.CreateBinding();
                errorBinding = errorMessage?.CreateBinding();
                if (titleBinding != null)
                {
                    titleBinding.ValueChanged += ApplyTitle;
                    await titleBinding.RefreshAsync();
                }
                if (errorBinding != null)
                {
                    errorBinding.ValueChanged += ApplyError;
                    await errorBinding.RefreshAsync();
                }

                if (this == null) return; // destroyed during the awaits

                ApplyTitle(titleBinding?.LastSuccessfulResult ?? title?.String ?? string.Empty);
                ApplyError(errorBinding?.LastSuccessfulResult ?? errorMessage?.String ?? string.Empty);
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

        private void SetupButtonListeners()
        {
            // Setup digit field buttons
            for (int i = 0; i < digitButtons.Length; i++)
            {
                int index = i;
                if (digitButtons[i] != null)
                {
                    digitButtons[i].onClick.AddListener(OnDigitFieldClicked);
                }
            }

            // Setup action buttons
            if (sendButton != null)
                sendButton.onClick.AddListener(OnSendButtonClicked);
            
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelButtonClicked);
        }

        private void OnDestroy()
        {
            if (titleBinding != null)
            {
                titleBinding.ValueChanged -= ApplyTitle;
                titleBinding.Dispose();
                titleBinding = null;
            }
            if (errorBinding != null)
            {
                errorBinding.ValueChanged -= ApplyError;
                errorBinding.Dispose();
                errorBinding = null;
            }
            // Clean up button listeners
            for (int i = 0; i < digitButtons.Length; i++)
            {
                if (digitButtons[i] != null)
                    digitButtons[i].onClick.RemoveAllListeners();
            }

            if (sendButton != null)
                sendButton.onClick.RemoveAllListeners();
            
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
        }

        private void ApplyTitle(string value)
        {
            if (titleText != null)
                titleText.SetText(value);
        }

        private void ApplyError(string value)
        {
            if (errorText != null)
                errorText.SetText(value);
        }

        private void OnDigitFieldClicked()
        {
            // Show the number input keyboard for the specific field
            ShowNumberKeyboard();
        }

        private void ShowNumberKeyboard()
        {
            if (activeKeyboard != null)
            {
                return;
            }

            var modalManager = RuntimeManager.GetSubsystem<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found. Cannot show NumberInputKeyboard.");
                return;
            }

            activeKeyboard = modalManager.ShowModal(modalKey) as NumberInputKeyboard;
            activeKeyboard.Setup(
                titleBinding?.LastSuccessfulResult ?? title?.String ?? string.Empty,
                currentCode,
                false,
                codeLength,
                "OK",
                "Cancel",
                code => OnValueConfirmed(code),
                OnValueCancelled);
            activeKeyboard.onValueChanged += OnValueChanged;

            var parent = activeKeyboard.transform.parent;   
            transform.SetParent(parent);
            transform.localPosition = Vector3.zero;
            transform.localScale = Vector3.one;
            transform.localRotation = Quaternion.identity;
            transform.SetAsFirstSibling();

            LayoutRebuilder.ForceRebuildLayoutImmediate(transform as RectTransform);
        }

        private void ResetParent()
        {
            transform.SetParent(originalParent);
            transform.localPosition = originalPosition;
            transform.localScale = Vector3.one;
            transform.localRotation = Quaternion.identity;
            LayoutRebuilder.ForceRebuildLayoutImmediate(transform as RectTransform);
        }

        private void OnValueConfirmed(string code)
        {
            activeKeyboard = null;
            ResetParent();
        }

        private void OnValueCancelled()
        {
            activeKeyboard = null;
            ResetParent();
        }

        private void OnValueChanged(string code)
        {
            currentCode = code;
            UpdateDigitDisplay();
        }

        private void OnSendButtonClicked()
        {
            if (IsCodeComplete())
            {
                errorText.gameObject.SetActive(false);
                onCodeValidated?.Invoke();

                if (autoDismiss)
                {
                    OnCancelButtonClicked();
                }
            }
            else
            {
                errorText.gameObject.SetActive(true);
            }
        }

        private void OnCancelButtonClicked()
        {
            if (activeKeyboard != null)
            {
                activeKeyboard.Close();
                activeKeyboard = null;
            }

            onCodeCancelled?.Invoke();
            ResetParent();
            gameObject.SetActive(false);
        }

        private void UpdateDigitDisplay()
        {
            for (int i = 0; i < digitFields.Length; i++)
            {
                if (digitFields[i] != null)
                {
                    if (i < currentCode.Length && currentCode[i] != '0')
                    {
                        digitFields[i].text = currentCode[i].ToString();
                    }
                    else
                    {
                        digitFields[i].text = placeholderText;
                    }
                }
            }
        }

        private bool IsCodeComplete()
        {
            return currentCode == expectedCode;
        }

        public void SetCode(string code)
        {
            if (code != null && code.Length <= codeLength)
            {
                currentCode = code.PadRight(codeLength, '0');
                UpdateDigitDisplay();
            }
        }

        public string GetCode()
        {
            return currentCode;
        }

        public void ClearCode()
        {
            currentCode = "";
            UpdateDigitDisplay();
        }
    }
}
