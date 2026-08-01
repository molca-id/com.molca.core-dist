namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// Which part of an activation is running.
    /// </summary>
    /// <remarks>
    /// A release activation spends real time in several places before a single byte of content is
    /// downloaded: resolving the active release, fetching and verifying the signed manifest, and
    /// loading the catalog. A bare 0..1 fraction reports all of that as zero, so the only honest
    /// reading of the UI during those seconds is "stuck" — and a user who concludes that force-quits
    /// the app, which is the one action that turns a recoverable staged activation into a retry.
    ///
    /// The phase is what makes the wait legible. The fraction alone cannot be, because the phases
    /// before <see cref="Downloading"/> have no measurable denominator.
    /// </remarks>
    public enum ReleaseActivationPhase
    {
        /// <summary>Asking the control plane which release is active for this platform.</summary>
        Resolving = 0,

        /// <summary>Fetching the manifest and checking its signature, digest, and scope.</summary>
        Verifying = 1,

        /// <summary>Binding access material and loading the release catalog.</summary>
        PreparingCatalog = 2,

        /// <summary>Downloading the required package closure.</summary>
        Downloading = 3,

        /// <summary>Swapping the catalog and persisting the activation record.</summary>
        Committing = 4,

        /// <summary>Re-downloading optional packages the user already had.</summary>
        CarryingForward = 5,

        /// <summary>Nothing is in flight.</summary>
        Idle = 6,
    }

    /// <summary>
    /// A snapshot of an in-flight release activation.
    /// </summary>
    /// <remarks>
    /// Carries no ticket, URL, or token — only what is safe to render and safe to log. Callers show
    /// this to players, and a player-facing string is the least controlled surface there is.
    /// </remarks>
    public readonly struct ReleaseActivationProgress
    {
        /// <summary>What the activation is doing now.</summary>
        public ReleaseActivationPhase Phase { get; }

        /// <summary>Overall completion 0..1, meaningful mainly during <see cref="ReleaseActivationPhase.Downloading"/>.</summary>
        public float Fraction { get; }

        /// <summary>The content version being adopted, or empty before it is known.</summary>
        public string ContentVersion { get; }

        /// <summary>The package currently transferring, or empty.</summary>
        public string PackageId { get; }

        /// <summary>1-based index of <see cref="PackageId"/> within the required closure, or 0.</summary>
        public int PackageIndex { get; }

        /// <summary>Required packages in this activation, or 0 before the manifest is known.</summary>
        public int PackageCount { get; }

        /// <summary>Builds a snapshot.</summary>
        /// <param name="phase">Current phase.</param>
        /// <param name="fraction">Overall completion 0..1.</param>
        /// <param name="contentVersion">Content version being adopted.</param>
        /// <param name="packageId">Package currently transferring.</param>
        /// <param name="packageIndex">1-based index within the required closure.</param>
        /// <param name="packageCount">Required package count.</param>
        public ReleaseActivationProgress(
            ReleaseActivationPhase phase,
            float fraction = 0f,
            string contentVersion = "",
            string packageId = "",
            int packageIndex = 0,
            int packageCount = 0)
        {
            Phase = phase;
            Fraction = fraction < 0f ? 0f : fraction > 1f ? 1f : fraction;
            ContentVersion = contentVersion ?? "";
            PackageId = packageId ?? "";
            PackageIndex = packageIndex;
            PackageCount = packageCount;
        }

        /// <summary>
        /// A short player-facing sentence describing this snapshot.
        /// </summary>
        /// <remarks>
        /// Lives here rather than in each UI so that every surface — the SDK panel, a custom loading
        /// screen, an editor tool — says the same thing about the same state. Deliberately says
        /// nothing about <em>where</em> content comes from: a player has no use for the gateway host,
        /// and the moment it appears in a label it also appears in a screenshot.
        /// </remarks>
        /// <returns>The description.</returns>
        public string Describe() => Phase switch
        {
            ReleaseActivationPhase.Resolving => "Checking for new content…",
            ReleaseActivationPhase.Verifying => "Verifying content…",
            ReleaseActivationPhase.PreparingCatalog => "Preparing content…",
            ReleaseActivationPhase.Downloading => PackageCount > 0
                ? $"Downloading {PackageIndex} of {PackageCount}… ({Fraction:P0})"
                : $"Downloading… ({Fraction:P0})",
            ReleaseActivationPhase.Committing => "Finishing up…",
            ReleaseActivationPhase.CarryingForward => "Restoring your downloads…",
            _ => "",
        };
    }
}
