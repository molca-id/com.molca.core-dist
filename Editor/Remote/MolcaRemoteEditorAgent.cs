using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Molca.Editor.Licensing;
using Molca.Editor.Mcp;
using Molca.Editor.Mcp.Assistant;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.Remote
{
    /// <summary>Project-scoped local policy for the outbound Molca Remote connection.</summary>
    internal static class MolcaRemoteSettings
    {
        private const string EnabledKey = "Remote.Enabled";
        private const string AssistantKey = "Remote.AllowAssistant";
        private const string ActionsKey = "Remote.AllowActions";
        private const string InstallationKey = "Remote.InstallationId";

        internal static bool Enabled
        {
            get => MolcaEditorPrefs.GetBool(EnabledKey, false);
            set => MolcaEditorPrefs.SetBool(EnabledKey, value);
        }

        internal static bool AllowAssistant
        {
            get => MolcaEditorPrefs.GetBool(AssistantKey, false);
            set => MolcaEditorPrefs.SetBool(AssistantKey, value);
        }

        internal static bool AllowActions
        {
            get => MolcaEditorPrefs.GetBool(ActionsKey, false);
            set => MolcaEditorPrefs.SetBool(ActionsKey, value);
        }

        internal static string InstallationId
        {
            get
            {
                var value = MolcaEditorPrefs.GetString(InstallationKey, string.Empty);
                if (!string.IsNullOrEmpty(value)) return value;
                value = Guid.NewGuid().ToString("N");
                MolcaEditorPrefs.SetString(InstallationKey, value);
                return value;
            }
        }
    }

    /// <summary>Pure local action gate shared by the dispatcher and adversarial Editor tests.</summary>
    internal static class MolcaRemoteActionPolicy
    {
        internal static string Validate(
            bool requestedAction, McpToolKind actualKind, bool actionsEnabled,
            bool isAllowlisted, bool assistantBusy)
        {
            if (requestedAction != (actualKind == McpToolKind.Action))
                return "tool_kind_mismatch";
            if (!requestedAction) return null;
            if (!actionsEnabled) return "remote_actions_disabled";
            if (!isAllowlisted) return "action_not_allowlisted";
            if (assistantBusy) return "assistant_busy";
            return null;
        }
    }

    /// <summary>Builds bounded v1 envelopes and the privacy-allowlisted Editor state.</summary>
    internal static class MolcaRemoteProtocol
    {
        internal const int Version = 1;
        internal const int MaxMessageBytes = 64 * 1024;

        internal static string Envelope(
            string type, string sessionId, long sequence, JObject payload, string correlationId = null)
        {
            return new JObject
            {
                ["schema"] = Version,
                ["type"] = type,
                ["sessionId"] = sessionId,
                ["messageId"] = Guid.NewGuid().ToString("N"),
                ["correlationId"] = correlationId,
                ["sequence"] = sequence,
                ["sentAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["payload"] = payload ?? new JObject()
            }.ToString(Formatting.None);
        }

        internal static JObject CollectState()
        {
            var scene = SceneManager.GetActiveScene();
            var selected = Selection.activeObject;
            var project = global::Molca.MolcaProjectSettings.Instance;
            return new JObject
            {
                ["editor"] = new JObject
                {
                    ["unityVersion"] = Application.unityVersion,
                    ["coreVersion"] = UnityEditor.PackageManager.PackageInfo
                        .FindForAssembly(typeof(MolcaRemoteEditorAgent).Assembly)?.version ?? string.Empty,
                    ["mode"] = EditorApplication.isPlaying ? "play" : "edit",
                    ["paused"] = EditorApplication.isPaused,
                    ["compiling"] = EditorApplication.isCompiling,
                    ["updating"] = EditorApplication.isUpdating,
                    ["safeToInvoke"] = !EditorApplication.isCompiling && !EditorApplication.isUpdating
                },
                ["project"] = new JObject
                {
                    ["projectId"] = project?.ProjectId ?? string.Empty,
                    ["projectCode"] = project?.ProjectCode ?? string.Empty
                },
                ["scene"] = new JObject
                {
                    ["activeScene"] = scene.IsValid() ? scene.name : string.Empty,
                    ["dirty"] = scene.IsValid() && scene.isDirty,
                    ["loadedSceneCount"] = SceneManager.sceneCount
                },
                ["selection"] = new JObject
                {
                    ["displayName"] = selected != null ? selected.name : string.Empty,
                    ["objectType"] = selected != null ? selected.GetType().Name : string.Empty
                },
                ["console"] = new JObject { ["errors"] = 0, ["warnings"] = 0 },
                ["assistant"] = new JObject { ["status"] = "idle" }
            };
        }
    }

    [Serializable]
    internal sealed class MolcaRemoteConnectResponse
    {
        public string sessionId;
        public string deviceId;
        public int protocolVersion;
        public string agentToken;
        public string tokenExpiresAt;
        public string sessionExpiresAt;
        public string socketUrl;
        public int heartbeatSeconds;
    }

    /// <summary>
    /// Domain-lifetime outbound connection from this Editor to Molca Remote. It never opens a local/public
    /// listener and remains inert until the signed-in user explicitly enables Remote in project preferences.
    /// </summary>
    [InitializeOnLoad]
    internal static class MolcaRemoteEditorAgent
    {
        /// <summary>How long a burst of activity changes is allowed to settle before a snapshot is built.</summary>
        private const int SnapshotDebounceMs = 750;

        /// <summary>Minimum wall-clock gap between two change-driven snapshots.</summary>
        private static readonly TimeSpan SnapshotFloor = TimeSpan.FromSeconds(2);

        private static CancellationTokenSource _lifetime;
        private static Task _runTask;
        private static readonly string ProcessNonce = Guid.NewGuid().ToString("N");

        // Set by the heartbeat so a snapshot goes out even when nothing changed — a session that has been
        // quiet for a minute should still be provably current, not merely assumed so.
        private static int _forceSnapshot;

        internal static string Status { get; private set; } = "Disabled";
        internal static string SessionId { get; private set; } = string.Empty;

        static MolcaRemoteEditorAgent()
        {
            EditorApplication.delayCall += EnsureStarted;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        internal static void ApplySettings()
        {
            Stop();
            EnsureStarted();
        }

        private static void EnsureStarted()
        {
            if (!MolcaRemoteSettings.Enabled)
            {
                Status = "Disabled";
                return;
            }
            if (_runTask != null && !_runTask.IsCompleted) return;
            _lifetime = new CancellationTokenSource();
            _runTask = RunLoopAsync(_lifetime.Token);
        }

        private static void Stop()
        {
            try { _lifetime?.Cancel(); } catch { }
            _lifetime?.Dispose();
            _lifetime = null;
            _runTask = null;
            SessionId = string.Empty;
            Status = MolcaRemoteSettings.Enabled ? "Disconnected" : "Disabled";
            RemoteCompanionFacade.Stop();
            if (!MolcaRemoteSettings.Enabled)
            {
                AssistantRemoteFacade.StopForAuthorizationLoss();
                RemoteAutomationCommands.CancelForAuthorizationLoss();
            }
        }

        private static async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            var delaySeconds = 2;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Status = "Connecting";
                    var session = await ConnectSessionAsync(cancellationToken);
                    SessionId = session.sessionId;
                    Status = "Connected";
                    await RunSocketAsync(session, cancellationToken);
                    delaySeconds = 2;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Status = UserFacingStatus(exception);
                    Debug.LogWarning($"[Molca Remote] {Status}");
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                delaySeconds = Math.Min(60, delaySeconds * 2);
            }
        }

        private static async Task<MolcaRemoteConnectResponse> ConnectSessionAsync(CancellationToken cancellationToken)
        {
            var entitlement = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(
                entitlement, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                throw new InvalidOperationException("Sign in required");
            var project = global::Molca.MolcaProjectSettings.Instance;
            if (project == null || string.IsNullOrWhiteSpace(project.ProjectBinding))
                throw new InvalidOperationException("Project binding required");

            var body = new JObject
            {
                ["projectBinding"] = project.ProjectBinding,
                ["installationId"] = MolcaRemoteSettings.InstallationId,
                ["processNonce"] = ProcessNonce,
                ["protocolVersion"] = MolcaRemoteProtocol.Version,
                ["coreVersion"] = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(MolcaRemoteEditorAgent).Assembly)?.version ?? string.Empty,
                ["unityVersion"] = Application.unityVersion,
                ["displayName"] = $"{Environment.MachineName} · Unity",
                ["capabilityDigest"] = $"assistant:{MolcaRemoteSettings.AllowAssistant};actions:{MolcaRemoteSettings.AllowActions}"
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Post, DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/remote/editor/connect");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entitlement);
            request.Headers.Add("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseReason(json, $"Remote connect failed ({(int)response.StatusCode})"));
            var result = JsonUtility.FromJson<MolcaRemoteConnectResponse>(json);
            if (result == null || result.protocolVersion != MolcaRemoteProtocol.Version ||
                string.IsNullOrEmpty(result.sessionId) || string.IsNullOrEmpty(result.agentToken) ||
                string.IsNullOrEmpty(result.socketUrl))
                throw new InvalidOperationException("Remote connect returned an invalid response");
            return result;
        }

        private static async Task RunSocketAsync(
            MolcaRemoteConnectResponse session, CancellationToken cancellationToken)
        {
            using var socket = new ClientWebSocket();
            using var sendLock = new SemaphoreSlim(1, 1);
            socket.Options.SetRequestHeader("Authorization", $"Bearer {session.agentToken}");
            await socket.ConnectAsync(new Uri(session.socketUrl), cancellationToken);
            long sequence = 0;
            await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                "agent.hello", session.sessionId, Interlocked.Increment(ref sequence), new JObject()), cancellationToken);

            // The companion's activity providers are stateful observers, so they are created once per
            // session on the main thread and disposed in the finally below — a provider that watches a
            // static source leaks otherwise.
            McpMainThreadDispatcher.Invoke(() => { RemoteCompanionFacade.Start(); return true; });
            Interlocked.Exchange(ref _forceSnapshot, 0);

            var firstSnapshot = CollectCompanionState();
            await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                "state.snapshot", session.sessionId, Interlocked.Increment(ref sequence),
                JObject.Parse(firstSnapshot)), cancellationToken);

            Func<long> nextSequence = () => Interlocked.Increment(ref sequence);
            using var snapshotWake = new SemaphoreSlim(0, 1);
            Action assistantChanged = () => SendAssistantStateInBackground(
                socket, sendLock, session.sessionId, nextSequence, cancellationToken);
            Action companionChanged = () => Signal(snapshotWake);
            AssistantRemoteFacade.Changed += assistantChanged;
            RemoteCompanionFacade.Changed += companionChanged;
            try
            {
                await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                    "assistant.turn.state", session.sessionId, nextSequence(),
                    AssistantRemoteFacade.Snapshot(), Guid.NewGuid().ToString("N")), cancellationToken);
                var receiveTask = ReceiveLoopAsync(
                    socket, session.sessionId, sendLock, nextSequence, cancellationToken);
                var snapshotTask = SnapshotLoopAsync(
                    socket, sendLock, session.sessionId, nextSequence, snapshotWake,
                    firstSnapshot, cancellationToken);
                var heartbeat = Math.Max(5, session.heartbeatSeconds);
                var rotateAt = DateTime.TryParse(
                    session.tokenExpiresAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var tokenExpiry)
                    ? tokenExpiry.ToUniversalTime().AddSeconds(-30)
                    : DateTime.UtcNow.AddMinutes(8);
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= rotateAt)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, "token_rotation", cancellationToken);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(heartbeat), cancellationToken);
                    await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                        "agent.heartbeat", session.sessionId, nextSequence(),
                        new JObject()), cancellationToken);
                    Interlocked.Exchange(ref _forceSnapshot, 1);
                    Signal(snapshotWake);
                }
                // The snapshot loop parks on the wake handle, which no closed socket will ever release.
                // Nudge it once so it observes the closed state and unwinds instead of hanging the session.
                Signal(snapshotWake);
                await receiveTask;
                await snapshotTask;
            }
            finally
            {
                AssistantRemoteFacade.Changed -= assistantChanged;
                RemoteCompanionFacade.Changed -= companionChanged;
                McpMainThreadDispatcher.Invoke(() =>
                {
                    // A remote-initiated run that nobody can still watch or stop from the browser must not
                    // keep mutating the project, so socket loss cancels it just as authorization loss does.
                    RemoteAutomationCommands.CancelForAuthorizationLoss();
                    RemoteCompanionFacade.Stop();
                    return true;
                });
            }
        }

        /// <summary>
        /// Sends <c>state.snapshot</c> on change rather than on a timer: a 750 ms debounce so a burst of
        /// activity updates becomes one message, a 2 s floor so a chatty run cannot saturate the socket, and
        /// a byte-identical drop so a provider that re-reports the same state costs nothing. The heartbeat
        /// forces one through regardless, so a quiet session is provably current.
        /// </summary>
        private static async Task SnapshotLoopAsync(
            ClientWebSocket socket, SemaphoreSlim sendLock, string sessionId, Func<long> nextSequence,
            SemaphoreSlim wake, string seedPayload, CancellationToken cancellationToken)
        {
            var lastPayload = seedPayload;
            var lastSentAt = DateTime.UtcNow;
            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    await wake.WaitAsync(cancellationToken);
                    await Task.Delay(SnapshotDebounceMs, cancellationToken);

                    var sinceLast = DateTime.UtcNow - lastSentAt;
                    if (sinceLast < SnapshotFloor)
                        await Task.Delay(SnapshotFloor - sinceLast, cancellationToken);
                    if (socket.State != WebSocketState.Open) return;

                    var forced = Interlocked.Exchange(ref _forceSnapshot, 0) == 1;
                    var payload = CollectCompanionState();
                    // A change that nets out to the same projection is not a change worth a message —
                    // battery and mobile data are the constraint this rule exists for.
                    if (!forced && payload == lastPayload) continue;

                    lastPayload = payload;
                    lastSentAt = DateTime.UtcNow;
                    await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                        "state.snapshot", sessionId, nextSequence(),
                        JObject.Parse(payload)), cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { } // the session's wake handle was torn down first
        }

        /// <summary>
        /// Builds the full snapshot payload on the main thread — base state plus the companion's activity
        /// and automation blocks — and returns it as compact JSON so the caller can compare two snapshots
        /// byte-for-byte without walking two object graphs.
        /// </summary>
        private static string CollectCompanionState() => McpMainThreadDispatcher.Invoke(() =>
        {
            var state = MolcaRemoteProtocol.CollectState();
            foreach (var block in RemoteCompanionFacade.StateBlocks())
                state[block.Key] = block.Value;
            return state.ToString(Formatting.None);
        });

        // SemaphoreSlim(0, 1) is a coalescing edge signal: a release while one is already pending is the
        // same request, so the full-semaphore throw is the expected case, not an error.
        private static void Signal(SemaphoreSlim wake)
        {
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task ReceiveLoopAsync(
            ClientWebSocket socket, string sessionId, SemaphoreSlim sendLock,
            Func<long> nextSequence, CancellationToken cancellationToken)
        {
            var buffer = new byte[MolcaRemoteProtocol.MaxMessageBytes];
            long inboundSequence = 0;
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatusDescription == "server_restart" ||
                        result.CloseStatusDescription == "maintenance")
                    {
                        McpMainThreadDispatcher.Invoke(() =>
                        {
                            AssistantRemoteFacade.StopActionsForMaintenance();
                            return true;
                        });
                    }
                    return;
                }
                if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("Unsupported Remote message");
                var envelope = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (envelope.Value<int>("schema") != MolcaRemoteProtocol.Version ||
                    envelope.Value<string>("sessionId") != sessionId)
                    continue;
                var incomingSequence = envelope.Value<long>("sequence");
                if (incomingSequence != inboundSequence + 1)
                    throw new InvalidOperationException("Remote message sequence is invalid");
                inboundSequence = incomingSequence;
                if (envelope.Value<string>("type") == "error")
                {
                    var code = (envelope["payload"] as JObject)?.Value<string>("code") ?? string.Empty;
                    if (code == "capability_denied" || code == "membership_required" ||
                        code == "session_revoked" || code == "device_revoked" ||
                        code == "project_binding_revoked" || code == "remote_disabled")
                    {
                        McpMainThreadDispatcher.Invoke(() =>
                        {
                            AssistantRemoteFacade.StopForAuthorizationLoss();
                            RemoteAutomationCommands.CancelForAuthorizationLoss();
                            return true;
                        });
                    }
                    continue;
                }
                if (envelope.Value<string>("type") != "command.invoke") continue;
                var correlationId = envelope.Value<string>("correlationId");
                if (string.IsNullOrWhiteSpace(correlationId))
                    throw new InvalidOperationException("Remote command correlation is missing");
                var payload = envelope["payload"] as JObject ?? new JObject();
                await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                    "command.ack", sessionId, nextSequence(),
                    new JObject { ["accepted"] = true }, correlationId), cancellationToken);
                var outcome = ExecuteCommand(payload);
                await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                    "command.result", sessionId, nextSequence(), outcome, correlationId), cancellationToken);
            }
        }

        private static JObject ExecuteCommand(JObject payload)
        {
            if (payload.Value<string>("type")?.StartsWith("assistant.", StringComparison.Ordinal) == true)
                return McpMainThreadDispatcher.Invoke(() => AssistantRemoteFacade.Execute(payload));
            if (payload.Value<string>("type")?.StartsWith("automation.", StringComparison.Ordinal) == true)
                return McpMainThreadDispatcher.Invoke(() => RemoteAutomationCommands.Execute(payload));
            var commandType = payload.Value<string>("type");
            var requestedAction = commandType == "tool.action";
            if (commandType != "tool.invoke" && !requestedAction)
                return new JObject { ["ok"] = false, ["error"] = "unsupported_command" };
            if (DateTime.TryParse(
                    payload.Value<string>("expiresAt"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt) &&
                expiresAt.ToUniversalTime() <= DateTime.UtcNow)
                return new JObject { ["ok"] = false, ["error"] = "command_expired" };
            var toolName = payload.Value<string>("tool");
            var arguments = payload["arguments"]?.ToString(Formatting.None) ?? "{}";
            try
            {
                var resolved = McpMainThreadDispatcher.Invoke<(
                    McpToolDefinition tool, McpSettings settings, string error)>(() =>
                {
                    var settings = McpSettings.GetOrCreateSettings();
                    var registry = settings.BuildRegistry();
                    if (!registry.TryGet(toolName, out var tool))
                        return (tool: (McpToolDefinition)null, settings: settings, error: "unknown_tool");
                    var policyError = MolcaRemoteActionPolicy.Validate(
                        requestedAction, tool.Kind, MolcaRemoteSettings.AllowActions,
                        settings.IsActionAllowed(tool.Name),
                        AssistantChatRuntime.Shared.Controller.IsBusy);
                    if (policyError != null)
                        return (tool: (McpToolDefinition)null, settings: settings, error: policyError);
                    if (requestedAction)
                    {
                        var preconditionError = ValidateActionPrecondition(
                            payload["precondition"] as JObject);
                        if (preconditionError != null)
                            return (tool: (McpToolDefinition)null, settings: settings,
                                error: preconditionError);
                    }
                    var modeError = McpModeGate.Check(tool.Mode, EditorApplication.isPlaying);
                    return modeError == null
                        ? (tool, settings, (string)null)
                        : (null, settings, modeError);
                });
                if (resolved.error != null)
                    return new JObject { ["ok"] = false, ["error"] = resolved.error };
                var undoBefore = requestedAction &&
                    resolved.tool.Reversibility == McpToolReversibility.FileSnapshot
                    ? CurrentUndoId() : null;
                var undoGroupBefore = requestedAction &&
                    resolved.tool.Reversibility == McpToolReversibility.UnityUndo
                    ? McpMainThreadDispatcher.Invoke(() => Undo.GetCurrentGroup()) : -1;
                var timeoutMs = resolved.tool.InvocationTimeoutMs > 0
                    ? resolved.tool.InvocationTimeoutMs
                    : requestedAction ? 14 * 60 * 1000 : 60 * 1000;
                var raw = resolved.tool.IsAsync
                    ? McpMainThreadDispatcher.InvokeAsync(
                        () => resolved.tool.ExecuteAsync(arguments), timeoutMs)
                    : McpMainThreadDispatcher.Invoke(
                        () => resolved.tool.Execute(arguments), timeoutMs);
                var outcome = new JObject
                {
                    ["ok"] = true,
                    ["result"] = JToken.Parse(raw ?? "null")
                };
                if (requestedAction)
                {
                    var undoAfter = resolved.tool.Reversibility == McpToolReversibility.FileSnapshot
                        ? CurrentUndoId() : null;
                    var undoGroupAfter = resolved.tool.Reversibility == McpToolReversibility.UnityUndo
                        ? McpMainThreadDispatcher.Invoke(() => Undo.GetCurrentGroup()) : -1;
                    outcome["action"] = new JObject
                    {
                        ["tool"] = resolved.tool.Name,
                        ["reversibility"] = resolved.tool.Reversibility.ToString(),
                        ["undoEntryId"] = !string.IsNullOrEmpty(undoAfter) && undoAfter != undoBefore
                            ? undoAfter : null,
                        ["undoGroup"] = undoGroupAfter > undoGroupBefore
                            ? undoGroupBefore + 1 : -1
                    };
                    McpMainThreadDispatcher.Invoke(() =>
                    {
                        McpActionAuditLog.Record(
                            resolved.tool.Name, arguments, "remote", "executed");
                        return true;
                    });
                }
                return Encoding.UTF8.GetByteCount(outcome.ToString(Formatting.None)) <= 48 * 1024
                    ? outcome
                    : new JObject { ["ok"] = false, ["error"] = "result_too_large" };
            }
            catch (Exception exception)
            {
                if (requestedAction)
                {
                    try
                    {
                        McpMainThreadDispatcher.Invoke(() =>
                        {
                            McpActionAuditLog.Record(
                                toolName, arguments, "remote", "failed", exception.Message);
                            return true;
                        });
                    }
                    catch { }
                }
                return new JObject { ["ok"] = false, ["error"] = exception.Message };
            }
        }

        private static string CurrentUndoId()
        {
            return McpMainThreadDispatcher.Invoke(() =>
            {
                var entries = McpUndoStack.Entries;
                return entries.Count > 0 ? entries[entries.Count - 1].Id : null;
            });
        }

        private static string ValidateActionPrecondition(JObject expected)
        {
            if (expected == null) return "action_precondition_missing";
            var scene = SceneManager.GetActiveScene();
            var selected = Selection.activeObject;
            var mode = EditorApplication.isPlaying ? "play" : "edit";
            if (expected.Value<string>("mode") != mode ||
                expected.Value<string>("activeScene") !=
                    (scene.IsValid() ? scene.name : string.Empty) ||
                expected.Value<string>("selectionName") !=
                    (selected != null ? selected.name : string.Empty) ||
                expected.Value<string>("selectionType") !=
                    (selected != null ? selected.GetType().Name : string.Empty))
                return "action_context_changed";
            return null;
        }

        private static async void SendAssistantStateInBackground(
            ClientWebSocket socket, SemaphoreSlim sendLock, string sessionId,
            Func<long> nextSequence, CancellationToken cancellationToken)
        {
            try
            {
                if (socket.State != WebSocketState.Open || cancellationToken.IsCancellationRequested) return;
                await SendLockedAsync(socket, sendLock, MolcaRemoteProtocol.Envelope(
                    "assistant.turn.state", sessionId, nextSequence(),
                    AssistantRemoteFacade.Snapshot(), Guid.NewGuid().ToString("N")), cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogWarning($"Molca Remote Assistant update failed: {exception.Message}"); }
        }

        private static async Task SendLockedAsync(
            ClientWebSocket socket, SemaphoreSlim sendLock, string json, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MolcaRemoteProtocol.MaxMessageBytes)
                throw new InvalidOperationException("Remote message exceeds the protocol limit");
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally { sendLock.Release(); }
        }

        private static string ParseReason(string json, string fallback)
        {
            try { return JObject.Parse(json).Value<string>("reason") ?? fallback; }
            catch { return fallback; }
        }

        private static string UserFacingStatus(Exception exception)
        {
            var message = exception.Message ?? "Connection failed";
            return message switch
            {
                "membership_required" => "Project access removed",
                "capability_denied" => "Remote access denied",
                "project_binding_revoked" => "Project binding revoked",
                "protocol_unsupported" => "Framework update required",
                _ => message
            };
        }
    }
}
