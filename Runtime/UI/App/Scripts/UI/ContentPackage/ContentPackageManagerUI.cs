using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Molca.App.UI;
using Molca;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Release;
using Molca.ContentPackage.Services;
using Molca.ContentPackage.Utilities;
using Molca.Settings;
using Molca.Modals;

namespace Molca.App.UI.ContentPackage
{
    /// <summary>
    /// Runtime UI for browsing, installing, and uninstalling content packages.
    /// Attach to a Canvas root. Wire all serialized references in the Inspector.
    /// The <see cref="ColorIDButtonGroup"/> on <see cref="_listContainer"/> must be
    /// configured as radio (single selection, <c>allowSwitchOff = false</c>).
    /// </summary>
    public class ContentPackageManagerUI : MonoBehaviour
    {
        // ── Header ───────────────────────────────────────────────────────────

        [Header("Header")]
        [SerializeField] private TextMeshProUGUI _titleLabel;
        [SerializeField] private ColorIDButton   _refreshButton;
        [SerializeField] private TextMeshProUGUI _refreshButtonLabel;
        [SerializeField] private TextMeshProUGUI _statusLabel;

        // ── Package list (left panel) ────────────────────────────────────────

        [Header("Package List")]
        [SerializeField] private Transform           _listContainer;
        [SerializeField] private ColorIDButtonGroup  _listButtonGroup;
        [SerializeField] private PackageListItemUI   _listItemPrefab;
        [SerializeField] private TextMeshProUGUI     _emptyLabel;

        // ── List toolbar ──────────────────────────────────────────────────────
        //
        // Hidden below PackageListQuery.ToolbarThreshold packages: a filter over a list you can see
        // all of at once costs a row of vertical space and returns nothing.

        [Header("List Toolbar")]
        [SerializeField] private GameObject      _listToolbar;
        [SerializeField] private TMP_InputField  _searchField;
        [SerializeField] private ColorIDButton   _filterButton;
        [SerializeField] private TextMeshProUGUI _filterButtonLabel;
        [SerializeField] private ColorIDButton   _sortButton;
        [SerializeField] private TextMeshProUGUI _sortButtonLabel;

        // ── Release activation ────────────────────────────────────────────────
        //
        // Separate from the per-package progress row below, and deliberately not inside the detail
        // panel: adopting a release is an app-wide operation, and a progress bar that vanishes
        // because the user tapped a different package would look like the download stopped.

        [Header("Release Activation")]
        [SerializeField] private GameObject      _releaseProgressRow;
        [SerializeField] private TextMeshProUGUI _releaseProgressLabel;
        [SerializeField] private Slider          _releaseProgressSlider;

        // ── Detail panel (right panel) ────────────────────────────────────────

        [Header("Detail Panel")]
        [SerializeField] private GameObject      _detailPanel;
        [SerializeField] private TextMeshProUGUI _detailName;
        [SerializeField] private TextMeshProUGUI _detailId;
        [SerializeField] private TextMeshProUGUI _detailVersion;
        [SerializeField] private TextMeshProUGUI _detailDescription;
        [SerializeField] private TextMeshProUGUI _detailSize;
        [SerializeField] private TextMeshProUGUI _detailDownloadSize;
        [SerializeField] private TextMeshProUGUI _detailTags;
        [SerializeField] private TextMeshProUGUI _detailChangelog;
        [SerializeField] private GameObject      _detailChangelogRow;
        [SerializeField] private TextMeshProUGUI _detailStatus;
        [SerializeField] private TextMeshProUGUI _detailDependencies;
        [SerializeField] private TextMeshProUGUI _detailUsedBy;
        [SerializeField] private TextMeshProUGUI _detailErrorMessage;
        [SerializeField] private GameObject      _errorRow;

        // ── Progress ──────────────────────────────────────────────────────────

        [Header("Progress")]
        [SerializeField] private GameObject      _progressRow;
        [SerializeField] private Slider          _progressSlider;
        [SerializeField] private TextMeshProUGUI _progressLabel;

        // ── Action buttons ────────────────────────────────────────────────────

        [Header("Action Buttons")]
        [SerializeField] private ColorIDButton   _installButton;
        [SerializeField] private TextMeshProUGUI _installButtonLabel;
        [SerializeField] private ColorIDButton   _uninstallButton;
        [SerializeField] private ColorIDButton   _updateAllButton;
        [SerializeField] private TextMeshProUGUI _updateAllButtonLabel;
        [SerializeField] private ColorIDButton   _cancelButton;
        [SerializeField] private ColorIDButton   _freeUpSpaceButton;
        [SerializeField] private TextMeshProUGUI _freeUpSpaceButtonLabel;

        // ── Footer ────────────────────────────────────────────────────────────

        [Header("Footer")]
        [SerializeField] private TextMeshProUGUI _footerInstalledSizeLabel;
        [SerializeField] private TextMeshProUGUI _footerInstalledCountLabel;

        // ── Status colors ─────────────────────────────────────────────────────

