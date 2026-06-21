namespace Molca.Settings
{
    /// <summary>
    /// Plain C# object that holds the mutable runtime state for a paired
    /// <see cref="SettingModule"/>. Owned by <see cref="GlobalSettings"/> and never
    /// serialized to disk via Unity — persistence happens through
    /// <see cref="Load"/>/<see cref="Save"/>, typically backed by <see cref="UnityEngine.PlayerPrefs"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="SettingModule"/> ScriptableObject holds authored defaults only and
    /// is read-only at runtime. Any value that changes during play (volumes, UI scale,
    /// active language, etc.) belongs on the paired <see cref="SettingState"/>.
    /// <para>
    /// Subclass this and override <see cref="SettingModule.CreateState"/> on the paired
    /// module to opt in. Modules that have no runtime-mutable state may leave
    /// <see cref="SettingModule.CreateState"/> returning <c>null</c>.
    /// </para>
    /// <para>
    /// Lifecycle: <see cref="GlobalSettings"/> calls <see cref="Load"/> on each state
    /// during bootstrap (after <see cref="SettingModule.Initialize"/>) and <see cref="Save"/>
    /// on shutdown. Modules may also call <see cref="Save"/> from their property setters
    /// when an immediate write-through is desired.
    /// </para>
    /// </remarks>
    public abstract class SettingState
    {
        /// <summary>
        /// Loads persisted values into this state from <see cref="UnityEngine.PlayerPrefs"/>
        /// or another backing store, falling back to defaults from the paired module.
        /// </summary>
        /// <param name="owner">
        /// The paired <see cref="SettingModule"/>, providing access to authored defaults
        /// and the module's <c>FieldKey</c> namespace via the protected helpers.
        /// </param>
        public abstract void Load(SettingModule owner);

        /// <summary>
        /// Persists this state's values to <see cref="UnityEngine.PlayerPrefs"/> or another
        /// backing store under the paired module's namespace.
        /// </summary>
        /// <param name="owner">The paired <see cref="SettingModule"/>.</param>
        public abstract void Save(SettingModule owner);
    }
}
