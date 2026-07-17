using UnityEngine;

namespace Molca.Audio
{
    /// <summary>
    /// Single decision point for the audio config ScriptableObjects' read-only-at-runtime
    /// rule: serialized-list mutators (add/remove collection or entry, destructive
    /// rebuilds) are edit-time authoring operations and must refuse to run in play mode.
    /// </summary>
    internal static class AudioAuthoringGuard
    {
        /// <summary>
        /// Test seam: EditMode tests run with <c>Application.isPlaying == false</c>, so
        /// gating behavior is only testable by forcing this. Never set in production.
        /// </summary>
        internal static bool? IsPlayingOverrideForTests;

        /// <summary>Whether serialized mutation must be refused right now.</summary>
        internal static bool IsRuntime => IsPlayingOverrideForTests ?? Application.isPlaying;
    }
}
