using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Appends one redacted JSONL record per completed run under <c>Library/Molca/Automation/</c> (§15,
    /// §16): command, transport, policy-relevant status, duration, redacted arguments, and revert
    /// metadata. Never records credentials or arbitrary content, and never throws into a run — an audit
    /// failure is logged, not propagated. Kept out of Git by living under <c>Library/</c>.
    /// </summary>
    public static class MolcaAutomationAuditLog
    {
        private const string RelativeDir = "Library/Molca/Automation";
        private const string FileName = "audit.jsonl";

        /// <summary>Records one run's outcome as a redacted JSONL line.</summary>
        /// <param name="context">The run context (run id, command, transport, arguments).</param>
        /// <param name="result">The terminal result.</param>
        public static void Record(MolcaCommandContext context, MolcaCommandResult result)
        {
            if (context == null || result == null) return;
            try
            {
                var record = new JObject
                {
                    ["timestampUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["runId"] = context.RunId,
                    ["command"] = result.Command,
                    ["transport"] = context.Transport.ToString(),
                    ["status"] = MolcaCommandResult.WireStatusName(result.Status.ToString()),
                    ["durationMs"] = result.DurationMs,
                    ["arguments"] = Redact(context.Arguments),
                    ["revert"] = result.Revert.ToJson()
                };

                var dir = Path.GetFullPath(RelativeDir);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, FileName), record.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca Automation] Audit write failed (run continues): {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a copy of <paramref name="arguments"/> with any value whose key looks like a secret
        /// (token/secret/key/password/authorization/credential) replaced by <c>"[redacted]"</c>.
        /// </summary>
        /// <param name="arguments">The raw arguments object.</param>
        /// <returns>A redacted copy, safe to persist.</returns>
        public static JObject Redact(JObject arguments)
        {
            var copy = arguments != null ? (JObject)arguments.DeepClone() : new JObject();
            foreach (var prop in copy.Properties())
            {
                if (LooksSecret(prop.Name))
                    prop.Value = "[redacted]";
                else if (prop.Value is JObject nested)
                    prop.Value = Redact(nested);
            }
            return copy;
        }

        private static bool LooksSecret(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var k = key.ToLowerInvariant();
            return k.Contains("token") || k.Contains("secret") || k.Contains("password") ||
                   k.Contains("apikey") || k.Contains("api_key") || k.Contains("authorization") ||
                   k.Contains("credential");
        }
    }
}
