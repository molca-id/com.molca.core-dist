using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using Molca;
using Molca.Modals;

namespace Molca.App
{
    public class ProgressModal : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI progressText;
        [SerializeField] private Slider progressBar;
        [SerializeField] private Image progressBarFill;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button pauseButton;
        [SerializeField] private Button resumeButton;

        [Header("Settings")]
        [SerializeField] private string title = "Processing...";
        [SerializeField] private string initialStatus = "Please wait...";
        [SerializeField] private bool showCancelButton = true;
        [SerializeField] private bool showPauseButton = false;
        [SerializeField] private string cancelText = "Cancel";
        [SerializeField] private string pauseText = "Pause";
        [SerializeField] private string resumeText = "Resume";

        private Action onCancelCallback;
        private Action onPauseCallback;
        private Action onResumeCallback;
        private bool isPaused = false;

        public void Setup(string title, string initialStatus, bool showCancelButton, bool showPauseButton,
                         string cancelText, string pauseText, string resumeText,
                         Action onCancel = null, Action onPause = null, Action onResume = null)
        {
            this.title = title ?? "Processing...";
            this.initialStatus = initialStatus ?? "Please wait...";
            this.showCancelButton = showCancelButton;
            this.showPauseButton = showPauseButton;
            this.cancelText = cancelText ?? "Cancel";
            this.pauseText = pauseText ?? "Pause";
            this.resumeText = resumeText ?? "Resume";
            
            onCancelCallback = onCancel;
            onPauseCallback = onPause;
            onResumeCallback = onResume;

            // Initialize UI
            if (titleText != null) titleText.text = this.title;
            if (statusText != null) statusText.text = this.initialStatus;
            if (progressText != null) progressText.text = "0%";
            
            if (progressBar != null)
            {
                progressBar.value = 0f;
                progressBar.minValue = 0f;
                progressBar.maxValue = 1f;
            }

            if (cancelButton != null)
            {
                var cancelLabel = cancelButton.GetComponentInChildren<TextMeshProUGUI>();
                if (cancelLabel != null) cancelLabel.text = this.cancelText;
                cancelButton.gameObject.SetActive(showCancelButton);
            }

            if (pauseButton != null)
            {
                var pauseLabel = pauseButton.GetComponentInChildren<TextMeshProUGUI>();
                if (pauseLabel != null) pauseLabel.text = this.pauseText;
                pauseButton.gameObject.SetActive(showPauseButton);
            }

            if (resumeButton != null)
            {
                var resumeLabel = resumeButton.GetComponentInChildren<TextMeshProUGUI>();
                if (resumeLabel != null) resumeLabel.text = this.resumeText;
                resumeButton.gameObject.SetActive(false); // Initially hidden
            }
        }

        private void Awake()
        {
            SetupButtonListeners();
        }

        private void SetupButtonListeners()
        {
            if (cancelButton != null)
                cancelButton.onClick.AddListener(OnCancelPressed);
            
            if (pauseButton != null)
                pauseButton.onClick.AddListener(OnPausePressed);
            
            if (resumeButton != null)
                resumeButton.onClick.AddListener(OnResumePressed);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy
            if (cancelButton != null)
                cancelButton.onClick.RemoveAllListeners();
            
            if (pauseButton != null)
                pauseButton.onClick.RemoveAllListeners();
            
            if (resumeButton != null)
                resumeButton.onClick.RemoveAllListeners();
        }

        private void OnCancelPressed()
        {
            onCancelCallback?.Invoke();
            Close();
        }

        private void OnPausePressed()
        {
            isPaused = true;
            onPauseCallback?.Invoke();
            
            if (pauseButton != null) pauseButton.gameObject.SetActive(false);
            if (resumeButton != null) resumeButton.gameObject.SetActive(true);
            
            if (statusText != null) statusText.text = "Paused";
        }

        private void OnResumePressed()
        {
            isPaused = false;
            onResumeCallback?.Invoke();
            
            if (pauseButton != null) pauseButton.gameObject.SetActive(true);
            if (resumeButton != null) resumeButton.gameObject.SetActive(false);
            
            if (statusText != null) statusText.text = "Resuming...";
        }

        /// <summary>
        /// Update the progress (0.0 to 1.0)
        /// </summary>
        public void UpdateProgress(float progress, string status = null)
        {
            progress = Mathf.Clamp01(progress);
            
            if (progressBar != null)
                progressBar.value = progress;
            
            if (progressText != null)
                progressText.text = $"{Mathf.RoundToInt(progress * 100)}%";
            
            if (status != null && statusText != null)
                statusText.text = status;
        }

        /// <summary>
        /// Update the progress with current and total values
        /// </summary>
        public void UpdateProgress(int current, int total, string status = null)
        {
            if (total <= 0) return;
            
            float progress = (float)current / total;
            UpdateProgress(progress, status);
        }

        /// <summary>
        /// Set the status message
        /// </summary>
        public void SetStatus(string status)
        {
            if (statusText != null)
                statusText.text = status;
        }

        /// <summary>
        /// Set the title
        /// </summary>
        public void SetTitle(string title)
        {
            if (titleText != null)
                titleText.text = title;
        }

        /// <summary>
        /// Set progress bar color
        /// </summary>
        public void SetProgressBarColor(Color color)
        {
            if (progressBarFill != null)
                progressBarFill.color = color;
        }

        /// <summary>
        /// Check if the operation is paused
        /// </summary>
        public bool IsPaused => isPaused;

        /// <summary>
        /// Complete the progress (set to 100%)
        /// </summary>
        public void Complete(string status = "Complete!")
        {
            UpdateProgress(1f, status);
        }

        public override void Open(bool showNoButton = true)
        {
            base.Open(showNoButton);
            SetNoButtonVisible(showNoButton);
        }

        public override void SetNoButtonVisible(bool visible)
        {
            if (cancelButton != null)
                cancelButton.gameObject.SetActive(visible && showCancelButton);
        }

        /// <summary>
        /// Static method to show the progress modal
        /// </summary>
        public static ProgressModal Show(string title = "Processing...", 
                                       string initialStatus = "Please wait...",
                                       bool showCancelButton = true,
                                       bool showPauseButton = false,
                                       string cancelText = "Cancel",
                                       string pauseText = "Pause",
                                       string resumeText = "Resume",
                                       Action onCancel = null,
                                       Action onPause = null,
                                       Action onResume = null)
        {
            var modalManager = RuntimeManager.GetSubsystem<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found. Cannot show ProgressModal.");
                return null;
            }

            var modalPrefab = Resources.Load<ProgressModal>("ProgressModal");
            if (modalPrefab == null)
            {
                Debug.LogError("ProgressModal prefab not found in Resources folder.");
                return null;
            }

            var modal = modalManager.ShowModal(modalPrefab) as ProgressModal;
            if (modal != null)
            {
                modal.Setup(title, initialStatus, showCancelButton, showPauseButton, 
                           cancelText, pauseText, resumeText, onCancel, onPause, onResume);
            }

            return modal;
        }
    }
} 