using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Append-only JSONL audit trail for add-on install, update, remove, and verification failures.
    /// Signed URLs and entitlement tokens are never recorded.
    /// </summary>
    internal static class AddonAuditLog
    {
        internal static void Record(string action, string outcome, string id, string version,
            string sha256 = null, string sourceHost = null, string error = null)
        {
            try
            {
                var entry = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["action"] = action,
                    ["outcome"] = outcome,
                    ["id"] = id ?? string.Empty,
                    ["version"] = version ?? string.Empty,
                };
                if (!string.IsNullOrEmpty(sha256)) entry["sha256"] = sha256;
                if (!string.IsNullOrEmpty(sourceHost)) entry["sourceHost"] = sourceHost;
                if (!string.IsNullOrEmpty(error)) entry["error"] = error;

                string path = LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.AppendAllText(path, entry.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Add-ons] Could not write audit entry: {exception.Message}");
            }
        }

        private static string LogPath()
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            return Path.Combine(root, "Library", "Molca", "addon-audit.jsonl");
        }
    }
}