        [Header("Status Colors")]
        [SerializeField] private Color _colorAvailable   = new Color(0.55f, 0.55f, 0.55f);
        [SerializeField] private Color _colorDownloading = new Color(1f,    0.75f, 0f);
        [SerializeField] private Color _colorInstalled   = new Color(0.1f,  0.8f,  0.1f);
        [SerializeField] private Color _colorFailed      = new Color(1f,    0.25f, 0.25f);
        [SerializeField] private Color _colorUpdate      = new Color(0.3f,  0.7f,  1f);

        // ── Internal state ────────────────────────────────────────────────────

        [Inject] private PackageSubsystem _packageSubsystem;

        private PackageService         _service;
        private ContentPackageSettings _settings;

        private readonly List<PackageListItemUI> _items = new List<PackageListItemUI>();
        private string _selectedPackageId;

        // Per-operation tokens — each flow owns its own so they don't stomp each other.
        private CancellationTokenSource _installCts;
        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _downloadSizeCts;
        private CancellationTokenSource _freeUpSpaceCts;

        private bool _refreshing;
        private bool _updatingAll;
        private bool _freeingSpace;
        private bool _activatingRelease;

        private string             _searchText = "";
        private PackageListFilter  _filter     = PackageListFilter.All;
        private PackageListSort    _sort       = PackageListSort.Name;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();

                _service  = _packageSubsystem?.PackageService;
                _settings = GlobalSettings.GetModule<ContentPackageSettings>();

                if (_service == null)
                {
                    SetStatus("PackageService unavailable.", _colorFailed);
                    return;
                }

                SubscribeEvents();

                if (_refreshButton   != null) _refreshButton.onClick.AddListener(OnRefreshClicked);
                if (_installButton   != null) _installButton.onClick.AddListener(OnInstallClicked);
                if (_uninstallButton != null) _uninstallButton.onClick.AddListener(OnUninstallClicked);
                if (_updateAllButton != null) _updateAllButton.onClick.AddListener(OnUpdateAllClicked);
                if (_cancelButton    != null) _cancelButton.onClick.AddListener(OnCancelClicked);
                if (_freeUpSpaceButton != null) _freeUpSpaceButton.onClick.AddListener(OnFreeUpSpaceClicked);
                if (_filterButton    != null) _filterButton.onClick.AddListener(OnFilterClicked);
                if (_sortButton      != null) _sortButton.onClick.AddListener(OnSortClicked);
                if (_searchField     != null) _searchField.onValueChanged.AddListener(OnSearchChanged);

                RefreshToolbarLabels();
                SetReleaseProgress(new ReleaseActivationProgress(ReleaseActivationPhase.Idle));

                RebuildList();
                RefreshFooter();
                RefreshFreeUpSpaceButton();
                SetStatus("Ready", _colorAvailable);
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

        /// <summary>
        /// Cancels in-flight work and unsubscribes.
        /// </summary>
        /// <remarks>
        /// Every operation token is linked to <c>destroyCancellationToken</c>, so this is belt and
        /// braces rather than the only guard: a handler that returns after the panel is gone finds
        /// its token already cancelled instead of writing to destroyed TextMeshPro objects.
        /// </remarks>
        private void OnDestroy()
        {
            CancelAndDispose(ref _installCts);
            CancelAndDispose(ref _freeUpSpaceCts);
            CancelAndDispose(ref _refreshCts);
            CancelAndDispose(ref _downloadSizeCts);
            UnsubscribeEvents();
        }

        // ── Event wiring ──────────────────────────────────────────────────────

        private void SubscribeEvents()
        {
            if (_service == null) return;
            _service.OnPackageStateChanged += OnPackageStateChanged;
            _service.OnDownloadProgress    += OnDownloadProgress;
            _service.OnPackageError        += OnPackageError;
            _service.OnCatalogRefreshed    += OnCatalogRefreshed;
            _service.OnReleaseActivationProgress += SetReleaseProgress;
        }

        private void UnsubscribeEvents()
        {
            if (_service == null) return;
            _service.OnPackageStateChanged -= OnPackageStateChanged;
            _service.OnDownloadProgress    -= OnDownloadProgress;
            _service.OnPackageError        -= OnPackageError;
            _service.OnCatalogRefreshed    -= OnCatalogRefreshed;
            _service.OnReleaseActivationProgress -= SetReleaseProgress;
        }

        // ── Service callbacks ─────────────────────────────────────────────────

        private void OnPackageStateChanged(string packageId, PackageStatus status)
        {
            RefreshItemCard(packageId);
            if (packageId == _selectedPackageId)
                RefreshDetailPanel();
            RefreshFooter();
            RefreshUpdateAllButton();
            RefreshFreeUpSpaceButton();
        }

        private void OnDownloadProgress(string packageId, float progress)
        {
            if (packageId != _selectedPackageId) return;
            SetProgress(progress);
        }

        private void OnPackageError(string packageId, string error)
        {
            if (packageId == _selectedPackageId)
                RefreshDetailPanel();
            SetStatus($"Error: {error}", _colorFailed);
        }

