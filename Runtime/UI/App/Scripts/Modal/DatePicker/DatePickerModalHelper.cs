using UnityEngine;
using UnityEngine.Events;
using System;
using Molca.App;

namespace Molca.App
{
    [System.Serializable]
    public class DatePickerModalData
    {
        [Header("Display Settings")]
        public string title = "Select Date";
        public string description = "";
        public string confirmText = "OK";
        public string cancelText = "Cancel";
        public string todayText = "Today";
        public string clearText = "Clear";

        [Header("Date Settings")]
        public DateTime? initialDate = null;
        public DateTime? minDate = null;
        public DateTime? maxDate = null;
        public bool allowClear = true;
        public bool showTodayButton = true;

        [Header("Callbacks")]
        public UnityEvent<DateTime?> onConfirm = new UnityEvent<DateTime?>();
        public UnityEvent onCancel = new UnityEvent();
    }

    public class DatePickerModalHelper : MonoBehaviour
    {
        [SerializeField] private DatePickerModalData modalData = new DatePickerModalData();

        /// <summary>
        /// Show the date picker modal with the configured settings
        /// </summary>
        public void ShowDatePicker()
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: modalData.minDate,
                maxDate: modalData.maxDate,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal with custom settings
        /// </summary>
        public void ShowDatePicker(string title, string description, DateTime? initialDate = null)
        {
            DatePickerModal.Show(
                title: title,
                description: description,
                initialDate: initialDate,
                minDate: modalData.minDate,
                maxDate: modalData.maxDate,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal with date range restrictions
        /// </summary>
        public void ShowDatePickerWithRange(DateTime minDate, DateTime maxDate)
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: minDate,
                maxDate: maxDate,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a future date only
        /// </summary>
        public void ShowFutureDatePicker()
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: DateTime.Today,
                maxDate: modalData.maxDate,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a past date only
        /// </summary>
        public void ShowPastDatePicker()
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: modalData.minDate,
                maxDate: DateTime.Today,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the next 30 days
        /// </summary>
        public void ShowNext30DaysPicker()
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: DateTime.Today,
                maxDate: DateTime.Today.AddDays(30),
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the last 30 days
        /// </summary>
        public void ShowLast30DaysPicker()
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: DateTime.Today.AddDays(-30),
                maxDate: DateTime.Today,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the current year
        /// </summary>
        public void ShowCurrentYearPicker()
        {
            DateTime startOfYear = new DateTime(DateTime.Today.Year, 1, 1);
            DateTime endOfYear = new DateTime(DateTime.Today.Year, 12, 31);

            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startOfYear,
                maxDate: endOfYear,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the next year
        /// </summary>
        public void ShowNextYearPicker()
        {
            DateTime startOfNextYear = new DateTime(DateTime.Today.Year + 1, 1, 1);
            DateTime endOfNextYear = new DateTime(DateTime.Today.Year + 1, 12, 31);

            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startOfNextYear,
                maxDate: endOfNextYear,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the previous year
        /// </summary>
        public void ShowPreviousYearPicker()
        {
            DateTime startOfPreviousYear = new DateTime(DateTime.Today.Year - 1, 1, 1);
            DateTime endOfPreviousYear = new DateTime(DateTime.Today.Year - 1, 12, 31);

            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startOfPreviousYear,
                maxDate: endOfPreviousYear,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within a specific year
        /// </summary>
        public void ShowYearPicker(int year)
        {
            DateTime startOfYear = new DateTime(year, 1, 1);
            DateTime endOfYear = new DateTime(year, 12, 31);

            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startOfYear,
                maxDate: endOfYear,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within a specific month
        /// </summary>
        public void ShowMonthPicker(int year, int month)
        {
            DateTime startOfMonth = new DateTime(year, month, 1);
            DateTime endOfMonth = new DateTime(year, month, DateTime.DaysInMonth(year, month));

            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startOfMonth,
                maxDate: endOfMonth,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within a specific range
        /// </summary>
        public void ShowRangePicker(DateTime startDate, DateTime endDate)
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: startDate,
                maxDate: endDate,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the next N days
        /// </summary>
        public void ShowNextDaysPicker(int days)
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: DateTime.Today,
                maxDate: DateTime.Today.AddDays(days),
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }

        /// <summary>
        /// Show the date picker modal for selecting a date within the last N days
        /// </summary>
        public void ShowLastDaysPicker(int days)
        {
            DatePickerModal.Show(
                title: modalData.title,
                description: modalData.description,
                initialDate: modalData.initialDate,
                minDate: DateTime.Today.AddDays(-days),
                maxDate: DateTime.Today,
                allowClear: modalData.allowClear,
                showTodayButton: modalData.showTodayButton,
                confirmText: modalData.confirmText,
                cancelText: modalData.cancelText,
                onConfirm: (date) => modalData.onConfirm?.Invoke(date),
                onCancel: () => modalData.onCancel?.Invoke()
            );
        }
    }
} 