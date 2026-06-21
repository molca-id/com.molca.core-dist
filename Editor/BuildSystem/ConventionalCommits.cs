using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Molca.Settings
{
    /// <summary>The semantic-version bump implied by a set of conventional commits.</summary>
    public enum VersionBump
    {
        /// <summary>No version-affecting commits.</summary>
        None = 0,
        /// <summary>A fix/perf change — bump the patch component.</summary>
        Patch = 1,
        /// <summary>A new feature — bump the minor component.</summary>
        Minor = 2,
        /// <summary>A breaking change — bump the major component.</summary>
        Major = 3,
    }

    /// <summary>A commit subject parsed against the Conventional Commits convention.</summary>
    public readonly struct ConventionalCommit
    {
        /// <summary>Lowercased commit type (e.g. <c>feat</c>, <c>fix</c>), or empty when not conventional.</summary>
        public readonly string Type;
        /// <summary>Optional scope inside the parentheses, or empty.</summary>
        public readonly string Scope;
        /// <summary>True when the commit is marked breaking (a <c>!</c> before the colon).</summary>
        public readonly bool Breaking;
        /// <summary>Description after the colon, or the whole subject when not conventional.</summary>
        public readonly string Description;
        /// <summary>The original subject line.</summary>
        public readonly string Raw;

        /// <summary>Initializes a parsed conventional-commit record.</summary>
        public ConventionalCommit(string type, string scope, bool breaking, string description, string raw)
        {
            Type = type;
            Scope = scope;
            Breaking = breaking;
            Description = description;
            Raw = raw;
        }

        /// <summary>True when the subject matched the conventional-commit grammar.</summary>
        public bool IsConventional => !string.IsNullOrEmpty(Type);
    }

    /// <summary>
    /// Parses commit subjects against the Conventional Commits convention and turns a batch of them
    /// into a categorized changelog block and a suggested SemVer bump.
    /// </summary>
    /// <remarks>
    /// Only the subject line is inspected (the changelog records <c>%s</c>), so breaking changes are
    /// detected via the <c>!</c> marker rather than a <c>BREAKING CHANGE:</c> body footer.
    /// </remarks>
    public static class ConventionalCommits
    {
        // type(scope)!: description  —  scope and the breaking "!" are optional.
        private static readonly Regex Pattern = new Regex(
            @"^(?<type>[a-zA-Z]+)(?:\((?<scope>[^)]*)\))?(?<bang>!)?:\s*(?<desc>.+)$",
            RegexOptions.Compiled);

        /// <summary>Parses a single commit subject. Non-conventional subjects yield an empty <see cref="ConventionalCommit.Type"/>.</summary>
        /// <param name="subject">The commit subject line, optionally with a trailing " (hash)".</param>
        /// <returns>The parsed commit record.</returns>
        public static ConventionalCommit Parse(string subject)
        {
            var raw = subject?.Trim() ?? string.Empty;
            var match = Pattern.Match(raw);
            if (!match.Success)
                return new ConventionalCommit(string.Empty, string.Empty, false, raw, raw);

            var type = match.Groups["type"].Value.ToLowerInvariant();
            var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value.Trim() : string.Empty;
            var breaking = match.Groups["bang"].Success;
            var desc = match.Groups["desc"].Value.Trim();
            return new ConventionalCommit(type, scope, breaking, desc, raw);
        }

        /// <summary>
        /// Returns the largest version bump implied by <paramref name="subjects"/>: a breaking change
        /// implies <see cref="VersionBump.Major"/>, a <c>feat</c> implies <see cref="VersionBump.Minor"/>,
        /// and a <c>fix</c>/<c>perf</c> implies <see cref="VersionBump.Patch"/>.
        /// </summary>
        /// <param name="subjects">The commit subjects to evaluate.</param>
        /// <returns>The suggested bump, or <see cref="VersionBump.None"/> when nothing version-affecting is present.</returns>
        public static VersionBump SuggestBump(IReadOnlyList<string> subjects)
        {
            var bump = VersionBump.None;
            if (subjects == null)
                return bump;

            foreach (var subject in subjects)
            {
                var commit = Parse(subject);
                if (commit.Breaking)
                    return VersionBump.Major;
                if (commit.Type == "feat" && bump < VersionBump.Minor)
                    bump = VersionBump.Minor;
                else if ((commit.Type == "fix" || commit.Type == "perf") && bump < VersionBump.Patch)
                    bump = VersionBump.Patch;
            }

            return bump;
        }

        /// <summary>
        /// Formats <paramref name="subjects"/> into Markdown sections grouped by Conventional Commits
        /// category (Breaking, Features, Fixes, Other). Breaking commits appear only under Breaking.
        /// </summary>
        /// <param name="subjects">The commit subjects to format.</param>
        /// <returns>The categorized Markdown block, or an empty string when there are no subjects.</returns>
        public static string Format(IReadOnlyList<string> subjects)
        {
            if (subjects == null || subjects.Count == 0)
                return string.Empty;

            var breaking = new List<string>();
            var features = new List<string>();
            var fixes = new List<string>();
            var other = new List<string>();

            foreach (var subject in subjects)
            {
                var commit = Parse(subject);
                var line = commit.IsConventional && !string.IsNullOrEmpty(commit.Scope)
                    ? $"**{commit.Scope}:** {commit.Description}"
                    : (commit.IsConventional ? commit.Description : commit.Raw);

                if (commit.Breaking)
                    breaking.Add(line);
                else if (commit.Type == "feat")
                    features.Add(line);
                else if (commit.Type == "fix")
                    fixes.Add(line);
                else
                    other.Add(line);
            }

            var sb = new StringBuilder();
            AppendSection(sb, "⚠ BREAKING CHANGES", breaking);
            AppendSection(sb, "Features", features);
            AppendSection(sb, "Fixes", fixes);
            AppendSection(sb, "Other", other);
            return sb.ToString().TrimEnd();
        }

        private static void AppendSection(StringBuilder sb, string title, List<string> lines)
        {
            if (lines.Count == 0)
                return;

            sb.Append("#### ").Append(title).Append('\n');
            foreach (var line in lines)
                sb.Append("- ").Append(line).Append('\n');
            sb.Append('\n');
        }
    }
}
