using System;
using System.IO;
using System.Text;
using Molca.Editor.KnowledgeGraph;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // Cap a single read so a huge generated file can't blow the context budget; callers page with
        // startLine/endLine. ~600 lines comfortably covers a typical class.
        private const int MaxReadLines = 600;

        /// <summary>
        /// The <c>molca_read_source</c> tool: reads a source/text file inside the project by path. This is
        /// the missing link after a knowledge-graph hit — <c>molca_kg_query</c> returns a node's
        /// <c>src=path:Lnn</c>, and this tool lets the assistant actually read that file to explain how it
        /// works, instead of re-querying the graph. Read-only; confined to the project root.
        /// </summary>
        private static McpToolDefinition CreateReadSourceTool() => new McpToolDefinition(
            name: "molca_read_source",
            description: "Reads a text/source file inside the project by path (project-relative, e.g. "
                       + "'Packages/com.molca.core/Runtime/ContentPackage/Storage/ContentPackageStorageProvider.cs', "
                       + "or an absolute path within the project). Optional 'startLine'/'endLine' (1-based) "
                       + "page through large files; output is line-numbered. Use this to read the file a "
                       + "molca_kg_query result points at (its src=path:Lnn) and explain it from the actual "
                       + "code. Read-only; cannot escape the project root.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Project-relative or in-project absolute file path.\"}," +
                "\"startLine\":{\"type\":\"integer\",\"description\":\"1-based first line to return.\"}," +
                "\"endLine\":{\"type\":\"integer\",\"description\":\"1-based last line to return.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteReadSource,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteReadSource(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return KgError("Provide a 'path'.");

            string fullPath;
            try
            {
                // Resolve relative paths against the project root; normalize so '..' can't escape it.
                var combined = Path.IsPathRooted(path) ? path : Path.Combine(GraphifyCli.ProjectRoot, path);
                fullPath = Path.GetFullPath(combined);
            }
            catch (Exception ex)
            {
                return KgError($"Invalid path: {ex.Message}");
            }

            var root = Path.GetFullPath(GraphifyCli.ProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return KgError("Path is outside the project root; refused.");

            if (!File.Exists(fullPath))
                return KgError($"File not found: {fullPath}");

            string[] lines;
            try { lines = File.ReadAllLines(fullPath); }
            catch (Exception ex) { return KgError($"Could not read file: {ex.Message}"); }

            int total = lines.Length;
            int start = Math.Max(1, args.Value<int?>("startLine") ?? 1);
            int end = args.Value<int?>("endLine") ?? total;
            if (end < start) end = start;
            end = Math.Min(end, total);

            // Bound the span so a whole large file can't flood the response; report truncation.
            bool truncated = false;
            if (end - start + 1 > MaxReadLines)
            {
                end = start + MaxReadLines - 1;
                truncated = true;
            }

            var sb = new StringBuilder();
            for (int i = start; i <= end && i <= total; i++)
                sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');

            var result = new JObject
            {
                ["path"] = fullPath,
                ["totalLines"] = total,
                ["startLine"] = start,
                ["endLine"] = end,
                ["truncated"] = truncated,
                ["content"] = sb.ToString()
            };
            if (truncated)
                result["hint"] = $"Showing {MaxReadLines} lines; request startLine={end + 1} to continue.";
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
