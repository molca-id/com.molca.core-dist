using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Molca.Localization
{
    [Serializable]
    public class DynamicLocalization
    {
        [Header("Localization Settings")]
        public bool disabled = false;
        public bool useLocalizedString = false;

        [SerializeField] private LocalizedString localizedString;

        [Header("Translations")]
        [SerializeField] private List<DynamicLocalizationEntry> translations = new List<DynamicLocalizationEntry>();

        private LocalizedString _locale;
        private string _cachedString = string.Empty;

        /// <summary>The active <see cref="LocalizedString"/>: the authored one when <see cref="useLocalizedString"/> is true, otherwise the runtime-managed one.</summary>
        public LocalizedString locale => useLocalizedString ? localizedString : _locale;

        /// <summary>
        /// Returns the last successfully resolved translation string without triggering an async fetch.
        /// Empty when <see cref="disabled"/> is true.
        /// </summary>
        public string String => disabled ? string.Empty :
            !string.IsNullOrEmpty(_cachedString) ? _cachedString :
            translations.Count > 0 ? (translations[0].text ?? string.Empty) : string.Empty;

        /// <summary>
        /// Resolves the localized string asynchronously, falling back through Unity's system,
        /// local translations, then the default language.
        /// </summary>
        public async Awaitable<string> GetLocalizedString()
        {
            if (disabled)
                return string.Empty;

            await RuntimeManager.WaitForInitialization();

            if (useLocalizedString && localizedString != null && !localizedString.IsEmpty)
            {
                try
                {
                    var handle = localizedString.GetLocalizedStringAsync();
                    await RuntimeManager.AwaitHandle(handle);
                    if (handle.Status == AsyncOperationStatus.Succeeded &&
                        !string.IsNullOrEmpty(handle.Result))
                    {
                        _cachedString = handle.Result;
                        return _cachedString;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to get localized string from LocalizedString: {e.Message}");
                }
            }

            if (_locale != null && !_locale.IsEmpty)
            {
                try
                {
                    var handle = _locale.GetLocalizedStringAsync();
                    await RuntimeManager.AwaitHandle(handle);
                    if (handle.Status == AsyncOperationStatus.Succeeded &&
                        !string.IsNullOrEmpty(handle.Result))
                    {
                        _cachedString = handle.Result;
                        return _cachedString;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to get localized string from Unity system: {e.Message}");
                }
            }

            var currentLanguage = LocalizationManager.CurrentLanguage;
            var localTranslation = GetTextForLanguage(currentLanguage);
            if (!string.IsNullOrEmpty(localTranslation))
            {
                _cachedString = localTranslation;
                return _cachedString;
            }

            var defaultLanguage = LocalizationManager.DefaultLanguageCode;
            if (!string.IsNullOrEmpty(defaultLanguage) && defaultLanguage != currentLanguage)
            {
                localTranslation = GetTextForLanguage(defaultLanguage);
                if (!string.IsNullOrEmpty(localTranslation))
                {
                    _cachedString = localTranslation;
                    return _cachedString;
                }
            }

            if (translations.Count > 0)
            {
                _cachedString = translations[0].text ?? string.Empty;
                return _cachedString;
            }

            _cachedString = string.Empty;
            return string.Empty;
        }

        [NonSerialized]
        private string _key = string.Empty;

        /// <summary>
        /// Adds an empty translation entry for <paramref name="languageCode"/> if one does
        /// not already exist. Editor-authoring helper so all supported languages show up as
        /// rows in the inspector even before text is assigned. Non-destructive.
        /// </summary>
        public void EnsureLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode)) return;
            if (translations.All(t => t.languageCode != languageCode))
                translations.Add(new DynamicLocalizationEntry(languageCode, ""));
        }

        /// <summary>Returns the translation for <paramref name="languageCode"/>, or empty if not found.</summary>
        public string GetTextForLanguage(string languageCode)
        {
            return translations.FirstOrDefault(t => t.languageCode == languageCode)?.text ?? string.Empty;
        }

        private async void UpdateEntryAsync(string key, string languageCode, string text)
        {
            try
            {
                var locMgr = RuntimeManager.GetSubsystem<LocalizationManager>();
                if (locMgr != null && !string.IsNullOrEmpty(key))
                {
                    var locString = await locMgr.UpdateEntry(key, languageCode, text);
                    locString.RefreshString();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to update runtime translation for key {key} in language {languageCode}: {e.Message}");
            }
        }

        /// <summary>
        /// Sets the translation for <paramref name="languageCode"/> (defaults to the current active language).
        /// Also pushes the value into the runtime Dynamic StringTable when playing.
        /// </summary>
        public void SetTextForLanguage(string text, string languageCode = null)
        {
            if (useLocalizedString)
            {
                Debug.LogWarning("Cannot set text for language when using LocalizedString. Use the LocalizedString directly.");
                return;
            }

            if (string.IsNullOrEmpty(languageCode) && Application.isPlaying)
                languageCode = LocalizationManager.CurrentLanguage;

            // Never write an entry with a null/empty language code: it is unmatchable by
            // GetTextForLanguage and permanently pollutes the translations list. This can
            // happen in the editor (no CurrentLanguage fallback) or before the manager is
            // ready at runtime (CurrentLanguage returns string.Empty).
            if (string.IsNullOrEmpty(languageCode))
            {
                Debug.LogWarning("DynamicLocalization.SetTextForLanguage: no language code could be resolved; " +
                                 "ignoring. Pass an explicit languageCode when not in play mode.");
                return;
            }

            var entry = translations.FirstOrDefault(t => t.languageCode == languageCode);
            if (entry != null)
                entry.text = text;
            else
                translations.Add(new DynamicLocalizationEntry(languageCode, text));

            if (!Application.isPlaying)
                return;

            UpdateEntryAsync(_key, languageCode, text);

            var locMgr = RuntimeManager.GetSubsystem<LocalizationManager>();
            if (languageCode == LocalizationManager.CurrentLanguage)
                _cachedString = text;
        }

        /// <summary>
        /// Registers this instance with <see cref="LocalizationManager"/> and pre-populates the
        /// Dynamic StringTable with authored translations. Awaitable form.
        /// </summary>
        /// <param name="key">Unique key used to identify this entry in the Dynamic table.</param>
        /// <param name="forceUpdate">When true, re-registers and re-pushes entries even if already initialized.</param>
        /// <returns>An awaitable that completes once registration and the initial resolve have finished.</returns>
        /// <remarks>
        /// Prefer this over the fire-and-forget <see cref="Init"/> whenever the caller resolves the
        /// string immediately afterwards (e.g. before calling <see cref="GetLocalizedString"/>):
        /// awaiting it closes the init/resolve race that the <c>async void</c> overload exposes.
        /// </remarks>
        public async Awaitable InitAsync(string key, bool forceUpdate = false)
        {
            if (disabled)
                return;

            await RuntimeManager.WaitForInitialization();

            var locMgr = RuntimeManager.GetSubsystem<LocalizationManager>();
            if (locMgr == null) return;

            if (useLocalizedString)
            {
                if (localizedString != null && !localizedString.IsEmpty)
                {
                    var handle = localizedString.GetLocalizedStringAsync();
                    await RuntimeManager.AwaitHandle(handle);
                    if (handle.Status == AsyncOperationStatus.Succeeded)
                        _cachedString = handle.Result;

                    locMgr.RegisterDynamicLocalization(this);
                }
                return;
            }

            if (string.IsNullOrEmpty(key) || translations.Count == 0)
                return;

            if (_key == key && !forceUpdate)
                return;

            // Claim the key before the first await so two concurrent Init/InitAsync calls
            // cannot both pass the guard above and double-register / double-push entries.
            _key = key;

            try
            {
                _locale = locMgr.GetLocale(key);

                foreach (var translation in translations)
                {
                    // Skip entries with no text or an invalid (null/empty) language code —
                    // UpdateEntry would no-op on the latter anyway, and pushing them only
                    // wastes table lookups.
                    if (!string.IsNullOrEmpty(translation.text) && !string.IsNullOrEmpty(translation.languageCode))
                        await locMgr.UpdateEntry(_key, translation.languageCode, translation.text);
                }

                var currentTranslation = GetTextForLanguage(LocalizationManager.CurrentLanguage);
                if (!string.IsNullOrEmpty(currentTranslation))
                    _cachedString = currentTranslation;

                locMgr.RegisterDynamicLocalization(this);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to initialize localization for key {_key}: {e.Message}");
            }
        }

        /// <summary>
        /// Fire-and-forget wrapper over <see cref="InitAsync"/> for Unity event-handler entry
        /// points (e.g. <c>Start</c>) that cannot await. When the string is resolved immediately
        /// afterwards, await <see cref="InitAsync"/> instead to avoid an init/resolve race.
        /// </summary>
        /// <param name="key">Unique key used to identify this entry in the Dynamic table.</param>
        /// <param name="forceUpdate">When true, re-registers and re-pushes entries even if already initialized.</param>
        public async void Init(string key, bool forceUpdate = false)
        {
            try
            {
                await InitAsync(key, forceUpdate);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to initialize localization for key {key}: {e.Message}");
            }
        }

        /// <summary>
        /// Refreshes <see cref="String"/> from the localization system. Called by
        /// <see cref="LocalizationManager"/> on language change.
        /// </summary>
        public async void RefreshCachedString()
        {
            if (disabled)
                return;

            try
            {
                // Re-resolve through the full chain (Unity system → local translations →
                // default language) and cache the result. This is intentionally read-only:
                // the previous implementation routed through SetTextForLanguage, which
                // re-pushed entries into the Dynamic table on every language change, and it
                // ignored useLocalizedString — GetLocalizedString honors both correctly.
                _cachedString = await GetLocalizedString();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to refresh localized string: {e.Message}");
            }
        }

        /// <summary>
        /// Assigns a <see cref="LocalizedString"/> to this instance.
        /// Only valid when <see cref="useLocalizedString"/> is true.
        /// </summary>
        public void SetLocalizedString(LocalizedString value)
        {
            if (!useLocalizedString)
            {
                Debug.LogWarning("Cannot set LocalizedString when useLocalizedString is false.");
                return;
            }

            localizedString = value;
        }
    }
}
