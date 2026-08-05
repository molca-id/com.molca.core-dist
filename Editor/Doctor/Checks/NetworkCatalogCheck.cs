using System.Collections.Generic;
using System.Threading;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Hub;
using Molca.Editor.Networking.Validation;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reports the project's <c>NetworkCatalog</c> validation findings.
    /// </summary>
    /// <remarks>
    /// It runs <c>NetworkCatalogValidator</c> — the same function the Hub's Diagnostics view and the build
    /// gate call — and projects its findings. Doctor deliberately implements <b>no</b> networking rule of
    /// its own: a second set of rules means a catalog Doctor calls clean and the build gate rejects, and
    /// whichever a person saw first is the one they will believe (plan §7.13).
    /// <para>
    /// This check is in <see cref="MolcaBuildGate.CheckIds"/>, so it also runs as the pre-build gate the
    /// Hub, CI and the Build workflow wait on. It reports Error only when the catalog has enabled
    /// <c>FailBuildOnValidationError</c> — the same switch <c>NetworkCatalogBuildValidator</c> reads —
    /// so the gate and the build callback can never reach different verdicts about one catalog.
    /// </para>
    /// <para>
    /// Each issue carries the workspace deep link for its finding, so the message says where to go rather
    /// than only what is wrong.
    /// </para>
    /// </remarks>
    public class NetworkCatalogCheck : IDoctorCheck
    {
        /// <inheritdoc/>
        public string Id => "network-catalog";

        /// <inheritdoc/>
        public string Description => "NetworkCatalog validation (routes, hosts, credentials, policies)";

        /// <inheritdoc/>
        public string Category => "Networking";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(
            DoctorContext context, CancellationToken cancellationToken)
        {
            // AssetDatabase and SerializedObject only: stay on the main thread, and yield once so a long
            // Doctor run stays responsive.
            await Awaitable.NextFrameAsync(cancellationToken);

            var issues = new List<DoctorIssue>();

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
            {
                // Not a finding. A project that has not adopted the catalog is supported by contract, and
                // an unconditional nag would train people to ignore this check.
                return issues;
            }

            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(catalog);

            if (!NetworkCatalogLocator.IsRegistered(catalog))
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    "This project has a NetworkCatalog that is not registered on GlobalSettings, so the " +
                    "runtime does not load it and every routed request will fail to resolve. Register it " +
                    "from Hub ▸ Network ▸ ⋯ ▸ Register on GlobalSettings.",
                    assetPath));
            }

            if (catalog.RequiresSchemaMigration)
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    $"The catalog is at schema v{catalog.SchemaVersion} and this framework expects " +
                    $"v{Molca.Networking.Configuration.NetworkCatalog.CurrentSchemaVersion}. Run the schema " +
                    "migration from the Network workspace.",
                    assetPath));
            }

            var report = NetworkCatalogValidator.Validate(catalog);

            // Whether a catalog error blocks is the catalog's call, not this check's. Since this check is
            // in MolcaBuildGate.CheckIds, an Error here aborts a build — so reporting Error for a project
            // that has deliberately not opted in would override that decision from the outside, and the
            // Doctor window would disagree with the build callback about the same catalog. Opting in is
            // the one switch; both surfaces read it.
            bool errorsBlock = catalog.FailBuildOnValidationError;

            foreach (var finding in report.Findings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Info findings are already visible in the workspace and would only add noise here; Doctor
                // reports the ones that mean something is wrong.
                if (finding.Severity == NetworkValidationSeverity.Info)
                    continue;

                string remedy = string.IsNullOrEmpty(finding.Remedy) ? string.Empty : " " + finding.Remedy;

                bool isError = finding.Severity == NetworkValidationSeverity.Error;

                // Say why an error is being reported as a warning, so the reader knows the finding is real
                // and the leniency is theirs to withdraw — otherwise this reads as the check disagreeing
                // with itself about severity.
                string policyNote = isError && !errorsBlock
                    ? " (reported as a warning: this catalog does not enable 'Fail Build On Validation Error')"
                    : string.Empty;

                issues.Add(new DoctorIssue(
                    Id,
                    isError && errorsBlock ? DoctorSeverity.Error : DoctorSeverity.Warning,
                    $"[{finding.Code}] {finding.Message}{remedy}{policyNote} Open: {NetworkHubDeepLinks.For(finding)}",
                    assetPath));
            }

            return issues;
        }
    }
}
