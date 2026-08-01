using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Adapts the shared <see cref="LocalizationAuditEngine"/> snapshot into Doctor findings.
    /// </summary>
    /// <remarks>
    /// The legacy check id remains registered for compatibility. Individual issues carry the
    /// focused stable finding ids produced by the shared engine.
    /// </remarks>
    public class DynamicLocalizationLocaleValidityCheck : IDoctorCheck
    {
        /// <inheritdoc />
        public string Id => "dynamic-localization-locale-invalid";

        /// <inheritdoc />
        public string Description =>
            "Shared localization configuration, content, coverage, and Addressables audit";

        /// <inheritdoc />
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(
            DoctorContext context,
            CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var request = LocalizationAuditRequest.CreateDoctorRequest();
            request.CancellationToken = cancellationToken;
            request.IsIgnored = context.IsIgnored;
            request.ReportStatus = context.ReportStatus;
            var snapshot = LocalizationAuditEngine.Audit(request);

            return snapshot.Findings
                .Select(finding => new DoctorIssue(
                    finding.Id,
                    MapSeverity(finding.Severity),
                    finding.Message,
                    finding.Path))
                .ToArray();
        }

        private static DoctorSeverity MapSeverity(LocalizationAuditSeverity severity) =>
            severity switch
            {
                LocalizationAuditSeverity.Error => DoctorSeverity.Error,
                LocalizationAuditSeverity.Warning => DoctorSeverity.Warning,
                _ => DoctorSeverity.Info,
            };
    }
}
