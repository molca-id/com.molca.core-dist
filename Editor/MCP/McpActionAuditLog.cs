using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Append-only audit trail of action-tool invocations (Sprint 17.5): tool, arguments, caller,
    /// timestamp, and outcome. Written as JSON Lines to <c>&lt;project&gt;/Library/Molca/mcp-action-audit.jsonl</c>
    /// — under <c>Library/</c> so it persists across sessions but is never committed.
    /// </summary>
    public static class McpActionAuditLog
    {
        private static readonly System.Threading.AsyncLocal<string> CallerOverride =
            new System.Threading.AsyncLocal<string>();

        /// <summary>Overrides the caller label for a bounded shared-runtime operation.</summary>
        internal static IDisposable BeginCaller(string caller)
        {
            var previous = CallerOverride.Value;
            CallerOverride.Value = caller;
            return new CallerScope(() => CallerOverride.Value = previous);
        }

        private static string LogPath
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(projectRoot, "Library", "Molca", "mcp-action-audit.jsonl");
            }
        }

        /// <summary>
        /// Records one action-tool invocation outcome.
        /// </summary>
        /// <param name="tool">Tool name.</param>
        /// <param name="argumentsJson">The invocation arguments (JSON).</param>
        /// <param name="caller">Originating front-end, e.g. "bridge" or "chat".</param>
        /// <param name="outcome">"executed", "refused", "denied", or "failed".</param>
        /// <param name="error">Optional error detail when the outcome is "failed".</param>
        public static void Record(string tool, string argumentsJson, string caller, string outcome, string error = null)
        {
            try
            {
                caller = string.IsNullOrWhiteSpace(CallerOverride.Value)
                    ? caller : CallerOverride.Value;
                var entry = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["tool"] = tool,
                    ["caller"] = caller,
                    ["outcome"] = outcome,
                    ["arguments"] = SafeParse(argumentsJson)
                };
                if (!string.IsNullOrEmpty(error))
                    entry["error"] = error;

                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.AppendAllText(path, entry.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Auditing must never break the tool path; surface as a warning only.
                Debug.LogWarning($"[Molca MCP] Failed to write action audit entry: {ex.Message}");
            }
        }

        private sealed class CallerScope : IDisposable
        {
            private Action _dispose;
            internal CallerScope(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                var dispose = _dispose;
                _dispose = null;
                dispose?.Invoke();
            }
        }

        private static JToken SafeParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try { return JToken.Parse(json); }
            catch { return json; } // store raw string if it isn't valid JSON
        }
    }
}
