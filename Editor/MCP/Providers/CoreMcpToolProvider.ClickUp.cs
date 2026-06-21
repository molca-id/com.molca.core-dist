using System.Threading;
using Molca.Settings.Integration;
using Molca.Settings.Integration.ClickUp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// ClickUp tools: read the integration's connection/target state, list the project folder's tasks,
    /// list accessible workspaces, change a task's status, and create a task. Reads are
    /// <see cref="McpToolKind.ReadOnly"/>; the mutating tools are <see cref="McpToolKind.Action"/>, gated by
    /// the allowlist + confirmation guardrails.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Providers/</c> (partial of
    /// <see cref="CoreMcpToolProvider"/>; surfaced by convention-based discovery).
    /// All work goes through the single <see cref="ClickUpIntegrationProvider"/> registered in
    /// <see cref="IntegrationSettings"/> — the same provider the Hub Tasks section uses — so the personal API
    /// token (stored in <see cref="IntegrationCredentialStore"/>) never crosses MCP. Network tools run at
    /// <see cref="McpToolMode.Edit"/> on the main thread; the ClickUp REST API is the source of truth, so the
    /// Action tools are <see cref="McpToolReversibility.Irreversible"/> (a status change or new task cannot be
    /// rolled back via Unity Undo).
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_clickup_status (read) ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpStatusTool() => new McpToolDefinition(
            name: "molca_clickup_status",
            description: "Reads the ClickUp integration state: whether a token is stored, whether it has been "
                       + "verified this session, the status message, the target list/folder/workspace ids, and "
                       + "the canPush/canViewTasks readiness flags. The token itself is never returned. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteClickUpStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteClickUpStatus(string argumentsJson)
        {
            var provider = ResolveClickUpProvider(out string error);
            if (provider == null) return ClickUpError(error);

            return new JObject
            {
                ["enabled"] = provider.Enabled,
                ["hasToken"] = provider.HasToken,
                ["isConnected"] = provider.IsConnected,
                ["statusMessage"] = provider.StatusMessage,
                ["targetListId"] = provider.TargetListId,
                ["targetFolderId"] = provider.TargetFolderId,
                ["targetWorkspaceId"] = provider.TargetWorkspaceId,
                ["canPush"] = provider.CanPush,
                ["canViewTasks"] = provider.CanViewTasks
            }.ToString(Formatting.None);
        }

        // ── molca_clickup_list_tasks (read) ──────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpListTasksTool() => new McpToolDefinition(
            name: "molca_clickup_list_tasks",
            description: "Lists the ClickUp tasks scoped to the configured Target Folder Id (the same view as "
                       + "Hub → Tasks). Defaults to the token user's open tasks. Returns each task's id, name, "
                       + "url, current status, and list, plus the folder's available status names. Set "
                       + "'onlyMine' to false for everyone's tasks, 'includeClosed' to true to include done "
                       + "tasks. Requires a stored token and a Target Folder Id. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"onlyMine\":{\"type\":\"boolean\",\"description\":\"Limit to the token user's tasks (default true).\"}," +
                "\"includeClosed\":{\"type\":\"boolean\",\"description\":\"Include tasks in a closed/done status (default false).\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteClickUpListTasks,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteClickUpListTasks(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveClickUpProvider(out string error);
            if (provider == null) return ClickUpError(error);

            bool onlyMine = args.Value<bool?>("onlyMine") ?? true;
            bool includeClosed = args.Value<bool?>("includeClosed") ?? false;

            var result = await provider.FetchTasksAsync(onlyMine, includeClosed, CancellationToken.None);
            if (!result.Success)
                return ClickUpError(result.Error);

            var tasks = new JArray();
            foreach (var task in result.Tasks)
            {
                tasks.Add(new JObject
                {
                    ["id"] = task.id,
                    ["name"] = task.name,
                    ["url"] = task.url,
                    ["status"] = task.status?.status,
                    ["list"] = task.list?.name
                });
            }

            var statuses = new JArray();
            foreach (var status in result.Statuses)
                statuses.Add(status);

            return new JObject
            {
                ["onlyMine"] = onlyMine,
                ["includeClosed"] = includeClosed,
                ["count"] = result.Tasks.Length,
                ["tasks"] = tasks,
                ["statuses"] = statuses
            }.ToString(Formatting.None);
        }

        // ── molca_clickup_list_workspaces (read) ─────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpListWorkspacesTool() => new McpToolDefinition(
            name: "molca_clickup_list_workspaces",
            description: "Lists the ClickUp workspaces ('teams') the stored token can access: id and name. Use a "
                       + "workspace id to set Target Workspace Id on the integration. Requires a stored token. "
                       + "Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            executeAsync: ExecuteClickUpListWorkspaces,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteClickUpListWorkspaces(string argumentsJson)
        {
            var provider = ResolveClickUpProvider(out string error);
            if (provider == null) return ClickUpError(error);
            if (!provider.HasToken)
                return ClickUpError("ClickUp is not connected — add a token in Hub → Integrations.");

            var workspaces = await provider.FetchWorkspacesAsync(CancellationToken.None);
            var arr = new JArray();
            foreach (var workspace in workspaces)
                arr.Add(new JObject { ["id"] = workspace.Id, ["name"] = workspace.Name });

            return new JObject { ["count"] = arr.Count, ["workspaces"] = arr }.ToString(Formatting.None);
        }

        // ── molca_clickup_set_task_status (action) ───────────────────────────────────────────

        private static McpToolDefinition CreateClickUpSetTaskStatusTool() => new McpToolDefinition(
            name: "molca_clickup_set_task_status",
            description: "Changes a ClickUp task's status. 'taskId' and 'status' are required; the status name "
                       + "must exist in the task's status set (use molca_clickup_list_tasks to discover the valid "
                       + "names). This writes to ClickUp and cannot be undone from Unity.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"taskId\":{\"type\":\"string\",\"description\":\"The ClickUp task id to update.\"}," +
                "\"status\":{\"type\":\"string\",\"description\":\"The destination status name.\"}}," +
                "\"required\":[\"taskId\",\"status\"],\"additionalProperties\":false}",
            executeAsync: ExecuteClickUpSetTaskStatus,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteClickUpSetTaskStatus(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveClickUpProvider(out string error);
            if (provider == null) return ClickUpError(error);
            if (!provider.HasToken)
                return ClickUpError("ClickUp is not connected — add a token in Hub → Integrations.");

            string taskId = args.Value<string>("taskId");
            string status = args.Value<string>("status");
            if (string.IsNullOrWhiteSpace(taskId)) return ClickUpError("'taskId' is required.");
            if (string.IsNullOrWhiteSpace(status)) return ClickUpError("'status' is required.");

            bool ok = await provider.SetTaskStatusAsync(taskId, status, CancellationToken.None);
            return ok
                ? new JObject { ["success"] = true, ["taskId"] = taskId, ["status"] = status }.ToString(Formatting.None)
                : ClickUpError($"Failed to change status of task '{taskId}' to '{status}' — check the id and that "
                             + "the status exists in the task's set.");
        }

        // ── molca_clickup_create_task (action) ───────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpCreateTaskTool() => new McpToolDefinition(
            name: "molca_clickup_create_task",
            description: "Creates a ClickUp task. 'name' is required; 'markdownDescription' is an optional "
                       + "Markdown body. By default the task is created in the configured Target List Id; pass "
                       + "'listId' to override it. This writes to ClickUp and cannot be undone from Unity.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"The task title.\"}," +
                "\"markdownDescription\":{\"type\":\"string\",\"description\":\"Optional Markdown task body.\"}," +
                "\"listId\":{\"type\":\"string\",\"description\":\"Override the configured Target List Id.\"}}," +
                "\"required\":[\"name\"],\"additionalProperties\":false}",
            executeAsync: ExecuteClickUpCreateTask,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteClickUpCreateTask(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveClickUpProvider(out string error);
            if (provider == null) return ClickUpError(error);

            var client = provider.CreateClient();
            if (client == null)
                return ClickUpError("ClickUp is not connected — add a token in Hub → Integrations.");

            string name = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return ClickUpError("'name' is required.");

            string listId = args.Value<string>("listId");
            if (string.IsNullOrWhiteSpace(listId)) listId = provider.TargetListId;
            if (string.IsNullOrWhiteSpace(listId))
                return ClickUpError("No list id — pass 'listId' or set a Target List Id on the ClickUp integration.");

            string markdown = args.Value<string>("markdownDescription");

            var result = await client.CreateTaskAsync(listId, name, markdown, CancellationToken.None);
            if (!result.Success)
                return ClickUpError($"Create failed ({result.StatusCode}): {result.Error}");

            return new JObject
            {
                ["success"] = true,
                ["listId"] = listId,
                ["taskId"] = result.Id
            }.ToString(Formatting.None);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        // Resolves the single ClickUp provider from IntegrationSettings; sets error (and returns null) when
        // none is registered. The token/connection state is checked per-tool, so a registered-but-unconfigured
        // provider still resolves (so the status tool can report it).
        private static ClickUpIntegrationProvider ResolveClickUpProvider(out string error)
        {
            error = null;
            var settings = IntegrationSettings.FindSettings();
            var provider = settings != null ? settings.GetProvider<ClickUpIntegrationProvider>() : null;
            if (provider == null)
                error = "No ClickUp integration is registered. Add one in Hub → Integrations (+ Add integration).";
            return provider;
        }

        private static string ClickUpError(string message)
            => new JObject { ["error"] = message ?? "Unknown error." }.ToString(Formatting.None);
    }
}
