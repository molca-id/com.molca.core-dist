#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Runs <see cref="ColorThemeDeprecationReport"/> from the menu and from a headless CLI invocation.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Audit/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>, plus a static entry point for <c>-executeMethod</c>.
    /// <para/>
    /// The CLI form matters more than the menu one: the removal gate is meant to be checked in CI on the
    /// release that proposes a removal, not remembered by whoever is doing the release.
    /// </remarks>
    public static class ColorThemeDeprecationReportMenu
    {
        /// <summary>Runs a full audit and logs the compatibility usage report.</summary>
        [MenuItem("Molca/ColorID/Report Compatibility Usage", priority = 90)]
        public static void Report()
        {
            var snapshot = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            var result = ColorThemeDeprecationReport.Build(snapshot);

            string text = ColorThemeDeprecationReport.Format(result);

            // Inconclusive is a warning, not an error: a partial scan is a normal state for a project with
            // scenes it cannot open, and the report already says the counts are a floor.
            if (result.IsConclusive) Debug.Log(text);
            else Debug.LogWarning(text);
        }

        /// <summary>
        /// Ceiling for a whole-project audit, including closed scenes.
        /// </summary>
        /// <remarks>
        /// Plan §17.8's audit-duration budget, asserted here rather than in an EditMode test. A full audit
        /// opens every closed scene, and that much engine churn inside a shared unit-test run emits
        /// scene-load asserts the test runner attributes to whichever test happens to be running when they
        /// are pumped. This entry point already runs exactly one full audit and is its own process.
        /// <para/>
        /// Generous on purpose: absolute duration is a property of the project's size and the machine, not
        /// of the audit's design. Its job is to fail when an audit starts taking minutes — the shape of a
        /// regression from linear to quadratic scanning.
        /// </remarks>
        private const double AuditBudgetMs = 90_000d;

        /// <summary>
        /// Headless entry point. Logs the report and sets a non-zero exit code when it is inconclusive or
        /// the audit blew its duration budget.
        /// </summary>
        /// <remarks>
        /// Remaining legacy usage is expected during the compatibility window and must not fail a build. An
        /// inconclusive scan is a different thing — it means the gate did not actually run.
        /// </remarks>
        public static void ReportFromCli()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            clock.Stop();

            var result = ColorThemeDeprecationReport.Build(snapshot);

            Debug.Log(ColorThemeDeprecationReport.Format(result));
            Debug.Log($"[ColorTheme] Whole-project audit took {clock.Elapsed.TotalSeconds:F1}s "
                      + $"(budget {AuditBudgetMs / 1000d:F0}s).");

            if (clock.Elapsed.TotalMilliseconds >= AuditBudgetMs)
            {
                Debug.LogError($"[ColorTheme] The whole-project audit took "
                               + $"{clock.Elapsed.TotalSeconds:F1}s, over its "
                               + $"{AuditBudgetMs / 1000d:F0}s budget.");
                EditorApplication.Exit(1);
                return;
            }

            if (!result.IsConclusive)
            {
                Debug.LogError("[ColorTheme] The compatibility usage report is inconclusive; it cannot be "
                               + "used as removal evidence.");
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }
    }
}
#endif
