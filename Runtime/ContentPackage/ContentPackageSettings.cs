using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;
using Molca.Settings;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Settings module for the content package DLC system.
    /// Authored defaults live on this ScriptableObject; mutable runtime values are held by
    /// the paired <see cref="ContentPackageSettingsState"/> so the SO is never written at runtime.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-content.png")]
    [CreateAssetMenu(fileName = "ContentPackageSettings", menuName = "Molca/Settings/Content Package Settings", order = 10)]
    public class ContentPackageSettings : SettingModule
    {
        // ── Nested config types ──────────────────────────────────────────────

        /// <summary>A dependency reference from one package to another.</summary>
        [System.Serializable]
        public class PackageDependency
        {
            public string packageId;
        }

        /// <summary>
        /// Authored metadata seeded at edit time. At runtime these values are superseded by
        /// the remote manifest fetched from CDN — see <see cref="Core.RemotePackageEntry"/>.
        /// </summary>
        [System.Serializable]
        public class PackageMetadata
        {
            /// <summary>
            /// Authoring default; becomes the initial value written to <c>packages.json</c> by
            /// the build pipeline. The CDN version is the runtime source of truth.
            /// </summary>
            public string version = "1.0.0";
            [TextArea(2, 4)]
            public string description = "";
            public string author = "";
            public string[] tags = new string[0];
        }

        /// <summary>Authored configuration for a single content package.</summary>
        [System.Serializable]
        public class PackageConfig
        {
            public string packageId;
            public string displayName;
            public PackageMetadata metadata;
            /// <summary>When false the package is hidden from the manager UI and skipped by <see cref="PackageService"/>.</summary>
            [FormerlySerializedAs("isEnabled")]
            public bool isVisible = true;
            /// <summary>Required packages are auto-installed on startup and cannot be uninstalled.</summary>
            [FormerlySerializedAs("allowUninstall")]
            public bool isRequired = false;
            public PackageDependency[] dependencies;
            /// <summary>Addressables labels whose dependencies are downloaded when this package is installed.</summary>
            [FormerlySerializedAs("addressablesKeys")]
            public string[] addressableLabels;
        }

        // ── Read-only authored config (never mutated at runtime) ─────────────

        /// <summary>All content packages available in this project. Authored at edit time; never written at runtime.</summary>
        public List<PackageConfig> packageConfigs = new List<PackageConfig>();

        // ── Serialized defaults ──────────────────────────────────────────────

        [SerializeField] private string _remoteCatalogUrl = "";
        [SerializeField] private string _remotePackagesManifestUrl = "";
        [SerializeField] private bool   _checkForCatalogUpdates = true;
        [SerializeField] private int    _maxRetryAttempts = 3;
        [SerializeField] private bool   _enableVerboseLogging = false;

        /// <summary>
        /// When enabled, <c>packages.json</c> is expected to be a <c>ContentVersionIndex</c>
        /// (schema v2) and the system supports multi-version content switching.
        /// When disabled, <c>packages.json</c> is treated as a flat <c>RemotePackageManifest</c>
        /// (schema v1) — identical behaviour to before versioning was introduced.
        /// </summary>
        [SerializeField] private bool _enableContentVersioning = false;

        /// <summary>
        /// App version used to filter compatible content versions from the CDN index.
        /// Must be a three-part semantic version string (e.g. <c>"2.1.0"</c>).
        /// Defaults to <see cref="UnityEngine.Application.version"/> if left empty at runtime.
        /// Only used when <see cref="EnableContentVersioning"/> is <c>true</c>.
        /// </summary>
        [SerializeField] private string _appVersion = "";

        [Header("Downloads & Storage")]
        /// <summary>Maximum number of packages the download queue installs concurrently.</summary>
        [SerializeField, Min(1)] private int _maxConcurrentDownloads = 2;

        /// <summary>
        /// Soft cap on total cached package bytes. <c>0</c> means unlimited (no eviction). When the
        /// cap would be exceeded, least-recently-used non-required packages are evicted to fit.
        /// </summary>
        [SerializeField, Min(0)] private long _maxCacheBytes = 0;

        // ── Internal accessors (for SettingState seeding) ────────────────────

        internal string DefaultRemoteCatalogUrl           => _remoteCatalogUrl;
        internal string DefaultRemotePackagesManifestUrl  => _remotePackagesManifestUrl;
        internal bool   DefaultCheckForCatalogUpdates     => _checkForCatalogUpdates;
        internal int    DefaultMaxRetryAttempts           => _maxRetryAttempts;
        internal bool   DefaultEnableVerboseLogging       => _enableVerboseLogging;
        internal bool   DefaultEnableContentVersioning    => _enableContentVersioning;
        internal string DefaultAppVersion                 => _appVersion;
        internal int    DefaultMaxConcurrentDownloads     => _maxConcurrentDownloads;
        internal long   DefaultMaxCacheBytes              => _maxCacheBytes;

        // ── Public runtime properties ────────────────────────────────────────
        // In Edit mode State is null; fall back to the serialized default so Editor
        // code can safely read these without crashing.

        private ContentPackageSettingsState TypedState => (ContentPackageSettingsState)State;

        /// <summary>URL for remote content catalog.</summary>
        public string RemoteCatalogUrl
            => State != null ? TypedState.RemoteCatalogUrl : _remoteCatalogUrl;

        /// <summary>
        /// URL of the <c>packages.json</c> remote manifest deployed alongside the Addressables catalog.
        /// Platform-agnostic — typically at the CDN bucket root (e.g. <c>https://cdn.example.com/packages.json</c>).
        /// Leave empty to disable remote manifest fetching.
        /// </summary>
        public string RemotePackagesManifestUrl
            => State != null ? TypedState.RemotePackagesManifestUrl : _remotePackagesManifestUrl;

        /// <summary>Whether Addressables catalog updates are checked automatically on startup.</summary>
        public bool CheckForCatalogUpdates
            => State != null ? TypedState.CheckForCatalogUpdates : _checkForCatalogUpdates;

        /// <summary>Maximum retry attempts for a failed operation.</summary>
        public int MaxRetryAttempts
            => State != null ? TypedState.MaxRetryAttempts : _maxRetryAttempts;

        /// <summary>Whether verbose debug logging is enabled for the package system.</summary>
        public bool EnableVerboseLogging
            => State != null ? TypedState.EnableVerboseLogging : _enableVerboseLogging;

        /// <summary>
        /// When <c>true</c>, the system expects a <c>ContentVersionIndex</c> (schema v2) at the
        /// manifest URL and enables multi-version content switching via
        /// <see cref="Services.PackageService.SwitchContentVersionAsync"/>.
        /// When <c>false</c>, the manifest is treated as a flat <c>RemotePackageManifest</c>
        /// (schema v1) and versioning APIs are no-ops — identical behaviour to before
        /// content versioning was introduced.
        /// </summary>
        public bool EnableContentVersioning
            => State != null ? TypedState.EnableContentVersioning : _enableContentVersioning;

        /// <summary>
        /// App version used to filter compatible content versions from the CDN index.
        /// Falls back to <c>Application.version</c> when the serialized field is empty.
        /// </summary>
        public string AppVersion
        {
            get
            {
                var v = State != null ? TypedState.AppVersion : _appVersion;
                return string.IsNullOrEmpty(v) ? Application.version : v;
            }
        }

        /// <summary>Maximum number of packages the download queue installs concurrently (>= 1).</summary>
        public int MaxConcurrentDownloads
            => Mathf.Max(1, State != null ? TypedState.MaxConcurrentDownloads : _maxConcurrentDownloads);

        /// <summary>Soft cap on total cached package bytes; 0 means unlimited (no eviction).</summary>
        public long MaxCacheBytes
            => System.Math.Max(0, State != null ? TypedState.MaxCacheBytes : _maxCacheBytes);

        // Hard-coded operational constants (not user-configurable)
        public const int   DownloadTimeoutSeconds = 300;
        public const float InitialRetryDelay      = 1f;
        public const float MaxRetryDelay          = 30f;

        // ── SettingModule implementation ─────────────────────────────────────

        /// <inheritdoc/>
        public override SettingState CreateState() => new ContentPackageSettingsState(this);

        /// <inheritdoc/>
        public override void LoadSettings() { if (State != null) TypedState.Load(this); }

        /// <inheritdoc/>
        public override void SaveSettings() { if (State != null) TypedState.Save(this); }

        // ── Utility methods ──────────────────────────────────────────────────

        /// <summary>Returns the <see cref="PackageConfig"/> with the given ID, or <c>null</c> if not found.</summary>
        public PackageConfig GetPackageConfig(string packageId)
            => packageConfigs.Find(c => c.packageId == packageId);

        /// <summary>Returns all configs where <see cref="PackageConfig.isVisible"/> is <c>true</c>.</summary>
        public List<PackageConfig> GetVisiblePackages()
            => packageConfigs.FindAll(c => c.isVisible);

        /// <summary>
        /// Validates package configurations and returns a list of human-readable error strings.
        /// </summary>
        public List<string> ValidateConfigurations()
        {
            var errors = new List<string>();
            foreach (var config in packageConfigs)
            {
                if (string.IsNullOrEmpty(config.packageId))
                    errors.Add("Package configuration missing packageId");

                if (string.IsNullOrEmpty(config.displayName))
                    errors.Add($"Package '{config.packageId}' missing display name");

                if (config.addressableLabels == null || config.addressableLabels.Length == 0)
                    errors.Add($"Package '{config.packageId}' has no Addressable labels defined");
            }
            return errors;
        }

        /// <summary>Resets the runtime state to the authored defaults.</summary>
        /// <remarks>
        /// The SerializeFields ARE the defaults and are never written at runtime
        /// (SO cardinal rule); only the paired state is reset and re-persisted.
        /// </remarks>
        public override void ResetToDefaults()
        {
            var state = TypedState;
            if (state == null)
                return;

            state.RemoteCatalogUrl          = _remoteCatalogUrl;
            state.RemotePackagesManifestUrl = _remotePackagesManifestUrl;
            state.CheckForCatalogUpdates    = _checkForCatalogUpdates;
            state.MaxRetryAttempts          = _maxRetryAttempts;
            state.EnableVerboseLogging      = _enableVerboseLogging;
            state.EnableContentVersioning   = _enableContentVersioning;
            state.AppVersion                = _appVersion;
            state.MaxConcurrentDownloads    = _maxConcurrentDownloads;
            state.MaxCacheBytes             = _maxCacheBytes;
            state.Save(this);
        }
    }

    /// <summary>
    /// Mutable runtime state for <see cref="ContentPackageSettings"/>.
    /// </summary>
    public class ContentPackageSettingsState : SettingState
    {
        public string RemoteCatalogUrl;
        public string RemotePackagesManifestUrl;
        public bool   CheckForCatalogUpdates;
        public int    MaxRetryAttempts;
        public bool   EnableVerboseLogging;
        public string AppVersion;
        public bool   EnableContentVersioning;
        public int    MaxConcurrentDownloads;
        public long   MaxCacheBytes;

        /// <summary>Seeds all fields from the module's authored defaults.</summary>
        public ContentPackageSettingsState(ContentPackageSettings m)
        {
            RemoteCatalogUrl          = m.DefaultRemoteCatalogUrl;
            RemotePackagesManifestUrl = m.DefaultRemotePackagesManifestUrl;
            CheckForCatalogUpdates    = m.DefaultCheckForCatalogUpdates;
            MaxRetryAttempts          = m.DefaultMaxRetryAttempts;
            EnableVerboseLogging      = m.DefaultEnableVerboseLogging;
            EnableContentVersioning   = m.DefaultEnableContentVersioning;
            AppVersion                = m.DefaultAppVersion;
            MaxConcurrentDownloads    = m.DefaultMaxConcurrentDownloads;
            MaxCacheBytes             = m.DefaultMaxCacheBytes;
        }

        /// <inheritdoc/>
        public override void Load(SettingModule owner)
        {
            RemoteCatalogUrl          = owner.LoadString(nameof(RemoteCatalogUrl), RemoteCatalogUrl);
            RemotePackagesManifestUrl = owner.LoadString(nameof(RemotePackagesManifestUrl), RemotePackagesManifestUrl);
            CheckForCatalogUpdates    = owner.LoadInt(nameof(CheckForCatalogUpdates), CheckForCatalogUpdates ? 1 : 0) == 1;
            MaxRetryAttempts          = owner.LoadInt(nameof(MaxRetryAttempts), MaxRetryAttempts);
            EnableVerboseLogging      = owner.LoadInt(nameof(EnableVerboseLogging), EnableVerboseLogging ? 1 : 0) == 1;
            EnableContentVersioning   = owner.LoadInt(nameof(EnableContentVersioning), EnableContentVersioning ? 1 : 0) == 1;
            AppVersion                = owner.LoadString(nameof(AppVersion), AppVersion);
            MaxConcurrentDownloads    = owner.LoadInt(nameof(MaxConcurrentDownloads), MaxConcurrentDownloads);
            MaxCacheBytes             = long.TryParse(
                owner.LoadString(nameof(MaxCacheBytes), MaxCacheBytes.ToString()), out var maxCache)
                ? maxCache : MaxCacheBytes;
        }

        /// <inheritdoc/>
        public override void Save(SettingModule owner)
        {
            owner.SaveString(nameof(RemoteCatalogUrl), RemoteCatalogUrl);
            owner.SaveString(nameof(RemotePackagesManifestUrl), RemotePackagesManifestUrl);
            owner.SaveInt(nameof(CheckForCatalogUpdates), CheckForCatalogUpdates ? 1 : 0);
            owner.SaveInt(nameof(MaxRetryAttempts), MaxRetryAttempts);
            owner.SaveInt(nameof(EnableVerboseLogging), EnableVerboseLogging ? 1 : 0);
            owner.SaveInt(nameof(EnableContentVersioning), EnableContentVersioning ? 1 : 0);
            owner.SaveString(nameof(AppVersion), AppVersion);
            owner.SaveInt(nameof(MaxConcurrentDownloads), MaxConcurrentDownloads);
            owner.SaveString(nameof(MaxCacheBytes), MaxCacheBytes.ToString());
        }
    }
}
