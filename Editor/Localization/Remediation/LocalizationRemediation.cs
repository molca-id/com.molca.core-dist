using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Remediation;
using Molca.Localization;
using Molca.Settings;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Localization.Remediation
{
    /// <summary>
    /// Travels in <see cref="MolcaFixTarget.DomainContext"/> so a localization fix has the finding without
    /// re-running the audit.
    /// </summary>
    public sealed class LocalizationFixDomainContext
    {
        /// <summary>Creates a carrier.</summary>
        /// <param name="finding">The finding being remediated.</param>
        public LocalizationFixDomainContext(LocalizationAuditFinding finding) => Finding = finding;

        /// <summary>The finding being remediated.</summary>
        public LocalizationAuditFinding Finding { get; }
    }

    /// <summary>
    /// Projects the localization audit into the shared remediation pass.
    /// </summary>
    /// <remarks>
    /// <para>Almost every localization finding is report-only, and that is a finding about the domain rather
    /// than a gap in the work: a missing translation needs a translator, a placeholder mismatch needs
    /// someone who knows what the string means, and a fallback graph is policy. The one mechanical repair is
    /// registering a module the project has already authored but never wired up.</para>
    /// <para>The audit itself is read-only and stays so; this only re-shapes its snapshot.</para>
    /// </remarks>
    public static class LocalizationRemediationBridge
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "localization";

        /// <summary>Runs the localization audit and projects its findings as fix targets.</summary>
        /// <param name="request">What to cover; <c>null</c> means the Doctor scope.</param>
        /// <returns>The projection. Never null.</returns>
        public static MolcaAuditProjection Project(LocalizationAuditRequest request = null)
        {
            var snapshot = LocalizationAuditEngine.Audit(
                request ?? LocalizationAuditRequest.CreateDoctorRequest());

            var targets = snapshot.Findings
                .Select(finding => new MolcaFixTarget(
                    finding.Id,
                    string.IsNullOrEmpty(finding.Path) ? "(project)" : finding.Path,
                    finding.Message,
                    finding.PropertyPath,
                    new LocalizationFixDomainContext(finding)))
                .ToList();

            return new MolcaAuditProjection(
                targets,
                snapshot.Status == LocalizationAuditStatus.Incomplete
                    ? "some declared localization inputs could not be scanned"
                    : null,
                isStale: snapshot.Status == LocalizationAuditStatus.Failed);
        }

        /// <summary>Builds a remediation request for localization.</summary>
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
                UndoGroupName = "Localization remediation",
            };
    }

    /// <summary>
    /// Registers the project's sole <see cref="LocalizationModule"/> asset in <c>GlobalSettings.modules</c>
    /// when one exists but none is registered.
    /// </summary>
    /// <remarks>
    /// <para>Locally decidable precisely when there is exactly one candidate: an unregistered module is
    /// inert, so with a single asset "which one is the active module" has one answer. With several, it is a
    /// decision the data does not record and the fix declines.</para>
    /// <para>Green rather than a provisioning fix: it creates nothing. It appends an existing asset to a
    /// serialized array through a <see cref="SerializedObject"/>, so one Ctrl+Z takes it back.</para>
    /// </remarks>
    internal sealed class LocalizationRegisterModuleFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "localization.register-sole-module";

        /// <inheritdoc/>
        public override string Description =>
            "Registers the project's only LocalizationModule asset in GlobalSettings.";

        /// <inheritdoc/>
        public override string HandledFindingCode => "localization-settings-unregistered";

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var modules = AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .Where(module => module != null)
                .ToList();

            if (modules.Count == 0)
                return MolcaFixOutcome.NotApplied(
                    "No LocalizationModule asset exists in project space, so there is nothing to register.");

            if (modules.Count > 1)
                return MolcaFixOutcome.NotApplied(
                    $"{modules.Count} LocalizationModule assets exist "
                    + $"({string.Join(", ", modules.Select(AssetDatabase.GetAssetPath))}); which one is the "
                    + "active module is a decision the project has to make.");

            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
                return MolcaFixOutcome.NotApplied(
                    "No GlobalSettings asset is assigned yet — fix that first, then register the module.");

            var only = modules[0];
            var path = AssetDatabase.GetAssetPath(only);

            if ((globalSettings.modules ?? Array.Empty<SettingModule>()).Any(m => m == only))
                return MolcaFixOutcome.NotApplied("That module is already registered.");

            if (dryRun)
                return new MolcaFixOutcome(
                    true, $"Would register the only LocalizationModule, '{path}'.", "(unregistered)", path);

            var serialized = new SerializedObject(globalSettings);
            var array = serialized.FindProperty("modules");
            if (array == null || !array.isArray)
                return MolcaFixOutcome.NotApplied("GlobalSettings has no 'modules' array to register into.");

            int index = array.arraySize;
            array.InsertArrayElementAtIndex(index);
            array.GetArrayElementAtIndex(index).objectReferenceValue = only;
            Undo.SetCurrentGroupName("Register localization module");
            serialized.ApplyModifiedProperties();

            return new MolcaFixOutcome(
                true, $"Registered '{path}' as the active localization module.", "(unregistered)", path);
        }
    }

    /// <summary>Registers localization as a project-wide remediation domain.</summary>
    internal sealed class LocalizationRemediationDomainProvider : IMolcaRemediationDomainProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaRemediationDomain> GetDomains() => new[]
        {
            new MolcaRemediationDomain(
                LocalizationRemediationBridge.Domain, "Localization",
                createRequest: policy => LocalizationRemediationBridge.Request(policy),
                order: 40),
        };
    }
}
