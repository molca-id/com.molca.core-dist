using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Molca.Editor.Doctor
{
    /// <summary>A C# source file in the scan scope, loaded once and shared by all checks.</summary>
    public class DoctorSourceFile
    {
        /// <summary>Project-relative path with forward slashes.</summary>
        public string Path;
        public string[] Lines;
        /// <summary>True for editor-only code (under an /Editor/ folder or *Editor.cs).</summary>
        public bool IsEditor;
    }

    /// <summary>
    /// Shared state for one Doctor run: the cached source files of Molca Core and
    /// the project working area. Checks read from here instead of hitting disk.
    /// </summary>
    /// <remarks>
    /// Third-party / vendor code is excluded from the scan so its conventions are not
    /// reported as Molca violations. Exclusions come from three sources, combined:
    /// <see cref="DefaultIgnoreGlobs"/>, any patterns passed to the constructor, and a
    /// <c>.doctorignore</c> file at the project root (one glob per line, <c>#</c> comments).
    /// A fork or project can therefore tune exclusions without modifying Core.
    /// </remarks>
    public class DoctorContext
    {
        /// <summary>Line marker that suppresses any Doctor finding on that line.</summary>
        public const string IgnoreMarker = "doctor:ignore";

        /// <summary>Optional project-root file listing extra ignore globs (one per line).</summary>
        public const string IgnoreFileName = ".doctorignore";

        /// <summary>
        /// Built-in ignore globs for common third-party locations. Matched against the
        /// project-relative, forward-slash path. <c>**</c> spans path segments, <c>*</c>
        /// matches within a segment; a pattern with no wildcard matches as a substring.
        /// </summary>
        public static IReadOnlyList<string> DefaultIgnoreGlobs { get; } = new[]
        {
            "**/Plugins/**",
            "**/TextMesh Pro/**",
            "**/ThirdParty/**",
            "**/Third Party/**",
            "**/Vendor/**",
            "**/External/**",
            "**/Standard Assets/**",
            "**/Samples/**",       // Unity drops imported package samples under Assets/Samples/<pkg>/<ver>/
            "**/AssetStoreTools/**",
        };

        private readonly List<Regex> _ignore;
        private List<DoctorSourceFile> _sources;

        /// <summary>Roots scanned for .cs files, project-relative.</summary>
        public IReadOnlyList<string> ScanRoots { get; }

        /// <summary>
        /// Optional sink for human-readable, sub-check progress detail (e.g.
        /// "Prefabs 3/12"). Set by the orchestrator; a long-running check calls
        /// <see cref="ReportStatus"/> so the UI can show that progress is happening.
        /// </summary>
        public Action<string> StatusReporter { get; set; }

        /// <summary>Reports a one-line progress detail for the running check, if a sink is set.</summary>
        public void ReportStatus(string detail) => StatusReporter?.Invoke(detail);

        /// <param name="scanRoots">Roots to scan; null uses Core + Assets.</param>
        /// <param name="extraIgnoreGlobs">
        /// Additional ignore globs, combined with <see cref="DefaultIgnoreGlobs"/> and the
        /// project's <c>.doctorignore</c> file. Use this for programmatic/test scoping.
        /// </param>
        public DoctorContext(IEnumerable<string> scanRoots = null, IEnumerable<string> extraIgnoreGlobs = null)
        {
            ScanRoots = (scanRoots ?? DefaultScanRoots()).ToList();

            var globs = DefaultIgnoreGlobs
                .Concat(LoadIgnoreFileGlobs())
                .Concat(extraIgnoreGlobs ?? Enumerable.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g));
            _ignore = globs.Select(GlobToRegex).ToList();
        }

        private static IEnumerable<string> DefaultScanRoots()
        {
            yield return "Packages/com.molca.core";
            if (Directory.Exists("Assets"))
                yield return "Assets";
        }

        // Reads <projectRoot>/.doctorignore if present. Blank lines and #-comments ignored.
        private static IEnumerable<string> LoadIgnoreFileGlobs()
        {
            if (!File.Exists(IgnoreFileName))
                return Enumerable.Empty<string>();
            try
            {
                return File.ReadAllLines(IgnoreFileName)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .ToList();
            }
            catch (Exception)
            {
                return Enumerable.Empty<string>(); // unreadable ignore file must not abort the run
            }
        }

        /// <summary>All C# sources in scope (lazily loaded, cached for the run).</summary>
        public IReadOnlyList<DoctorSourceFile> Sources
        {
            get
            {
                if (_sources == null)
                    _sources = LoadSources();
                return _sources;
            }
        }

        /// <summary>Runtime (non-editor) sources only.</summary>
        public IEnumerable<DoctorSourceFile> RuntimeSources => Sources.Where(s => !s.IsEditor);

        /// <summary>True if the path is excluded by any active ignore glob.</summary>
        public bool IsIgnored(string normalizedPath) => _ignore.Any(rx => rx.IsMatch(normalizedPath));

        private List<DoctorSourceFile> LoadSources()
        {
            var result = new List<DoctorSourceFile>();
            foreach (var root in ScanRoots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    var normalized = file.Replace('\\', '/');
                    // Third-party and generated content is out of scope — Doctor reports
                    // Molca-convention violations, which only apply to first-party code.
                    if (IsIgnored(normalized))
                        continue;

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch (Exception)
                    {
                        continue; // unreadable file — skip rather than abort the run
                    }

                    result.Add(new DoctorSourceFile
                    {
                        Path = normalized,
                        Lines = lines,
                        IsEditor = normalized.Contains("/Editor/") || normalized.EndsWith("Editor.cs", StringComparison.Ordinal),
                    });
                }
            }
            return result;
        }

        // Converts a path glob to a regex. A pattern with no wildcard is treated as a
        // case-insensitive substring match (so bare folder names like "Vendor" work);
        // wildcard patterns are anchored: ** spans segments, * stays within one.
        private static Regex GlobToRegex(string glob)
        {
            var trimmed = glob.Trim();
            if (!trimmed.Contains('*') && !trimmed.Contains('?'))
                return new Regex(Regex.Escape(trimmed), RegexOptions.IgnoreCase);

            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '*')
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '*')
                    {
                        sb.Append(".*"); // ** — across segments
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*"); // * — within a segment
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }

        /// <summary>True if the line opts out of Doctor findings via the ignore marker.</summary>
        public static bool IsSuppressed(string line) =>
            line.IndexOf(IgnoreMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
