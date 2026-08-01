#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Molca.ColorID;

namespace Molca.ColorID.Editor
{
    /// <summary>Severity of a colour-theme audit finding.</summary>
    public enum ColorThemeFindingSeverity
    {
        /// <summary>Informational; never blocks anything.</summary>
        Info = 0,

        /// <summary>Worth fixing but not shipping-blocking.</summary>
        Warning = 1,

        /// <summary>Blocks a production build.</summary>
        Error = 2
    }

    /// <summary>What kind of problem a finding describes.</summary>
    /// <remarks>
    /// A stable, filterable vocabulary shared by the Hub, Doctor, the build gate and MCP. Codes are
    /// deliberately coarser than messages: the message explains one site, the code groups every site
    /// with the same cause so a workspace can offer one action for all of them.
    /// </remarks>
    public enum ColorThemeFindingKind
    {
        /// <summary>No theme settings module, or it references no theme set.</summary>
        SettingsMissing,

        /// <summary>The theme set failed structural validation.</summary>
        ThemeSetInvalid,

        /// <summary>A required token does not resolve in some selectable variant.</summary>
        RequiredTokenMissingInVariant,

        /// <summary>Alias resolution found a cycle or an over-deep chain.</summary>
        AliasCycle,

        /// <summary>A declared token that no reference anywhere uses.</summary>
        UnusedToken,

        /// <summary>A serialized reference that does not resolve in some selectable variant.</summary>
        UnresolvedReference,

        /// <summary>A legacy pair with no authored alias, resolving by guess or not at all.</summary>
        UnmappedLegacyPair,

        /// <summary>A legacy bare ID matching more than one canonical token.</summary>
        AmbiguousLegacyPair,

        /// <summary>A deprecated token still referenced by content.</summary>
        DeprecatedTokenInUse,

        /// <summary>An authored contrast requirement that fails.</summary>
        ContrastFailure,

        /// <summary>A contrast requirement that cannot be measured as authored.</summary>
        ContrastIncomplete,

        /// <summary>Generated UI Toolkit output is missing or stale.</summary>
        GeneratedOutputStale,

        /// <summary>A declared scan input could not be read, so coverage is incomplete.</summary>
        CoverageIncomplete
    }

    /// <summary>One audit finding.</summary>
    /// <remarks>
    /// Immutable. Findings are produced by a read-only scan and consumed by several surfaces; a mutable
    /// finding would let one consumer's presentation change what another sees.
    /// </remarks>
    public sealed class ColorThemeFinding
    {
        /// <summary>What kind of problem this is.</summary>
        public ColorThemeFindingKind Kind { get; }

        /// <summary>How serious it is.</summary>
        public ColorThemeFindingSeverity Severity { get; }

        /// <summary>What is wrong, and what to do about it.</summary>
        public string Message { get; }

        /// <summary>Project-relative asset path, when the finding has one.</summary>
        public string AssetPath { get; }

        /// <summary>The canonical token or legacy key involved, when applicable.</summary>
        public string Subject { get; }

        /// <summary>The variant this finding applies to, or <c>null</c> when variant-independent.</summary>
        public string VariantId { get; }

        /// <summary>
        /// Whether the site is package-owned and therefore not writable by project tooling.
        /// </summary>
        /// <remarks>
        /// Carried on the finding rather than derived later so every consumer makes the same call. A
        /// rename transaction reports these instead of attempting a mutation that would either fail or
        /// modify an installed package.
        /// </remarks>
        public bool IsPackageOwned { get; }

        /// <summary>Creates a finding.</summary>
        public ColorThemeFinding(ColorThemeFindingKind kind, ColorThemeFindingSeverity severity,
            string message, string assetPath = null, string subject = null, string variantId = null,
            bool isPackageOwned = false)
        {
            Kind = kind;
            Severity = severity;
            Message = message;
            AssetPath = assetPath;
            Subject = subject;
            VariantId = variantId;
            IsPackageOwned = isPackageOwned;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            string where = string.IsNullOrEmpty(AssetPath) ? "" : $" ({AssetPath})";
            return $"[{Severity}] {Kind}: {Message}{where}";
        }
    }

