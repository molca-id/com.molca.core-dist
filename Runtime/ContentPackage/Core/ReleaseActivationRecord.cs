using System;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// What release this device is running, and what release it is part-way through adopting.
    /// </summary>
    /// <remarks>
    /// Two fields rather than one, and that is the point. Before this existed, "which release am I
    /// on" was a single version string written at the <em>start</em> of a switch, so a switch that
    /// failed left the device claiming a release it had never finished downloading. The next launch
    /// then saw nothing to do.
    ///
    /// Holding the staged release separately makes an interrupted activation recognisable on the
    /// next launch: <see cref="ActiveReleaseId"/> is what the app is actually running, and
    /// <see cref="StagedReleaseId"/> is unfinished business to either resume or discard. Neither
    /// answer is guessed from the other.
    ///
    /// <see cref="PreviousReleaseId"/> exists for cache retention (plan §11.4): the release we just
    /// replaced is kept evictable-but-not-yet-evicted so a rollback does not have to re-download
    /// everything.
    /// </remarks>
    [Serializable]
    public class ReleaseActivationRecord
    {
        /// <summary>The release currently in force, or empty when none has ever activated.</summary>
        public string activeReleaseId = "";

        /// <summary>Content version of <see cref="activeReleaseId"/>.</summary>
        public string activeContentVersion = "";

        /// <summary>ISO 8601 instant the active release was committed.</summary>
        public string activatedAt = "";

        /// <summary>A release being adopted but not yet committed, or empty.</summary>
        public string stagedReleaseId = "";

        /// <summary>Content version of <see cref="stagedReleaseId"/>.</summary>
        public string stagedContentVersion = "";

        /// <summary>ISO 8601 instant staging began.</summary>
        public string stagedAt = "";

        /// <summary>The release <see cref="activeReleaseId"/> replaced, retained for rollback.</summary>
        public string previousReleaseId = "";

        /// <summary>True when an activation was interrupted and left unfinished.</summary>
        public bool HasStagedActivation => !string.IsNullOrEmpty(stagedReleaseId);

        /// <summary>True when a release has successfully activated at least once.</summary>
        public bool HasActiveRelease => !string.IsNullOrEmpty(activeReleaseId);

        /// <summary>Records the start of an activation attempt. Does not touch the active release.</summary>
        /// <param name="releaseId">The release being staged.</param>
        /// <param name="contentVersion">Its content version.</param>
        public void BeginStaging(string releaseId, string contentVersion)
        {
            stagedReleaseId = releaseId ?? "";
            stagedContentVersion = contentVersion ?? "";
            stagedAt = DateTime.UtcNow.ToString("O");
        }

        /// <summary>
        /// Promotes the staged release to active, remembering what it replaced.
        /// </summary>
        /// <remarks>
        /// Re-committing the release that is already active is a no-op for
        /// <see cref="previousReleaseId"/>. Without that guard a repeated activation would record a
        /// release as its own predecessor, and the retention rule would then treat the live content
        /// as the retired copy it is free to evict.
        /// </remarks>
        public void CommitStaging()
        {
            if (!HasStagedActivation) return;
            if (!string.Equals(activeReleaseId, stagedReleaseId, StringComparison.Ordinal))
                previousReleaseId = activeReleaseId;

            activeReleaseId = stagedReleaseId;
            activeContentVersion = stagedContentVersion;
            activatedAt = DateTime.UtcNow.ToString("O");
            ClearStaging();
        }

        /// <summary>Discards an activation attempt, leaving the active release untouched.</summary>
        public void ClearStaging()
        {
            stagedReleaseId = "";
            stagedContentVersion = "";
            stagedAt = "";
        }

        /// <summary>Forgets the retained predecessor once its content has been evicted.</summary>
        public void ClearPrevious() => previousReleaseId = "";

        /// <summary>Creates an independent copy.</summary>
        public ReleaseActivationRecord Clone() => new ReleaseActivationRecord
        {
            activeReleaseId = activeReleaseId,
            activeContentVersion = activeContentVersion,
            activatedAt = activatedAt,
            stagedReleaseId = stagedReleaseId,
            stagedContentVersion = stagedContentVersion,
            stagedAt = stagedAt,
            previousReleaseId = previousReleaseId,
        };
    }
}
