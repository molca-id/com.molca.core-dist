using System;
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
        }

        /// <summary>
        /// List of supported languages and their flag sprites.
        /// </summary>
        [SerializeField]
        public LanguageEntry[] Languages;

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
                    if (entry.Code == active) return entry;
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
                if (entry.Code == code) return entry.Flag;
            return null;
        }

        /// <summary>Sets the active language by index into <see cref="Languages"/>.</summary>
        public void SetLanguage(int index)
        {
            if (Languages == null || Languages.Length == 0) return;
            SetLanguage(Languages[index % Languages.Length].Code);
        }

        internal void SetLanguage(string code)
        {
            if (Languages == null || Languages.Length == 0) return;
            TypedState.ActiveLanguage = string.IsNullOrEmpty(code) ? Languages[0].Code : code;
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
