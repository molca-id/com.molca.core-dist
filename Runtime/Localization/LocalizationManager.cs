using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using Molca.Settings;
using Molca.Events;

namespace Molca.Localization
{
    public class LocalizationManager : RuntimeSubsystem
    {
        private const string DYNAMIC_TABLE_KEY = "Dynamic";

        private LocalizationModule _localizationModule;
        private readonly HashSet<LocalizedText> _localizedTexts = new();
        private readonly HashSet<DynamicLocalization> _dynamicLocalizations = new();
        private readonly Dictionary<string, LocalizedString> _localizedStringCache = new();
        private bool _isInitialized;

        // Static properties/method preserve the public API surface expected by SDK layers
        // and non-injectable contexts. All route through the service locator.

        /// <summary>The BCP-47 code of the currently active locale.</summary>
        public static string CurrentLanguage
        {
            get
            {
                var mgr = RuntimeManager.GetSubsystem<LocalizationManager>();
                if (mgr == null || mgr._localizationModule == null) return string.Empty;
                return mgr._localizationModule.ActiveLanguage;
            }
        }

        /// <summary>The first language code defined in <see cref="LocalizationModule.Languages"/>.</summary>
        public static string DefaultLanguageCode
        {
            get
            {
                var mgr = RuntimeManager.GetSubsystem<LocalizationManager>();
                if (mgr == null || mgr._localizationModule == null) return null;
                var codes = mgr._localizationModule.LanguageCode;
                return codes != null && codes.Length > 0 ? codes[0] : null;
            }
        }

        /// <summary>Switches the active locale. From injected contexts, call <see cref="ApplyLocale"/> on the instance directly.</summary>
        public static void SetLanguage(string lang) =>
            RuntimeManager.GetSubsystem<LocalizationManager>()?.ApplyLocale(lang);

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            if (_isInitialized)
            {
                finishCallback?.Invoke(this);
                return;
            }

            InitializeAsync(finishCallback);
        }

        private async void InitializeAsync(Action<IRuntimeSubsystem> finishCallback)
        {
            try
            {
                await RuntimeManager.AwaitHandle(LocalizationSettings.InitializationOperation);

                _localizationModule = GlobalSettings.GetModule<LocalizationModule>();
                ApplyLocale(_localizationModule.ActiveLanguage);

                LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
                _isInitialized = true;

                finishCallback?.Invoke(this);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to initialize LocalizationManager: {e.Message}");
                finishCallback?.Invoke(this);
            }
        }

        private void OnDestroy()
        {
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
        }

        private void HandleLocaleChanged(Locale locale)
        {
            _localizationModule.SetLanguage(locale.Identifier.Code);
            TypedEvents.LanguageChanged.Dispatch(_localizationModule.ActiveLanguage);
            RefreshAllTexts();
        }

        private void RefreshAllTexts()
        {
            foreach (var dynamicLoc in _dynamicLocalizations)
            {
                if (dynamicLoc != null)
                    dynamicLoc.RefreshCachedString();
            }
            _dynamicLocalizations.RemoveWhere(d => d == null);

            foreach (var text in _localizedTexts)
            {
                if (text != null)
                    text.OnRefresh(CurrentLanguage);
            }
            _localizedTexts.RemoveWhere(t => t == null);
        }

        /// <summary>Switches the active locale to <paramref name="lang"/> on this instance.</summary>
        internal void ApplyLocale(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return;
            var locale = LocalizationSettings.AvailableLocales.GetLocale(lang);
            if (locale != null)
                LocalizationSettings.SelectedLocale = locale;
        }

        /// <summary>Registers a <see cref="LocalizedText"/> to receive language-change notifications.</summary>
        public bool RegisterText(LocalizedText localizedText) => _localizedTexts.Add(localizedText);

        /// <summary>Unregisters a <see cref="LocalizedText"/> from language-change notifications.</summary>
        public bool UnregisterText(LocalizedText localizedText) => _localizedTexts.Remove(localizedText);

        /// <summary>Registers a <see cref="DynamicLocalization"/> to receive language-change notifications.</summary>
        public bool RegisterDynamicLocalization(DynamicLocalization dynamicLocalization) =>
            _dynamicLocalizations.Add(dynamicLocalization);

