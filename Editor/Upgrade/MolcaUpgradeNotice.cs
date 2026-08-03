using System;
using System.Linq;
using Molca.Editor.Hub;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageEvents = UnityEditor.PackageManager.Events;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Molca.Editor.Upgrade
{
    /// <summary>Offers the unified upgrade report once when a project first loads Core 2.x.</summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <b>Registration:</b> <see cref="InitializeOnLoadMethodAttribute"/> plus the Package Manager's
    /// registered-packages event after package import/domain load.
    /// <para/>
    /// The notice never migrates automatically. Every project's first 2.x load runs the read-only audit,
    /// because absence of a settings asset cannot prove absence of legacy content or source. A genuinely
    /// fresh project remains silent when that report is clean. The seen-version preference is project-scoped
    /// so a domain reload does not become a recurring nag.
    /// </remarks>
    internal static class MolcaUpgradeNotice
    {
        private const string SeenVersionKey = "Upgrade.LastAuditedCoreVersion";
        private const string RemediationWorkspaceId = "remediation";
        private const string CorePackageName = "com.molca.core";
        private const int VersionReadRetryLimit = 3;

        private static bool _scheduled;
        private static int _versionReadAttempts;

        [InitializeOnLoadMethod]
        private static void ScheduleAfterImport()
        {
            if (Application.isBatchMode) return;
            PackageEvents.registeredPackages -= OnRegisteredPackages;
            PackageEvents.registeredPackages += OnRegisteredPackages;
            QueueCheck();
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs packages)
        {
            var added = packages.added?.AsEnumerable() ?? Enumerable.Empty<UpmPackageInfo>();
            var changed = packages.changedTo?.AsEnumerable() ?? Enumerable.Empty<UpmPackageInfo>();
            if (added.Concat(changed)
                .Any(package => package != null && package.name == CorePackageName))
                QueueCheck();
        }

        private static void QueueCheck()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += CheckAfterImport;
        }

        private static void CheckAfterImport()
        {
            _scheduled = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueueCheck();
                return;
            }

            string currentVersion = CurrentPackageVersion();
            if (string.IsNullOrEmpty(currentVersion) && _versionReadAttempts++ < VersionReadRetryLimit)
            {
                QueueCheck();
                return;
            }

            if (string.IsNullOrEmpty(currentVersion))
                Debug.LogWarning("[MolcaUpgrade] Core was registered, but its installed version could not "
                                 + "be read after several attempts; the automatic 1.x upgrade audit was skipped.");

            _versionReadAttempts = 0;
            string seenVersion = MolcaEditorPrefs.GetString(SeenVersionKey, string.Empty);
            if (!ShouldAudit(seenVersion, currentVersion))
            {
                Remember(currentVersion);
                return;
            }

            MolcaUpgradeReport report = MolcaUpgradeAudit.Run();
            Remember(currentVersion); // Mark before UI: a reload or exception must not repeat the prompt.

            if (report.IsClean) return;

            Debug.Log(report.ToPreview());
            string summary = report.IsConclusive
                ? $"Molca Core {currentVersion} found {report.Findings.Count} item(s) left from 1.x. "
                  + "Review the unified report before continuing."
                : $"Molca Core {currentVersion} could not complete every 1.x upgrade check. "
                  + "Review the report before treating this project as upgraded.";

            if (EditorUtility.DisplayDialog("Molca Core 2.x Upgrade", summary,
                    "Review Upgrade", "Later"))
                MolcaHubWindow.OpenWorkspace(RemediationWorkspaceId);
        }

        /// <summary>Whether this package transition should run the 1.x-to-2.x audit.</summary>
        internal static bool ShouldAudit(string seenVersion, string currentVersion)
        {
            if (!TryMajor(currentVersion, out int currentMajor) || currentMajor != 2) return false;
            if (string.IsNullOrWhiteSpace(seenVersion)) return true;
            return !TryMajor(seenVersion, out int seenMajor) || seenMajor < 2;
        }

        private static string CurrentPackageVersion()
        {
            try
            {
                return UpmPackageInfo.FindForAssembly(typeof(MolcaUpgradeNotice).Assembly)?.version
                       ?? string.Empty;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[MolcaUpgrade] Could not read the installed Core version: "
                                 + exception.Message);
                return string.Empty;
            }
        }

        private static void Remember(string version)
        {
            if (!string.IsNullOrWhiteSpace(version))
                MolcaEditorPrefs.SetString(SeenVersionKey, version);
        }

        private static bool TryMajor(string version, out int major)
        {
            major = 0;
            if (string.IsNullOrWhiteSpace(version)) return false;

            string value = version.Trim();
            int separator = value.IndexOfAny(new[] { '.', '-', '+' });
            if (separator >= 0) value = value.Substring(0, separator);
            return int.TryParse(value, out major) && major >= 0;
        }
    }
}
