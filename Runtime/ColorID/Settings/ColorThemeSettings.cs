using UnityEngine;
using Molca.Settings;

namespace Molca.ColorID
{
    /// <summary>When the active-variant choice is persisted across sessions.</summary>
    public enum ColorThemePersistencePolicy
    {
        /// <summary>Persist the choice; it survives a restart.</summary>
        Persist = 0,

        /// <summary>
        /// Never persist; every session starts on <see cref="ColorThemeSettings.DefaultVariantId"/>.
        /// </summary>
        /// <remarks>
        /// For kiosk or demo builds that must present identically on every launch.
        /// </remarks>
        SessionOnly = 1
    }

    /// <summary>
    /// The project's colour-theme configuration: which theme set is active, which variant is the
    /// authored default, and whether users may change it.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Settings/</c>.
    /// <b>Base class:</b> <see cref="SettingModule"/>.
    /// <b>Registration:</b> added to the project's <c>GlobalSettings</c> module list by Quick Setup or
    /// onboarding. <see cref="CreateState"/> returns the paired <see cref="ColorThemeState"/>, which
    /// <c>GlobalSettings</c> creates and owns.
    /// <para/>
    /// Authored defaults only — the fields here are never written at runtime. The mutable active
    /// variant lives on <see cref="ColorThemeState"/>.
    /// <para/>
    /// Installing this module is what switches a project from the legacy <see cref="ColorModule"/> path
    /// to V2. With it installed, the Runtime Manager prefab no longer needs to serialize palette
    /// references at all, which removes the whole class of package-GUID closure failure that broke
    /// fresh installs.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Color Theme Settings", menuName = "Molca/Settings/Color Theme Settings", order = 12)]
    public class ColorThemeSettings : SettingModule
    {
        [SerializeField] private ColorThemeSet _themeSet;
        [SerializeField] private string _defaultVariantId;
        [SerializeField] private bool _allowRuntimeSwitching = true;
        [SerializeField] private ColorThemePersistencePolicy _persistencePolicy =
            ColorThemePersistencePolicy.Persist;

        /// <summary>The theme set this project uses.</summary>
        public ColorThemeSet ThemeSet => _themeSet;

        /// <summary>
        /// The variant to activate when nothing valid is persisted.
        /// </summary>
        /// <remarks>
        /// Falls back to the theme set's first declared variant when left blank, so a set with variants
        /// is always activatable even if this was never filled in.
        /// </remarks>
        public string DefaultVariantId
        {
            get
            {
                if (!string.IsNullOrEmpty(_defaultVariantId)) return _defaultVariantId;
                if (_themeSet == null) return null;
                var ids = _themeSet.GetVariantIds();
                return ids.Length > 0 ? ids[0] : null;
            }
        }

        /// <summary>Whether the application may change variant at runtime.</summary>
        public bool AllowRuntimeSwitching => _allowRuntimeSwitching;

        /// <summary>Whether the active-variant choice survives a restart.</summary>
        public ColorThemePersistencePolicy PersistencePolicy => _persistencePolicy;

        /// <summary>The paired mutable runtime state, or <c>null</c> before bootstrap.</summary>
        public ColorThemeState TypedState => State as ColorThemeState;

        /// <inheritdoc/>
        public override SettingState CreateState() => new ColorThemeState();

        /// <inheritdoc/>
        public override void LoadSettings()
        {
            if (State == null) return;

            // SessionOnly must not read a previously persisted value, or a build that opted out of
            // persistence would still resume the last session's theme after an upgrade turned the
            // policy on and off again.
            if (_persistencePolicy == ColorThemePersistencePolicy.SessionOnly)
            {
                TypedState.ResetToAuthoredDefault(DefaultVariantId);
                return;
            }

            TypedState.Load(this);
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            if (State == null) return;
            if (_persistencePolicy == ColorThemePersistencePolicy.SessionOnly) return;
            TypedState.Save(this);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Clears the persisted variant preference and returns to the authored default. Never touches
        /// the theme set — that is asset data.
        /// </remarks>
        public override void ResetToDefaults()
        {
            if (State == null) return;

            PlayerPrefs.DeleteKey(FieldKey(ColorThemeState.ActiveVariantKey));
            PlayerPrefs.DeleteKey(FieldKey(ColorThemeState.SetIdKey));
            PlayerPrefs.DeleteKey(FieldKey(ColorThemeState.SchemaVersionKey));
            TypedState.ResetToAuthoredDefault(DefaultVariantId);
        }
    }