    /// <summary>What kind of reference a usage site is.</summary>
    public enum ColorThemeUsageKind
    {
        /// <summary>A legacy <see cref="ColorID"/> component.</summary>
        LegacyColorIdComponent,

        /// <summary>A legacy <see cref="ColorIDReference"/> field on some component.</summary>
        LegacyColorIdReference,

        /// <summary>A V2 <see cref="ColorTokenReference"/> field.</summary>
        CanonicalTokenReference,

        /// <summary>A V2 <see cref="ColorThemeBinding"/> binding.</summary>
        ThemeBinding,

        /// <summary>A UI Token Catalog colour entry.</summary>
        UiTokenCatalogEntry
    }

    /// <summary>One place in the project that references a colour.</summary>
    public sealed class ColorThemeUsageSite
    {
        /// <summary>What kind of reference this is.</summary>
        public ColorThemeUsageKind Kind { get; }

        /// <summary>Project-relative asset path.</summary>
        public string AssetPath { get; }

        /// <summary>
        /// The canonical token this site resolves to, or <c>null</c> when it does not resolve.
        /// </summary>
        public string CanonicalTokenId { get; }

        /// <summary>The raw legacy key for a legacy site, or <c>null</c>.</summary>
        public string LegacyKey { get; }

        /// <summary>Whether the owning asset is package-owned and therefore read-only.</summary>
        public bool IsPackageOwned { get; }

        /// <summary>Creates a usage site.</summary>
        public ColorThemeUsageSite(ColorThemeUsageKind kind, string assetPath, string canonicalTokenId,
            string legacyKey, bool isPackageOwned)
        {
            Kind = kind;
            AssetPath = assetPath;
            CanonicalTokenId = canonicalTokenId;
            LegacyKey = legacyKey;
            IsPackageOwned = isPackageOwned;
        }

        /// <summary>Whether this site is writable by project tooling.</summary>
        public bool IsWritable => !IsPackageOwned;
    }

    /// <summary>Per-variant token coverage.</summary>
    public sealed class ColorThemeVariantCoverage
    {
        /// <summary>The variant.</summary>
        public string VariantId { get; }

        /// <summary>Whether the variant resolved at all.</summary>
        public bool Resolved { get; }

        /// <summary>Tokens the variant resolves.</summary>
        public int ResolvedTokenCount { get; }

        /// <summary>Required tokens the variant does not resolve.</summary>
        public IReadOnlyList<string> MissingRequiredTokens { get; }

        /// <summary>Optional tokens the variant does not resolve.</summary>
        public IReadOnlyList<string> MissingOptionalTokens { get; }

        /// <summary>Creates coverage.</summary>
        public ColorThemeVariantCoverage(string variantId, bool resolved, int resolvedTokenCount,
            IReadOnlyList<string> missingRequired, IReadOnlyList<string> missingOptional)
        {
            VariantId = variantId;
            Resolved = resolved;
            ResolvedTokenCount = resolvedTokenCount;
            MissingRequiredTokens = missingRequired ?? Array.Empty<string>();
            MissingOptionalTokens = missingOptional ?? Array.Empty<string>();
        }

        /// <summary>Whether this variant satisfies the whole required contract.</summary>
        public bool IsComplete => Resolved && MissingRequiredTokens.Count == 0;
    }

    /// <summary>A category of input the audit declares it will scan.</summary>
    public enum ColorThemeScanInput
    {
        /// <summary>The theme settings module and theme set.</summary>
        ThemeSettings,

        /// <summary>Prefabs and ScriptableObjects under <c>Assets/</c>.</summary>
        ProjectAssets,

        /// <summary>Prefabs and assets inside installed Molca packages.</summary>
        PackageAssets,

        /// <summary>Scenes currently open in the editor.</summary>
        OpenScenes,

        /// <summary>Scenes on disk that are not open.</summary>
        ClosedScenes,

        /// <summary>UI Token Catalog assets.</summary>
        UiTokenCatalogs,

        /// <summary>Generated UI Toolkit output and its manifest.</summary>
        GeneratedArtifacts
    }

