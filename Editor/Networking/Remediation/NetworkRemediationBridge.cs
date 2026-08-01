using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Editor.Remediation;
using Molca.Networking.Configuration;
using UnityEditor;

namespace Molca.Editor.Networking.Remediation
{
    /// <summary>
    /// Travels in <see cref="MolcaFixTarget.DomainContext"/> so a network fix receives the finding and the
    /// catalog it belongs to without re-locating either.
    /// </summary>
    public sealed class NetworkFixDomainContext
    {
        /// <summary>Creates a carrier.</summary>
        /// <param name="finding">The finding being remediated.</param>
        /// <param name="catalog">The catalog the finding was produced from.</param>
        public NetworkFixDomainContext(NetworkValidationFinding finding, NetworkCatalog catalog)
        {
            Finding = finding;
            Catalog = catalog;
        }

        /// <summary>The finding being remediated.</summary>
        public NetworkValidationFinding Finding { get; }

        /// <summary>The catalog the finding was produced from; may be <c>null</c> when none exists.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>An editing service bound to <see cref="Catalog"/>, or <c>null</c> when there is none.</summary>
        /// <remarks>
        /// Fixes mutate exclusively through this — never through their own <c>SerializedObject</c> — so id
        /// validity, uniqueness and cross-reference rewriting stay in one place.
        /// </remarks>
        public NetworkCatalogEditingService Editing =>
            Catalog != null ? new NetworkCatalogEditingService(Catalog) : null;
    }

    /// <summary>
    /// Projects network catalog validation into the shared remediation pass.
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkCatalogValidator"/> already emits stable, namespaced codes
    /// (<c>network.*</c>), so unlike the sequence and reference domains no code translation is needed —
    /// the validator's <see cref="NetworkValidationFinding.Code"/> <i>is</i> the unified finding code.
    /// Validation stays pure and read-only; only an explicit pass mutates.
    /// </remarks>
    public static class NetworkRemediationBridge
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "network";

        /// <summary>
        /// Validates the project's catalog and projects the findings as fix targets.
        /// </summary>
        /// <param name="catalog">The catalog to validate, or <c>null</c> to locate the project's.</param>
        /// <returns>The projection. Never null.</returns>
        public static MolcaAuditProjection Project(NetworkCatalog catalog = null)
        {
            var resolved = catalog ?? NetworkCatalogLocator.FindCatalog();
            var report = NetworkCatalogValidator.Validate(resolved);

            var targets = report.Findings
                .Select(finding => new MolcaFixTarget(
                    finding.Code,
                    DescribePath(resolved, finding),
                    string.IsNullOrEmpty(finding.Remedy)
                        ? finding.Message
                        : $"{finding.Message} {finding.Remedy}",
                    // Entity plus environment is what distinguishes two findings of the same code — a
                    // per-environment binding problem repeats the code once per environment.
                    string.IsNullOrEmpty(finding.EnvironmentId)
                        ? finding.EntityId
                        : $"{finding.EntityId}@{finding.EnvironmentId}",
                    new NetworkFixDomainContext(finding, resolved)))
                .ToList();

            return new MolcaAuditProjection(
                targets,
                resolved == null ? "no network catalog in the project — nothing was validated" : null);
        }

        /// <summary>
        /// Builds a remediation request for the project's network catalog.
        /// </summary>
        /// <param name="policy">Which fixes may auto-apply.</param>
        /// <param name="fixIdFilter">Restricts the pass to these fix ids; <c>null</c> means all the policy allows.</param>
        /// <param name="catalog">The catalog to remediate, or <c>null</c> to locate the project's.</param>
        /// <returns>The request, ready for <see cref="MolcaRemediationPass"/>.</returns>
        public static MolcaRemediationRequest Request(
            RemediationPolicy policy = RemediationPolicy.SafeOnly,
            IReadOnlyCollection<string> fixIdFilter = null,
            NetworkCatalog catalog = null)
            => new MolcaRemediationRequest(Domain, () => Project(catalog))
            {
                Policy = policy,
                FixIdFilter = fixIdFilter,
                UndoGroupName = "Network remediation",
            };

        private static string DescribePath(NetworkCatalog catalog, NetworkValidationFinding finding)
        {
            var assetPath = catalog != null ? AssetDatabase.GetAssetPath(catalog) : null;
            if (string.IsNullOrEmpty(assetPath)) assetPath = catalog != null ? catalog.name : "(no catalog)";
            return string.IsNullOrEmpty(finding.EntityId) ? assetPath : $"{assetPath} :: {finding.EntityId}";
        }
    }
}