        private void OnCatalogRefreshed()
        {
            RebuildList();
            RefreshFooter();
            RefreshUpdateAllButton();
            RefreshFreeUpSpaceButton();
            _refreshing = false;
            if (_refreshButton      != null) _refreshButton.interactable      = true;
            if (_refreshButtonLabel != null) _refreshButtonLabel.text         = "Refresh Catalog";
        }

        // ── List ──────────────────────────────────────────────────────────────

        private void RebuildList()
        {
            foreach (var item in _items)
                if (item != null) Destroy(item.gameObject);
            _items.Clear();

            var visible = VisiblePackages();
            RefreshToolbarVisibility(visible.Count);

            if (visible.Count == 0)
            {
                ShowEmptyMessage("No content packages are configured.");
                return;
            }

            var shown = PackageListQuery.Apply(BuildEntries(visible), _searchText, _filter, _sort);

            if (shown.Count == 0)
            {
                // Distinguished from "no packages" on purpose. A user who has narrowed the list to
                // nothing needs to know their filter did it, not conclude the catalog is empty and
                // go looking for a fault that is not there.
                ShowEmptyMessage("No packages match this search and filter.");
                return;
            }

            if (_emptyLabel != null) _emptyLabel.gameObject.SetActive(false);

            foreach (var entry in shown)
            {
                var cfg = _settings.GetPackageConfig(entry.PackageId);
                if (cfg == null) continue;

                var item   = Instantiate(_listItemPrefab, _listContainer);
                var state  = _service.GetPackageState(cfg.packageId);
                var remote = _service.GetRemoteMetadata(cfg.packageId);
                item.Initialize(cfg, state, StatusColor(state?.status ?? PackageStatus.Available), OnItemSelected);
                item.RefreshSize(remote?.bundleSizeBytes ?? 0);
                _items.Add(item);
            }

            if (_listButtonGroup != null)
                _listButtonGroup.RefreshButtonGroup();

            // Auto-select: keep previous selection if still present, else pick first item.
            if (_items.Count > 0)
            {
                bool previousStillExists = !string.IsNullOrEmpty(_selectedPackageId)
                    && _items.Exists(i => i.PackageId == _selectedPackageId);

                string idToSelect = previousStillExists ? _selectedPackageId : _items[0].PackageId;
                OnItemSelected(idToSelect);
            }
        }

        /// <summary>Every package this panel is allowed to show, before search and filter.</summary>
        private List<ContentPackageSettings.PackageConfig> VisiblePackages()
        {
            var visible = new List<ContentPackageSettings.PackageConfig>();
            if (_settings?.packageConfigs == null) return visible;

            foreach (var cfg in _settings.packageConfigs)
                if (cfg != null && cfg.isVisible && !string.IsNullOrEmpty(cfg.packageId))
                    visible.Add(cfg);

            return visible;
        }

        /// <summary>
        /// Projects configs and live state into the rows the query operates on.
        /// </summary>
        /// <remarks>
        /// Reads <c>IsInstalled</c> and <c>HasUpdate</c> from the state records rather than inferring
        /// them from the projected status, for the reason spelled out on
        /// <see cref="PackageListEntry"/>: the filter must agree with the buttons.
        /// </remarks>
        private List<PackageListEntry> BuildEntries(List<ContentPackageSettings.PackageConfig> configs)
        {
            var entries = new List<PackageListEntry>(configs.Count);

            foreach (var cfg in configs)
            {
                var state  = _service?.GetPackageState(cfg.packageId);
                var remote = _service?.GetRemoteMetadata(cfg.packageId);

                entries.Add(new PackageListEntry(
                    cfg.packageId,
                    string.IsNullOrEmpty(cfg.displayName) ? cfg.packageId : cfg.displayName,
                    remote?.tags,
                    state?.status ?? PackageStatus.Available,
                    state != null && state.IsInstalled,
                    state != null && state.HasUpdate,
                    remote?.bundleSizeBytes ?? 0));
            }

            return entries;
        }

        private void ShowEmptyMessage(string message)
        {
            if (_emptyLabel == null) return;
            _emptyLabel.gameObject.SetActive(true);
            _emptyLabel.text = message;
        }

        // ── Search, filter, sort ──────────────────────────────────────────────

        /// <summary>Shows the toolbar only once the list is longer than a user can take in at a glance.</summary>
        private void RefreshToolbarVisibility(int packageCount)
        {
            if (_listToolbar == null) return;

            bool needed = packageCount >= PackageListQuery.ToolbarThreshold;

            // Also keep it up while a search or filter is active. Dropping below the threshold
            // because of the user's own filter must not remove the control they would need to undo
            // it -- that leaves the list stuck showing nothing with no way back.
            bool narrowed = !string.IsNullOrEmpty(_searchText) || _filter != PackageListFilter.All;

            _listToolbar.SetActive(needed || narrowed);
        }

        private void RefreshToolbarLabels()
        {
            SetText(_filterButtonLabel, PackageListQuery.Caption(_filter));
            SetText(_sortButtonLabel, PackageListQuery.Caption(_sort));
        }

        private void OnSearchChanged(string value)
        {
            _searchText = value ?? "";
            RebuildList();
        }