    /// <summary>What the audit was asked to cover.</summary>
    public sealed class ColorThemeAuditRequest
    {
        /// <summary>The inputs to scan.</summary>
        public IReadOnlyCollection<ColorThemeScanInput> Inputs { get; }

        /// <summary>Whether to build the full usage index. Off makes a validity-only scan much faster.</summary>
        public bool IncludeUsageIndex { get; }

        /// <summary>Creates a request.</summary>
        /// <param name="inputs">Inputs to scan; <c>null</c> means <see cref="Default"/>'s set.</param>
        /// <param name="includeUsageIndex">Whether to build the usage index.</param>
        public ColorThemeAuditRequest(IReadOnlyCollection<ColorThemeScanInput> inputs = null,
            bool includeUsageIndex = true)
        {
            Inputs = inputs ?? DefaultInputs;
            IncludeUsageIndex = includeUsageIndex;
        }

        /// <summary>
        /// The inputs a full audit declares.
        /// </summary>
        /// <remarks>
        /// <see cref="ColorThemeScanInput.ClosedScenes"/> is declared even though scanning it is
        /// expensive, because omitting it from the declaration is what would let a partial scan report
        /// Clean. A scan that skips it reports <see cref="ColorThemeCoverageStatus.Incomplete"/> instead
        /// of quietly narrowing what "complete" means.
        /// </remarks>
        public static readonly ColorThemeScanInput[] DefaultInputs =
        {
            ColorThemeScanInput.ThemeSettings,
            ColorThemeScanInput.ProjectAssets,
            ColorThemeScanInput.PackageAssets,
            ColorThemeScanInput.OpenScenes,
            ColorThemeScanInput.ClosedScenes,
            ColorThemeScanInput.UiTokenCatalogs,
            ColorThemeScanInput.GeneratedArtifacts
        };

        /// <summary>A full audit of every declared input.</summary>
        public static ColorThemeAuditRequest Default => new ColorThemeAuditRequest();

        /// <summary>
        /// A fast audit that validates the theme set and open content only.
        /// </summary>
        /// <remarks>
        /// Deliberately does not declare closed scenes or package assets, so its result can never read
        /// as Clean — a quick check is honest about being partial.
        /// </remarks>
        public static ColorThemeAuditRequest Quick => new ColorThemeAuditRequest(
            new[]
            {
                ColorThemeScanInput.ThemeSettings,
                ColorThemeScanInput.ProjectAssets,
                ColorThemeScanInput.OpenScenes
            },
            includeUsageIndex: false);
    }

    /// <summary>Overall health of an audit result.</summary>
    public enum ColorThemeCoverageStatus
    {
        /// <summary>Every declared input was scanned and nothing was found.</summary>
        Clean = 0,

        /// <summary>
        /// Every declared input was scanned and findings exist.
        /// </summary>
        Findings = 1,

        /// <summary>
        /// A declared input was skipped or failed, so absence of findings proves nothing.
        /// </summary>
        /// <remarks>
        /// The distinction that makes the whole audit trustworthy: a scan that could not read the
        /// closed scenes has not shown the project is clean, it has shown that it does not know. V1
        /// scanning was limited to <c>Assets/</c> and never opened closed scenes, yet reported a clean
        /// result — which is how package prefabs with broken references shipped.
        /// </remarks>
        Incomplete = 2
    }

    /// <summary>
    /// One immutable, read-only audit result, shared by the Hub, Doctor, the build gate, MCP and
    /// migration planning.
    /// </summary>
    /// <remarks>
    /// The <see cref="Fingerprint"/> is what binds a transaction to the state it was planned against:
    /// an executor refuses to apply a plan whose snapshot fingerprint no longer matches, so a plan built
    /// against stale data cannot silently rewrite something that has since changed.
    /// </remarks>
    public sealed class ColorThemeAuditSnapshot
    {
        /// <summary>What was requested.</summary>
        public ColorThemeAuditRequest Request { get; }

        /// <summary>The theme set audited, or <c>null</c> in a legacy-only project.</summary>
        public ColorThemeSet ThemeSet { get; }

