using System;
using System.Collections.Generic;
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
        public static async Awaitable<LlmToolResult> ExecuteAsync(
            McpToolRegistry registry, LlmToolCall call, CancellationToken cancellationToken,
            Func<string, bool> isActionAllowed = null,
            Func<McpToolDefinition, string, bool> confirmAction = null,
            Func<AssistantUserPrompt, CancellationToken, Awaitable<string>> askUser = null,
            Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync = null,
            Action<McpProgressReport> onProgress = null)
        {
            if (registry == null || !registry.TryGet(call.Name, out var tool))
                return new LlmToolResult(call.Id, $"{{\"error\":\"Unknown tool '{call.Name}'.\"}}", isError: true);

            var modeError = McpModeGate.Check(tool.Mode, EditorApplication.isPlaying);
            if (modeError != null)
                return new LlmToolResult(call.Id, $"{{\"error\":{Quote(modeError)}}}", isError: true);

            var args = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

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

        // Minimal JSON string-escape for embedding a message into a hand-built error object.
        private static string Quote(string s)
            => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
    }
}
