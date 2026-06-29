using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Bridges the shared <see cref="McpToolRegistry"/> to the LLM's function-calling surface
    /// (Sprint 16.4): exposes registry tools as <see cref="LlmToolSpec"/>s and executes the model's
    /// tool calls in-process against the registry — the same capability layer the IDE proxy uses, one
    /// registration, every front-end.
    /// </summary>
    /// <remarks>
    /// Read-only tools are always offered. <see cref="McpToolKind.Action"/> tools are offered only when
    /// on the action allowlist, and executing one prompts the user for confirmation via a modal dialog
    /// (Sprint 17.2) — the in-editor equivalent of the bridge's confirmation-token round-trip. Tool
    /// execution runs on the Unity main thread (the chat controller drives the loop there), so tool
    /// delegates and the dialog may touch editor APIs directly.
    /// </remarks>
    public static class AssistantToolBridge
    {
        /// <summary>
        /// Maps registry tools to provider-neutral specs for the model: all read-only tools, plus any
        /// Action tools permitted by <paramref name="isActionAllowed"/>.
        /// </summary>
        public static List<LlmToolSpec> GetToolSpecs(McpToolRegistry registry, Func<string, bool> isActionAllowed = null)
        {
            var specs = new List<LlmToolSpec>();
            if (registry == null) return specs;
            foreach (var tool in registry.Tools)
            {
                if (tool.Kind == McpToolKind.Action && !(isActionAllowed?.Invoke(tool.Name) ?? false))
                    continue; // action not allowlisted — don't even offer it
                specs.Add(new LlmToolSpec(tool.Name, tool.Description, tool.InputSchemaJson));
            }
            return specs;
        }

        /// <summary>
        /// Rough token estimate (~4 chars/token) of a tool-spec payload as serialized to the model — the
        /// sum of every spec's name + description + input schema. Used as the Sprint-67 baseline metric for
        /// the per-request tool-spec cost the optimization drives down.
        /// </summary>
        /// <param name="specs">The tool specs that would be sent in a request's <c>tools</c> array.</param>
        /// <returns>Estimated token count of the whole payload.</returns>
        public static int EstimateSpecTokens(IReadOnlyList<LlmToolSpec> specs)
        {
            if (specs == null) return 0;
            var chars = 0;
            foreach (var s in specs)
            {
                if (s == null) continue;
                chars += (s.Name?.Length ?? 0) + (s.Description?.Length ?? 0) + (s.InputSchemaJson?.Length ?? 0);
            }
            return chars / 4;
        }

        /// <summary>The meta-tool that fetches full schemas on demand for tiered exposure (Sprint 67).</summary>
        public const string ToolSchemaToolName = "molca_tool_schema";

        /// <summary>
        /// Builds the tiered tool-spec payload for a request (Sprint 67): only <see cref="ToolSchemaToolName"/>
        /// plus the schemas for tools the model has already "activated" this turn (via a
        /// <c>molca_tool_schema</c> call). The full catalog of available tools is given as text by
        /// <see cref="BuildToolCatalog"/>; this keeps the per-request schema payload tiny.
        /// </summary>
        /// <param name="registry">The tool registry.</param>
        /// <param name="isActionAllowed">Allowlist predicate for Action tools.</param>
        /// <param name="activated">Names the model has fetched schemas for (and may now call).</param>
        public static List<LlmToolSpec> GetTieredToolSpecs(
            McpToolRegistry registry, Func<string, bool> isActionAllowed, ISet<string> activated)
        {
            var specs = new List<LlmToolSpec>();
            if (registry == null) return specs;
            foreach (var tool in registry.Tools)
            {
                var isMeta = tool.Name == ToolSchemaToolName || tool.Name == ListToolsToolName;
                if (!isMeta && (activated == null || !activated.Contains(tool.Name)))
                    continue; // not a meta-tool and not yet activated → don't send its schema
                if (tool.Kind == McpToolKind.Action && !(isActionAllowed?.Invoke(tool.Name) ?? false))
                    continue;
                specs.Add(new LlmToolSpec(tool.Name, tool.Description, tool.InputSchemaJson));
            }
            return specs;
        }

        /// <summary>
        /// Builds the compact tool catalog injected into the system prompt (Sprint 67.3): one line per
        /// family — <c>[family] (N): name1, name2, …</c> — listing tool <b>names</b> grouped by family but
        /// <b>not</b> their per-tool summaries (the expensive part). The model picks a tool by name and
        /// fetches its parameters with <c>molca_tool_schema</c>; for a family it needs to understand better,
        /// <c>molca_list_tools(family)</c> returns that family's names + summaries on demand. This keeps the
        /// always-sent catalog small while every tool stays discoverable.
        /// </summary>
        /// <param name="registry">The tool registry.</param>
        /// <param name="isActionAllowed">Allowlist predicate; non-allowlisted Action tools are omitted.</param>
        public static string BuildToolCatalog(McpToolRegistry registry, Func<string, bool> isActionAllowed)
        {
            if (registry == null) return string.Empty;

            var byFamily = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var tool in registry.Tools)
            {
                if (tool.Name == ToolSchemaToolName || tool.Name == ListToolsToolName) continue; // meta-tools always offered
                if (tool.Kind == McpToolKind.Action && !(isActionAllowed?.Invoke(tool.Name) ?? false))
                    continue;

                var family = FamilyOf(tool.Name);
                if (!byFamily.TryGetValue(family, out var list)) byFamily[family] = list = new List<string>();
                list.Add(tool.Kind == McpToolKind.Action ? tool.Name + "*" : tool.Name); // '*' marks an action
            }

            var sb = new StringBuilder();
            foreach (var kv in byFamily)
            {
                kv.Value.Sort(StringComparer.Ordinal);
                sb.Append('[').Append(kv.Key).Append("] (").Append(kv.Value.Count).Append("): ")
                  .Append(string.Join(", ", kv.Value)).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The meta-tool that expands a family's tools (name + summary) on demand (Sprint 67.3).</summary>
        public const string ListToolsToolName = "molca_list_tools";

        /// <summary>
        /// Returns the tools in one family as <c>name — summary</c> lines (the detail dropped from the
        /// compact catalog). Backs <c>molca_list_tools(family)</c>.
        /// </summary>
        /// <param name="registry">The tool registry.</param>
        /// <param name="family">The family key as shown in the catalog (e.g. <c>content</c>, <c>unity/addressable</c>).</param>
        /// <param name="isActionAllowed">Allowlist predicate; non-allowlisted Action tools are omitted.</param>
        public static string BuildFamilyListing(McpToolRegistry registry, string family, Func<string, bool> isActionAllowed)
        {
            if (registry == null || string.IsNullOrWhiteSpace(family)) return string.Empty;

            var lines = new List<string>();
            foreach (var tool in registry.Tools)
            {
                if (tool.Kind == McpToolKind.Action && !(isActionAllowed?.Invoke(tool.Name) ?? false)) continue;
                if (!string.Equals(FamilyOf(tool.Name), family, StringComparison.Ordinal)) continue;
                var kindTag = tool.Kind == McpToolKind.Action ? " [action]" : string.Empty;
                lines.Add($"  {tool.Name}{kindTag} — {FirstSentence(tool.Description)}");
            }
            lines.Sort(StringComparer.Ordinal);
            return lines.Count == 0
                ? $"No tools in family '{family}'."
                : $"[{family}]\n" + string.Join("\n", lines);
        }

        /// <summary>Family key for grouping: the segment after a <c>molca_</c>/<c>molca_unity_</c> prefix.</summary>
        private static string FamilyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "misc";
            var n = name.StartsWith("molca_unity_", StringComparison.Ordinal) ? name.Substring("molca_unity_".Length)
                  : name.StartsWith("molca_", StringComparison.Ordinal) ? name.Substring("molca_".Length)
                  : name;
            var prefix = name.StartsWith("molca_unity_", StringComparison.Ordinal) ? "unity/" : string.Empty;
            var us = n.IndexOf('_');
            return prefix + (us > 0 ? n.Substring(0, us) : n);
        }

        /// <summary>First sentence of a description, capped, for the catalog line.</summary>
        private static string FirstSentence(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;
            var text = description.Replace('\n', ' ').Trim();
            var dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0) text = text.Substring(0, dot);
            const int max = 140;
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// Executes one model tool call against the registry, applying mode-gating and — for Action
        /// tools — the allowlist plus a user-confirmation step. Errors and refusals are returned
        /// as error results (not thrown) so the conversation can continue.
        /// </summary>
        /// <param name="confirmAction">
        /// Decides whether an allowlisted Action tool may run, given the tool and its raw arguments.
        /// When <c>null</c>, a blocking modal confirmation dialog is shown (the default, Ask-mode
        /// behavior). Pass a delegate that always returns <c>true</c> for Auto mode, or one that shows a
        /// custom prompt. Read-only tools never reach this callback.
        /// </param>
        /// <param name="askUser">
        /// Surfaces a <see cref="AssistantUserPrompt"/> to the user and resolves with their answer, used to
        /// pause the turn for the interactive <c>molca_ask_user</c> tool (Sprint 25.6). When <c>null</c>
        /// (no interactive front-end) the tool falls through to its fallback delegate, which returns an
        /// error explaining no UI is available.
        /// </param>
        /// <param name="confirmActionAsync">
        /// Async action confirmation that resolves true to run, false to cancel (Sprint 25 follow-up).
        /// Preferred over <paramref name="confirmAction"/> and the modal dialog when set, so Ask-mode
        /// confirmations can flow through the in-chat docked prompt bar instead of a blocking dialog.
        /// </param>
        /// <param name="onProgress">
        /// Receives <see cref="McpProgressReport"/>s emitted by the tool (via <see cref="McpProgress"/>)
        /// while it runs, so the front-end can show live progress for long-running tools (build/deploy).
        /// When <c>null</c>, no progress sink is installed and reporting tools simply no-op. Invoked on the
        /// main thread (tools report there).
        /// </param>
        /// <param name="proposePlan">
        /// Surfaces a structured plan (Sprint 52) for the interactive <c>molca_propose_plan</c> tool: given
        /// the parsed ordered steps, renders a reviewable plan turn, pauses for Approve/Edit/Cancel, and
        /// resolves with the JSON tool-result content describing the user's disposition. When <c>null</c>
        /// (no interactive front-end) the tool falls through to its fallback delegate.
        /// </param>
        public static async Awaitable<LlmToolResult> ExecuteAsync(
            McpToolRegistry registry, LlmToolCall call, CancellationToken cancellationToken,
            Func<string, bool> isActionAllowed = null,
            Func<McpToolDefinition, string, bool> confirmAction = null,
            Func<AssistantUserPrompt, CancellationToken, Awaitable<string>> askUser = null,
            Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync = null,
            Action<McpProgressReport> onProgress = null,
            Func<IReadOnlyList<PlanStep>, CancellationToken, Awaitable<string>> proposePlan = null,
            Func<IReadOnlyList<SubtaskRequest>, CancellationToken, Awaitable<string>> spawnSubtasks = null)
        {
            if (registry == null || !registry.TryGet(call.Name, out var tool))
                return new LlmToolResult(call.Id, $"{{\"error\":\"Unknown tool '{call.Name}'.\"}}", isError: true);

            var modeError = McpModeGate.Check(tool.Mode, EditorApplication.isPlaying);
            if (modeError != null)
                return new LlmToolResult(call.Id, $"{{\"error\":{Quote(modeError)}}}", isError: true);

            var args = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

            // Structured plan proposal (Sprint 52): surface the steps as a reviewable plan turn and pause
            // for Approve/Edit/Cancel, feeding the disposition back as the tool result so the round loop
            // continues. The delegate returns the full result-content JSON (it owns the disposition shape).
            if (call.Name == Providers.CoreMcpToolProvider.ProposePlanToolName && proposePlan != null)
            {
                var steps = ParsePlanSteps(args);
                var planResult = await proposePlan(steps, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmToolResult(call.Id, planResult ?? "{}");
            }

            // Research sub-agents (Sprint 56): delegate to the read-only sub-agent runner, which returns only
            // the digest(s) so verbose tool output never enters the main history.
            if ((call.Name == Providers.CoreMcpToolProvider.SpawnSubtaskToolName
                 || call.Name == Providers.CoreMcpToolProvider.SpawnSubtasksToolName) && spawnSubtasks != null)
            {
                var requests = ParseSubtasks(call.Name, args);
                var digest = await spawnSubtasks(requests, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmToolResult(call.Id, digest ?? "{}");
            }

            // Interactive prompt (Sprint 25.6): pause the turn and surface the question as choices, then
            // feed the user's answer back as the tool result so the loop continues in the same turn.
            if (call.Name == Providers.CoreMcpToolProvider.AskUserToolName && askUser != null)
            {
                var prompt = ParseUserPrompt(args);
                var answer = await askUser(prompt, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmToolResult(call.Id, $"{{\"answer\":{Quote(answer ?? string.Empty)}}}");
            }

            // Action guardrail (Sprint 17.2): allowlist + modal confirmation.
            if (tool.Kind == McpToolKind.Action)
            {
                if (!(isActionAllowed?.Invoke(tool.Name) ?? false))
                {
                    McpActionAuditLog.Record(tool.Name, args, "chat", "refused", "not allowlisted");
                    return new LlmToolResult(call.Id,
                        $"{{\"error\":{Quote($"Action tool '{tool.Name}' is not on the MCP action allowlist.")}}}",
                        isError: true);
                }

                bool confirmed;
                if (confirmActionAsync != null)
                    confirmed = await confirmActionAsync(tool, args, cancellationToken);
                else if (confirmAction != null)
                    confirmed = confirmAction(tool, args);
                else
                    confirmed = EditorUtility.DisplayDialog("Confirm MCP Action",
                        McpActionGuard.BuildPrompt(tool, args), "Run", "Cancel");
                if (!confirmed)
                {
                    McpActionAuditLog.Record(tool.Name, args, "chat", "denied");
                    return new LlmToolResult(call.Id,
                        $"{{\"error\":{Quote("The user declined to run this action.")}}}", isError: true);
                }
            }

            try
            {
                // Install the progress sink (if any) only around the tool body, so reports made by this
                // tool reach the front-end and the sink is torn down before we return.
                using var progressScope = onProgress != null ? McpProgress.BeginScope(onProgress) : null;

                string result = tool.IsAsync
                    ? await tool.ExecuteAsync(args)
                    : tool.Execute(args);
                cancellationToken.ThrowIfCancellationRequested();
                if (tool.Kind == McpToolKind.Action)
                    McpActionAuditLog.Record(tool.Name, args, "chat", "executed");
                return new LlmToolResult(call.Id, result ?? "null");
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (System.Exception ex)
            {
                if (tool.Kind == McpToolKind.Action)
                    McpActionAuditLog.Record(tool.Name, args, "chat", "failed", ex.Message);
                return new LlmToolResult(call.Id, $"{{\"error\":{Quote(ex.Message)}}}", isError: true);
            }
        }

        /// <summary>Parses the <c>molca_ask_user</c> arguments into a prompt; tolerant of missing fields.</summary>
        private static AssistantUserPrompt ParseUserPrompt(string argsJson)
        {
            var question = string.Empty;
            var options = Array.Empty<string>();
            try
            {
                var o = JObject.Parse(argsJson);
                question = (string)o["question"] ?? string.Empty;
                if (o["options"] is JArray arr)
                {
                    var list = new List<string>();
                    foreach (var t in arr)
                    {
                        var s = (string)t;
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                    options = list.ToArray();
                }
            }
            catch
            {
                // Malformed arguments — fall back to whatever question we parsed (possibly empty).
            }
            return new AssistantUserPrompt(question, options);
        }

        /// <summary>Parses sub-task arguments for the single/batch spawn tools into requests; tolerant of missing fields.</summary>
        private static List<SubtaskRequest> ParseSubtasks(string toolName, string argsJson)
        {
            var requests = new List<SubtaskRequest>();
            try
            {
                var o = JObject.Parse(argsJson);
                if (toolName == Providers.CoreMcpToolProvider.SpawnSubtasksToolName)
                {
                    if (o["tasks"] is JArray arr)
                        foreach (var t in arr)
                        {
                            if (t is not JObject task) continue;
                            var p = (string)task["prompt"];
                            if (!string.IsNullOrWhiteSpace(p)) requests.Add(new SubtaskRequest(p.Trim(), (string)task["focus"]));
                        }
                }
                else
                {
                    var p = (string)o["prompt"];
                    if (!string.IsNullOrWhiteSpace(p)) requests.Add(new SubtaskRequest(p.Trim(), (string)o["focus"]));
                }
            }
            catch
            {
                // Malformed arguments — return whatever parsed (possibly none).
            }
            return requests;
        }

        /// <summary>Parses the <c>molca_propose_plan</c> arguments into an ordered step list; tolerant of missing fields.</summary>
        private static List<PlanStep> ParsePlanSteps(string argsJson)
        {
            var steps = new List<PlanStep>();
            try
            {
                var o = JObject.Parse(argsJson);
                if (o["steps"] is JArray arr)
                {
                    var index = 1;
                    foreach (var t in arr)
                    {
                        if (t is not JObject step) continue;
                        var summary = (string)step["summary"];
                        if (string.IsNullOrWhiteSpace(summary)) continue;
                        var id = (string)step["id"];
                        if (string.IsNullOrWhiteSpace(id)) id = index.ToString();
                        steps.Add(new PlanStep(id, summary));
                        index++;
                    }
                }
            }
            catch
            {
                // Malformed arguments — surface whatever steps parsed (possibly none).
            }
            return steps;
        }

        // Minimal JSON string-escape for embedding a message into a hand-built error object.
        private static string Quote(string s)
            => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
    }
}
