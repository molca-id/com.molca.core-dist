using System;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Provider-agnostic snapshot of a completed build, assembled once by
    /// <see cref="IntegrationActivityRouter"/> and fanned out to every opted-in
    /// <see cref="IntegrationProvider"/>.
    /// </summary>
    /// <remarks>
    /// Carries primitives only so providers never depend on <c>UnityEditor.Build.Reporting</c>; the router
    /// already resolved project/version/result fields. Editor-only.
    /// </remarks>
    public readonly struct BuildActivity
    {
        /// <summary>Initializes the build activity payload.</summary>
        public BuildActivity(
            string projectName, string version, string platform, bool succeeded, string result,
            TimeSpan duration, ulong sizeBytes, int errors, string triggeredBy)
        {
            ProjectName = projectName;
            Version = version;
            Platform = platform;
            Succeeded = succeeded;
            Result = result;
            Duration = duration;
            SizeBytes = sizeBytes;
            Errors = errors;
            TriggeredBy = triggeredBy;
        }

        /// <summary>Human-readable project name.</summary>
        public readonly string ProjectName;
        /// <summary>Full version string (e.g. "1.8.2 (build 41)").</summary>
        public readonly string Version;
        /// <summary>Target platform name.</summary>
        public readonly string Platform;
        /// <summary>True when the build succeeded (i.e. not Failed/Cancelled).</summary>
        public readonly bool Succeeded;
        /// <summary>Raw build result text ("Succeeded", "Failed", "Cancelled", …).</summary>
        public readonly string Result;
        /// <summary>Total build duration.</summary>
        public readonly TimeSpan Duration;
        /// <summary>Output size in bytes (0 when unavailable).</summary>
        public readonly ulong SizeBytes;
        /// <summary>Total error count reported by the build.</summary>
        public readonly int Errors;
        /// <summary>User/agent that triggered the build.</summary>
        public readonly string TriggeredBy;
    }

    /// <summary>
    /// Provider-agnostic snapshot of a cut release, assembled once by <see cref="IntegrationActivityRouter"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Notes"/> is the <i>composed</i> changelog entry (raw notes + git commits) the router has
    /// already resolved, so providers do not each re-read the changelog. Editor-only.
    /// </remarks>
    public readonly struct ReleaseActivity
    {
        /// <summary>Initializes the release activity payload.</summary>
        public ReleaseActivity(string projectName, string version, string triggeredBy, string notes)
        {
            ProjectName = projectName;
            Version = version;
            TriggeredBy = triggeredBy;
            Notes = notes;
        }

        /// <summary>Human-readable project name.</summary>
        public readonly string ProjectName;
        /// <summary>The released version string.</summary>
        public readonly string Version;
        /// <summary>User/agent that cut the release.</summary>
        public readonly string TriggeredBy;
        /// <summary>Composed release notes (may be null/empty).</summary>
        public readonly string Notes;
    }
}
