#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>One token's row in the variant matrix.</summary>
    public sealed class ColorThemeTokenRow
    {
        /// <summary>The token's definition.</summary>
        public ColorTokenDefinition Definition { get; }

        /// <summary>Resolved colour per variant ID. A variant that cannot resolve it is absent.</summary>
        public IReadOnlyDictionary<string, Color> Values { get; }

        /// <summary>The alias each variant resolves through, or <c>null</c> for a literal.</summary>
        public IReadOnlyDictionary<string, string> Sources { get; }

        /// <summary>How many references the audit found to this token.</summary>
        public int UsageCount { get; }

        /// <summary>Creates a row.</summary>
        public ColorThemeTokenRow(ColorTokenDefinition definition, IReadOnlyDictionary<string, Color> values,
            IReadOnlyDictionary<string, string> sources, int usageCount)
        {
            Definition = definition;
            Values = values;
            Sources = sources;
            UsageCount = usageCount;
        }

        /// <summary>Variants that do not resolve this token.</summary>
        /// <param name="variantIds">Every variant in the set.</param>
        public IEnumerable<string> MissingIn(IEnumerable<string> variantIds) =>
            variantIds.Where(id => !Values.ContainsKey(id));

        /// <summary>Whether every variant resolves it.</summary>
        /// <param name="variantIds">Every variant in the set.</param>
        public bool IsComplete(IEnumerable<string> variantIds) => !MissingIn(variantIds).Any();
    }

    /// <summary>One contrast requirement, measured in every variant.</summary>
    public sealed class ColorThemeContrastRow
    {
        /// <summary>The requirement.</summary>
        public ColorContrastRequirement Requirement { get; }

        /// <summary>Measured ratio per variant. A variant that could not be measured is absent.</summary>
        public IReadOnlyDictionary<string, float> Ratios { get; }

        /// <summary>Variants where the requirement is not met.</summary>
        public IReadOnlyList<string> FailingVariants { get; }

        /// <summary>Creates a row.</summary>
        public ColorThemeContrastRow(ColorContrastRequirement requirement,
            IReadOnlyDictionary<string, float> ratios, IReadOnlyList<string> failingVariants)
        {
            Requirement = requirement;
            Ratios = ratios;
            FailingVariants = failingVariants ?? Array.Empty<string>();
        }

        /// <summary>Whether it holds everywhere it could be measured.</summary>
        public bool Passes => FailingVariants.Count == 0;
    }

    /// <summary>
    /// Everything the Themes workspace displays, gathered once from the shared editor services.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Hub/</c>.
    /// <b>Shape:</b> an immutable snapshot, rebuilt on demand.
    /// <para/>
    /// <b>Why a model and not view code that queries as it draws.</b> Plan §11.1 requires the workspace to
    /// consume shared services rather than embed logic, and the sharper reason is consistency: the Overview
    /// mode's health, the Tokens matrix, the Accessibility table and the Migration progress are four views
    /// of one state. Gathering per panel would let them disagree — the matrix showing a token the health
    /// summary had already counted as missing — and a disagreement between two panels of the same window is
    /// worse than a stale number in both.
    /// <para/>
    /// Everything here is read-only. Mutations go through the transaction and migration services, and the
    /// view rebuilds a fresh model afterwards rather than patching this one.
    /// </remarks>
    public sealed class ColorThemeWorkspaceModel
    {
        /// <summary>The audit this model was projected from.</summary>
        public ColorThemeAuditSnapshot Audit { get; }

        /// <summary>The installed theme set, or <c>null</c> in a legacy-only project.</summary>
        public ColorThemeSet ThemeSet { get; }

        /// <summary>Project-relative path of the theme set, or <c>null</c>.</summary>
        public string ThemeSetPath { get; }

        /// <summary>Every variant ID, in authored order.</summary>
        public IReadOnlyList<string> VariantIds { get; }

        /// <summary>The variant the settings module starts on, or <c>null</c>.</summary>
        public string DefaultVariantId { get; }

        /// <summary>One row per declared token.</summary>
        public IReadOnlyList<ColorThemeTokenRow> Tokens { get; }

        /// <summary>One row per contrast requirement.</summary>
        public IReadOnlyList<ColorThemeContrastRow> Contrast { get; }

        /// <summary>Compatibility usage and the alias removal gate.</summary>
        public ColorThemeDeprecationReport.Result Deprecation { get; }

        /// <summary>Why the model could not be built fully, or empty.</summary>
        public IReadOnlyList<string> Problems { get; }

        private ColorThemeWorkspaceModel(ColorThemeAuditSnapshot audit, ColorThemeSet themeSet,
            string themeSetPath, IReadOnlyList<string> variantIds, string defaultVariantId,
            IReadOnlyList<ColorThemeTokenRow> tokens, IReadOnlyList<ColorThemeContrastRow> contrast,
            ColorThemeDeprecationReport.Result deprecation, IReadOnlyList<string> problems)
        {
            Audit = audit;
            ThemeSet = themeSet;
            ThemeSetPath = themeSetPath;
            VariantIds = variantIds ?? Array.Empty<string>();
            DefaultVariantId = defaultVariantId;
            Tokens = tokens ?? Array.Empty<ColorThemeTokenRow>();
            Contrast = contrast ?? Array.Empty<ColorThemeContrastRow>();
            Deprecation = deprecation;
            Problems = problems ?? Array.Empty<string>();
        }

        /// <summary>Whether a V2 theme set is installed at all.</summary>
        public bool IsInstalled => ThemeSet != null;

        /// <summary>Overall health, mirroring the audit's own vocabulary.</summary>
        public MolcaStatusForTheme Health
        {
            get
            {
                if (!IsInstalled) return MolcaStatusForTheme.NotInstalled;
                if (Audit.Status == ColorThemeCoverageStatus.Incomplete) return MolcaStatusForTheme.Incomplete;
                if (Audit.HasErrors) return MolcaStatusForTheme.Errors;
                return Audit.Findings.Count > 0 ? MolcaStatusForTheme.Warnings : MolcaStatusForTheme.Clean;
            }
        }

        /// <summary>Tokens no variant resolves, or that some variant misses.</summary>
        public IEnumerable<ColorThemeTokenRow> IncompleteTokens =>
            Tokens.Where(t => !t.IsComplete(VariantIds));

        /// <summary>Builds a model. Runs a full audit, so it is not cheap.</summary>
        /// <param name="request">What the audit should cover; <c>null</c> means a full one.</param>
        /// <returns>The model.</returns>
        public static ColorThemeWorkspaceModel Build(ColorThemeAuditRequest request = null)
        {
            var audit = ColorThemeAuditService.Run(request ?? ColorThemeAuditRequest.Default);
            var problems = new List<string>();

            var themeSet = audit.ThemeSet;
            string themeSetPath = themeSet == null ? null : AssetDatabase.GetAssetPath(themeSet);

            if (themeSet == null)
            {
                problems.Add("No Color Theme Set is installed. Run Molca ▸ ColorID ▸ Install Color Theme "
                             + "Settings (V1 → V2).");
                return new ColorThemeWorkspaceModel(audit, null, null, null, null, null, null,
                    ColorThemeDeprecationReport.Build(audit), problems);
            }

            var variantIds = themeSet.GetVariantIds().ToList();

            // Resolved once per variant and shared by every row. Resolving per token would repeat the whole
            // alias walk for each of them, and — worse — a mid-loop edit could give two rows values from
            // different states of the same variant.
            var resolved = new Dictionary<string, ResolvedColorTheme>();
            foreach (string variantId in variantIds)
            {
                if (ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme, out var diagnostics)
                    == ColorThemeActivation.Activated)
                {
                    resolved[variantId] = theme;
                }
                else
                {
                    problems.Add($"Variant '{variantId}' did not resolve: {string.Join("; ", diagnostics)}");
                }
            }

            var usageCounts = CountUsage(audit);

            var tokens = themeSet.TokenDefinitions
                .Select(definition => BuildTokenRow(definition, themeSet, resolved, usageCounts))
                .ToList();

            var contrast = themeSet.AccessibilityRequirements
                .Select(requirement => BuildContrastRow(requirement, themeSet, resolved))
                .ToList();

            return new ColorThemeWorkspaceModel(audit, themeSet, themeSetPath, variantIds,
                ResolveDefaultVariantId(), tokens, contrast, ColorThemeDeprecationReport.Build(audit),
                problems);
        }

        private static Dictionary<string, int> CountUsage(ColorThemeAuditSnapshot audit)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var site in audit.UsageSites)
            {
                if (string.IsNullOrEmpty(site.CanonicalTokenId)) continue;
                counts.TryGetValue(site.CanonicalTokenId, out int count);
                counts[site.CanonicalTokenId] = count + 1;
            }
            return counts;
        }

        private static ColorThemeTokenRow BuildTokenRow(ColorTokenDefinition definition,
            ColorThemeSet themeSet, IReadOnlyDictionary<string, ResolvedColorTheme> resolved,
            IReadOnlyDictionary<string, int> usageCounts)
        {
            var values = new Dictionary<string, Color>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var pair in resolved)
            {
                if (pair.Value.TryGetColor(definition.Id, out Color color)) values[pair.Key] = color;

                // The authored expression, not the resolved value: an author inspecting a row wants to know
                // *why* it is this colour, and "aliases palette/ink/base at 0.6" answers that where a hex
                // string does not.
                var variant = themeSet.GetVariant(pair.Key);
                if (variant == null) continue;

                foreach (var value in variant.Values)
                {
                    if (value.TokenId != definition.Id) continue;
                    if (value.Expression.ExpressionKind == ColorExpression.Kind.Alias)
                        sources[pair.Key] = value.Expression.AliasTokenId;
                    break;
                }
            }

            usageCounts.TryGetValue(definition.Id, out int usage);
            return new ColorThemeTokenRow(definition, values, sources, usage);
        }

        private static ColorThemeContrastRow BuildContrastRow(ColorContrastRequirement requirement,
            ColorThemeSet themeSet, IReadOnlyDictionary<string, ResolvedColorTheme> resolved)
        {
            var ratios = new Dictionary<string, float>(StringComparer.Ordinal);
            var failing = new List<string>();

            foreach (var pair in resolved)
            {
                foreach (var result in ColorThemeResolver.EvaluateContrast(themeSet, pair.Value))
                {
                    if (!ReferenceEquals(result.Requirement, requirement)) continue;

                    // An incomplete result is neither pass nor fail — it means the pair could not be
                    // measured, usually a translucent background with no under-surface named. Recording it
                    // as a failure would invent a verdict the data does not support.
                    if (result.IsIncomplete) continue;

                    ratios[pair.Key] = result.Ratio;
                    if (!result.Passed) failing.Add(pair.Key);
                }
            }

            return new ColorThemeContrastRow(requirement, ratios, failing);
        }

        private static string ResolveDefaultVariantId()
        {
            var settings = MolcaProjectSettings.Instance == null
                ? null
                : MolcaProjectSettings.Instance.GlobalSettings;
            if (settings == null) return null;

            if (settings.modules == null) return null;

            foreach (var module in settings.modules)
            {
                if (module is ColorThemeSettings themeSettings) return themeSettings.DefaultVariantId;
            }
            return null;
        }
    }

    /// <summary>Overall health of the installed colour theme, as the workspace reports it.</summary>
    public enum MolcaStatusForTheme
    {
        /// <summary>No theme set is installed.</summary>
        NotInstalled,

        /// <summary>The audit could not cover every declared input, so nothing it says is exhaustive.</summary>
        Incomplete,

        /// <summary>At least one finding would block a production build.</summary>
        Errors,

        /// <summary>Findings exist, none blocking.</summary>
        Warnings,

        /// <summary>Complete coverage, no findings.</summary>
        Clean
    }
}
#endif
