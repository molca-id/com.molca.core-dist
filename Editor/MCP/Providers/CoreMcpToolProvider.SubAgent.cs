namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>Name of the single research sub-agent tool (Sprint 56); the assistant bridge intercepts it.</summary>
        public const string SpawnSubtaskToolName = "molca_spawn_subtask";

        /// <summary>Name of the batch research sub-agent tool (Sprint 56); runs several sub-agents concurrently.</summary>
        public const string SpawnSubtasksToolName = "molca_spawn_subtasks";

        /// <summary>
        /// The <c>molca_spawn_subtask</c> tool (Sprint 56): delegates a broad research question to a bounded,
        /// read-only sub-agent that runs in its own throwaway context and returns only a short digest, so the
        /// verbose tool output never enters the main chat's history.
        /// </summary>
        /// <remarks>
        /// Read-only and interactive: the in-editor assistant bridge intercepts it, runs the sub-agent loop,
        /// and returns its digest as the tool result. Outside that surface the fallback returns an error.
        /// </remarks>
        private static McpToolDefinition CreateSpawnSubtaskTool() => new McpToolDefinition(
            name: SpawnSubtaskToolName,
            description: "Delegate a broad, read-only research question (e.g. \"how is X wired across the "
                       + "project\", \"which scenes reference Y\") to a sub-agent that reads files / queries "
                       + "the knowledge graph / scans scenes in its own context and returns only a concise "
                       + "digest. Use this instead of reading many files inline so your context stays small. "
                       + "Provide 'prompt' (the question) and optional 'focus' (extra scoping). The sub-agent "
                       + "is read-only and cannot modify anything.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{"
                + "\"prompt\":{\"type\":\"string\",\"description\":\"The research question for the sub-agent.\"},"
                + "\"focus\":{\"type\":\"string\",\"description\":\"Optional extra scoping (files, systems, constraints).\"}"
                + "},\"required\":[\"prompt\"],\"additionalProperties\":false}",
            execute: ExecuteSpawnSubtaskFallback,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>
        /// The <c>molca_spawn_subtasks</c> batch tool (Sprint 56): runs several read-only research sub-agents
        /// concurrently (a "swarm") and returns each digest, for independent questions you want answered in
        /// parallel without each one's tool output entering your context.
        /// </summary>
        private static McpToolDefinition CreateSpawnSubtasksTool() => new McpToolDefinition(
            name: SpawnSubtasksToolName,
            description: "Run several independent read-only research sub-agents concurrently and get each "
                       + "digest back. Use for parallel, unrelated questions. Provide 'tasks' as an array of "
                       + "{prompt, focus?}. Sub-agents are read-only, bounded, and capped per turn.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{"
                + "\"tasks\":{\"type\":\"array\",\"description\":\"Research questions to run in parallel.\",\"items\":{"
                + "\"type\":\"object\",\"properties\":{"
                + "\"prompt\":{\"type\":\"string\",\"description\":\"The research question.\"},"
                + "\"focus\":{\"type\":\"string\",\"description\":\"Optional extra scoping.\"}"
                + "},\"required\":[\"prompt\"],\"additionalProperties\":false}}"
                + "},\"required\":[\"tasks\"],\"additionalProperties\":false}",
            execute: ExecuteSpawnSubtaskFallback,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>Fallback for surfaces with no interactive assistant; the in-editor bridge intercepts these tools first.</summary>
        private static string ExecuteSpawnSubtaskFallback(string argumentsJson) =>
            "{\"error\":\"molca_spawn_subtask requires the in-editor Molca Assistant to run the sub-agent loop; "
            + "no sub-agent runner is available in this context.\"}";
    }
}