    /// <summary>
    /// Mutable runtime state for <see cref="ColorThemeSettings"/>: which variant is active, and the
    /// last one known to activate successfully.
    /// </summary>
    /// <remarks>
    /// A plain C# object, never serialized to the asset. Persistence is scoped by the theme set's
    /// <see cref="ColorThemeSet.StableSetId"/> and schema version, so a stored preference from a
    /// different or newer theme set is recognised and ignored instead of being applied to a set where
    /// that variant means something else. V1's equivalent keys derived from
    /// <c>typeof(ColorModule).FullName</c>, which meant every variant shared one key.
    /// </remarks>
    public class ColorThemeState : SettingState
    {
        /// <summary>PlayerPrefs sub-key holding the active variant ID.</summary>
        internal const string ActiveVariantKey = "activeVariantId";

        /// <summary>PlayerPrefs sub-key holding the theme set the preference belongs to.</summary>
        internal const string SetIdKey = "themeSetId";

        /// <summary>PlayerPrefs sub-key holding the schema version the preference was written under.</summary>
        internal const string SchemaVersionKey = "schemaVersion";

        /// <summary>The variant the user or application selected. May be invalid or stale.</summary>
        public string ActiveVariantId { get; set; }

        /// <summary>
        /// The last variant that activated successfully.
        /// </summary>
        /// <remarks>
        /// What "preserve the last known good theme" is built on. When an activation fails, this is
        /// the variant that remains live; the application keeps rendering the last coherent palette
        /// instead of losing its colours.
        /// </remarks>
        public string LastKnownGoodVariantId { get; set; }

        /// <inheritdoc/>
        public override void Load(SettingModule owner)
        {
            var settings = owner as ColorThemeSettings;
            string authoredDefault = settings?.DefaultVariantId;

            string persistedSetId = owner.LoadString(SetIdKey, string.Empty);
            int persistedSchema = owner.LoadInt(SchemaVersionKey, 0);
            string persistedVariant = owner.LoadString(ActiveVariantKey, string.Empty);

            string currentSetId = settings?.ThemeSet != null ? settings.ThemeSet.StableSetId : string.Empty;
            int currentSchema = settings?.ThemeSet != null
                ? settings.ThemeSet.SchemaVersion
                : ColorThemeSet.CurrentSchemaVersion;

            bool belongsToThisSet = !string.IsNullOrEmpty(persistedSetId)
                                    && persistedSetId == currentSetId;

            // A preference written by a newer schema may name a variant that no longer means the same
            // thing, so it is discarded rather than trusted.
            bool schemaIsReadable = persistedSchema <= currentSchema;

            ActiveVariantId = belongsToThisSet && schemaIsReadable && !string.IsNullOrEmpty(persistedVariant)
                ? persistedVariant
                : authoredDefault;

            // Not persisted: "known good" is only meaningful for activations observed this session.
            LastKnownGoodVariantId = null;
        }

        /// <inheritdoc/>
        public override void Save(SettingModule owner)
        {
            var settings = owner as ColorThemeSettings;
            if (settings?.ThemeSet == null) return;
            if (string.IsNullOrEmpty(ActiveVariantId)) return;

            owner.SaveString(ActiveVariantKey, ActiveVariantId);
            owner.SaveString(SetIdKey, settings.ThemeSet.StableSetId ?? string.Empty);
            owner.SaveInt(SchemaVersionKey, settings.ThemeSet.SchemaVersion);
            PlayerPrefs.Save();
        }

        /// <summary>Returns this state to the authored default, discarding the in-memory selection.</summary>
        /// <param name="defaultVariantId">The authored default variant ID.</param>
        public void ResetToAuthoredDefault(string defaultVariantId)
        {
            ActiveVariantId = defaultVariantId;
            LastKnownGoodVariantId = null;
        }
    }
}
