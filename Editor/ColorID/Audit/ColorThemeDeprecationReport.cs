#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Molca.ColorID;

namespace Molca.ColorID.Editor
{
    /// <summary>What the removal gate says about one legacy alias.</summary>
    public enum ColorAliasRemovalStatus
    {
        /// <summary>
        /// No usage anywhere the audit scanned, and the alias declares a removal version. Safe to drop in
        /// that release.
        /// </summary>
        Unused = 0,

        /// <summary>
        /// Used only by assets this project cannot write — installed package content. Removing the alias
        /// would break a consumer this project cannot migrate, so it blocks.
        /// </summary>
        UsedByPackageContent = 1,

        /// <summary>Used by project-owned content, which a migration transaction can rewrite.</summary>
        UsedByProjectContent = 2,

        /// <summary>
        /// The alias declares no removal version, so it can never be scheduled — nothing recorded which
        /// release consumers were told to expect.
        /// </summary>
        NoDeclaredLifecycle = 3
    }

    /// <summary>The removal verdict for one legacy alias, with the evidence behind it.</summary>
    public sealed class ColorAliasUsage
    {
        /// <summary>The legacy key.</summary>
        public LegacyColorKey Key { get; }

        /// <summary>The canonical token the key resolves through.</summary>
        public string CanonicalTokenId { get; }

        /// <summary>The version in which the alias may be removed, or <c>null</c>.</summary>
        public string RemovalVersion { get; }

        /// <summary>Usage sites in project-owned, writable assets.</summary>
        public int ProjectSiteCount { get; }

        /// <summary>Usage sites in installed package content this project cannot rewrite.</summary>
        public int PackageSiteCount { get; }

        /// <summary>The verdict.</summary>
        public ColorAliasRemovalStatus Status { get; }

        /// <summary>Creates a usage row.</summary>
        public ColorAliasUsage(LegacyColorKey key, string canonicalTokenId, string removalVersion,
            int projectSiteCount, int packageSiteCount)
        {
            Key = key;
            CanonicalTokenId = canonicalTokenId;
            RemovalVersion = removalVersion;
            ProjectSiteCount = projectSiteCount;
            PackageSiteCount = packageSiteCount;

            // Order matters. A missing lifecycle declaration outranks usage counts: an alias with no
            // declared removal version cannot be scheduled even at zero usage, so reporting it as "Unused"
            // would invite exactly the unannounced removal the policy exists to prevent.
            if (string.IsNullOrEmpty(removalVersion)) Status = ColorAliasRemovalStatus.NoDeclaredLifecycle;
            else if (packageSiteCount > 0) Status = ColorAliasRemovalStatus.UsedByPackageContent;
            else if (projectSiteCount > 0) Status = ColorAliasRemovalStatus.UsedByProjectContent;
            else Status = ColorAliasRemovalStatus.Unused;
        }

        /// <summary>Total sites found.</summary>
        public int TotalSiteCount => ProjectSiteCount + PackageSiteCount;

        /// <summary>Whether this alias may be removed in <see cref="RemovalVersion"/>.</summary>
        public bool IsRemovable => Status == ColorAliasRemovalStatus.Unused;
    }

    /// <summary>
    /// The evidence behind the legacy-alias removal gate: how much V1 content still exists, which aliases
    /// carry it, and which are therefore safe to drop.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Audit/</c>.
    /// <b>Shape:</b> a pure projection of a <see cref="ColorThemeAuditSnapshot"/>. It scans nothing itself
    /// and writes nothing, so it inherits the audit's single-contract and read-only rules for free — and a
    /// snapshot whose coverage is <see cref="ColorThemeCoverageStatus.Incomplete"/> produces a report that
    /// says so rather than one that under-counts.
    /// <para/>
    /// <b>Why this exists.</b> The deprecation policy says an alias may be removed in the declared major
    /// release <i>and</i> only when an audit shows no blocking usage. Without a report, that second clause
    /// is unenforceable and removal becomes a guess. Two distinctions carry the weight:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Project versus package content.</b> A migration transaction can rewrite the former and not the
    /// latter, so package usage blocks removal outright while project usage is merely work outstanding.
    /// </description></item>
    /// <item><description>
    /// <b>Zero usage is not the same as removable.</b> An alias with no declared removal version is not
    /// removable at any usage count, because no consumer was ever told when to expect it to go.
    /// </description></item>
    /// </list>
    /// <para/>
    /// <b>Known under-count: prefab-instance overrides.</b> The audit finds legacy pairs by matching the
    /// serialized field pair in asset text. A pair carried as a prefab-instance <i>override</i> is
    /// serialized as a <c>propertyPath</c>/<c>value</c> modification instead and is not matched, so it does
    /// not appear here. This was found by the content-migration previewer, which reaches content through
    /// loaded objects rather than text, and which surfaced two such sites in this project — one of them a
    /// legacy pair with no alias at all, rendering magenta.
    /// <para/>
    /// The direction of the error is the dangerous one: usage is under-reported, so an alias can look
    /// removable when content still depends on it. Until the scan understands overrides, treat
    /// <see cref="Result.Removable"/> as a candidate list to confirm against a migration preview, not as
    /// proof on its own. <c>IsConclusive</c> deliberately does not claim otherwise — it reports whether the
    /// declared inputs were all scanned, not whether the scan understands every serialization shape.
    /// </remarks>
    public static class ColorThemeDeprecationReport
    {
        /// <summary>The report for one snapshot.</summary>
        public sealed class Result
        {
            /// <summary>The snapshot this was projected from.</summary>
            public ColorThemeAuditSnapshot Snapshot { get; }

