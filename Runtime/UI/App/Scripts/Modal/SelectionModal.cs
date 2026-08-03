using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca;
using Molca.Modals;

namespace Molca.App
{
    public class SelectionModal : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI descriptionText;
        [SerializeField] private Transform optionsContainer;
        [SerializeField] private GameObject optionPrefab;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button selectAllButton;
        [SerializeField] private Button deselectAllButton;

        [Header("Settings")]
        [SerializeField] private string title = "Select Option";
        [SerializeField] private string description = "";
        [SerializeField] private SelectionType selectionType = SelectionType.Single;
        [SerializeField] private string confirmText = "OK";
        [SerializeField] private string cancelText = "Cancel";
        [SerializeField] private string selectAllText = "Select All";
        [SerializeField] private string deselectAllText = "Deselect All";

        public enum SelectionType
        {
            Single,     // Radio button style - only one selection
            Multiple    // Checkbox style - multiple selections
        }

        private List<SelectionOption> options = new List<SelectionOption>();
        private Action<SelectionOption> onSingleConfirmCallback;
        private Action<List<SelectionOption>> onMultipleConfirmCallback;
        private Action onCancelCallback;

        [System.Serializable]
        public class SelectionOption
        {
            public string id;
            public string displayText;
            public string description;
            public bool isSelected;
            public bool isEnabled = true;
            public object data; // Additional data for the option

            public SelectionOption(string id, string displayText, string description = "", bool isSelected = false, object data = null)
            {
                this.id = id;
                this.displayText = displayText;
                this.description = description;
                this.isSelected = isSelected;
                this.data = data;
            }
        }

        public void Setup(string title, string description, List<SelectionOption> options, 
                         SelectionType selectionType, string confirmText, string cancelText,
                         Action<SelectionOption> onSingleConfirm = null,
                         Action<List<SelectionOption>> onMultipleConfirm = null,
                         Action onCancel = null)
        {
            this.title = title ?? "Select Option";
            this.description = description ?? "";
            this.options = options ?? new List<SelectionOption>();
            this.selectionType = selectionType;
            this.confirmText = confirmText ?? "OK";
            this.cancelText = cancelText ?? "Cancel";
            
            onSingleConfirmCallback = onSingleConfirm;
            onMultipleConfirmCallback = onMultipleConfirm;
            onCancelCallback = onCancel;

            // Initialize UI
            if (titleText != null) titleText.text = this.title;
            if (descriptionText != null) 
            {
                descriptionText.text = this.description;
                descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(this.description));
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

            if (selectAllButton != null)
            {
                var selectAllLabel = selectAllButton.GetComponentInChildren<TextMeshProUGUI>();
                if (selectAllLabel != null) selectAllLabel.text = selectAllText;
                selectAllButton.gameObject.SetActive(selectionType == SelectionType.Multiple);
            }

            if (deselectAllButton != null)
            {
                var deselectAllLabel = deselectAllButton.GetComponentInChildren<TextMeshProUGUI>();
                if (deselectAllLabel != null) deselectAllLabel.text = deselectAllText;
                deselectAllButton.gameObject.SetActive(selectionType == SelectionType.Multiple);
            }

            CreateOptions();
            UpdateConfirmButtonState();
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
            
            if (selectAllButton != null)
                selectAllButton.onClick.AddListener(OnSelectAllPressed);
            
            if (deselectAllButton != null)
                deselectAllButton.onClick.AddListener(OnDeselectAllPressed);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy
            if (confirmButton != null)
                confirmButton.onClick.RemoveAllListeners();
            
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
            
            if (selectAllButton != null)
                selectAllButton.onClick.RemoveAllListeners();
            
            if (deselectAllButton != null)
                deselectAllButton.onClick.RemoveAllListeners();
        }

