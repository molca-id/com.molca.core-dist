using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Enforces the Molca editor design language (Sprint 27.8). Flags editor-only UI code that drifts
    /// from <c>Documentation~/EDITOR_DESIGN_LANGUAGE.md</c>: raw hex colors in C# where a shared token
    /// exists, raw <c>rgb()</c>/<c>#hex</c> color literals in USS that should reference a
    /// <c>--molca-*</c> token (the gap that let the Hub sheet drift dark-only), unscoped
    /// <c>EditorPrefs</c> instead of <c>MolcaEditorPrefs</c>, nested section cards, hardcoded
    /// settings-asset paths instead of <c>MolcaEditorSettingsAsset.GetOrCreate</c>, and USS class names
    /// that are not domain-scoped.
    /// </summary>
    /// <remarks>
    /// Findings are <see cref="DoctorSeverity.Warning"/> except the non-domain-scoped USS class-name
    /// advisory, which is <see cref="DoctorSeverity.Info"/> — text analysis cannot prove intent, and
    /// forks must not be CI-broken by a styling heuristic. The USS color scan exempts translucent
    /// <c>rgba()</c> washes (they composite over a themed surface and already track the skin). Suppress
    /// an intentional case (e.g. a near-black foreground on a fixed status fill) with a
    /// <c>doctor:ignore</c> comment. Domain color (ColorID theming, color-field drawers) and the shared
    /// palette/component files under <c>Editor/UI/</c> are excluded by design. Surfaced to the assistant
    /// through the <c>molca_doctor</c> MCP tool because it runs the same check registry.
    /// </remarks>
    public class DesignLanguageCheck : IDoctorCheck
    {
        public string Id => "design-language";
        public string Description => "Editor UI follows the Molca design language (tokens, scoped prefs, no nested cards)";

        // Literal Color construction in editor-UI chrome. Domain color is excluded at the file level.
        private static readonly Regex HexColor = new Regex(@"\bnew\s+Color(32)?\s*\(");
        // EditorPrefs used directly (not the project-scoped MolcaEditorPrefs wrapper).
        private static readonly Regex RawPrefs = new Regex(@"(?<!Molca)\bEditorPrefs\s*\.");
        // A section card added into another card's Body — the one nested-card shape we can detect in text.
        private static readonly Regex NestedCard =
            new Regex(@"\.Body\s*\.\s*Add\s*\(\s*new\s+Molca(Hub)?SectionCard\b");
        // AssetDatabase.CreateAsset(...) with a hardcoded "Assets/....asset" literal — editor settings SOs
        // must route through MolcaEditorSettingsAsset.GetOrCreate instead. User-chosen saves pass a path
        // *variable* (e.g. from SaveFilePanelInProject), so they do not match this literal-path shape.
        private static readonly Regex HardcodedAssetPath =
            new Regex(@"AssetDatabase\s*\.\s*CreateAsset\s*\([^)]*,\s*\$?""Assets/[^""]+\.asset""");
        // USS class selector at the start of a rule, e.g. ".molca-card {".
        private static readonly Regex UssClass = new Regex(@"^\s*\.([A-Za-z_][\w-]*)");
        // Opaque color literal in USS — an `rgb(...)` or `#hex`. Translucent `rgba(...)` washes are
        // deliberately exempt: layered over a themed surface they already track the skin (negative
        // lookahead on the `a` keeps `rgba(` from matching). Hex is bounded so it is not part of an id.
        private static readonly Regex UssRawColor =
            new Regex(@"(?<![\w-])(#[0-9a-fA-F]{3,8}\b|rgb\s*\()");

        // USS class prefixes that count as domain-scoped (design-language compliant).
        private static readonly string[] ScopedPrefixes = { "molca", "unity", "chat" };

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();

            // --- C# editor-UI scan: hex / prefs / nested cards ---
            foreach (var source in context.Sources.Where(s => s.IsEditor))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool allowHex = IsDomainColorFile(source.Path);
                bool allowPrefs = IsMachineGlobalPrefsFile(source.Path);
                bool allowAssetPath = IsSettingsAssetHelperFile(source.Path);

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;
                    if (DoctorContext.IsSuppressed(line))
                        continue;

                    if (!allowHex && HexColor.IsMatch(line))
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Raw color literal in editor UI — use a MolcaEditorColors token (or a --molca-* USS var) so the surface tracks the shared design language.",
                            source.Path, i + 1));

                    if (!allowPrefs && RawPrefs.IsMatch(line))
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Unscoped EditorPrefs in editor UI — use MolcaEditorPrefs so editor state is project-scoped.",
                            source.Path, i + 1));

                    if (NestedCard.IsMatch(line))
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Section card nested inside another card's Body — use a divider/subheading instead (EDITOR_DESIGN_LANGUAGE.md > Section Card).",
                            source.Path, i + 1));

                    if (!allowAssetPath && HardcodedAssetPath.IsMatch(line))
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Hardcoded settings-asset path in CreateAsset — route editor settings SOs through MolcaEditorSettingsAsset.GetOrCreate<T>(fileName) so they share the canonical Assets/_Molca/Editor/ location (suppress with doctor:ignore for legitimate one-off asset generation).",
                            source.Path, i + 1));
                }
            }

            // --- USS scan: domain-scoped class names (advisory) ---
            foreach (var ussPath in EnumerateEditorUss(context))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string[] lines;
                try { lines = File.ReadAllLines(ussPath); }
                catch (Exception) { continue; }

                // The shared palette/component sheets under Editor/UI/ legitimately define raw color;
                // every other editor sheet must reference the tokens instead.
                bool allowRawColor = IsDomainColorFile(ussPath);

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (DoctorContext.IsSuppressed(line)) continue;

                    var trimmed = line.TrimStart();
                    bool isComment = trimmed.StartsWith("*") || trimmed.StartsWith("/*") || trimmed.StartsWith("//");

                    if (!allowRawColor && !isComment && UssRawColor.IsMatch(line))
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Raw color literal in USS — reference a --molca-* token so the surface tracks the light/dark skin (translucent rgba() washes and an intentional fixed-accent value marked doctor:ignore are exempt).",
                            ussPath, i + 1));

                    var match = UssClass.Match(line);
                    if (!match.Success) continue;

                    var name = match.Groups[1].Value;
                    if (ScopedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                        $"USS class `.{name}` is not domain-scoped — prefix Molca editor classes with `molca-` to avoid collisions.",
                        ussPath, i + 1));
                }
            }

            return issues;
        }

        // The shared palette/components legitimately define colors; ColorID and *Drawer files edit
        // color as their domain data, not chrome.
        private static bool IsDomainColorFile(string path) =>
            path.Contains("/Editor/UI/") ||
            path.Contains("/ColorID/") ||
            path.EndsWith("Drawer.cs", StringComparison.Ordinal);

        // EditorPrefs here is intentionally machine-global (auth tokens, pending-build paths) — the
        // wrapper itself, and the documented exceptions from the Sprint 27.3 audit.
        private static bool IsMachineGlobalPrefsFile(string path) =>
            path.EndsWith("MolcaEditorPrefs.cs", StringComparison.Ordinal) ||
            path.EndsWith("McpAuth.cs", StringComparison.Ordinal) ||
            path.EndsWith("AssistantApiAuth.cs", StringComparison.Ordinal) ||
            path.EndsWith("BuildManager.cs", StringComparison.Ordinal) ||
            path.EndsWith("ChangelogWriter.cs", StringComparison.Ordinal);

        // The shared helper is the one place that legitimately writes a canonical settings-asset path.
        private static bool IsSettingsAssetHelperFile(string path) =>
            path.EndsWith("MolcaEditorSettingsAsset.cs", StringComparison.Ordinal);

        private static IEnumerable<string> EnumerateEditorUss(DoctorContext context)
        {
            foreach (var root in context.ScanRoots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.uss", SearchOption.AllDirectories); }
                catch (Exception) { continue; }

                foreach (var file in files)
                {
                    var normalized = file.Replace('\\', '/');
                    if (!normalized.Contains("/Editor/")) continue;
                    if (context.IsIgnored(normalized)) continue;
                    yield return normalized;
                }
            }
        }
    }
}
