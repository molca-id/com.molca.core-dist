using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using System;

namespace Molca.App
{
    public class DateField : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI displayText;
        [SerializeField] private TextMeshProUGUI placeholderText;
        [SerializeField] private Button selectButton;
        [SerializeField] private Button clearButton;
        [SerializeField] private Image backgroundImage;
        [SerializeField] private Image borderImage;

        [Header("Settings")]
        [SerializeField] private string placeholder = "Select a date...";
        [SerializeField] private string dateFormat = "MM/dd/yyyy";
        [SerializeField] private DateTime? initialDate = null;
        [SerializeField] private DateTime? minDate = null;
        [SerializeField] private DateTime? maxDate = null;
        [SerializeField] private bool allowClear = true;
        [SerializeField] private bool showTodayButton = true;
        [SerializeField] private bool required = false;
        [SerializeField] private string modalTitle = "Select Date";
        [SerializeField] private string modalDescription = "";

        [Header("Visual Settings")]
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color selectedColor = new Color(0.9f, 0.9f, 1f);
        [SerializeField] private Color errorColor = new Color(1f, 0.9f, 0.9f);
        [SerializeField] private Color disabledColor = new Color(0.8f, 0.8f, 0.8f);
        [SerializeField] private Color placeholderColor = new Color(0.5f, 0.5f, 0.5f);

        [Header("Events")]
        public UnityEvent<DateTime?> onDateChanged = new UnityEvent<DateTime?>();
        public UnityEvent onDateSelected = new UnityEvent();
        public UnityEvent onDateCleared = new UnityEvent();

        private DateTime? selectedDate;
        private bool isInteractable = true;
        private bool hasError = false;

        public DateTime? SelectedDate
        {
            get => selectedDate;
            set
            {
                if (selectedDate != value)
                {
                    selectedDate = value;
                    UpdateDisplay();
                    onDateChanged?.Invoke(selectedDate);
                }
            }
        }

        public bool IsInteractable
        {
            get => isInteractable;
            set
            {
                isInteractable = value;
                UpdateInteractability();
            }
        }

        public bool HasError
        {
            get => hasError;
            set
            {
                hasError = value;
                UpdateVisualState();
            }
        }

        public bool IsValid => !required || selectedDate.HasValue;

        private void Awake()
        {
            SetupButtonListeners();
            InitializeDisplay();
        }

        private void Start()
        {
            // Set initial date if provided
            if (initialDate.HasValue)
            {
                SelectedDate = initialDate.Value;
            }
        }

        private void SetupButtonListeners()
        {
            if (selectButton != null)
                selectButton.onClick.AddListener(OnSelectButtonClicked);
            
            if (clearButton != null)
                clearButton.onClick.AddListener(OnClearButtonClicked);
        }

        private void OnDestroy()
        {
            if (selectButton != null)
                selectButton.onClick.RemoveAllListeners();
            
            if (clearButton != null)
                clearButton.onClick.RemoveAllListeners();
        }

        private void InitializeDisplay()
        {
            if (placeholderText != null)
            {
                placeholderText.text = placeholder;
                placeholderText.color = placeholderColor;
            }

            if (clearButton != null)
                clearButton.gameObject.SetActive(allowClear);

            UpdateDisplay();
            UpdateInteractability();
        }

        private void UpdateDisplay()
        {
            if (displayText != null)
            {
                if (selectedDate.HasValue)
                {
                    displayText.text = selectedDate.Value.ToString(dateFormat);
                    displayText.color = normalColor;
                    
                    if (placeholderText != null)
                        placeholderText.gameObject.SetActive(false);
                }
                else
                {
                    displayText.text = "";
                    
                    if (placeholderText != null)
                        placeholderText.gameObject.SetActive(true);
                }
            }

            // Update clear button visibility
            if (clearButton != null)
                clearButton.gameObject.SetActive(allowClear && selectedDate.HasValue);
        }

        private void UpdateInteractability()
        {
            if (selectButton != null)
                selectButton.interactable = isInteractable;
            
            if (clearButton != null)
                clearButton.interactable = isInteractable && allowClear && selectedDate.HasValue;

            UpdateVisualState();
        }

        private void UpdateVisualState()
        {
            Color targetColor = normalColor;

            if (!isInteractable)
            {
                targetColor = disabledColor;
            }
            else if (hasError)
            {
                targetColor = errorColor;
            }
            else if (selectedDate.HasValue)
            {
                targetColor = selectedColor;
            }

            if (backgroundImage != null)
                backgroundImage.color = targetColor;
        }

