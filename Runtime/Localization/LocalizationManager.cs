using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Events;
using Molca.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace Molca.Localization
{
    /// <summary>
    /// Owns locale selection and coordinates localization consumers at runtime.
    /// </summary>
    /// <remarks>
    /// <see cref="LocalizationSettings.SelectedLocale"/> is the runtime source of truth.
    /// <see cref="LocalizationModule"/> stores authored language policy and the last valid
    /// selection only.
    /// </remarks>
    // The strongest case for declaring a requirement: without this module the subsystem throws
    // InvalidOperationException during bootstrap. Declared, that becomes an edit-time finding instead.
    [RequiresSettingModule(typeof(LocalizationModule))]
    public class LocalizationManager : RuntimeSubsystem
    {
        private const string DynamicTableKey = "Dynamic";

        private LocalizationModule _localizationModule;
        private readonly HashSet<LocalizedText> _localizedTexts = new();
        private readonly List<WeakReference<LocalizedValue>> _localizedValues = new();
        private readonly Dictionary<string, LocalizedString> _localizedStringCache = new();
        private TMP_FontAsset[] _baseTmpFallbackFonts = Array.Empty<TMP_FontAsset>();
        private bool _capturedTmpFallbackFonts;
        private bool _isInitialized;
        private bool _localeChangedSubscribed;
        private bool _isCorrectingInvalidLocale;
        private LocalizationOverlayManager _overlayManager;
        private LocalizationRemoteCatalogClient _remoteCatalogClient;

        /// <summary>
        /// The live subsystem, or <c>null</c> when the runtime is not up.
        /// </summary>
        /// <remarks>
        /// Every static accessor below already tolerates a missing manager, so they resolve quietly:
        /// they are reached from edit-mode paths such as <c>LocalizedText.OnValidate</c>, where a
        /// "runtime not initialized" warning per inspected component is pure noise.
        /// </remarks>
        private static LocalizationManager Resolved =>
            RuntimeManager.TryGetService<LocalizationManager>(out var manager) ? manager : null;

        /// <summary>The BCP-47 code of the locale currently selected by Unity Localization.</summary>
        public static string CurrentLanguage
        {
            get
            {
                var selectedCode = LocalizationSettings.SelectedLocale?.Identifier.Code;
                if (!string.IsNullOrEmpty(selectedCode))
                    return selectedCode;

                var manager = Resolved;
                return manager == null
                    ? string.Empty
                    : manager._localizationModule?.ActiveLanguage ?? string.Empty;
            }
        }

        /// <summary>The first valid language code defined in <see cref="LocalizationModule.Languages"/>.</summary>
        public static string DefaultLanguageCode
        {
            get
            {
                var manager = Resolved;
                return manager?._localizationModule?.LanguageCode
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
            }
        }

        /// <summary>Returns the authored presentation profile for a configured locale.</summary>
        public static LocalePresentationProfile GetPresentationProfile(string languageCode)
        {
            var manager = Resolved;
            return manager?._localizationModule?.GetPresentationProfile(languageCode);
        }

        /// <summary>Returns the explicit authored fallback chain for a locale.</summary>
        public static IReadOnlyList<string> GetFallbackChain(string languageCode)
        {
            var manager = Resolved;
            if (manager?._localizationModule != null)
                return manager._localizationModule.GetFallbackChain(languageCode);
            return string.IsNullOrWhiteSpace(languageCode)
                ? Array.Empty<string>()
                : new[] { LocalizationModule.CanonicalizeLocaleCode(languageCode) };
        }

        /// <summary>Current remote localization overlay status.</summary>
        public static LocalizationOverlayStatus OverlayStatus
        {
            get
            {
                var manager = Resolved;
                return manager?._overlayManager?.Status ?? LocalizationOverlayStatus.Disabled;
            }
        }

        /// <summary>Current immutable remote overlay snapshot, when active.</summary>
        public static LocalizationOverlaySnapshot ActiveOverlay =>
            Resolved?._overlayManager?.Active;

        /// <summary>Checks for and atomically activates the current signed remote catalog.</summary>
        public static async Awaitable<LocalizationOverlayActivationResult> RefreshRemoteCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            var manager = Resolved;
            if (manager?._remoteCatalogClient == null)
                return new LocalizationOverlayActivationResult(
                    false,
                    "localization-overlay-disabled",
                    "Remote localization is not configured.",
                    string.Empty);
            return await manager._remoteCatalogClient.RefreshAsync(cancellationToken);
        }

        /// <summary>Restores the prior verified in-memory remote catalog snapshot.</summary>
        public static LocalizationOverlayActivationResult RollbackRemoteCatalog()
        {
            var manager = Resolved;
            return manager?._overlayManager?.Rollback() ??
                   new LocalizationOverlayActivationResult(
                       false,
                       "localization-overlay-disabled",
                       "Remote localization is not configured.",
                       string.Empty);
        }

        /// <summary>Resolves a catalog reference through the active overlay and locale fallback policy.</summary>
        public static bool TryResolveOverlay(
            LocalizedString reference,
            string languageCode,
            out string value,
            out string resolvedLocale)
            => TryResolveOverlay(
                reference,
                languageCode,
                reference?.Arguments,
                out value,
                out resolvedLocale);

        internal static bool TryResolveOverlay(
            LocalizedString reference,
            string languageCode,
            IList<object> arguments,
            out string value,
            out string resolvedLocale)
        {
            value = string.Empty;
            resolvedLocale = string.Empty;
            var manager = Resolved;
            var snapshot = manager?._overlayManager?.Active;
            if (snapshot == null || reference == null || reference.IsEmpty)
                return false;

            var tableReference = reference.TableReference;
            var collectionGuid = tableReference.TableCollectionNameGuid;
            var entryReference = reference.TableEntryReference;
            var entryId = entryReference.ReferenceType == TableEntryReference.Type.Id
                ? entryReference.KeyId
                : SharedTableData.EmptyId;
            if (collectionGuid == Guid.Empty || entryId <= 0)
                return false;
            if (!snapshot.TryGet(
                collectionGuid.ToString("N"),
                entryId,
                manager._localizationModule.GetFallbackChain(languageCode),
                out value,
                out resolvedLocale))
                return false;
            try
            {
                if (value.IndexOf('{') < 0)
                    return true;
                var formatter = LocalizationSettings.StringDatabase?.SmartFormatter;
                if (formatter == null)
                    return false;
                var locale = LocalizationSettings.AvailableLocales?.GetLocale(resolvedLocale);
                value = formatter.Format(
                    locale?.Formatter,
                    value,
                    arguments?.ToArray() ?? Array.Empty<object>());
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Localization overlay formatting failed for {collectionGuid:N}:{entryId}: " +
                    exception.Message);
                value = string.Empty;
                resolvedLocale = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Requests a locale switch while preserving the legacy fire-and-forget API.
        /// </summary>
        /// <param name="lang">BCP-47 language code of a configured Unity locale.</param>
        public static void SetLanguage(string lang)
        {
            var manager = Resolved;
            if (manager != null && !manager.ApplyLocale(lang))
                Debug.LogWarning($"LocalizationManager: locale '{lang}' is not configured and was not selected.");
        }

        /// <summary>
        /// Preserves the callback initialization API for older SDK consumers.
        /// </summary>
        /// <param name="finishCallback">Invoked after initialization succeeds.</param>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            InitializeLegacyAsync(finishCallback);
        }

        /// <summary>
        /// Initializes Unity Localization, validates configured policy, and selects a usable locale.
        /// </summary>
        /// <param name="cancellationToken">Cancelled when bootstrap times out or shuts down.</param>
        public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            if (_isInitialized)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            await RuntimeManager.AwaitHandle(LocalizationSettings.InitializationOperation);
            cancellationToken.ThrowIfCancellationRequested();

            _localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (_localizationModule == null)
                throw new InvalidOperationException(
                    "LocalizationModule is missing from GlobalSettings. Add one before enabling LocalizationManager.");

            InitializeRemoteCatalog();

            var configuredCodes = _localizationModule.LanguageCode
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (configuredCodes.Length == 0)
                throw new InvalidOperationException("LocalizationModule does not define a valid language code.");

            var usableCodes = configuredCodes
                .Where(code => LocalizationSettings.AvailableLocales.GetLocale(code) != null)
                .ToArray();
            if (usableCodes.Length == 0)
                throw new InvalidOperationException(
                    "None of the languages in LocalizationModule exist in Unity Localization Available Locales.");

            SubscribeToLocaleChanges();
            _baseTmpFallbackFonts = TMP_Settings.fallbackFontAssets?
                .Where(font => font != null)
                .Distinct()
                .ToArray() ?? Array.Empty<TMP_FontAsset>();
            _capturedTmpFallbackFonts = true;

            var selectedCode = LocalizationSettings.SelectedLocale?.Identifier.Code;
            var initialCode = IsUsable(_localizationModule.ActiveLanguage, usableCodes)
                ? _localizationModule.ActiveLanguage
                : IsUsable(selectedCode, usableCodes)
                    ? selectedCode
                    : usableCodes[0];

            if (!ApplyLocale(initialCode))
                throw new InvalidOperationException($"Failed to select the validated locale '{initialCode}'.");

            // Unity does not raise SelectedLocaleChanged when the requested locale is already selected.
            PersistSelectedLocale(LocalizationSettings.SelectedLocale, dispatchEvent: false);
            _isInitialized = true;
            StartRemoteRefresh();
        }

        /// <summary>
        /// Unsubscribes runtime callbacks and releases consumer registrations.
        /// </summary>
        public override void Teardown()
        {
            if (_localeChangedSubscribed)
            {
                LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
                _localeChangedSubscribed = false;
            }

            _localizedTexts.Clear();
            _localizedValues.Clear();
            _localizedStringCache.Clear();
            if (_overlayManager != null)
            {
                _overlayManager.Changed -= HandleOverlayChanged;
                _overlayManager.Dispose();
            }
            _overlayManager = null;
            _remoteCatalogClient = null;
            if (_capturedTmpFallbackFonts)
                TMP_Settings.fallbackFontAssets = _baseTmpFallbackFonts.ToList();
            _baseTmpFallbackFonts = Array.Empty<TMP_FontAsset>();
            _capturedTmpFallbackFonts = false;
            _isInitialized = false;
            base.Teardown();
        }

        private void InitializeRemoteCatalog()
        {
            var settings = _localizationModule?.RemoteCatalog;
            if (settings == null || !settings.Enabled)
                return;
            _overlayManager = new LocalizationOverlayManager(settings);
            _overlayManager.Changed += HandleOverlayChanged;
            _remoteCatalogClient = new LocalizationRemoteCatalogClient(settings, _overlayManager);
            var projectId = string.IsNullOrWhiteSpace(settings.ProjectId)
                ? BuildInfo.ProjectId
                : settings.ProjectId;
            _overlayManager.TryLoadLastKnownGood(
                _remoteCatalogClient.CacheDirectory,
                projectId,
                Application.version);
        }

        private async void StartRemoteRefresh() // doctor:ignore async-void owns background refresh exceptions
        {
            if (_remoteCatalogClient == null)
                return;
            try
            {
                var result = await _remoteCatalogClient.RefreshAsync(ShutdownToken);
                if (!result.Success &&
                    result.DiagnosticCode != "localization-overlay-build-token-missing")
                    Debug.LogWarning(
                        $"Localization remote catalog refresh rejected: {result.DiagnosticCode}: {result.Message}");
            }
            catch (OperationCanceledException)
            {
                // Runtime teardown superseded the optional refresh.
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Localization remote catalog refresh failed: {exception.Message}");
            }
        }

        private void HandleOverlayChanged() => RefreshAllTexts();

        private async void InitializeLegacyAsync(Action<IRuntimeSubsystem> finishCallback)
        {
            try
            {
                await InitializeAsync(ShutdownToken);
                finishCallback?.Invoke(this);
            }
            catch (OperationCanceledException)
            {
                // Runtime teardown owns cancellation reporting.
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to initialize LocalizationManager: {exception}");
                // The legacy callback has no failure channel. Complete it to preserve its
                // no-hang contract; RuntimeManager uses InitializeAsync and receives the fault.
                finishCallback?.Invoke(this);
            }
        }

        private static bool IsUsable(string code, IEnumerable<string> usableCodes) =>
            !string.IsNullOrWhiteSpace(code) &&
            usableCodes.Contains(code, StringComparer.OrdinalIgnoreCase);

        private void SubscribeToLocaleChanges()
        {
            if (_localeChangedSubscribed)
                return;

            LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
            _localeChangedSubscribed = true;
        }

        private void HandleLocaleChanged(Locale locale)
        {
            if (!PersistSelectedLocale(locale, dispatchEvent: true))
            {
                if (_isCorrectingInvalidLocale || _localizationModule == null)
                    return;

                _isCorrectingInvalidLocale = true;
                try
                {
                    var fallback = _localizationModule.HasLanguage(_localizationModule.ActiveLanguage)
                        ? _localizationModule.ActiveLanguage
                        : DefaultLanguageCode;
                    if (!ApplyLocale(fallback))
                        Debug.LogError(
                            $"LocalizationManager: failed to restore valid locale '{fallback}'.");
                }
                finally
                {
                    _isCorrectingInvalidLocale = false;
                }
                return;
            }

            RefreshAllTexts();
        }

        private bool PersistSelectedLocale(Locale locale, bool dispatchEvent)
        {
            var code = locale?.Identifier.Code;
            if (_localizationModule == null || !_localizationModule.HasLanguage(code))
            {
                Debug.LogError(
                    $"LocalizationManager: Unity selected locale '{code}', but it is not declared in LocalizationModule.");
                return false;
            }

            _localizationModule.SetLanguage(code);
            ApplyLocaleFontFallbacks(code);
            if (dispatchEvent)
                TypedEvents.LanguageChanged.Dispatch(code);
            return true;
        }

        private void ApplyLocaleFontFallbacks(string localeCode)
        {
            var localeFallbacks = _localizationModule
                ?.GetPresentationProfile(localeCode)
                ?.FallbackFonts ?? Array.Empty<TMP_FontAsset>();
            TMP_Settings.fallbackFontAssets = localeFallbacks
                .Concat(_baseTmpFallbackFonts)
                .Where(font => font != null)
                .Distinct()
                .ToList();
        }

        private void RefreshAllTexts()
        {
            for (var index = _localizedValues.Count - 1; index >= 0; index--)
            {
                if (_localizedValues[index].TryGetTarget(out var localization))
                    localization.RefreshCachedString();
                else
                    _localizedValues.RemoveAt(index);
            }

            foreach (var text in _localizedTexts)
            {
                if (text != null)
                    text.OnRefresh(CurrentLanguage);
            }

            _localizedTexts.RemoveWhere(text => text == null);
        }

        /// <summary>
        /// Selects a configured Unity locale.
        /// </summary>
        /// <param name="lang">BCP-47 language code.</param>
        /// <returns><c>true</c> when the locale was configured and selected.</returns>
        internal bool ApplyLocale(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang))
                return false;
            if (_localizationModule != null && !_localizationModule.HasLanguage(lang))
                return false;

            var locale = LocalizationSettings.AvailableLocales.GetLocale(lang);
            if (locale == null)
                return false;

            LocalizationSettings.SelectedLocale = locale;
            return true;
        }

        /// <summary>Registers a <see cref="LocalizedText"/> to receive language-change notifications.</summary>
        /// <param name="localizedText">Consumer to register.</param>
        /// <returns><c>true</c> when the consumer was newly registered.</returns>
        public bool RegisterText(LocalizedText localizedText) =>
            localizedText != null && _localizedTexts.Add(localizedText);

        /// <summary>Unregisters a <see cref="LocalizedText"/> from language-change notifications.</summary>
        /// <param name="localizedText">Consumer to unregister.</param>
        /// <returns><c>true</c> when the consumer was registered.</returns>
        public bool UnregisterText(LocalizedText localizedText) =>
            localizedText != null && _localizedTexts.Remove(localizedText);

        /// <summary>
        /// Registers a <see cref="DynamicLocalization"/> through a weak reference.
        /// </summary>
        /// <param name="dynamicLocalization">Consumer to register.</param>
        /// <returns><c>true</c> when the consumer was newly registered.</returns>
        public bool RegisterDynamicLocalization(DynamicLocalization dynamicLocalization)
            => RegisterLocalizedValue(dynamicLocalization);

        /// <summary>Registers a schema-v2 value through a weak reference.</summary>
        public bool RegisterLocalizedValue(LocalizedValue localizedValue)
        {
            if (localizedValue == null)
                return false;

            for (var index = _localizedValues.Count - 1; index >= 0; index--)
            {
                if (!_localizedValues[index].TryGetTarget(out var existing))
                    _localizedValues.RemoveAt(index);
                else if (ReferenceEquals(existing, localizedValue))
                    return false;
            }

            _localizedValues.Add(new WeakReference<LocalizedValue>(localizedValue));
            return true;
        }

        /// <summary>Unregisters a <see cref="DynamicLocalization"/> from language-change notifications.</summary>
        /// <param name="dynamicLocalization">Consumer to unregister.</param>
        /// <returns><c>true</c> when the consumer was registered.</returns>
        public bool UnregisterDynamicLocalization(DynamicLocalization dynamicLocalization)
            => UnregisterLocalizedValue(dynamicLocalization);

        /// <summary>Unregisters a schema-v2 value from language-change notifications.</summary>
        public bool UnregisterLocalizedValue(LocalizedValue localizedValue)
        {
            var removed = false;
            for (var index = _localizedValues.Count - 1; index >= 0; index--)
            {
                if (!_localizedValues[index].TryGetTarget(out var existing) ||
                    ReferenceEquals(existing, localizedValue))
                {
                    removed |= existing != null;
                    _localizedValues.RemoveAt(index);
                }
            }

            return removed;
        }

        /// <summary>
        /// Updates or creates an entry in the legacy Dynamic StringTable.
        /// </summary>
        /// <param name="key">The entry key.</param>
        /// <param name="languageCode">BCP-47 code of the target locale.</param>
        /// <param name="value">Translated value.</param>
        /// <returns>A <see cref="LocalizedString"/> referencing the legacy entry.</returns>
        /// <remarks>
        /// Retained for API compatibility. New inline localization resolves directly from
        /// serialized values and does not mutate StringTables at runtime.
        /// </remarks>
        public async Awaitable<LocalizedString> UpdateEntry(string key, string languageCode, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(languageCode))
                return GetLocale(key);

            try
            {
                var database = LocalizationSettings.StringDatabase;
                var locale = LocalizationSettings.AvailableLocales.GetLocale(languageCode);
                if (database == null || locale == null)
                    return GetLocale(key);

                var handle = database.GetTableAsync(DynamicTableKey, locale);
                await RuntimeManager.AwaitHandle(handle);
                var table = handle.Result;
                if (table == null)
                    return GetLocale(key);

                var entry = table.GetEntry(key);
                if (entry == null)
                    table.AddEntry(key, value);
                else
                    entry.Value = value;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to update entry '{key}': {exception.Message}");
            }

            return GetLocale(key);
        }

        /// <summary>Returns a cached reference to an entry in the legacy Dynamic StringTable.</summary>
        /// <param name="key">Entry key.</param>
        /// <returns>A localized string reference.</returns>
        public LocalizedString GetLocale(string key)
        {
            if (string.IsNullOrEmpty(key))
                return new LocalizedString(DynamicTableKey, string.Empty);
            if (_localizedStringCache.TryGetValue(key, out var cached))
                return cached;

            var created = new LocalizedString(DynamicTableKey, key);
            _localizedStringCache[key] = created;
            return created;
        }

        /// <summary>Creates a localized string reference for a collection and entry.</summary>
        /// <param name="collectionName">String table collection name.</param>
        /// <param name="entryKey">Entry key.</param>
        /// <returns>A localized string reference, or an empty reference for invalid arguments.</returns>
        public static LocalizedString GetLocalizedString(string collectionName, string entryKey)
        {
            if (string.IsNullOrEmpty(collectionName) || string.IsNullOrEmpty(entryKey))
            {
                Debug.LogWarning(
                    $"Invalid collection name or entry key: Collection='{collectionName}', Key='{entryKey}'");
                return new LocalizedString();
            }

            return new LocalizedString(collectionName, entryKey);
        }

        /// <summary>
        /// Gets a translated string from the legacy Dynamic table for an explicit or selected locale.
        /// </summary>
        /// <param name="key">Entry key.</param>
        /// <param name="languageCode">Optional BCP-47 locale code. Uses the selected locale when omitted.</param>
        /// <returns>The translation, or <paramref name="key"/> when it cannot be resolved.</returns>
        public async Awaitable<string> GetLocalizedStringAsync(string key, string languageCode = null)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            try
            {
                var database = LocalizationSettings.StringDatabase;
                if (database == null)
                    return key;

                Locale locale = null;
                if (!string.IsNullOrWhiteSpace(languageCode))
                {
                    locale = LocalizationSettings.AvailableLocales.GetLocale(languageCode);
                    if (locale == null)
                        return key;
                }

                var handle = locale == null
                    ? database.GetTableAsync(DynamicTableKey)
                    : database.GetTableAsync(DynamicTableKey, locale);
                await RuntimeManager.AwaitHandle(handle);
                var entry = handle.Result?.GetEntry(key);
                return entry?.GetLocalizedString() ?? key;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to get localized string for key '{key}': {exception.Message}");
                return key;
            }
        }

        /// <summary>Returns BCP-47 codes for all Unity Localization available locales.</summary>
        /// <returns>Configured locale codes.</returns>
        public string[] GetAvailableLanguages() =>
            LocalizationSettings.AvailableLocales.Locales.Select(locale => locale.Identifier.Code).ToArray();

        /// <summary>Determines whether Unity Localization contains a locale.</summary>
        /// <param name="languageCode">BCP-47 locale code.</param>
        /// <returns><c>true</c> when the locale is available.</returns>
        public bool HasLanguage(string languageCode) =>
            !string.IsNullOrWhiteSpace(languageCode) &&
            LocalizationSettings.AvailableLocales.GetLocale(languageCode) != null;
    }
}