        /// <summary>Unregisters a <see cref="DynamicLocalization"/> from language-change notifications.</summary>
        public bool UnregisterDynamicLocalization(DynamicLocalization dynamicLocalization) =>
            _dynamicLocalizations.Remove(dynamicLocalization);

        /// <summary>
        /// Updates or creates a localization entry in the Dynamic StringTable for the given locale.
        /// </summary>
        /// <param name="key">The entry key.</param>
        /// <param name="languageCode">BCP-47 language code of the target locale.</param>
        /// <param name="value">The translated string.</param>
        /// <returns>A <see cref="LocalizedString"/> pointing at the entry.</returns>
        public async Awaitable<LocalizedString> UpdateEntry(string key, string languageCode, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(languageCode))
                return GetLocale(key);

            try
            {
                var stringDatabase = LocalizationSettings.StringDatabase;
                var locale = LocalizationSettings.AvailableLocales.GetLocale(languageCode);

                if (stringDatabase != null && locale != null)
                {
                    var tableHandle = stringDatabase.GetTableAsync(DYNAMIC_TABLE_KEY, locale);
                    await RuntimeManager.AwaitHandle(tableHandle);
                    var table = tableHandle.Result;

                    if (table != null)
                    {
                        var entry = table.GetEntry(key);
                        if (entry == null)
                            table.AddEntry(key, value);
                        else
                            entry.Value = value;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to update entry {key}: {e.Message}");
            }

            return GetLocale(key);
        }

        /// <summary>
        /// Returns a cached <see cref="LocalizedString"/> for the given key in the Dynamic table.
        /// </summary>
        public LocalizedString GetLocale(string key)
        {
            if (string.IsNullOrEmpty(key))
                return new LocalizedString(DYNAMIC_TABLE_KEY, string.Empty);

            if (_localizedStringCache.TryGetValue(key, out var cached))
                return cached;

            var newEntry = new LocalizedString(DYNAMIC_TABLE_KEY, key);
            _localizedStringCache[key] = newEntry;
            return newEntry;
        }

        /// <summary>
        /// Returns a <see cref="LocalizedString"/> for a specific string collection and entry key.
        /// </summary>
        /// <param name="collectionName">The name of the string table collection.</param>
        /// <param name="entryKey">The entry key within the collection.</param>
        public static LocalizedString GetLocalizedString(string collectionName, string entryKey)
        {
            if (string.IsNullOrEmpty(collectionName) || string.IsNullOrEmpty(entryKey))
            {
                Debug.LogWarning($"Invalid collection name or entry key: Collection='{collectionName}', Key='{entryKey}'");
                return new LocalizedString();
            }

            return new LocalizedString(collectionName, entryKey);
        }

        /// <summary>
        /// Gets the translated string for <paramref name="key"/> from the Dynamic table asynchronously.
        /// Returns <paramref name="key"/> itself as fallback if no entry is found.
        /// </summary>
        public async Awaitable<string> GetLocalizedStringAsync(string key, string languageCode = null)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            try
            {
                var stringDatabase = LocalizationSettings.StringDatabase;
                if (stringDatabase == null)
                    return key;

                var tableHandle = stringDatabase.GetTableAsync(DYNAMIC_TABLE_KEY);
                await RuntimeManager.AwaitHandle(tableHandle);
                var table = tableHandle.Result;
                if (table == null)
                    return key;

                var entry = table.GetEntry(key);
                return entry?.GetLocalizedString() ?? key;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to get localized string for key '{key}': {e.Message}");
                return key;
            }
        }

        /// <summary>Returns BCP-47 codes for all locales registered in <see cref="LocalizationSettings"/>.</summary>
        public string[] GetAvailableLanguages() =>
            LocalizationSettings.AvailableLocales.Locales.Select(l => l.Identifier.Code).ToArray();

        /// <summary>Returns true if <paramref name="languageCode"/> is a registered locale.</summary>
        public bool HasLanguage(string languageCode) =>
            LocalizationSettings.AvailableLocales.GetLocale(languageCode) != null;
    }
}