        private void OnSelectButtonClicked()
        {
            if (!isInteractable) return;

            DatePickerModal.Show(
                title: modalTitle,
                description: modalDescription,
                initialDate: selectedDate,
                minDate: minDate,
                maxDate: maxDate,
                allowClear: allowClear,
                showTodayButton: showTodayButton,
                confirmText: "OK",
                cancelText: "Cancel",
                onConfirm: (date) => {
                    SelectedDate = date;
                    onDateSelected?.Invoke();
                    HasError = false;
                },
                onCancel: null
            );
        }

        private void OnClearButtonClicked()
        {
            if (!isInteractable) return;

            SelectedDate = null;
            onDateCleared?.Invoke();
            HasError = false;
        }

        /// <summary>
        /// Set the date range for the date picker
        /// </summary>
        public void SetDateRange(DateTime? minDate, DateTime? maxDate)
        {
            this.minDate = minDate;
            this.maxDate = maxDate;
        }

        /// <summary>
        /// Set the minimum selectable date
        /// </summary>
        public void SetMinDate(DateTime? minDate)
        {
            this.minDate = minDate;
        }

        /// <summary>
        /// Set the maximum selectable date
        /// </summary>
        public void SetMaxDate(DateTime? maxDate)
        {
            this.maxDate = maxDate;
        }

        /// <summary>
        /// Set the date format for display
        /// </summary>
        public void SetDateFormat(string format)
        {
            dateFormat = format;
            UpdateDisplay();
        }

        /// <summary>
        /// Set the placeholder text
        /// </summary>
        public void SetPlaceholder(string placeholder)
        {
            this.placeholder = placeholder;
            if (placeholderText != null)
                placeholderText.text = placeholder;
        }

        /// <summary>
        /// Set whether the field is required
        /// </summary>
        public void SetRequired(bool required)
        {
            this.required = required;
        }

        /// <summary>
        /// Set whether clearing is allowed
        /// </summary>
        public void SetAllowClear(bool allowClear)
        {
            this.allowClear = allowClear;
            if (clearButton != null)
                clearButton.gameObject.SetActive(allowClear && selectedDate.HasValue);
        }

        /// <summary>
        /// Set whether to show the today button in the date picker
        /// </summary>
        public void SetShowTodayButton(bool showTodayButton)
        {
            this.showTodayButton = showTodayButton;
        }

        /// <summary>
        /// Set the modal title and description
        /// </summary>
        public void SetModalSettings(string title, string description)
        {
            modalTitle = title;
            modalDescription = description;
        }

        /// <summary>
        /// Clear the selected date
        /// </summary>
        public void Clear()
        {
            SelectedDate = null;
        }

        /// <summary>
        /// Set the selected date
        /// </summary>
        public void SetDate(DateTime? date)
        {
            SelectedDate = date;
        }

        /// <summary>
        /// Get the selected date as a string
        /// </summary>
        public string GetDateString()
        {
            return selectedDate?.ToString(dateFormat) ?? "";
        }

        /// <summary>
        /// Get the selected date as a formatted string
        /// </summary>
        public string GetDateString(string format)
        {
            return selectedDate?.ToString(format) ?? "";
        }

        /// <summary>
        /// Validate the field (useful for forms)
        /// </summary>
        public bool Validate()
        {
            bool isValid = IsValid;
            HasError = !isValid;
            return isValid;
        }

        /// <summary>
        /// Reset the field to its initial state
        /// </summary>
        public void Reset()
        {
            SelectedDate = initialDate;
            HasError = false;
        }

        /// <summary>
        /// Set the field to today's date
        /// </summary>
        public void SetToToday()
        {
            if (IsDateSelectable(DateTime.Today))
            {
                SelectedDate = DateTime.Today;
            }
        }

        /// <summary>
        /// Check if a date is selectable based on min/max constraints
        /// </summary>
        private bool IsDateSelectable(DateTime date)
        {
            if (minDate.HasValue && date.Date < minDate.Value.Date)
                return false;
            
            if (maxDate.HasValue && date.Date > maxDate.Value.Date)
                return false;
            
            return true;
        }

        /// <summary>
        /// Set custom colors for different states
        /// </summary>
        public void SetColors(Color normal, Color selected, Color error, Color disabled)
        {
            normalColor = normal;
            selectedColor = selected;
            errorColor = error;
            disabledColor = disabled;
            UpdateVisualState();
        }

        /// <summary>
        /// Set the placeholder color
        /// </summary>
        public void SetPlaceholderColor(Color color)
        {
            placeholderColor = color;
            if (placeholderText != null)
                placeholderText.color = color;
        }
    }
} 