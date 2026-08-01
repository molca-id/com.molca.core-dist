using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Molca.Localization;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace Molca.Editor
{
    /// <summary>
    /// Previewed, stale-plan-safe localization authoring operations shared by Hub and MCP.
    /// </summary>
    public static class LocalizationAuthoringService
    {
        private const string DefaultLocaleDirectory = "Assets/_Molca/Localization/Locales";
        private const int MaximumRememberedPlans = 32;
        private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
        private static readonly Dictionary<string, LocalizationLocaleAuthoringPlan> Plans = new();
        private static readonly Queue<string> PlanOrder = new();
        private static readonly Dictionary<string, LocalizationLocaleArchivePlan> ArchivePlans = new();
        private static readonly Queue<string> ArchivePlanOrder = new();

        /// <summary>Builds and remembers a read-only add-or-repair locale preview.</summary>
        /// <param name="code">BCP-47 locale code.</param>
        /// <param name="displayName">Optional Molca display name.</param>
        /// <param name="modulePath">Optional module path when exactly one module exists.</param>
        /// <returns>An immutable plan with explicit changes and precondition errors.</returns>
        public static LocalizationLocaleAuthoringPlan PreviewAddLocale(
            string code,
            string displayName = null,
            string modulePath = null)
        {
            var snapshot = LocalizationAuditEngine.Audit(
                LocalizationAuditRequest.CreateDoctorRequest());
            var canonicalCode = CanonicalizeCultureCode(code, out var cultureError);
            var selectedModule = ResolveModule(modulePath, out var selectedModulePath, out var moduleError);
            var resolvedName = string.IsNullOrWhiteSpace(displayName)
                ? canonicalCode
                : displayName.Trim();
            var plan = new LocalizationLocaleAuthoringPlan(
                canonicalCode,
                resolvedName,
                selectedModulePath,
                snapshot.CatalogFingerprint);

            if (cultureError != null)
                plan.AddError(cultureError);
            if (moduleError != null)
                plan.AddError(moduleError);
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                plan.AddError("Addressables settings are missing. Create them before adding a locale.");
            if (LocalizationEditorSettings.ActiveLocalizationSettings == null)
                plan.AddError(
                    "Unity Localization settings are not active. Create or assign LocalizationSettings first.");
            if (plan.Errors.Count > 0)
            {
                Remember(plan);
                return plan;
            }

            plan.Module = selectedModule;
            var hasExistingEntry = selectedModule.Languages != null &&
                                   selectedModule.Languages.Any(entry =>
                                       string.Equals(
                                           entry.Code,
                                           canonicalCode,
                                           StringComparison.OrdinalIgnoreCase));
            var existingEntry = hasExistingEntry
                ? selectedModule.Languages.First(entry =>
                    string.Equals(
                        entry.Code,
                        canonicalCode,
                        StringComparison.OrdinalIgnoreCase))
                : default;
            plan.AddModuleEntry = !hasExistingEntry;
            if (plan.AddModuleEntry)
                plan.AddChange($"Add '{canonicalCode}' to {selectedModulePath}.");
            else if (!string.IsNullOrWhiteSpace(displayName) &&
                     !string.Equals(existingEntry.Name, resolvedName, StringComparison.Ordinal))
                plan.AddWarning(
                    $"The existing Molca row is named '{existingEntry.Name}'. This repair plan will preserve it.");

            var localeMatches = AssetDatabase.FindAssets("t:Locale")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, locale: AssetDatabase.LoadAssetAtPath<Locale>(path)))
                .Where(item => item.locale != null &&
                               string.Equals(
                                   item.locale.Identifier.Code,
                                   canonicalCode,
                                   StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToList();
            if (localeMatches.Count > 1)
            {
                plan.AddError(
                    $"Multiple Locale assets declare '{canonicalCode}': " +
                    $"{string.Join(", ", localeMatches.Select(item => item.path))}. Resolve the ambiguity first.");
                Remember(plan);
                return plan;
            }

            plan.CreateLocaleAsset = localeMatches.Count == 0;
            plan.LocaleAsset = localeMatches.FirstOrDefault().locale;
            plan.LocaleAssetPath = plan.CreateLocaleAsset
                ? FindAvailableAssetPath(
                    $"{DefaultLocaleDirectory}/{canonicalCode}.asset")
                : localeMatches[0].path;
            if (plan.CreateLocaleAsset)
                plan.AddChange($"Create Unity Locale asset at {plan.LocaleAssetPath}.");

            var registered = LocalizationEditorSettings.GetLocales().Any(locale =>
                string.Equals(locale.Identifier.Code, canonicalCode, StringComparison.OrdinalIgnoreCase));
            plan.RegisterLocale = !registered;
            if (plan.RegisterLocale)
                plan.AddChange($"Register '{canonicalCode}' in Unity Localization and Addressables.");

            var identifier = new LocaleIdentifier(canonicalCode);
            var collections = LocalizationEditorSettings.GetStringTableCollections()
                .Cast<LocalizationTableCollection>()
                .Concat(LocalizationEditorSettings.GetAssetTableCollections())
                .OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal);
            foreach (var collection in collections)
            {
                if (collection == null || collection.ContainsTable(identifier))
                    continue;
                plan.MissingTableCollections.Add(collection);
                plan.AddChange(
                    $"Create the '{canonicalCode}' table in '{collection.TableCollectionName}'.");
            }

            if (plan.Changes.Count == 0)
                plan.AddError($"Locale '{canonicalCode}' is already fully configured.");

            Remember(plan);
            return plan;
        }

        /// <summary>Gets an unexpired preview by its opaque identifier.</summary>
        /// <param name="planId">Identifier returned by <see cref="PreviewAddLocale"/>.</param>
        /// <param name="plan">Resolved plan when found.</param>
        /// <returns>True when the plan exists and has not expired.</returns>
        public static bool TryGetPlan(
            string planId,
            out LocalizationLocaleAuthoringPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(planId) || !Plans.TryGetValue(planId, out var found))
                return false;
            if (DateTime.UtcNow - found.CreatedAtUtc > PlanLifetime)
            {
                Plans.Remove(planId);
                return false;
            }

            plan = found;
            return true;
        }

        /// <summary>Builds and remembers a non-destructive archive-locale preview.</summary>
        /// <param name="code">Configured BCP-47 locale code to archive.</param>
        /// <param name="modulePath">Optional module path when exactly one module exists.</param>
        /// <returns>A fingerprint-bound plan that preserves Locale, table, and inline assets.</returns>
        public static LocalizationLocaleArchivePlan PreviewArchiveLocale(
            string code,
            string modulePath = null)
        {
            var snapshot = LocalizationAuditEngine.Audit(
                LocalizationAuditRequest.CreateDoctorRequest());
            var canonicalCode = CanonicalizeCultureCode(code, out var cultureError);
            var module = ResolveModule(modulePath, out var selectedModulePath, out var moduleError);
            var plan = new LocalizationLocaleArchivePlan(
                canonicalCode,
                selectedModulePath,
                snapshot.CatalogFingerprint);

            if (cultureError != null)
                plan.AddError(cultureError);
            if (moduleError != null)
                plan.AddError(moduleError);
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                plan.AddError("Addressables settings are missing. Restore them before archiving a locale.");
            if (LocalizationEditorSettings.ActiveLocalizationSettings == null)
                plan.AddError(
                    "Unity Localization settings are not active. Restore them before archiving a locale.");
            if (plan.Errors.Count > 0)
            {
                Remember(plan);
                return plan;
            }

            plan.Module = module;
            if (!module.HasLanguage(canonicalCode))
            {
                plan.AddError(
                    $"LocalizationModule '{selectedModulePath}' does not configure '{canonicalCode}'.");
                Remember(plan);
                return plan;
            }
            var remainingCodes = module.LanguageCode
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate) &&
                                    !string.Equals(
                                        candidate,
                                        canonicalCode,
                                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (remainingCodes.Length == 0)
            {
                plan.AddError(
                    $"'{canonicalCode}' is the module's last configured locale. Add a replacement before archiving it.");
                Remember(plan);
                return plan;
            }
            plan.AddChange($"Remove '{canonicalCode}' from {selectedModulePath}.");
            if (string.Equals(
                    module.LanguageCode.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)),
                    canonicalCode,
                    StringComparison.OrdinalIgnoreCase))
                plan.AddWarning(
                    $"The default fallback will change from '{canonicalCode}' to '{remainingCodes[0]}'.");

            var localeMatches = AssetDatabase.FindAssets("t:Locale")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, locale: AssetDatabase.LoadAssetAtPath<Locale>(path)))
                .Where(item => item.locale != null &&
                               string.Equals(
                                   item.locale.Identifier.Code,
                                   canonicalCode,
                                   StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToList();
            if (localeMatches.Count > 1)
            {
                plan.AddError(
                    $"Multiple Locale assets declare '{canonicalCode}': " +
                    $"{string.Join(", ", localeMatches.Select(item => item.path))}. Resolve the ambiguity first.");
                Remember(plan);
                return plan;
            }

            plan.LocaleAsset = localeMatches.FirstOrDefault().locale;
            plan.UnregisterLocale = plan.LocaleAsset != null &&
                                    LocalizationEditorSettings.GetLocales().Any(locale =>
                                        locale == plan.LocaleAsset ||
                                        string.Equals(
                                            locale.Identifier.Code,
                                            canonicalCode,
                                            StringComparison.OrdinalIgnoreCase));
            if (plan.UnregisterLocale)
                plan.AddChange(
                    $"Unregister '{canonicalCode}' from Unity Localization and its Locale Addressable entry.");
            if (plan.LocaleAsset != null)
                plan.AddWarning(
                    $"Preserve Locale asset '{localeMatches[0].path}' for later restore or explicit deletion.");

            var identifier = new LocaleIdentifier(canonicalCode);
            var collections = LocalizationEditorSettings.GetStringTableCollections()
                .Cast<LocalizationTableCollection>()
                .Concat(LocalizationEditorSettings.GetAssetTableCollections())
                .OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal);
            foreach (var collection in collections)
            {
                var table = collection?.GetTable(identifier);
                if (collection == null || table == null)
                    continue;
                plan.Tables.Add((collection, table));
                plan.AddChange(
                    $"Detach the '{canonicalCode}' table from '{collection.TableCollectionName}' and Addressables.");
                plan.AddWarning(
                    $"Preserve table asset '{AssetDatabase.GetAssetPath(table)}' for restore or explicit deletion.");
            }

            plan.AddWarning(
                $"Inline '{canonicalCode}' rows are preserved as orphaned values; archive never deletes authored text.");
            Remember(plan);
            return plan;
        }

        /// <summary>Gets an unexpired archive preview by its opaque identifier.</summary>
        public static bool TryGetArchivePlan(
            string planId,
            out LocalizationLocaleArchivePlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(planId) ||
                !ArchivePlans.TryGetValue(planId, out var found))
                return false;
            if (DateTime.UtcNow - found.CreatedAtUtc > PlanLifetime)
            {
                ArchivePlans.Remove(planId);
                return false;
            }

            plan = found;
            return true;
        }

        /// <summary>Executes an add-or-repair preview as one verified transaction.</summary>
        /// <param name="plan">Executable preview from this service.</param>
        /// <returns>Success evidence or a rollback/refusal reason.</returns>
        public static LocalizationLocaleAuthoringResult ExecuteAddLocale(
            LocalizationLocaleAuthoringPlan plan)
        {
            var result = new LocalizationLocaleAuthoringResult(plan);
            if (plan == null)
            {
                result.Error = "The locale authoring plan is missing.";
                return result;
            }
            if (!plan.IsExecutable)
            {
                result.Error = plan.Errors.Count > 0
                    ? string.Join(" ", plan.Errors)
                    : "The plan has no executable changes.";
                return result;
            }

            var currentSnapshot = LocalizationAuditEngine.Audit(
                LocalizationAuditRequest.CreateDoctorRequest());
            if (!string.Equals(
                    currentSnapshot.CatalogFingerprint,
                    plan.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                result.WasStale = true;
                result.Error =
                    "The localization catalog changed after this preview. Create a new plan before executing.";
                return result;
            }

            var module = AssetDatabase.LoadAssetAtPath<LocalizationModule>(plan.ModulePath);
            if (module == null || module != plan.Module)
            {
                result.WasStale = true;
                result.Error = "The selected LocalizationModule changed or no longer exists.";
                return result;
            }

            var originalLanguages = module.Languages?.ToArray() ?? Array.Empty<LocalizationModule.LanguageEntry>();
            var createdTables = new List<(LocalizationTableCollection collection, LocalizationTable table, string path)>();
            var createdFolders = new List<string>();
            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (addressables == null)
            {
                result.WasStale = true;
                result.Error = "Addressables settings changed or no longer exist. Create a new preview.";
                return result;
            }
            var originalGroups = addressables.groups.Where(group => group != null).ToArray();
            Locale locale = plan.LocaleAsset;
            var createdLocale = false;
            var registeredLocale = false;
            var undoGroup = BeginUndoGroup($"Add localization locale {plan.Code}");
            var stage = "initializing the transaction";

            try
            {
                Undo.RegisterCompleteObjectUndo(
                    new UnityEngine.Object[] { addressables }
                        .Concat(originalGroups)
                        .ToArray(),
                    $"Configure Addressables for {plan.Code}");
                if (plan.CreateLocaleAsset)
                {
                    stage = $"creating the Locale asset at '{plan.LocaleAssetPath}'";
                    EnsureAssetDirectory(Path.GetDirectoryName(plan.LocaleAssetPath), createdFolders);
                    locale = Locale.CreateLocale(plan.Code);
                    AssetDatabase.CreateAsset(locale, plan.LocaleAssetPath);
                    Undo.RegisterCreatedObjectUndo(locale, $"Create locale {plan.Code}");
                    createdLocale = true;
                    result.AddCreatedAssetPath(plan.LocaleAssetPath);
                }
                if (locale == null)
                    throw new InvalidOperationException($"Locale asset '{plan.LocaleAssetPath}' could not be loaded.");

                if (plan.RegisterLocale)
                {
                    stage = $"registering locale '{plan.Code}'";
                    LocalizationEditorSettings.AddLocale(locale, createUndo: true);
                    registeredLocale = true;
                }

                foreach (var collection in plan.MissingTableCollections)
                {
                    stage = $"creating the '{plan.Code}' table in '{collection?.TableCollectionName}'";
                    if (collection == null)
                        throw new InvalidOperationException("A planned table collection no longer exists.");
                    if (collection.ContainsTable(locale.Identifier))
                        throw new InvalidOperationException(
                            $"Collection '{collection.TableCollectionName}' changed after preview.");

                    Undo.RegisterCompleteObjectUndo(
                        collection,
                        $"Add {plan.Code} table to {collection.TableCollectionName}");
                    var table = collection.AddNewTable(locale.Identifier);
                    var path = AssetDatabase.GetAssetPath(table);
                    Undo.RegisterCreatedObjectUndo(
                        table,
                        $"Create {collection.TableCollectionName} {plan.Code} table");
                    createdTables.Add((collection, table, path));
                    result.AddCreatedAssetPath(path);
                }

                foreach (var group in addressables.groups
                             .Where(group => group != null && !originalGroups.Contains(group))
                             .ToArray())
                {
                    Undo.RegisterCreatedObjectUndo(group, $"Create Addressables group for {plan.Code}");
                    result.AddCreatedAssetPath(AssetDatabase.GetAssetPath(group));
                    foreach (var schema in group.Schemas
                                 .Where(schema => schema != null)
                                 .ToArray())
                    {
                        Undo.RegisterCreatedObjectUndo(
                            schema,
                            $"Create Addressables schema for {plan.Code}");
                        result.AddCreatedAssetPath(AssetDatabase.GetAssetPath(schema));
                    }
                }

                if (plan.AddModuleEntry)
                {
                    stage = $"updating '{plan.ModulePath}'";
                    Undo.RecordObject(module, $"Add Molca locale {plan.Code}");
                    module.Languages = originalLanguages
                        .Concat(new[]
                        {
                            new LocalizationModule.LanguageEntry
                            {
                                Code = plan.Code,
                                Name = plan.DisplayName,
                            }
                        })
                        .ToArray();
                    EditorUtility.SetDirty(module);
                }

                stage = "saving localization assets";
                AssetDatabase.SaveAssets();
                stage = "verifying transaction postconditions";
                VerifyPostconditions(plan, module, locale);
                stage = "capturing the post-transaction audit";
                result.PostAudit = LocalizationAuditEngine.Audit(
                    LocalizationAuditRequest.CreateDoctorRequest());
                foreach (var folder in createdFolders)
                    result.AddCreatedAssetPath(folder);
                result.Succeeded = true;
                Undo.CollapseUndoOperations(undoGroup);
                Plans.Remove(plan.PlanId);
                return result;
            }
            catch (Exception exception)
            {
                result.Error = RollBack(
                    new InvalidOperationException($"{stage}: {exception.Message}", exception),
                    undoGroup,
                    module,
                    originalLanguages,
                    createdTables,
                    locale,
                    plan.LocaleAssetPath,
                    createdLocale,
                    registeredLocale,
                    addressables,
                    originalGroups,
                    createdFolders);
                return result;
            }
        }

        /// <summary>Executes a non-destructive archive preview as one verified Undo transaction.</summary>
        public static LocalizationLocaleArchiveResult ExecuteArchiveLocale(
            LocalizationLocaleArchivePlan plan)
        {
            var result = new LocalizationLocaleArchiveResult(plan);
            if (plan == null)
            {
                result.Error = "The locale archive plan is missing.";
                return result;
            }
            if (!plan.IsExecutable)
            {
                result.Error = plan.Errors.Count > 0
                    ? string.Join(" ", plan.Errors)
                    : "The archive plan has no executable changes.";
                return result;
            }

            var currentSnapshot = LocalizationAuditEngine.Audit(
                LocalizationAuditRequest.CreateDoctorRequest());
            if (!string.Equals(
                    currentSnapshot.CatalogFingerprint,
                    plan.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                result.WasStale = true;
                result.Error =
                    "The localization catalog changed after this preview. Create a new archive plan.";
                return result;
            }

            var module = AssetDatabase.LoadAssetAtPath<LocalizationModule>(plan.ModulePath);
            if (module == null || module != plan.Module)
            {
                result.WasStale = true;
                result.Error = "The selected LocalizationModule changed or no longer exists.";
                return result;
            }

            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (addressables == null)
            {
                result.WasStale = true;
                result.Error = "Addressables settings changed or no longer exist. Create a new preview.";
                return result;
            }

            var originalLanguages = module.Languages?.ToArray() ??
                                    Array.Empty<LocalizationModule.LanguageEntry>();
            var originalGroups = addressables.groups.Where(group => group != null).ToArray();
            var undoGroup = BeginUndoGroup($"Archive localization locale {plan.Code}");
            var stage = "initializing the archive transaction";
            try
            {
                Undo.RegisterCompleteObjectUndo(
                    new UnityEngine.Object[] { addressables }
                        .Concat(originalGroups)
                        .ToArray(),
                    $"Archive Addressables locale {plan.Code}");
                foreach (var (collection, table) in plan.Tables)
                {
                    stage = $"detaching the '{plan.Code}' table from '{collection?.TableCollectionName}'";
                    if (collection == null || table == null ||
                        !collection.ContainsTable(table))
                        throw new InvalidOperationException(
                            "A planned localization table changed after preview.");
                    collection.RemoveTable(table, createUndo: true);
                }

                if (plan.UnregisterLocale)
                {
                    stage = $"unregistering locale '{plan.Code}'";
                    if (plan.LocaleAsset == null)
                        throw new InvalidOperationException("The planned Locale asset no longer exists.");
                    LocalizationEditorSettings.RemoveLocale(plan.LocaleAsset, createUndo: true);
                }

                stage = $"updating '{plan.ModulePath}'";
                Undo.RecordObject(module, $"Archive Molca locale {plan.Code}");
                module.Languages = originalLanguages
                    .Where(entry => !string.Equals(
                        entry.Code,
                        plan.Code,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                EditorUtility.SetDirty(module);

                stage = "saving archived localization state";
                AssetDatabase.SaveAssets();
                stage = "verifying archive postconditions";
                VerifyArchivePostconditions(plan, module);
                stage = "capturing the post-archive audit";
                result.PostAudit = LocalizationAuditEngine.Audit(
                    LocalizationAuditRequest.CreateDoctorRequest());
                result.Succeeded = true;
                Undo.CollapseUndoOperations(undoGroup);
                ArchivePlans.Remove(plan.PlanId);
                return result;
            }
            catch (Exception exception)
            {
                result.Error = RollBackArchive(
                    new InvalidOperationException($"{stage}: {exception.Message}", exception),
                    undoGroup,
                    plan,
                    module,
                    originalLanguages);
                return result;
            }
        }

        private static void VerifyArchivePostconditions(
            LocalizationLocaleArchivePlan plan,
            LocalizationModule module)
        {
            if (module.HasLanguage(plan.Code))
                throw new InvalidOperationException("Molca module still configures the archived locale.");
            if (plan.UnregisterLocale &&
                LocalizationEditorSettings.GetLocales().Any(locale =>
                    string.Equals(
                        locale.Identifier.Code,
                        plan.Code,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Unity still registers the archived locale.");
            foreach (var (collection, table) in plan.Tables)
                if (collection != null && table != null && collection.ContainsTable(table))
                    throw new InvalidOperationException(
                        $"Collection '{collection.TableCollectionName}' still contains the archived table.");
        }

        private static string RollBackArchive(
            Exception cause,
            int undoGroup,
            LocalizationLocaleArchivePlan plan,
            LocalizationModule module,
            LocalizationModule.LanguageEntry[] originalLanguages)
        {
            var rollbackErrors = new List<string>();
            TryRollbackStep(
                () => Undo.RevertAllDownToGroup(undoGroup),
                "reverting the archive Undo group",
                rollbackErrors);
            foreach (var (collection, table) in plan.Tables)
                TryRollbackStep(
                    () =>
                    {
                        if (collection != null && table != null &&
                            !collection.ContainsTable(table))
                            collection.AddTable(table);
                    },
                    $"reattaching preserved table '{AssetDatabase.GetAssetPath(table)}'",
                    rollbackErrors);
            TryRollbackStep(
                () =>
                {
                    if (plan.UnregisterLocale && plan.LocaleAsset != null &&
                        !LocalizationEditorSettings.GetLocales().Contains(plan.LocaleAsset))
                        LocalizationEditorSettings.AddLocale(plan.LocaleAsset);
                },
                $"restoring locale registration '{plan.Code}'",
                rollbackErrors);
            TryRollbackStep(
                () =>
                {
                    if (module == null)
                        return;
                    module.Languages = originalLanguages;
                    EditorUtility.SetDirty(module);
                },
                "restoring the Molca localization module",
                rollbackErrors);
            TryRollbackStep(AssetDatabase.SaveAssets, "saving archive rollback state", rollbackErrors);

            return rollbackErrors.Count == 0
                ? $"Locale archive failed and was rolled back: {cause.Message}"
                : $"Locale archive failed: {cause.Message}. Rollback also reported: " +
                  string.Join(" ", rollbackErrors);
        }

        private static void VerifyPostconditions(
            LocalizationLocaleAuthoringPlan plan,
            LocalizationModule module,
            Locale locale)
        {
            if (!module.HasLanguage(plan.Code))
                throw new InvalidOperationException("Molca module verification failed.");
            if (!LocalizationEditorSettings.GetLocales().Any(candidate =>
                    candidate == locale ||
                    string.Equals(
                        candidate.Identifier.Code,
                        plan.Code,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Unity Locale registration verification failed.");
            foreach (var collection in plan.MissingTableCollections)
                if (collection == null || !collection.ContainsTable(locale.Identifier))
                    throw new InvalidOperationException(
                        $"Table verification failed for '{collection?.TableCollectionName ?? "missing collection"}'.");
        }

        private static string RollBack(
            Exception cause,
            int undoGroup,
            LocalizationModule module,
            LocalizationModule.LanguageEntry[] originalLanguages,
            IReadOnlyList<(LocalizationTableCollection collection, LocalizationTable table, string path)> createdTables,
            Locale locale,
            string localeAssetPath,
            bool createdLocale,
            bool registeredLocale,
            AddressableAssetSettings addressables,
            IReadOnlyCollection<AddressableAssetGroup> originalGroups,
            IReadOnlyList<string> createdFolders)
        {
            var rollbackErrors = new List<string>();
            string[] createdAddressablePaths;
            try
            {
                createdAddressablePaths = addressables.groups
                    .Where(group => group != null && !originalGroups.Contains(group))
                    .SelectMany(group => new[] { AssetDatabase.GetAssetPath(group) }
                        .Concat(group.Schemas
                            .Where(schema => schema != null)
                            .Select(AssetDatabase.GetAssetPath)))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception)
            {
                createdAddressablePaths = Array.Empty<string>();
                rollbackErrors.Add($"capturing generated Addressables paths: {exception.Message}");
            }

            // Revert registered Unity Undo state first, then explicitly remove anything created by
            // package APIs that did not participate in the Undo group (notably Addressables entries).
            TryRollbackStep(
                () => Undo.RevertAllDownToGroup(undoGroup),
                "reverting the Unity Undo group",
                rollbackErrors);
            for (var index = createdTables.Count - 1; index >= 0; index--)
            {
                var (collection, table, path) = createdTables[index];
                TryRollbackStep(
                    () =>
                    {
                        if (collection != null && table != null && collection.ContainsTable(table))
                            collection.RemoveTable(table);
                        if (!string.IsNullOrEmpty(path))
                            AssetDatabase.DeleteAsset(path);
                    },
                    $"removing generated table '{path}'",
                    rollbackErrors);
            }

            TryRollbackStep(
                () =>
                {
                    if (registeredLocale && locale != null)
                        LocalizationEditorSettings.RemoveLocale(locale);
                },
                $"unregistering locale '{localeAssetPath}'",
                rollbackErrors);
            TryRollbackStep(
                () =>
                {
                    if (createdLocale && !string.IsNullOrWhiteSpace(localeAssetPath))
                        AssetDatabase.DeleteAsset(localeAssetPath);
                },
                $"deleting locale asset '{localeAssetPath}'",
                rollbackErrors);
            TryRollbackStep(
                () =>
                {
                    if (module == null)
                        return;
                    module.Languages = originalLanguages;
                    EditorUtility.SetDirty(module);
                },
                "restoring the Molca localization module",
                rollbackErrors);

            foreach (var group in addressables.groups
                         .Where(group => group != null && !originalGroups.Contains(group))
                         .ToArray())
                TryRollbackStep(
                    () =>
                    {
                        if (group.entries.Count == 0)
                            addressables.RemoveGroup(group);
                    },
                    $"removing Addressables group '{group.Name}'",
                    rollbackErrors);
            foreach (var path in createdAddressablePaths
                         .OrderByDescending(path => path.Count(character => character == '/')))
                TryRollbackStep(
                    () => AssetDatabase.DeleteAsset(path),
                    $"deleting generated Addressables asset '{path}'",
                    rollbackErrors);

            for (var index = createdFolders.Count - 1; index >= 0; index--)
            {
                var folder = createdFolders[index];
                TryRollbackStep(
                    () =>
                    {
                        if (AssetDatabase.IsValidFolder(folder) &&
                            AssetDatabase.FindAssets(string.Empty, new[] { folder }).Length == 0)
                            AssetDatabase.DeleteAsset(folder);
                    },
                    $"removing generated folder '{folder}'",
                    rollbackErrors);
            }

            TryRollbackStep(AssetDatabase.SaveAssets, "saving rollback state", rollbackErrors);
            return rollbackErrors.Count == 0
                ? $"Locale transaction failed and was rolled back: {cause.Message}"
                : $"Locale transaction failed: {cause.Message}. Rollback also reported: " +
                  string.Join(" ", rollbackErrors);
        }

        private static void TryRollbackStep(
            Action action,
            string description,
            ICollection<string> errors)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                errors.Add($"{description}: {exception.Message}");
            }
        }

        private static int BeginUndoGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return group;
        }

        private static void EnsureAssetDirectory(string path, ICollection<string> createdFolders)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "Assets")
                return;
            var normalized = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(normalized))
                return;
            var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            EnsureAssetDirectory(parent, createdFolders);
            var folderName = Path.GetFileName(normalized);
            AssetDatabase.CreateFolder(parent, folderName);
            createdFolders.Add(normalized);
        }

        private static string FindAvailableAssetPath(string desiredPath)
        {
            if (!File.Exists(desiredPath) &&
                string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(desiredPath)))
                return desiredPath;

            var directory = Path.GetDirectoryName(desiredPath)?.Replace('\\', '/');
            var fileName = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);
            for (var suffix = 1; suffix < 10000; suffix++)
            {
                var candidate = $"{directory}/{fileName} {suffix}{extension}";
                if (!File.Exists(candidate) &&
                    string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(candidate)))
                    return candidate;
            }

            throw new InvalidOperationException(
                $"Could not allocate a unique asset path for '{desiredPath}'.");
        }

        private static string CanonicalizeCultureCode(string code, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(code))
            {
                error = "A non-blank BCP-47 locale code is required.";
                return string.Empty;
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(code.Trim());
                if (string.IsNullOrWhiteSpace(culture.Name))
                {
                    error = $"'{code}' is not a specific BCP-47 locale code.";
                    return code.Trim();
                }
                return culture.Name;
            }
            catch (CultureNotFoundException)
            {
                error = $"'{code}' is not a recognized BCP-47 locale code.";
                return code.Trim();
            }
        }

        private static LocalizationModule ResolveModule(
            string modulePath,
            out string selectedPath,
            out string error)
        {
            error = null;
            selectedPath = modulePath;
            var modules = AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, module: AssetDatabase.LoadAssetAtPath<LocalizationModule>(path)))
                .Where(item => item.module != null)
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToList();
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                var match = modules.FirstOrDefault(item =>
                    string.Equals(item.path, modulePath, StringComparison.Ordinal));
                if (match.module != null)
                {
                    selectedPath = match.path;
                    return match.module;
                }

                error = $"No LocalizationModule exists at '{modulePath}'.";
                return null;
            }

            if (modules.Count == 1)
            {
                selectedPath = modules[0].path;
                return modules[0].module;
            }

            error = modules.Count == 0
                ? "No LocalizationModule asset exists."
                : "Multiple LocalizationModule assets exist; select an explicit module path.";
            return null;
        }

        private static void Remember(LocalizationLocaleAuthoringPlan plan)
        {
            Plans[plan.PlanId] = plan;
            PlanOrder.Enqueue(plan.PlanId);
            while (PlanOrder.Count > MaximumRememberedPlans)
                Plans.Remove(PlanOrder.Dequeue());
        }

        private static void Remember(LocalizationLocaleArchivePlan plan)
        {
            ArchivePlans[plan.PlanId] = plan;
            ArchivePlanOrder.Enqueue(plan.PlanId);
            while (ArchivePlanOrder.Count > MaximumRememberedPlans)
                ArchivePlans.Remove(ArchivePlanOrder.Dequeue());
        }
    }
}
