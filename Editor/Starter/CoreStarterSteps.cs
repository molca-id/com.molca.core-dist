using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Starter
{
    /// <summary>Creates the project's <see cref="GlobalSettings"/> asset and wires it into project settings.</summary>
    /// <remarks>
    /// Generated, not copied. <see cref="GlobalSettings.GetOrCreateSettings"/> already owns this and is
    /// reused rather than duplicated — the starter's job is to sequence the setup, not to become a second
    /// creator of anything.
    /// </remarks>
    internal sealed class GlobalSettingsStarterStep : IMolcaStarterStep
    {
        /// <inheritdoc/>
        public string Id => "starter.global-settings";

        /// <inheritdoc/>
        public string Title => "Global Settings";

        /// <inheritdoc/>
        public string Description =>
            "Creates the GlobalSettings asset that holds every setting module, and points the project "
            + "settings at it.";

        /// <inheritdoc/>
        public int Order => 10;

        /// <inheritdoc/>
        public bool IsSatisfied() => MolcaProjectSettings.Instance != null
                                     && MolcaProjectSettings.Instance.GlobalSettings != null;

        /// <inheritdoc/>
        public MolcaStarterOutcome Apply(bool dryRun, CancellationToken cancellationToken)
        {
            if (MolcaProjectSettings.Instance == null)
                return MolcaStarterOutcome.NoChange(
                    "No MolcaProjectSettings asset could be created; the install may be broken.");

            if (dryRun)
                return new MolcaStarterOutcome(true, "Would create a GlobalSettings asset and assign it.");

            var created = GlobalSettings.GetOrCreateSettings();
            if (created == null)
                return MolcaStarterOutcome.NoChange("The GlobalSettings asset could not be created.");

            var path = AssetDatabase.GetAssetPath(created);
            return new MolcaStarterOutcome(true, $"Created and assigned '{path}'.", new[] { path });
        }
    }

    /// <summary>
    /// Creates and registers one instance of every setting module the project's assemblies define — the
    /// step that makes "all features enabled" mean something specific.
    /// </summary>
    /// <remarks>
    /// <para><b>The set is derived, not listed.</b> Every concrete <see cref="SettingModule"/> subclass found
    /// by <c>TypeCache</c> is a feature this framework offers, so enabling all of them is exactly "one of
    /// each". A hardcoded list would go stale the moment a module is added, and a fork's own modules would
    /// never appear in it; derived, they are included automatically.</para>
    /// <para>Each module is generated from its own field initializers. Values that cannot be defaulted —
    /// a server origin, a brand palette, which locales to ship — stay at their defaults and are the author's
    /// to fill in. The starter gets a project to "every feature is present and running", not to
    /// "configured for your product".</para>
    /// <para>Modules already registered are left alone, including ones a project registered by hand, so
    /// re-running never duplicates or overwrites.</para>
    /// </remarks>
    internal sealed class SettingModulesStarterStep : IMolcaStarterStep
    {
        /// <inheritdoc/>
        public string Id => "starter.setting-modules";

        /// <inheritdoc/>
        public string Title => "Setting Modules";

        /// <inheritdoc/>
        public string Description =>
            "Creates and registers one of every setting module the framework offers, each with its own "
            + "defaults, so all features are present.";

        /// <inheritdoc/>
        public int Order => 20;

        /// <inheritdoc/>
        public bool IsSatisfied() => MissingModuleTypes().Count == 0;

        /// <inheritdoc/>
        public MolcaStarterOutcome Apply(bool dryRun, CancellationToken cancellationToken)
        {
            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
                return MolcaStarterOutcome.NoChange(
                    "No GlobalSettings asset yet — the Global Settings step has to run first.");

            var missing = MissingModuleTypes();
            if (missing.Count == 0)
                return MolcaStarterOutcome.NoChange("Every setting module is already registered.");

            if (dryRun)
                return new MolcaStarterOutcome(
                    true,
                    $"Would create and register {missing.Count} module(s): "
                    + string.Join(", ", missing.Select(t => t.Name)));

            var folder = MolcaStarter.EnsureSettingsFolder();
            var created = new List<string>();
            var names = new List<string>();

            var serialized = new SerializedObject(globalSettings);
            var array = serialized.FindProperty("modules");
            if (array == null || !array.isArray)
                return MolcaStarterOutcome.NoChange("GlobalSettings has no 'modules' array to register into.");

            foreach (var type in missing)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var module = ScriptableObject.CreateInstance(type) as SettingModule;
                if (module == null) continue;

                var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{type.Name}.asset");
                AssetDatabase.CreateAsset(module, path);

                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);
                array.GetArrayElementAtIndex(index).objectReferenceValue = module;

                created.Add(path);
                names.Add(type.Name);
            }

            Undo.SetCurrentGroupName("Molca starter: setting modules");
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return created.Count == 0
                ? MolcaStarterOutcome.NoChange("No module could be created.")
                : new MolcaStarterOutcome(
                    true, $"Created and registered {created.Count}: {string.Join(", ", names)}", created);
        }

        /// <summary>Concrete setting-module types the project defines but GlobalSettings does not register.</summary>
        /// <remarks>
        /// Test-assembly modules are excluded: a fixture's throwaway module is not a feature, and creating
        /// an asset for one in a real project would be pure noise.
        /// </remarks>
        internal static IReadOnlyList<Type> MissingModuleTypes()
        {
            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            var registered = new HashSet<Type>();
            foreach (var module in globalSettings?.modules ?? Array.Empty<SettingModule>())
                if (module != null) registered.Add(module.GetType());

            return TypeCache.GetTypesDerivedFrom<SettingModule>()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
                .Where(t => !IsTestType(t))
                // A subclass already registered satisfies its base: the feature is present.
                .Where(t => !registered.Any(r => t.IsAssignableFrom(r)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsTestType(Type type)
        {
            var assembly = type.Assembly.GetName().Name ?? string.Empty;
            return assembly.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0
                   || assembly.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>Points the project settings at a RuntimeManager prefab when exactly one exists.</summary>
    /// <remarks>
    /// Assigns only; it never generates a prefab. Which object bootstraps the application is an authoring
    /// decision, and with zero or several candidates the step reports what it found rather than choosing.
    /// </remarks>
    internal sealed class RuntimeManagerStarterStep : IMolcaStarterStep
    {
        /// <inheritdoc/>
        public string Id => "starter.runtime-manager";

        /// <inheritdoc/>
        public string Title => "RuntimeManager";

        /// <inheritdoc/>
        public string Description =>
            "Points the project settings at the RuntimeManager prefab that bootstraps the application.";

        /// <inheritdoc/>
        public int Order => 30;

        /// <inheritdoc/>
        public bool IsSatisfied() => MolcaProjectSettings.Instance != null
                                     && MolcaProjectSettings.Instance.RuntimeManager != null;

        /// <inheritdoc/>
        public MolcaStarterOutcome Apply(bool dryRun, CancellationToken cancellationToken)
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
                return MolcaStarterOutcome.NoChange("No MolcaProjectSettings asset is available.");

            var candidates = Remediation.Provisioning.BootstrapProvisioning.FindRuntimeManagerPrefabs();
            if (candidates.Count == 0)
                return MolcaStarterOutcome.NoChange(
                    "No RuntimeManager prefab exists yet. Create one with the subsystems this project "
                    + "needs, then re-run — which subsystems it carries is the decision that shapes the app.");

            if (candidates.Count > 1)
                return MolcaStarterOutcome.NoChange(
                    $"{candidates.Count} RuntimeManager prefabs exist; assign the intended one in the "
                    + "project settings.");

            var only = candidates[0];
            var path = AssetDatabase.GetAssetPath(only);
            if (dryRun)
                return new MolcaStarterOutcome(true, $"Would assign the only RuntimeManager prefab, '{path}'.");

            var serialized = new SerializedObject(projectSettings);
            var property = serialized.FindProperty("runtimeManager");
            if (property == null)
                return MolcaStarterOutcome.NoChange("MolcaProjectSettings has no 'runtimeManager' field.");

            property.objectReferenceValue = only;
            Undo.SetCurrentGroupName("Molca starter: RuntimeManager");
            serialized.ApplyModifiedProperties();

            return new MolcaStarterOutcome(true, $"Assigned '{path}'.");
        }
    }

    /// <summary>
    /// Generates the per-platform <see cref="Molca.Utilities.BudgetSettings"/> assets the performance
    /// monitor and the scene audit grade against.
    /// </summary>
    /// <remarks>
    /// <para>These three used to ship as <c>.asset</c> files inside Core, where the numbers were both
    /// un-editable (an immutable install cannot be written to) and volatile (an upgrade replaced them).
    /// The thresholds now live in <see cref="Molca.Utilities.BudgetSettings.Create"/>, and this step
    /// materializes them into project space where the author owns them.</para>
    /// <para>Assigning them to a <c>BudgetMonitor</c> is left to the author: which scene carries the
    /// overlay, and whether it ships at all, is a decision the starter has no basis to make.</para>
    /// </remarks>
    internal sealed class BudgetSettingsStarterStep : IMolcaStarterStep
    {
        private const string Folder = MolcaStarter.SettingsFolder + "/Budgets";

        /// <inheritdoc/>
        public string Id => "starter.budget-settings";

        /// <inheritdoc/>
        public string Title => "Performance Budgets";

        /// <inheritdoc/>
        public string Description =>
            "Creates the PC, Mobile and Quest performance budgets that the budget monitor and the scene "
            + "audit grade against.";

        /// <inheritdoc/>
        public int Order => 40;

        /// <inheritdoc/>
        public bool IsSatisfied() => MissingPresets().Count == 0;

        /// <inheritdoc/>
        public MolcaStarterOutcome Apply(bool dryRun, CancellationToken cancellationToken)
        {
            var missing = MissingPresets();
            if (missing.Count == 0)
                return MolcaStarterOutcome.NoChange("Every platform budget already exists.");

            if (dryRun)
                return new MolcaStarterOutcome(
                    true,
                    $"Would create {missing.Count} budget(s): "
                    + string.Join(", ", missing.Select(Molca.Utilities.BudgetSettings.PresetAssetName)));

            if (!AssetDatabase.IsValidFolder(Folder))
            {
                MolcaStarter.EnsureSettingsFolder();
                System.IO.Directory.CreateDirectory(Folder);
                AssetDatabase.Refresh();
            }

            var created = new List<string>();
            foreach (var preset in missing)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var settings = Molca.Utilities.BudgetSettings.Create(preset);
                var name = Molca.Utilities.BudgetSettings.PresetAssetName(preset);
                var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder}/{name}.asset");
                AssetDatabase.CreateAsset(settings, path);
                created.Add(path);
            }

            Undo.SetCurrentGroupName("Molca starter: performance budgets");
            AssetDatabase.SaveAssets();

            return created.Count == 0
                ? MolcaStarterOutcome.NoChange("No budget could be created.")
                : new MolcaStarterOutcome(true, $"Created {created.Count} platform budget(s).", created);
        }

        /// <summary>Presets with no matching asset anywhere in the project.</summary>
        /// <remarks>
        /// Matched by name rather than by path, because that is how
        /// <see cref="Molca.Utilities.BudgetSettingsProvider"/> resolves them at runtime: a budget the
        /// author already wrote and named correctly counts, wherever it lives.
        /// </remarks>
        internal static IReadOnlyList<Molca.Utilities.BudgetPreset> MissingPresets()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(Molca.Utilities.BudgetSettings)))
                existing.Add(System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)));

            return Enum.GetValues(typeof(Molca.Utilities.BudgetPreset))
                .Cast<Molca.Utilities.BudgetPreset>()
                .Where(p => !existing.Contains(Molca.Utilities.BudgetSettings.PresetAssetName(p)))
                .ToList();
        }
    }
}
