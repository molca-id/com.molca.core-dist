using System;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Editor-domain, transport-neutral owner for the shared Assistant controller and active turn.
    /// Views and remote clients observe this service; only an explicit stop cancels accepted work.
    /// </summary>
    internal sealed class AssistantChatRuntime
    {
        private static AssistantChatRuntime _shared;
        private CancellationTokenSource _turnCancellation = new CancellationTokenSource();
        private bool _remoteTurnActive;
        private bool _remoteActionsEnabled;

        internal static AssistantChatRuntime Shared => _shared ??= CreateProductionRuntime();

        internal AssistantSettings Settings { get; }
        internal AssistantChatController Controller { get; }
        internal CancellationToken TurnToken => _turnCancellation.Token;
        internal event Action Changed;

        internal AssistantChatRuntime(AssistantSettings settings, AssistantChatController controller)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Controller = controller ?? throw new ArgumentNullException(nameof(controller));
            Controller.Changed += () => Changed?.Invoke();
        }

        internal bool TryStartTurn(string text)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(text)) return false;
            RunTurnAsync(text);
            return true;
        }

        internal bool TryStartRemoteReadOnlyTurn(string text)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(text)) return false;
            RunRemoteReadOnlyTurnAsync(text, retry: false);
            return true;
        }

        internal bool TryStartRemoteTurn(string text, bool allowActions)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(text)) return false;
            RunRemoteTurnAsync(text, retry: false, allowActions);
            return true;
        }

        internal bool TryRetryTurn(string editedText = null)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(Controller.LastUserText)) return false;
            RunRetryAsync(editedText);
            return true;
        }

        internal bool TryRetryRemoteReadOnlyTurn(string editedText = null)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(Controller.LastUserText)) return false;
            RunRemoteReadOnlyTurnAsync(editedText, retry: true);
            return true;
        }

        internal bool TryRetryRemoteTurn(string editedText, bool allowActions)
        {
            if (Controller.IsBusy || string.IsNullOrWhiteSpace(Controller.LastUserText)) return false;
            RunRemoteTurnAsync(editedText, retry: true, allowActions);
            return true;
        }

        internal bool TryAnswerPending(string answer)
        {
            if (!Controller.IsAwaitingUser) return false;
            Controller.AnswerPending(answer ?? string.Empty);
            return true;
        }

        internal void StopCurrentTurn()
        {
            _turnCancellation.Cancel();
            _turnCancellation.Dispose();
            _turnCancellation = new CancellationTokenSource();
            Changed?.Invoke();
        }

        internal bool StopRemoteTurnIfActive()
        {
            if (!_remoteTurnActive) return false;
            StopCurrentTurn();
            return true;
        }

        internal bool StopRemoteActionTurnIfActive()
        {
            if (!_remoteTurnActive || !_remoteActionsEnabled) return false;
            StopCurrentTurn();
            return true;
        }

        private async void RunTurnAsync(string text)
        {
            try { await Controller.SendAsync(text, TurnToken); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private async void RunRetryAsync(string editedText)
        {
            try { await Controller.RetryLastAsync(TurnToken, editedText); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private async void RunRemoteReadOnlyTurnAsync(string text, bool retry)
        {
            var previous = Controller.ConfirmActionInModeAsyncOverride;
            Controller.ConfirmActionInModeAsyncOverride = DenyRemoteAction;
            try
            {
                if (retry) await Controller.RetryLastAsync(TurnToken, text);
                else await Controller.SendAsync(text, TurnToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { Controller.ConfirmActionInModeAsyncOverride = previous; }
        }

        private async void RunRemoteTurnAsync(string text, bool retry, bool allowActions)
        {
            var previous = Controller.ConfirmActionInModeAsyncOverride;
            if (!allowActions) Controller.ConfirmActionInModeAsyncOverride = DenyRemoteAction;
            using var caller = McpActionAuditLog.BeginCaller("remote-assistant");
            _remoteTurnActive = true;
            _remoteActionsEnabled = allowActions;
            try
            {
                if (retry) await Controller.RetryLastAsync(TurnToken, text);
                else await Controller.SendAsync(text, TurnToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogException(exception); }
            finally
            {
                _remoteTurnActive = false;
                _remoteActionsEnabled = false;
                Controller.ConfirmActionInModeAsyncOverride = previous;
            }
        }

        private static Awaitable<bool> DenyRemoteAction(
            McpToolDefinition _tool, string _arguments, CancellationToken _cancellationToken)
        {
            var source = new AwaitableCompletionSource<bool>();
            source.SetResult(false);
            return source.Awaitable;
        }

        private static AssistantChatRuntime CreateProductionRuntime()
        {
            var settings = AssistantSettings.GetOrCreateSettings();
            var controller = new AssistantChatController(settings)
            {
                ActionMode = AssistantComposer.LoadActionMode()
            };
            return new AssistantChatRuntime(settings, controller);
        }
    }
}