        private void OnFilterClicked()
        {
            _filter = PackageListQuery.Next(_filter);
            RefreshToolbarLabels();
            RebuildList();
        }

        private void OnSortClicked()
        {
            _sort = PackageListQuery.Next(_sort);
            RefreshToolbarLabels();
            RebuildList();
        }

        // ── Release activation progress ───────────────────────────────────────

        /// <summary>
        /// Renders a release activation snapshot, hiding the row when nothing is in flight.
        /// </summary>
        /// <remarks>
        /// Also disables Refresh for the duration. The coordinator refuses a concurrent activation
        /// rather than queueing it, so an enabled button here would produce a visible "another
        /// activation is already in progress" error for doing the obvious thing.
        /// </remarks>
        private void SetReleaseProgress(ReleaseActivationProgress progress)
        {
            bool active = progress.Phase != ReleaseActivationPhase.Idle;
            bool changed = active != _activatingRelease;
            _activatingRelease = active;

            if (_releaseProgressRow != null) _releaseProgressRow.SetActive(active);
            if (_releaseProgressSlider != null) _releaseProgressSlider.value = progress.Fraction;
            SetText(_releaseProgressLabel, active ? progress.Describe() : "");

            if (_refreshButton != null && !_refreshing) _refreshButton.interactable = !active;

            // Only when it flips: this runs on every progress tick, and rebuilding the detail panel
            // per frame of a download would relayout the whole right-hand side continuously.
            if (changed) RefreshDetailPanel();
        }

        private void RefreshItemCard(string packageId)
        {
            var item = _items.Find(i => i.PackageId == packageId);
            if (item == null) return;

            var state  = _service.GetPackageState(packageId);
            var remote = _service.GetRemoteMetadata(packageId);
            item.Refresh(state, StatusColor(state?.status ?? PackageStatus.Available));
            item.RefreshSize(remote?.bundleSizeBytes ?? 0);
        }

        // ── Selection ─────────────────────────────────────────────────────────

        private void OnItemSelected(string packageId)
        {
            _selectedPackageId = packageId;

            foreach (var item in _items)
                item.SetSelected(item.PackageId == packageId);

            ShowDetail(packageId);
        }

        // ── Detail panel ──────────────────────────────────────────────────────

        private void ShowDetail(string packageId)
        {
            if (string.IsNullOrEmpty(packageId) || _settings == null)
            {
                if (_detailPanel != null) _detailPanel.SetActive(false);
                return;
            }

            var cfg = _settings.GetPackageConfig(packageId);
            if (cfg == null) { if (_detailPanel != null) _detailPanel.SetActive(false); return; }

            if (_detailPanel != null) _detailPanel.SetActive(true);
            RefreshDetailPanel();
            FetchDownloadSizeAsync(packageId);
        }

