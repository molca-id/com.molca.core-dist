using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.ContentPackage.Editor.Remediation
{
    /// <summary>
    /// Travels in <see cref="MolcaFixTarget.DomainContext"/> so a content fix has the issue and the settings
    /// asset without re-locating either.
    /// </summary>
    public sealed class ContentFixDomainContext
    {
        /// <summary>Creates a carrier.</summary>
        /// <param name="issue">The validation issue being remediated.</param>
        /// <param name="settings">The settings asset the issue was produced from.</param>
        public ContentFixDomainContext(ContentIssue issue, ContentPackageSettings settings)
        {
            Issue = issue;
            Settings = settings;
        }

        /// <summary>The validation issue being remediated.</summary>
        public ContentIssue Issue { get; }

        /// <summary>The settings asset; may be <c>null</c> when none was found.</summary>
        public ContentPackageSettings Settings { get; }

        /// <summary>
        /// The existing editing service bound to <see cref="Settings"/>, or <c>null</c> when there is none.
        /// </summary>
        /// <remarks>
        /// Fixes mutate exclusively through <see cref="ContentPackageEditingService"/> — the single place
        /// <see cref="ContentPackageSettings"/> is written. No fix opens its own <c>SerializedObject</c>.
        /// </remarks>
        public ContentPackageEditingService Editing =>
            Settings != null ? new ContentPackageEditingService(Settings) : null;

        /// <summary>The config the issue is about, or <c>null</c> for a release-wide issue.</summary>
        public ContentPackageSettings.PackageConfig Config =>
            Settings == null || string.IsNullOrEmpty(Issue?.PackageId)
                ? null
                : Settings.packageConfigs?.FirstOrDefault(
                    c => c != null && string.Equals(c.packageId, Issue.PackageId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Projects content-package validation into the shared remediation pass.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ContentValidation.ValidateSettings"/> rather than the full
    /// <see cref="ContentValidation.Validate"/>: a remediation pass must not require a resolved build graph,
    /// and every fixable content finding is a settings-only finding. Build-graph findings
    /// (<c>package_not_in_build</c>, <c>bundle_unreferenced</c>) are build decisions with no mechanical
    /// repair, so nothing is lost by not seeing them here.
    /// </remarks>
    public static class ContentPackageRemediationBridge
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "content";

        /// <summary>The finding-code prefix content issues are namespaced under.</summary>
        public const string CodePrefix = "content.";

        /// <summary>Builds the unified finding code for a content issue code.</summary>
        /// <param name="code">A <see cref="ContentIssue.Code"/> value, e.g. <c>label_duplicate</c>.</param>
        /// <returns>The namespaced code, e.g. <c>content.label_duplicate</c>.</returns>
        public static string CodeFor(string code) => CodePrefix + code;

        /// <summary>
        /// Validates the project's content-package settings and projects the issues as fix targets.
        /// </summary>
        /// <param name="settings">The settings to validate, or <c>null</c> to locate the project's.</param>
        /// <returns>The projection. Never null.</returns>
        public static MolcaAuditProjection Project(ContentPackageSettings settings = null)
        {
            var resolved = settings ?? FindSettings();
            if (resolved == null)
                return new MolcaAuditProjection(
                    new List<MolcaFixTarget>(),
                    "no ContentPackageSettings asset in the project — nothing was validated");

            var report = ContentValidation.ValidateSettings(resolved.packageConfigs);
            var assetPath = AssetDatabase.GetAssetPath(resolved);
            if (string.IsNullOrEmpty(assetPath)) assetPath = resolved.name;

            var targets = report.Issues
                .Select(issue => new MolcaFixTarget(
                    CodeFor(issue.Code),
                    string.IsNullOrEmpty(issue.PackageId) ? assetPath : $"{assetPath} :: {issue.PackageId}",
                    issue.Message,
                    issue.PackageId,
                    new ContentFixDomainContext(issue, resolved)))
                .ToList();

            // A settings asset in a package or the SDK layer cannot be written at all — reported as a coverage
            // note rather than as a per-finding refusal, since it applies to every finding equally.
            var readOnly = new ContentPackageEditingService(resolved).ReadOnlyReason();
            return new MolcaAuditProjection(targets, readOnly);
        }

        /// <summary>
        /// Builds a remediation request for the project's content-package settings.
        /// </summary>
        /// <param name="policy">Which fixes may auto-apply.</param>
        /// <param name="fixIdFilter">Restricts the pass to these fix ids; <c>null</c> means all the policy allows.</param>
        /// <param name="settings">The settings to remediate, or <c>null</c> to locate the project's.</param>
        /// <returns>The request, ready for <see cref="MolcaRemediationPass"/>.</returns>
        public static MolcaRemediationRequest Request(
            RemediationPolicy policy = RemediationPolicy.SafeOnly,
            IReadOnlyCollection<string> fixIdFilter = null,
            ContentPackageSettings settings = null)
            => new MolcaRemediationRequest(Domain, () => Project(settings))
            {
                Policy = policy,
                FixIdFilter = fixIdFilter,
                UndoGroupName = "Content package remediation",
            };

        private static ContentPackageSettings FindSettings()
        {
            // Read-only lookup: locating settings must never create an asset as a side effect.
            var guid = AssetDatabase.FindAssets($"t:{nameof(ContentPackageSettings)}").FirstOrDefault();
            return string.IsNullOrEmpty(guid)
                ? null
                : AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(AssetDatabase.GUIDToAssetPath(guid));
        }
    }

    /// <summary>
    /// Base for content fixes: resolves the domain context, honours the read-only zones, and implements
    /// dry-run by asking a read-only predicate rather than by mutating and rolling back.
    /// </summary>
    /// <remarks>
    /// <see cref="ContentPackageEditingService"/> has no dry-run mode — correctly, since its job is to write.
    /// A previewing fix therefore answers "would this change anything?" from the authored data itself, which
    /// keeps <see cref="MolcaRemediationPass.Plan"/> genuinely side-effect free.
    /// </remarks>
    internal abstract class ContentFixBase : MolcaFixBase
    {
        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            if (!(target?.DomainContext is ContentFixDomainContext domain) || domain.Settings == null)
                return MolcaFixOutcome.NotApplied(
                    "No ContentPackageSettings asset is available for this finding.");

            var readOnly = domain.Editing.ReadOnlyReason();
            if (readOnly != null)
                return MolcaFixOutcome.NotApplied(readOnly);

            var config = domain.Config;
            if (config == null)
                return MolcaFixOutcome.NotApplied(
                    "The finding is release-wide, or names no package that still exists, so there is nothing "
                    + "for this fix to change.");

            if (!WouldChange(config))
                return MolcaFixOutcome.NotApplied(NothingToDoMessage);

            if (dryRun)
                return new MolcaFixOutcome(true, $"Would apply '{Id}' to '{config.packageId}': {Description}");

            var result = Mutate(domain.Editing, config.packageId);
            return result.Changed
                ? new MolcaFixOutcome(true, result.Message, result.Before, result.After)
                : MolcaFixOutcome.NotApplied(result.Message);
        }

        /// <summary>Whether the authored data still contains what this fix removes or fills.</summary>
        /// <param name="config">The package config to inspect. Never mutated.</param>
        /// <returns><c>true</c> when applying would change something.</returns>
        protected abstract bool WouldChange(ContentPackageSettings.PackageConfig config);

        /// <summary>Performs the change through the editing service.</summary>
        /// <param name="editing">The single writer for content settings.</param>
        /// <param name="packageId">The package to change.</param>
        /// <returns>The service's result.</returns>
        protected abstract ContentEditResult Mutate(
            ContentPackageEditingService editing, string packageId);

        /// <summary>What to report when the finding is present but nothing matches.</summary>
        protected abstract string NothingToDoMessage { get; }

        /// <summary>The package's non-blank labels, never null.</summary>
        protected static IEnumerable<string> NonBlankLabels(ContentPackageSettings.PackageConfig config) =>
            (config.addressableLabels ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l));

        /// <summary>The package's non-blank dependency ids, never null.</summary>
        protected static IEnumerable<string> DependencyIds(ContentPackageSettings.PackageConfig config) =>
            (config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.packageId))
            .Select(d => d.packageId);
    }

    /// <summary>Removes repeated Addressables labels, keeping the first occurrence of each.</summary>
    /// <remarks>
    /// A repeated label is redundancy with exactly one resolution: the set of labels is unchanged by removing
    /// the copy, so nothing the package ships can change.
    /// </remarks>
    internal sealed class ContentDuplicateLabelFix : ContentFixBase
    {
        /// <inheritdoc/>
        public override string Id => "content.dedupe-labels";

        /// <inheritdoc/>
        public override string Description => "Removes repeated Addressables labels from a package.";

        /// <inheritdoc/>
        public override string HandledFindingCode =>
            ContentPackageRemediationBridge.CodeFor("label_duplicate");

        /// <inheritdoc/>
        protected override string NothingToDoMessage => "No repeated labels remain.";

        /// <inheritdoc/>
        protected override bool WouldChange(ContentPackageSettings.PackageConfig config)
        {
            var labels = NonBlankLabels(config).ToList();
            return labels.Count != labels.Distinct(StringComparer.Ordinal).Count();
        }

        /// <inheritdoc/>
        protected override ContentEditResult Mutate(
            ContentPackageEditingService editing, string packageId) => editing.DedupeLabels(packageId);
    }

    /// <summary>Removes blank Addressables label entries.</summary>
    /// <remarks>
    /// A blank label resolves to nothing, so removing it cannot change what the package ships. Distinct from
    /// <c>labels_missing</c> (a package declaring no labels at all), which may be a legitimate metadata-only
    /// package and is therefore never "fixed".
    /// </remarks>
    internal sealed class ContentEmptyLabelFix : ContentFixBase
    {
        /// <inheritdoc/>
        public override string Id => "content.remove-empty-labels";

        /// <inheritdoc/>
        public override string Description => "Removes blank Addressables label entries from a package.";

        /// <inheritdoc/>
        public override string HandledFindingCode =>
            ContentPackageRemediationBridge.CodeFor("label_empty");

        /// <inheritdoc/>
        protected override string NothingToDoMessage => "No blank label entries remain.";

        /// <inheritdoc/>
        protected override bool WouldChange(ContentPackageSettings.PackageConfig config) =>
            (config.addressableLabels ?? Array.Empty<string>()).Any(string.IsNullOrWhiteSpace);

        /// <inheritdoc/>
        protected override ContentEditResult Mutate(
            ContentPackageEditingService editing, string packageId) => editing.RemoveEmptyLabels(packageId);
    }

    /// <summary>Removes repeated dependency entries, keeping the first occurrence of each id.</summary>
    /// <remarks>
    /// Distinct-but-related problems — a missing dependency, a cycle, a required package depending on an
    /// optional one — are decisions about which edge is wrong, and stay report-only.
    /// </remarks>
    internal sealed class ContentDuplicateDependencyFix : ContentFixBase
    {
        /// <inheritdoc/>
        public override string Id => "content.dedupe-dependencies";

        /// <inheritdoc/>
        public override string Description => "Removes repeated dependency entries from a package.";

        /// <inheritdoc/>
        public override string HandledFindingCode =>
            ContentPackageRemediationBridge.CodeFor("dependency_duplicate");

        /// <inheritdoc/>
        protected override string NothingToDoMessage => "No repeated dependency entries remain.";

        /// <inheritdoc/>
        protected override bool WouldChange(ContentPackageSettings.PackageConfig config)
        {
            var ids = DependencyIds(config).ToList();
            return ids.Count != ids.Distinct(StringComparer.Ordinal).Count();
        }

        /// <inheritdoc/>
        protected override ContentEditResult Mutate(
            ContentPackageEditingService editing, string packageId) => editing.DedupeDependencies(packageId);
    }

    /// <summary>
    /// Removes a package's dependency on itself.
    /// </summary>
    /// <remarks>
    /// Deterministic — a self-edge can never be satisfiable, so removing it is the only resolution — but it
    /// is marked destructive because the entry was authored deliberately and its presence usually signals a
    /// modelling mistake the author should see rather than have tidied away. It is therefore opt-in.
    /// </remarks>
    internal sealed class ContentSelfDependencyFix : ContentFixBase
    {
        /// <inheritdoc/>
        public override string Id => "content.remove-self-dependency";

        /// <inheritdoc/>
        public override string Description => "Removes a package's dependency on itself.";

        /// <inheritdoc/>
        public override string HandledFindingCode =>
            ContentPackageRemediationBridge.CodeFor("dependency_self");

        /// <inheritdoc/>
        public override bool IsDestructive => true;

        /// <inheritdoc/>
        protected override string NothingToDoMessage => "No self-dependency remains.";

        /// <inheritdoc/>
        protected override bool WouldChange(ContentPackageSettings.PackageConfig config) =>
            DependencyIds(config).Any(id => string.Equals(id, config.packageId, StringComparison.Ordinal));

        /// <inheritdoc/>
        protected override ContentEditResult Mutate(
            ContentPackageEditingService editing, string packageId) =>
            editing.RemoveSelfDependency(packageId);
    }

    // There is deliberately no fix for `package_display_name_missing`.
    //
    // It looked like a cheap Yellow — derive the name from the id — and shipping it required declaring the
    // fix destructive, which is false: filling a blank field discards nothing. That lie was the tell. The
    // facets describe what a change *does*, and there is no facet for "mechanically possible but a bad idea",
    // because the answer in that case is not to offer the fix.
    //
    // And it is a bad idea: ContentValidation raises this at Error severity, so it blocks publishing. A
    // display name is content, and inventing content to clear a release gate is the same failure mode as
    // auto-translating a missing string — the gate stops complaining while the underlying problem, that
    // nobody has named this package, silently ships. `ContentPackageEditingService.DeriveDisplayNameFromId`
    // remains available for a human who wants it.
}
