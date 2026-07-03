using System.Linq;
using Molca.Editor.Mcp.Assistant;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Cross-session project-memory tools (Sprint 77): <c>molca_memory_recall</c> reads durable facts about
    /// this project/user for grounding, and <c>molca_memory_save</c> / <c>molca_memory_delete</c> maintain them.
    /// Memory is file-backed under the <b>consumer</b> project (<see cref="AssistantMemoryStore.RelativeRoot"/>),
    /// never inside the read-only Core package, and compounds with KG grounding (Sprint 47).
    /// </summary>
    /// <remarks>
    /// <c>molca_memory_recall</c> is <see cref="McpToolKind.ReadOnly"/> and auto-consulted for grounding; the
    /// mutating tools are <see cref="McpToolKind.Action"/> so a memory write is a <b>confirmed</b> action, not
    /// silent. Scope discipline (durable facts only, absolute dates, not conversation minutiae) is enforced by a
    /// base-prompt rule mirroring the framework's own memory guidance. See
    /// <c>Documentation~/reference/ASSISTANT_MEMORY.md</c>.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_memory_recall (read) ────────────────────────────────────────────────────────

        private static McpToolDefinition CreateMemoryRecallTool() => new McpToolDefinition(
            name: "molca_memory_recall",
            description: "Returns durable, cross-session facts about this project/user relevant to a query "
                       + "(conventions, decisions, environment). Read-only. Relevant entries are also injected "
                       + "automatically at the start of a turn; call this to look further or list what's stored.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"What to recall; empty lists the most recent entries.\"}," +
                "\"maxResults\":{\"type\":\"integer\",\"description\":\"Optional cap on returned entries (default 10).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteMemoryRecall,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteMemoryRecall(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var query = args.Value<string>("query") ?? string.Empty;
            var max = args["maxResults"]?.Value<int?>() ?? 10;
            if (max < 1) max = 1;

            // A generous token budget for an on-demand recall; turn-start injection uses the settings budget.
            var entries = AssistantMemoryStore.Recall(query, approxTokenBudget: 8000).Take(max).ToList();
            var rows = new JArray();
            foreach (var e in entries)
                rows.Add(new JObject
                {
                    ["name"] = e.Name,
                    ["description"] = e.Description,
                    ["body"] = e.Body
                });
            return new JObject { ["count"] = rows.Count, ["entries"] = rows }.ToString(Formatting.None);
        }

        // ── molca_memory_save (action, confirmed) ────────────────────────────────────────────

        private static McpToolDefinition CreateMemorySaveTool() => new McpToolDefinition(
            name: "molca_memory_save",
            description: "Saves a durable fact about this project/user to cross-session memory (overwrites an "
                       + "entry with the same name). Confirmed action. Save conventions, decisions, and "
                       + "environment — NOT conversation minutiae or what the code/KG already records; convert "
                       + "relative dates to absolute.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"Short kebab-case slug identifying the fact.\"}," +
                "\"description\":{\"type\":\"string\",\"description\":\"One-line summary used for recall relevance.\"}," +
                "\"body\":{\"type\":\"string\",\"description\":\"The durable fact (absolute dates).\"}}," +
                "\"required\":[\"name\",\"body\"],\"additionalProperties\":false}",
            execute: ExecuteMemorySave,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteMemorySave(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var name = args.Value<string>("name");
            var body = args.Value<string>("body");
            var description = args.Value<string>("description") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required (a short kebab-case slug).");
            if (string.IsNullOrWhiteSpace(body)) return Error("'body' is required (the durable fact to remember).");

            var entry = AssistantMemoryStore.Save(name, description, body, out var error);
            if (entry == null) return Error(error);

            AssetDatabase.Refresh();
            return new JObject
            {
                ["saved"] = true,
                ["name"] = entry.Name,
                ["description"] = entry.Description
            }.ToString(Formatting.None);
        }

        // ── molca_memory_delete (action, confirmed) ──────────────────────────────────────────

        private static McpToolDefinition CreateMemoryDeleteTool() => new McpToolDefinition(
            name: "molca_memory_delete",
            description: "Deletes a cross-session memory entry by name. Confirmed action.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"The entry name/slug to delete.\"}}," +
                "\"required\":[\"name\"],\"additionalProperties\":false}",
            execute: ExecuteMemoryDelete,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteMemoryDelete(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var name = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required.");

            var removed = AssistantMemoryStore.Delete(name);
            if (removed) AssetDatabase.Refresh();
            return new JObject
            {
                ["deleted"] = removed,
                ["name"] = AssistantMemoryStore.Slugify(name)
            }.ToString(Formatting.None);
        }
    }
}
