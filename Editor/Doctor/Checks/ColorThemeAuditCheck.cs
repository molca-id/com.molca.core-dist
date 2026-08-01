#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Molca.ColorID;
using Molca.ColorID.Editor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reports colour-theme problems from the shared audit snapshot.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Doctor/Checks/</c>.
    /// <b>Registration:</b> discovered by <c>DoctorCheckRegistry</c> as an <see cref="IDoctorCheck"/>.
    /// <para/>
    /// This check adds no scanning logic of its own — it runs
    /// <see cref="ColorThemeAuditService"/> and translates findings. That is the point of one shared
    /// snapshot: Doctor, the Hub, the build gate and MCP cannot disagree about whether a project is
    /// healthy, because they are all reading the same scan.
    /// <para/>
    /// It also fixes a real blind spot in the older
    /// <c>ColorIDReferenceValidityCheck</c>, which unions keys across every <see cref="ColorModule"/> and
    /// so accepts a reference that is defined in <i>any</i> palette. Under V2 every selectable variant is
    /// checked separately, and a reference missing from one variant is reported with that variant named —
    /// because switching to it renders magenta at runtime.
    /// <para/>
    /// Read-only, like the audit it wraps: it never opens a scene, dirties an asset or rewrites an ID.
    /// </remarks>
    public class ColorThemeAuditCheck : IDoctorCheck
    {
        /// <inheritdoc/>
        public string Id => "color-theme-audit";

        /// <inheritdoc/>
        public string Description =>
            "Colour theme validity, per-variant token coverage, reference resolution and generated output freshness";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context,
            CancellationToken cancellationToken)
        {
            // AssetDatabase and the filesystem sweep are main-thread only. Ending on the main thread is
            // also required by the Core convention that a check must not complete its Awaitable on a
            // pool thread.
            await Awaitable.MainThreadAsync();

            var issues = new List<DoctorIssue>();
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);

            // A legacy-only project has one Info finding and nothing else; reporting it as an issue
            // would nag every unmigrated project on every run.
            if (snapshot.ThemeSet == null) return issues;

            foreach (var finding in snapshot.Findings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var severity = finding.Severity switch
                {
                    ColorThemeFindingSeverity.Error => DoctorSeverity.Error,
                    ColorThemeFindingSeverity.Warning => DoctorSeverity.Warning,
                    _ => DoctorSeverity.Info
                };

                string ownership = finding.IsPackageOwned
                    ? " (package-owned: fix via a package update, a consumer override, or a migration alias)"
                    : string.Empty;

                issues.Add(new DoctorIssue(Id, severity, $"{finding.Message}{ownership}",
                    finding.AssetPath));
            }

            // Incomplete coverage is itself reportable: without it, a scan that could not read part of
            // the project would present as a clean bill of health.
            foreach (var skipped in snapshot.SkippedInputs)
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    $"Colour theme scan coverage is incomplete — '{skipped.Key}' was not covered: "
                    + $"{skipped.Value} Findings above cannot be treated as exhaustive."));
            }

            return issues;
        }
    }
}
#endif
