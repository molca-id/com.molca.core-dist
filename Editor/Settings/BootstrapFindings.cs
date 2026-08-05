using System.Collections.Generic;
using System.Linq;
using Molca.Settings;

namespace Molca.Editor.Settings
{
    /// <summary>One bootstrap-configuration problem, with a stable code remediation can key a fix to.</summary>
    /// <remarks>
    /// <see cref="BootstrapAssetValidator"/> has reported these since the framework shipped, but only as
    /// <c>Debug.LogError</c> text — which no pipeline can consume. Giving them codes is what lets the day-one
    /// failures ("nothing is wired up yet") take part in remediation at all.
    /// </remarks>
    public sealed class BootstrapFinding
    {
        /// <summary>The project settings asset is missing or could not be loaded.</summary>
        public const string CodeProjectSettingsMissing = "bootstrap.project-settings-missing";

        /// <summary>The project settings asset names no <c>GlobalSettings</c>.</summary>
        public const string CodeGlobalSettingsUnset = "bootstrap.global-settings-unset";

        /// <summary>No RuntimeManager prefab is assigned, so bootstrap cannot start.</summary>
        public const string CodeRuntimeManagerUnset = "bootstrap.runtime-manager-unset";

        /// <summary>A <c>GlobalSettings.modules</c> entry is null.</summary>
        public const string CodeModuleEntryNull = "bootstrap.module-entry-null";

        /// <summary>Two entries declare the same <see cref="SettingModule"/> type.</summary>
        public const string CodeModuleDuplicate = "bootstrap.module-duplicate";

        /// <summary>A <c>MolcaProjectSettings.BootstrapExtensions</c> entry is null.</summary>
        public const string CodeExtensionEntryNull = "bootstrap.extension-entry-null";

        /// <summary>
        /// A subsystem on the RuntimeManager prefab declares a required
        /// <see cref="SettingModule"/> that <c>GlobalSettings.modules</c> does not contain.
        /// </summary>
        public const string CodeModuleMissing = "bootstrap.module-missing";

        /// <summary>Creates a finding.</summary>
        /// <param name="code">One of the <c>Code*</c> constants.</param>
        /// <param name="message">What is wrong and what it prevents.</param>
        /// <param name="index">Index into the offending collection, or <c>-1</c>.</param>
        /// <param name="context">The asset the finding is about, for selection; may be <c>null</c>.</param>
        /// <param name="subject">
        /// The type the finding is about, when it names one — the missing <see cref="SettingModule"/> type
        /// for <see cref="CodeModuleMissing"/>. Lets a fix act on the type without parsing the message.
        /// </param>
        public BootstrapFinding(
            string code,
            string message,
            int index = -1,
            UnityEngine.Object context = null,
            System.Type subject = null)
        {
            Code = code;
            Message = message;
            Index = index;
            Context = context;
            Subject = subject;
        }

        /// <summary>The type the finding names, or <c>null</c>.</summary>
        public System.Type Subject { get; }

        /// <summary>Stable machine-readable code.</summary>
        public string Code { get; }

        /// <summary>Human-readable description.</summary>
        public string Message { get; }

        /// <summary>Index into the offending collection, or <c>-1</c> when not collection-scoped.</summary>
        public int Index { get; }

        /// <summary>The asset the finding is about; may be <c>null</c>.</summary>
        public UnityEngine.Object Context { get; }
    }

    /// <summary>
    /// The read-only bootstrap configuration check, shared by <see cref="BootstrapAssetValidator"/>'s
    /// domain-reload logging and by remediation.
    /// </summary>
    /// <remarks>
    /// <para>Pure: it loads assets and inspects them, and writes nothing. Note that
    /// <see cref="MolcaProjectSettings.Instance"/> self-seeds a project settings asset on first editor
    /// access — generated from the type's own field initializers, not cloned from any packaged template —
    /// so <see cref="BootstrapFinding.CodeProjectSettingsMissing"/> fires only when that seeding itself
    /// failed: a genuinely broken install rather than a fresh one.</para>
    /// <para>Editor-only; main thread.</para>
    /// </remarks>
    public static class BootstrapCheck
    {
        /// <summary>Runs the check.</summary>
        /// <returns>Every finding, in a stable order. Empty when bootstrap is correctly configured.</returns>
        public static IReadOnlyList<BootstrapFinding> Run()
        {
            var findings = new List<BootstrapFinding>();

            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
            {
                findings.Add(new BootstrapFinding(
                    BootstrapFinding.CodeProjectSettingsMissing,
                    "The MolcaProjectSettings asset could not be loaded or seeded. Bootstrap will fail at "
                    + "runtime."));
                return findings;
            }

            if (projectSettings.RuntimeManager == null)
                findings.Add(new BootstrapFinding(
                    BootstrapFinding.CodeRuntimeManagerUnset,
                    "MolcaProjectSettings names no RuntimeManager prefab, so nothing bootstraps at runtime.",
                    context: projectSettings));

            var globalSettings = projectSettings.GlobalSettings;
            if (globalSettings == null)
            {
                findings.Add(new BootstrapFinding(
                    BootstrapFinding.CodeGlobalSettingsUnset,
                    "MolcaProjectSettings names no GlobalSettings asset, so no setting module is loaded.",
                    context: projectSettings));
            }
            else
            {
                CheckModules(globalSettings, findings);
            }

            CheckExtensions(projectSettings, findings);
            CheckRequiredModules(projectSettings, globalSettings, findings);
            return findings;
        }

