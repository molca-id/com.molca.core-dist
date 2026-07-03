using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// One durable memory entry (Sprint 77): a single fact about this project/user, with a short
    /// <see cref="Description"/> used for recall relevance and the fact itself in <see cref="Body"/>.
    /// </summary>
    public sealed class AssistantMemoryEntry
    {
        /// <summary>Stable kebab-case slug identifying the entry (also its file name without extension).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>One-line summary used to decide relevance during recall.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>The durable fact (may be multi-line).</summary>
        public string Body { get; set; } = string.Empty;
    }

    /// <summary>
    /// File-backed, human-readable cross-session project memory for the assistant (Sprint 77). Entries are one
    /// fact per Markdown file (YAML frontmatter <c>name</c>/<c>description</c> + body) under the <b>consumer</b>
    /// project (<c>Assets/_Molca/AssistantMemory/</c>) with a regenerated <c>INDEX.md</c> — never inside the
    /// read-only Core package (Sprint-35.5 write-path rule). Editable outside the assistant.
    /// </summary>
    /// <remarks>
    /// Distinct from the session library (Sprint 35), which is per-conversation: memory is cross-session and
    /// survives <c>NewChat</c> and session deletion. CRUD is capped (<see cref="MaxEntries"/> /
    /// <see cref="MaxEntryChars"/>) so the store can't grow without bound. Editor-only; the write path is
    /// overridable for tests via <see cref="OverrideRootForTests"/> so a test never touches the real project.
    /// </remarks>
    public static class AssistantMemoryStore
    {
        /// <summary>Consumer-space folder (relative to the project root) that holds memory entries.</summary>
        public const string RelativeRoot = "Assets/_Molca/AssistantMemory";

        /// <summary>Maximum number of stored entries; a save beyond this is refused with a clear error.</summary>
        public const int MaxEntries = 200;

        /// <summary>Maximum characters in a single entry's body (a durable fact, not a transcript).</summary>
        public const int MaxEntryChars = 8000;

        /// <summary>Maximum characters in an entry's one-line description.</summary>
        public const int MaxDescriptionChars = 240;

        private static string _rootOverride;

        /// <summary>
        /// Overrides the memory directory for tests (Sprint 77) so CRUD/recall run against a temp folder,
        /// never the real project. Pass <c>null</c> to restore the default consumer-space path.
        /// </summary>
        public static void OverrideRootForTests(string absoluteDirectory) => _rootOverride = absoluteDirectory;

        /// <summary>The absolute memory directory. Consumer-space by construction; created lazily on first write.</summary>
        public static string RootDirectory =>
            _rootOverride ?? Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".", RelativeRoot.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Whether <paramref name="absolutePath"/> is inside the consumer project and NOT inside the read-only
        /// Core package (Sprint 77 hard rule). Used by the write path and its regression test to guarantee
        /// memory never lands in the package.
        /// </summary>
        /// <param name="absolutePath">An absolute file or directory path.</param>
        /// <returns><c>true</c> when the path is under <c>Assets/</c> and not under a <c>Packages/</c> folder.</returns>
        public static bool IsUnderConsumerSpace(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return false;
            var norm = absolutePath.Replace('\\', '/');
            return norm.Contains("/Assets/") && !norm.Contains("/Packages/");
        }

        /// <summary>Normalizes an arbitrary label into a stable kebab-case slug (safe as a file name).</summary>
        /// <param name="raw">The proposed name or a phrase to derive one from.</param>
        /// <returns>A non-empty kebab-case slug (falls back to <c>"memory"</c> for empty input).</returns>
        public static string Slugify(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "memory";
            var sb = new StringBuilder(raw.Length);
            var lastDash = false;
            foreach (var ch in raw.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastDash = false; }
                else if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
            var slug = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "memory" : slug;
        }

        /// <summary>Lists all stored entries, most-recently-modified first. Empty when the store doesn't exist yet.</summary>
        public static IReadOnlyList<AssistantMemoryEntry> List()
        {
            var dir = RootDirectory;
            if (!Directory.Exists(dir)) return Array.Empty<AssistantMemoryEntry>();
            var entries = new List<(AssistantMemoryEntry Entry, DateTime Stamp)>();
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                if (string.Equals(Path.GetFileName(file), "INDEX.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryReadFile(file, out var entry))
                {
                    DateTime stamp;
                    try { stamp = File.GetLastWriteTimeUtc(file); } catch { stamp = DateTime.MinValue; }
                    entries.Add((entry, stamp));
                }
            }
            return entries.OrderByDescending(e => e.Stamp).Select(e => e.Entry).ToList();
        }

        /// <summary>Loads a single entry by name, or <c>null</c> when it doesn't exist.</summary>
        public static AssistantMemoryEntry Get(string name)
        {
            var slug = Slugify(name);
            var path = Path.Combine(RootDirectory, slug + ".md");
            return File.Exists(path) && TryReadFile(path, out var entry) ? entry : null;
        }

        /// <summary>
        /// Saves (creates or overwrites) an entry (Sprint 77). The body is capped at <see cref="MaxEntryChars"/>
        /// and the description at <see cref="MaxDescriptionChars"/>; a new entry beyond <see cref="MaxEntries"/>
        /// is refused. Regenerates <c>INDEX.md</c>. Returns the stored entry.
        /// </summary>
        /// <param name="name">The entry name/slug (kebab-cased).</param>
        /// <param name="description">One-line relevance summary.</param>
        /// <param name="body">The durable fact.</param>
        /// <param name="error">Set (and <c>null</c> returned) when the save is refused, e.g. the cap is hit.</param>
        /// <returns>The stored entry, or <c>null</c> on refusal.</returns>
        public static AssistantMemoryEntry Save(string name, string description, string body, out string error)
        {
            error = null;
            var slug = Slugify(name);
            if (string.IsNullOrWhiteSpace(body))
            {
                error = "A memory entry needs a non-empty fact in 'body'.";
                return null;
            }

            var dir = RootDirectory;
            var path = Path.Combine(dir, slug + ".md");
            var isNew = !File.Exists(path);
            if (isNew && CountEntries() >= MaxEntries)
            {
                error = $"Memory is full ({MaxEntries} entries). Delete an entry before adding another.";
                return null;
            }

            // Hard rule (Sprint 77): a write must land in consumer space, never the Core package.
            if (!IsUnderConsumerSpace(path))
            {
                error = "Refusing to write memory outside the consumer project (Assets/).";
                return null;
            }

            var entry = new AssistantMemoryEntry
            {
                Name = slug,
                Description = Truncate(SingleLine(description), MaxDescriptionChars),
                Body = Truncate(body.Trim(), MaxEntryChars)
            };

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(entry));
                RegenerateIndex();
            }
            catch (Exception ex)
            {
                error = $"Failed to write memory: {ex.Message}";
                return null;
            }
            return entry;
        }

        /// <summary>Deletes an entry by name (Sprint 77). Returns <c>true</c> when a file was removed.</summary>
        public static bool Delete(string name)
        {
            var slug = Slugify(name);
            var path = Path.Combine(RootDirectory, slug + ".md");
            if (!File.Exists(path) || !IsUnderConsumerSpace(path)) return false;
            try
            {
                File.Delete(path);
                var meta = path + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
                RegenerateIndex();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to delete memory '{slug}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns the entries most relevant to <paramref name="query"/> (Sprint 77), ranked by keyword overlap
        /// against name/description/body, keeping only positive-scoring entries and stopping once the running
        /// text would exceed <paramref name="approxTokenBudget"/> (~4 chars/token). A blank query returns the
        /// most recent entries within budget so a low-information turn still gets grounding.
        /// </summary>
        /// <param name="query">The user's turn text (or any phrase to match against).</param>
        /// <param name="approxTokenBudget">Approximate token ceiling for the returned set.</param>
        /// <returns>The relevant entries, most-relevant first, within budget.</returns>
        public static IReadOnlyList<AssistantMemoryEntry> Recall(string query, int approxTokenBudget)
        {
            var all = List();
            if (all.Count == 0) return all;

            var terms = Tokenize(query);
            IEnumerable<AssistantMemoryEntry> ordered;
            if (terms.Count == 0)
            {
                ordered = all; // already most-recent-first
            }
            else
            {
                ordered = all
                    .Select(e => (Entry: e, Score: ScoreEntry(e, terms)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Entry);
            }

            var budgetChars = Math.Max(0, approxTokenBudget) * 4;
            var kept = new List<AssistantMemoryEntry>();
            var used = 0;
            foreach (var entry in ordered)
            {
                var cost = (entry.Description?.Length ?? 0) + (entry.Body?.Length ?? 0) + (entry.Name?.Length ?? 0) + 8;
                if (kept.Count > 0 && used + cost > budgetChars) break;
                kept.Add(entry);
                used += cost;
            }
            return kept;
        }

        /// <summary>Renders a set of entries as a compact grounding block for turn-start injection (Sprint 77).</summary>
        public static string FormatForInjection(IReadOnlyList<AssistantMemoryEntry> entries)
        {
            if (entries == null || entries.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (e == null) continue;
                sb.Append("- ");
                if (!string.IsNullOrWhiteSpace(e.Description)) sb.Append(e.Description.Trim()).Append(": ");
                sb.AppendLine(SingleLine(e.Body));
            }
            return sb.ToString().TrimEnd();
        }

        // ── internals ────────────────────────────────────────────────────────────────────────

        private static int CountEntries()
        {
            var dir = RootDirectory;
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.md")
                .Count(f => !string.Equals(Path.GetFileName(f), "INDEX.md", StringComparison.OrdinalIgnoreCase));
        }

        private static int ScoreEntry(AssistantMemoryEntry e, IReadOnlyCollection<string> terms)
        {
            var haystack = ((e.Name ?? string.Empty) + " " + (e.Description ?? string.Empty) + " " + (e.Body ?? string.Empty))
                .ToLowerInvariant();
            var score = 0;
            foreach (var term in terms)
                if (haystack.Contains(term)) score++;
            return score;
        }

        private static List<string> Tokenize(string text)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return terms;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in text.ToLowerInvariant().Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '?', '!', '"', '\'', '(', ')', '[', ']', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                // Skip very short / stop-ish tokens so common words don't match everything.
                if (raw.Length < 3) continue;
                if (seen.Add(raw)) terms.Add(raw);
            }
            return terms;
        }

        private static string Serialize(AssistantMemoryEntry entry)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.Append("name: ").AppendLine(entry.Name);
            sb.Append("description: ").AppendLine(SingleLine(entry.Description));
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(entry.Body);
            sb.AppendLine();
            return sb.ToString();
        }

        private static bool TryReadFile(string path, out AssistantMemoryEntry entry)
        {
            entry = null;
            try
            {
                var text = File.ReadAllText(path);
                entry = Parse(text, Path.GetFileNameWithoutExtension(path));
                return entry != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to read memory '{path}': {ex.Message}");
                return false;
            }
        }

        /// <summary>Parses a memory file's frontmatter + body; internal for tests. Falls back to the file name for a missing name.</summary>
        internal static AssistantMemoryEntry Parse(string text, string fallbackName)
        {
            if (text == null) return null;
            var name = fallbackName ?? string.Empty;
            var description = string.Empty;
            var body = text;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                var close = -1;
                for (var i = 1; i < lines.Length; i++)
                    if (lines[i].Trim() == "---") { close = i; break; }
                if (close > 0)
                {
                    for (var i = 1; i < close; i++)
                    {
                        var line = lines[i];
                        var colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                        var value = line.Substring(colon + 1).Trim();
                        if (key == "name" && !string.IsNullOrWhiteSpace(value)) name = value;
                        else if (key == "description") description = value;
                    }
                    body = string.Join("\n", lines.Skip(close + 1)).Trim();
                }
            }

            return new AssistantMemoryEntry
            {
                Name = string.IsNullOrWhiteSpace(name) ? (fallbackName ?? "memory") : name,
                Description = description,
                Body = body
            };
        }

        private static void RegenerateIndex()
        {
            try
            {
                var dir = RootDirectory;
                if (!Directory.Exists(dir)) return;
                var sb = new StringBuilder();
                sb.AppendLine("# Assistant Project Memory");
                sb.AppendLine();
                sb.AppendLine("Cross-session, project-scoped facts the assistant maintains. One fact per file; editable by hand.");
                sb.AppendLine();
                foreach (var e in List())
                {
                    sb.Append("- **").Append(e.Name).Append("**");
                    if (!string.IsNullOrWhiteSpace(e.Description)) sb.Append(" — ").Append(e.Description.Trim());
                    sb.AppendLine();
                }
                File.WriteAllText(Path.Combine(dir, "INDEX.md"), sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to regenerate memory index: {ex.Message}");
            }
        }

        private static string SingleLine(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : s.Replace("\r", " ").Replace("\n", " ").Trim();

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, max) + "…";
        }
    }
}