            /// <summary>Every alias in the theme set, with its verdict.</summary>
            public IReadOnlyList<ColorAliasUsage> Aliases { get; }

            /// <summary>Legacy sites whose key matches no alias — these render magenta today.</summary>
            public IReadOnlyList<ColorThemeUsageSite> UnaliasedSites { get; }

            /// <summary>Creates a result.</summary>
            public Result(ColorThemeAuditSnapshot snapshot, IReadOnlyList<ColorAliasUsage> aliases,
                IReadOnlyList<ColorThemeUsageSite> unaliasedSites)
            {
                Snapshot = snapshot;
                Aliases = aliases ?? Array.Empty<ColorAliasUsage>();
                UnaliasedSites = unaliasedSites ?? Array.Empty<ColorThemeUsageSite>();
            }

            /// <summary>Total legacy usage sites, across every alias.</summary>
            public int LegacySiteCount => Aliases.Sum(a => a.TotalSiteCount) + UnaliasedSites.Count;

            /// <summary>V2 sites: canonical references, bindings and catalog entries.</summary>
            public int CanonicalSiteCount => Snapshot.UsageSites.Count(s => !IsLegacy(s.Kind));

            /// <summary>
            /// Share of colour references that are already canonical, 0–1, or <c>-1</c> when there are none.
            /// </summary>
            public float MigrationProgress
            {
                get
                {
                    int total = LegacySiteCount + CanonicalSiteCount;
                    return total == 0 ? -1f : CanonicalSiteCount / (float)total;
                }
            }

            /// <summary>
            /// Whether the report can be used as removal evidence at all.
            /// </summary>
            /// <remarks>
            /// <c>false</c> when the snapshot skipped a declared input or carried no usage index. Either way
            /// the counts below are a floor, not a total, and a floor cannot prove absence.
            /// </remarks>
            public bool IsConclusive =>
                Snapshot.Status != ColorThemeCoverageStatus.Incomplete
                && Snapshot.Request != null && Snapshot.Request.IncludeUsageIndex;

            /// <summary>Aliases that may be removed in their declared version.</summary>
            public IEnumerable<ColorAliasUsage> Removable =>
                IsConclusive ? Aliases.Where(a => a.IsRemovable) : Enumerable.Empty<ColorAliasUsage>();

            /// <summary>Aliases that block removal, most-used first.</summary>
            public IEnumerable<ColorAliasUsage> Blocking =>
                Aliases.Where(a => !a.IsRemovable).OrderByDescending(a => a.TotalSiteCount);
        }

        /// <summary>Whether a usage kind is a V1 reference.</summary>
        /// <param name="kind">The usage kind.</param>
        public static bool IsLegacy(ColorThemeUsageKind kind) =>
            kind == ColorThemeUsageKind.LegacyColorIdComponent
            || kind == ColorThemeUsageKind.LegacyColorIdReference;

