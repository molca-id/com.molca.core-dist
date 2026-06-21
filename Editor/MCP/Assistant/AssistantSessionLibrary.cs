using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Lightweight metadata header stored inside each session file so the switcher can list
    /// conversations without rebuilding their full transcripts (Sprint 35). Times are ISO-8601 UTC
    /// strings for stable, culture-independent (de)serialization.
    /// </summary>
    [Serializable]
    public sealed class SessionMeta
    {
        /// <summary>Stable session id (also the file stem under the sessions folder).</summary>
        public string Id;
        /// <summary>Human-readable title, derived from the first user message.</summary>
        public string Title;
        /// <summary>ISO-8601 UTC creation time.</summary>
        public string CreatedUtc;
        /// <summary>ISO-8601 UTC time of the last save.</summary>
        public string UpdatedUtc;
        /// <summary>Number of transcript turns at the last save (for a quick size hint).</summary>
        public int TurnCount;
        /// <summary>Cumulative prompt (input) tokens billed across this session (Sprint 49); 0 on legacy files.</summary>
        public long InputTokens;
        /// <summary>Cumulative completion (output) tokens across this session (Sprint 49); 0 on legacy files.</summary>
        public long OutputTokens;

        /// <summary>Parses <see cref="UpdatedUtc"/>, falling back to <see cref="DateTime.MinValue"/>.</summary>
        public DateTime UpdatedAt =>
            DateTime.TryParse(UpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt : DateTime.MinValue;
    }

    /// <summary>
    /// Manages the on-disk library of assistant conversations (Sprint 35): one JSON file per session
    /// under <c>Library/Molca/sessions/</c>, each carrying a <see cref="SessionMeta"/> header. Replaces
    /// the single-file model of <see cref="AssistantSessionStore"/> (which is retained for the one-time
    /// migration of the legacy file). All session state lives under <c>Library/</c> — never project
    /// content, excluded from version control.
    /// </summary>
    public static class AssistantSessionLibrary
    {
        private const int TitleMaxLength = 50;

        private static string SessionsDir =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Molca", "sessions");

        private static string PathFor(string id) => Path.Combine(SessionsDir, id + ".json");

        /// <summary>Creates a sortable, collision-resistant session id (timestamp + short GUID).</summary>
        public static string NewId() =>
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}".Substring(0, 24);

        /// <summary>Writes a session to disk, stamping/refreshing its metadata header.</summary>
        public static void Save(
            string id,
            IReadOnlyList<ChatTurn> transcript,
            IReadOnlyList<LlmMessage> history,
            IReadOnlyList<AssistantContextItem> context,
            string title,
            long inputTokens = 0,
            long outputTokens = 0)
        {
            if (string.IsNullOrEmpty(id)) return;

            // Preserve the original creation time and any previously stored title if the file exists.
            var createdUtc = DateTime.UtcNow.ToString("o");
            SessionMeta existing = null;
            if (AssistantSessionStore.TryReadMeta(PathFor(id), out existing) && existing != null &&
                !string.IsNullOrEmpty(existing.CreatedUtc))
                createdUtc = existing.CreatedUtc;

            // Title precedence: an explicit title (e.g. an LLM-generated one) wins; otherwise keep the
            // title already on disk so it isn't clobbered on every autosave; only as a last resort derive
            // one mechanically from the first user turn.
            var resolvedTitle = title;
            if (string.IsNullOrWhiteSpace(resolvedTitle))
                resolvedTitle = existing != null && !string.IsNullOrWhiteSpace(existing.Title)
                    ? existing.Title
                    : DeriveTitle(transcript);

            var meta = new SessionMeta
            {
                Id = id,
                Title = resolvedTitle,
                CreatedUtc = createdUtc,
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                TurnCount = transcript?.Count ?? 0,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
            AssistantSessionStore.SaveToPath(PathFor(id), transcript, history, context, meta);
        }

        /// <summary>Loads a session by id, including its metadata.</summary>
        public static bool TryLoad(
            string id,
            out List<ChatTurn> transcript,
            out List<LlmMessage> history,
            out List<AssistantContextItem> context,
            out SessionMeta meta)
        {
            transcript = new List<ChatTurn>();
            history = new List<LlmMessage>();
            context = new List<AssistantContextItem>();
            meta = null;
            if (string.IsNullOrEmpty(id)) return false;
            return AssistantSessionStore.TryLoadFromPath(PathFor(id), out transcript, out history, out context, out meta);
        }

        /// <summary>All sessions' metadata, most-recently-updated first.</summary>
        public static List<SessionMeta> ListSessions()
        {
            var list = new List<SessionMeta>();
            try
            {
                if (!Directory.Exists(SessionsDir)) return list;
                foreach (var file in Directory.GetFiles(SessionsDir, "*.json"))
                {
                    if (AssistantSessionStore.TryReadMeta(file, out var meta) && meta != null)
                    {
                        // Trust the file stem as the id if the header is missing one.
                        if (string.IsNullOrEmpty(meta.Id)) meta.Id = Path.GetFileNameWithoutExtension(file);
                        list.Add(meta);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to list assistant sessions: {ex.Message}");
            }
            return list.OrderByDescending(m => m.UpdatedAt).ToList();
        }

        /// <summary>Deletes a session file. No-op if it doesn't exist.</summary>
        public static void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var path = PathFor(id);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to delete assistant session {id}: {ex.Message}");
            }
        }

        /// <summary>True if at least one session file exists on disk.</summary>
        public static bool HasAny()
        {
            try { return Directory.Exists(SessionsDir) && Directory.GetFiles(SessionsDir, "*.json").Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// One-time migration (Sprint 35): if the sessions folder is empty but the legacy single-file
        /// session exists, import it as the first session and return its new id. Leaves the legacy file
        /// in place as a harmless fallback. Returns null when there is nothing to migrate.
        /// </summary>
        public static string MigrateLegacyIfNeeded()
        {
            if (HasAny()) return null;
            if (!AssistantSessionStore.TryLoadFromPath(AssistantSessionStore.LegacySessionPath,
                    out var transcript, out var history, out var context, out _))
                return null;
            if (transcript.Count == 0 && history.Count == 0) return null;

            var id = NewId();
            Save(id, transcript, history, context, DeriveTitle(transcript));
            return id;
        }

        /// <summary>Derives a session title from the first user turn, elided to a readable length.</summary>
        public static string DeriveTitle(IReadOnlyList<ChatTurn> transcript)
        {
            if (transcript != null)
            {
                foreach (var t in transcript)
                {
                    if (t == null || t.Kind != ChatTurnKind.User || string.IsNullOrWhiteSpace(t.Text)) continue;
                    var line = t.Text.Trim().Replace("\r", " ").Replace("\n", " ");
                    return line.Length <= TitleMaxLength ? line : line.Substring(0, TitleMaxLength - 1).TrimEnd() + "…";
                }
            }
            return "New chat";
        }
    }
}
