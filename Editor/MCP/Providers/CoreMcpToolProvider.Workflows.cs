using System;
using System.Linq;
using Molca.Editor.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Composed-workflow tools (Sprint 93.4): let the assistant validate a data-driven workflow, save it,
    /// run it fire-and-poll through the automation kernel, and poll a run's status. Composition is always
    /// validated by <see cref="MolcaComposedWorkflowCompiler"/> against the live command registry, and a
    /// run goes through <see cref="MolcaAutomationKernel.InvokeAsync"/> — the same policy, mode, resource,
    /// confirmation, and audit path as every other command. There is no bypass.
    /// </summary>
    /// <remarks>
    /// Run/status follow the kernel's fire-and-poll model (the Pipeline request path caps at ~30s; an
    /// in-editor assistant turn should not hold an await across a multi-minute workflow either). A reload
    /// mid-run reconciles the run to <c>Interrupted</c> in the journal — status reports that truthfully
    /// (§12; Sprint 93.6). In headless batch mode a detached <c>Awaitable</c> never pumps, so
    /// <c>molca_workflow_run</c> refuses there and points at the Pipeline adapter instead.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        private const string WorkflowObjectSchema =
            "\"workflow\":{\"type\":\"object\",\"description\":\"The composed workflow: {id, displayName?, " +
            "description?, failOnWarning?, steps:[{commandId, args?, critical?, id?}]}. Step commandIds must " +
            "be registered automation commands (see molca_workflow_commands).\"}";

        // ── molca_workflow_commands (read) ───────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowCommandsTool() => new McpToolDefinition(
            name: "molca_workflow_commands",
            description: "Lists the automation commands available as composed-workflow steps: id, display "
                       + "name, category, kind (ReadOnly/Action), mode, reversibility, whether confirmation "
                       + "is required, and the arguments JSON schema. Read-only. Use these ids in "
                       + "molca_workflow_validate / molca_workflow_save steps.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"category\":{\"type\":\"string\",\"description\":\"Optional: only commands in this category.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteWorkflowCommands,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteWorkflowCommands(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var category = args.Value<string>("category");
            var rows = new JArray();
            foreach (var command in MolcaAutomationKernel.Instance.Capabilities())
            {
                if (!string.IsNullOrWhiteSpace(category)
                    && !string.Equals(command.Category, category, StringComparison.OrdinalIgnoreCase))
                    continue;
                JToken schema;
                try { schema = JToken.Parse(command.InputSchemaJson); }
                catch (JsonException) { schema = new JObject(); }
                rows.Add(new JObject
                {
                    ["id"] = command.Id,
                    ["displayName"] = command.DisplayName,
                    ["category"] = command.Category,
                    ["kind"] = command.Kind.ToString(),
                    ["mode"] = command.Mode.ToString(),
                    ["reversibility"] = command.Reversibility.ToString(),
                    ["requiresConfirmation"] = command.RequiresConfirmation,
                    ["inputSchema"] = schema
                });
            }
            return new JObject { ["count"] = rows.Count, ["commands"] = rows }.ToString(Formatting.None);
        }

        // ── molca_workflow_validate (read) ───────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowValidateTool() => new McpToolDefinition(
            name: "molca_workflow_validate",
            description: "Validates a composed workflow against the live automation registry WITHOUT running "
                       + "anything: unknown command ids, argument-schema violations, duplicate step ids, "
                       + "self-reference, and Edit/Play mode conflicts, plus the kernel-aggregated policy "
                       + "facets (kind, mode, reversibility, confirmation, resource claims). Always validate "
                       + "before proposing a run. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{" + WorkflowObjectSchema + "}," +
                             "\"required\":[\"workflow\"],\"additionalProperties\":false}",
            execute: ExecuteWorkflowValidate,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteWorkflowValidate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var workflow = MolcaComposedWorkflow.FromJson(args["workflow"] as JObject);
            var validation = MolcaComposedWorkflowCompiler.Validate(workflow, MolcaAutomationKernel.Instance.Registry);
            return validation.ToJson().ToString(Formatting.None);
        }

        // ── molca_workflow_save (action) ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowSaveTool() => new McpToolDefinition(
            name: "molca_workflow_save",
            description: "Validates and saves a composed workflow as JSON under "
                       + MolcaComposedWorkflowStore.RelativeRoot + " and registers it as an automation "
                       + "command (id = workflow id), visible in Hub Automation, CLI, and MCP. Refuses an "
                       + "invalid composition — fix the reported issues first. Revert by "
                       + "molca_workflow_delete.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{" + WorkflowObjectSchema + "}," +
                             "\"required\":[\"workflow\"],\"additionalProperties\":false}",
            execute: ExecuteWorkflowSave,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteWorkflowSave(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var workflow = MolcaComposedWorkflow.FromJson(args["workflow"] as JObject);
            var kernel = MolcaAutomationKernel.Instance;
            var validation = MolcaComposedWorkflowCompiler.Validate(workflow, kernel.Registry);
            if (!validation.IsValid)
                return new JObject { ["saved"] = false, ["validation"] = validation.ToJson() }.ToString(Formatting.None);

            if (!MolcaComposedWorkflowStore.Save(workflow, validation.Facets, out var error))
                return new JObject { ["saved"] = false, ["error"] = error }.ToString(Formatting.None);

            AssetDatabase.Refresh();
            kernel.Rebuild(); // the saved workflow is a command now

            // Saving does not authorize. An Action workflow stays refused until its command id is on the
            // automation action allowlist, so report that here rather than letting the run fail opaquely.
            // Deliberately not auto-allowlisted: that would let the model widen its own permissions.
            var isAction = validation.Facets.Kind == MolcaCommandKind.Action;
            var allowlisted = !isAction
                || (kernel.Policy is MolcaAutomationPolicy p && p.IsAllowlisted(workflow.Id));
            var result = new JObject
            {
                ["saved"] = true,
                ["commandId"] = workflow.Id,
                ["path"] = MolcaComposedWorkflowStore.RelativeRoot + "/" + workflow.Id + ".json",
                ["facets"] = validation.Facets.ToJson(),
                ["authorizedToRun"] = allowlisted
            };
            if (!allowlisted)
                result["authorizationNote"] =
                    $"'{workflow.Id}' is an action workflow and is NOT in the automation action allowlist, so "
                    + "molca_workflow_run will be Refused under every profile (the CI profile's allowlist is "
                    + "exact, so raising the profile does not help). The user must authorize it — from the "
                    + "workflow panel in the assistant canvas, or in Hub → Automation → Permissions. Tell them "
                    + "this instead of retrying the run.";
            return result.ToString(Formatting.None);
        }

        // ── molca_workflow_delete (action) ───────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowDeleteTool() => new McpToolDefinition(
            name: "molca_workflow_delete",
            description: "Deletes a saved composed workflow by id (removes its JSON under "
                       + MolcaComposedWorkflowStore.RelativeRoot + " and unregisters its command).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"id\":{\"type\":\"string\",\"description\":\"The saved workflow id.\"}}," +
                "\"required\":[\"id\"],\"additionalProperties\":false}",
            execute: ExecuteWorkflowDelete,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteWorkflowDelete(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var id = args.Value<string>("id");
            // The store drops any allowlist entry with the file — that invariant lives there, not here, so
            // every caller gets it and it can be tested without going through an MCP tool.
            var deleted = MolcaComposedWorkflowStore.Delete(id, out var authorizationRemoved);
            if (deleted)
            {
                AssetDatabase.Refresh();
                MolcaAutomationKernel.Instance.Rebuild();
            }
            return new JObject
            {
                ["deleted"] = deleted,
                ["id"] = id,
                ["authorizationRemoved"] = authorizationRemoved
            }.ToString(Formatting.None);
        }

        // ── molca_workflow_list (read) ───────────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowListTool() => new McpToolDefinition(
            name: "molca_workflow_list",
            description: "Lists the saved composed workflows: id, display name, step count, save time, and "
                       + "whether each still validates against the current registry. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteWorkflowList,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteWorkflowList(string argumentsJson)
        {
            var registry = MolcaAutomationKernel.Instance.Registry;
            var rows = new JArray();
            foreach (var entry in MolcaComposedWorkflowStore.List())
            {
                var validation = MolcaComposedWorkflowCompiler.Validate(entry.Workflow, registry);
                rows.Add(new JObject
                {
                    ["id"] = entry.Workflow.Id,
                    ["displayName"] = entry.Workflow.DisplayName,
                    ["stepCount"] = entry.Workflow.Steps.Count,
                    ["savedAtUtc"] = entry.SavedAtUtc == DateTime.MinValue ? null : entry.SavedAtUtc.ToString("o"),
                    ["valid"] = validation.IsValid
                });
            }
            return new JObject { ["count"] = rows.Count, ["workflows"] = rows }.ToString(Formatting.None);
        }

        // ── molca_workflow_run (action, fire-and-poll) ───────────────────────────────────────

        private static McpToolDefinition CreateWorkflowRunTool() => new McpToolDefinition(
            name: "molca_workflow_run",
            description: "Starts a SAVED composed workflow through the automation kernel (policy, mode, "
                       + "resources, confirmation, and audit all apply) and returns a runId immediately — "
                       + "poll molca_workflow_status for progress and the result. Save the workflow first "
                       + "with molca_workflow_save. The kernel may refuse by policy; that is reported in the "
                       + "final status, not silently overridden.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"id\":{\"type\":\"string\",\"description\":\"The saved workflow id to run.\"}}," +
                "\"required\":[\"id\"],\"additionalProperties\":false}",
            execute: ExecuteWorkflowRun,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteWorkflowRun(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var id = args.Value<string>("id");
            var kernel = MolcaAutomationKernel.Instance;

            if (!kernel.TryGetCommand(id, out _))
                return new JObject
                {
                    ["started"] = false,
                    ["error"] = $"No command '{id}' is registered. Save the workflow first (molca_workflow_save)."
                }.ToString(Formatting.None);

            // Refuse up front when the policy cannot possibly authorize it: starting a detached run only to
            // have it come back Refused hides the actionable part (who must authorize it, and how).
            var kernelPolicy = kernel.Policy as MolcaAutomationPolicy;
            if (kernel.TryGetCommand(id, out var definition)
                && definition.Kind == MolcaCommandKind.Action
                && kernelPolicy != null && !kernelPolicy.IsAllowlisted(id))
                return new JObject
                {
                    ["started"] = false,
                    ["error"] = $"'{id}' is an action and is not in the automation action allowlist, so the run "
                              + $"would be Refused (active profile: {kernelPolicy.Profile}; the CI profile's "
                              + "allowlist is exact, so raising the profile does not help). Ask the user to "
                              + "authorize it from the workflow panel in the canvas or Hub → Settings → "
                              + "Automation; do not retry until they have."
                }.ToString(Formatting.None);

            // A detached Awaitable never pumps in headless batch mode (the editor loop is not running the
            // player loop the await resumes on), so fire-and-poll cannot work there.
            if (Application.isBatchMode)
                return new JObject
                {
                    ["started"] = false,
                    ["error"] = "molca_workflow_run is fire-and-poll and cannot start detached work in batch mode; drive the workflow over the Pipeline adapter instead."
                }.ToString(Formatting.None);

            var runId = Guid.NewGuid().ToString();
            RunDetachedAsync(kernel, id, runId);
            return new JObject
            {
                ["started"] = true,
                ["runId"] = runId,
                ["commandId"] = id,
                ["poll"] = "molca_workflow_status"
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Fire-and-forget wrapper for the detached run. The kernel records the run in its store/journal
        /// under <paramref name="runId"/>, so pollers observe it; this method owns its exceptions
        /// (async-contract rule 5) — a failure is reflected in the run result, and anything escaping that
        /// is logged rather than lost in an unobserved awaitable.
        /// </summary>
        private static async void RunDetachedAsync(MolcaAutomationKernel kernel, string commandId, string runId)
        {
            try
            {
                // MCP-side confirmation for this Action tool has already been granted by the caller's
                // confirmation flow, so it is forwarded; kernel policy still authorizes independently.
                await kernel.InvokeAsync(commandId, new JObject(), MolcaTransport.Mcp,
                    isConfirmed: true, runId: runId);
            }
            catch (OperationCanceledException) { /* cancelled via molca_workflow_status/cancel — not an error */ }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        // ── molca_workflow_status (read) ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateWorkflowStatusTool() => new McpToolDefinition(
            name: "molca_workflow_status",
            description: "Polls a workflow run by runId: status (Queued/Running/Succeeded/Failed/Cancelled/"
                       + "Refused/NeedsConfirmation/Blocked/Interrupted), latest progress, and the final "
                       + "result envelope once terminal. Interrupted means the editor reloaded mid-run — "
                       + "the run did not complete and was not resumed. Optional cancel=true requests "
                       + "cancellation. Read-only apart from the explicit cancel request.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"runId\":{\"type\":\"string\",\"description\":\"The run id returned by molca_workflow_run.\"}," +
                "\"cancel\":{\"type\":\"boolean\",\"description\":\"Request cancellation of the run.\"}}," +
                "\"required\":[\"runId\"],\"additionalProperties\":false}",
            execute: ExecuteWorkflowStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteWorkflowStatus(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var runId = args.Value<string>("runId");
            var kernel = MolcaAutomationKernel.Instance;

            var cancelRequested = args.Value<bool?>("cancel") == true;
            var cancelled = cancelRequested && kernel.Cancel(runId);

            if (!kernel.TryGetRun(runId, out var handle) || handle == null)
                return new JObject { ["found"] = false, ["runId"] = runId }.ToString(Formatting.None);

            var json = new JObject
            {
                ["found"] = true,
                ["runId"] = handle.RunId,
                ["commandId"] = handle.CommandId,
                ["status"] = handle.Status.ToString(),
                ["isTerminal"] = handle.IsTerminal
            };
            if (cancelRequested) json["cancelRequested"] = cancelled;
            if (handle.Progress is { } progress)
                json["progress"] = new JObject
                {
                    ["message"] = progress.Message,
                    ["fraction"] = progress.IsIndeterminate ? null : (JToken)progress.Fraction,
                    ["stepIndex"] = progress.StepIndex,
                    ["stepCount"] = progress.StepCount,
                    ["stepName"] = progress.StepName
                };
            if (handle.Result != null)
                json["result"] = handle.Result.ToJson();
            return json.ToString(Formatting.None);
        }
    }
}