        /// <summary>Every finding, most severe first.</summary>
        public IReadOnlyList<ColorThemeFinding> Findings { get; }

        /// <summary>Per-variant coverage.</summary>
        public IReadOnlyList<ColorThemeVariantCoverage> VariantCoverage { get; }

        /// <summary>Every reference site found, when the usage index was requested.</summary>
        public IReadOnlyList<ColorThemeUsageSite> UsageSites { get; }

        /// <summary>Inputs that were actually scanned.</summary>
        public IReadOnlyCollection<ColorThemeScanInput> ScannedInputs { get; }

        /// <summary>Declared inputs that were skipped, with the reason.</summary>
        public IReadOnlyDictionary<ColorThemeScanInput, string> SkippedInputs { get; }

        /// <summary>
        /// Identity of the audited state, for binding transactions to it.
        /// </summary>
        /// <remarks>
        /// Combines the theme set's resolved variant fingerprints with the count and identity of the
        /// scanned usage sites, so both a palette edit and a content change invalidate a plan.
        /// </remarks>
        public string Fingerprint { get; }

        /// <summary>Creates a snapshot.</summary>
        public ColorThemeAuditSnapshot(ColorThemeAuditRequest request, ColorThemeSet themeSet,
            IReadOnlyList<ColorThemeFinding> findings,
            IReadOnlyList<ColorThemeVariantCoverage> variantCoverage,
            IReadOnlyList<ColorThemeUsageSite> usageSites,
            IReadOnlyCollection<ColorThemeScanInput> scannedInputs,
            IReadOnlyDictionary<ColorThemeScanInput, string> skippedInputs,
            string fingerprint)
        {
            Request = request;
            ThemeSet = themeSet;
            Findings = findings ?? Array.Empty<ColorThemeFinding>();
            VariantCoverage = variantCoverage ?? Array.Empty<ColorThemeVariantCoverage>();
            UsageSites = usageSites ?? Array.Empty<ColorThemeUsageSite>();
            ScannedInputs = scannedInputs ?? Array.Empty<ColorThemeScanInput>();
            SkippedInputs = skippedInputs ?? new Dictionary<ColorThemeScanInput, string>();
            Fingerprint = fingerprint;
        }

        /// <summary>Overall health.</summary>
        /// <remarks>
        /// Incomplete outranks everything: if a declared input was skipped, the result is Incomplete
        /// whether or not findings exist, because the finding list cannot be trusted to be exhaustive.
        /// </remarks>
        public ColorThemeCoverageStatus Status
        {
            get
            {
                if (SkippedInputs.Count > 0) return ColorThemeCoverageStatus.Incomplete;
                return Findings.Count > 0
                    ? ColorThemeCoverageStatus.Findings
                    : ColorThemeCoverageStatus.Clean;
            }
        }

        /// <summary>Whether any finding would block a production build.</summary>
        public bool HasErrors
        {
            get
            {
                foreach (var finding in Findings)
                {
                    if (finding.Severity == ColorThemeFindingSeverity.Error) return true;
                }
                return false;
            }
        }

        /// <summary>Every usage site that resolves to a token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <returns>A fresh list; safe for the caller to keep.</returns>
        public List<ColorThemeUsageSite> GetSitesForToken(string tokenId)
        {
            var sites = new List<ColorThemeUsageSite>();
            if (string.IsNullOrEmpty(tokenId)) return sites;

            foreach (var site in UsageSites)
            {
                if (string.Equals(site.CanonicalTokenId, tokenId, StringComparison.Ordinal))
                    sites.Add(site);
            }
            return sites;
        }

        /// <summary>A one-line summary for logs and CI output.</summary>
        public override string ToString()
        {
            string skipped = SkippedInputs.Count == 0
                ? ""
                : $", skipped: {string.Join(", ", SkippedInputs.Keys)}";
            return $"{Status}: {Findings.Count} finding(s) across {VariantCoverage.Count} variant(s), "
                   + $"{UsageSites.Count} usage site(s){skipped}";
        }
    }
}
#endif
