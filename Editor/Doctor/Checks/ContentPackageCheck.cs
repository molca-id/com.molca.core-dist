using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reports content package configuration problems for every
    /// <see cref="ContentPackageSettings"/> asset in the project.
    /// </summary>
    /// <remarks>
    /// This check used to carry its own validation: its own duplicate-ID detection, its own
    /// dependency resolution, and its own cycle finder with its own cycle-normalisation rule. The
    /// inspector, automation, and the MCP tools each carried different ones. A package could
    /// therefore pass in the window an author was looking at and fail in the Doctor, or the other
    /// way round, and neither was wrong about its own rules.
    ///
    /// It now runs <see cref="ContentValidation"/> and translates the result. Adding a check
    /// belongs in the engine, where every surface picks it up at once.
    ///
    /// Only the settings-level checks run here: the Doctor must not trigger an Addressables build,
    /// so findings that need a build graph are out of scope by design rather than by omission.
    /// </remarks>
    public class ContentPackageCheck : IDoctorCheck
    {
        /// <inheritdoc/>
        public string Id => "content-package-valid";

        /// <inheritdoc/>
        public string Description =>
            "Content package definitions are internally consistent (ids, versions, labels, dependencies)";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(
            DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            foreach (var guid in AssetDatabase.FindAssets("t:ContentPackageSettings"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(path);
                if (settings?.packageConfigs == null || settings.packageConfigs.Count == 0)
                    continue;

                var report = ContentValidation.ValidateSettings(settings.packageConfigs);

                foreach (var issue in report.Issues)
                {
                    // Informational findings are useful while authoring a release but are noise in a
                    // project health report, which is read as a to-do list.
                    if (issue.Severity == ContentIssueSeverity.Info) continue;

                    issues.Add(new DoctorIssue(
                        Id,
                        issue.Severity == ContentIssueSeverity.Error ? DoctorSeverity.Error : DoctorSeverity.Warning,
                        string.IsNullOrEmpty(issue.PackageId)
                            ? $"{path}: {issue.Message}"
                            : $"{path}: package '{issue.PackageId}' — {issue.Message}"));
                }
            }

            return issues;
        }
    }
}
