using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;
using Molca;
using Molca.Modals;

namespace Molca.App
{
    public class DatePickerModal : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI descriptionText;
        [SerializeField] private TextMeshProUGUI monthYearText;
        [SerializeField] private Button previousMonthButton;
        [SerializeField] private Button nextMonthButton;
        [SerializeField] private Transform calendarGrid;
        [SerializeField] private GameObject dayButtonPrefab;
        [SerializeField] private TextMeshProUGUI errorText;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button todayButton;
        [SerializeField] private Button clearButton;

        [Header("Settings")]
        [SerializeField] private string title = "Select Date";
        [SerializeField] private string description = "";
        [SerializeField] private string confirmText = "OK";
        [SerializeField] private string cancelText = "Cancel";
        [SerializeField] private string todayText = "Today";
        [SerializeField] private string clearText = "Clear";
        [SerializeField] private DateTime? initialDate = null;
        [SerializeField] private DateTime? minDate = null;
        [SerializeField] private DateTime? maxDate = null;
        [SerializeField] private bool allowClear = true;
        [SerializeField] private bool showTodayButton = true;

        private Action<DateTime?> onConfirmCallback;
        private Action onCancelCallback;
        private Func<DateTime, bool> validationCallback;
        
        private DateTime currentMonth;
        private DateTime? selectedDate;
        private List<Button> dayButtons = new List<Button>();

        public void Setup(string title, string description, DateTime? initialDate, DateTime? minDate, DateTime? maxDate,
                         bool allowClear, bool showTodayButton, string confirmText, string cancelText,
                         Action<DateTime?> onConfirm, Action onCancel = null, Func<DateTime, bool> validation = null)
        {
            this.title = title ?? "Select Date";
            this.description = description ?? "";
            this.initialDate = initialDate;
            this.minDate = minDate;
            this.maxDate = maxDate;
            this.allowClear = allowClear;
            this.showTodayButton = showTodayButton;
            this.confirmText = confirmText ?? "OK";
            this.cancelText = cancelText ?? "Cancel";
            
            onConfirmCallback = onConfirm;
            onCancelCallback = onCancel;
            validationCallback = validation;

            // Initialize current month and selected date
            currentMonth = initialDate?.Date ?? DateTime.Today;
            selectedDate = initialDate;

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

            if (todayButton != null)
            {
                var todayLabel = todayButton.GetComponentInChildren<TextMeshProUGUI>();
                if (todayLabel != null) todayLabel.text = todayText;
                todayButton.gameObject.SetActive(showTodayButton);
            }

            if (clearButton != null)
            {
                var clearLabel = clearButton.GetComponentInChildren<TextMeshProUGUI>();
                if (clearLabel != null) clearLabel.text = clearText;
                clearButton.gameObject.SetActive(allowClear);
            }

            // Clear error text
            if (errorText != null) errorText.text = "";

            // Generate calendar
            GenerateCalendar();
        }

        private void Awake()
        {
            SetupButtonListeners();
        }

        private void SetupButtonListeners()
        {
            if (previousMonthButton != null)
                previousMonthButton.onClick.AddListener(OnPreviousMonth);
            
            if (nextMonthButton != null)
                nextMonthButton.onClick.AddListener(OnNextMonth);
            
            if (confirmButton != null)
                confirmButton.onClick.AddListener(OnConfirmPressed);
            
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelPressed);
            
            if (todayButton != null)
                todayButton.onClick.AddListener(OnTodayPressed);
            
            if (clearButton != null)
                clearButton.onClick.AddListener(OnClearPressed);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy
            if (previousMonthButton != null)
                previousMonthButton.onClick.RemoveAllListeners();
            
            if (nextMonthButton != null)
                nextMonthButton.onClick.RemoveAllListeners();
            
            if (confirmButton != null)
                confirmButton.onClick.RemoveAllListeners();
            
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
            
            if (todayButton != null)
                todayButton.onClick.RemoveAllListeners();
            
            if (clearButton != null)
                clearButton.onClick.RemoveAllListeners();

