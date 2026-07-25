using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Build-time developer license gate. Before any <c>BuildPipeline.BuildPlayer</c> runs, verifies
    /// that a valid, unexpired entitlement bound to this machine (or supplied via
    /// <see cref="DevLicenseConfig.EntitlementEnvVar"/> for CI) is present, and aborts the build with
    /// a <see cref="BuildFailedException"/> otherwise.
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
        /// <summary>Runs early, but after the version preprocessor (<c>int.MinValue</c>) so version data is set.</summary>
        public int callbackOrder => -10000;

        /// <summary>Verifies the developer entitlement and aborts the build when it is missing/invalid/expired.</summary>
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
                Debug.Log($"[License] Build authorized for '{payload.licenseeId}' ({payload.email}), " +
                          $"entitlement valid until {payload.ExpiresAtUtc:u}.");
                // Bake the licensee identity plus a signed build token into the player, so the shipped
                // build can report framework usage without carrying this developer's credential. A
                // failed mint is soft: the build proceeds and simply never reports.
                var (buildToken, buildId) = BuildTokenStore.Acquire(
                    payload.licenseeId, payload.coreVersion, Application.version);
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
