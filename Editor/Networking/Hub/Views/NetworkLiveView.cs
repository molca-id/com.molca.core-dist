using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Networking;
using Molca.Networking.Configuration;
using Molca.Networking.Data;
using Molca.Networking.Diagnostics;
using Molca.Networking.Pipeline;
using Molca.Networking.Streaming;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Live pipeline telemetry: what is in flight, what each route's queue and circuit are doing, the
    /// recent request timeline, and streaming session state.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="INetworkDiagnostics"/> — the stable interface the network subsystem registers —
    /// rather than reaching into the client, and reads streaming state through
    /// <see cref="INetworkStreamStatus"/> rather than by reflection over provider types (plan §7.12).
    /// <para>
    /// Two sources, deliberately kept apart. The <b>runtime</b> section is the running game's pipeline and
    /// only exists in Play mode; the <b>console</b> section is this workspace's own client. Merging them
    /// would make "did my game send that, or did I?" unanswerable, and they have separate circuit
    /// breakers for the same reason.
    /// </para>
    /// </remarks>
    internal sealed class NetworkLiveView : VisualElement
    {
        /// <summary>Poll interval, matching the Settings leaf's live telemetry.</summary>
        private const long RefreshIntervalMs = 1000;

        /// <summary>Timeline rows rendered at once.</summary>
        private const int MaxTimelineRows = 40;

        private readonly NetworkHubSession _session;
        private readonly VisualElement _content = new VisualElement();

        private readonly List<string> _serviceFilterChoices = new List<string>();
        private string _serviceFilter = AnyChoice;
        private string _outcomeFilter = AnyChoice;
        private string _correlationFilter = string.Empty;
        private bool _showConsoleTraffic = true;

        private IVisualElementScheduledItem _poll;

        private const string AnyChoice = "(any)";

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkLiveView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            scroll.Add(BuildFilters());
            scroll.Add(_content);
            Add(scroll);

            Rebuild();

            // Polls only while attached, and stops on detach — the workspace is cached, so a view left
            // hidden must not keep waking the editor once it is evicted.
            RegisterCallback<AttachToPanelEvent>(_ => _poll = schedule.Execute(Rebuild).Every(RefreshIntervalMs));
            RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause());
        }

        #region Filters

        private VisualElement BuildFilters()
        {
            var card = NetworkHubUi.Card("Filters", "Applies to the timeline below");

            _serviceFilterChoices.Clear();
            _serviceFilterChoices.Add(AnyChoice);
            foreach (var service in _session.Catalog.Services)
            {
                if (service != null && !string.IsNullOrEmpty(service.Id))
                    _serviceFilterChoices.Add(service.Id);
            }

            var serviceField = new PopupField<string>("Service", _serviceFilterChoices,
                _serviceFilterChoices.Contains(_serviceFilter) ? _serviceFilter : AnyChoice);
            serviceField.RegisterValueChangedCallback(evt => { _serviceFilter = evt.newValue; Rebuild(); });
            card.Body.Add(serviceField);

            var outcomes = new List<string> { AnyChoice, "Succeeded", "Failed" };
            var outcome = new PopupField<string>("Outcome", outcomes,
                outcomes.Contains(_outcomeFilter) ? _outcomeFilter : AnyChoice);
            outcome.RegisterValueChangedCallback(evt => { _outcomeFilter = evt.newValue; Rebuild(); });
            card.Body.Add(outcome);

            var correlation = new TextField("Correlation ID") { value = _correlationFilter };
            correlation.tooltip =
                "Paste the correlation ID from a log line or a bug report to find the one send it names.";
            correlation.RegisterCallback<BlurEvent>(_ =>
            {
                _correlationFilter = correlation.value;
                Rebuild();
            });
            card.Body.Add(correlation);

            var console = new Toggle("Include console traffic") { value = _showConsoleTraffic };
            console.tooltip = "Requests this workspace sent, as opposed to the running game's.";
            console.RegisterValueChangedCallback(evt => { _showConsoleTraffic = evt.newValue; Rebuild(); });
            card.Body.Add(console);

            return card;
        }

        private bool Matches(NetworkRequestDiagnostic record)
        {
            if (record == null) return false;

            if (!string.Equals(_serviceFilter, AnyChoice, StringComparison.Ordinal) &&
                !string.Equals(record.Route.ServiceId, _serviceFilter, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(_outcomeFilter, "Succeeded", StringComparison.Ordinal) && !record.IsSuccess)
                return false;

            if (string.Equals(_outcomeFilter, "Failed", StringComparison.Ordinal) && record.IsSuccess)
                return false;

            if (!string.IsNullOrEmpty(_correlationFilter) &&
                record.CorrelationId.IndexOf(_correlationFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Sections

        private void Rebuild()
        {
            _content.Clear();

            var runtime = RuntimeDiagnostics();

            _content.Add(BuildRuntimeCard(runtime));
            _content.Add(BuildRouteStateCard(runtime));
            _content.Add(BuildTimelineCard(runtime));
            _content.Add(BuildStreamingCard());
        }

        /// <summary>
        /// The running game's diagnostics, or <c>null</c> outside Play mode.
        /// </summary>
        /// <remarks>
        /// Resolved through <c>RuntimeManager</c> as a registered service, which is why this view needs no
        /// reference to the subsystem type and gets <c>null</c> — not an exception — when the game is not
        /// running or the subsystem is absent.
        /// </remarks>
        private static INetworkDiagnostics RuntimeDiagnostics() =>
            Application.isPlaying && RuntimeManager.IsReady
                ? RuntimeManager.GetService<INetworkDiagnostics>()
                : null;

        private VisualElement BuildRuntimeCard(INetworkDiagnostics runtime)
        {
            var card = NetworkHubUi.Card(
                "Runtime pipeline",
                "The running game's routed client",
                runtime != null ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                runtime != null ? "Live" : "Not in Play mode");

            if (runtime == null)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Enter Play mode to see the game's own requests. The console section below is live " +
                    "either way — it is this workspace's client, not the game's."));
                return card;
            }

            card.Body.Add(NetworkHubUi.Field("Completed", runtime.TotalCompleted.ToString()));
            card.Body.Add(NetworkHubUi.Field("Failed", runtime.TotalFailed.ToString()));
            card.Body.Add(NetworkHubUi.Field("Retained", $"{runtime.Count} / {runtime.Capacity}",
                "The diagnostic ring buffer is bounded, so a session that runs for days costs a fixed " +
                "amount of memory."));
            card.Body.Add(NetworkHubUi.Field("Observer failures", runtime.ObserverFailureCount.ToString(),
                "Observer callbacks that threw. Counted separately because they never affect a request."));
            card.Body.Add(NetworkHubUi.Field("Recording", runtime.IsPaused ? "paused" : "on"));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Export redacted", () =>
                    EditorGUIUtility.systemCopyBuffer = runtime.Export())));

            return card;
        }

        private VisualElement BuildRouteStateCard(INetworkDiagnostics runtime)
        {
            var card = NetworkHubUi.Card("Queues and circuits", "Per route");

            var states = new List<RoutePipelineState>();
            if (runtime != null) states.AddRange(runtime.RouteStates());
            if (_showConsoleTraffic && _session.Console.Diagnostics != null)
                states.AddRange(_session.Console.Diagnostics.RouteStates());

            if (states.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No route has been used yet. State appears the first time a request resolves to a route."));
                return card;
            }

            foreach (var state in states)
            {
                if (!string.Equals(_serviceFilter, AnyChoice, StringComparison.Ordinal) &&
                    !string.Equals(state.Route.ServiceId, _serviceFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                var policy = _session.Effective?.Resolve(state.Route)?.Policy;
                var circuit = policy != null
                    ? state.CircuitStateAt(policy, DateTime.UtcNow)
                    : NetworkCircuitState.Closed;

                var status = circuit == NetworkCircuitState.Open ? MolcaStatusKind.Error
                    : circuit == NetworkCircuitState.HalfOpen ? MolcaStatusKind.Warning
                    : state.ActiveCount > 0 ? MolcaStatusKind.Ok
                    : MolcaStatusKind.Idle;

                card.Body.Add(NetworkHubUi.ListRow(
                    state.Route.ToString(),
                    $"{state.ActiveCount} in flight · {state.WaitingCount} queued · " +
                    $"{state.ConsecutiveFailures} consecutive failure(s)",
                    status,
                    $"Circuit {circuit}.",
                    selected: false,
                    onClick: () => _session.Navigate(
                        NetworkHubNavigationTarget.Service(state.Route.ServiceId, state.Route.EnvironmentId)),
                    NetworkHubUi.Badge(circuit.ToString())));
            }

            return card;
        }

        private VisualElement BuildTimelineCard(INetworkDiagnostics runtime)
        {
            var card = NetworkHubUi.Card("Recent requests", "Redacted · newest first");

            var records = new List<NetworkRequestDiagnostic>();
            if (runtime != null) records.AddRange(runtime.Snapshot());
            if (_showConsoleTraffic && _session.Console.Diagnostics != null)
                records.AddRange(_session.Console.Diagnostics.Snapshot());

            records.Sort((a, b) => a.CompletedUtc.CompareTo(b.CompletedUtc));

            int shown = 0;
            for (int i = records.Count - 1; i >= 0 && shown < MaxTimelineRows; i--)
            {
                var record = records[i];
                if (!Matches(record)) continue;

                shown++;
                card.Body.Add(NetworkHubUi.ListRow(
                    $"{record.Method} {Molca.Networking.Utils.LogRedaction.RedactUrl(record.Uri)}",
                    $"{record.Route} · {record.CompletedUtc:HH:mm:ss} · " +
                    $"{record.Timings.Total.TotalMilliseconds:F0} ms · {record.Attempts.Count} attempt(s)" +
                    (record.ServedFromCache ? " · cached" : string.Empty) +
                    (string.IsNullOrEmpty(record.CredentialProfileId)
                        ? " · anonymous"
                        : $" · {record.CredentialProfileId}"),
                    record.IsSuccess ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                    record.IsSuccess ? "Succeeded" : record.Message,
                    selected: false,
                    onClick: () => EditorGUIUtility.systemCopyBuffer = record.CorrelationId,
                    NetworkHubUi.Badge(record.StatusCode > 0 ? record.StatusCode.ToString() : "—")));
            }

            if (shown == 0)
            {
                card.Body.Add(NetworkHubUi.Note(records.Count == 0
                    ? "No requests recorded. A policy with request logging disabled records nothing, by design."
                    : "No request matches the current filters."));
            }
            else
            {
                card.SetStatus(MolcaStatusKind.None, $"{shown} shown");
                card.Body.Add(NetworkHubUi.Note("Click a row to copy its correlation ID."));
            }

            return card;
        }

        /// <summary>
        /// Streaming provider state, read through <see cref="INetworkStreamStatus"/>.
        /// </summary>
        /// <remarks>
        /// The interface is what replaced a reflective <c>GetProperty("ConnectionStatus")</c> lookup. The
        /// WebSocket and Socket.IO providers only compile under their own define symbols, so this editor
        /// assembly cannot name their types — but it can test for an interface that is always compiled.
        /// </remarks>
        private VisualElement BuildStreamingCard()
        {
            var card = NetworkHubUi.Card("Streaming sessions", "SSE, WebSocket, and Socket.IO providers");

            if (!Application.isPlaying || !RuntimeManager.IsReady)
            {
                card.Body.Add(NetworkHubUi.Note("Streaming state is live in Play mode."));
                return card;
            }

            int rows = 0;

            // Subsystem-owned sessions first. These are the routed streams: their state lives in the
            // registry, so the row reads live state rather than a field on an asset.
            var diagnostics = RuntimeManager.GetService<INetworkDiagnostics>();
            var sessions = diagnostics?.StreamSessions();

            if (sessions != null && sessions.Count > 0)
            {
                card.Body.Add(NetworkHubUi.Heading("Routed sessions"));

                foreach (var session in sessions)
                {
                    var live = session;
                    rows++;
                    card.Body.Add(NetworkHubUi.ListRow(
                        live.Id,
                        $"{live.Route} · {live.Describe()} · {live.ReceivedCount} message(s)" +
                        (live.IsAuthenticated ? " · authenticated" : " · anonymous"),
                        StatusOf(live),
                        live.Describe(),
                        selected: false,
                        onClick: () => _session.Navigate(NetworkHubNavigationTarget.Provider(live.Id)),
                        NetworkHubUi.Badge(live.Protocol.ToString())));
                }
            }

            // Then providers still running their own connection loop. They report through
            // INetworkStreamStatus, which is a type test rather than the reflection this replaced.
            var manager = RuntimeManager.GetSubsystem<DataManager>();
            var ids = manager?.GetProviderIds();

            if (ids != null)
            {
                var unrouted = new List<VisualElement>();

                foreach (var id in ids)
                {
                    string providerId = id;
                    if (sessions != null && HasSession(sessions, providerId)) continue;
                    if (!(manager.GetProvider(providerId) is INetworkStreamStatus stream)) continue;

                    unrouted.Add(NetworkHubUi.ListRow(
                        providerId,
                        stream.StreamStatus,
                        stream.IsStreamConnected ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                        stream.IsStreamConnected ? "Connected" : "Not connected",
                        selected: false,
                        onClick: () => _session.Navigate(
                            NetworkHubNavigationTarget.Provider(providerId))));
                }

                if (unrouted.Count > 0)
                {
                    card.Body.Add(NetworkHubUi.Heading("Provider-owned connections"));
                    foreach (var row in unrouted)
                    {
                        rows++;
                        card.Body.Add(row);
                    }
                }
            }

            if (rows == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Nothing is streaming. HTTP data providers do not hold a session."));
            }

            return card;
        }

        private static MolcaStatusKind StatusOf(NetworkStreamSession session)
        {
            switch (session.State)
            {
                case NetworkStreamSessionState.Connected: return MolcaStatusKind.Ok;
                case NetworkStreamSessionState.Faulted: return MolcaStatusKind.Error;
                case NetworkStreamSessionState.Reconnecting: return MolcaStatusKind.Warning;
                default: return MolcaStatusKind.Idle;
            }
        }

        private static bool HasSession(IReadOnlyList<NetworkStreamSession> sessions, string id)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(sessions[i].Id, id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion
    }
}
