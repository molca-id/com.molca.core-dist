using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Abstract base class for all editor-side service integrations (ClickUp, GitHub, etc.).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/</c>.
    /// Base class: <see cref="ScriptableObject"/>.
    /// Registration: add the provider asset to <see cref="IntegrationSettings"/>' provider list in
    /// the Inspector; it is then discovered by the registry and rendered as a card by the Molca Hub
    /// Integrations section. This mirrors the <c>NotificationProvider</c> + <c>NotificationSettings</c>
    /// pair one layer up.
    /// <para>
    /// Integrations are <b>editor-only</b>. They never run as a <c>RuntimeSubsystem</c>, never mutate a
    /// runtime ScriptableObject at play time, and perform all network I/O through editor HTTP helpers
    /// (e.g. <c>EditorHttpClient</c>). Secret credentials must live in <see cref="IntegrationCredentialStore"/>
    /// (backed by <c>EditorUserSettings</c>), never in serialized fields on the asset.
    /// </para>
    /// </remarks>
    public abstract class IntegrationProvider : ScriptableObject
    {
        [SerializeField] protected bool enabled = true;

        /// <summary>Stable per-provider key used to namespace stored credentials and settings.</summary>
        /// <remarks>Defaults to the type's full name; override only if a shorter stable key is preferred.</remarks>
        public virtual string ProviderKey => GetType().FullName;

        /// <summary>Human-readable name shown on the integration card (e.g. "ClickUp").</summary>
        public abstract string DisplayName { get; }

        /// <summary>Short one-line description of what the integration does, shown under the name.</summary>
        public abstract string Description { get; }

        /// <summary>Single-character glyph rendered in the card icon badge (e.g. "C").</summary>
        public abstract string Glyph { get; }

        /// <summary>Badge color for the card glyph, authored as <c>"rgb(r, g, b)"</c>.</summary>
        public abstract string GlyphColor { get; }

        /// <summary>Whether the user has toggled this integration on.</summary>
        public bool Enabled => enabled;

        /// <summary>
        /// Whether a credential (token/webhook) is stored for this provider.
        /// </summary>
        /// <remarks>
        /// All built-in providers persist their secret through <see cref="IntegrationCredentialStore"/> keyed
        /// on <see cref="ProviderKey"/>, so this is a uniform "is it configured" signal for validation and
        /// status rendering without exposing the secret itself.
        /// </remarks>
        public bool HasCredential => IntegrationCredentialStore.HasToken(ProviderKey);

        /// <summary>
        /// Whether the integration is currently connected and ready to use.
        /// </summary>
        /// <remarks>
        /// Implementations should derive this from cached connection state (e.g. a validated token),
        /// not perform a network call — this is read on the card render path.
        /// </remarks>
        public abstract bool IsConnected { get; }

        /// <summary>Short status line shown next to the connection dot on the card.</summary>
        public abstract string StatusMessage { get; }

        /// <summary>
        /// Validates credentials against the remote service and updates cached connection state.
        /// </summary>
        /// <param name="cancellationToken">Cancels the connection attempt; cancellation is not an error.</param>
        /// <returns><c>true</c> if the integration is connected after the attempt.</returns>
        public abstract Awaitable<bool> ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears cached connection state and any stored credentials for this provider.
        /// </summary>
        public abstract void Disconnect();

        // ---- Activity push (Sprint 29 routing seam) -------------------------------------------------

        /// <summary>
        /// Whether this provider wants build activity pushed to it. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <see cref="IntegrationActivityRouter"/> checks this before assembling/sending a payload, so a
        /// provider that does not push build activity costs nothing. Override to gate on enabled + opt-in +
        /// credentials, mirroring the connection-independent semantics: the remote API validates the token.
        /// </remarks>
        public virtual bool ShouldPushOnBuild => false;

        /// <summary>Whether this provider wants release (version-bump) activity pushed to it. Default <c>false</c>.</summary>
        public virtual bool ShouldPushOnRelease => false;

        /// <summary>
        /// Pushes a completed-build activity to the remote service. Default is a no-op.
        /// </summary>
        /// <param name="activity">The build snapshot the router assembled once for all providers.</param>
        /// <param name="cancellationToken">Cancels the push; cancellation is not an error.</param>
        /// <remarks>Only invoked when <see cref="ShouldPushOnBuild"/> is <c>true</c>. Override to send.</remarks>
        public virtual Awaitable PushBuildActivityAsync(BuildActivity activity, CancellationToken cancellationToken = default)
            => Completed();

        /// <summary>
        /// Pushes a cut-release activity to the remote service. Default is a no-op.
        /// </summary>
        /// <param name="activity">The release snapshot (notes already composed) the router assembled.</param>
        /// <param name="cancellationToken">Cancels the push; cancellation is not an error.</param>
        /// <remarks>Only invoked when <see cref="ShouldPushOnRelease"/> is <c>true</c>. Override to send.</remarks>
        public virtual Awaitable PushReleaseActivityAsync(ReleaseActivity activity, CancellationToken cancellationToken = default)
            => Completed();

        /// <summary>A pre-completed <see cref="Awaitable"/> for the no-op push defaults.</summary>
        private static Awaitable Completed()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }
}
