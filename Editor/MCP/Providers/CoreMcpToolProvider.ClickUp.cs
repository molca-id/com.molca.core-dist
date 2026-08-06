using System.Threading;
using Molca.Settings.Integration;
using Molca.Settings.Integration.ClickUp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// ClickUp tools: read the integration's connection/target state, list the project folder's tasks, list
    /// accessible workspaces, read and set the focused task, change a task's status, and create a task. Reads
    /// are <see cref="McpToolKind.ReadOnly"/>; the mutating tools are <see cref="McpToolKind.Action"/>, gated by
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
    /// <para>
    /// <b>Why focus is exposed to MCP.</b> The focused task (<see cref="ClickUpTaskFocus"/>) is the project's own
    /// statement of what is being worked on right now. Reading it lets an agent scope its work to the actual
    /// ticket instead of asking, and it is also what build/release activity comments on — so an agent changing it
    /// is changing where automated reports land.
    /// </para>
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_clickup_status (read) ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpStatusTool() => new McpToolDefinition(
            name: "molca_clickup_status",
            description: "Reads the ClickUp integration state: whether a token is stored, whether it has been "
                       + "verified this session, the status message, the target list/folder/workspace ids, the "
                       + "push target and whether it has a destination, the focused task, and the "
                       + "canPush/canViewTasks readiness flags. The token itself is never returned. Read-only.",
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
                ["connectedEmail"] = provider.ConnectedEmail,
                ["targetListId"] = provider.TargetListId,
                ["targetFolderId"] = provider.TargetFolderId,
                ["targetWorkspaceId"] = provider.TargetWorkspaceId,
                ["pushOnBuild"] = provider.PushOnBuild,
                ["pushOnRelease"] = provider.PushOnRelease,
                ["pushTarget"] = provider.PushTarget.ToString(),
                ["hasPushDestination"] = provider.HasPushDestination,
                ["focusedTaskId"] = ClickUpTaskFocus.FocusedTaskId,
                ["focusedTaskName"] = ClickUpTaskFocus.FocusedTaskName,
                ["pinnedCount"] = ClickUpTaskFocus.PinnedCount,
                ["canPush"] = provider.CanPush,
                ["canViewTasks"] = provider.CanViewTasks
            }.ToString(Formatting.None);
        }

        // ── molca_clickup_focus (read) ───────────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpFocusTool() => new McpToolDefinition(
            name: "molca_clickup_focus",
            description: "Reads the ClickUp task currently focused for this project — the task the developer is "
                       + "working on, and the one build/release activity comments on when the push target is a "
                       + "comment mode. Returns its id, name, and url, plus the pinned task ids. Focus is stored "
                       + "per-machine and is not committed. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteClickUpFocus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteClickUpFocus(string argumentsJson)
        {
            var pinned = new JArray();
            foreach (var id in ClickUpTaskFocus.GetPinnedIds())
                pinned.Add(id);

            return new JObject
            {
                ["hasFocus"] = ClickUpTaskFocus.HasFocus,
                ["taskId"] = ClickUpTaskFocus.FocusedTaskId,
                ["taskName"] = ClickUpTaskFocus.FocusedTaskName,
                ["taskUrl"] = ClickUpTaskFocus.FocusedTaskUrl,
                ["pinnedTaskIds"] = pinned
            }.ToString(Formatting.None);
        }

        // ── molca_clickup_set_focus (action) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpSetFocusTool() => new McpToolDefinition(
            name: "molca_clickup_set_focus",
            description: "Sets or clears the focused ClickUp task for this project. Pass 'taskId' (with optional "
                       + "'taskName' and 'taskUrl' for labelling) to focus a task, or 'clear': true to unfocus. "
                       + "This changes where build/release activity is reported when the push target is a comment "
                       + "mode. It only writes local editor state (per-machine, not committed) and can be changed "
                       + "back by calling this tool again, but it is not on Unity's undo stack.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"taskId\":{\"type\":\"string\",\"description\":\"The ClickUp task id to focus.\"}," +
                "\"taskName\":{\"type\":\"string\",\"description\":\"Optional display name for the focused task.\"}," +
                "\"taskUrl\":{\"type\":\"string\",\"description\":\"Optional ClickUp URL for the focused task.\"}," +
                "\"clear\":{\"type\":\"boolean\",\"description\":\"Clear the focus instead of setting it.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteClickUpSetFocus,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteClickUpSetFocus(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            if (args.Value<bool?>("clear") == true)
            {
                ClickUpTaskFocus.ClearFocus();
                return new JObject { ["success"] = true, ["hasFocus"] = false }.ToString(Formatting.None);
            }

            string taskId = args.Value<string>("taskId");
            if (string.IsNullOrWhiteSpace(taskId))
                return ClickUpError("'taskId' is required unless 'clear' is true.");

            ClickUpTaskFocus.SetFocus(
                taskId.Trim(), args.Value<string>("taskName"), args.Value<string>("taskUrl"));

            return new JObject
            {
                ["success"] = true,
                ["hasFocus"] = true,
                ["taskId"] = ClickUpTaskFocus.FocusedTaskId,
                ["taskName"] = ClickUpTaskFocus.FocusedTaskName
            }.ToString(Formatting.None);
        }

        // ── molca_clickup_list_tasks (read) ──────────────────────────────────────────────────

        private static McpToolDefinition CreateClickUpListTasksTool() => new McpToolDefinition(
            name: "molca_clickup_list_tasks",
            description: "Lists the ClickUp tasks scoped to the configured Target Folder Id (the same view as "
                       + "Hub → Tasks), following pagination so folders with more than 100 tasks are complete. "
                       + "Defaults to the token user's open tasks. Returns each task's id, name, url, status, "
                       + "list, priority, due date (ISO-8601), tags, assignees, and whether it is pinned or "
                       + "focused, plus the folder's available status names in workflow order. Set 'onlyMine' to "
                       + "false for everyone's tasks, 'includeClosed' to true to include done tasks. Requires a "
                       + "stored token and a Target Folder Id. Read-only.",
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
                tasks.Add(DescribeTask(task));

            // Names only: the wire contract for this tool is the ordered list of status names an agent may pass
            // back to molca_clickup_set_task_status. Colors are presentational and would only be noise here.
            var statuses = new JArray();
            foreach (var status in result.Statuses)
            {
                if (!string.IsNullOrEmpty(status?.status)) statuses.Add(status.status);
            }

            return new JObject
            {
                ["onlyMine"] = onlyMine,
                ["includeClosed"] = includeClosed,
                ["folderName"] = result.FolderName,
                ["count"] = result.Tasks.Length,
                ["tasks"] = tasks,
                ["statuses"] = statuses
            }.ToString(Formatting.None);
        }

        // One task as JSON. Dates are emitted as ISO-8601 rather than ClickUp's epoch-millisecond strings so a
        // consumer can compare them without knowing the wire quirk.
        private static JObject DescribeTask(ClickUpModels.ClickUpTask task)
        {
            var tags = new JArray();
            if (task.tags != null)
            {
                foreach (var tag in task.tags)
                {
                    if (!string.IsNullOrEmpty(tag?.name)) tags.Add(tag.name);
                }
            }

            var assignees = new JArray();
            if (task.assignees != null)
            {
                foreach (var user in task.assignees)
                {
                    if (user == null) continue;
                    assignees.Add(new JObject
                    {
                        ["id"] = user.id,
                        ["username"] = user.username,
                        ["email"] = user.email
                    });
                }
            }

            return new JObject
            {
                ["id"] = task.id,
                ["name"] = task.name,
                ["url"] = task.url,
                ["status"] = task.status?.status,
                ["list"] = task.list?.name,
                ["priority"] = task.priority?.priority,
                ["dueDate"] = ClickUpTaskFormat.ToIso8601(task.due_date),
                ["updated"] = ClickUpTaskFormat.ToIso8601(task.date_updated),
                ["tags"] = tags,
                ["assignees"] = assignees,
                ["pinned"] = ClickUpTaskFocus.IsPinned(task.id),
                ["focused"] = ClickUpTaskFocus.IsFocused(task.id)
            };
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

            var result = await provider.FetchWorkspacesAsync(CancellationToken.None);
            if (!result.Success)
                return ClickUpError(result.Error);

            var arr = new JArray();
            foreach (var workspace in result.Workspaces)
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

            var result = await provider.SetTaskStatusAsync(taskId, status, CancellationToken.None);
            return result.Success
                ? new JObject { ["success"] = true, ["taskId"] = taskId, ["status"] = status }.ToString(Formatting.None)
                : ClickUpError($"Failed to change status of task '{taskId}' to '{status}': {result.Error}");
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
            if (!provider.HasToken)
                return ClickUpError("ClickUp is not connected — add a token in Hub → Integrations.");

            string name = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return ClickUpError("'name' is required.");

            var result = await provider.CreateTaskAsync(
                name, args.Value<string>("markdownDescription"), args.Value<string>("listId"),
                CancellationToken.None);

            if (!result.Success)
                return ClickUpError($"Create failed ({result.StatusCode}): {result.Error}");

            return new JObject
            {
                ["success"] = true,
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
