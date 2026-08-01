using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Runs the shared localization audit for every player-build entry point and prevents a
    /// production player from shipping incomplete or stale localization content.
    /// </summary>
    public sealed class LocalizationBuildGate : IPreprocessBuildWithReport
    {
        /// <summary>Session-scoped key for the manual override; never persisted across Editor restarts.</summary>
        private const string OverrideSessionKey = "Molca.LocalizationBuildGate.Override";

        /// <summary>Menu path of the session override toggle.</summary>
        private const string OverrideMenuPath = "Molca/Build/Skip Localization Gate (This Session)";

        // Set by BuildManager immediately before BuildPipeline.BuildPlayer when it has just built the
        // Addressables content this player ships. One-shot: consumed and cleared by the preprocessor,
        // and never trusted across builds — a stale latch would silently weaken the gate.
        private static bool _addressablesContentBuilt;

        /// <summary>Runs after the early reference gate and before Addressables player preparation.</summary>
        public int callbackOrder => -900;

        /// <summary>
        /// Whether the gate reports blockers without aborting, for this Editor session only.
        /// </summary>
        /// <remarks>
        /// Deliberately <see cref="SessionState"/> rather than <c>EditorPrefs</c>: an escape hatch that
        /// survives an Editor restart is one that gets switched on once and silently disables the gate
        /// forever. Every suppressed build logs the findings it skipped.
        /// </remarks>
        public static bool OverrideEnabled
        {
            get => SessionState.GetBool(OverrideSessionKey, false);
            set => SessionState.SetBool(OverrideSessionKey, value);
        }

        /// <summary>
        /// Records that the caller built this player's Addressables content, satisfying the
        /// production freshness policy without requiring the build-with-player setting.
        /// </summary>
        internal static void MarkAddressablesContentBuilt() => _addressablesContentBuilt = true;

        /// <summary>Audits the build scope and aborts on blocking findings.</summary>
        /// <param name="report">Unity build report describing options and output target.</param>
        /// <exception cref="BuildFailedException">
        /// Thrown when audit coverage is incomplete or blocking localization findings exist.
        /// </exception>
        public void OnPreprocessBuild(BuildReport report)
        {
            var isDevelopment = report?.summary.options.HasFlag(BuildOptions.Development)
                                ?? EditorUserBuildSettings.development;

            var contentAlreadyBuilt = _addressablesContentBuilt;
            _addressablesContentBuilt = false;

            var request = LocalizationAuditRequest.CreateBuildRequest(!isDevelopment);
            request.AddressablesContentAlreadyBuilt = contentAlreadyBuilt;
            var snapshot = LocalizationAuditEngine.Audit(request);

            var blockers = GetBlockingFindings(snapshot, isDevelopment);
            if (blockers.Length > 0)
            {
                if (!OverrideEnabled)
                    throw new BuildFailedException(BuildFailureMessage(snapshot, blockers));

                Debug.LogWarning(
                    $"[LocalizationBuildGate] Override active: building despite {blockers.Length} " +
                    "blocking localization finding(s). This player may ship broken or stale " +
                    "localization.\n  " + string.Join("\n  ", blockers.Select(FormatFinding)));
            }

            if (snapshot.Warnings.Count > 0)
                Debug.LogWarning(
                    $"[LocalizationBuildGate] {snapshot.Warnings.Count} localization warning(s):\n  " +
                    string.Join("\n  ", snapshot.Warnings.Select(FormatFinding)));
        }

        /// <summary>Returns findings that block the selected build policy.</summary>
        /// <param name="snapshot">Shared audit result.</param>
        /// <param name="isDevelopment">Whether warning-level incompleteness may proceed.</param>
        /// <returns>Blocking findings in deterministic snapshot order.</returns>
        internal static LocalizationAuditFinding[] GetBlockingFindings(
            LocalizationAuditSnapshot snapshot,
            bool isDevelopment)
        {
            if (snapshot == null)
                return new[]
                {
                    new LocalizationAuditFinding(
                        "localization-audit-failed",
                        LocalizationAuditSeverity.Error,
                        "Localization audit returned no snapshot."),
                };

            var errors = snapshot.Errors.ToList();
            if (!snapshot.Coverage.IsComplete)
                errors.Add(new LocalizationAuditFinding(
                    "localization-audit-coverage-incomplete",
                    isDevelopment
                        ? LocalizationAuditSeverity.Warning
                        : LocalizationAuditSeverity.Error,
                    $"Localization audit coverage is incomplete: {snapshot.Coverage.Describe()}."));

            return errors
                .Where(finding => finding.Severity == LocalizationAuditSeverity.Error)
                .ToArray();
        }

        private static string BuildFailureMessage(
            LocalizationAuditSnapshot snapshot,
            LocalizationAuditFinding[] blockers)
        {
            var details = string.Join("\n  ", blockers.Select(FormatFinding));
            return
                $"[LocalizationBuildGate] Build aborted: {blockers.Length} blocking localization " +
                $"finding(s); status {snapshot.Status}, coverage {snapshot.Coverage.Describe()}.\n  " +
                $"{details}\nFix these in Molca Doctor or the Localization authoring surface and rebuild, " +
                $"or override the gate for this Editor session via '{OverrideMenuPath}'.";
        }

        /// <summary>Toggles the session override.</summary>
        [MenuItem(OverrideMenuPath, priority = 50)]
        private static void ToggleOverride()
        {
            OverrideEnabled = !OverrideEnabled;
            if (OverrideEnabled)
                Debug.LogWarning(
                    "[LocalizationBuildGate] Gate overridden for this Editor session. Localization " +
                    "blockers will be logged instead of failing the build. This resets when the " +
                    "Editor restarts.");
        }

        /// <summary>Shows the override's current state as a checkmark.</summary>
        /// <returns>Always true; the item is never disabled.</returns>
        [MenuItem(OverrideMenuPath, validate = true)]
        private static bool ToggleOverrideValidate()
        {
            Menu.SetChecked(OverrideMenuPath, OverrideEnabled);
            return true;
        }

        private static string FormatFinding(LocalizationAuditFinding finding)
        {
            var location = string.IsNullOrEmpty(finding.Path) ? string.Empty : $" ({finding.Path})";
            return $"[{finding.Id}] {finding.Message}{location}";
        }
    }
}
