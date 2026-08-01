using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.ColorID;
using Molca.Editor.Doctor;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;

namespace Molca.ColorID.Editor.Remediation
{
    /// <summary>
    /// Travels in <see cref="MolcaFixTarget.DomainContext"/> so a colour-theme fix has the finding and the
    /// theme set it belongs to without re-locating either.
    /// </summary>
    public sealed class ColorThemeFixDomainContext
    {
        /// <summary>Creates a carrier.</summary>
        /// <param name="finding">The finding being remediated.</param>
        /// <param name="themeSet">The theme set the audit ran against; may be <c>null</c>.</param>
        public ColorThemeFixDomainContext(ColorThemeFinding finding, ColorThemeSet themeSet)
        {
            Finding = finding;
            ThemeSet = themeSet;
        }

        /// <summary>The finding being remediated.</summary>
        public ColorThemeFinding Finding { get; }

        /// <summary>The theme set the audit ran against; <c>null</c> when the project has none.</summary>
        public ColorThemeSet ThemeSet { get; }
    }

    /// <summary>
    /// Projects the colour-theme audit into the shared remediation pass.
    /// </summary>
    /// <remarks>
    /// <see cref="ColorThemeAuditService.Run"/> is read-only and stays so; this bridge only re-shapes its
    /// snapshot. Only one colour finding is mechanically repairable — regenerating derived UI Toolkit output
    /// — because every other kind encodes a design or naming decision. In particular, unused and deprecated
    /// tokens stay report-only: dropping a legacy alias is gated on four conditions recorded in
    /// <c>docs/internal/COLORID_LEGACY_KEY_USAGE_INVENTORY.md</c>, and zero measured usage is never one of them.
    /// </remarks>
    public static class ColorThemeRemediationBridge
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "colorid";

        /// <summary>The finding-code prefix colour-theme findings are namespaced under.</summary>
        public const string CodePrefix = "colorid.";

        /// <summary>Builds the unified finding code for a colour-theme finding kind.</summary>
        /// <param name="kind">The finding kind.</param>
        /// <returns>The namespaced code, e.g. <c>colorid.GeneratedOutputStale</c>.</returns>
        public static string CodeFor(ColorThemeFindingKind kind) => CodePrefix + kind;

        /// <summary>
        /// Runs the colour-theme audit and projects its findings as fix targets.
        /// </summary>
        /// <param name="request">What to cover; <c>null</c> means the audit's own default.</param>
        /// <returns>The projection. Never null.</returns>
        public static MolcaAuditProjection Project(ColorThemeAuditRequest request = null)
        {
            var snapshot = ColorThemeAuditService.Run(request);
            var themeSet = ColorThemeAuditService.FindThemeSettings()?.ThemeSet;

            var targets = snapshot.Findings
                .Select(finding => new MolcaFixTarget(
                    CodeFor(finding.Kind),
                    string.IsNullOrEmpty(finding.AssetPath) ? "(project)" : finding.AssetPath,
                    finding.Message,
                    // Subject plus variant is what distinguishes two findings of one kind — a token can fail
                    // in one variant and resolve in another.
                    string.IsNullOrEmpty(finding.VariantId)
                        ? finding.Subject
                        : $"{finding.Subject}@{finding.VariantId}",
                    new ColorThemeFixDomainContext(finding, themeSet)))
                .ToList();

            var incomplete = snapshot.Findings
                .Where(f => f.Kind == ColorThemeFindingKind.CoverageIncomplete)
                .Select(f => f.Message)
                .FirstOrDefault();

            return new MolcaAuditProjection(targets, incomplete);
        }

        /// <summary>
        /// Builds a remediation request for the project's colour theme.
        /// </summary>
        /// <param name="policy">Which fixes may auto-apply.</param>
        /// <param name="fixIdFilter">Restricts the pass to these fix ids; <c>null</c> means all the policy allows.</param>
        /// <returns>The request, ready for <see cref="MolcaRemediationPass"/>.</returns>
        public static MolcaRemediationRequest Request(
            RemediationPolicy policy = RemediationPolicy.SafeOnly,
            IReadOnlyCollection<string> fixIdFilter = null)
            => new MolcaRemediationRequest(Domain, () => Project())
            {
                Policy = policy,
                FixIdFilter = fixIdFilter,
                UndoGroupName = "Colour theme remediation",
            };
    }

    /// <summary>
    /// Regenerates the derived UI Toolkit stylesheets and manifest for a theme set whose generated output is
    /// missing or stale.
    /// </summary>
    /// <remarks>
    /// The one unambiguously mechanical colour fix: the output is <b>derived</b> data, so overwriting it is
    /// the entire point and there is nothing authored to lose. It writes generated files to disk, so it
    /// declares <see cref="FixReversibility.FileSnapshot"/> and is therefore an opt-in repair rather than part
    /// of the safe pass — the same honesty the network schema migration required.
    /// </remarks>
    internal sealed class ColorThemeRegenerateOutputFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "colorid.regenerate-uss";

        /// <inheritdoc/>
        public override string Description =>
            "Regenerates the UI Toolkit stylesheets and manifest derived from the theme set.";

        /// <inheritdoc/>
        public override string HandledFindingCode =>
            ColorThemeRemediationBridge.CodeFor(ColorThemeFindingKind.GeneratedOutputStale);

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            if (!(target?.DomainContext is ColorThemeFixDomainContext domain) || domain.ThemeSet == null)
                return MolcaFixOutcome.NotApplied(
                    "No theme set is configured, so there is no source to regenerate output from.");

            if (domain.Finding != null && domain.Finding.IsPackageOwned)
                return MolcaFixOutcome.NotApplied(
                    "The generated output is package-owned, so project tooling must not rewrite it.");

            if (dryRun)
                return new MolcaFixOutcome(
                    true,
                    $"Would regenerate UI Toolkit output for '{domain.ThemeSet.name}'.",
                    "stale", "regenerated");

            var result = ColorThemeUssGenerator.Generate(domain.ThemeSet);
            return result.Success
                ? new MolcaFixOutcome(
                    true,
                    result.Messages.Count > 0
                        ? string.Join("; ", result.Messages)
                        : $"Regenerated UI Toolkit output for '{domain.ThemeSet.name}'.",
                    "stale",
                    "regenerated")
                : MolcaFixOutcome.NotApplied(
                    result.Messages.Count > 0
                        ? string.Join("; ", result.Messages)
                        : "The generator reported no change.");
        }
    }
}
