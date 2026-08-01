using System;
using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// One networking operation long enough to be worth a chip in the Hub's activity rail.
    /// </summary>
    internal sealed class NetworkActivity
    {
        /// <summary>Stable chip id.</summary>
        public string Id { get; }

        /// <summary>Short label, e.g. <c>Network</c>.</summary>
        public string Label { get; }

        /// <summary>What the operation is doing right now.</summary>
        public string Status { get; internal set; }

        /// <summary>Determinate progress in [0,1], or <c>null</c> for an indeterminate operation.</summary>
        public float? Progress { get; internal set; }

        /// <summary>Where clicking the chip should land.</summary>
        public NetworkHubNavigationTarget Target { get; }

        /// <summary>Cancels the operation, or <c>null</c> when it cannot be cancelled.</summary>
        public Action Cancel { get; }

        internal NetworkActivity(
            string id, string label, string status, NetworkHubNavigationTarget target, Action cancel)
        {
            Id = id;
            Label = label;
            Status = status;
            Target = target;
            Cancel = cancel;
        }
    }

    /// <summary>
    /// The networking operations currently running, for the Hub's bottom activity rail.
    /// </summary>
    /// <remarks>
    /// Static because the rail's <see cref="MolcaHubActivityProvider"/> is constructed by the Hub and
    /// has no route to a workspace instance, while the operations themselves start inside views,
    /// services, and menu items. This is a registry of in-flight work, not a UI controller: it owns no
    /// element, renders nothing, and holds nothing after an operation ends.
    /// <para>
    /// <b>Only operations a user can outwait belong here.</b> Validating a catalog and resolving a route
    /// are microseconds; registering them would produce a chip that flickers and trains people to ignore
    /// the rail. The entries are the legacy scan, migration, a console send, and a diagnostics export.
    /// </para>
    /// </remarks>
    internal static class NetworkActivityTracker
    {
        private static readonly List<NetworkActivity> Activities = new List<NetworkActivity>();

        /// <summary>Raised whenever the set of activities, or one of their captions, changes.</summary>
        public static event Action Changed;

        /// <summary>The activities currently running, in start order.</summary>
        public static IReadOnlyList<NetworkActivity> Active => Activities;

        /// <summary>
        /// Starts tracking an operation.
        /// </summary>
        /// <param name="id">Stable chip id; starting a second operation with the same id replaces the first.</param>
        /// <param name="label">Short chip label.</param>
        /// <param name="status">Initial caption.</param>
        /// <param name="target">Where the chip navigates.</param>
        /// <param name="cancel">Cancels the operation, or <c>null</c> when it cannot be cancelled.</param>
        /// <returns>A scope; dispose it (or call <see cref="Scope.Complete"/>) to remove the chip.</returns>
        public static Scope Begin(
            string id,
            string label,
            string status,
            NetworkHubNavigationTarget target = default,
            Action cancel = null)
        {
            Remove(id);

            var activity = new NetworkActivity(id, label, status, target, cancel);
            Activities.Add(activity);
            Changed?.Invoke();

            return new Scope(activity);
        }

        /// <summary>Removes an activity by id. No-op when absent.</summary>
        /// <param name="id">The id to remove.</param>
        internal static void Remove(string id)
        {
            for (int i = Activities.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Activities[i].Id, id, StringComparison.Ordinal))
                    Activities.RemoveAt(i);
            }
        }

        /// <summary>Drops every activity. For test isolation and Hub teardown.</summary>
        internal static void Clear()
        {
            if (Activities.Count == 0) return;

            Activities.Clear();
            Changed?.Invoke();
        }

        /// <summary>
        /// A running operation's handle. Disposing it removes the chip.
        /// </summary>
        /// <remarks>
        /// <see cref="IDisposable"/> so a <c>using</c> guarantees the chip is removed even when the
        /// operation throws — a rail that keeps showing work that already failed is worse than no rail.
        /// </remarks>
        public sealed class Scope : IDisposable
        {
            private readonly NetworkActivity _activity;
            private bool _ended;

            internal Scope(NetworkActivity activity) => _activity = activity;

            /// <summary>Updates the caption and progress.</summary>
            /// <param name="status">The new caption, or <c>null</c> to keep the current one.</param>
            /// <param name="progress">Progress in [0,1], or <c>null</c> for indeterminate.</param>
            public void Report(string status = null, float? progress = null)
            {
                if (_ended) return;

                if (!string.IsNullOrEmpty(status)) _activity.Status = status;
                _activity.Progress = progress;
                Changed?.Invoke();
            }

            /// <summary>Ends the operation and removes its chip.</summary>
            public void Complete() => Dispose();

            /// <inheritdoc />
            public void Dispose()
            {
                if (_ended) return;

                _ended = true;
                Remove(_activity.Id);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Projects <see cref="NetworkActivityTracker"/> into the Hub's bottom activity rail.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Networking/Hub/</c>. Discovered via <c>TypeCache</c>;
    /// the rail constructs one, subscribes to <see cref="MolcaHubActivityProvider.Changed"/>, and
    /// disposes it on Hub teardown — so the static subscription is detached in <see cref="Dispose"/>.
    /// <para>
    /// Chips are <b>not</b> remote-safe. A caption here names a route and can name a host, and a
    /// developer probing a staging service from the request console has not agreed to project that
    /// destination into a shared session.
    /// </para>
    /// </remarks>
    internal sealed class NetworkHubActivityProvider : MolcaHubActivityProvider
    {
        /// <summary>Subscribes to the tracker.</summary>
        public NetworkHubActivityProvider() => NetworkActivityTracker.Changed += NotifyChanged;

        /// <inheritdoc />
        public override void Dispose() => NetworkActivityTracker.Changed -= NotifyChanged;

        /// <inheritdoc />
        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            foreach (var activity in NetworkActivityTracker.Active)
                yield return Chip(activity);
        }

        /// <summary>
        /// The chip for one activity.
        /// </summary>
        /// <param name="activity">The activity to render.</param>
        /// <returns>The chip.</returns>
        /// <remarks>
        /// Pure and internal so the mapping — including "a cancellable operation gets the ✕" — is
        /// testable without a Hub window.
        /// </remarks>
        internal static MolcaHubActivity Chip(NetworkActivity activity) =>
            new MolcaHubActivity(
                id: "network." + activity.Id,
                label: activity.Label,
                status: activity.Status,
                state: MolcaHubActivityState.Running,
                progress: activity.Progress,
                workspaceId: NetworkHubWorkspaceProvider.WorkspaceId,
                onClick: activity.Target.IsEmpty
                    ? (Action)null
                    : () => NetworkHubWorkspace.Open(activity.Target),
                // The ✕ cancels rather than hides: a chip for work still running is not something to
                // dismiss, and cancelling is the action a user actually wants from the rail.
                onDismiss: activity.Cancel,
                remoteSafe: false);
    }
}