        private void CreateOptions()
        {
            if (optionsContainer == null || optionPrefab == null)
                return;

            // Clear existing options
            foreach (Transform child in optionsContainer)
            {
                Destroy(child.gameObject);
            }

            // Create new options
            foreach (var option in options)
            {
                var optionGO = Instantiate(optionPrefab, optionsContainer);
                var optionUI = optionGO.GetComponent<SelectionOptionUI>();
                
                if (optionUI != null)
                {
                    optionUI.Initialize(option, selectionType, OnOptionChanged);
                }
            }
        }

        private void OnOptionChanged(SelectionOption option, bool isSelected)
        {
            option.isSelected = isSelected;

            // For single selection, deselect others
            if (selectionType == SelectionType.Single && isSelected)
            {
                foreach (var otherOption in options)
                {
                    if (otherOption != option)
                        otherOption.isSelected = false;
                }
                
                // Refresh UI
                CreateOptions();
            }

            UpdateConfirmButtonState();
        }

        private void OnConfirmPressed()
        {
            if (selectionType == SelectionType.Single)
            {
                var selectedOption = options.FirstOrDefault(o => o.isSelected);
                onSingleConfirmCallback?.Invoke(selectedOption);
            }
            else
            {
                var selectedOptions = options.Where(o => o.isSelected).ToList();
                onMultipleConfirmCallback?.Invoke(selectedOptions);
            }
            
            Close();
        }

        private void OnCancelPressed()
        {
            onCancelCallback?.Invoke();
            Close();
        }

        private void OnSelectAllPressed()
        {
            foreach (var option in options.Where(o => o.isEnabled))
            {
                option.isSelected = true;
            }
            CreateOptions();
            UpdateConfirmButtonState();
        }

        private void OnDeselectAllPressed()
        {
            foreach (var option in options)
            {
                option.isSelected = false;
            }
            CreateOptions();
            UpdateConfirmButtonState();
        }

        private void UpdateConfirmButtonState()
        {
            if (confirmButton != null)
            {
                bool hasSelection = options.Any(o => o.isSelected);
                confirmButton.interactable = hasSelection;
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

        /// <summary>
        /// Static method to show single selection modal
        /// </summary>
        public static SelectionModal ShowSingle(string title, string description, List<SelectionOption> options,
                                               string confirmText = "OK", string cancelText = "Cancel",
                                               Action<SelectionOption> onConfirm = null, Action onCancel = null)
        {
            return Show(title, description, options, SelectionType.Single, confirmText, cancelText, onConfirm, null, onCancel);
        }

        /// <summary>
        /// Static method to show multiple selection modal
        /// </summary>
        public static SelectionModal ShowMultiple(string title, string description, List<SelectionOption> options,
                                                 string confirmText = "OK", string cancelText = "Cancel",
                                                 Action<List<SelectionOption>> onConfirm = null, Action onCancel = null)
        {
            return Show(title, description, options, SelectionType.Multiple, confirmText, cancelText, null, onConfirm, onCancel);
        }

        /// <summary>
        /// Static method to show the selection modal
        /// </summary>
        private static SelectionModal Show(string title, string description, List<SelectionOption> options,
                                          SelectionType selectionType, string confirmText, string cancelText,
                                          Action<SelectionOption> onSingleConfirm = null,
                                          Action<List<SelectionOption>> onMultipleConfirm = null,
                                          Action onCancel = null)
        {
            var modalManager = RuntimeManager.GetSubsystem<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found. Cannot show SelectionModal.");
                return null;
            }

            var modalPrefab = Resources.Load<SelectionModal>("SelectionModal");
            if (modalPrefab == null)
            {
                Debug.LogError("SelectionModal prefab not found in Resources folder.");
                return null;
            }

            var modal = modalManager.ShowModal(modalPrefab) as SelectionModal;
            if (modal != null)
            {
                modal.Setup(title, description, options, selectionType, confirmText, cancelText, 
                           onSingleConfirm, onMultipleConfirm, onCancel);
            }

            return modal;
        }
    }
} 