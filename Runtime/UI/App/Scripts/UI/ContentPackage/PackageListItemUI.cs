using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Molca.App.UI;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Utilities;

namespace Molca.App.UI.ContentPackage
{
    /// <summary>
    /// Single row in the content package list.
    /// Uses <see cref="ColorIDButton"/> in toggle mode — selection state and colors
    /// are driven by the ColorID theme system.
    /// The parent list container must have a <see cref="ColorIDButtonGroup"/> configured
    /// as radio (single selection, <c>allowSwitchOff = false</c>).
    /// </summary>
    public class PackageListItemUI : MonoBehaviour
    {
        [SerializeField] private ColorIDButton   _button;
        [SerializeField] private Image           _statusDot;
        [SerializeField] private TextMeshProUGUI _nameLabel;
        [SerializeField] private TextMeshProUGUI _idLabel;
        [SerializeField] private TextMeshProUGUI _statusLabel;
        [SerializeField] private TextMeshProUGUI _sizeLabel;
        [SerializeField] private Image           _progressBar;

        /// <summary>Package ID this row represents.</summary>
        public string PackageId { get; private set; }

        private Action<string> _onSelected;

        // ── Setup ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the row. Call once after instantiating the prefab.
        /// </summary>
        /// <param name="cfg">Package config to display.</param>
        /// <param name="state">Current runtime state (may be null for untracked packages).</param>
        /// <param name="statusColor">Dot and label color matching the package status.</param>
        /// <param name="onSelected">Callback fired when the row is toggled on.</param>
        public void Initialize(
            ContentPackageSettings.PackageConfig cfg,
            PackageState state,
            Color statusColor,
            Action<string> onSelected)
        {
            PackageId   = cfg.packageId;
            _onSelected = onSelected;

            if (_button != null)
                _button.onToggleOn.AddListener(() => _onSelected?.Invoke(PackageId));

            ApplyConfig(cfg);
            Refresh(state, statusColor);
        }

        private void ApplyConfig(ContentPackageSettings.PackageConfig cfg)
        {
            if (_nameLabel != null)
                _nameLabel.text = string.IsNullOrEmpty(cfg.displayName) ? cfg.packageId : cfg.displayName;

            if (_idLabel != null)
                _idLabel.text = cfg.displayName != cfg.packageId ? cfg.packageId : "";

            if (_sizeLabel != null)
                _sizeLabel.text = "";
        }

        /// <summary>Updates the size label from the remote manifest bundle size.</summary>
        public void RefreshSize(long bundleSizeBytes)
        {
            if (_sizeLabel != null)
                _sizeLabel.text = bundleSizeBytes > 0 ? SizeFormatter.Format(bundleSizeBytes) : "";
        }

        // ── Update ────────────────────────────────────────────────────────────

        /// <summary>Refreshes the status dot, label, and inline download progress bar.</summary>
        public void Refresh(PackageState state, Color statusColor)
        {
            var status = state?.status ?? PackageStatus.Available;

            if (_statusDot != null)
                _statusDot.color = statusColor;

            if (_statusLabel != null)
            {
                _statusLabel.text  = StatusLabel(status);
                _statusLabel.color = statusColor;
            }

            bool downloading = status == PackageStatus.Downloading;
            if (_progressBar != null)
            {
                _progressBar.gameObject.SetActive(downloading);
                if (downloading && state != null)
                    _progressBar.fillAmount = state.downloadProgress;
            }
        }

        /// <summary>
        /// Sets the toggle state on the underlying <see cref="ColorIDButton"/> without
        /// notifying the group — used by <see cref="ContentPackageManagerUI"/> to reflect
        /// external selection changes (e.g. after catalog refresh).
        /// </summary>
        public void SetSelected(bool selected)
        {
            if (_button != null && _button.IsOn != selected)
                _button.SetToggleState(selected, notifyGroup: false);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string StatusLabel(PackageStatus status) => status switch
        {
            PackageStatus.Available       => "Available",
            PackageStatus.Downloading     => "Downloading…",
            PackageStatus.Installed       => "Installed",
            PackageStatus.Failed          => "Failed",
            PackageStatus.UpdateAvailable => "Update Available",
            _                             => ""
        };
    }
}
