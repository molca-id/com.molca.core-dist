using Molca.Attributes;
using Molca.Settings;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca
{
    /// <summary>
    /// Owns the project's <see cref="SettingModule"/> assets and drives their load/save lifecycle.
    /// </summary>
    /// <remarks>
    /// Initialized eagerly by <see cref="RuntimeManager"/> during bootstrap, before any
    /// <see cref="RuntimeSubsystem"/>. Access modules through the static <see cref="GetModule{T}"/>;
    /// <c>GlobalSettings</c> is not a registered DI service, so it is never <c>[Inject]</c>ed.
    /// <para>
    /// This is a ScriptableObject asset shared across every play session in the editor. Nothing
    /// authored on it may be mutated at runtime, and any runtime subscriber it accumulates must be
    /// dropped in <see cref="DeInitialize"/> — otherwise last session's destroyed listeners are
    /// still invoked in the next one.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Global Settings", menuName = "Molca/Core/Global Settings", order = 0)]
    public class GlobalSettings : ScriptableObject
    {
        /// <summary>
        /// The project's <c>GlobalSettings</c> asset, or <c>null</c> when the project is not configured.
        /// </summary>
        /// <remarks>
        /// Null-safe by contract. This used to dereference <see cref="MolcaProjectSettings.Instance"/>
        /// unconditionally, so it threw a <see cref="NullReferenceException"/> in exactly the situation
        /// callers were null-checking it for — a settings asset that failed to load at runtime — which
        /// made every downstream <c>main == null</c> guard unreachable.
        /// </remarks>
        public static GlobalSettings main
        {
            get
            {
                var projectSettings = MolcaProjectSettings.Instance;
                return projectSettings == null ? null : projectSettings.GlobalSettings;
            }
        }

        /// <summary>The registered setting modules, authored in the Inspector.</summary>
        /// <remarks>
        /// A public field with a non-conventional name, retained deliberately: it is the serialized
        /// key in every existing <c>GlobalSettings.asset</c> and is read (and written) by editor
        /// tooling and SDK forks. Renaming it would strand authored data and break consumers for a
        /// cosmetic gain. Treat it as read-only at runtime — use <see cref="GetModule{T}"/>.
        /// </remarks>
        public SettingModule[] modules;

        private Dictionary<Type, SettingModule> _moduleCache;

        /// <summary>
        /// Raised when <see cref="SetQuality"/> applies a new <see cref="QualitySettings"/> level.
        /// </summary>
        /// <remarks>
        /// Cleared by <see cref="DeInitialize"/>. Subscribers are not carried across play sessions —
        /// see the type-level remark on shared asset state.
        /// </remarks>
        public Action<int> onQualityChanged;

        // Namespaced to match SettingModule.FieldKey ("Setting.{FullName}.{field}"). The bare
        // "QUALITY" key below is the pre-namespacing spelling, still read once so an existing
        // player's chosen quality level survives the upgrade.
        private const string PREF_QUALITY = "Molca.Settings.QualityLevel";
        private const string PREF_QUALITY_LEGACY = "QUALITY";

        /// <summary>
        /// Prepares every registered module: establishes its persistence keys and creates its
        /// paired <see cref="SettingState"/>. Called by <see cref="RuntimeManager"/> during bootstrap,
        /// before <see cref="LoadAllSettings"/>.
        /// </summary>
        public void Initialize()
        {
            _moduleCache = new Dictionary<Type, SettingModule>();

            // Initialize all modules
            foreach (var module in modules ?? Array.Empty<SettingModule>())
            {
                if (module == null) continue;

                var moduleType = module.GetType();
                if (_moduleCache.ContainsKey(moduleType))
                {
                    // Matches how RuntimeManager reports duplicate subsystem types. Silently
                    // overwriting made which asset wins depend on list order, with no signal.
                    Debug.LogWarning(
                        $"[GlobalSettings] Multiple '{moduleType.Name}' modules are registered. " +
                        "Keeping the first and ignoring the rest — each SettingModule type must " +
                        "appear at most once.", this);
                    continue;
                }

                module.Initialize();
                module.State = module.CreateState();
                _moduleCache[moduleType] = module;
            }

            // Deferred a frame (historical behavior); explicit fire-and-forget per
            // the async contract — the callee owns its exceptions.
            _ = ApplyPersistedQualityAsync();
        }

        private async Awaitable ApplyPersistedQualityAsync()
        {
            try
            {
                await Awaitable.NextFrameAsync();

                if (PlayerPrefs.HasKey(PREF_QUALITY))
                    QualitySettings.SetQualityLevel(PlayerPrefs.GetInt(PREF_QUALITY, 2));
                else if (PlayerPrefs.HasKey(PREF_QUALITY_LEGACY))
                    QualitySettings.SetQualityLevel(PlayerPrefs.GetInt(PREF_QUALITY_LEGACY, 2));
            }
            catch (Exception e)
            {
                Debug.LogError($"[GlobalSettings] Failed to apply persisted quality level: {e}");
            }
        }

        /// <summary>
        /// Releases all per-session state: the module lookup, each module's
        /// <see cref="SettingModule.State"/>, and this object's own subscribers.
        /// Called by <see cref="RuntimeManager"/> during shutdown, after <see cref="SaveAllSettings"/>.
        /// </summary>
        /// <remarks>
        /// Clearing per-module state is not optional bookkeeping. This asset outlives the play
        /// session, so a <see cref="SettingState"/> left attached is silently reused by the next
        /// one — modules would write into the previous session's state instead of hitting their
        /// own "not initialized yet" guards.
        /// </remarks>
        public void DeInitialize()
        {
            foreach (var module in modules ?? Array.Empty<SettingModule>())
            {
                if (module != null)
                    module.DeInitialize();
            }

            onQualityChanged = null;
            _moduleCache?.Clear();
            _moduleCache = null;
        }

        /// <summary>
        /// Resolves a registered setting module by type.
        /// </summary>
        /// <typeparam name="T">The module type, or any base type/interface it satisfies.</typeparam>
        /// <returns>The registered module, or <c>null</c> if none matches or the project is unconfigured.</returns>
        /// <remarks>
        /// Assignability, not exact type identity, decides a match — a fork's specialization of a
        /// module still satisfies a query for the base type, matching how <c>[DependsOn]</c> and the
        /// editor's bootstrap check already resolve module requirements. The cached path previously
        /// keyed on the exact concrete type while the uncached fallback used <c>is T</c>, so the same
        /// base-type query answered differently before and after <see cref="Initialize"/>.
        /// </remarks>
        public static T GetModule<T>() where T : SettingModule
        {
            // Guard against an unconfigured project: main is null when no GlobalSettings is
            // assigned, and modules is null before Initialize() runs.
            var settings = main;
            if (settings == null)
                return null;

            if (settings._moduleCache != null)
            {
                // Exact hit first — the common case, and a dictionary lookup.
                if (settings._moduleCache.TryGetValue(typeof(T), out var module))
                    return (T)module;

                foreach (var cached in settings._moduleCache.Values)
                {
                    if (cached is T typedModule)
                        return typedModule;
                }

                return null;
            }

            foreach (var module in settings.modules ?? Array.Empty<SettingModule>())
            {
                if (module is T typedModule)
                    return typedModule;
            }

            return null;
        }

        /// <summary>Persists every registered module and flushes <see cref="PlayerPrefs"/> to disk.</summary>
        public void SaveAllSettings()
        {
            Debug.Log("Saving all settings");
            foreach (var module in modules ?? Array.Empty<SettingModule>())
            {
                if (module != null)
                    module.SaveSettings();
            }
            PlayerPrefs.Save();
            Debug.Log("Settings saved");
        }

        /// <summary>
        /// Loads persisted values into every registered module. Called by <see cref="RuntimeManager"/>
        /// during bootstrap, immediately after <see cref="Initialize"/>.
        /// </summary>
        public void LoadAllSettings()
        {
            foreach (var module in modules ?? Array.Empty<SettingModule>())
            {
                if (module != null)
                    module.LoadSettings();
            }
        }

        /// <summary>The active <see cref="QualitySettings"/> level.</summary>
        public static int Quality => QualitySettings.GetQualityLevel();

        /// <summary>
        /// Applies and persists the <see cref="QualitySettings"/> level after a short
        /// settle delay (lets a settings UI finish its transition first).
        /// </summary>
        /// <param name="value">The quality level to apply.</param>
        /// <param name="cancellationToken">Cancels the settle delay; when cancelled, the
        /// level is not applied and <see cref="onQualityChanged"/> does not fire. Cancellation
        /// surfaces as <see cref="OperationCanceledException"/>.</param>
        /// <remarks>
        /// The notification deliberately fires <em>after</em> the cancellation point. Raising it up
        /// front told listeners about a change that a cancelled call then never made.
        /// </remarks>
        public static async Awaitable SetQuality(int value, System.Threading.CancellationToken cancellationToken = default)
        {
            await Awaitable.WaitForSecondsAsync(.1f, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            QualitySettings.SetQualityLevel(value);
            PlayerPrefs.SetInt(PREF_QUALITY, value);
            // Flush now rather than relying on shutdown: a crash or a force-quit between here and
            // SaveAllSettings would otherwise silently discard the choice.
            PlayerPrefs.Save();

            main?.onQualityChanged?.Invoke(value);
        }

        #if UNITY_EDITOR
        /// <summary>
        /// Returns the project's <c>GlobalSettings</c> asset, creating and assigning one if the
        /// project has none. Editor-only.
        /// </summary>
        /// <returns>The live asset, or <c>null</c> if <see cref="MolcaProjectSettings"/> is unavailable.</returns>
        public static GlobalSettings GetOrCreateSettings()
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
            {
                Debug.LogError("[GlobalSettings] MolcaProjectSettings is unavailable; cannot create GlobalSettings.");
                return null;
            }

            var settings = projectSettings.GlobalSettings;
            if (settings == null)
            {
                settings = CreateInstance<GlobalSettings>();
                if (!System.IO.Directory.Exists("Assets/_Molca/Resources"))
                    System.IO.Directory.CreateDirectory("Assets/_Molca/Resources");
                UnityEditor.AssetDatabase.CreateAsset(settings, "Assets/_Molca/Resources/GlobalSettings.asset");
                UnityEditor.AssetDatabase.SaveAssets();
                projectSettings.GlobalSettings = settings;
            }
            return settings;
        }
        #endif
    }
}
