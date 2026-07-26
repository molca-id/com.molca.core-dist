using System;
using Molca.Editor.Mcp.Assistant;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Remote
{
    /// <summary>Bounded, UI-free adapter over the one shared in-Editor Assistant runtime.</summary>
    internal static class AssistantRemoteFacade
    {
        private const int MaxTurns = 30;
        private const int MaxTurnTextChars = 1200;

        internal static event Action Changed
        {
            add => AssistantChatRuntime.Shared.Changed += value;
            remove => AssistantChatRuntime.Shared.Changed -= value;
        }

        internal static JObject Snapshot()
        {
            var controller = AssistantChatRuntime.Shared.Controller;
            var transcript = new JArray();
            var first = Math.Max(0, controller.Transcript.Count - MaxTurns);
            for (var i = first; i < controller.Transcript.Count; i++)
            {
                var turn = controller.Transcript[i];
                transcript.Add(new JObject
                {
                    ["kind"] = turn.Kind.ToString().ToLowerInvariant(),
                    ["text"] = Truncate(turn.Text, MaxTurnTextChars),
                    ["answer"] = Truncate(turn.PromptAnswer, 512),
                    ["isConfirmation"] = turn.IsConfirmation
                });
            }

            var pending = controller.PendingPrompt;
            return new JObject
            {
                ["sessionId"] = controller.CurrentSessionId ?? string.Empty,
                ["title"] = controller.CurrentSessionTitle ?? string.Empty,
                ["status"] = controller.IsAwaitingUser ? "waiting" :
                    controller.IsBusy ? "running" : "idle",
                ["streamingText"] = Truncate(controller.StreamingText, 8000),
                ["activeTool"] = controller.ActiveToolName ?? string.Empty,
                ["allowActions"] = MolcaRemoteSettings.AllowActions,
                ["actionMode"] = controller.ActionMode.ToString(),
                ["pendingPrompt"] = pending == null ? null : new JObject
                {
                    ["question"] = Truncate(pending.Question, 2000),
                    ["options"] = new JArray(pending.Options ?? Array.Empty<string>()),
                    ["canAnswerRemotely"] = !PendingPromptIsConfirmation(controller)
                },
                ["transcript"] = transcript
            };
        }

        internal static JObject Execute(JObject payload)
        {
            if (!MolcaRemoteSettings.AllowAssistant)
                return Failure("remote_assistant_disabled");

            var runtime = AssistantChatRuntime.Shared;
            var type = payload.Value<string>("type");
            switch (type)
            {
                case "assistant.turn.start":
                    return runtime.TryStartRemoteTurn(
                        Truncate(payload.Value<string>("text"), 16000),
                        MolcaRemoteSettings.AllowActions &&
                        payload.Value<bool>("serverAllowsActions"))
                        ? Accepted() : Failure("assistant_busy_or_empty");
                case "assistant.turn.retry":
                    return runtime.TryRetryRemoteTurn(
                        Truncate(payload.Value<string>("text"), 16000),
                        MolcaRemoteSettings.AllowActions &&
                        payload.Value<bool>("serverAllowsActions"))
                        ? Accepted() : Failure("assistant_retry_unavailable");
                case "assistant.turn.stop":
                    runtime.StopCurrentTurn();
                    return Accepted();
                case "assistant.prompt.answer":
                    if (PendingPromptIsConfirmation(runtime.Controller))
                        return Failure("local_confirmation_required");
                    return runtime.TryAnswerPending(Truncate(payload.Value<string>("answer"), 4000))
                        ? Accepted() : Failure("assistant_prompt_unavailable");
                case "assistant.state.get":
                    return new JObject { ["ok"] = true, ["result"] = Snapshot() };
                default:
                    return Failure("unsupported_command");
            }
        }

        internal static void StopForAuthorizationLoss()
        {
            AssistantChatRuntime.Shared.StopRemoteTurnIfActive();
        }

        internal static void StopActionsForMaintenance()
        {
            AssistantChatRuntime.Shared.StopRemoteActionTurnIfActive();
        }

        private static bool PendingPromptIsConfirmation(AssistantChatController controller)
        {
            for (var i = controller.Transcript.Count - 1; i >= 0; i--)
            {
                var turn = controller.Transcript[i];
                if (turn.Kind == ChatTurnKind.Prompt && string.IsNullOrEmpty(turn.PromptAnswer))
                    return turn.IsConfirmation;
            }
            return false;
        }

        private static JObject Accepted() =>
            new JObject { ["ok"] = true, ["result"] = Snapshot() };

        private static JObject Failure(string error) =>
            new JObject { ["ok"] = false, ["error"] = error };

        private static string Truncate(string value, int max)
        {
            value ??= string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