        private void RefreshDetailPanel()
        {
            if (string.IsNullOrEmpty(_selectedPackageId) || _settings == null) return;

            var cfg    = _settings.GetPackageConfig(_selectedPackageId);
            var state  = _service?.GetPackageState(_selectedPackageId);
            var remote = _service?.GetRemoteMetadata(_selectedPackageId);
            if (cfg == null) return;

            var status = state?.status ?? PackageStatus.Available;

            SetText(_detailName,    string.IsNullOrEmpty(cfg.displayName) ? cfg.packageId : cfg.displayName);
            SetText(_detailId,      cfg.packageId);

            // Installed and available are two different versions and used to share one field, so a
            // player with an old copy installed was shown the version they did not have and told it
            // was theirs. Only collapse to one line when they genuinely agree.
            string installedVersion = state != null && state.IsInstalled
                ? (string.IsNullOrEmpty(state.install?.packageVersion) ? "installed" : state.install.packageVersion)
                : null;
            string availableVersion = remote?.version ?? cfg.metadata?.version;
            SetText(_detailVersion, DescribeVersions(installedVersion, availableVersion));

            var desc = remote?.description ?? cfg.metadata?.description;
            SetText(_detailDescription, string.IsNullOrEmpty(desc) ? "No description." : desc);

            SetText(_detailSize, remote != null ? SizeFormatter.Format(remote.bundleSizeBytes) : "");

            // Tags — comma-separated; hidden when empty.
            var tags = remote?.tags;
            SetText(_detailTags, tags != null && tags.Length > 0 ? string.Join(", ", tags) : "");

            // Changelog — hide row entirely when absent to save space.
            bool hasChangelog = !string.IsNullOrEmpty(remote?.changelog);
            if (_detailChangelogRow != null) _detailChangelogRow.SetActive(hasChangelog);
            if (hasChangelog) SetText(_detailChangelog, remote.changelog);

            if (_detailStatus != null)
            {
                _detailStatus.text  = StatusLabel(status);
                _detailStatus.color = StatusColor(status);
            }

            if (_detailDependencies != null)
            {
                var deps = new System.Text.StringBuilder();
                if (cfg.dependencies != null)
                    foreach (var d in cfg.dependencies)
                    {
                        if (string.IsNullOrEmpty(d.packageId)) continue;
                        deps.AppendLine($"• {d.packageId}");
                    }
                _detailDependencies.text = deps.Length > 0 ? deps.ToString().TrimEnd() : "None";
            }

            if (_detailUsedBy != null)
            {
                var usedBy = new System.Text.StringBuilder();
                foreach (var other in _settings.packageConfigs)
                {
                    if (other.dependencies == null) continue;
                    foreach (var dep in other.dependencies)
                    {
                        if (dep.packageId != cfg.packageId) continue;
                        string label = string.IsNullOrEmpty(other.displayName) ? other.packageId : other.displayName;
                        usedBy.AppendLine($"• {label}");
                        break;
                    }
                }
                _detailUsedBy.text = usedBy.Length > 0 ? usedBy.ToString().TrimEnd() : "None";
            }

            // Keyed on the error record, not on the projected status. A package whose content is
            // present but whose last update failed projects as Installed, so gating on
            // status == Failed hid exactly the case the user needs explained: their content works,
            // but it is not the version they asked for.
            bool hasError = state != null && state.HasError;
            if (_errorRow != null) _errorRow.SetActive(hasError);
            SetText(_detailErrorMessage, hasError
                ? (state.IsInstalled
                    ? $"{state.operation.errorMessage}  (the installed copy is still usable)"
                    : state.operation.errorMessage)
                : "");

            bool downloading = state != null && state.IsDownloading;
            if (_progressRow != null) _progressRow.SetActive(downloading);
            if (downloading) SetProgress(state.downloadProgress);

            // A release activation is app-wide work that downloads content, so it counts as busy even
            // though no single package owns it -- otherwise Install stays live during an activation
            // and the coordinator refuses the second operation with an error the user provoked.
            bool busy = downloading || _updatingAll || _freeingSpace || _activatingRelease;
            if (_cancelButton    != null) _cancelButton.gameObject.SetActive(busy);
            if (_installButton   != null) _installButton.gameObject.SetActive(!busy);
            if (_uninstallButton != null) _uninstallButton.gameObject.SetActive(!busy);
            if (_freeUpSpaceButton != null) _freeUpSpaceButton.gameObject.SetActive(!busy);

            bool installed = state != null && state.IsInstalled;
            bool hasUpdate = state != null && state.HasUpdate;

            if (_installButton != null)
            {
                _installButton.interactable = (!installed || hasUpdate || hasError) && !busy;
                if (_installButtonLabel != null)
                    _installButtonLabel.text = hasUpdate ? "Update" : installed ? "Repair" : "Install";
            }

            if (_uninstallButton != null)
            {
                // The service owns this rule. The UI used to re-derive it from isRequired plus its own
                // dependents scan, which is a second policy that can disagree with the one actually
                // enforced -- and when it did, the button was enabled and the operation refused.
                var permitted = _service?.ValidatePackageUninstallation(cfg.packageId);
                bool canUninstall = installed && !busy && permitted != null && permitted.Success;
                _uninstallButton.interactable = canUninstall;

                if (_detailUsedBy != null && installed && permitted != null && !permitted.Success)
                    _detailUsedBy.text = permitted.ErrorMessage;
            }

            RefreshFreeUpSpaceButton();
        }

        /// <summary>Renders installed and available versions, distinguishing them when they differ.</summary>
        /// <param name="installed">The version on disk, or null when nothing is installed.</param>
        /// <param name="available">The version the catalog offers, or null when unknown.</param>
        private static string DescribeVersions(string installed, string available)
        {
            if (installed == null) return string.IsNullOrEmpty(available) ? "—" : $"{available} (not installed)";
            if (string.IsNullOrEmpty(available)) return installed;
            return string.Equals(installed, available, System.StringComparison.Ordinal)
                ? installed
                : $"{installed} → {available}";
        }

        // ── Pre-install download size ─────────────────────────────────────────

