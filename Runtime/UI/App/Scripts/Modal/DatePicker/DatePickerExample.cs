using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

namespace Molca.App
{
    public class DatePickerExample : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private DateField birthDateField;
        [SerializeField] private DateField appointmentDateField;
        [SerializeField] private DateField eventDateField;
        [SerializeField] private Button showDatePickerButton;
        [SerializeField] private Button showFutureDateButton;
        [SerializeField] private Button showPastDateButton;
        [SerializeField] private Button showRangeDateButton;
        [SerializeField] private TextMeshProUGUI resultText;

        [Header("Settings")]
        [SerializeField] private string resultPrefix = "Selected Date: ";

        private void Start()
        {
            SetupButtonListeners();
            SetupDateFields();
        }

        private void SetupButtonListeners()
        {
            if (showDatePickerButton != null)
                showDatePickerButton.onClick.AddListener(ShowBasicDatePicker);
            
            if (showFutureDateButton != null)
                showFutureDateButton.onClick.AddListener(ShowFutureDatePicker);
            
            if (showPastDateButton != null)
                showPastDateButton.onClick.AddListener(ShowPastDatePicker);
            
            if (showRangeDateButton != null)
                showRangeDateButton.onClick.AddListener(ShowRangeDatePicker);
        }

        private void SetupDateFields()
        {
            // Setup birth date field (past dates only)
            if (birthDateField != null)
            {
                birthDateField.SetDateRange(DateTime.Today.AddYears(-100), DateTime.Today);
                birthDateField.SetDateFormat("MM/dd/yyyy");
                birthDateField.SetPlaceholder("Select your birth date");
                birthDateField.SetModalSettings("Birth Date", "Please select your date of birth");
                birthDateField.onDateChanged.AddListener(OnBirthDateChanged);
            }

            // Setup appointment date field (future dates only)
            if (appointmentDateField != null)
            {
                appointmentDateField.SetDateRange(DateTime.Today, DateTime.Today.AddDays(90));
                appointmentDateField.SetDateFormat("MM/dd/yyyy");
                appointmentDateField.SetPlaceholder("Select appointment date");
                appointmentDateField.SetModalSettings("Appointment Date", "Choose a date for your appointment");
                appointmentDateField.onDateChanged.AddListener(OnAppointmentDateChanged);
            }

            // Setup event date field (any date)
            if (eventDateField != null)
            {
                eventDateField.SetDateFormat("MMMM dd, yyyy");
                eventDateField.SetPlaceholder("Select event date");
                eventDateField.SetModalSettings("Event Date", "Choose a date for your event");
                eventDateField.onDateChanged.AddListener(OnEventDateChanged);
            }
        }

        private void ShowBasicDatePicker()
        {
            DatePickerModal.Show(
                title: "Select Date",
                description: "Choose any date",
                onConfirm: (date) => {
                    UpdateResultText($"Basic Date Picker: {date?.ToString("MM/dd/yyyy") ?? "None"}");
                },
                onCancel: () => {
                    UpdateResultText("Date selection cancelled");
                }
            );
        }

        private void ShowFutureDatePicker()
        {
            DatePickerModal.Show(
                title: "Select Future Date",
                description: "Choose a date in the future",
                minDate: DateTime.Today,
                maxDate: DateTime.Today.AddYears(1),
                onConfirm: (date) => {
                    UpdateResultText($"Future Date: {date?.ToString("MM/dd/yyyy") ?? "None"}");
                },
                onCancel: () => {
                    UpdateResultText("Future date selection cancelled");
                }
            );
        }

        private void ShowPastDatePicker()
        {
            DatePickerModal.Show(
                title: "Select Past Date",
                description: "Choose a date in the past",
                minDate: DateTime.Today.AddYears(-10),
                maxDate: DateTime.Today,
                onConfirm: (date) => {
                    UpdateResultText($"Past Date: {date?.ToString("MM/dd/yyyy") ?? "None"}");
                },
                onCancel: () => {
                    UpdateResultText("Past date selection cancelled");
                }
            );
        }

        private void ShowRangeDatePicker()
        {
            DateTime startDate = DateTime.Today.AddDays(-7);
            DateTime endDate = DateTime.Today.AddDays(7);

            DatePickerModal.Show(
                title: "Select Date Range",
                description: $"Choose a date between {startDate.ToString("MM/dd")} and {endDate.ToString("MM/dd")}",
                minDate: startDate,
                maxDate: endDate,
                onConfirm: (date) => {
                    UpdateResultText($"Range Date: {date?.ToString("MM/dd/yyyy") ?? "None"}");
                },
                onCancel: () => {
                    UpdateResultText("Range date selection cancelled");
                }
            );
        }

        private void OnBirthDateChanged(DateTime? date)
        {
            if (date.HasValue)
            {
                int age = CalculateAge(date.Value);
                UpdateResultText($"Birth Date: {date.Value.ToString("MM/dd/yyyy")} (Age: {age})");
            }
            else
            {
                UpdateResultText("Birth date cleared");
            }
        }

        private void OnAppointmentDateChanged(DateTime? date)
        {
            if (date.HasValue)
            {
                int daysUntil = (date.Value - DateTime.Today).Days;
                UpdateResultText($"Appointment: {date.Value.ToString("MM/dd/yyyy")} (in {daysUntil} days)");
            }
            else
            {
                UpdateResultText("Appointment date cleared");
            }
        }

        private void OnEventDateChanged(DateTime? date)
        {
            if (date.HasValue)
            {
                string dayOfWeek = date.Value.ToString("dddd");
                UpdateResultText($"Event: {date.Value.ToString("MMMM dd, yyyy")} ({dayOfWeek})");
            }
            else
            {
                UpdateResultText("Event date cleared");
            }
        }

        private void UpdateResultText(string message)
        {
            if (resultText != null)
            {
                resultText.text = resultPrefix + message;
            }
            Debug.Log(message);
        }

        private int CalculateAge(DateTime birthDate)
        {
            DateTime today = DateTime.Today;
            int age = today.Year - birthDate.Year;
            
            if (birthDate.Date > today.AddYears(-age))
                age--;

            return age;
        }

        // Example methods for DatePickerModalHelper usage
        public void ShowNext30Days()
        {
            var helper = GetComponent<DatePickerModalHelper>();
            if (helper != null)
            {
                helper.ShowNext30DaysPicker();
            }
        }

        public void ShowCurrentYear()
        {
            var helper = GetComponent<DatePickerModalHelper>();
            if (helper != null)
            {
                helper.ShowCurrentYearPicker();
            }
        }

        public void ShowSpecificYear(int year)
        {
            var helper = GetComponent<DatePickerModalHelper>();
            if (helper != null)
            {
                helper.ShowYearPicker(year);
            }
        }

        // Example validation function
        private bool ValidateWeekendOnly(DateTime date)
        {
            return date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
        }

        public void ShowWeekendOnlyPicker()
        {
            DatePickerModal.Show(
                title: "Select Weekend Date",
                description: "Choose a Saturday or Sunday",
                validation: ValidateWeekendOnly,
                onConfirm: (date) => {
                    UpdateResultText($"Weekend Date: {date?.ToString("dddd, MM/dd/yyyy") ?? "None"}");
                }
            );
        }
    }
} 