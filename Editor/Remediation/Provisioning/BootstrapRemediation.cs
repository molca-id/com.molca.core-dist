using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.Remediation;
using Molca.Editor.Settings;
using Molca.Settings;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Remediation.Provisioning
{
    /// <summary>
    /// Travels in <see cref="MolcaFixTarget.DomainContext"/> so a bootstrap fix has the finding without
    /// re-running the check.
    /// </summary>
    public sealed class BootstrapFixDomainContext
    {
        /// <summary>Creates a carrier.</summary>
        /// <param name="finding">The finding being remediated.</param>
        public BootstrapFixDomainContext(BootstrapFinding finding) => Finding = finding;

        /// <summary>The finding being remediated.</summary>
        public BootstrapFinding Finding { get; }
    }

    /// <summary>
    /// Projects the bootstrap configuration check into the shared remediation pass — the day-one
    /// "nothing is wired up yet" surface.
    /// </summary>
    public static class BootstrapRemediationBridge
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "bootstrap";

        /// <summary>Runs the read-only bootstrap check and projects its findings as fix targets.</summary>
        /// <returns>The projection. Never null.</returns>
        public static MolcaAuditProjection Project()
        {
            var targets = BootstrapCheck.Run()
                .Select(finding => new MolcaFixTarget(
                    finding.Code,
                    finding.Context != null
                        ? AssetDatabase.GetAssetPath(finding.Context)
                        : "(project)",
                    finding.Message,
                    finding.Index >= 0 ? finding.Index.ToString() : null,
                    new BootstrapFixDomainContext(finding)))
                .ToList();

            return new MolcaAuditProjection(targets);
        }

        /// <summary>Builds a remediation request for bootstrap configuration.</summary>
        /// <param name="policy">Which fixes may auto-apply.</param>
        /// <param name="fixIdFilter">Restricts the pass to these fix ids; <c>null</c> means all the policy allows.</param>
        /// <returns>The request, ready for <see cref="MolcaRemediationPass"/>.</returns>
        public static MolcaRemediationRequest Request(
            RemediationPolicy policy = RemediationPolicy.SafeOnly,
            IReadOnlyCollection<string> fixIdFilter = null)
            => new MolcaRemediationRequest(Domain, Project)
            {
                Policy = policy,
                FixIdFilter = fixIdFilter,
                UndoGroupName = "Bootstrap remediation",
            };
    }

    /// <summary>
    /// Removes null entries from <c>GlobalSettings.modules</c>.
    /// </summary>
    /// <remarks>
    /// The one genuinely Green bootstrap fix: a null slot loads nothing, so deleting it cannot change what
    /// the project configures. Compaction runs backwards over the collected indices because deleting from a
    /// <see cref="SerializedProperty"/> array shifts every later element.
    /// </remarks>
    internal sealed class BootstrapModuleEntryNullFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "bootstrap.compact-module-entries";

        /// <inheritdoc/>
        public override string Description => "Removes null entries from GlobalSettings.modules.";

        /// <inheritdoc/>
        public override string HandledFindingCode => BootstrapFinding.CodeModuleEntryNull;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
                return MolcaFixOutcome.NotApplied(
                    "No GlobalSettings asset is assigned, so there is no module list to compact.");

            int removed = BootstrapProvisioning.CompactNullElements(
                globalSettings, "modules", dryRun, "Compact setting modules");

            return removed == 0
                ? MolcaFixOutcome.NotApplied("No null module entries remain.")
                : new MolcaFixOutcome(
                    true,
                    dryRun
                        ? $"Would remove {removed} null module entr{(removed == 1 ? "y" : "ies")}."
                        : $"Removed {removed} null module entr{(removed == 1 ? "y" : "ies")}.",
                    $"{removed} null entries", "none");
        }
    }

    /// <summary>Removes null entries from <c>MolcaProjectSettings.BootstrapExtensions</c>.</summary>
    /// <remarks>Green for the same reason as the module compaction: a null extension runs nothing.</remarks>
    internal sealed class BootstrapExtensionEntryNullFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "bootstrap.compact-extension-entries";

        /// <inheritdoc/>
        public override string Description =>
            "Removes null entries from MolcaProjectSettings.BootstrapExtensions.";

        /// <inheritdoc/>
        public override string HandledFindingCode => BootstrapFinding.CodeExtensionEntryNull;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
                return MolcaFixOutcome.NotApplied("No MolcaProjectSettings asset is available.");

            int removed = BootstrapProvisioning.CompactNullElements(
                projectSettings, "bootstrapExtensions", dryRun, "Compact bootstrap extensions");

            return removed == 0
                ? MolcaFixOutcome.NotApplied("No null extension entries remain.")
                : new MolcaFixOutcome(
                    true,
                    dryRun
                        ? $"Would remove {removed} null extension entr{(removed == 1 ? "y" : "ies")}."
                        : $"Removed {removed} null extension entr{(removed == 1 ? "y" : "ies")}.",
                    $"{removed} null entries", "none");
        }
    }

    /// <summary>
    /// Creates and assigns a <see cref="GlobalSettings"/> asset when the project names none.
    /// </summary>
    /// <remarks>
    /// A provisioning fix: it creates an asset, so it declares <see cref="FixReversibility.FileSnapshot"/>
    /// (Unity Undo cannot reliably remove a created asset) and stays out of the safe pass. It delegates to
    /// the existing <see cref="GlobalSettings.GetOrCreateSettings"/> rather than introducing a second
    /// creation path — that method already owns the location and the wiring back into project settings.
    /// The created asset carries only its own defaults; nothing is guessed.
    /// </remarks>
    internal sealed class BootstrapGlobalSettingsFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "bootstrap.create-global-settings";

        /// <inheritdoc/>
        public override string Description =>
            "Creates an empty GlobalSettings asset and assigns it to the project settings.";

        /// <inheritdoc/>
        public override string HandledFindingCode => BootstrapFinding.CodeGlobalSettingsUnset;

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
                return MolcaFixOutcome.NotApplied("No MolcaProjectSettings asset is available.");

            if (projectSettings.GlobalSettings != null)
                return MolcaFixOutcome.NotApplied("A GlobalSettings asset is already assigned.");

            if (dryRun)
                return new MolcaFixOutcome(
                    true, "Would create a GlobalSettings asset and assign it to the project settings.",
                    "(none)", "new GlobalSettings");

            var created = GlobalSettings.GetOrCreateSettings();
            if (created == null)
                return MolcaFixOutcome.NotApplied("The asset could not be created.");

            var path = AssetDatabase.GetAssetPath(created);
            return new MolcaFixOutcome(
                true,
                $"Created and assigned '{path}'. It carries no modules yet — add the ones this project needs.",
                "(none)",
                path,
                // Makes the declared FileSnapshot reversibility true: reverting deletes what was created.
                Mcp.McpUndoStack.RecordCreated(path, Id, $"Created GlobalSettings at '{path}'"));
        }
    }

    /// <summary>
    /// Assigns the RuntimeManager prefab when exactly one exists in the project.
    /// </summary>
    /// <remarks>
    /// With zero or several candidates this declines: which prefab bootstraps the application is an
    /// authoring decision, and picking one silently would start the wrong app. Only the assignment is made —
    /// no prefab is ever created.
    /// </remarks>
    internal sealed class BootstrapRuntimeManagerFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "bootstrap.assign-sole-runtime-manager";

        /// <inheritdoc/>
        public override string Description =>
            "Assigns the RuntimeManager prefab when the project contains exactly one.";

        /// <inheritdoc/>
        public override string HandledFindingCode => BootstrapFinding.CodeRuntimeManagerUnset;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
                return MolcaFixOutcome.NotApplied("No MolcaProjectSettings asset is available.");

            if (projectSettings.RuntimeManager != null)
                return MolcaFixOutcome.NotApplied("A RuntimeManager prefab is already assigned.");

            var candidates = BootstrapProvisioning.FindRuntimeManagerPrefabs();
            if (candidates.Count == 0)
                return MolcaFixOutcome.NotApplied(
                    "No RuntimeManager prefab exists in the project. Create one, then assign it.");

            if (candidates.Count > 1)
                return MolcaFixOutcome.NotApplied(
                    $"{candidates.Count} RuntimeManager prefabs exist "
                    + $"({string.Join(", ", candidates.Select(AssetDatabase.GetAssetPath))}); which one "
                    + "bootstraps the application is an authoring decision.");

            var only = candidates[0];
            var path = AssetDatabase.GetAssetPath(only);
            if (dryRun)
                return new MolcaFixOutcome(true, $"Would assign the only RuntimeManager prefab, '{path}'.",
                    "(none)", path);

            var serialized = new SerializedObject(projectSettings);
            var property = serialized.FindProperty("runtimeManager");
            if (property == null)
                return MolcaFixOutcome.NotApplied(
                    "MolcaProjectSettings has no 'runtimeManager' field to assign.");

            property.objectReferenceValue = only;
            Undo.SetCurrentGroupName("Assign RuntimeManager prefab");
            serialized.ApplyModifiedProperties();

            return new MolcaFixOutcome(true, $"Assigned RuntimeManager prefab '{path}'.", "(none)", path);
        }
    }

    /// <summary>
    /// Creates the <see cref="SettingModule"/> a subsystem declares it requires, and registers it in
    /// <c>GlobalSettings.modules</c>.
    /// </summary>
    /// <remarks>
    /// Locally decidable because the subsystem names the exact type in
    /// <see cref="RequiresSettingModuleAttribute"/> — there is nothing to guess about <em>which</em> module
    /// is missing. What it cannot know is how the module should be configured, so the asset carries only
    /// its own serialized defaults; anything the project must actually decide stays at its default and
    /// visible. Creating an asset means <see cref="FixReversibility.FileSnapshot"/> and no place in the
    /// safe pass.
    /// <para>Placement follows the GlobalSettings asset rather than a constant, so a project that moved its
    /// settings keeps its modules beside them.</para>
    /// </remarks>
    internal sealed class BootstrapMissingModuleFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "bootstrap.create-required-module";

        /// <inheritdoc/>
        public override string Description =>
            "Creates the setting module a subsystem requires and registers it in GlobalSettings.";

        /// <inheritdoc/>
        public override string HandledFindingCode => BootstrapFinding.CodeModuleMissing;

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            if (!(target?.DomainContext is BootstrapFixDomainContext domain)
                || domain.Finding?.Subject == null)
                return MolcaFixOutcome.NotApplied(
                    "The finding names no module type, so there is nothing to create.");

            var moduleType = domain.Finding.Subject;
            if (moduleType.IsAbstract)
                return MolcaFixOutcome.NotApplied(
                    $"'{moduleType.Name}' is abstract. A subsystem must require a concrete module type, or "
                    + "the project must choose which implementation to use.");

            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
                return MolcaFixOutcome.NotApplied(
                    "No GlobalSettings asset is assigned yet — create that first.");

            var folder = BootstrapProvisioning.FolderOf(globalSettings);
            if (folder == null)
                return MolcaFixOutcome.NotApplied(
                    "The GlobalSettings asset has no project path (an in-memory instance), so a module "
                    + "asset cannot be created beside it.");

            if (dryRun)
                return new MolcaFixOutcome(
                    true,
                    $"Would create a '{moduleType.Name}' asset in '{folder}' and register it.",
                    "(missing)", moduleType.Name);

            var created = BootstrapProvisioning.CreateAndRegisterModule(moduleType, globalSettings, folder);
            if (created == null)
                return MolcaFixOutcome.NotApplied($"Could not create a '{moduleType.Name}' asset.");

            var path = AssetDatabase.GetAssetPath(created);
            return new MolcaFixOutcome(
                true,
                $"Created '{path}' and registered it. It carries only default values — review them.",
                "(missing)",
                path,
                Mcp.McpUndoStack.RecordCreated(path, Id, $"Created setting module '{path}'"));
        }
    }

    /// <summary>
    /// Shared mechanics for bootstrap provisioning fixes.
    /// </summary>
    /// <remarks>
    /// <para><b>Provisioning delegates; it never re-implements.</b> Core already seeds
    /// <see cref="MolcaProjectSettings"/> from a package template on first editor access, and
    /// <see cref="GlobalSettings.GetOrCreateSettings"/> already owns where a GlobalSettings asset goes. A
    /// parallel creation path here would be a second source of truth for asset placement — the same mistake
    /// as a second settings writer.</para>
    /// <para>Consequently no fix hardcodes a path, and none creates assets inside a read-only zone.</para>
    /// </remarks>
    internal static class BootstrapProvisioning
    {
        /// <summary>
        /// Deletes null elements from a serialized array field.
        /// </summary>
        /// <param name="owner">The asset owning the field.</param>
        /// <param name="fieldName">The serialized array field name.</param>
        /// <param name="dryRun">When true, count without writing.</param>
        /// <param name="undoName">Undo group name for the write.</param>
        /// <returns>How many elements were (or would be) removed.</returns>
        internal static int CompactNullElements(
            UnityEngine.Object owner, string fieldName, bool dryRun, string undoName)
        {
            var serialized = new SerializedObject(owner);
            var array = serialized.FindProperty(fieldName);
            if (array == null || !array.isArray) return 0;

            var doomed = new List<int>();
            for (int i = 0; i < array.arraySize; i++)
                if (array.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    doomed.Add(i);

            if (doomed.Count == 0 || dryRun) return doomed.Count;

            // Backwards: deleting shifts every later index. Note that for an object-reference array Unity's
            // first DeleteArrayElementAtIndex only nulls the slot, so the call is repeated per index until
            // the element is actually gone.
            foreach (var index in Enumerable.Reverse(doomed))
            {
                int size = array.arraySize;
                array.DeleteArrayElementAtIndex(index);
                if (array.arraySize == size) array.DeleteArrayElementAtIndex(index);
            }

            Undo.SetCurrentGroupName(undoName);
            serialized.ApplyModifiedProperties();
            return doomed.Count;
        }

        /// <summary>The folder containing an asset, or <c>null</c> for an in-memory instance.</summary>
        /// <param name="asset">The asset whose folder to resolve.</param>
        /// <returns>A project-relative folder path, or <c>null</c>.</returns>
        internal static string FolderOf(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;
            var folder = System.IO.Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(folder) ? null : folder.Replace('\\', '/');
        }

        /// <summary>
        /// Creates a setting-module asset and appends it to <c>GlobalSettings.modules</c>.
        /// </summary>
        /// <param name="moduleType">Concrete <see cref="SettingModule"/> type to create.</param>
        /// <param name="globalSettings">The settings asset to register it in.</param>
        /// <param name="folder">Folder to create the asset in.</param>
        /// <returns>The created module, or <c>null</c> on failure.</returns>
        /// <remarks>
        /// The registration goes through a <see cref="SerializedObject"/> so Ctrl+Z takes it back, and the
        /// caller records the created path with <c>McpUndoStack.RecordCreated</c> so the asset itself is
        /// deleted on revert. Both halves are needed: Unity Undo cannot remove a created asset, and the undo
        /// stack does not track in-memory edits.
        /// </remarks>
        internal static SettingModule CreateAndRegisterModule(
            System.Type moduleType, GlobalSettings globalSettings, string folder)
        {
            var module = ScriptableObject.CreateInstance(moduleType) as SettingModule;
            if (module == null) return null;

            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{moduleType.Name}.asset");
            AssetDatabase.CreateAsset(module, path);
            AssetDatabase.SaveAssets();

            var serialized = new SerializedObject(globalSettings);
            var array = serialized.FindProperty("modules");
            if (array == null || !array.isArray)
            {
                // Registration is the point; an unregistered asset would leave the finding standing while
                // littering the project, so the creation is rolled back rather than half-done.
                AssetDatabase.DeleteAsset(path);
                return null;
            }

            int index = array.arraySize;
            array.InsertArrayElementAtIndex(index);
            array.GetArrayElementAtIndex(index).objectReferenceValue = module;
            Undo.SetCurrentGroupName("Register setting module");
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return module;
        }

        /// <summary>Finds every prefab carrying a <see cref="RuntimeManager"/> component.</summary>
        /// <returns>The candidate prefabs' RuntimeManager components, in path order.</returns>
        internal static IReadOnlyList<RuntimeManager> FindRuntimeManagerPrefabs() =>
            AssetDatabase.FindAssets("t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(go => go != null)
                .Select(go => go.GetComponent<RuntimeManager>())
                .Where(rm => rm != null)
                .ToList();
    }

    /// <summary>Registers bootstrap as a project-wide remediation domain.</summary>
    internal sealed class BootstrapRemediationDomainProvider : IMolcaRemediationDomainProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaRemediationDomain> GetDomains() => new[]
        {
            // Ordered first: if bootstrap is broken, every other domain's findings are downstream noise.
            new MolcaRemediationDomain(
                BootstrapRemediationBridge.Domain, "Bootstrap",
                createRequest: policy => BootstrapRemediationBridge.Request(policy),
                order: 0),
        };
    }
}
