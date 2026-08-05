using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Build-time developer license and project gate. Before any <c>BuildPipeline.BuildPlayer</c> runs,
    /// verifies a valid machine-bound entitlement, a connected backend project, and fresh server
    /// authorization, then aborts with <see cref="BuildFailedException"/> when any requirement fails.
    /// </summary>
    /// <remarks>
    /// Unity discovers <see cref="IPreprocessBuildWithReport"/> by type, so this runs for the Build
    /// Manager, <c>File &gt; Build</c>, and CI alike.
    /// <para>
    /// <b>Inert until configured.</b> When <see cref="DevLicenseConfig.IsConfigured"/> is false (the
    /// placeholders have not been replaced), the gate does nothing — so shipping the licensing feature
    /// never blocks builds in a fork that has not stood up its own server/keys yet.
    /// </para>
    /// <para>
    /// This is a deterrent + audit gate, not DRM: a developer with Core source can remove it. Its
    /// value is the enforced sign-in / terms acceptance and the named-identity record it produces.
    /// </para>
    /// </remarks>
    public sealed class LicenseBuildGate : IPreprocessBuildWithReport
    {
        /// <summary>
        /// First of the gates, but after the version preprocessor (<c>int.MinValue</c>) so version data
        /// is set. See <see cref="Molca.Editor.MolcaBuildCallbackOrder"/> for the bands.
        /// </summary>
        public int callbackOrder => Molca.Editor.MolcaBuildCallbackOrder.LicenseGate;

        /// <summary>Verifies developer and project authorization and aborts when either is unavailable.</summary>
        /// <param name="report">The Unity build report for the build about to run.</param>
        /// <exception cref="BuildFailedException">Thrown to abort the build when not licensed.</exception>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!DevLicenseConfig.IsConfigured)
                return; // Licensing not set up for this distribution — stay out of the way.

            string token = DevEntitlementStore.LoadEffective();
            var status = DevEntitlementVerifier.Evaluate(token, SystemInfo.deviceUniqueIdentifier, out var payload);

            if (status == DevLicenseStatus.Valid)
            {
                if (!HasProjectConnection(MolcaProjectSettings.Instance))
                {
                    Telemetry.MolcaEditorTelemetry.Track("build.blocked",
                        new System.Collections.Generic.Dictionary<string, object>
                        {
                            { "reason", "ProjectConnectionRequired" },
                        });
                    throw new BuildFailedException(
                        "[License] Build blocked: this repository is not connected to a Molca backend project.\n" +
                        "An owner or manager must connect it in Molca Hub > Settings > Project.");
                }

                Debug.Log($"[License] Build authorized for '{payload.licenseeId}' ({payload.email}), " +
                          $"entitlement valid until {payload.ExpiresAtUtc:u}.");
                // Bake the licensee identity plus a signed build token into the player, so the shipped
                // build can report framework usage without carrying this developer's credential.
                // Full project migration requires a fresh server authorization for every build.
                var (buildToken, buildId) = BuildTokenStore.Acquire(
                    payload.licenseeId, payload.coreVersion, Application.version);
                if (string.IsNullOrEmpty(buildToken))
                {
                    Telemetry.MolcaEditorTelemetry.Track("build.blocked",
                        new System.Collections.Generic.Dictionary<string, object>
                        {
                            { "reason", "ProjectAuthorizationFailed" },
                        });
                    throw new BuildFailedException(
                        "[License] Build blocked: Molca could not authorize the connected project.\n" +
                        "Verify the project connection and current membership, then retry while online.");
                }
                LicenseBuildStamp.Write(payload.licenseeId, payload.coreVersion, buildToken, buildId, Application.version);

                Telemetry.MolcaEditorTelemetry.Track("build.authorized", new System.Collections.Generic.Dictionary<string, object>
                {
                    { "platform", report.summary.platform.ToString() },
                    { "runtimeReporting", !string.IsNullOrEmpty(buildToken) },
                });
                return;
            }

            Telemetry.MolcaEditorTelemetry.Track("build.blocked", new System.Collections.Generic.Dictionary<string, object>
            {
                { "reason", status.ToString() },
            });

            throw new BuildFailedException(
                "[License] Build blocked: " + Explain(status) + "\n" +
                "Open  Molca > License > Developer Sign-In  and sign in with an authorized Google " +
                "account, or set the " + DevLicenseConfig.EntitlementEnvVar + " environment variable " +
                "with a pre-activated entitlement for CI builds.");
        }

        internal static bool HasProjectConnection(MolcaProjectSettings settings) =>
            settings != null && !string.IsNullOrWhiteSpace(settings.ProjectBinding);

        /// <summary>Maps a non-valid status to an actionable explanation.</summary>
        private static string Explain(DevLicenseStatus status)
        {
            switch (status)
            {
                case DevLicenseStatus.Missing: return "no developer entitlement found on this machine.";
                case DevLicenseStatus.Expired: return "the developer entitlement has expired — re-authenticate.";
                case DevLicenseStatus.WrongMachine: return "the entitlement was issued for a different machine — re-authenticate here.";
                case DevLicenseStatus.Invalid: return "the developer entitlement is invalid or corrupt — re-authenticate.";
                default: return "not licensed.";
            }
        }
    }

    /// <summary>
    /// Removes the generated license stamp after the build, regardless of outcome. Runs last
    /// (<see cref="callbackOrder"/> = max) so nothing else needs the stamp during post-process.
    /// </summary>
    public sealed class LicenseBuildStampPostprocessor : IPostprocessBuildWithReport
    {
        /// <summary>Runs after every other post-process callback.</summary>
        public int callbackOrder => int.MaxValue;

        /// <summary>Deletes the generated <c>MolcaLicenseStamp.json</c> written during pre-process.</summary>
        /// <param name="report">The Unity build report for the completed build.</param>
        public void OnPostprocessBuild(BuildReport report) => LicenseBuildStamp.Cleanup();
    }
}
