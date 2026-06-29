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
            description: "Reads text/source file(s) inside the project. Use 'path' for one file (with optional "
                       + "1-based 'startLine'/'endLine' paging) or 'paths' (array) to read several files in one "
                       + "call — batch related files together instead of one call each. Output is line-numbered; "
                       + "batch returns a 'files' array (each a result or a per-file {path,error}). Use this to "
                       + "read the file a molca_kg_query result points at (its src=path:Lnn). Read-only; cannot "
                       + "escape the project root.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Project-relative or in-project absolute file path (single file).\"}," +
                "\"paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Several file paths to read in one call (whole files).\"}," +
                "\"startLine\":{\"type\":\"integer\",\"description\":\"1-based first line to return (single-file only).\"}," +
                "\"endLine\":{\"type\":\"integer\",\"description\":\"1-based last line to return (single-file only).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteReadSource,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteReadSource(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            // Batch mode (Sprint 67.4): 'paths' reads several files in one call, returning a 'files' array
            // (each entry is a normal read result or a per-file { path, error }). One call instead of N.
            if (args["paths"] is JArray paths && paths.Count > 0)
            {
                var files = new JArray();
                foreach (var token in paths)
                {
                    var p = token?.ToString();
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    files.Add(ReadOneFile(p, null, null));
                }
                return new JObject { ["files"] = files }.ToString(Newtonsoft.Json.Formatting.None);
            }

            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return KgError("Provide a 'path' (or 'paths' to read several files at once).");

            return ReadOneFile(path, args.Value<int?>("startLine"), args.Value<int?>("endLine"))
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Reads one project file (root-confined, line-bounded) into a result <see cref="JObject"/>.</summary>
        private static JObject ReadOneFile(string path, int? startLine, int? endLine)
        {
            string fullPath;
            try
            {
                // Resolve relative paths against the project root; normalize so '..' can't escape it.
                var combined = Path.IsPathRooted(path) ? path : Path.Combine(GraphifyCli.ProjectRoot, path);
                fullPath = Path.GetFullPath(combined);
            }
            catch (Exception ex)
            {
                return FileError(path, $"Invalid path: {ex.Message}");
            }

            var root = Path.GetFullPath(GraphifyCli.ProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return FileError(path, "Path is outside the project root; refused.");

            if (!File.Exists(fullPath))
                return FileError(path, $"File not found: {fullPath}");

            string[] lines;
            try { lines = File.ReadAllLines(fullPath); }
            catch (Exception ex) { return FileError(path, $"Could not read file: {ex.Message}"); }

            int total = lines.Length;
            int start = Math.Max(1, startLine ?? 1);
            int end = endLine ?? total;
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
            return result;
        }

        private static JObject FileError(string path, string message) => new JObject
        {
            ["path"] = path,
            ["error"] = message
        };
    }
}
