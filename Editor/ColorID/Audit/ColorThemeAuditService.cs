#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Molca.ColorID;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Produces the shared colour-theme audit snapshot. Strictly read-only.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Audit/</c>.
    /// <b>Shape:</b> editor-only static service, invoked explicitly by the Hub, Doctor, the build gate,
    /// MCP read tools and migration planning.
    /// <para/>
    /// <b>This service never calls <c>SetDirty</c>, never saves, never opens or modifies a scene, and
    /// never rewrites an ID.</b> That is the locked rule the V1 tooling broke in several places — a
    /// palette's <c>OnValidate</c> recoloured every open scene, and a property drawer repointed
    /// unresolved references just by rendering. Repair is a separate, previewed transaction; see
    /// <see cref="ColorThemeTransactionPlanner"/>.
    /// <para/>
    /// Scanning reads serialized YAML text rather than loading every asset. That is what makes it
    /// possible to cover closed scenes and package assets — which <c>AssetDatabase</c>-driven scanning
    /// cannot do without opening them — and it guarantees the read cannot mutate anything, because
    /// nothing is deserialized in the first place.
    /// </remarks>
    public static class ColorThemeAuditService
    {
        // Serialized field names, both spellings. Shipped content predates the _camelCase rename and
        // FormerlySerializedAs resolves it at load, so on-disk data still overwhelmingly uses the old
        // names — matching only one spelling finds a small fraction of real usage.
        private const string LegacyPairPattern =
            @"_?swatchName:\s*(?<swatch>\S.*?)\s*[\r\n]+\s*_?colorId:\s*(?<color>\S.*?)\s*[\r\n]";

        private const string TokenIdPattern = @"_tokenId:\s*(?<token>\S.*?)\s*[\r\n]";

        private static readonly System.Text.RegularExpressions.Regex LegacyPairRegex =
            new System.Text.RegularExpressions.Regex(LegacyPairPattern,
                System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.Multiline);

        private static readonly System.Text.RegularExpressions.Regex TokenIdRegex =
            new System.Text.RegularExpressions.Regex(TokenIdPattern,
                System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>Runs an audit.</summary>
        /// <param name="request">What to cover; <c>null</c> means <see cref="ColorThemeAuditRequest.Default"/>.</param>
        /// <returns>An immutable snapshot.</returns>
        public static ColorThemeAuditSnapshot Run(ColorThemeAuditRequest request = null)
        {
            request ??= ColorThemeAuditRequest.Default;

            var findings = new List<ColorThemeFinding>();
            var coverage = new List<ColorThemeVariantCoverage>();
            var usageSites = new List<ColorThemeUsageSite>();
            var scanned = new List<ColorThemeScanInput>();
            var skipped = new Dictionary<ColorThemeScanInput, string>();

            var themeSettings = FindThemeSettings();
            var themeSet = themeSettings?.ThemeSet;

            if (request.Inputs.Contains(ColorThemeScanInput.ThemeSettings))
            {
                scanned.Add(ColorThemeScanInput.ThemeSettings);
                AuditThemeSet(themeSettings, themeSet, findings, coverage);
            }
            else
            {
                skipped[ColorThemeScanInput.ThemeSettings] = "Not requested.";
            }

            var resolvedVariants = ResolveAllVariants(themeSet);

            ScanContentInputs(request, themeSet, resolvedVariants, findings, usageSites, scanned, skipped);

            if (request.Inputs.Contains(ColorThemeScanInput.GeneratedArtifacts))
            {
                scanned.Add(ColorThemeScanInput.GeneratedArtifacts);
                AuditGeneratedArtifacts(themeSet, resolvedVariants, findings);
            }
            else
            {
                skipped[ColorThemeScanInput.GeneratedArtifacts] = "Not requested.";
            }

            if (request.IncludeUsageIndex) ReportUnusedTokens(themeSet, usageSites, findings);

            findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));

            return new ColorThemeAuditSnapshot(request, themeSet, findings, coverage, usageSites,
                scanned, skipped, ComputeFingerprint(resolvedVariants, usageSites));
        }

        #region Theme set validity and coverage

        private static void AuditThemeSet(ColorThemeSettings settings, ColorThemeSet themeSet,
            List<ColorThemeFinding> findings, List<ColorThemeVariantCoverage> coverage)
        {
            if (settings == null)
            {
                // A legacy-only project is a supported configuration during the compatibility window,
                // so this is information, not a defect.
                findings.Add(new ColorThemeFinding(ColorThemeFindingKind.SettingsMissing,
                    ColorThemeFindingSeverity.Info,
                    "No ColorThemeSettings module is installed; this project still uses the legacy "
                    + "ColorModule path. That is supported during the compatibility window."));
                return;
            }

            if (themeSet == null)
            {
                findings.Add(new ColorThemeFinding(ColorThemeFindingKind.SettingsMissing,
                    ColorThemeFindingSeverity.Error,
                    $"'{settings.name}' is installed but references no theme set, so no colour token "
                    + "can resolve at runtime.", AssetPathOf(settings)));
                return;
            }

            var structuralErrors = new List<string>();
            if (!themeSet.Validate(structuralErrors))
            {
                foreach (string error in structuralErrors)
                {
                    findings.Add(new ColorThemeFinding(ColorThemeFindingKind.ThemeSetInvalid,
                        ColorThemeFindingSeverity.Error, error, AssetPathOf(themeSet)));
                }
                return;
            }

            foreach (string variantId in themeSet.GetVariantIds())
            {
                var outcome = ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme,
                    out var diagnostics);

                if (outcome != ColorThemeActivation.Activated)
                {
                    var kind = outcome == ColorThemeActivation.AliasCycle
                        ? ColorThemeFindingKind.AliasCycle
                        : ColorThemeFindingKind.RequiredTokenMissingInVariant;

                    findings.Add(new ColorThemeFinding(kind, ColorThemeFindingSeverity.Error,
                        $"Variant '{variantId}' does not resolve ({outcome}): "
                        + string.Join("; ", diagnostics), AssetPathOf(themeSet), null, variantId));

                    coverage.Add(new ColorThemeVariantCoverage(variantId, false, 0,
                        CollectRequiredTokenIds(themeSet), Array.Empty<string>()));
                    continue;
                }

                var missingRequired = new List<string>();
                var missingOptional = new List<string>();
                foreach (var definition in themeSet.TokenDefinitions)
                {
                    if (definition == null || theme.Contains(definition.Id)) continue;
                    (definition.Required ? missingRequired : missingOptional).Add(definition.Id);
                }

                coverage.Add(new ColorThemeVariantCoverage(variantId, true, theme.TokenCount,
                    missingRequired, missingOptional));

                foreach (var contrast in ColorThemeResolver.EvaluateContrast(themeSet, theme))
                {
                    if (contrast.IsIncomplete)
                    {
                        findings.Add(new ColorThemeFinding(ColorThemeFindingKind.ContrastIncomplete,
                            ColorThemeFindingSeverity.Warning, contrast.ToString(),
                            AssetPathOf(themeSet), contrast.Requirement?.ForegroundTokenId, variantId));
                        continue;
                    }

                    if (contrast.Passed) continue;

                    var severity = contrast.Requirement.Severity == ColorContrastSeverity.Error
                        ? ColorThemeFindingSeverity.Error
                        : ColorThemeFindingSeverity.Warning;
                    findings.Add(new ColorThemeFinding(ColorThemeFindingKind.ContrastFailure, severity,
                        contrast.ToString(), AssetPathOf(themeSet),
                        contrast.Requirement.ForegroundTokenId, variantId));
                }
            }
        }

        private static List<string> CollectRequiredTokenIds(ColorThemeSet themeSet)
        {
            var ids = new List<string>();
            foreach (var definition in themeSet.TokenDefinitions)
            {
                if (definition != null && definition.Required) ids.Add(definition.Id);
            }
            return ids;
        }

        private static Dictionary<string, ResolvedColorTheme> ResolveAllVariants(ColorThemeSet themeSet)
        {
            var resolved = new Dictionary<string, ResolvedColorTheme>(StringComparer.OrdinalIgnoreCase);
            if (themeSet == null) return resolved;

            foreach (string variantId in themeSet.GetVariantIds())
            {
                if (ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme, out _)
                    == ColorThemeActivation.Activated)
                {
                    resolved[variantId] = theme;
                }
            }
            return resolved;
        }

        #endregion

        #region Content scanning

        private static void ScanContentInputs(ColorThemeAuditRequest request, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants, List<ColorThemeFinding> findings,
            List<ColorThemeUsageSite> usageSites, List<ColorThemeScanInput> scanned,
            Dictionary<ColorThemeScanInput, string> skipped)
        {
            var inputPaths = new List<(ColorThemeScanInput input, string[] paths)>();

            if (request.Inputs.Contains(ColorThemeScanInput.ProjectAssets))
                inputPaths.Add((ColorThemeScanInput.ProjectAssets, CollectFiles("Assets", false)));
            else
                skipped[ColorThemeScanInput.ProjectAssets] = "Not requested.";

            if (request.Inputs.Contains(ColorThemeScanInput.PackageAssets))
                inputPaths.Add((ColorThemeScanInput.PackageAssets, CollectFiles("Packages", false)));
            else
                skipped[ColorThemeScanInput.PackageAssets] = "Not requested.";

            // Scenes come from the same filesystem sweep, so open and closed scenes are covered
            // identically — and no scene is ever opened, which is what keeps the scan read-only.
            bool wantOpen = request.Inputs.Contains(ColorThemeScanInput.OpenScenes);
            bool wantClosed = request.Inputs.Contains(ColorThemeScanInput.ClosedScenes);
            if (wantOpen || wantClosed)
            {
                var scenes = new List<string>();
                scenes.AddRange(CollectFiles("Assets", true));
                scenes.AddRange(CollectFiles("Packages", true));
                inputPaths.Add((wantClosed ? ColorThemeScanInput.ClosedScenes
                    : ColorThemeScanInput.OpenScenes, scenes.ToArray()));
            }

            if (!wantOpen) skipped[ColorThemeScanInput.OpenScenes] = "Not requested.";
            if (!wantClosed)
            {
                skipped[ColorThemeScanInput.ClosedScenes] =
                    "Not requested — findings cannot be treated as exhaustive.";
            }

            if (request.Inputs.Contains(ColorThemeScanInput.UiTokenCatalogs))
                scanned.Add(ColorThemeScanInput.UiTokenCatalogs);
            else
                skipped[ColorThemeScanInput.UiTokenCatalogs] = "Not requested.";

            foreach (var (input, paths) in inputPaths)
            {
                if (!scanned.Contains(input)) scanned.Add(input);
                if (input == ColorThemeScanInput.ClosedScenes && wantOpen
                    && !scanned.Contains(ColorThemeScanInput.OpenScenes))
                {
                    scanned.Add(ColorThemeScanInput.OpenScenes);
                }

                foreach (string path in paths)
                {
                    try
                    {
                        ScanFile(path, themeSet, resolvedVariants, findings, usageSites);
                    }
                    catch (IOException exception)
                    {
                        // A file that could not be read means the finding list is not exhaustive, so
                        // the whole input is marked skipped rather than the failure being swallowed.
                        skipped[input] = $"Could not read '{path}': {exception.Message}";
                    }
                }
            }
        }

        private static string[] CollectFiles(string root, bool scenesOnly)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();

            var results = new List<string>();
            string[] patterns = scenesOnly
                ? new[] { "*.unity" }
                : new[] { "*.prefab", "*.asset" };

            foreach (string pattern in patterns)
            {
                foreach (string path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    // Samples~ is never imported, so its contents are not project content; Library is
                    // derived. Neither belongs in a usage index.
                    if (path.Contains("~" + Path.DirectorySeparatorChar)) continue;
                    if (path.Contains(Path.DirectorySeparatorChar + "Library" + Path.DirectorySeparatorChar))
                        continue;
                    results.Add(path.Replace('\\', '/'));
                }
            }
            return results.ToArray();
        }

        private static void ScanFile(string path, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants, List<ColorThemeFinding> findings,
            List<ColorThemeUsageSite> usageSites)
        {
            string text = File.ReadAllText(path);

            // Cheap reject before running the regexes over a large scene file.
            if (text.IndexOf("colorId:", StringComparison.Ordinal) < 0
                && text.IndexOf("_tokenId:", StringComparison.Ordinal) < 0)
                return;

            bool isPackageOwned = path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in LegacyPairRegex.Matches(text))
            {
                string swatch = match.Groups["swatch"].Value;
                string colorId = match.Groups["color"].Value;
                var key = new LegacyColorKey(swatch, colorId);

                string canonical = themeSet?.ResolveLegacyToken(key);

                usageSites.Add(new ColorThemeUsageSite(ColorThemeUsageKind.LegacyColorIdComponent, path,
                    canonical, key.ToString(), isPackageOwned));

                if (themeSet == null) continue;

                if (canonical == null)
                {
                    findings.Add(new ColorThemeFinding(ColorThemeFindingKind.UnmappedLegacyPair,
                        ColorThemeFindingSeverity.Warning,
                        $"Legacy colour '{key}' has no authored alias, so it resolves only by guess or "
                        + "not at all. Add a LegacyColorAlias mapping it to a canonical token.",
                        path, key.ToString(), null, isPackageOwned));
                    continue;
                }

                ReportPerVariantResolution(canonical, key.ToString(), path, resolvedVariants, findings,
                    isPackageOwned);
                ReportDeprecated(themeSet, canonical, path, findings, isPackageOwned);
            }

            foreach (System.Text.RegularExpressions.Match match in TokenIdRegex.Matches(text))
            {
                string tokenId = match.Groups["token"].Value;
                if (string.IsNullOrEmpty(tokenId)) continue;

                usageSites.Add(new ColorThemeUsageSite(ColorThemeUsageKind.CanonicalTokenReference, path,
                    tokenId, null, isPackageOwned));

                if (themeSet == null) continue;

                if (themeSet.GetDefinition(tokenId) == null)
                {
                    findings.Add(new ColorThemeFinding(ColorThemeFindingKind.UnresolvedReference,
                        ColorThemeFindingSeverity.Error,
                        $"Reference to '{tokenId}', which the theme set does not declare.",
                        path, tokenId, null, isPackageOwned));
                    continue;
                }

                ReportPerVariantResolution(tokenId, tokenId, path, resolvedVariants, findings,
                    isPackageOwned);
                ReportDeprecated(themeSet, tokenId, path, findings, isPackageOwned);
            }
        }

        /// <summary>
        /// Reports a reference that resolves in some selectable variants but not all of them.
        /// </summary>
        /// <remarks>
        /// This is the V1 blind spot the plan calls out: the old validity check unioned keys across
        /// every <see cref="ColorModule"/>, so a key present in <i>any</i> palette was accepted even
        /// though switching to a palette that lacked it produced magenta at runtime. Every variant is
        /// checked separately here, and the failing variants are named.
        /// </remarks>
        private static void ReportPerVariantResolution(string tokenId, string subject, string path,
            Dictionary<string, ResolvedColorTheme> resolvedVariants, List<ColorThemeFinding> findings,
            bool isPackageOwned)
        {
            if (resolvedVariants.Count == 0) return;

            List<string> failing = null;
            foreach (var pair in resolvedVariants)
            {
                if (pair.Value.Contains(tokenId)) continue;
                (failing ??= new List<string>()).Add(pair.Key);
            }

            if (failing == null) return;

            findings.Add(new ColorThemeFinding(ColorThemeFindingKind.UnresolvedReference,
                ColorThemeFindingSeverity.Error,
                $"'{subject}' resolves to token '{tokenId}', which is missing from variant(s): "
                + $"{string.Join(", ", failing)}. Switching to one of those renders magenta.",
                path, subject, string.Join(",", failing), isPackageOwned));
        }

        private static void ReportDeprecated(ColorThemeSet themeSet, string tokenId, string path,
            List<ColorThemeFinding> findings, bool isPackageOwned)
        {
            var definition = themeSet.GetDefinition(tokenId);
            if (definition == null || !definition.Deprecated) return;

            findings.Add(new ColorThemeFinding(ColorThemeFindingKind.DeprecatedTokenInUse,
                ColorThemeFindingSeverity.Warning,
                $"Uses deprecated token '{tokenId}'; migrate to '{definition.ReplacementId}'.",
                path, tokenId, null, isPackageOwned));
        }

        private static void ReportUnusedTokens(ColorThemeSet themeSet,
            List<ColorThemeUsageSite> usageSites, List<ColorThemeFinding> findings)
        {
            if (themeSet == null) return;

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var site in usageSites)
            {
                if (!string.IsNullOrEmpty(site.CanonicalTokenId)) used.Add(site.CanonicalTokenId);
            }

            foreach (var definition in themeSet.TokenDefinitions)
            {
                if (definition == null || used.Contains(definition.Id)) continue;

                // Primitives are ingredients for semantic tokens, so being unreferenced by content is
                // their normal state, not a finding.
                if (definition.Kind == ColorTokenKind.Primitive) continue;

                findings.Add(new ColorThemeFinding(ColorThemeFindingKind.UnusedToken,
                    ColorThemeFindingSeverity.Info,
                    $"Semantic token '{definition.Id}' is declared but nothing references it.",
                    AssetPathOf(themeSet), definition.Id));
            }
        }

        #endregion

        #region Generated artifacts

        private static void AuditGeneratedArtifacts(ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants, List<ColorThemeFinding> findings)
        {
            if (themeSet == null || string.IsNullOrEmpty(themeSet.StableSetId)) return;

            ColorThemeManifest manifest = null;
            foreach (string guid in AssetDatabase.FindAssets("t:ColorThemeManifest"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ColorThemeManifest>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.ThemeSetStableId == themeSet.StableSetId)
                {
                    manifest = candidate;
                    break;
                }
            }

            // No manifest means this project does not use runtime UI Toolkit theming, which is the
            // documented way to opt out rather than a problem to report.
            if (manifest == null) return;

            foreach (var pair in resolvedVariants)
            {
                if (manifest.IsFresh(pair.Value, ColorThemeUssGeneratorVersion.Current, out string stale))
                    continue;

                findings.Add(new ColorThemeFinding(ColorThemeFindingKind.GeneratedOutputStale,
                    ColorThemeFindingSeverity.Error,
                    $"Generated UI Toolkit output for variant '{pair.Key}' is stale — {stale}",
                    AssetDatabase.GetAssetPath(manifest), null, pair.Key));
            }
        }

        #endregion

        #region Helpers

        /// <summary>Finds the project's theme settings module.</summary>
        /// <remarks>
        /// Walks <c>GlobalSettings.modules</c> directly: at edit time the runtime module cache is not
        /// built, so <c>GlobalSettings.GetModule</c> returns null even on a correctly configured project.
        /// </remarks>
        internal static ColorThemeSettings FindThemeSettings()
        {
            var globalSettings = GlobalSettings.main;
            if (globalSettings?.modules == null) return null;

            foreach (var module in globalSettings.modules)
            {
                if (module is ColorThemeSettings themeSettings) return themeSettings;
            }
            return null;
        }

        private static string AssetPathOf(UnityEngine.Object asset) =>
            asset == null ? null : AssetDatabase.GetAssetPath(asset);

        /// <summary>
        /// Identity of the audited state, so a transaction can refuse to apply against changed data.
        /// </summary>
        /// <remarks>
        /// Folds in each variant's resolved fingerprint (so a colour or alias edit invalidates a plan)
        /// and every usage site's path plus token (so a content change does too). Order-independent by
        /// XOR, since neither dictionary nor filesystem enumeration order is stable.
        /// </remarks>
        private static string ComputeFingerprint(Dictionary<string, ResolvedColorTheme> resolvedVariants,
            List<ColorThemeUsageSite> usageSites)
        {
            unchecked
            {
                ulong accumulator = 1469598103934665603UL;
                ulong xor = 0UL;

                foreach (var pair in resolvedVariants)
                {
                    xor ^= Hash($"{pair.Key}:{pair.Value.SourceFingerprint}");
                }

                foreach (var site in usageSites)
                {
                    xor ^= Hash($"{site.AssetPath}|{site.Kind}|{site.CanonicalTokenId}|{site.LegacyKey}");
                }

                accumulator ^= xor;
                accumulator *= 1099511628211UL;
                return accumulator.ToString("x16");
            }
        }

        private static ulong Hash(string value)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                if (string.IsNullOrEmpty(value)) return hash;
                foreach (char c in value)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        #endregion
    }
}
#endif