            // Clear day buttons
            foreach (var button in dayButtons)
            {
                if (button != null)
                    button.onClick.RemoveAllListeners();
            }
            dayButtons.Clear();
        }

        private void GenerateCalendar()
        {
            if (calendarGrid == null || dayButtonPrefab == null) return;

            // Clear existing day buttons
            foreach (var button in dayButtons)
            {
                if (button != null)
                    DestroyImmediate(button.gameObject);
            }
            dayButtons.Clear();

            // Update month/year text
            if (monthYearText != null)
                monthYearText.text = currentMonth.ToString("MMMM, yyyy");

            // Get first day of month and number of days
            DateTime firstDayOfMonth = new DateTime(currentMonth.Year, currentMonth.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(currentMonth.Year, currentMonth.Month);
            
            // Get day of week for first day (0 = Sunday, 1 = Monday, etc.)
            int firstDayOfWeek = (int)firstDayOfMonth.DayOfWeek;
            
            // Calculate total cells needed (6 rows * 7 columns = 42)
            int totalCells = 42;
            
            for (int i = 0; i < totalCells; i++)
            {
                GameObject dayButtonObj = Instantiate(dayButtonPrefab, calendarGrid);
                Button dayButton = dayButtonObj.GetComponent<Button>();
                TextMeshProUGUI dayText = dayButtonObj.GetComponentInChildren<TextMeshProUGUI>();
                
                if (dayButton == null || dayText == null) continue;

                dayButtons.Add(dayButton);
                
                // Calculate the date for this cell
                int dayOffset = i - firstDayOfWeek;
                DateTime cellDate = firstDayOfMonth.AddDays(dayOffset);
                
                // Set button text
                dayText.text = cellDate.Day.ToString();
                
                // Determine if this date is in the current month
                bool isCurrentMonth = cellDate.Month == currentMonth.Month;
                
                // Determine if this date is selected
                bool isSelected = selectedDate.HasValue && 
                                 selectedDate.Value.Year == cellDate.Year && 
                                 selectedDate.Value.Month == cellDate.Month && 
                                 selectedDate.Value.Day == cellDate.Day;
                
                // Determine if this date is today
                bool isToday = cellDate.Date == DateTime.Today;
                
                // Determine if this date is selectable
                bool isSelectable = IsDateSelectable(cellDate);
                
                // Apply visual styling
                ApplyDayButtonStyling(dayButton, dayText, isCurrentMonth, isSelected, isToday, isSelectable);
                
                // Setup click handler
                if (isSelectable)
                {
                    DateTime capturedDate = cellDate; // Capture for closure
                    dayButton.onClick.AddListener(() => OnDaySelected(capturedDate));
                }
                else
                {
                    dayButton.interactable = false;
                }
            }

            // Update navigation buttons
            UpdateNavigationButtons();
        }

        private void ApplyDayButtonStyling(Button button, TextMeshProUGUI text, bool isCurrentMonth, bool isSelected, bool isToday, bool isSelectable)
        {
            // Set text color based on state
            if (isSelected)
            {
                text.color = Color.white;
                // You might want to change button background color here
            }
            else if (isToday)
            {
                text.color = Color.blue;
            }
            else if (isCurrentMonth)
            {
                text.color = Color.black;
            }
            else
            {
                text.color = Color.gray;
            }

            // Set button interactability
            button.interactable = isSelectable;
        }

        private bool IsDateSelectable(DateTime date)
        {
            // Check min date
            if (minDate.HasValue && date.Date < minDate.Value.Date)
                return false;
            
            // Check max date
            if (maxDate.HasValue && date.Date > maxDate.Value.Date)
                return false;
            
            // Custom validation
            if (validationCallback != null && !validationCallback(date))
                return false;
            
            return true;
        }

        private void UpdateNavigationButtons()
        {
            if (previousMonthButton != null)
            {
                DateTime previousMonth = currentMonth.AddMonths(-1);
                previousMonthButton.interactable = !minDate.HasValue || previousMonth >= minDate.Value;
            }
            
            if (nextMonthButton != null)
            {
                DateTime nextMonth = currentMonth.AddMonths(1);
                nextMonthButton.interactable = !maxDate.HasValue || nextMonth <= maxDate.Value;
            }
        }

        private void OnPreviousMonth()
        {
            currentMonth = currentMonth.AddMonths(-1);
            GenerateCalendar();
        }

        private void OnNextMonth()
        {
            currentMonth = currentMonth.AddMonths(1);
            GenerateCalendar();
        }

        private void OnDaySelected(DateTime date)
        {
            selectedDate = date;
            GenerateCalendar(); // Regenerate to update selection styling
            UpdateConfirmButtonState();
        }

        private void OnConfirmPressed()
        {
            // Validate selection
            if (!ValidateSelection())
                return;

            onConfirmCallback?.Invoke(selectedDate);
            Close();
        }

        private void OnCancelPressed()
        {
            onCancelCallback?.Invoke();
            Close();
        }

        private void OnTodayPressed()
        {
            DateTime today = DateTime.Today;
            if (IsDateSelectable(today))
            {
                selectedDate = today;
                currentMonth = today;
                GenerateCalendar();
                UpdateConfirmButtonState();
            }
            else
            {
                ShowError("Today is not a valid selection date.");
            }
        }

        private void OnClearPressed()
        {
            selectedDate = null;
            GenerateCalendar();
            UpdateConfirmButtonState();
        }

        private bool ValidateSelection()
        {
            if (!selectedDate.HasValue)
            {
                ShowError("Please select a date.");
                return false;
            }

            if (!IsDateSelectable(selectedDate.Value))
            {
                ShowError("The selected date is not valid.");
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
                bool isValid = selectedDate.HasValue && IsDateSelectable(selectedDate.Value);
                confirmButton.interactable = isValid;
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
        /// Static method to show the date picker modal
        /// </summary>
        public static DatePickerModal Show(string title = "Select Date", 
                                         string description = "",
                                         DateTime? initialDate = null,
                                         DateTime? minDate = null,
                                         DateTime? maxDate = null,
                                         bool allowClear = true,
                                         bool showTodayButton = true,
                                         string confirmText = "OK", 
                                         string cancelText = "Cancel",
                                         Action<DateTime?> onConfirm = null, 
                                         Action onCancel = null,
                                         Func<DateTime, bool> validation = null)
        {
            var modalManager = RuntimeManager.GetSubsystem<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found. Cannot show DatePickerModal.");
                return null;
            }

            var modalPrefab = Resources.Load<DatePickerModal>("DatePickerModal");
            if (modalPrefab == null)
            {
                Debug.LogError("DatePickerModal prefab not found in Resources folder.");
                return null;
            }

            var modal = modalManager.ShowModal(modalPrefab) as DatePickerModal;
            if (modal != null)
            {
                modal.Setup(title, description, initialDate, minDate, maxDate, allowClear, showTodayButton,
                           confirmText, cancelText, onConfirm, onCancel, validation);
            }

            return modal;
        }
    }
} 