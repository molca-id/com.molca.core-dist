using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Molca.Editor.Upgrade
{
    /// <summary>One API that 2.0 removes or renames, and what replaces it.</summary>
    public sealed class RetiredApi
    {
        /// <summary>The identifier as it appears in source.</summary>
        public string Name { get; }

        /// <summary>What to write instead, or a sentence saying there is no direct replacement.</summary>
        public string Replacement { get; }

        /// <summary>
        /// Whether a namespace shares this name, so a trailing dot means it is not the type.
        /// </summary>
        /// <remarks>
        /// <c>ColorID</c> is both the type and the namespace it lives in, so <c>using Molca.ColorID;</c>
        /// and <c>Molca.ColorID.ColorThemeBinding</c> are not usages of the retired class while
        /// <c>ColorID target</c> is. Without this the detector would flag every V2 file in the project and
        /// teach people to ignore it — the same "don't manufacture findings" rule the audits are held to.
        /// </remarks>
        public bool SharesNameWithNamespace { get; }

        /// <summary>Creates an entry.</summary>
        public RetiredApi(string name, string replacement, bool sharesNameWithNamespace = false)
        {
            Name = name;
            Replacement = replacement;
            SharesNameWithNamespace = sharesNameWithNamespace;
        }
    }

    /// <summary>
    /// Finds project C# still naming an API that 2.0 removes or renames.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// <b>This is a report and can never be a fix.</b> Rewriting a consumer's own source is neither
    /// reversible nor locally decidable — the same two gates every remediation fix is held to — and a
    /// half-correct automated edit to code someone owns is worse than a precise list. So it does what a
    /// fix cannot: says exactly which file, which line, and what to write instead. That turns "your
    /// project no longer compiles" into a work list, which is the whole difference between a release note
    /// and an upgrade path.
    /// <para/>
    /// <b>Text matching, deliberately.</b> Roslyn is not available to an editor script, and by the time
    /// the API is gone the project does not compile anyway, so no semantic model exists to consult. The
    /// cost is false positives on identifiers that merely share a name; the alternative is silence, which
    /// is worse. Comments and strings are skipped, and every hit is reported with its line so a human can
    /// judge in a second.
    /// <para/>
    /// Scans <c>Assets/</c> only. Package code is not the consumer's to fix.
    /// </remarks>
    public sealed class RetiredApiUsageDetector : IMolcaUpgradeDetector
    {
        /// <inheritdoc/>
        public string System => "Scripts";

        /// <summary>What 2.0 removes or renames, with the replacement.</summary>
        /// <remarks>
        /// Kept as data so the release note and this detector cannot drift: the note is generated from
        /// the same list.
        /// </remarks>
        public static readonly IReadOnlyList<RetiredApi> Retired = new[]
        {
            new RetiredApi("ColorID",
                "ColorThemeBinding — one component holding several tokens, each naming its own target",
                sharesNameWithNamespace: true),
            new RetiredApi("ColorIDReference",
                "ColorTokenReference — holds a canonical token id instead of a (swatch, colorId) pair"),
            new RetiredApi("ColorModule",
                "ColorThemeSet plus a ColorThemeSettings module; palettes are authored as theme variants"),
            new RetiredApi("IColorProvider",
                "IColorThemeService, resolved with RuntimeManager.GetService<IColorThemeService>()"),
            new RetiredApi("IColorSchemeService",
                "IColorThemeService — SetVariant replaces the scheme calls"),
            new RetiredApi("ColorSchemeManager",
                "IColorThemeService; the subsystem still exists but is consumed through the interface"),
            new RetiredApi("ColorTargetApplier",
                "ColorTargetAdapterRegistry.Apply(component, channel, colour)"),
            new RetiredApi("ColorUtility",
                "ColorThemeBinding for persistent tokens, or explicit IColorThemeService resolution for one-off colour"),
            new RetiredApi("BooleanColor",
                "no replacement — it had no users; hold two ColorTokenReference fields"),
            new RetiredApi("MolcaSDK",
                "Molca.App — the namespace and assembly were renamed"),
        };

        /// <summary>Matches a comment or a string literal, so a mention inside one is not a usage.</summary>
        /// <remarks>
        /// Not <see cref="RegexOptions.Singleline"/>: a block comment is only blanked within its own line,
        /// which keeps line numbers intact. A multi-line block comment therefore leaks its middle lines —
        /// accepted, because shifting every reported line number is the worse failure.
        /// </remarks>
        private static readonly Regex Noise = new Regex(
            @"//.*$|/\*.*?\*/|""(?:\\.|[^""\\])*""", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>An import/declaration naming the ColorID namespace itself, not its retired type.</summary>
        private static readonly Regex ColorIdNamespaceDirective = new Regex(
            @"^\s*(?:(?:global\s+)?using\s+(?:\w+\s*=\s*)?|namespace\s+)"
            + @"(?:global::)?(?:Molca\s*\.\s*)?ColorID\s*(?:;|\{|$)", RegexOptions.Compiled);

        /// <summary>The Unity type that legitimately shares the removed Molca utility's short name.</summary>
        private static readonly Regex UnityColorUtility = new Regex(
            @"(?<!\w)(?:global::)?UnityEngine\s*\.\s*ColorUtility\b", RegexOptions.Compiled);

        private static readonly Regex UnityColorUtilityAlias = new Regex(
            @"^\s*(?:global\s+)?using\s+ColorUtility\s*=\s*(?:global::)?UnityEngine\s*\.\s*ColorUtility\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ImportsUnityEngine = new Regex(
            @"^\s*(?:global\s+)?using\s+UnityEngine\s*;", RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ImportsMolcaColorId = new Regex(
            @"^\s*(?:global\s+)?using\s+Molca\s*\.\s*ColorID\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex QualifiedMolcaColorUtility = new Regex(
            @"(?<!\w)(?:global::)?Molca\s*\.\s*ColorID\s*\.\s*ColorUtility\b", RegexOptions.Compiled);

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var byApi = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string path in ProjectScripts())
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception exception) when (exception is IOException
                                                  || exception is UnauthorizedAccessException)
                {
                    continue;
                }

                if (!Retired.Any(api => text.IndexOf(api.Name, StringComparison.Ordinal) >= 0)) continue;

                foreach (var (line, api) in FindUsages(text))
                {
                    if (!byApi.TryGetValue(api, out var hits)) byApi[api] = hits = new List<string>();
                    hits.Add($"{Relative(path)}:{line}");
                }
            }

            foreach (var api in Retired)
            {
                if (!byApi.TryGetValue(api.Name, out var hits) || hits.Count == 0) continue;

                yield return new MolcaUpgradeFinding(
                    $"scripts.retired-api.{api.Name.ToLowerInvariant()}",
                    $"{hits.Count} reference(s) to '{api.Name}', retired or changed in 2.0",
                    $"Replace with: {api.Replacement}. These are in your own scripts, so no migration can "
                    + "rewrite them — the list below is every place that needs changing.",
                    MolcaUpgradeSeverity.Blocking,
                    hits);
            }
        }

        /// <summary>Every retired-API usage in one source file, as (1-based line, API name).</summary>
        /// <param name="text">The file's contents.</param>
        /// <returns>One entry per API per line; never <c>null</c>.</returns>
        /// <remarks>
        /// Separated from the file walk so the part with judgement in it can be tested directly. The
        /// matching rules, each of which exists because of a specific false result:
        /// <list type="bullet">
        /// <item><description>
        /// Comments and string literals are blanked first — a mention in either is not a usage, and
        /// migration notes in comments are common in a project mid-upgrade.
        /// </description></item>
        /// <item><description>
        /// Preceded by a non-word character, so <c>MyColorIDReference</c> is not a hit. A leading
        /// <c>.</c> is allowed on purpose: <c>Molca.ColorID.ColorIDReference</c> is a real usage, and an
        /// earlier version that excluded it missed every fully-qualified reference.
        /// </description></item>
        /// <item><description>
        /// For a name a namespace also carries, a following <c>.</c> disqualifies it — see
        /// <see cref="RetiredApi.SharesNameWithNamespace"/>.
        /// </description></item>
        /// </list>
        /// </remarks>
        internal static IEnumerable<(int Line, string Api)> FindUsages(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            string cleaned = Noise.Replace(text, string.Empty);
            string[] lines = cleaned.Split('\n');
            bool unqualifiedColorUtilityCanBeMolca = ImportsMolcaColorId.IsMatch(cleaned)
                                                     && !ImportsUnityEngine.IsMatch(cleaned)
                                                     && !UnityColorUtilityAlias.IsMatch(cleaned);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                foreach (var api in Retired)
                {
                    // Skip only a directive that ends at the namespace. Type aliases and other retired
                    // names inside using directives remain genuine usages.
                    if (api.SharesNameWithNamespace && ColorIdNamespaceDirective.IsMatch(line)) continue;

                    string searchable = line;
                    if (api.Name == "ColorUtility")
                    {
                        bool qualifiedMolca = QualifiedMolcaColorUtility.IsMatch(line);
                        if (!qualifiedMolca && !unqualifiedColorUtilityCanBeMolca) continue;
                        searchable = UnityColorUtility.Replace(line, string.Empty);
                    }
                    string pattern = $@"(?<!\w){Regex.Escape(api.Name)}\b";
                    if (api.SharesNameWithNamespace) pattern += @"(?!\s*\.)";

                    if (Regex.IsMatch(searchable, pattern)) yield return (i + 1, api.Name);
                }
            }
        }

        private static IEnumerable<string> ProjectScripts()
        {
            string assets = Application.dataPath;
            if (!Directory.Exists(assets)) return Array.Empty<string>();

            return Directory.EnumerateFiles(assets, "*.cs", SearchOption.AllDirectories);
        }

        private static string Relative(string path)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName;
            return string.IsNullOrEmpty(root)
                ? path
                : path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
        }
    }

    /// <summary>Generates the retired-API section of the checked-in 2.0 consumer upgrade guide.</summary>
    /// <remarks>
    /// The detector list is the source of truth. A package test compares this output with the guide, so
    /// adding or changing a detector entry cannot leave release guidance behind.
    /// </remarks>
    internal static class RetiredApiUpgradeGuide
    {
        internal const string BeginMarker = "<!-- BEGIN GENERATED RETIRED API TABLE -->";
        internal const string EndMarker = "<!-- END GENERATED RETIRED API TABLE -->";

        /// <summary>Builds the generated Markdown section, including its drift-check markers.</summary>
        internal static string GenerateSection()
        {
            var markdown = new StringBuilder();
            markdown.AppendLine(BeginMarker);
            markdown.AppendLine("| Retired or changed 1.x API | 2.x replacement |");
            markdown.AppendLine("| --- | --- |");

            foreach (var api in RetiredApiUsageDetector.Retired)
                markdown.Append("| `").Append(Escape(api.Name)).Append("` | ")
                    .Append(Escape(api.Replacement)).AppendLine(" |");

            markdown.Append(EndMarker);
            return markdown.ToString();
        }

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
