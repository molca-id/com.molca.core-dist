using System.IO;
using System.Threading;
using Molca.Editor.KnowledgeGraph;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // --- molca_kg_status --------------------------------------------------------------------------

        /// <summary>
        /// The <c>molca_kg_status</c> tool: reports whether a graphify knowledge graph has been built for
        /// this project and where it lives. Cheap (file checks only) so the assistant can decide whether a
        /// build is needed before querying.
        /// </summary>
        private static McpToolDefinition CreateKgStatusTool() => new McpToolDefinition(
            name: "molca_kg_status",
            description: "Reports the project knowledge-graph status: whether a graphify graph has been "
                       + "built (graph.json), its path and last-modified time, and the corpus directory. "
                       + "Use before molca_kg_query to check the graph exists; if not, call molca_kg_build.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteKgStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteKgStatus(string argumentsJson)
        {
            var exists = GraphifyCli.GraphExists;
            var result = new JObject
            {
                ["graphExists"] = exists,
                ["graphPath"] = GraphifyCli.GraphJsonPath,
                ["corpusDir"] = GraphifyCli.CorpusDir,
                ["lastBuilt"] = exists ? File.GetLastWriteTimeUtc(GraphifyCli.GraphJsonPath).ToString("o") : null,
                ["hint"] = exists ? null : "No graph yet — run molca_kg_build to index the project."
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        // --- molca_kg_query ---------------------------------------------------------------------------

        /// <summary>
        /// The <c>molca_kg_query</c> tool: a natural-language question answered against the project
        /// knowledge graph via <c>graphify query</c> (GraphRAG traversal). The payoff tool — lets the
        /// assistant answer open-ended "how does X work / what relates to Y" questions about the codebase.
        /// </summary>
        private static McpToolDefinition CreateKgQueryTool() => new McpToolDefinition(
            name: "molca_kg_query",
            description: "Answers a natural-language question about the project (code, assets, docs) using "
                       + "the graphify knowledge graph. Pass 'question'; optionally 'dfs' (trace one path "
                       + "instead of broad context) and 'budget' (max answer tokens). Requires a built "
                       + "graph (see molca_kg_status / molca_kg_build).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"question\":{\"type\":\"string\",\"description\":\"The question to answer.\"}," +
                "\"dfs\":{\"type\":\"boolean\",\"description\":\"Depth-first (trace a specific path) instead of broad BFS context.\"}," +
                "\"budget\":{\"type\":\"integer\",\"description\":\"Cap the answer at N tokens.\"}}," +
                "\"required\":[\"question\"],\"additionalProperties\":false}",
            executeAsync: ExecuteKgQuery,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteKgQuery(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var question = args.Value<string>("question");
            if (string.IsNullOrWhiteSpace(question))
                return KgError("Provide a 'question'.");
            if (!GraphifyCli.GraphExists)
                return KgError("No knowledge graph built yet. Run molca_kg_build first.");

            var cmd = "query " + GraphifyCli.Quote(question);
            if (args.Value<bool?>("dfs") == true) cmd += " --dfs";
            var budget = args.Value<int?>("budget");
            if (budget.HasValue && budget.Value > 0) cmd += " --budget " + budget.Value;

            return await RunGraphify(cmd);
        }

        // --- molca_kg_path ----------------------------------------------------------------------------

        /// <summary>The <c>molca_kg_path</c> tool: the shortest relationship path between two concepts.</summary>
        private static McpToolDefinition CreateKgPathTool() => new McpToolDefinition(
            name: "molca_kg_path",
            description: "Finds the shortest relationship path between two concepts/entities in the project "
                       + "knowledge graph (graphify path). Pass 'from' and 'to'. Requires a built graph.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"from\":{\"type\":\"string\"},\"to\":{\"type\":\"string\"}}," +
                "\"required\":[\"from\",\"to\"],\"additionalProperties\":false}",
            executeAsync: ExecuteKgPath,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteKgPath(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var from = args.Value<string>("from");
            var to = args.Value<string>("to");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return KgError("Provide both 'from' and 'to'.");
            if (!GraphifyCli.GraphExists)
                return KgError("No knowledge graph built yet. Run molca_kg_build first.");

            return await RunGraphify($"path {GraphifyCli.Quote(from)} {GraphifyCli.Quote(to)}");
        }

        // --- molca_kg_explain -------------------------------------------------------------------------

        /// <summary>The <c>molca_kg_explain</c> tool: a plain-language explanation of one graph node.</summary>
        private static McpToolDefinition CreateKgExplainTool() => new McpToolDefinition(
            name: "molca_kg_explain",
            description: "Explains a single concept/entity (node) in the project knowledge graph in plain "
                       + "language (graphify explain). Pass 'node'. Requires a built graph.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"node\":{\"type\":\"string\"}},\"required\":[\"node\"],\"additionalProperties\":false}",
            executeAsync: ExecuteKgExplain,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteKgExplain(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var node = args.Value<string>("node");
            if (string.IsNullOrWhiteSpace(node))
                return KgError("Provide a 'node'.");
            if (!GraphifyCli.GraphExists)
                return KgError("No knowledge graph built yet. Run molca_kg_build first.");

            return await RunGraphify($"explain {GraphifyCli.Quote(node)}");
        }

        // --- molca_kg_build ---------------------------------------------------------------------------

        /// <summary>
        /// The <c>molca_kg_build</c> tool (Action): (re)builds the project knowledge graph by running
        /// graphify over the Assets tree. Action-classified (allowlist + confirmation) because it writes
        /// to disk and incurs LLM cost. Incremental by default (<c>--update</c>).
        /// </summary>
        private static McpToolDefinition CreateKgBuildTool() => new McpToolDefinition(
            name: "molca_kg_build",
            description: "Builds (or incrementally updates) the project knowledge graph with graphify over "
                       + "the Assets tree, so molca_kg_query/path/explain can answer questions about the "
                       + "project. Writes graphify-out/graph.json and incurs LLM cost. Pass 'full':true to "
                       + "rebuild from scratch instead of --update. Optional 'timeoutMinutes' overrides "
                       + "the default graphify build budget for very large projects.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"full\":{\"type\":\"boolean\",\"description\":\"Rebuild from scratch instead of incremental --update.\"}," +
                "\"timeoutMinutes\":{\"type\":\"integer\",\"description\":\"Build timeout in minutes. Defaults to 30; clamped to 5-180.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteKgBuild,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible,
            invocationTimeoutMs: GraphifyCli.ResolveBuildTimeoutMs(GraphifyCli.MaxBuildTimeoutMinutes) + 60_000);

        private static async Awaitable<string> ExecuteKgBuild(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            bool full = args.Value<bool?>("full") == true;
            var timeoutMs = GraphifyCli.ResolveBuildTimeoutMs(args.Value<int?>("timeoutMinutes"));

            McpProgress.Report("Exporting Unity facts...", 0.05f, "kg-build");

            // Refresh the Unity facts corpus (asset + type wiring graphify can't see in raw source) so the
            // build indexes Unity structure, not just C# text. Best-effort: a corpus failure shouldn't
            // block indexing the code/docs.
            UnityFactsExporter.ExportSummary export = default;
            string exportError = null;
            try { export = UnityFactsExporter.ExportAll(); }
            catch (System.Exception ex) { exportError = ex.Message; }

            McpProgress.Report(exportError == null
                ? $"Unity facts exported ({export.TypeCount} types, {export.AssetCount} assets)."
                : "Unity facts export failed; continuing with source/docs graph.",
                0.15f,
                "kg-build");

            // Index the Assets tree (project + SDK), the Core package source, and the facts corpus.
            var cmd = GraphifyCli.BuildIndexArgs(full);

            // Builds can be long (LLM extraction over the whole project); allow a generous timeout.
            McpProgress.Report("Starting graphify build...", 0.2f, "kg-build");
            var result = await GraphifyCli.RunAsync(
                cmd,
                CancellationToken.None,
                timeoutMs: timeoutMs,
                onProgressLine: line => McpProgress.Report("graphify: " + line, null, "kg-build"));
            McpProgress.Report(result.Ok ? "Knowledge graph build complete." : "Knowledge graph build failed.", 1f, "kg-build");
            return ShapeBuildResult(result, export, exportError);
        }

        private static string ShapeBuildResult(GraphifyResult result, UnityFactsExporter.ExportSummary export, string exportError)
        {
            if (result.NotFound)
                return KgError("graphify CLI not found. Install it (see https://graphify.net) and ensure "
                             + "'graphify' is on PATH.");
            if (!result.Ok)
                return KgError($"graphify build failed (exit {result.ExitCode}). {result.StdErr}");

            return new JObject
            {
                ["ok"] = true,
                ["output"] = result.StdOut,
                ["facts"] = new JObject
                {
                    ["corpusDir"] = export.CorpusDir,
                    ["types"] = export.TypeCount,
                    ["assets"] = export.AssetCount,
                    ["exportError"] = exportError
                }
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // --- shared helpers ---------------------------------------------------------------------------

        private static async Awaitable<string> RunGraphify(string graphifyArgs)
        {
            var result = await GraphifyCli.RunAsync(graphifyArgs, CancellationToken.None);
            return ShapeResult(result, "query");
        }

        private static string ShapeResult(GraphifyResult result, string op)
        {
            if (result.NotFound)
                return KgError("graphify CLI not found. Install it (see https://graphify.net) and ensure "
                             + "'graphify' is on PATH.");
            if (!result.Ok)
                return KgError($"graphify {op} failed (exit {result.ExitCode}). {result.StdErr}");

            return new JObject
            {
                ["ok"] = true,
                ["output"] = result.StdOut
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string KgError(string message)
            => new JObject { ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
