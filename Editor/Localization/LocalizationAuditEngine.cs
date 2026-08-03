using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Molca.Localization;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Localization;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Molca.Editor
{
    /// <summary>
    /// Produces one deterministic, read-only localization snapshot for every Editor consumer.
    /// </summary>
    public static class LocalizationAuditEngine
    {
        // Mirrors AddressablesPreferences.kBuildAddressablesWithPlayerBuildKey, which is internal.
        private const string AddressablesBuildWithPlayerPrefKey =
            "Addressables.BuildAddressablesWithPlayerBuild";

        private static bool _auditHasConfiguredRtl;

        /// <summary>Runs a complete audit synchronously on the Unity main thread.</summary>
        /// <param name="request">Scope, policy, progress, and cancellation settings.</param>
        /// <returns>A snapshot containing findings, coverage, and a source fingerprint.</returns>
        public static LocalizationAuditSnapshot Audit(LocalizationAuditRequest request)
        {
            request ??= LocalizationAuditRequest.CreateDoctorRequest();
            var snapshot = new LocalizationAuditSnapshot(request);
            _auditHasConfiguredRtl = false;

            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var configuredCodes = AuditConfiguration(snapshot);
                _auditHasConfiguredRtl = HasConfiguredRtlLocale();
                snapshot.ConfiguredLocales = configuredCodes.ToArray();
                AuditCatalogValues(snapshot, configuredCodes);

                if (request.Scope.HasFlag(LocalizationAuditScope.Addressables))
                    AuditAddressables(snapshot, configuredCodes);

                AuditSerializedAssets(snapshot, configuredCodes);
                AuditScenes(snapshot, configuredCodes);
                FinalizeSnapshot(snapshot);
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-audit-failed",
                    LocalizationAuditSeverity.Error,
                    $"Localization audit failed: {exception.Message}"));
                snapshot.Status = LocalizationAuditStatus.Failed;
                return snapshot;
            }
        }

        private static void AuditCatalogValues(
            LocalizationAuditSnapshot snapshot,
            IReadOnlyCollection<string> configuredCodes)
        {
            var defaultCode = LocalizationCatalogAuthoringService.GetDefaultLocaleCode();
            foreach (var collection in LocalizationEditorSettings.GetStringTableCollections()
                         .Where(collection => collection != null)
                         .OrderBy(
                             collection => collection.SharedData.TableCollectionNameGuid,
                             Comparer<Guid>.Default))
            {
                snapshot.Request.CancellationToken.ThrowIfCancellationRequested();
                snapshot.Request.ReportStatus?.Invoke(
                    $"Auditing localization catalog '{collection.TableCollectionName}'");
                var sharedPath = AssetDatabase.GetAssetPath(collection.SharedData);
                snapshot.AddSourcePath(sharedPath);
                var sourceTable = string.IsNullOrEmpty(defaultCode)
                    ? null
                    : collection.GetTable(defaultCode) as StringTable;

                foreach (var sharedEntry in collection.SharedData.Entries
                             .Where(entry => entry != null)
                             .OrderBy(entry => entry.Id))
                {
                    snapshot.Request.CancellationToken.ThrowIfCancellationRequested();
                    var sourceEntry = sourceTable?.GetEntry(sharedEntry.Id);
                    var sourceValue = sourceEntry?.Value;
                    var expectedPlaceholders = LocalizationPlaceholderUtility.Extract(sourceValue);
                    var expectedPlurals = LocalizationPluralUtility.Extract(sourceValue);
                    foreach (var code in configuredCodes.OrderBy(
                                 value => value,
                                 StringComparer.Ordinal))
                    {
                        var table = collection.GetTable(code) as StringTable;
                        if (table == null)
                            continue;
                        var tablePath = AssetDatabase.GetAssetPath(table);
                        snapshot.AddSourcePath(tablePath);
                        var targetEntry = table.GetEntry(sharedEntry.Id);
                        var value = targetEntry?.Value;
                        if (string.IsNullOrEmpty(value))
                        {
                            snapshot.AddFinding(new LocalizationAuditFinding(
                                "localization-catalog-value-missing",
                                snapshot.Request.RequireCompleteTranslations
                                    ? LocalizationAuditSeverity.Error
                                    : LocalizationAuditSeverity.Warning,
                                $"Catalog key '{sharedEntry.Key}' in " +
                                $"'{collection.TableCollectionName}' has no value for '{code}'.",
                                tablePath,
                                $"entry:{sharedEntry.Id}"));
                            continue;
                        }

                        if (string.IsNullOrEmpty(sourceValue) ||
                            string.Equals(code, defaultCode, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (sourceEntry.IsSmart != targetEntry.IsSmart)
                            snapshot.AddFinding(new LocalizationAuditFinding(
                                "localization-smart-mode-mismatch",
                                LocalizationAuditSeverity.Error,
                                $"Catalog key '{sharedEntry.Key}' in " +
                                $"'{collection.TableCollectionName}' has Smart String mode " +
                                $"{targetEntry.IsSmart} for '{code}', expected {sourceEntry.IsSmart}.",
                                tablePath,
                                $"entry:{sharedEntry.Id}"));
                        var actualPlaceholders = LocalizationPlaceholderUtility.Extract(value);
                        if (!expectedPlaceholders.SetEquals(actualPlaceholders))
                            snapshot.AddFinding(new LocalizationAuditFinding(
                                "localization-placeholder-mismatch",
                                LocalizationAuditSeverity.Error,
                                $"Catalog key '{sharedEntry.Key}' in " +
                                $"'{collection.TableCollectionName}' has placeholders " +
                                $"{{{string.Join(", ", actualPlaceholders)}}} for '{code}', expected " +
                                $"{{{string.Join(", ", expectedPlaceholders)}}} from '{defaultCode}'.",
                                tablePath,
                                $"entry:{sharedEntry.Id}"));
                        var actualPlurals = LocalizationPluralUtility.Extract(value);
                        if (!expectedPlurals.SetEquals(actualPlurals) ||
                            LocalizationPluralUtility.ContainsMalformedPlural(value))
                            snapshot.AddFinding(new LocalizationAuditFinding(
                                "localization-plural-mismatch",
                                LocalizationAuditSeverity.Error,
                                $"Catalog key '{sharedEntry.Key}' in " +
                                $"'{collection.TableCollectionName}' has plural signatures " +
                                $"[{string.Join(", ", actualPlurals)}] for '{code}', expected " +
                                $"[{string.Join(", ", expectedPlurals)}] from '{defaultCode}'.",
                                tablePath,
                                $"entry:{sharedEntry.Id}"));
                    }
                }
            }
        }

        private static HashSet<string> AuditConfiguration(LocalizationAuditSnapshot snapshot)
        {
            var modules = AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, module: AssetDatabase.LoadAssetAtPath<LocalizationModule>(path)))
                .Where(item => item.module != null)
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToList();

            foreach (var item in modules)
                snapshot.AddSourcePath(item.path);

            if (modules.Count == 0)
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-settings-missing",
                    LocalizationAuditSeverity.Error,
                    "No LocalizationModule exists. Create one and register it in GlobalSettings."));
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var configuredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, module) in ResolveAuditedModules(snapshot, modules))
            {
                AuditRemoteCatalog(snapshot, path, module.RemoteCatalog);
                foreach (var error in module.ValidateFallbackGraph())
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-fallback-invalid",
                        LocalizationAuditSeverity.Error,
                        error,
                        path,
                        "Languages"));

                var localCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < module.LanguageCode.Length; index++)
                {
                    var code = module.LanguageCode[index];
                    // These two causes used to share one id. They need different things from the author —
                    // one needs a code written, the other needs a row chosen and removed — so a repair
                    // keyed to the shared id could not tell which case it was looking at.
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-locale-code-blank",
                            LocalizationAuditSeverity.Error,
                            $"LocalizationModule language row {index} has a blank code.",
                            path, $"Languages.Array.data[{index}].Code"));
                        continue;
                    }

                    var canonicalCode = LocalizationModule.CanonicalizeLocaleCode(code);
                    if (!localCodes.Add(canonicalCode))
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-locale-code-duplicate",
                            LocalizationAuditSeverity.Error,
                            $"LocalizationModule contains duplicate language code '{code}' "
                            + $"(canonicalizes to '{canonicalCode}').",
                            path, $"Languages.Array.data[{index}].Code"));
                    configuredCodes.Add(canonicalCode);

                    var language = module.Languages[index];
                    var profile = language.PresentationProfile;
                    var severity = snapshot.Request.RequireCompleteTranslations
                        ? LocalizationAuditSeverity.Error
                        : LocalizationAuditSeverity.Warning;
                    if (profile == null)
                    {
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-font-profile-missing",
                            severity,
                            $"Locale '{code}' has no presentation profile. Assign a font, glyph, and writing-direction policy.",
                            path,
                            $"Languages.Array.data[{index}].PresentationProfile"));
                        continue;
                    }

                    snapshot.AddSourcePath(AssetDatabase.GetAssetPath(profile));
                    if (profile.WritingDirection == LocalizationWritingDirection.Unspecified)
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-writing-direction-missing",
                            severity,
                            $"Locale '{code}' has no explicit writing direction.",
                            AssetDatabase.GetAssetPath(profile),
                            "writingDirection"));
                    if (profile.PrimaryFont == null)
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-primary-font-missing",
                            severity,
                            $"Locale '{code}' has no primary TMP font in its presentation profile.",
                            AssetDatabase.GetAssetPath(profile),
                            "primaryFont"));
                    var missingGlyphs = profile.GetMissingRequiredCharacters();
                    if (missingGlyphs.Count > 0)
                        snapshot.AddFinding(new LocalizationAuditFinding(
                            "localization-glyph-coverage-missing",
                            severity,
                            $"Locale '{code}' is missing {missingGlyphs.Count} required glyph(s): " +
                            $"{string.Join(" ", missingGlyphs.Take(24))}.",
                            AssetDatabase.GetAssetPath(profile),
                            "requiredCharacters"));
                }
            }

            return configuredCodes;
        }

        /// <summary>
        /// Narrows the discovered modules to the one the runtime actually loads.
        /// </summary>
        /// <param name="snapshot">Snapshot receiving registration findings.</param>
        /// <param name="discovered">Every <see cref="LocalizationModule"/> asset in the project.</param>
        /// <returns>
        /// The single registered module when registration resolves it; otherwise every discovered
        /// module, so the rest of the audit still reports against something.
        /// </returns>
        /// <remarks>
        /// An unregistered module is inert — it is never initialized by
        /// <see cref="GlobalSettings.Initialize"/> and, being unreferenced, is not included in a
        /// player build. Its mere existence is therefore a hygiene warning, not a build blocker.
        /// Ambiguity is an error only when registration genuinely cannot single one out, because
        /// every downstream locale and Addressables finding depends on knowing which module is live.
        /// </remarks>
        private static List<(string path, LocalizationModule module)> ResolveAuditedModules(
            LocalizationAuditSnapshot snapshot,
            List<(string path, LocalizationModule module)> discovered)
        {
            var registered = FindRegisteredModules(snapshot, out var unresolvedReason);

            if (registered.Count > 1)
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-settings-ambiguous",
                    LocalizationAuditSeverity.Error,
                    $"GlobalSettings registers {registered.Count} LocalizationModule assets. The " +
                    "runtime initializes whichever comes first; register exactly one.",
                    AssetDatabase.GetAssetPath(registered[0])));
                return discovered;
            }

            if (registered.Count == 0)
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-settings-unregistered",
                    LocalizationAuditSeverity.Error,
                    $"Found {discovered.Count} LocalizationModule asset(s) but none is registered as " +
                    $"the active module: {unresolvedReason} Auditing all of them until registration " +
                    "resolves.",
                    discovered[0].path));
                return discovered;
            }

            var activePath = AssetDatabase.GetAssetPath(registered[0]);
            var active = discovered
                .Where(item => string.Equals(item.path, activePath, StringComparison.Ordinal))
                .ToList();

            // The registered module has no discoverable asset path (sub-asset or an unimported
            // folder). Fall back rather than audit nothing at all.
            if (active.Count == 0)
                return discovered;

            var strays = discovered
                .Where(item => !string.Equals(item.path, activePath, StringComparison.Ordinal))
                .ToList();
            if (strays.Count > 0)
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-settings-ambiguous",
                    LocalizationAuditSeverity.Warning,
                    $"{strays.Count} unregistered LocalizationModule asset(s) sit alongside the active " +
                    $"module '{activePath}' and are not audited: " +
                    $"{string.Join(", ", strays.Select(item => item.path))}. Delete them or fold them " +
                    "into the registered module.",
                    strays[0].path));

            return active;
        }

        /// <summary>
        /// Resolves the modules registered on the project's <see cref="GlobalSettings"/>.
        /// </summary>
        /// <param name="snapshot">Snapshot receiving the settings assets as fingerprint sources.</param>
        /// <param name="unresolvedReason">Sentence explaining an empty result.</param>
        /// <returns>Registered localization modules in serialized order.</returns>
        /// <remarks>
        /// Walks the same chain as <see cref="GlobalSettings.main"/> but loads the settings asset
        /// through <see cref="AssetDatabase"/> instead of <c>MolcaProjectSettings.Instance</c>: that
        /// property seeds a new asset when the project has none, and an audit must never write.
        /// </remarks>
        private static List<LocalizationModule> FindRegisteredModules(
            LocalizationAuditSnapshot snapshot,
            out string unresolvedReason)
        {
            unresolvedReason = string.Empty;
            var empty = new List<LocalizationModule>();

            // The package ships a read-only seed copy under Packages/; only a live asset in
            // consumer space can be the project's settings.
            var settingsPaths = AssetDatabase.FindAssets("t:MolcaProjectSettings")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (settingsPaths.Count != 1)
            {
                unresolvedReason = settingsPaths.Count == 0
                    ? "no MolcaProjectSettings asset exists under Assets/."
                    : $"{settingsPaths.Count} MolcaProjectSettings assets exist under Assets/.";
                return empty;
            }

            snapshot.AddSourcePath(settingsPaths[0]);
            var projectSettings =
                AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(settingsPaths[0]);
            var globalSettings = projectSettings != null ? projectSettings.GlobalSettings : null;
            if (globalSettings == null)
            {
                unresolvedReason = $"'{settingsPaths[0]}' has no GlobalSettings assigned.";
                return empty;
            }

            var globalSettingsPath = AssetDatabase.GetAssetPath(globalSettings);
            snapshot.AddSourcePath(globalSettingsPath);

            var registered = globalSettings.modules == null
                ? empty
                : globalSettings.modules.OfType<LocalizationModule>().ToList();
            if (registered.Count == 0)
                unresolvedReason = $"'{globalSettingsPath}' registers no LocalizationModule.";
            return registered;
        }

        private static void AuditRemoteCatalog(
            LocalizationAuditSnapshot snapshot,
            string modulePath,
            LocalizationRemoteCatalogSettings settings)
        {
            if (settings == null || !settings.Enabled)
                return;
            var settingsPath = AssetDatabase.GetAssetPath(settings);
            snapshot.AddSourcePath(settingsPath);
            var severity = snapshot.Request.RequireCompleteTranslations
                ? LocalizationAuditSeverity.Error
                : LocalizationAuditSeverity.Warning;
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-project-missing",
                    severity,
                    "Remote localization is enabled without a project id.",
                    settingsPath,
                    "projectId"));
            if (settings.Channel is not ("stable" or "beta" or "internal"))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-channel-invalid",
                    LocalizationAuditSeverity.Error,
                    $"Remote localization channel '{settings.Channel}' is unsupported.",
                    settingsPath,
                    "channel"));
            if (!string.IsNullOrWhiteSpace(settings.ManifestUrl) &&
                (!Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var uri) ||
                 uri.Scheme != Uri.UriSchemeHttps &&
                 !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-transport-insecure",
                    LocalizationAuditSeverity.Error,
                    "Remote manifest URL must use HTTPS or loopback HTTP.",
                    settingsPath,
                    "manifestUrl"));
            if (settings.TrustedKeys.Count == 0 ||
                settings.TrustedKeys.Any(key =>
                    key == null ||
                    string.IsNullOrWhiteSpace(key.KeyId) ||
                    string.IsNullOrWhiteSpace(key.ModulusBase64) ||
                    string.IsNullOrWhiteSpace(key.ExponentBase64)))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-trust-invalid",
                    LocalizationAuditSeverity.Error,
                    "Remote localization needs at least one complete public verification key.",
                    settingsPath,
                    "trustedKeys"));
            if (settings.TrustedKeys
                    .Where(key => key != null)
                    .GroupBy(key => key.KeyId ?? string.Empty, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-trust-duplicate",
                    LocalizationAuditSeverity.Error,
                    "Remote localization contains duplicate verification key ids.",
                    settingsPath,
                    "trustedKeys"));
            if (settings.AllowedEntries.Count == 0)
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-allowlist-empty",
                    severity,
                    "Remote localization has no shipped identity allowlist. Sync it from the Localization Hub.",
                    settingsPath,
                    "allowedEntries"));
            if (settings.AllowedEntries
                    .Where(entry => entry != null)
                    .GroupBy(entry => entry.Identity, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-allowlist-duplicate",
                    LocalizationAuditSeverity.Error,
                    "Remote localization contains duplicate allowlist identities.",
                    settingsPath,
                    "allowedEntries"));
            if (string.IsNullOrEmpty(settingsPath))
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-overlay-settings-transient",
                    LocalizationAuditSeverity.Error,
                    "Remote catalog settings must be a saved project asset.",
                    modulePath,
                    "remoteCatalog"));
        }

        private static void AuditAddressables(
            LocalizationAuditSnapshot snapshot,
            IReadOnlyCollection<string> configuredCodes)
        {
            var localeAssets = AssetDatabase.FindAssets("t:Locale")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, locale: AssetDatabase.LoadAssetAtPath<Locale>(path)))
                .Where(item => item.locale != null)
                .ToList();
            foreach (var item in localeAssets)
                snapshot.AddSourcePath(item.path);

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-addressable-entry-missing",
                    LocalizationAuditSeverity.Error,
                    "Addressables settings are missing; Unity Localization content cannot be built."));
                return;
            }

            snapshot.AddSourcePath(AssetDatabase.GetAssetPath(settings));
            var localizedTables = AssetDatabase.FindAssets("t:StringTable t:AssetTable")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, table: AssetDatabase.LoadAssetAtPath<LocalizationTable>(path)))
                .Where(item => item.table != null)
                .ToList();
            foreach (var item in localizedTables)
            {
                snapshot.AddSourcePath(item.path);
                var sharedDataPath = AssetDatabase.GetAssetPath(item.table.SharedData);
                snapshot.AddSourcePath(sharedDataPath);

                var tableGuid = AssetDatabase.AssetPathToGUID(item.path);
                if (settings.FindAssetEntry(tableGuid) == null)
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-addressable-entry-missing",
                        LocalizationAuditSeverity.Error,
                        $"Localization table '{item.table.TableCollectionName}' for " +
                        $"'{item.table.LocaleIdentifier.Code}' is not included in Addressables.",
                        item.path));
            }

            foreach (var collection in localizedTables.GroupBy(item =>
                         item.table.SharedData.TableCollectionNameGuid))
            {
                var presentCodes = new HashSet<string>(
                    collection.Select(item => item.table.LocaleIdentifier.Code),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var code in configuredCodes.Where(code => !presentCodes.Contains(code)))
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-build-policy-incomplete",
                        LocalizationAuditSeverity.Error,
                        $"Localization table collection '{collection.First().table.TableCollectionName}' " +
                        $"has no table for configured locale '{code}'.",
                        AssetDatabase.GetAssetPath(collection.First().table.SharedData)));
            }

            foreach (var code in configuredCodes)
            {
                var localeAsset = localeAssets.FirstOrDefault(item =>
                    string.Equals(item.locale.Identifier.Code, code, StringComparison.OrdinalIgnoreCase));
                if (localeAsset.locale == null)
                {
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-locale-state-drift",
                        LocalizationAuditSeverity.Error,
                        $"Language '{code}' is declared in LocalizationModule but has no Unity Locale asset."));
                    continue;
                }

                var guid = AssetDatabase.AssetPathToGUID(localeAsset.path);
                if (settings.FindAssetEntry(guid) == null)
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-addressable-entry-missing",
                        LocalizationAuditSeverity.Error,
                        $"Unity Locale '{code}' is not included in Addressables.",
                        localeAsset.path));
            }

            if (snapshot.Request.RequireAddressablesBuildWithPlayer &&
                !snapshot.Request.AddressablesContentAlreadyBuilt &&
                !WillBuildAddressablesWithPlayer(settings, out var staleReason))
            {
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-addressable-build-stale",
                    LocalizationAuditSeverity.Error,
                    $"A production build would ship whatever Addressables content is already on disk, " +
                    $"so localization edits made since the last content build would silently not " +
                    $"appear: {staleReason} Either let the player build rebuild content, or build it " +
                    "explicitly first (BuildManager's 'Build Addressables First' profile option).",
                    AssetDatabase.GetAssetPath(settings)));
            }
        }

        /// <summary>
        /// Mirrors Addressables' own decision about whether a player build rebuilds content.
        /// </summary>
        /// <param name="settings">Project Addressables settings.</param>
        /// <param name="reason">Explanation of why content will not be rebuilt.</param>
        /// <returns>True when this player build will rebuild Addressables content.</returns>
        /// <remarks>
        /// Reimplements <c>AddressablesPlayerBuildProcessor.ShouldBuildAddressablesForPlayerBuild</c>,
        /// which is <c>internal</c> to the Addressables Editor assembly. Checking the setting for
        /// equality with <c>BuildWithPlayer</c> instead was wrong:
        /// <see cref="AddressableAssetSettings.PlayerBuildOption.PreferencesValue"/> — the Unity
        /// default — is not stale, it defers to an Editor preference that itself defaults to on. That
        /// blocked builds which were in fact rebuilding their content.
        /// </remarks>
        private static bool WillBuildAddressablesWithPlayer(
            AddressableAssetSettings settings,
            out string reason)
        {
            reason = string.Empty;
            switch (settings.BuildAddressablesWithPlayerBuild)
            {
                case AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer:
                    reason = "Addressables is set to 'Do Not Build Addressables With Player'.";
                    return false;

                case AddressableAssetSettings.PlayerBuildOption.PreferencesValue:
                    // The preference key is internal to the Addressables Editor assembly, so it is
                    // duplicated here; the default matches AddressablesPreferences, which reads a
                    // missing preference as enabled.
                    if (!EditorPrefs.GetBool(AddressablesBuildWithPlayerPrefKey, true))
                    {
                        reason =
                            "Addressables defers to this machine's 'Build Addressables on Player " +
                            "Build' preference, which is off. Note that this preference is per-machine " +
                            "and not committed, so CI and local builds can disagree.";
                        return false;
                    }

                    return true;

                default:
                    return true;
            }
        }

        private static void AuditSerializedAssets(
            LocalizationAuditSnapshot snapshot,
            IReadOnlyCollection<string> configuredCodes)
        {
            var includePrefabs = snapshot.Request.Scope.HasFlag(LocalizationAuditScope.Prefabs);
            var includeScriptableObjects =
                snapshot.Request.Scope.HasFlag(LocalizationAuditScope.ScriptableObjects);
            if (!includePrefabs && !includeScriptableObjects)
                return;

            var query = includePrefabs && includeScriptableObjects
                ? "t:Prefab t:ScriptableObject"
                : includePrefabs ? "t:Prefab" : "t:ScriptableObject";
            var assetPaths = AssetDatabase.FindAssets(query, new[] { "Assets", "Packages" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsProjectOrMolcaPackagePath)
                .Where(ContainsSerializedLocalization)
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            snapshot.Coverage.DeclaredAssets = assetPaths.Count;

            for (var index = 0; index < assetPaths.Count; index++)
            {
                snapshot.Request.CancellationToken.ThrowIfCancellationRequested();
                var path = assetPaths[index];
                if (snapshot.Request.IsIgnored?.Invoke(path) == true)
                {
                    snapshot.Coverage.IgnoredAssets++;
                    continue;
                }

                snapshot.Request.ReportStatus?.Invoke(
                    $"Localization assets {index + 1}/{assetPaths.Count}");
                try
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(path);
                    IEnumerable<UnityEngine.Object> targets = main switch
                    {
                        GameObject gameObject => gameObject
                            .GetComponentsInChildren<MonoBehaviour>(true)
                            .Where(component => component != null)
                            .Cast<UnityEngine.Object>(),
                        ScriptableObject => AssetDatabase.LoadAllAssetsAtPath(path)
                            .OfType<ScriptableObject>()
                            .Cast<UnityEngine.Object>(),
                        _ => Enumerable.Empty<UnityEngine.Object>(),
                    };

                    ScanObjects(snapshot, targets, path, configuredCodes);
                    snapshot.AddSourcePath(path);
                    snapshot.Coverage.ScannedAssets++;
                    if (path.StartsWith("Packages/", StringComparison.Ordinal))
                        snapshot.Coverage.PackageAssets++;
                }
                catch (Exception exception)
                {
                    snapshot.Coverage.FailedAssets++;
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-audit-coverage-incomplete",
                        LocalizationAuditSeverity.Error,
                        $"Failed to scan localization asset: {exception.Message}",
                        path));
                }
            }
        }

        private static void AuditScenes(
            LocalizationAuditSnapshot snapshot,
            IReadOnlyCollection<string> configuredCodes)
        {
            var scenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Request.Scope.HasFlag(LocalizationAuditScope.LoadedScenes))
            {
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    if (scene.isLoaded)
                        scenePaths.Add(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);
                }
            }

            if (snapshot.Request.Scope.HasFlag(LocalizationAuditScope.BuildScenes))
                foreach (var scene in EditorBuildSettings.scenes.Where(scene => scene.enabled))
                    if (ContainsSerializedLocalization(scene.path))
                        scenePaths.Add(scene.path);

            snapshot.Coverage.DeclaredScenes = scenePaths.Count;
            foreach (var path in scenePaths.OrderBy(value => value, StringComparer.Ordinal))
            {
                snapshot.Request.CancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Request.IsIgnored?.Invoke(path) == true)
                {
                    // Scene ignores are policy exclusions and therefore do not create a coverage gap.
                    snapshot.Coverage.DeclaredScenes--;
                    continue;
                }

                snapshot.Request.ReportStatus?.Invoke($"Localization scene {path}");
                var scene = SceneManager.GetSceneByPath(path);
                var openedForAudit = false;
                try
                {
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                        openedForAudit = true;
                    }

                    var targets = scene.GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                        .Where(component => component != null)
                        .Cast<UnityEngine.Object>();
                    ScanObjects(snapshot, targets, path, configuredCodes);
                    snapshot.AddSourcePath(path);
                    snapshot.Coverage.ScannedScenes++;
                }
                catch (Exception exception)
                {
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-audit-coverage-incomplete",
                        LocalizationAuditSeverity.Error,
                        $"Failed to scan localization scene: {exception.Message}",
                        path));
                }
                finally
                {
                    if (openedForAudit && scene.IsValid() && scene.isLoaded)
                        EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void ScanObjects(
            LocalizationAuditSnapshot snapshot,
            IEnumerable<UnityEngine.Object> objects,
            string assetPath,
            IReadOnlyCollection<string> configuredCodes)
        {
            foreach (var target in objects)
            {
                if (target is LocalizedText localizedText && localizedText.enabled)
                    snapshot.AddFinding(EvaluateReference(localizedText, assetPath));

                if (target is LocalizedText rtlText &&
                    _auditHasConfiguredRtl &&
                    rtlText.GetComponentInParent<HorizontalLayoutGroup>() != null &&
                    rtlText.GetComponentInParent<LocalizedLayoutDirectionAdapter>() == null)
                    snapshot.AddFinding(new LocalizationAuditFinding(
                        "localization-rtl-readiness",
                        snapshot.Request.RequireCompleteTranslations
                            ? LocalizationAuditSeverity.Error
                            : LocalizationAuditSeverity.Warning,
                        $"LocalizedText '{target.name}' is inside a horizontal layout, but no explicit RTL adapter is present.",
                        assetPath,
                        "m_Parent"));

                var serialized = new SerializedObject(target);
                var property = serialized.GetIterator();
                var enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.Generic)
                        continue;

                    if (!LocalizedValueSerializedUtility.TryDescribe(
                            property,
                            out var descriptor))
                    {
                        var audioRows = property.FindPropertyRelative("_languageClips");
                        if (audioRows != null && audioRows.isArray &&
                            property.FindPropertyRelative("id") != null)
                        {
                            enterChildren = false;
                            AuditLocalizedAudioRows(
                                snapshot,
                                target,
                                property,
                                audioRows,
                                assetPath,
                                configuredCodes);
                        }
                        continue;
                    }

                    enterChildren = false;
                    if (descriptor.IsLegacy)
                        AddInlineFinding(
                            snapshot,
                            "localization-legacy-value-schema",
                            LocalizationAuditSeverity.Warning,
                            target,
                            property,
                            $"uses legacy schema v{descriptor.SchemaVersion.intValue}. " +
                            "Preview and execute the Localization Hub value migration.",
                            assetPath);

                    if (descriptor.SourceKind == LocalizedValueSourceKind.Catalog ||
                        descriptor.Disabled?.boolValue == true ||
                        descriptor.Rows == null)
                        continue;

                    var diagnostics = LocalizationAuthoringUtility.Analyze(
                        descriptor.Rows,
                        configuredCodes,
                        descriptor.CodeField);
                    foreach (var invalidCode in diagnostics.InvalidCodes)
                    {
                        var label = string.IsNullOrWhiteSpace(invalidCode) ? "<blank>" : invalidCode;
                        AddInlineFinding(
                            snapshot, "localization-inline-row-invalid",
                            LocalizationAuditSeverity.Error, target, property,
                            $"contains an unknown language row '{label}'.", assetPath);
                    }
                    foreach (var duplicateCode in diagnostics.DuplicateCodes)
                        AddInlineFinding(
                            snapshot, "localization-inline-row-invalid",
                            LocalizationAuditSeverity.Error, target, property,
                            $"contains duplicate rows for '{duplicateCode}'.", assetPath);
                    foreach (var missingCode in diagnostics.MissingCodes)
                        AddInlineFinding(
                            snapshot, "localization-inline-coverage-missing",
                            snapshot.Request.RequireCompleteTranslations
                                ? LocalizationAuditSeverity.Error
                                : LocalizationAuditSeverity.Warning,
                            target, property,
                            $"is missing required language '{missingCode}'. " +
                            "Use Add Missing Languages in the Inspector.",
                            assetPath);

                    for (var row = 0; row < descriptor.Rows.arraySize; row++)
                    {
                        var entry = descriptor.Rows.GetArrayElementAtIndex(row);
                        var code = entry.FindPropertyRelative(descriptor.CodeField)?.stringValue;
                        var text = entry.FindPropertyRelative(descriptor.ValueField)?.stringValue;
                        if (configuredCodes.Contains(code) && string.IsNullOrEmpty(text))
                            AddInlineFinding(
                                snapshot, "localization-required-translation-missing",
                                snapshot.Request.RequireCompleteTranslations
                                    ? LocalizationAuditSeverity.Error
                                    : LocalizationAuditSeverity.Warning,
                                target, property,
                                $"has an empty value for required language '{code}'.",
                                assetPath);
                    }
                }
            }
        }

        /// <summary>
        /// Judges one enabled <see cref="LocalizedText"/> reference slot against what the component says
        /// that slot is for.
        /// </summary>
        /// <param name="text">The component to judge.</param>
        /// <param name="assetPath">Project-relative path of the owning prefab, scene, or asset.</param>
        /// <returns>The finding, or <c>null</c> when the slot and the declaration agree.</returns>
        /// <remarks>
        /// <para>An empty slot is only a defect when nobody claimed responsibility for filling it. A label
        /// whose text arrives from code — a name, a count, a server string — is authored empty on purpose,
        /// and reporting every one of them would drown the findings that are real. So the emptiness alone
        /// no longer decides: <see cref="LocalizedText.RuntimeAssigned"/> does, and the component itself
        /// checks at runtime that the promise it makes here is kept.</para>
        /// <para>The declaration is held to the same standard in reverse. Marked *and* authored is
        /// incoherent — the authored value renders until code replaces it, so the label flashes one string
        /// and settles on another — and it is how a truthful flag decays into a blanket suppression that
        /// outlives the reason it was ticked.</para>
        /// <para>Separate from <see cref="ScanObjects"/>, its only production caller, so the rule can be
        /// tested against a bare component instead of a prefab fixture.</para>
        /// </remarks>
        internal static LocalizationAuditFinding EvaluateReference(LocalizedText text, string assetPath)
        {
            if (text == null)
                return null;

            var reference = text.GetLocalizedString();
            var isEmpty = reference == null || reference.IsEmpty;

            if (text.RuntimeAssigned)
                return isEmpty
                    ? null
                    : new LocalizationAuditFinding(
                        "localization-runtime-assigned-authored",
                        LocalizationAuditSeverity.Warning,
                        $"LocalizedText '{text.name}' is marked Runtime Assigned but also has an authored " +
                        "LocalizedString. The authored value renders until code replaces it; keep one of " +
                        "the two.",
                        assetPath, "runtimeAssigned");

            return isEmpty
                ? new LocalizationAuditFinding(
                    "localization-reference-empty",
                    LocalizationAuditSeverity.Warning,
                    $"Enabled LocalizedText '{text.name}' has no LocalizedString reference. Assign one, " +
                    "or tick Runtime Assigned if code supplies it.",
                    assetPath, "localizedString")
                : null;
        }

        private static void AddInlineFinding(
            LocalizationAuditSnapshot snapshot,
            string id,
            LocalizationAuditSeverity severity,
            UnityEngine.Object target,
            SerializedProperty property,
            string detail,
            string assetPath)
        {
            snapshot.AddFinding(new LocalizationAuditFinding(
                id, severity,
                $"LocalizedValue '{property.propertyPath}' on '{target.name}' {detail}",
                assetPath, property.propertyPath));
        }

        private static void AuditLocalizedAudioRows(
            LocalizationAuditSnapshot snapshot,
            UnityEngine.Object target,
            SerializedProperty property,
            SerializedProperty rows,
            string assetPath,
            IReadOnlyCollection<string> configuredCodes)
        {
            var diagnostics = LocalizationAuthoringUtility.Analyze(
                rows,
                configuredCodes,
                "languageCode");
            foreach (var invalidCode in diagnostics.InvalidCodes)
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-audio-row-invalid",
                    LocalizationAuditSeverity.Error,
                    $"LocalizedAudioEntry '{property.propertyPath}' on '{target.name}' contains " +
                    $"an unknown language row '{(string.IsNullOrWhiteSpace(invalidCode) ? "<blank>" : invalidCode)}'.",
                    assetPath,
                    property.propertyPath));
            foreach (var duplicateCode in diagnostics.DuplicateCodes)
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-audio-row-invalid",
                    LocalizationAuditSeverity.Error,
                    $"LocalizedAudioEntry '{property.propertyPath}' on '{target.name}' contains " +
                    $"duplicate rows for '{duplicateCode}'.",
                    assetPath,
                    property.propertyPath));
            foreach (var missingCode in diagnostics.MissingCodes)
                snapshot.AddFinding(new LocalizationAuditFinding(
                    "localization-audio-coverage-missing",
                    LocalizationAuditSeverity.Warning,
                    $"LocalizedAudioEntry '{property.propertyPath}' on '{target.name}' is missing " +
                    $"a locale slot for '{missingCode}'. Add it explicitly in the Inspector.",
                    assetPath,
                    property.propertyPath));
        }

        private static bool IsProjectOrMolcaPackagePath(string path) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) ||
            path.StartsWith("Packages/com.molca.", StringComparison.Ordinal);

        private static bool HasConfiguredRtlLocale() =>
            AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .Where(module => module != null)
                .SelectMany(module => module.Languages ?? Array.Empty<LocalizationModule.LanguageEntry>())
                .Any(language => language.PresentationProfile?.IsRightToLeft == true);

        /// <summary>
        /// Fast YAML prefilter shared by audit and migration discovery before loading serialized objects.
        /// Binary or unreadable assets are retained so the authoritative SerializedObject scan decides.
        /// </summary>
        internal static bool ContainsSerializedLocalization(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            var localizedTextGuid = AssetDatabase.AssetPathToGUID(
                "Packages/com.molca.core/Runtime/Localization/LocalizedText.cs");
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.IndexOf("useLocalizedString:", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("inlineSource:", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("_languageClips:", StringComparison.Ordinal) >= 0)
                        return true;
                    if (!string.IsNullOrEmpty(localizedTextGuid) &&
                        line.IndexOf(localizedTextGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception)
            {
                // Unity can serialize some ScriptableObjects in binary. Include them rather than
                // failing open; the regular SerializedObject scan will decide whether they contain
                // localization.
                return true;
            }

            return false;
        }

        private static void FinalizeSnapshot(LocalizationAuditSnapshot snapshot)
        {
            var fingerprintInput = snapshot.SourcePaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{path}:{AssetDatabase.GetAssetDependencyHash(path)}");
            snapshot.CatalogFingerprint = Hash128.Compute(
                string.Join("|", fingerprintInput)).ToString();

            // Stable ordering makes Doctor, Hub, MCP, build, and exported reports comparable.
            var ordered = snapshot.Findings
                .OrderBy(finding => finding.Id, StringComparer.Ordinal)
                .ThenBy(finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.PropertyPath, StringComparer.Ordinal)
                .ThenBy(finding => finding.Message, StringComparer.Ordinal)
                .ToArray();
            var mutableFindings = snapshot.Findings as List<LocalizationAuditFinding>;
            if (mutableFindings != null)
            {
                mutableFindings.Clear();
                mutableFindings.AddRange(ordered);
            }

            snapshot.Status = !snapshot.Coverage.IsComplete
                ? LocalizationAuditStatus.Incomplete
                : snapshot.Findings.Count > 0
                    ? LocalizationAuditStatus.Findings
                    : LocalizationAuditStatus.Clean;
        }
    }
}