        /// <summary>
        /// Reports a required <see cref="SettingModule"/> that no entry in <c>GlobalSettings.modules</c>
        /// provides.
        /// </summary>
        /// <remarks>
        /// Scoped to the subsystems actually present and enabled on the assigned RuntimeManager prefab,
        /// matching how <c>RuntimeManager</c> collects them at bootstrap. Scanning every
        /// <c>RuntimeSubsystem</c> type in the project instead would report modules for subsystems the
        /// application never instantiates — a false positive that teaches a user to ignore the check.
        /// </remarks>
        private static void CheckRequiredModules(
            MolcaProjectSettings projectSettings,
            GlobalSettings globalSettings,
            List<BootstrapFinding> findings)
        {
            var runtimeManager = projectSettings.RuntimeManager;
            if (runtimeManager == null) return; // Already reported; the prefab is the scope for this check.

            var present = new HashSet<System.Type>();
            foreach (var module in globalSettings?.modules ?? System.Array.Empty<SettingModule>())
                if (module != null) present.Add(module.GetType());

            var reported = new HashSet<System.Type>();

            // includeInactive: true is required, not merely permissive. A prefab asset's GameObjects are
            // never activeInHierarchy — they are not in a loaded scene — so the default overload returns
            // nothing at all and this check would silently never fire. The component's own `enabled` flag is
            // the authoring signal that survives into the instantiated copy, so that is what is filtered on.
            var subsystems = runtimeManager.GetComponentsInChildren<RuntimeSubsystem>(true)
                .Where(subsystem => subsystem != null && subsystem.enabled);

            foreach (var subsystem in subsystems)
            foreach (var attribute in subsystem.GetType()
                         .GetCustomAttributes(typeof(RequiresSettingModuleAttribute), inherit: true)
                         .Cast<RequiresSettingModuleAttribute>())
            foreach (var required in attribute.ModuleTypes)
            {
                if (required == null || !typeof(SettingModule).IsAssignableFrom(required)) continue;

                // A subclass satisfies the requirement: the subsystem asked for a capability, and a
                // fork's specialization of that module still provides it.
                if (present.Any(p => required.IsAssignableFrom(p))) continue;
                if (!reported.Add(required)) continue;

                findings.Add(new BootstrapFinding(
                    BootstrapFinding.CodeModuleMissing,
                    $"'{subsystem.GetType().Name}' requires a '{required.Name}' setting module, but "
                    + "GlobalSettings registers none. The subsystem will fail during bootstrap.",
                    context: globalSettings != null ? (UnityEngine.Object)globalSettings : projectSettings,
                    subject: required));
            }
        }

        private static void CheckModules(GlobalSettings globalSettings, List<BootstrapFinding> findings)
        {
            var modules = globalSettings.modules;
            if (modules == null) return;

            var seenTypes = new Dictionary<System.Type, int>();
            for (int i = 0; i < modules.Length; i++)
            {
                var module = modules[i];
                if (module == null)
                {
                    findings.Add(new BootstrapFinding(
                        BootstrapFinding.CodeModuleEntryNull,
                        $"GlobalSettings.modules[{i}] is null. It loads nothing and hides how much is "
                        + "actually configured.",
                        i, globalSettings));
                    continue;
                }

                var moduleType = module.GetType();
                if (seenTypes.TryGetValue(moduleType, out int first))
                    findings.Add(new BootstrapFinding(
                        BootstrapFinding.CodeModuleDuplicate,
                        $"Duplicate SettingModule type '{moduleType.Name}' at index {i} (first seen at "
                        + $"{first}). Each subtype must appear at most once; the runtime keeps one and which "
                        + "one is not defined.",
                        i, globalSettings));
                else
                    seenTypes[moduleType] = i;
            }
        }

        private static void CheckExtensions(
            MolcaProjectSettings projectSettings, List<BootstrapFinding> findings)
        {
            var extensions = projectSettings.BootstrapExtensions;
            if (extensions == null) return;

            for (int i = 0; i < extensions.Count; i++)
                if (extensions[i] == null)
                    findings.Add(new BootstrapFinding(
                        BootstrapFinding.CodeExtensionEntryNull,
                        $"MolcaProjectSettings.BootstrapExtensions[{i}] is null and does nothing.",
                        i, projectSettings));
        }
    }
}
