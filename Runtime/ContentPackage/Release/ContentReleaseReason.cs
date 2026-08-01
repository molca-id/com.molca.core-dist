namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// The closed set of machine-readable reasons from <c>contracts/content-release-v1.md</c> §8.
    /// </summary>
    /// <remarks>
    /// These are deliberately distinct and must not be collapsed into one generic failure. The
    /// contract makes the point directly: <c>no_release</c>, <c>app_incompatible</c>, and
    /// <c>unauthorized</c> demand completely different operator responses. A player that reports
    /// "content update failed" for all three tells support nothing and sends the operator looking in
    /// the wrong place.
    ///
    /// Strings, not an enum, because the set is a wire contract shared with the server. An unknown
    /// reason from a newer server must survive being logged and shown, which an enum parse would
    /// turn into a second, invented failure.
    /// </remarks>
    public static class ContentReleaseReason
    {
        /// <summary>The channel pointer has never been set for this identity tuple.</summary>
        public const string NoRelease = "no_release";

        /// <summary>The resolved release was revoked.</summary>
        public const string ReleaseRevoked = "release_revoked";

        /// <summary>App version falls outside the release compatibility range.</summary>
        public const string AppIncompatible = "app_incompatible";

        /// <summary>Platform not published for this project and channel.</summary>
        public const string PlatformUnsupported = "platform_unsupported";

        /// <summary>Token policy does not permit the requested channel.</summary>
        public const string ChannelForbidden = "channel_forbidden";

        /// <summary>Missing, malformed, expired, or revoked build authorization.</summary>
        public const string Unauthorized = "unauthorized";

        /// <summary>Access ticket or presigned URL expired.</summary>
        public const string TicketExpired = "ticket_expired";

        /// <summary>Ticket does not cover the requested release or object.</summary>
        public const string TicketScopeInvalid = "ticket_scope_invalid";

        /// <summary>Declared object absent from storage.</summary>
        public const string ObjectNotFound = "object_not_found";

        /// <summary>Fetched bytes disagree with the manifest hash or size.</summary>
        public const string ObjectMismatch = "object_mismatch";

        /// <summary>Signature, key id, or digest verification failed.</summary>
        public const string ManifestUntrusted = "manifest_untrusted";

        /// <summary><c>protocolVersion</c> beyond client or server support.</summary>
        public const string ProtocolUnsupported = "protocol_unsupported";

        /// <summary>Server offered an <c>access.mode</c> this client cannot perform.</summary>
        public const string AccessModeUnsupported = "access_mode_unsupported";

        /// <summary><c>expectedActiveReleaseId</c> did not match.</summary>
        public const string ActiveReleaseConflict = "active_release_conflict";
    }

    /// <summary>Access modes a server may offer (contract §6).</summary>
    public static class ContentAccessMode
    {
        /// <summary>Stable Molca object routes plus a short-lived ticket; the gateway redirects.</summary>
        public const string Gateway = "gateway";

        /// <summary>A short-lived object-id-to-presigned-URL table resolved client-side.</summary>
        public const string PresignedMap = "presigned-map";
    }
}
