using System;
using System.Collections.Generic;
using Molca.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public sealed partial class UnityMcpToolProvider
    {
        // Hard cap on returned entries so a flooded console can't blow the response/context budget;
        // callers page newer-than-X with 'sinceSequence'.
        private const int MaxConsoleEntries = 500;
        private const int DefaultConsoleEntries = 100;

        // Stack traces are truncated per entry so an exception storm stays readable.
        private const int MaxStackTraceChars = 2000;

        /// <summary>
        /// The <c>molca_unity_read_console</c> tool: reports recently captured Unity console messages
        /// from the editor-lifetime ring buffer (<see cref="McpConsoleLog"/>). Read-only; works in Edit
        /// or Play mode. Lets the assistant see what the editor logged — compile spam, warnings, runtime
        /// errors, and exceptions with their stack traces — without any <c>LogEntries</c> reflection.
        /// </summary>
        private static McpToolDefinition CreateReadConsoleTool() => new McpToolDefinition(
            name: "molca_unity_read_console",
            description: "Reads recently captured Unity console messages (logs, warnings, errors, "
                       + "exceptions) from a bounded editor-session ring buffer. Optional filters: "
                       + "'severity' ('all'|'log'|'warning'|'error'; 'error' also matches Exception/Assert), "
                       + "'contains' (case-insensitive substring), 'limit' (default 100, max 500, returns "
                       + "the most recent matches), 'sinceSequence' (only entries with a higher 'sequence' "
                       + "than this — poll for new logs without re-reading old ones), and 'includeStackTrace' "
                       + "(default: stack traces are returned for errors/exceptions only). The response also "
                       + "includes per-severity counts over the whole buffer and how many old entries were "
                       + "dropped. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"severity\":{\"type\":\"string\",\"enum\":[\"all\",\"log\",\"warning\",\"error\"]," +
                "\"description\":\"Severity filter. 'error' includes Exception and Assert. Default 'all'.\"}," +
                "\"contains\":{\"type\":\"string\",\"description\":\"Case-insensitive substring the message must contain.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (most recent first match). Default 100, max 500.\"}," +
                "\"sinceSequence\":{\"type\":\"integer\",\"description\":\"Only return entries whose 'sequence' is greater than this. Use for polling.\"}," +
                "\"includeStackTrace\":{\"type\":\"boolean\",\"description\":\"Force-include stack traces for all entries (not just errors).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteReadConsole,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteReadConsole(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var severity = (args.Value<string>("severity") ?? "all").Trim().ToLowerInvariant();
            var contains = args.Value<string>("contains");
            bool forceStack = args.Value<bool?>("includeStackTrace") ?? false;
            long sinceSequence = args.Value<long?>("sinceSequence") ?? 0;

            int limit = args.Value<int?>("limit") ?? DefaultConsoleEntries;
            limit = Math.Clamp(limit, 1, MaxConsoleEntries);

            var all = McpConsoleLog.Snapshot();

            // Whole-buffer severity tally — independent of the per-request filters below.
            int logs = 0, warnings = 0, errors = 0;
            foreach (var e in all)
            {
                switch (e.Type)
                {
                    case LogType.Warning: warnings++; break;
                    case LogType.Log: logs++; break;
                    default: errors++; break; // Error, Exception, Assert
                }
            }

            // Filter (chronological), then take the most recent `limit` so the newest context wins.
            var matched = new List<McpConsoleLog.Entry>();
            foreach (var e in all)
            {
                if (e.Sequence <= sinceSequence) continue;
                if (!MatchesSeverity(e.Type, severity)) continue;
                if (!string.IsNullOrEmpty(contains) &&
                    e.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                matched.Add(e);
            }

            int returnedFrom = Math.Max(0, matched.Count - limit);
            bool truncated = returnedFrom > 0;

            var entriesJson = new JArray();
            for (int i = returnedFrom; i < matched.Count; i++)
            {
                var e = matched[i];
                var obj = new JObject
                {
                    ["sequence"] = e.Sequence,
                    ["type"] = e.Type.ToString(),
                    ["timestampUtc"] = e.TimestampUtc.ToString("o"),
                    ["message"] = e.Message
                };

                bool isError = e.Type != LogType.Log && e.Type != LogType.Warning;
                if ((forceStack || isError) && !string.IsNullOrEmpty(e.StackTrace))
                {
                    var stack = e.StackTrace;
                    if (stack.Length > MaxStackTraceChars)
                        stack = stack.Substring(0, MaxStackTraceChars) + "\n…(truncated)";
                    obj["stackTrace"] = stack;
                }
                entriesJson.Add(obj);
            }

            var result = new JObject
            {
                ["bufferCapacity"] = McpConsoleLog.Capacity,
                ["totalSeen"] = McpConsoleLog.TotalSeen,
                ["dropped"] = McpConsoleLog.DroppedCount,
                ["counts"] = new JObject
                {
                    ["log"] = logs,
                    ["warning"] = warnings,
                    ["error"] = errors
                },
                ["matchedCount"] = matched.Count,
                ["returnedCount"] = entriesJson.Count,
                ["truncated"] = truncated,
                ["entries"] = entriesJson
            };
            if (truncated)
                result["hint"] = "More matches exist; raise 'limit' (max " + MaxConsoleEntries +
                                 ") or narrow with 'severity'/'contains'. The most recent matches are shown.";
            return result.ToString(Formatting.None);
        }

        /// <summary>Maps a <see cref="LogType"/> against the requested severity filter token.</summary>
        private static bool MatchesSeverity(LogType type, string severity) => severity switch
        {
            "log" => type == LogType.Log,
            "warning" => type == LogType.Warning,
            // 'error' groups the three failure severities Unity distinguishes.
            "error" => type == LogType.Error || type == LogType.Exception || type == LogType.Assert,
            _ => true // "all" or unrecognized
        };
    }
}
