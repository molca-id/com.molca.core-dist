using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Networking;
using Molca.Networking.Configuration;
using Molca.Networking.Data;
using Molca.Networking.Diagnostics;
using Molca.Networking.Streaming;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Streaming provider assets, authored against the same service/environment model HTTP uses.
    /// </summary>
    /// <remarks>
    /// The view that replaces per-asset URL authoring (plan §7.10). A provider names a service, an
    /// environment strategy, and a relative path; the origin comes from the catalog binding, and the
    /// resolved preview here uses the production resolver, so what this pane shows and what the session
    /// connects to cannot disagree.
    /// <para>
    /// Providers are found and edited through <c>SerializedObject</c> by <em>property name</em>, never by
    /// type. The WebSocket and Socket.IO providers compile only when their own dependency is present, so
    /// an editor pane that named those types would fail to build in a project that has neither — and the
    /// protocol-specific fields those assets own stay in the Inspector, where they belong.
    /// </para>
    /// </remarks>
    internal sealed class NetworkProvidersView : VisualElement
    {
        /// <summary>The serialized field a routed provider stores its route in.</summary>
        private const string RouteProperty = "_route";

        private const long RefreshIntervalMs = 1000;

        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;
        private readonly List<ProviderEntry> _providers = new List<ProviderEntry>();

        private IVisualElementScheduledItem _poll;

        /// <summary>One provider asset and what the workspace knows about it.</summary>
        private sealed class ProviderEntry
        {
            public DataProvider Asset;
            public string ProviderId;
            public NetworkProtocols Protocol;
            public SerializedObject Serialized;
            public SerializedProperty Route;

            /// <summary>Whether this provider connects through the catalog.</summary>
            public bool IsRouted =>
                Route != null &&
                !string.IsNullOrEmpty(Route.FindPropertyRelative("_serviceId")?.stringValue);
        }

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkProvidersView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            _split = new NetworkHubUi.Split();
            Add(_split);

            Reload();

            // Live session state only changes in Play mode; the poll is what keeps the status dots
            // honest without a per-frame cost, and it stops on detach.
            RegisterCallback<AttachToPanelEvent>(_ =>
                _poll = schedule.Execute(RefreshDetail).Every(RefreshIntervalMs));
            RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause());
        }

        #region Discovery

        private void Reload()
        {
            _providers.Clear();

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(DataProvider)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<DataProvider>(path);
                if (asset == null) continue;

                var protocol = ProtocolOf(asset);
                if (protocol == NetworkProtocols.Http || protocol == NetworkProtocols.None)
                    continue;

                var serialized = new SerializedObject(asset);
                _providers.Add(new ProviderEntry
                {
                    Asset = asset,
                    ProviderId = string.IsNullOrEmpty(asset.ProviderId) ? asset.name : asset.ProviderId,
                    Protocol = protocol,
                    Serialized = serialized,
                    Route = serialized.FindProperty(RouteProperty),
                });
            }

            _providers.Sort((a, b) => string.Compare(a.ProviderId, b.ProviderId, StringComparison.Ordinal));

            RefreshMaster();
            RefreshDetail();
        }

        /// <summary>
        /// The protocol a provider speaks, from its type name.
        /// </summary>
        /// <param name="provider">The provider asset.</param>
        /// <remarks>
        /// By name rather than by <c>is</c>, for the same reason the fields are read by name: the two
        /// optional provider types may not exist in this project's compilation.
        /// </remarks>
        private static NetworkProtocols ProtocolOf(DataProvider provider)
        {
            switch (provider.GetType().Name)
            {
                case "SSEProvider": return NetworkProtocols.ServerSentEvents;
                case "WebSocketDataProvider": return NetworkProtocols.WebSocket;
                case "SocketIODataProvider": return NetworkProtocols.SocketIO;
                case "HttpDataProvider": return NetworkProtocols.Http;
                default: return NetworkProtocols.None;
            }
        }

        #endregion

        #region Master

        private void RefreshMaster()
        {
            _split.Master.Clear();

            if (_providers.Count == 0)
            {
                _split.Master.Add(NetworkHubUi.Note(
                    "This project has no SSE, WebSocket, or Socket.IO provider assets. Create one from " +
                    "Assets ▸ Create ▸ Molca ▸ Networking."));
                return;
            }

            string selected = _session.SelectionFor(NetworkHubViews.Providers);

            foreach (var provider in _providers)
            {
                var entry = provider;
                _split.Master.Add(NetworkHubUi.ListRow(
                    entry.ProviderId,
                    entry.IsRouted ? RouteSummary(entry) : "Direct URL",
                    StatusOf(entry),
                    entry.IsRouted ? "Routed through the catalog." : "Not routed through the catalog.",
                    string.Equals(selected, entry.ProviderId, StringComparison.Ordinal),
                    () =>
                    {
                        _session.SetSelection(NetworkHubViews.Providers, entry.ProviderId);
                        RefreshMaster();
                        RefreshDetail();
                    },
                    NetworkHubUi.Badge(entry.Protocol.ToString())));
            }
        }

        /// <summary>
        /// The status a provider row shows.
        /// </summary>
        /// <remarks>
        /// A provider on a direct URL is <see cref="MolcaStatusKind.Warning"/>, not neutral: it is
        /// outside every rule the catalog enforces — allowed hosts, production schemes, credential
        /// scope — and that is worth seeing at a glance rather than only after opening it.
        /// </remarks>
        private MolcaStatusKind StatusOf(ProviderEntry entry)
        {
            var live = LiveSessionFor(entry);
            if (live != null)
            {
                return live.State == NetworkStreamSessionState.Faulted ? MolcaStatusKind.Error
                    : live.IsStreamConnected ? MolcaStatusKind.Ok
                    : MolcaStatusKind.Idle;
            }

            if (!entry.IsRouted)
                return MolcaStatusKind.Warning;

            return Resolve(entry)?.Resolves == true ? MolcaStatusKind.Ok : MolcaStatusKind.Error;
        }

        private string RouteSummary(ProviderEntry entry)
        {
            string service = entry.Route.FindPropertyRelative("_serviceId")?.stringValue ?? string.Empty;
            string path = entry.Route.FindPropertyRelative("_relativePath")?.stringValue ?? string.Empty;
            return string.IsNullOrEmpty(path) ? service : $"{service}/{path}";
        }

        #endregion

        #region Detail

        private void RefreshDetail()
        {
            _split.Detail.Clear();

            var entry = Selected();
            if (entry == null)
            {
                _split.Detail.Add(NetworkHubUi.Note("Select a provider."));
                return;
            }

            entry.Serialized.Update();

            _split.Detail.Add(BuildIdentity(entry));
            _split.Detail.Add(BuildRouteCard(entry));
            _split.Detail.Add(BuildResolvedCard(entry));
            _split.Detail.Add(BuildSessionCard(entry));
        }

        private VisualElement BuildIdentity(ProviderEntry entry)
        {
            var card = NetworkHubUi.Card(entry.ProviderId, entry.Protocol.ToString());

            card.Body.Add(NetworkHubUi.Field("Asset", AssetDatabase.GetAssetPath(entry.Asset)));
            card.Body.Add(NetworkHubUi.Field("Type", entry.Asset.GetType().Name));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Select asset", () =>
                {
                    Selection.activeObject = entry.Asset;
                    EditorGUIUtility.PingObject(entry.Asset);
                })));

            return card;
        }

        private VisualElement BuildRouteCard(ProviderEntry entry)
        {
            var card = NetworkHubUi.Card(
                "Route",
                "Service, environment strategy, and relative path",
                entry.IsRouted ? MolcaStatusKind.Ok : MolcaStatusKind.Warning,
                entry.IsRouted ? "Routed" : "Direct URL");

            if (entry.Route == null)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "This provider type has no route field, so it cannot be authored against the catalog " +
                    "from here."));
                return card;
            }

            // Bound through SerializedObject so Undo, multi-scene edits, and the Inspector all agree.
            var field = new PropertyField(entry.Route);
            field.Bind(entry.Serialized);
            field.RegisterValueChangeCallback(_ =>
            {
                entry.Serialized.ApplyModifiedProperties();
                RefreshMaster();
                RefreshDetail();
            });
            card.Body.Add(field);

            if (!entry.IsRouted)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Until a service is set, this provider connects to the URL authored on the asset. That " +
                    "URL is outside the catalog, so allowed hosts, production scheme rules, and credential " +
                    "scope do not apply to it."));
            }

            return card;
        }

        private VisualElement BuildResolvedCard(ProviderEntry entry)
        {
            var card = NetworkHubUi.Card("Resolved", $"Under the {_session.PreviewEnvironmentId} preview");

            if (!entry.IsRouted)
            {
                card.Body.Add(NetworkHubUi.Note("Set a service to see where this provider will connect."));
                return card;
            }

            var resolved = Resolve(entry);
            if (resolved == null || !resolved.Resolves)
            {
                card.SetStatus(MolcaStatusKind.Error, "Does not resolve");
                card.Body.Add(NetworkHubUi.Note(resolved?.FailureReason ??
                    "The route does not resolve under the previewed environment."));
                return card;
            }

            card.SetStatus(MolcaStatusKind.Ok, "Resolves");
            card.Body.Add(NetworkHubUi.Field("URI", resolved.ResolvedUri));
            card.Body.Add(NetworkHubUi.Field("Origin", resolved.Origin));
            card.Body.Add(NetworkHubUi.Field(
                "Credential",
                string.IsNullOrEmpty(resolved.CredentialProfileId)
                    ? "anonymous"
                    : resolved.CredentialAppliesToHost
                        ? $"{resolved.CredentialProfileId} (in scope)"
                        : $"{resolved.CredentialProfileId} (out of scope — connects anonymously)",
                "The profile name only. A stream never displays or exports a credential value."));

            var policy = resolved.Policy;
            if (policy != null)
            {
                card.Body.Add(NetworkHubUi.EffectiveField(
                    "Encrypted transport required",
                    policy.RequireSecureTransport.Value ? "yes" : "no",
                    policy.RequireSecureTransport.Source,
                    "Resolves tighten-only. A production environment forces it on regardless of the " +
                    "provider's own secure-connection setting."));
            }

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Open service", () => _session.Navigate(
                    NetworkHubNavigationTarget.Service(
                        resolved.Route.ServiceId, _session.PreviewEnvironmentId)))));

            return card;
        }

        /// <summary>
        /// The live session for a provider, in Play mode.
        /// </summary>
        /// <remarks>
        /// Read from <see cref="INetworkDiagnostics.StreamSessions"/>, which the subsystem populates from
        /// the registry that owns them. The provider asset holds no session state to read — that is the
        /// point of moving it (plan §6.7).
        /// </remarks>
        private VisualElement BuildSessionCard(ProviderEntry entry)
        {
            var live = LiveSessionFor(entry);

            var card = NetworkHubUi.Card(
                "Live session",
                "Subsystem-owned",
                live == null ? MolcaStatusKind.Idle
                    : live.State == NetworkStreamSessionState.Faulted ? MolcaStatusKind.Error
                    : live.IsStreamConnected ? MolcaStatusKind.Ok
                    : MolcaStatusKind.Warning,
                live?.Describe() ?? (Application.isPlaying ? "No session" : "Not in Play mode"));

            if (live == null)
            {
                card.Body.Add(NetworkHubUi.Note(Application.isPlaying
                    ? "This provider has no live session. A routed provider opens one when it activates."
                    : "Enter Play mode to see connection state, attempts, and received counts. None of it " +
                      "is stored on the asset."));
                return card;
            }

            card.Body.Add(NetworkHubUi.Field("State", live.State.ToString()));
            card.Body.Add(NetworkHubUi.Field("Attempts", live.AttemptCount.ToString()));
            card.Body.Add(NetworkHubUi.Field("Received", live.ReceivedCount.ToString()));
            card.Body.Add(NetworkHubUi.Field("Connected since",
                live.ConnectedSinceUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—"));
            card.Body.Add(NetworkHubUi.Field("Authenticated", live.IsAuthenticated ? "yes" : "no"));

            if (!string.IsNullOrEmpty(live.LastError))
                card.Body.Add(NetworkHubUi.Field("Last error", live.LastError));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Stop session", () => live.Stop())));

            return card;
        }

        #endregion

        #region Helpers

        private ProviderEntry Selected()
        {
            string selected = _session.SelectionFor(NetworkHubViews.Providers);

            foreach (var entry in _providers)
            {
                if (string.Equals(entry.ProviderId, selected, StringComparison.Ordinal)) return entry;
            }

            return _providers.Count > 0 ? _providers[0] : null;
        }

        private Authoring.NetworkEffectiveRoute Resolve(ProviderEntry entry)
        {
            if (_session.Effective == null || entry.Route == null) return null;

            string serviceId = entry.Route.FindPropertyRelative("_serviceId")?.stringValue;
            if (string.IsNullOrEmpty(serviceId)) return null;

            var strategy = (NetworkEnvironmentStrategy)(entry.Route
                .FindPropertyRelative("_environmentStrategy")?.enumValueIndex ?? 0);

            // An explicit strategy previews under the environment the asset names; a catalog-default one
            // previews under the workspace's preview environment, which is the closest an authoring
            // surface can get to "whatever the runtime default will be".
            string environmentId = strategy == NetworkEnvironmentStrategy.Explicit
                ? entry.Route.FindPropertyRelative("_environmentId")?.stringValue
                : _session.PreviewEnvironmentId;

            if (string.IsNullOrEmpty(environmentId)) return null;

            string endpointId = entry.Route.FindPropertyRelative("_endpointId")?.stringValue;
            return _session.Effective.Resolve(
                new Molca.Networking.Routing.NetworkRouteKey(environmentId, serviceId),
                entry.Protocol,
                string.IsNullOrEmpty(endpointId) ? null : endpointId);
        }

        private static NetworkStreamSession LiveSessionFor(ProviderEntry entry)
        {
            if (!Application.isPlaying || !RuntimeManager.IsReady) return null;

            var diagnostics = RuntimeManager.GetService<INetworkDiagnostics>();
            if (diagnostics == null) return null;

            foreach (var session in diagnostics.StreamSessions())
            {
                if (string.Equals(session.Id, entry.ProviderId, StringComparison.Ordinal)) return session;
            }

            return null;
        }

        #endregion
    }
}
