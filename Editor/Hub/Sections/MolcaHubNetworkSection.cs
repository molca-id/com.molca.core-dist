using System.Collections.Generic;
using Molca;
using Molca.Editor.UI.Components;
using Molca.Networking.Data;
using Molca.Networking.Http;
using Molca.Networking.Utils;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Read-only Network telemetry section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Network rail section is active.
    /// Surfaces live HTTP client counters, the <b>redacted</b> request history, cache size, and
    /// per-streaming-provider connection state. Live data only exists in Play mode while the
    /// <see cref="RuntimeManager"/> is ready; otherwise an idle notice is shown. The section never
    /// writes — it polls on a light timer and renders. URLs and headers are routed through
    /// <see cref="LogRedaction"/> via <see cref="Molca.Networking.Http.Models.HttpRequestContext.RedactedUrl"/>
    /// so credentials never reach the Hub.
    /// </remarks>
    internal sealed class MolcaHubNetworkSection : VisualElement
    {
        // Light poll so live counters/history stay current without a per-frame cost.
        private const long RefreshIntervalMs = 1000;
        private const int MaxHistoryRows = 25;

        private readonly VisualElement _content;
        private IVisualElementScheduledItem _poll;

        internal MolcaHubNetworkSection()
        {
            AddToClassList("molca-hub-network-section");

            _content = new VisualElement();
            Add(_content);

            Rebuild();

            // Poll only while attached to a panel; stop when the section is torn down.
            RegisterCallback<AttachToPanelEvent>(_ => _poll = schedule.Execute(Rebuild).Every(RefreshIntervalMs));
            RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause());
        }

        private void Rebuild()
        {
            _content.Clear();

            if (!Application.isPlaying || !RuntimeManager.IsReady)
            {
                var card = new MolcaSectionCard(
                    "Network Telemetry",
                    "Live view available in Play mode",
                    MolcaStatusKind.Idle,
                    "Runtime not active");
                var notice = new Label(
                    "Enter Play mode with an initialized RuntimeManager to view live request counts, redacted history, cache size, and streaming-provider connection state.");
                notice.AddToClassList("molca-hub-muted");
                card.Body.Add(notice);
                _content.Add(card);
                return;
            }

            BuildHttpCard();
            BuildHistoryCard();
            BuildCacheCard();
            BuildProvidersCard();
        }

        private void BuildHttpCard()
        {
            var http = RuntimeManager.GetService<IHttpClient>();
            var card = new MolcaSectionCard(
                "HTTP Client",
                http != null ? http.BaseUrl : null,
                http != null ? MolcaStatusKind.Ok : MolcaStatusKind.Warning,
                http != null ? "Active" : "Unavailable");

            if (http == null)
            {
                var msg = new Label("IHttpClient is not registered.");
                msg.AddToClassList("molca-hub-muted");
                card.Body.Add(msg);
                _content.Add(card);
                return;
            }

            card.Body.Add(MetricRow("Active requests", http.ActiveRequestCount.ToString()));
            card.Body.Add(MetricRow("Max concurrent", http.MaxConcurrentRequests.ToString()));
            card.Body.Add(MetricRow("History entries", http.RequestHistory.Count.ToString()));
            _content.Add(card);
        }

        private void BuildHistoryCard()
        {
            var http = RuntimeManager.GetService<IHttpClient>();
            var card = new MolcaSectionCard("Recent Requests", "Redacted — URL · status · latency", MolcaStatusKind.None);

            var history = http?.RequestHistory;
            if (history == null || history.Count == 0)
            {
                var empty = new Label("No requests recorded yet.");
                empty.AddToClassList("molca-hub-muted");
                card.Body.Add(empty);
                _content.Add(card);
                return;
            }

            // Newest first, capped so a long session doesn't flood the panel.
            int start = history.Count - 1;
            int shown = 0;
            for (int i = start; i >= 0 && shown < MaxHistoryRows; i--, shown++)
            {
                var ctx = history[i];
                if (ctx == null) continue;

                var row = new VisualElement();
                row.AddToClassList("molca-hub-network-row");

                var dot = new VisualElement();
                dot.AddToClassList("molca-hub-status-dot");
                bool ok = ctx.response != null && ctx.response.isSuccess;
                dot.AddToClassList(ok ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--error");
                row.Add(dot);

                var url = new Label($"{ctx.request?.method} {ctx.RedactedUrl}");
                url.AddToClassList("molca-hub-network-row__url");
                row.Add(url);

                int status = ctx.response?.statusCode ?? 0;
                var statusLabel = new Label(status > 0 ? status.ToString() : (ctx.wasCancelled ? "cancelled" : "—"));
                statusLabel.AddToClassList("molca-hub-network-row__status");
                row.Add(statusLabel);

                float latency = ctx.response?.responseTime ?? 0f;
                var latencyLabel = new Label(latency > 0f ? $"{latency * 1000f:F0} ms" : "—");
                latencyLabel.AddToClassList("molca-hub-network-row__latency");
                row.Add(latencyLabel);

                card.Body.Add(row);
            }

            _content.Add(card);
        }

        private void BuildCacheCard()
        {
            var cache = RuntimeManager.GetService<ICacheService>();
            var card = new MolcaSectionCard(
                "Network Cache",
                null,
                cache != null && cache.IsReady ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                cache != null && cache.IsReady ? "Ready" : "Not ready");

            if (cache == null)
            {
                var msg = new Label("ICacheService is not registered.");
                msg.AddToClassList("molca-hub-muted");
                card.Body.Add(msg);
                _content.Add(card);
                return;
            }

            card.Body.Add(MetricRow("Cache size", FormatBytes(cache.CacheSize)));
            _content.Add(card);
        }

        private void BuildProvidersCard()
        {
            var manager = RuntimeManager.GetSubsystem<DataManager>();
            var card = new MolcaSectionCard("Streaming Providers", "Connection status", MolcaStatusKind.None);

            if (manager == null)
            {
                var msg = new Label("DataManager is not active.");
                msg.AddToClassList("molca-hub-muted");
                card.Body.Add(msg);
                _content.Add(card);
                return;
            }

            var ids = manager.GetProviderIds();
            if (ids == null || ids.Count == 0)
            {
                var empty = new Label("No data providers registered.");
                empty.AddToClassList("molca-hub-muted");
                card.Body.Add(empty);
                _content.Add(card);
                return;
            }

            foreach (var id in ids)
            {
                var provider = manager.GetProvider(id);
                bool active = manager.IsProviderActive(id);

                var row = new VisualElement();
                row.AddToClassList("molca-hub-network-row");

                var dot = new VisualElement();
                dot.AddToClassList("molca-hub-status-dot");
                dot.AddToClassList(active ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
                row.Add(dot);

                var name = new Label(id);
                name.AddToClassList("molca-hub-network-row__url");
                row.Add(name);

                var status = new Label(ReadConnectionStatus(provider) ?? (active ? "active" : "inactive"));
                status.AddToClassList("molca-hub-network-row__status");
                row.Add(status);

                card.Body.Add(row);
            }

            _content.Add(card);
        }

        // Streaming providers (WebSocket/SocketIO) expose a string ConnectionStatus, but the
        // base DataProvider does not — read it reflectively so the section stays decoupled
        // from the optional MOLCA_WEBSOCKET/MOLCA_SOCKETIO provider types.
        private static string ReadConnectionStatus(object provider)
        {
            if (provider == null) return null;
            var prop = provider.GetType().GetProperty("ConnectionStatus");
            return prop != null && prop.PropertyType == typeof(string)
                ? prop.GetValue(provider) as string
                : null;
        }

        private static VisualElement MetricRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-network-metric");

            var key = new Label(label);
            key.AddToClassList("molca-hub-network-metric__label");
            row.Add(key);

            var val = new Label(value);
            val.AddToClassList("molca-hub-network-metric__value");
            row.Add(val);

            return row;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F1} GB";
        }
    }
}