        /// <summary>
        /// Asynchronously fetches the pending download size for <paramref name="packageId"/>
        /// and updates <see cref="_detailDownloadSize"/>. Cancels any in-flight size fetch
        /// for the previously selected package before starting.
        /// </summary>
        private async void FetchDownloadSizeAsync(string packageId)
        {
            try
            {
                CancelAndDispose(ref _downloadSizeCts);

                // Clear previous value immediately so stale data is never shown.
                SetText(_detailDownloadSize, "");

                var state  = _service?.GetPackageState(packageId);
                var status = state?.status ?? PackageStatus.Available;

                // Only relevant before content is on-device.
                if (status == PackageStatus.Installed || status == PackageStatus.Downloading) return;

                _downloadSizeCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
                var ct = _downloadSizeCts.Token;

                long bytes = await _service.GetDownloadSizeAsync(packageId);

                // Guard: user may have navigated away or CTS was disposed by OnDestroy.
                if (ct.IsCancellationRequested || packageId != _selectedPackageId) return;

                SetText(_detailDownloadSize, bytes > 0 ? $"↓ {SizeFormatter.Format(bytes)}" : "");
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

        // ── Footer ────────────────────────────────────────────────────────────

        private void RefreshFooter()
        {
            if (_settings == null || _settings.packageConfigs == null) return;

            int  count      = 0;
            long totalBytes = 0;

            foreach (var cfg in _settings.packageConfigs)
            {
                if (!cfg.isVisible) continue;
                var state = _service?.GetPackageState(cfg.packageId);
                if (state == null) continue;
                if (state.status != PackageStatus.Installed && state.status != PackageStatus.UpdateAvailable) continue;

                count++;
                var remote = _service?.GetRemoteMetadata(cfg.packageId);
                if (remote != null) totalBytes += remote.bundleSizeBytes;
            }

            if (_footerInstalledCountLabel != null)
                _footerInstalledCountLabel.text = $"{count} package{(count == 1 ? "" : "s")} installed";

            if (_footerInstalledSizeLabel != null)
                _footerInstalledSizeLabel.text = totalBytes > 0 ? SizeFormatter.Format(totalBytes) : "";
        }

        private void RefreshUpdateAllButton()
        {
            if (_updateAllButton == null) return;

            int updateCount = 0;
            if (_settings?.packageConfigs != null)
                foreach (var cfg in _settings.packageConfigs)
                {
                    var state = _service?.GetPackageState(cfg.packageId);
                    if (state?.status == PackageStatus.UpdateAvailable) updateCount++;
                }

            _updateAllButton.gameObject.SetActive(updateCount > 0);
            _updateAllButton.interactable = updateCount > 0 && !_updatingAll;
            if (_updateAllButtonLabel != null)
                _updateAllButtonLabel.text = updateCount > 1
                    ? $"Update All ({updateCount})"
                    : "Update";
        }

        private void RefreshFreeUpSpaceButton()
        {
            if (_freeUpSpaceButton == null) return;

            long reclaimableBytes = GetReclaimableBytes();
            bool hasReclaimable = reclaimableBytes > 0;

            _freeUpSpaceButton.interactable = hasReclaimable && !_freeingSpace && !_updatingAll;
            if (_freeUpSpaceButtonLabel != null)
            {
                _freeUpSpaceButtonLabel.text = hasReclaimable
                    ? $"Free Up Space ({SizeFormatter.Format(reclaimableBytes)})"
                    : "Free Up Space";
            }
        }

        private long GetReclaimableBytes()
        {
            if (_service == null) return 0;

            long total = 0;
            foreach (var packageId in _service.GetEvictionCandidates(0))
            {
                var state = _service.GetPackageState(packageId);
                if (state != null)
                    total += System.Math.Max(0, state.installedSizeBytes);
            }
            return total;
        }

        private void SetProgress(float value)
        {
            if (_progressSlider != null) _progressSlider.value = value;
            if (_progressLabel  != null) _progressLabel.text   = $"{value:P0}";
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private async void OnInstallClicked()
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedPackageId) || _service == null) return;

                CancelAndDispose(ref _installCts);
                _installCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

                SetStatus($"Installing {_selectedPackageId}…", _colorDownloading);
                SetButtonsInteractable(false);

                var progress = new System.Progress<float>(SetProgress);
                var result   = await _service.InstallPackageAsync(_selectedPackageId, progress, _installCts.Token);

                SetButtonsInteractable(true);

                if (result.Success)
                    SetStatus($"Installed: {_selectedPackageId}", _colorInstalled);
                else if (result.WasCancelled)
                    SetStatus("Cancelled.", _colorAvailable);
                else
                    SetStatus($"Failed: {result.ErrorMessage}", _colorFailed);
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

        private async void OnUninstallClicked()
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedPackageId) || _service == null) return;

                // Uninstalling deletes content the user waited to download. Asking first is cheap;
                // an accidental tap on a slow connection is not.
                var config = _settings?.GetPackageConfig(_selectedPackageId);
                string label = string.IsNullOrEmpty(config?.displayName) ? _selectedPackageId : config.displayName;
                if (!await ConfirmAsync("Remove content",
                        $"Remove {label} from this device? You can install it again later, which means " +
                        "downloading it again."))
                    return;

                CancelAndDispose(ref _installCts);
                _installCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

                SetStatus($"Uninstalling {_selectedPackageId}…", _colorDownloading);
                SetButtonsInteractable(false);

                var result = await _service.UninstallPackageAsync(_selectedPackageId, _installCts.Token);

                SetButtonsInteractable(true);

                if (result.Success)
                    SetStatus($"Uninstalled: {_selectedPackageId}", _colorAvailable);
                else
                    SetStatus($"Failed: {result.ErrorMessage}", _colorFailed);
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

