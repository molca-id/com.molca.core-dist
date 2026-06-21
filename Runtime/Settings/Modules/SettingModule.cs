using UnityEngine;

namespace Molca.Settings
{
    /// <summary>
    /// Base class for project setting modules. Each subclass is a
    /// <see cref="ScriptableObject"/> that holds <b>authored defaults only</b> —
    /// SerializeFields on this class must not be mutated at runtime.
    /// </summary>
    /// <remarks>
    /// Mutable runtime state belongs on a paired <see cref="SettingState"/> object,
    /// returned from <see cref="CreateState"/>. <see cref="GlobalSettings"/> creates and
    /// owns the state; the module reads/writes through the <see cref="State"/> property.
    /// Modules that have no runtime-mutable state (read-only configuration) may leave
    /// <see cref="CreateState"/> returning <c>null</c>.
    /// </remarks>
    public abstract class SettingModule : ScriptableObject
    {
        /// <summary>Fully qualified type name used to namespace persistence keys.</summary>
        public string SettingId { get; private set; }

        /// <summary>Persistence-key prefix derived from <see cref="SettingId"/>.</summary>
        public string ModuleKey { get; private set; }

        /// <summary>
        /// Runtime state owned by <see cref="GlobalSettings"/>. <c>null</c> if this module
        /// has no mutable state (i.e., <see cref="CreateState"/> returns <c>null</c>).
        /// Set internally by <see cref="GlobalSettings"/> during initialization.
        /// </summary>
        public SettingState State { get; internal set; }

        /// <summary>
        /// Called once by <see cref="GlobalSettings"/> during bootstrap, before
        /// <see cref="LoadSettings"/>. Establishes <see cref="SettingId"/> and
        /// <see cref="ModuleKey"/>. Subclasses overriding this must call <c>base.Initialize()</c>.
        /// </summary>
        public virtual void Initialize()
        {
            SettingId = GetType().FullName;
            ModuleKey = $"Setting.{SettingId}";
        }

        /// <summary>
        /// Factory for the paired <see cref="SettingState"/>. Override and return a
        /// new instance to opt into the state-based runtime mutation pattern.
        /// Default returns <c>null</c>, indicating this module has no runtime-mutable state.
        /// </summary>
        /// <remarks>
        /// Public (rather than protected) because Molca subsystems live in separate
        /// assemblies; <see cref="GlobalSettings"/> needs to invoke this during bootstrap.
        /// </remarks>
        public virtual SettingState CreateState() => null;

        /// <summary>Fully-qualified PlayerPrefs key for a named field on this module.</summary>
        public string FieldKey(string fieldName) => $"{ModuleKey}.{fieldName}";

        // PlayerPrefs convenience helpers. Public because paired SettingState classes
        // typically live in different assemblies and call these via the owner module.
        public void SaveFloat(string key, float value) => PlayerPrefs.SetFloat(FieldKey(key), value);
        public float LoadFloat(string key, float defaultValue = 0f) => PlayerPrefs.GetFloat(FieldKey(key), defaultValue);
        public void SaveString(string key, string value) => PlayerPrefs.SetString(FieldKey(key), value);
        public string LoadString(string key, string defaultValue = "") => PlayerPrefs.GetString(FieldKey(key), defaultValue);
        public void SaveInt(string key, int value) => PlayerPrefs.SetInt(FieldKey(key), value);
        public int LoadInt(string key, int defaultValue = 0) => PlayerPrefs.GetInt(FieldKey(key), defaultValue);

        /// <summary>
        /// Loads persisted values into <see cref="State"/> (or, for modules without state,
        /// performs any other one-time load logic). Called by
        /// <see cref="GlobalSettings.LoadAllSettings"/>; do not invoke directly.
        /// </summary>
        public abstract void LoadSettings();

        /// <summary>
        /// Persists current values from <see cref="State"/> to <see cref="PlayerPrefs"/>
        /// or an alternative backing store. Called by <see cref="GlobalSettings.SaveAllSettings"/>
        /// and may be invoked from module property setters that want immediate write-through.
        /// </summary>
        public abstract void SaveSettings();

        /// <summary>
        /// Resets persisted values to authored defaults. Default implementation simply
        /// re-runs <see cref="LoadSettings"/>; override for explicit default-restoration
        /// semantics.
        /// </summary>
        public virtual void ResetToDefaults() => LoadSettings();
    }
}
