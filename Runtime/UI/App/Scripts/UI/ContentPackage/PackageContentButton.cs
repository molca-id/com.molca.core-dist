using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using TMPro;
using Molca;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Services;

namespace Molca.App.UI.ContentPackage
{
    /// <summary>
    /// A <see cref="ColorIDButton"/> that gates its action behind a content package.
    /// While the package is not installed the button triggers a download instead of firing
    /// <see cref="onPackageReady"/>. A progress bar and status label are shown during the
    /// download and hidden once the package is installed or on failure.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="PackageSubsystem"/> to be initialized. Wire <see cref="onPackageReady"/>
    /// to whatever should happen when the package is confirmed installed and the button is pressed.
    /// </remarks>
    [AddComponentMenu("Molca/UI/Content Package/Package Content Button")]
    public class PackageContentButton : ColorIDButton
    {
        // ── Config ─────────────────────────────────────────────────────────────

        [SerializeField] private PackageReference _package;

        // ── Progress UI ────────────────────────────────────────────────────────

        [SerializeField] private GameObject      _progressRoot;
        [SerializeField] private Slider          _progressSlider;
        [SerializeField] private TextMeshProUGUI _progressLabel;
        [SerializeField] private TextMeshProUGUI _statusLabel;

        // ── Events ─────────────────────────────────────────────────────────────

        [Header("Package Events")]
        /// <summary>Fired when the package is installed and the user clicks the button.</summary>
        public UnityEvent onPackageReady;
        /// <summary>Fired when a download starts.</summary>
        public UnityEvent onDownloadStarted;
        /// <summary>Fired when installation completes successfully.</summary>
        public UnityEvent onInstallCompleted;
        /// <summary>Fired with the error message when installation fails.</summary>
        public UnityEvent<string> onInstallFailed;

        // ── Internal state ─────────────────────────────────────────────────────

        [Inject] private PackageSubsystem _packageSubsystem;

        private PackageService         _service;
        private CancellationTokenSource _installCts;
        private bool                   _initialized;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        protected override async void Start()
        {
            base.Start();

            await RuntimeManager.WaitForInitialization();

            _service = _packageSubsystem?.PackageService;

            if (_service == null)
            {
                SetStatus("Package service unavailable.");
                interactable = false;
                return;
            }

            _service.OnPackageStateChanged += OnPackageStateChanged;
            _service.OnDownloadProgress    += OnDownloadProgress;

            _initialized = true;
            RefreshVisuals();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CancelInstall();

            if (_service != null)
            {
                _service.OnPackageStateChanged -= OnPackageStateChanged;
                _service.OnDownloadProgress    -= OnDownloadProgress;
            }
        }

        // ── Click handling ─────────────────────────────────────────────────────

        public override void OnPointerClick(PointerEventData eventData)
        {
            // Let ColorIDButton handle toggle visuals, but skip its onClick relay —
            // we decide below whether to install or fire onPackageReady.
            if (!interactable || !_initialized || !_package.IsValid) return;

            var status = _service?.GetPackageState(_package.PackageId)?.status
                         ?? PackageStatus.Available;

            if (status == PackageStatus.Installed)
            {
                base.OnPointerClick(eventData); // fires base onClick + toggle
                onPackageReady?.Invoke();
            }
            else if (status != PackageStatus.Downloading)
            {
                _ = StartInstallAsync();
            }
        }

        // ── Install flow ───────────────────────────────────────────────────────

        private async Awaitable StartInstallAsync()
        {
            CancelInstall();
            _installCts = new CancellationTokenSource();

            SetStatus("Downloading…");
            SetProgressVisible(true);
            interactable = false;
            onDownloadStarted?.Invoke();

            var progress = new System.Progress<float>(v =>
            {
                if (_progressSlider != null) _progressSlider.value = v;
                if (_progressLabel  != null) _progressLabel.text   = $"{v:P0}";
            });

            var result = await _service.InstallPackageAsync(_package.PackageId, progress, _installCts.Token);

            SetProgressVisible(false);
            interactable = true;

            if (result.Success)
            {
                SetStatus("");
                onInstallCompleted?.Invoke();
            }
            else if (!result.WasCancelled)
            {
                SetStatus($"Failed: {result.ErrorMessage}");
                onInstallFailed?.Invoke(result.ErrorMessage);
            }
            else
            {
                SetStatus("");
            }
        }

        private void CancelInstall()
        {
            _installCts?.Cancel();
            _installCts?.Dispose();
            _installCts = null;
        }

        // ── Service callbacks ──────────────────────────────────────────────────

        private void OnPackageStateChanged(string packageId, PackageStatus status)
        {
            if (packageId != _package.PackageId) return;
            RefreshVisuals();
        }

        private void OnDownloadProgress(string packageId, float progress)
        {
            if (packageId != _package.PackageId) return;
            if (_progressSlider != null) _progressSlider.value = progress;
            if (_progressLabel  != null) _progressLabel.text   = $"{progress:P0}";
        }

        // ── Visuals ────────────────────────────────────────────────────────────

        private void RefreshVisuals()
        {
            if (_service == null || !_package.IsValid) return;

            var status = _service.GetPackageState(_package.PackageId)?.status
                         ?? PackageStatus.Available;

            bool downloading = status == PackageStatus.Downloading;
            SetProgressVisible(downloading);
            interactable = !downloading;

            if (downloading)
                SetStatus("Downloading…");
            else if (status == PackageStatus.Failed)
                SetStatus("Download failed. Tap to retry.");
            else if (status == PackageStatus.UpdateAvailable)
                SetStatus("Update available. Tap to update.");
            else if (status == PackageStatus.Available)
                SetStatus("Tap to download.");
            else
                SetStatus("");
        }

        private void SetProgressVisible(bool visible)
        {
            if (_progressRoot != null) _progressRoot.SetActive(visible);
        }

        private void SetStatus(string message)
        {
            if (_statusLabel != null) _statusLabel.text = message;
        }
    }
}
