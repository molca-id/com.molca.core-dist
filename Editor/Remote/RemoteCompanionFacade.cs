using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Automation;
using Molca.Editor.Hub;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Remote
{
    /// <summary>
    /// The single seam through which Molca Remote observes the Editor's two structured surfaces — the Hub
    /// activity rail and the automation kernel. It owns every bound, never hands a kernel or provider
    /// object to the transport, and exposes a <see cref="Changed"/> signal the agent uses to drive
    /// coalesced <c>state.snapshot</c> sends.
    /// </summary>
    /// <remarks>
    /// Editor-only; <see cref="Start"/>, <see cref="Stop"/>, and <see cref="StateBlocks"/> all touch
    /// Editor APIs and must be called on the main thread — the agent marshals through
    /// <see cref="Molca.Editor.Mcp.McpMainThreadDispatcher"/>.
    /// <para>
    /// The facade instantiates its <em>own</em> activity providers rather than borrowing the rail's,
    /// because the rail exists only while the Hub window is open and a companion has to keep working with
    /// the Hub closed. One visible consequence: a chip the user dismisses in the Hub is dismissed on that
    /// provider instance only, so a dismissible result chip can linger in a remote session until it
    /// naturally expires. That is preferred to a remote session that goes blind whenever the Hub closes.
    /// </para>
    /// <para>
    /// Observation is not separately opt-in. Enabling Molca Remote for the project enables it; the
    /// meaningful consent boundary is observation versus control, and control keeps its own gates
    /// (<see cref="MolcaRemoteSettings.AllowAssistant"/>, <see cref="MolcaRemoteSettings.AllowActions"/>,
    /// the automation policy). <see cref="MolcaHubActivity.RemoteSafe"/> still decides which providers'
    /// chips are eligible at all — a source-trust decision, not a user preference.
    /// </para>
    /// </remarks>
    internal static class RemoteCompanionFacade
    {
        /// <summary>Maximum chips projected into one snapshot, in post-<c>Collect</c> rail order.</summary>
        internal const int MaxChips = 12;

        private const int MaxChipIdChars = 64;
        private const int MaxChipLabelChars = 32;
        private const int MaxChipStatusChars = 128;
        private const int MaxWorkspaceIdChars = 32;

        private static List<MolcaHubActivityProvider> _providers;

        /// <summary>
        /// Raised on the main thread whenever a projected activity provider reports a change. The agent
        /// coalesces these into <c>state.snapshot</c> sends; it is not a per-change message channel.
        /// </summary>
        internal static event Action Changed;

        /// <summary>
        /// Instantiates the activity providers and begins observing them. Idempotent — a second call while
        /// already started does nothing, so a reconnect does not double-subscribe.
        /// </summary>
        internal static void Start()
        {
            if (_providers != null) return;
            _providers = MolcaHubActivityRegistry.CreateProviders().ToList();
            foreach (var provider in _providers)
                provider.Changed += OnProviderChanged;
        }

        /// <summary>
        /// Detaches and disposes the activity providers. Called when the session ends, Remote is disabled,
        /// or the domain is about to reload — a provider that observes a static source leaks otherwise.
        /// </summary>
        internal static void Stop()
        {
            if (_providers == null) return;
            foreach (var provider in _providers)
            {
                provider.Changed -= OnProviderChanged;
                try { provider.Dispose(); }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Molca Remote] Activity provider '{provider.GetType().FullName}' threw while disposing: {exception.Message}");
                }
            }
            _providers = null;
        }

        private static void OnProviderChanged() => Changed?.Invoke();

        /// <summary>
        /// The two additive <c>state.snapshot</c> payload blocks. Returned as a pair the caller merges into
        /// the base state so this stays a projection and never owns the envelope.
        /// </summary>
        /// <returns>An object carrying <c>activity</c> and <c>automation</c>.</returns>
        internal static JObject StateBlocks() => new JObject
        {
            ["activity"] = ActivityBlock(),
            ["automation"] = RemoteAutomationProjection.StateBlock(MolcaAutomationKernel.InstanceOrNull)
        };

        /// <summary>
        /// Projects the remote-safe subset of the Hub activity rail. Ordering and dedup come from
        /// <see cref="MolcaHubActivityRegistry.Collect"/> rather than being re-implemented, so the chips a
        /// phone shows are the chips the Hub shows, in the same order.
        /// </summary>
        /// <returns>The bounded chip array (empty when nothing is eligible).</returns>
        internal static JArray ActivityBlock() => ProjectChips(
            MolcaHubActivityRegistry.Collect(_providers ?? (IEnumerable<MolcaHubActivityProvider>)Array.Empty<MolcaHubActivityProvider>()));

        /// <summary>
        /// Filters and bounds an already-collected chip set. Exposed for tests so the projection can be
        /// exercised without instantiating live providers.
        /// </summary>
        /// <param name="chips">Chips in rail order, as <c>Collect</c> returns them.</param>
        /// <returns>The bounded, remote-safe projection.</returns>
        internal static JArray ProjectChips(IReadOnlyList<MolcaHubActivity> chips)
        {
            var array = new JArray();
            if (chips == null) return array;

            // Truncation happens after the RemoteSafe filter: taking the first 12 chips and then dropping
            // the unsafe ones would let one ineligible provider crowd out eligible chips.
            foreach (var chip in chips.Where(c => c != null && c.RemoteSafe && !string.IsNullOrEmpty(c.Id))
                         .Take(MaxChips))
            {
                var o = new JObject
                {
                    ["id"] = Truncate(chip.Id, MaxChipIdChars),
                    ["label"] = Truncate(chip.Label, MaxChipLabelChars),
                    ["status"] = Truncate(chip.Status, MaxChipStatusChars),
                    ["state"] = chip.State.ToString().ToLowerInvariant(),
                    ["order"] = chip.Order
                };
                // A non-finite fraction would serialize as NaN/Infinity and is indistinguishable from
                // "no bar" to a reader, so it is dropped rather than clamped to a misleading 0.
                if (chip.Progress.HasValue && !float.IsNaN(chip.Progress.Value) &&
                    !float.IsInfinity(chip.Progress.Value))
                    o["progress"] = Math.Min(1f, Math.Max(0f, chip.Progress.Value));
                if (!string.IsNullOrEmpty(chip.WorkspaceId))
                    o["workspaceId"] = Truncate(chip.WorkspaceId, MaxWorkspaceIdChars);
                array.Add(o);
            }
            return array;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
