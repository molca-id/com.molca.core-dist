namespace Molca.Editor.UI.Components
{
    /// <summary>Status kinds rendered by shared Molca editor status dots and section-card headers.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Promoted from the Hub-only <c>MolcaHubStatusKind</c> in Sprint 27.2 so non-Hub editor windows
    /// share one status vocabulary.
    /// </remarks>
    public enum MolcaStatusKind
    {
        /// <summary>No status dot is shown.</summary>
        None,

        /// <summary>Neutral / not-yet-evaluated state (grey).</summary>
        Idle,

        /// <summary>Healthy / configured state (green).</summary>
        Ok,

        /// <summary>Needs attention but not broken (amber).</summary>
        Warning,

        /// <summary>Misconfigured / failed state (orange-red).</summary>
        Error
    }
}
