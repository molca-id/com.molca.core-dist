using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Molca.Localization
{
    /// <summary>Serialized source kind for a schema-v2 <see cref="LocalizedValue"/>.</summary>
    public enum LocalizedValueSourceKind
    {
        None = 0,
        Catalog = 1,
        Inline = 2,
    }

    /// <summary>One locale-keyed inline string in a schema-v2 localized value.</summary>
    [Serializable]
    public sealed class LocalizedInlineValue
    {
        [SerializeField] private string localeCode;
        [SerializeField] private string value;

        public LocalizedInlineValue(string localeCode, string value)
        {
            this.localeCode = localeCode ?? string.Empty;
            this.value = value ?? string.Empty;
        }

        public string LocaleCode
        {
            get => localeCode ?? string.Empty;
            set => localeCode = value ?? string.Empty;
        }

        public string Value
        {
            get => value ?? string.Empty;
            set => this.value = value ?? string.Empty;
        }
    }

    /// <summary>Explicit Unity StringTable source for a schema-v2 localized value.</summary>
    [Serializable]
    public sealed class LocalizedCatalogSource
    {
        [SerializeField] private LocalizedString reference = new();

        public LocalizedString Reference
        {
            get => reference;
            set => reference = value ?? new LocalizedString();
        }

        public bool IsEmpty => reference == null || reference.IsEmpty;
    }

    /// <summary>Explicit locale-identity inline source for a schema-v2 localized value.</summary>
    [Serializable]
    public sealed class LocalizedInlineSource
    {
        [SerializeField] private List<LocalizedInlineValue> values = new();

        public IReadOnlyList<LocalizedInlineValue> Values => values;

        public string GetValue(string localeCode)
        {
            if (string.IsNullOrWhiteSpace(localeCode) || values == null)
                return string.Empty;
            return values.FirstOrDefault(candidate => string.Equals(
                candidate?.LocaleCode,
                localeCode,
                StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        }

        public void EnsureLocale(string localeCode)
        {
            if (string.IsNullOrWhiteSpace(localeCode))
                return;
            values ??= new List<LocalizedInlineValue>();
            if (values.All(candidate => !string.Equals(
                    candidate?.LocaleCode,
                    localeCode,
                    StringComparison.OrdinalIgnoreCase)))
                values.Add(new LocalizedInlineValue(localeCode, string.Empty));
        }

        public void SetValue(string localeCode, string value)
        {
            if (string.IsNullOrWhiteSpace(localeCode))
                throw new ArgumentException("A locale code is required.", nameof(localeCode));
            values ??= new List<LocalizedInlineValue>();
            var existing = values.FirstOrDefault(candidate => string.Equals(
                candidate?.LocaleCode,
                localeCode,
                StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                values.Add(new LocalizedInlineValue(localeCode, value));
            else
                existing.Value = value;
        }

        internal void Replace(IEnumerable<LocalizedInlineValue> replacement)
        {
            values = replacement?
                .Where(value => value != null)
                .Select(value => new LocalizedInlineValue(value.LocaleCode, value.Value))
                .ToList() ?? new List<LocalizedInlineValue>();
        }
    }

    /// <summary>
    /// Versioned localized string value with an explicit catalog or inline source.
    /// </summary>
    /// <remarks>
    /// The legacy serialized fields intentionally remain in this base type so
    /// <see cref="DynamicLocalization"/> assets load without data loss until an explicit migration
    /// copies them into <see cref="CatalogSource"/> or <see cref="InlineSource"/>.
    /// </remarks>
    [Serializable]
    public class LocalizedValue
    {
        public const int CurrentSchemaVersion = 2;

        [Header("Localization Settings")]
        public bool disabled;

        [SerializeField] private int schemaVersion;
        [SerializeField] private LocalizedValueSourceKind sourceKind;
        [SerializeField] private LocalizedCatalogSource catalogSource = new();
        [SerializeField] private LocalizedInlineSource inlineSource = new();

        // v1 compatibility payload. Migration clears these only after the v2 source is populated.
        [SerializeField, HideInInspector] public bool useLocalizedString;
        [SerializeField, HideInInspector] private LocalizedString localizedString;
        [SerializeField, HideInInspector] private List<DynamicLocalizationEntry> translations = new();

        private string _cachedString = string.Empty;
        private bool _hasCachedString;
        private string _legacyKey = string.Empty;
        private int _refreshGeneration;

        public LocalizedValue()
        {
            schemaVersion = CurrentSchemaVersion;
            sourceKind = LocalizedValueSourceKind.Inline;
        }

        public event Action<string> ValueChanged;

        public int SchemaVersion => schemaVersion;
        public LocalizedValueSourceKind SourceKind =>
            UsesExplicitSource ? sourceKind : EffectiveLegacyKind;
        public LocalizedCatalogSource CatalogSource => catalogSource ??= new LocalizedCatalogSource();
        public LocalizedInlineSource InlineSource => inlineSource ??= new LocalizedInlineSource();

        /// <summary>Whether serialized v1 fields still need an explicit migration transaction.</summary>
        public bool LegacyMigrationRequired =>
            schemaVersion < CurrentSchemaVersion ||
            useLocalizedString ||
            (localizedString != null && !localizedString.IsEmpty) ||
            (translations != null && translations.Count > 0);

        private bool UsesExplicitSource => !LegacyMigrationRequired;
        private LocalizedValueSourceKind EffectiveLegacyKind =>
            useLocalizedString
                ? LocalizedValueSourceKind.Catalog
                : LocalizedValueSourceKind.Inline;

        /// <summary>Returns the effective catalog reference, including the v1 adapter.</summary>
        public LocalizedString locale =>
            SourceKind == LocalizedValueSourceKind.Catalog
                ? UsesExplicitSource
                    ? CatalogSource.Reference
                    : localizedString
                : null;

        /// <summary>Returns the last successful value, then the authored default inline value.</summary>
        public string String => disabled
            ? string.Empty
            : _hasCachedString
                ? _cachedString
                : GetFirstInlineValue();

        /// <summary>Switches this value to an explicit catalog source.</summary>
        public void UseCatalog(LocalizedString reference)
        {
            schemaVersion = CurrentSchemaVersion;
            sourceKind = LocalizedValueSourceKind.Catalog;
            CatalogSource.Reference = reference;
            ClearLegacyPayload();
            if (Application.isPlaying)
                RefreshCachedString();
        }

        /// <summary>Switches this value to an explicit inline source.</summary>
        public void UseInline(IEnumerable<LocalizedInlineValue> values = null)
        {
            schemaVersion = CurrentSchemaVersion;
            sourceKind = LocalizedValueSourceKind.Inline;
            InlineSource.Replace(values);
            ClearLegacyPayload();
            if (Application.isPlaying)
                RefreshCachedString();
        }

        /// <summary>
        /// Copies the retained v1 payload into the explicit v2 source and clears the old payload.
        /// </summary>
        /// <returns>True when migration changed the value.</returns>
        public bool MigrateLegacySource()
        {
            if (!LegacyMigrationRequired)
                return false;

            if (useLocalizedString)
            {
                var reference = localizedString ?? new LocalizedString();
                sourceKind = LocalizedValueSourceKind.Catalog;
                CatalogSource.Reference = reference;
            }
            else
            {
                sourceKind = LocalizedValueSourceKind.Inline;
                InlineSource.Replace((translations ?? new List<DynamicLocalizationEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => new LocalizedInlineValue(entry.languageCode, entry.text)));
            }

            schemaVersion = CurrentSchemaVersion;
            ClearLegacyPayload();
            return true;
        }

        public void EnsureLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return;
            if (UsesExplicitSource)
            {
                if (sourceKind == LocalizedValueSourceKind.Inline)
                    InlineSource.EnsureLocale(languageCode);
                return;
            }

            translations ??= new List<DynamicLocalizationEntry>();
            if (translations.All(entry => !string.Equals(
                    entry?.languageCode,
                    languageCode,
                    StringComparison.OrdinalIgnoreCase)))
                translations.Add(new DynamicLocalizationEntry(languageCode, string.Empty));
        }

        public string GetTextForLanguage(string languageCode)
        {
            if (SourceKind != LocalizedValueSourceKind.Inline)
                return string.Empty;
            if (UsesExplicitSource)
                return InlineSource.GetValue(languageCode);
            if (translations == null || string.IsNullOrWhiteSpace(languageCode))
                return string.Empty;
            return translations.FirstOrDefault(entry => string.Equals(
                entry?.languageCode,
                languageCode,
                StringComparison.OrdinalIgnoreCase))?.text ?? string.Empty;
        }

        public void SetTextForLanguage(string text, string languageCode = null)
        {
            if (SourceKind == LocalizedValueSourceKind.Catalog)
            {
                Debug.LogWarning(
                    "Cannot set inline text for a catalog LocalizedValue. Edit its StringTable instead.");
                return;
            }
            if (string.IsNullOrWhiteSpace(languageCode) && Application.isPlaying)
                languageCode = LocalizationManager.CurrentLanguage;
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                Debug.LogWarning(
                    "LocalizedValue.SetTextForLanguage: no language code could be resolved; " +
                    "pass an explicit locale outside play mode.");
                return;
            }

            if (UsesExplicitSource)
            {
                InlineSource.SetValue(languageCode, text);
            }
            else
            {
                translations ??= new List<DynamicLocalizationEntry>();
                var entry = translations.FirstOrDefault(candidate => string.Equals(
                    candidate?.languageCode,
                    languageCode,
                    StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    translations.Add(new DynamicLocalizationEntry(languageCode, text));
                else
                    entry.text = text;
            }

            if (Application.isPlaying && string.Equals(
                    languageCode,
                    LocalizationManager.CurrentLanguage,
                    StringComparison.OrdinalIgnoreCase))
                SetCachedString(text);
        }

        public async Awaitable<string> GetLocalizedString()
        {
            return await GetLocalizedString(null);
        }

        /// <summary>Resolves with optional explicit Smart String arguments.</summary>
        public async Awaitable<string> GetLocalizedString(IList<object> arguments)
        {
            if (disabled)
                return string.Empty;
            await RuntimeManager.WaitForInitialization();
            var resolved = await ResolveLocalizedStringAsync(arguments);
            SetCachedString(resolved);
            return resolved;
        }

        public async Awaitable InitAsync(string key, bool forceUpdate = false)
        {
            if (disabled)
                return;
            await RuntimeManager.WaitForInitialization();
            if (!forceUpdate && string.Equals(_legacyKey, key, StringComparison.Ordinal))
                return;

            _legacyKey = key ?? string.Empty;
            var resolved = await ResolveLocalizedStringAsync(null);
            SetCachedString(resolved);
            RuntimeManager.GetSubsystem<LocalizationManager>()?.RegisterLocalizedValue(this);
        }

        public async void Init(string key, bool forceUpdate = false) // doctor:ignore compatibility entry point owns exceptions
        {
            try
            {
                await InitAsync(key, forceUpdate);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to initialize localization for key '{key}': {exception.Message}");
            }
        }

        public async void RefreshCachedString() // doctor:ignore locale-change callback owns exceptions
        {
            if (disabled)
                return;
            var generation = ++_refreshGeneration;
            try
            {
                var resolved = await ResolveLocalizedStringAsync(null);
                if (generation == _refreshGeneration)
                    SetCachedString(resolved);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Failed to refresh localized string: {exception.Message}");
            }
        }

        public void SetLocalizedString(LocalizedString value)
        {
            if (SourceKind != LocalizedValueSourceKind.Catalog)
            {
                Debug.LogWarning("Cannot set a catalog reference while the source is Inline.");
                return;
            }
            if (UsesExplicitSource)
                CatalogSource.Reference = value;
            else
                localizedString = value;
            if (Application.isPlaying)
                RefreshCachedString();
        }

        public LocalizedValueBinding CreateBinding(IList<object> arguments = null) =>
            new(this, arguments);

        private async Awaitable<string> ResolveLocalizedStringAsync(IList<object> arguments)
        {
            if (disabled)
                return string.Empty;

            var reference = locale;
            if (SourceKind == LocalizedValueSourceKind.Catalog &&
                reference != null &&
                !reference.IsEmpty)
            {
                if (LocalizationManager.TryResolveOverlay(
                        reference,
                        LocalizationManager.CurrentLanguage,
                        arguments,
                        out var overlayValue,
                        out _))
                    return overlayValue;
                try
                {
                    var handle = arguments == null
                        ? reference.GetLocalizedStringAsync()
                        : reference.GetLocalizedStringAsync(arguments);
                    await RuntimeManager.AwaitHandle(handle);
                    if (handle.Status == AsyncOperationStatus.Succeeded &&
                        !string.IsNullOrEmpty(handle.Result))
                        return handle.Result;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"Failed to resolve the catalog LocalizedValue: {exception.Message}");
                }
            }

            foreach (var localeCode in LocalizationManager.GetFallbackChain(
                         LocalizationManager.CurrentLanguage))
            {
                var candidate = GetTextForLanguage(localeCode);
                if (!string.IsNullOrEmpty(candidate))
                    return candidate;
            }
            return GetFirstInlineValue();
        }

        private string GetFirstInlineValue()
        {
            if (SourceKind != LocalizedValueSourceKind.Inline)
                return _hasCachedString ? _cachedString : string.Empty;
            if (UsesExplicitSource)
                return InlineSource.Values.FirstOrDefault()?.Value ?? string.Empty;
            return translations != null && translations.Count > 0
                ? translations[0]?.text ?? string.Empty
                : _hasCachedString
                    ? _cachedString
                    : string.Empty;
        }

        private void ClearLegacyPayload()
        {
            useLocalizedString = false;
            localizedString = null;
            translations?.Clear();
        }

        private void SetCachedString(string value)
        {
            value ??= string.Empty;
            if (_hasCachedString && string.Equals(_cachedString, value, StringComparison.Ordinal))
                return;
            _cachedString = value;
            _hasCachedString = true;
            ValueChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Generation-safe reactive binding for a <see cref="LocalizedValue"/>.
    /// </summary>
    public sealed class LocalizedValueBinding : IDisposable
    {
        private LocalizedValue _source;
        private IList<object> _arguments;
        private int _generation;
        private bool _disposed;

        public LocalizedValueBinding(LocalizedValue source, IList<object> arguments = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _arguments = arguments;
            _source.ValueChanged += OnSourceValueChanged;
            LocalizationSettings.SelectedLocaleChanged += OnLocaleChanged;
        }

        public event Action<string> ValueChanged;
        public string LastSuccessfulResult { get; private set; } = string.Empty;
        public LocalizedValue Source => _source;
        public bool IsDisposed => _disposed;

        public void ReplaceSource(LocalizedValue source)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LocalizedValueBinding));
            var replacement = source ?? throw new ArgumentNullException(nameof(source));
            _source.ValueChanged -= OnSourceValueChanged;
            _source = replacement;
            _source.ValueChanged += OnSourceValueChanged;
            Refresh();
        }

        public void SetArguments(IList<object> arguments)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LocalizedValueBinding));
            _arguments = arguments;
            Refresh();
        }

        public async Awaitable<string> RefreshAsync()
        {
            if (_disposed)
                return LastSuccessfulResult;
            var generation = ++_generation;
            var resolved = await _source.GetLocalizedString(_arguments);
            if (_disposed || generation != _generation)
                return LastSuccessfulResult;
            LastSuccessfulResult = resolved ?? string.Empty;
            ValueChanged?.Invoke(LastSuccessfulResult);
            return LastSuccessfulResult;
        }

        public async void Refresh() // doctor:ignore event callback owns exceptions
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception exception)
            {
                if (!_disposed)
                    Debug.LogWarning($"LocalizedValue binding refresh failed: {exception.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _source.ValueChanged -= OnSourceValueChanged;
            LocalizationSettings.SelectedLocaleChanged -= OnLocaleChanged;
            ValueChanged = null;
        }

        private void OnLocaleChanged(Locale _) => Refresh();

        private void OnSourceValueChanged(string value)
        {
            if (_disposed)
                return;
            _generation++;
            LastSuccessfulResult = value ?? string.Empty;
            ValueChanged?.Invoke(LastSuccessfulResult);
        }
    }
}
