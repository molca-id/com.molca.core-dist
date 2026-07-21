using System;
using System.IO;
using Molca.Editor.Hub.Docs;
using Molca.Editor.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only documentation tools: let the assistant navigate and cite the reference guides shipped by
    /// Core and any <c>com.molca.*</c> package — the same guides browsable in the Hub (Molca → Hub → Docs).
    /// </summary>
    /// <remarks>
    /// All three tools are <see cref="McpToolKind.ReadOnly"/> and read the shared
    /// <see cref="MolcaDocsRegistry"/>, so they surface exactly what the Hub does (front-matter-driven
    /// title/category/order). Bodies are returned front-matter-stripped. The assistant can cite a doc and
    /// emit <c>molca://doc/&lt;id&gt;</c> / <c>molca://asset/&lt;guid&gt;</c> deep-links back into chat. See
    /// <c>Documentation~/reference/DOCS_AUTHORING.md</c>.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        private const int DocsDefaultSearchResults = 10;
        private const int DocsSnippetLength = 200;

        // ── molca_docs_list (read) ────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateDocsListTool() => new McpToolDefinition(
            name: "molca_docs_list",
            description: "Lists the Molca reference guides available in this project (from Core and any "
                       + "com.molca.* package), with id, title, and category. Read-only. Use molca_docs_read "
                       + "to fetch a guide's content by id.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"category\":{\"type\":\"string\",\"description\":\"Optional: only guides in this category.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteDocsList,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteDocsList(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var category = args.Value<string>("category");

            var rows = new JArray();
            foreach (var doc in MolcaDocsRegistry.GetDocs())
            {
                if (!string.IsNullOrWhiteSpace(category)
                    && !string.Equals(doc.Category, category, StringComparison.OrdinalIgnoreCase))
                    continue;

                rows.Add(new JObject
                {
                    ["id"] = doc.Id,
                    ["title"] = doc.Title,
                    ["category"] = doc.Category,
                    ["order"] = doc.Order,
                    ["owner"] = doc.OwnerPackage
                });
            }

            return new JObject { ["count"] = rows.Count, ["docs"] = rows }.ToString(Formatting.None);
        }

        // ── molca_docs_read (read) ────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateDocsReadTool() => new McpToolDefinition(
            name: "molca_docs_read",
            description: "Returns the full Markdown body of a reference guide by its id (see molca_docs_list). "
                       + "Front-matter is stripped. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"id\":{\"type\":\"string\",\"description\":\"The guide id, e.g. ASSISTANT_VISION.\"}}," +
                "\"required\":[\"id\"],\"additionalProperties\":false}",
            execute: ExecuteDocsRead,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteDocsRead(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var id = args.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return Error("'id' is required (see molca_docs_list).");

            var doc = MolcaDocsRegistry.FindById(id);
            if (doc == null) return Error($"No reference guide with id '{id}'. Use molca_docs_list to see available ids.");

            if (!TryReadBody(doc, out var body, out var readError)) return Error(readError);

            return new JObject
            {
                ["id"] = doc.Id,
                ["title"] = doc.Title,
                ["category"] = doc.Category,
                ["body"] = body
            }.ToString(Formatting.None);
        }

        // ── molca_docs_search (read) ──────────────────────────────────────────────────────────

        private static McpToolDefinition CreateDocsSearchTool() => new McpToolDefinition(
            name: "molca_docs_search",
            description: "Searches the reference guides by a case-insensitive substring in titles and bodies, "
                       + "returning matches with id, title, category, and a snippet. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Text to find in guide titles and bodies.\"}," +
                "\"maxResults\":{\"type\":\"integer\",\"description\":\"Optional cap on matches (default 10).\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}",
            execute: ExecuteDocsSearch,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteDocsSearch(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var query = args.Value<string>("query");
            if (string.IsNullOrWhiteSpace(query)) return Error("'query' is required.");
            var max = args["maxResults"]?.Value<int?>() ?? DocsDefaultSearchResults;
            if (max < 1) max = 1;

            var matches = new JArray();
            foreach (var doc in MolcaDocsRegistry.GetDocs())
            {
                if (matches.Count >= max) break;

                var titleHit = doc.Title?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!TryReadBody(doc, out var body, out _)) body = string.Empty;
                var bodyIndex = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (!titleHit && bodyIndex < 0) continue;

                matches.Add(new JObject
                {
                    ["id"] = doc.Id,
                    ["title"] = doc.Title,
                    ["category"] = doc.Category,
                    ["snippet"] = Snippet(body, bodyIndex)
                });
            }

            return new JObject { ["count"] = matches.Count, ["matches"] = matches }.ToString(Formatting.None);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private static bool TryReadBody(MolcaDocEntry doc, out string body, out string error)
        {
            body = string.Empty;
            error = null;
            try
            {
                body = MolcaMarkdown.StripFrontMatter(File.ReadAllText(doc.AbsolutePath), out _);
                return true;
            }
            catch (Exception e)
            {
                error = $"Could not read '{doc.Id}': {e.Message}";
                return false;
            }
        }

        /// <summary>A single-line snippet around <paramref name="index"/> (or the body start when &lt; 0).</summary>
        private static string Snippet(string body, int index)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            var start = index < 0 ? 0 : Math.Max(0, index - DocsSnippetLength / 4);
            var len = Math.Min(DocsSnippetLength, body.Length - start);
            var text = body.Substring(start, len).Replace("\r", " ").Replace("\n", " ").Trim();
            if (start > 0) text = "…" + text;
            if (start + len < body.Length) text += "…";
            return text;
        }
    }
}