        private async void OnUpdateAllClicked()
        {
            try
            {
                if (_service == null || _updatingAll) return;

                _updatingAll = true;
                RefreshUpdateAllButton();
                SetButtonsInteractable(false);

                CancelAndDispose(ref _installCts);
                _installCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
                var ct = _installCts.Token;

                var toUpdate = new List<string>();
                if (_settings?.packageConfigs != null)
                    foreach (var cfg in _settings.packageConfigs)
                    {
                        var state = _service.GetPackageState(cfg.packageId);
                        if (state != null && state.HasUpdate) toUpdate.Add(cfg.packageId);
                    }

                int succeeded = 0;
                int failed    = 0;

                foreach (var packageId in toUpdate)
                {
                    if (ct.IsCancellationRequested) break;

                    SetStatus($"Updating {packageId}… ({succeeded + failed + 1}/{toUpdate.Count})", _colorDownloading);

                    var progress = new System.Progress<float>(v =>
                    {
                        if (packageId == _selectedPackageId) SetProgress(v);
                    });

                    var result = await _service.InstallPackageAsync(packageId, progress, ct);

                    if (result.Success)        succeeded++;
                    else if (!result.WasCancelled) failed++;
                }

                _updatingAll = false;
                SetButtonsInteractable(true);
                RefreshUpdateAllButton();
                RefreshFreeUpSpaceButton();
                RefreshDetailPanel();

                if (ct.IsCancellationRequested)
                    SetStatus("Update cancelled.", _colorAvailable);
                else if (failed > 0)
                    SetStatus($"Updated {succeeded}/{toUpdate.Count}. {failed} failed.", _colorFailed);
                else
                    SetStatus($"All {succeeded} package{(succeeded == 1 ? "" : "s")} updated.", _colorInstalled);
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

        private async void OnRefreshClicked()
        {
            try
            {
                if (_refreshing || _service == null) return;
                _refreshing = true;
                if (_refreshButton      != null) _refreshButton.interactable      = false;
                if (_refreshButtonLabel != null) _refreshButtonLabel.text         = "Refreshing…";
                SetStatus("Refreshing catalog…", _colorDownloading);

                CancelAndDispose(ref _refreshCts);
                _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

                // The two content paths are alternatives, never both. Under the release protocol the
                // legacy refresh now refuses outright -- it would load a second catalog over the one
                // the coordinator committed -- so Refresh has to mean "resolve the active release"
                // instead. Same button, same intent, the path the project actually uses.
                bool cancelled;
                string failure;

                if (_service.IsReleaseProtocolEnabled)
                {
                    var activation = await _service.ActivateLatestReleaseAsync(null, _refreshCts.Token);
                    cancelled = activation.Cancelled;

                    // "Nothing new to adopt" is a success for a refresh: the player asked whether
                    // there was new content and the honest answer is no.
                    failure = activation.Success || activation.Reason == ContentReleaseReason.NoRelease
                        ? null
                        : Describe(activation);
                }
                else
                {
                    var result = await _service.RefreshCatalogAsync(_refreshCts.Token);
                    cancelled = result.WasCancelled;
                    failure = result.Success ? null : result.ErrorMessage;
                }

                // A successful refresh raises OnCatalogRefreshed, which resets the button. Every other
                // ending -- failure, cancellation, and "there was nothing new" -- has to reset it
                // here, or the button stays disabled and the panel looks permanently busy.
                if (_refreshing)
                {
                    _refreshing = false;
                    if (_refreshButton      != null) _refreshButton.interactable      = true;
                    if (_refreshButtonLabel != null) _refreshButtonLabel.text         = "Refresh Catalog";

                    if (cancelled)
                        SetStatus("Refresh cancelled.", _colorAvailable);
                    else if (failure != null)
                        SetStatus($"Refresh failed: {failure}", _colorFailed);
                    else
                        SetStatus("Content is up to date.", _colorAvailable);
                }
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

        /// <summary>
        /// Turns a failed activation into one line a user can act on.
        /// </summary>
        /// <remarks>
        /// Prefers the server's detail over the reason code. The reason set is closed and stable and
        /// is what belongs in a log; <c>appIncompatible</c> on a screen tells a player nothing, while
        /// the detail says which app version the content needs. Falls back to the code when there is
        /// no detail, because an unfamiliar code still beats a blank message — including a code from
        /// a newer server this build has never heard of.
        /// </remarks>
        private static string Describe(ReleaseActivationResult activation) =>
            !string.IsNullOrEmpty(activation.Detail) ? activation.Detail
            : !string.IsNullOrEmpty(activation.Reason) ? activation.Reason
            : "Content could not be updated.";

        private void OnCancelClicked()
        {
            CancelAndDispose(ref _freeUpSpaceCts);
            CancelAndDispose(ref _installCts);

            // Includes the release activation. Cancelling before its commit is safe by construction
            // -- the coordinator rolls the staged session back and the previous release keeps
            // running -- and a multi-gigabyte activation with no way out is not something to ship.
            CancelAndDispose(ref _refreshCts);

            SetStatus("Cancelling…", _colorAvailable);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async void OnFreeUpSpaceClicked()
        {
            if (_service == null || _freeingSpace) return;

            long reclaimableBytes = GetReclaimableBytes();
            if (reclaimableBytes <= 0)
            {
                SetStatus("No optional cached packages to remove.", _colorAvailable);
                RefreshFreeUpSpaceButton();
                return;
            }

            // Names the packages, not just a byte figure. "Free 2.3 GB" does not tell a user what
            // they are about to lose, and the plan requires destructive cache removal to be confirmed.
            var candidates = _service.GetEvictionCandidates(long.MaxValue);
            if (!await ConfirmAsync("Free up space",
                    $"Remove {candidates.Count} downloaded package(s) to free about " +
                    $"{SizeFormatter.Format(reclaimableBytes)}?" + Newline + Newline +
                    DescribeCandidates(candidates) + Newline + Newline +
                    "Required content is never removed. You can download these again later."))
                return;

            _freeingSpace = true;
            CancelAndDispose(ref _freeUpSpaceCts);
            _freeUpSpaceCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

            SetButtonsInteractable(false);
            RefreshDetailPanel();
            SetStatus($"Freeing {SizeFormatter.Format(reclaimableBytes)}...", _colorDownloading);

            OperationResult result;
            try
            {
                result = await _service.FreeUpSpaceAsync(0, _freeUpSpaceCts.Token);
            }
            catch (System.OperationCanceledException)
            {
                result = OperationResult.CreateCancelled();
            }
            catch (System.Exception ex)
            {
                result = OperationResult.CreateFailure(ex.Message);
            }

            _freeingSpace = false;
            SetButtonsInteractable(true);
            RebuildList();
            RefreshFooter();
            RefreshUpdateAllButton();
            RefreshFreeUpSpaceButton();
            RefreshDetailPanel();

            if (result.Success)
                SetStatus($"Freed up to {SizeFormatter.Format(reclaimableBytes)}.", _colorInstalled);
            else if (result.WasCancelled)
                SetStatus("Free up space cancelled.", _colorAvailable);
            else
                SetStatus($"Free up space failed: {result.ErrorMessage}", _colorFailed);
        }

        /// <summary>
        /// Asks the user to confirm a destructive action.
        /// </summary>
        /// <remarks>
        /// Refuses rather than proceeds when no <see cref="ModalManager"/> is present. Deleting
        /// downloaded content with no way to ask is worse than not offering the button: the user did
        /// not agree to it, and on a metered connection re-downloading is a real cost. The refusal
        /// says so, so a project missing the modal subsystem finds out here rather than from a
        /// support ticket.
        /// </remarks>
        /// <param name="title">Dialog title.</param>
        /// <param name="message">What is about to happen, in the user's terms.</param>
        /// <returns>True when the user agreed.</returns>
        private async Awaitable<bool> ConfirmAsync(string title, string message)
        {
            var modals = RuntimeManager.GetSubsystem<ModalManager>();
            if (modals == null)
            {
                SetStatus("Cannot confirm: no modal system is present, so this was not done.", _colorFailed);
                Debug.LogWarning("[ContentPackageManagerUI] A destructive action was refused because no " +
                                 "ModalManager is available to confirm it.");
                return false;
            }

            var completion = new AwaitableCompletionSource<bool>();
            modals.ShowRegularConfirmation(title, message, "Continue", "Cancel",
                onYes: () => completion.TrySetResult(true),
                onNo: () => completion.TrySetResult(false));

            try
            {
                return await completion.Awaitable;
            }
            catch (System.OperationCanceledException)
            {
                // The panel went away mid-question. Treat silence as "no".
                return false;
            }
        }

        /// <summary>Line break for modal body text; a named constant keeps escapes out of message literals.</summary>
        private static readonly string Newline = System.Environment.NewLine;

        /// <summary>Lists the packages an eviction would remove, by display name.</summary>
        private string DescribeCandidates(System.Collections.Generic.List<string> packageIds)
        {
            if (packageIds == null || packageIds.Count == 0) return "";
            var lines = new System.Text.StringBuilder();
            foreach (string packageId in packageIds)
            {
                var config = _settings?.GetPackageConfig(packageId);
                lines.AppendLine($"• {(string.IsNullOrEmpty(config?.displayName) ? packageId : config.displayName)}");
            }
            return lines.ToString().TrimEnd();
        }

        private void SetStatus(string message, Color color)
        {
            if (_statusLabel == null) return;
            _statusLabel.text  = message;
            _statusLabel.color = color;
        }

        private static void SetText(TextMeshProUGUI label, string value)
        {
            if (label != null) label.text = value;
        }

        private void SetButtonsInteractable(bool value)
        {
            if (_installButton   != null) _installButton.interactable   = value;
            if (_uninstallButton != null) _uninstallButton.interactable = value;
            if (_refreshButton   != null) _refreshButton.interactable   = value;
            if (_freeUpSpaceButton != null) _freeUpSpaceButton.interactable = value;
        }

        private static void CancelAndDispose(ref CancellationTokenSource cts)
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        }

        private Color StatusColor(PackageStatus status) => status switch
        {
            PackageStatus.Available       => _colorAvailable,
            PackageStatus.Downloading     => _colorDownloading,
            PackageStatus.Installed       => _colorInstalled,
            PackageStatus.Failed          => _colorFailed,
            PackageStatus.UpdateAvailable => _colorUpdate,
            _                             => _colorAvailable
        };

        private static string StatusLabel(PackageStatus status) => status switch
        {
            PackageStatus.Available       => "Available",
            PackageStatus.Downloading     => "Downloading…",
            PackageStatus.Installed       => "Installed",
            PackageStatus.Failed          => "Failed",
            PackageStatus.UpdateAvailable => "Update Available",
            _                             => "Unknown"
        };
    }
}
