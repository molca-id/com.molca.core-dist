using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Upgrade
{
    /// <summary>Entry points for the 1.x → 2.x readiness report.</summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>, plus a static entry point for <c>-executeMethod</c>.
    /// <para/>
    /// The CLI entry exits non-zero only when the report is <i>inconclusive</i>. Findings are the expected
    /// state of a project mid-upgrade and must not fail a build; a scan that could not see everything is a
    /// different thing, because then "nothing left to migrate" is not something the report is entitled to
    /// say.
    /// </remarks>
    public static class MolcaUpgradeMenu
    {
        /// <summary>Reports what an upgrading project still carries.</summary>
        [MenuItem("Molca/Upgrade/Report 1.x → 2.x Readiness", priority = 1)]
        public static void Report() => Debug.Log(MolcaUpgradeAudit.Run().ToPreview());

        /// <summary>Headless report for <c>-executeMethod</c>.</summary>
        public static void ReportFromCli()
        {
            var report = MolcaUpgradeAudit.Run();
            Debug.Log(report.ToPreview());
            EditorApplication.Exit(report.IsConclusive ? 0 : 1);
        }
    }
}
