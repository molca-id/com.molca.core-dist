using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using Molca.Settings;

namespace Molca.Localization
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-localization.png")]
    [CreateAssetMenu(fileName = "Localization Setting", menuName = "Molca/Settings/Localization", order = 10)]
    public class LocalizationModule : SettingModule
    {
        /// <summary>
        /// Represents a language entry with code and flag sprite.
        /// </summary>
        [Serializable]
        public struct LanguageEntry
        {
            [UnityEngine.Serialization.FormerlySerializedAs("name")]
            public string Name;
            public string Code;
            public Sprite Flag;
            public LocalePresentationProfile PresentationProfile;
            public string[] FallbackCodes;
        }

        /// <summary>
        /// List of supported languages and their flag sprites.
        /// </summary>
        [SerializeField]
        public LanguageEntry[] Languages;
        [SerializeField] private LocalizationRemoteCatalogSettings remoteCatalog;

        /// <summary>Optional signed remote-catalog trust and activation policy.</summary>
        public LocalizationRemoteCatalogSettings RemoteCatalog => remoteCatalog;

        /// <summary>
        /// Array of language codes derived from <see cref="Languages"/>.
        /// </summary>
        /// <remarks>
        /// Derived fresh on every access. Caching was removed because the cache was never
        /// invalidated when <see cref="Languages"/> is edited in the inspector (editing a
        /// serialized array does not trigger a domain reload), which left drawers reading a
        /// stale, often-empty list and reporting "No languages configured." The arrays here are
        /// tiny and accessed only from editor drawers and per-language runtime loops, so
        /// recomputing per access is negligible.
        /// </remarks>
        public string[] LanguageCode
            => Languages != null ? Array.ConvertAll(Languages, x => x.Code) : Array.Empty<string>();

        /// <summary>Current active language code, stored in <see cref="LocalizationState"/>.</summary>
        public string ActiveLanguage => TypedState?.ActiveLanguage;

        /// <summary>The full <see cref="LanguageEntry"/> for the current active language.</summary>
        public LanguageEntry ActiveLanguageEntry
        {
            get
            {
                if (Languages == null) return default;
                var active = ActiveLanguage;
                foreach (var entry in Languages)
                    if (string.Equals(entry.Code, active, StringComparison.OrdinalIgnoreCase)) return entry;
                return default;
            }
        }

        private LocalizationState TypedState => (LocalizationState)State;

        /// <summary>
        /// Get the flag sprite for a given language code.
        /// </summary>
        public Sprite GetFlagForLanguage(string code)
        {
            if (Languages == null) return null;
            foreach (var entry in Languages)
                if (string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase)) return entry.Flag;
            return null;
        }

        /// <summary>Determines whether this module declares a language code.</summary>
        /// <param name="code">BCP-47 language code.</param>
        /// <returns><c>true</c> when a non-empty matching entry exists.</returns>
        public bool HasLanguage(string code)
        {
            if (Languages == null || string.IsNullOrWhiteSpace(code))
                return false;

            var canonicalCode = CanonicalizeLocaleCode(code);
            foreach (var entry in Languages)
                if (!string.IsNullOrWhiteSpace(entry.Code) &&
                    string.Equals(
                        CanonicalizeLocaleCode(entry.Code),
                        canonicalCode,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Returns the configured descriptor for a locale code.</summary>
        public bool TryGetLanguage(string code, out LanguageEntry entry)
        {
            var canonicalCode = CanonicalizeLocaleCode(code);
            if (Languages != null)
                foreach (var candidate in Languages)
                    if (string.Equals(
                            CanonicalizeLocaleCode(candidate.Code),
                            canonicalCode,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        entry = candidate;
                        return true;
                    }
            entry = default;
            return false;
        }

        public LocalePresentationProfile GetPresentationProfile(string code) =>
            TryGetLanguage(code, out var entry) ? entry.PresentationProfile : null;

        /// <summary>
        /// Traverses only authored fallback edges, then appends the project default for compatibility.
        /// No implicit two-letter parent fallback is invented.
        /// </summary>
        public IReadOnlyList<string> GetFallbackChain(string requestedCode)
        {
            var chain = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string code)
            {
                code = CanonicalizeLocaleCode(code);
                if (string.IsNullOrEmpty(code) || !visited.Add(code))
                    return;
                chain.Add(code);
                if (!TryGetLanguage(code, out var entry))
                    return;
                foreach (var fallback in entry.FallbackCodes ?? Array.Empty<string>())
                    Visit(fallback);
            }

            Visit(requestedCode);
            if (Languages != null && Languages.Length > 0)
                Visit(Languages[0].Code);
            return chain;
        }

        /// <summary>Validates authored fallback targets and cycles.</summary>
        public IReadOnlyList<string> ValidateFallbackGraph()
        {
            var errors = new List<string>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in Languages ?? Array.Empty<LanguageEntry>())
            {
                var code = CanonicalizeLocaleCode(language.Code);
                if (string.IsNullOrEmpty(code))
                    continue;
                foreach (var fallback in language.FallbackCodes ?? Array.Empty<string>())
                    if (!HasLanguage(fallback))
                        errors.Add($"Locale '{code}' falls back to unknown locale '{fallback}'.");
            }

            bool Visit(string code, Stack<string> path)
            {
                if (state.TryGetValue(code, out var current))
                {
                    if (current == 1)
                    {
                        errors.Add(
                            "Fallback cycle: " +
                            string.Join(" -> ", path.Reverse().Append(code)) + ".");
                        return false;
                    }
                    return current == 2;
                }

                state[code] = 1;
                path.Push(code);
                if (TryGetLanguage(code, out var entry))
                    foreach (var fallback in entry.FallbackCodes ?? Array.Empty<string>())
                        if (HasLanguage(fallback))
                            Visit(CanonicalizeLocaleCode(fallback), path);
                path.Pop();
                state[code] = 2;
                return true;
            }

            foreach (var language in Languages ?? Array.Empty<LanguageEntry>())
                Visit(CanonicalizeLocaleCode(language.Code), new Stack<string>());
            return errors.Distinct(StringComparer.Ordinal).ToArray();
        }

        public static string CanonicalizeLocaleCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;
            var normalized = code.Trim().Replace('_', '-');
            try
            {
                return CultureInfo.GetCultureInfo(normalized).Name;
            }
            catch (CultureNotFoundException)
            {
                return normalized;
            }
        }

        /// <summary>Sets the active language by index into <see cref="Languages"/>.</summary>
        public void SetLanguage(int index)
        {
            if (Languages == null || Languages.Length == 0) return;
            if (index < 0 || index >= Languages.Length)
            {
                Debug.LogWarning($"LocalizationModule: language index {index} is outside the configured range.");
                return;
            }
            SetLanguage(Languages[index].Code);
        }

        internal void SetLanguage(string code)
        {
            if (Languages == null || Languages.Length == 0) return;
            var resolvedCode = HasLanguage(code)
                ? Array.Find(Languages, entry =>
                    string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase)).Code
                : Languages[0].Code;
            if (string.Equals(TypedState.ActiveLanguage, resolvedCode, StringComparison.Ordinal))
                return;

            TypedState.ActiveLanguage = resolvedCode;
            SaveSettings();
        }

        public override SettingState CreateState() => new LocalizationState(this);

        public override void LoadSettings() => TypedState.Load(this);
        public override void SaveSettings() => TypedState.Save(this);
    }

    /// <summary>
    /// Mutable runtime state for <see cref="LocalizationModule"/>.
    /// </summary>
    public class LocalizationState : SettingState
    {
        public string ActiveLanguage;

        public LocalizationState(LocalizationModule module)
        {
            ActiveLanguage = module.Languages != null && module.Languages.Length > 0
                ? module.Languages[0].Code
                : string.Empty;
        }

        public override void Load(SettingModule owner)
        {
            ActiveLanguage = owner.LoadString(nameof(ActiveLanguage), ActiveLanguage);
        }

        public override void Save(SettingModule owner)
        {
            if (!string.IsNullOrEmpty(ActiveLanguage))
                owner.SaveString(nameof(ActiveLanguage), ActiveLanguage);
        }
    }
}
