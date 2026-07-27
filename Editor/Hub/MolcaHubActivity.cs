using System;
using System.Collections.Generic;

namespace Molca.Editor.Hub
{
    /// <summary>Visual state of a Hub activity chip; drives its status-dot color.</summary>
    public enum MolcaHubActivityState
    {
        /// <summary>Work in progress (accent dot).</summary>
        Running,
        /// <summary>Neutral / informational (grey dot).</summary>
        Idle,
        /// <summary>Completed successfully (green dot).</summary>
        Ok,
        /// <summary>Completed with warnings (amber dot).</summary>
        Warning,
        /// <summary>Completed with errors (red dot).</summary>
        Error,
    }

    /// <summary>
    /// Immutable snapshot of one chip shown in the Molca Hub's bottom activity rail — an ongoing process
    /// (e.g. a Doctor run) or a piece of live context. Providers return these from
    /// <see cref="MolcaHubActivityProvider.GetActivities"/>; the rail renders one pill per activity.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. A chip is purely a view-model: it holds no
    /// live handles, so a provider rebuilds it cheaply each time its source changes. Clicking a chip runs
    /// <see cref="OnClick"/> if set, otherwise focuses the Hub tab named by <see cref="WorkspaceId"/>.
    /// A chip that supplies <see cref="OnDismiss"/> renders an ✕ affordance. Editor-only; main thread.
    /// </remarks>
    public sealed class MolcaHubActivity
    {
        /// <summary>Stable, unique id for this chip within a run of the rail (used for dedup).</summary>
        public string Id { get; }

        /// <summary>Short bold label (e.g. "Doctor").</summary>
        public string Label { get; }

        /// <summary>Muted status caption (e.g. "12/34 · doc-links" or "5 findings").</summary>
        public string Status { get; }

        /// <summary>Chip state, driving the status-dot color.</summary>
        public MolcaHubActivityState State { get; }

        /// <summary>Determinate progress in [0, 1] to draw as a thin fill, or null for no bar.</summary>
        public float? Progress { get; }

        /// <summary>Hub workspace id to focus when the chip is clicked (used only if <see cref="OnClick"/> is null).</summary>
        public string WorkspaceId { get; }

        /// <summary>Custom click handler; when set it overrides <see cref="WorkspaceId"/> focus.</summary>
        public Action OnClick { get; }

        /// <summary>Optional dismiss handler; when set the chip shows an ✕ button that invokes it.</summary>
        public Action OnDismiss { get; }

        /// <summary>Sort order among chips (ascending; ties broken by <see cref="Id"/>).</summary>
        public int Order { get; }

        /// <summary>
        /// Whether this chip may be projected into a Molca Remote session snapshot. Defaults to
        /// <c>false</c>: <see cref="Status"/> is author-controlled free text, and a chip from a provider
        /// Molca does not own must not leave the machine until someone has reviewed what that text can
        /// contain. Core's own providers opt in; third-party and integration providers do not.
        /// </summary>
        /// <remarks>
        /// This is a source-trust decision, not a user preference — the user's consent to remote
        /// observation is the decision to enable Molca Remote for the project. <see cref="OnClick"/> and
        /// <see cref="OnDismiss"/> are never serialized regardless of this flag, and
        /// <see cref="WorkspaceId"/> travels only as a labelling hint, never as an invocation target.
        /// </remarks>
        public bool RemoteSafe { get; }

        /// <summary>Creates an activity chip descriptor.</summary>
        /// <param name="id">Stable unique id.</param>
        /// <param name="label">Short bold label.</param>
        /// <param name="status">Muted status caption.</param>
        /// <param name="state">Chip state (dot color).</param>
        /// <param name="progress">Determinate progress [0,1], or null for no bar.</param>
        /// <param name="workspaceId">Hub tab to focus on click (when <paramref name="onClick"/> is null).</param>
        /// <param name="onClick">Custom click handler; overrides <paramref name="workspaceId"/>.</param>
        /// <param name="onDismiss">Optional dismiss handler; shows an ✕ when set.</param>
        /// <param name="order">Sort order among chips.</param>
        /// <param name="remoteSafe">
        /// Whether the chip may be projected to a Molca Remote session. Defaults to <c>false</c>; see
        /// <see cref="RemoteSafe"/> for why the default is closed.
        /// </param>
        public MolcaHubActivity(
            string id, string label, string status,
            MolcaHubActivityState state = MolcaHubActivityState.Running,
            float? progress = null, string workspaceId = null,
            Action onClick = null, Action onDismiss = null, int order = 0,
            bool remoteSafe = false)
        {
            Id = id;
            Label = label;
            Status = status;
            State = state;
            Progress = progress;
            WorkspaceId = workspaceId;
            OnClick = onClick;
            OnDismiss = onDismiss;
            Order = order;
            RemoteSafe = remoteSafe;
        }
    }

    /// <summary>
    /// Editor-only seam for contributing live chips to the Molca Hub's bottom activity rail. Subclass,
    /// observe your source in the constructor, call <see cref="NotifyChanged"/> whenever your activities
    /// change, and return the current snapshot from <see cref="GetActivities"/>.
    /// </summary>
    /// <remarks>
    /// Non-abstract subclasses are discovered automatically via <c>TypeCache</c> (see
    /// <see cref="MolcaHubActivityRegistry"/>) — no Core edit, no registration call — and must have a public
    /// parameterless constructor. Unlike <see cref="MolcaHubWorkspaceProvider"/> (a stateless list), an
    /// activity provider is a <em>stateful observer</em>: the rail creates one instance, subscribes to
    /// <see cref="Changed"/>, and <see cref="IDisposable.Dispose"/>s it when the Hub closes — so a provider
    /// that subscribes to a global/static source must unsubscribe in <see cref="Dispose"/> to avoid leaking.
    /// All members are touched on the main thread.
    /// </remarks>
    public abstract class MolcaHubActivityProvider : IDisposable
    {
        /// <summary>Raised by the provider when its activities change, prompting the rail to refresh.</summary>
        public event Action Changed;

        /// <summary>Signals the rail that this provider's activities changed.</summary>
        protected void NotifyChanged() => Changed?.Invoke();

        /// <summary>Returns this provider's current activity chips (possibly empty; never null).</summary>
        public abstract IEnumerable<MolcaHubActivity> GetActivities();

        /// <summary>Releases any subscriptions to the observed source. Base implementation does nothing.</summary>
        public virtual void Dispose() { }
    }
}