        /// <summary>Projects a report from an audit snapshot.</summary>
        /// <param name="snapshot">The snapshot. Must not be <c>null</c>.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="snapshot"/> is <c>null</c>.</exception>
        public static Result Build(ColorThemeAuditSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // Legacy sites bucketed by key, case-insensitively — LegacyColorKey's own comparison is
            // case-insensitive, so a report keyed any other way would split "Default.Text" from
            // "default.text" and under-count both.
            var projectCounts = new Dictionary<LegacyColorKey, int>();
            var packageCounts = new Dictionary<LegacyColorKey, int>();
            var matched = new HashSet<LegacyColorKey>();

            foreach (var site in snapshot.UsageSites)
            {
                if (!IsLegacy(site.Kind) || string.IsNullOrEmpty(site.LegacyKey)) continue;

                var key = ParseLegacyKey(site.LegacyKey);
                var bucket = site.IsPackageOwned ? packageCounts : projectCounts;
                bucket.TryGetValue(key, out int count);
                bucket[key] = count + 1;
            }

            var aliases = new List<ColorAliasUsage>();
            if (snapshot.ThemeSet != null)
            {
                foreach (var alias in snapshot.ThemeSet.LegacyAliases)
                {
                    if (alias == null) continue;

                    var key = alias.Key;
                    matched.Add(key);
                    projectCounts.TryGetValue(key, out int project);
                    packageCounts.TryGetValue(key, out int package);
                    aliases.Add(new ColorAliasUsage(key, alias.CanonicalTokenId, alias.RemovalVersion,
                        project, package));
                }
            }

            var unaliased = snapshot.UsageSites
                .Where(s => IsLegacy(s.Kind) && !string.IsNullOrEmpty(s.LegacyKey)
                            && !matched.Contains(ParseLegacyKey(s.LegacyKey)))
                .ToList();

            // Deterministic: most-used first, then by key, so two runs over the same state produce the same
            // text and a diff of two reports means something changed.
            aliases.Sort((a, b) =>
            {
                int byUse = b.TotalSiteCount.CompareTo(a.TotalSiteCount);
                return byUse != 0
                    ? byUse
                    : string.Compare(a.Key.ToString(), b.Key.ToString(), StringComparison.OrdinalIgnoreCase);
            });

            return new Result(snapshot, aliases, unaliased);
        }

        /// <summary>Splits a dotted <c>Swatch.ColorId</c> key.</summary>
        /// <param name="legacyKey">The dotted key as the audit recorded it.</param>
        /// <remarks>
        /// Splits on the <i>first</i> dot only. V1 colour IDs are numeric or single words today, but the
        /// swatch name is the part the format guarantees, so a key such as <c>Text.60.alt</c> stays one
        /// colour ID rather than being silently truncated.
        /// </remarks>
        private static LegacyColorKey ParseLegacyKey(string legacyKey)
        {
            int dot = legacyKey.IndexOf('.');
            return dot < 0
                ? new LegacyColorKey(legacyKey, string.Empty)
                : new LegacyColorKey(legacyKey.Substring(0, dot), legacyKey.Substring(dot + 1));
        }

        /// <summary>Renders a report as author-facing text.</summary>
        /// <param name="result">The report. Must not be <c>null</c>.</param>
        /// <returns>A multi-line summary.</returns>
        public static string Format(Result result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var text = new StringBuilder("[ColorTheme] Compatibility usage report\n");

            if (!result.IsConclusive)
            {
                text.AppendLine("  NOT CONCLUSIVE — the counts below are a lower bound and cannot be used "
                                + "as removal evidence.");
                if (result.Snapshot.Request == null || !result.Snapshot.Request.IncludeUsageIndex)
                    text.AppendLine("    The snapshot was built without a usage index.");
                foreach (var skipped in result.Snapshot.SkippedInputs)
                    text.AppendLine($"    Skipped {skipped.Key}: {skipped.Value}");
            }

            float progress = result.MigrationProgress;
            text.AppendLine(progress < 0f
                ? "  No colour references found at all."
                : $"  {result.CanonicalSiteCount} canonical / {result.LegacySiteCount} legacy sites "
                  + $"— {progress:P1} migrated.");

            if (result.UnaliasedSites.Count > 0)
            {
                text.AppendLine($"  {result.UnaliasedSites.Count} legacy site(s) match no alias and render "
                                + "magenta today:");
                foreach (var site in result.UnaliasedSites.Take(10))
                    text.AppendLine($"    {site.LegacyKey} at {site.AssetPath}");
            }

            var blocking = result.Blocking.ToList();
            if (blocking.Count > 0)
            {
                text.AppendLine("  Aliases that block removal:");
                foreach (var alias in blocking)
                {
                    text.AppendLine($"    {alias.Key} -> {alias.CanonicalTokenId}: {alias.Status} "
                                    + $"({alias.ProjectSiteCount} project, {alias.PackageSiteCount} package)");
                }
            }

            var removable = result.Removable.ToList();
            if (removable.Count > 0)
            {
                text.AppendLine($"  {removable.Count} alias(es) unused and removable in their declared "
                                + "version:");
                foreach (var alias in removable)
                    text.AppendLine($"    {alias.Key} (removable in {alias.RemovalVersion})");
            }

            return text.ToString();
        }
    }
}
#endif
