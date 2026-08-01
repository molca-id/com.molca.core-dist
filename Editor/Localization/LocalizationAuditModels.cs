using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Molca.Editor
{
    /// <summary>Inputs included in a localization audit.</summary>
    [Flags]
    public enum LocalizationAuditScope
    {
        /// <summary>No serialized content inputs.</summary>
        None = 0,
        /// <summary>Project and Molca package prefabs.</summary>
        Prefabs = 1 << 0,
        /// <summary>Project and Molca package ScriptableObjects.</summary>
        ScriptableObjects = 1 << 1,
        /// <summary>Currently loaded scenes.</summary>
        LoadedScenes = 1 << 2,
        /// <summary>Enabled Editor Build Settings scenes.</summary>
        BuildScenes = 1 << 3,
        /// <summary>Unity Locale and Addressables configuration.</summary>
        Addressables = 1 << 4,
        /// <summary>Default Doctor scope.</summary>
        Doctor = Prefabs | ScriptableObjects | LoadedScenes | Addressables,
        /// <summary>Default player-build scope.</summary>
        PlayerBuild = Prefabs | ScriptableObjects | BuildScenes | Addressables,
    }

    /// <summary>Overall outcome of a localization audit.</summary>
    public enum LocalizationAuditStatus
    {
        /// <summary>Every declared input was scanned and no findings were produced.</summary>
        Clean,
        /// <summary>Every declared input was scanned and findings were produced.</summary>
        Findings,
        /// <summary>One or more declared inputs could not be scanned.</summary>
        Incomplete,
        /// <summary>The audit failed before it could produce a trustworthy result.</summary>
        Failed,
    }

    /// <summary>Severity independent of any specific audit consumer.</summary>
    public enum LocalizationAuditSeverity
    {
        /// <summary>Informational guidance.</summary>
        Info,
        /// <summary>Potential release issue that should be reviewed.</summary>
        Warning,
        /// <summary>Definite configuration or content error.</summary>
        Error,
    }

    /// <summary>Configures one deterministic localization audit.</summary>
    public sealed class LocalizationAuditRequest
    {
        /// <summary>Serialized inputs and configuration included in the scan.</summary>
        public LocalizationAuditScope Scope { get; set; } = LocalizationAuditScope.Doctor;

        /// <summary>When true, missing required inline values are errors rather than warnings.</summary>
        public bool RequireCompleteTranslations { get; set; }

        /// <summary>When true, the player build must ship freshly built Addressables content.</summary>
        public bool RequireAddressablesBuildWithPlayer { get; set; }

        /// <summary>
        /// When true, the caller already built Addressables content for this player build.
        /// </summary>
        /// <remarks>
        /// Satisfies <see cref="RequireAddressablesBuildWithPlayer"/> on its own: the policy exists to
        /// guarantee the shipped content is fresh, and an explicit pre-build content step guarantees
        /// exactly that without forcing every group — including remote ones — through Unity's
        /// build-with-player path.
        /// </remarks>
        public bool AddressablesContentAlreadyBuilt { get; set; }

        /// <summary>Optional project-relative path ignore predicate.</summary>
        public Func<string, bool> IsIgnored { get; set; }

        /// <summary>Optional progress callback receiving a short current-operation label.</summary>
        public Action<string> ReportStatus { get; set; }

        /// <summary>Cancellation token observed between every asset and scene.</summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>Creates the standard interactive Doctor request.</summary>
        /// <returns>A request that scans project/package assets and loaded scenes.</returns>
        public static LocalizationAuditRequest CreateDoctorRequest() => new();

        /// <summary>Creates the standard player-build request.</summary>
        /// <param name="production">Whether production completeness and Addressables policy are required.</param>
        /// <returns>A request covering enabled build scenes and serialized assets.</returns>
        public static LocalizationAuditRequest CreateBuildRequest(bool production) =>
            new()
            {
                Scope = LocalizationAuditScope.PlayerBuild,
                RequireCompleteTranslations = production,
                RequireAddressablesBuildWithPlayer = production,
            };
    }

    /// <summary>Stable, consumer-independent localization finding.</summary>
    public sealed class LocalizationAuditFinding
    {
        /// <summary>Creates a finding.</summary>
        /// <param name="id">Stable kebab-case finding identifier.</param>
        /// <param name="severity">Finding severity.</param>
        /// <param name="message">Human-readable explanation and repair guidance.</param>
        /// <param name="path">Project-relative asset or scene path.</param>
        /// <param name="propertyPath">Serialized property path when applicable.</param>
        public LocalizationAuditFinding(
            string id,
            LocalizationAuditSeverity severity,
            string message,
            string path = null,
            string propertyPath = null)
        {
            Id = id;
            Severity = severity;
            Message = message;
            Path = path;
            PropertyPath = propertyPath;
        }

        /// <summary>Stable finding identifier.</summary>
        public string Id { get; }

        /// <summary>Finding severity.</summary>
        public LocalizationAuditSeverity Severity { get; }

        /// <summary>Human-readable explanation and repair guidance.</summary>
        public string Message { get; }

        /// <summary>Project-relative asset or scene path.</summary>
        public string Path { get; }

        /// <summary>Serialized property path when applicable.</summary>
        public string PropertyPath { get; }
    }

    /// <summary>Coverage evidence for every declared audit input.</summary>
    public sealed class LocalizationAuditCoverage
    {
        /// <summary>Number of asset inputs declared by discovery.</summary>
        public int DeclaredAssets { get; internal set; }

        /// <summary>Number of asset inputs successfully scanned.</summary>
        public int ScannedAssets { get; internal set; }

        /// <summary>Number of asset inputs intentionally ignored by policy.</summary>
        public int IgnoredAssets { get; internal set; }

        /// <summary>Number of asset inputs that failed to load or scan.</summary>
        public int FailedAssets { get; internal set; }

        /// <summary>Number of scenes declared by the selected scope.</summary>
        public int DeclaredScenes { get; internal set; }

        /// <summary>Number of scenes successfully scanned.</summary>
        public int ScannedScenes { get; internal set; }

        /// <summary>Number of read-only package assets scanned.</summary>
        public int PackageAssets { get; internal set; }

        /// <summary>Whether every non-ignored declared input was scanned successfully.</summary>
        public bool IsComplete =>
            FailedAssets == 0 &&
            ScannedAssets + IgnoredAssets == DeclaredAssets &&
            ScannedScenes == DeclaredScenes;

        /// <summary>Returns a compact human-readable coverage summary.</summary>
        /// <returns>Counts for assets, scenes, ignored inputs, and failures.</returns>
        public string Describe() =>
            $"{ScannedAssets}/{DeclaredAssets} assets, {ScannedScenes}/{DeclaredScenes} scenes, " +
            $"{IgnoredAssets} ignored, {FailedAssets} failed";
    }

    /// <summary>Immutable result shared by Doctor, builds, Hub, MCP, and automation.</summary>
    public sealed class LocalizationAuditSnapshot
    {
        private readonly List<LocalizationAuditFinding> _findings = new();
        private readonly List<string> _sourcePaths = new();

        /// <summary>Creates an empty snapshot for an audit request.</summary>
        /// <param name="request">Request whose policy this snapshot represents.</param>
        internal LocalizationAuditSnapshot(LocalizationAuditRequest request)
        {
            SnapshotId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            Request = request;
        }

        /// <summary>Unique snapshot identifier used by preview/transaction preconditions.</summary>
        public string SnapshotId { get; }

        /// <summary>UTC creation timestamp.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>Request policy used to produce this snapshot.</summary>
        public LocalizationAuditRequest Request { get; }

        /// <summary>Fingerprint of all discovered localization source assets.</summary>
        public string CatalogFingerprint { get; internal set; } = string.Empty;

        /// <summary>Configured language codes in deterministic order.</summary>
        public IReadOnlyList<string> ConfiguredLocales { get; internal set; } = Array.Empty<string>();

        /// <summary>All stable findings.</summary>
        public IReadOnlyList<LocalizationAuditFinding> Findings => _findings;

        /// <summary>Declared-input coverage evidence.</summary>
        public LocalizationAuditCoverage Coverage { get; } = new();

        /// <summary>Discovered source paths contributing to <see cref="CatalogFingerprint"/>.</summary>
        public IReadOnlyList<string> SourcePaths => _sourcePaths;

        /// <summary>Overall audit state derived without failing open.</summary>
        public LocalizationAuditStatus Status { get; internal set; } = LocalizationAuditStatus.Clean;

        /// <summary>Error findings.</summary>
        public IReadOnlyList<LocalizationAuditFinding> Errors =>
            _findings.Where(finding => finding.Severity == LocalizationAuditSeverity.Error).ToArray();

        /// <summary>Warning findings.</summary>
        public IReadOnlyList<LocalizationAuditFinding> Warnings =>
            _findings.Where(finding => finding.Severity == LocalizationAuditSeverity.Warning).ToArray();

        /// <summary>Adds a finding while preserving engine ownership of mutable state.</summary>
        /// <param name="finding">Finding to append.</param>
        internal void AddFinding(LocalizationAuditFinding finding)
        {
            if (finding != null)
                _findings.Add(finding);
        }

        /// <summary>Adds a fingerprint source path once.</summary>
        /// <param name="path">Project-relative asset path.</param>
        internal void AddSourcePath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !_sourcePaths.Contains(path, StringComparer.Ordinal))
                _sourcePaths.Add(path);
        }
    }
}
