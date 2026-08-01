using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.ColorID
{
    /// <summary>
    /// Runtime subsystem that owns the active colour theme and variant switching.
    /// Resolve via the <see cref="IColorThemeService"/> instance API
    /// (<c>RuntimeManager.GetService&lt;IColorThemeService&gt;()</c> or <c>[Inject]</c>); the legacy
    /// <see cref="IColorSchemeService"/> remains available for the compatibility window.
    /// </summary>
    /// <remarks>
    /// This one subsystem serves <b>two generations</b> of the colour API, deliberately, rather than a
    /// V2 manager being introduced beside it. A second manager would mean two things could each believe
    /// they owned the active theme, and no way to say which was right.
    /// <para/>
    /// Which generation is live depends purely on configuration:
    /// <list type="bullet">
    /// <item><description>
    /// <b>V2</b> — the project's <c>GlobalSettings</c> contains a <see cref="ColorThemeSettings"/> module
    /// with a theme set. Variants resolve through <see cref="ColorThemeResolver"/> into an immutable
    /// snapshot, and legacy <c>(swatch, colorId)</c> lookups are translated by
    /// <see cref="LegacyColorProviderAdapter"/>. No <see cref="ColorModule"/> is involved, so the
    /// Runtime Manager prefab need not serialize palette references at all.
    /// </description></item>
    /// <item><description>
    /// <b>Legacy</b> — no theme settings module. The serialized <see cref="ColorModule"/> array behaves
    /// exactly as before.
    /// </description></item>
    /// </list>
    /// In V2 the legacy <see cref="IColorSchemeService"/> members are mapped onto variants, so existing
    /// content — including the shipped Color Scheme Dropdown prefab and all 194
    /// <see cref="ColorID"/> components — keeps working against the new data with no changes.
    /// </remarks>
    public class ColorSchemeManager : RuntimeSubsystem, IColorSchemeService, IColorThemeService
    {
        private const string PREF_ACTIVE_SCHEME = "ColorScheme_Active";

        [Header("Color Schemes")]
        [SerializeField, FormerlySerializedAs("availableSchemes")] private ColorModule[] _availableSchemes;
        [SerializeField, FormerlySerializedAs("defaultSchemeIndex")] private int _defaultSchemeIndex = 0;

        private int _activeSchemeIndex = -1;

        /// <summary>
        /// The schemes this subsystem actually serves: <see cref="_availableSchemes"/> with
        /// unresolved entries removed, or discovered from <c>GlobalSettings</c> when the serialized
        /// wiring yields nothing usable. Never contains nulls once initialization has run.
        /// </summary>
        /// <remarks>
        /// Kept separate from the serialized field so the subsystem can degrade gracefully without
        /// rewriting authored prefab data.
        /// </remarks>
        private ColorModule[] _resolvedSchemes = Array.Empty<ColorModule>();

        // Instance event — the IColorSchemeService API.
        private event Action<ColorModule> _schemeChanged;

        // ---- V2 theme state -------------------------------------------------------------------
        // All four move together on every publish and are only ever replaced, never mutated in
        // place, so a caller holding a snapshot keeps reading a coherent variant.
        private ColorThemeSettings _themeSettings;
        private ResolvedColorTheme _activeTheme;
        private LegacyColorProviderAdapter _legacyAdapter;
        private int _generation;

        private event Action<ColorThemeChanged> _themeChanged;

        /// <summary>Whether this project is configured for the V2 theme model.</summary>
        private bool IsV2 => _themeSettings != null && _themeSettings.ThemeSet != null;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            // GlobalSettings has already initialized its modules and states by the time subsystems
            // run, so the theme settings module and its state are both available here.
            _themeSettings = GlobalSettings.GetModule<ColorThemeSettings>();

            if (IsV2)
            {
                InitializeV2();
                finishCallback?.Invoke(this);
                return;
            }

            InitializeLegacy();
            finishCallback?.Invoke(this);
        }

        /// <summary>
        /// Activates the persisted variant, or the authored default, or the emergency fallback.
        /// </summary>
        /// <remarks>
        /// The ladder matters. A persisted preference can name a variant that an update removed, so
        /// falling back to the authored default keeps an updated build usable. If the authored default
        /// is also unusable the theme set itself is broken, and the emergency fallback keeps the
        /// application legible enough to show the error — while reporting a degraded state rather than
        /// claiming health, which is precisely what V1 got wrong.
        /// </remarks>
        private void InitializeV2()
        {
            var state = _themeSettings.TypedState;
            string requested = state?.ActiveVariantId ?? _themeSettings.DefaultVariantId;
            string authoredDefault = _themeSettings.DefaultVariantId;

            var result = Activate(requested, save: false, ColorThemeChangeReason.Initialized);

            if (!result.Published && !string.Equals(requested, authoredDefault, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[ColorTheme] Variant '{requested}' could not be activated "
                                 + $"({result.Outcome}); falling back to the authored default "
                                 + $"'{authoredDefault}'.");
                result = Activate(authoredDefault, save: false, ColorThemeChangeReason.Initialized);
            }

            if (result.Published) return;

            Debug.LogError($"[ColorTheme] No variant of theme set "
                           + $"'{_themeSettings.ThemeSet.DisplayName}' could be activated. The theme is "
                           + $"running in a degraded emergency fallback with 5 neutral tokens; most "
                           + $"colours will be missing.\n{result}");

            PublishTheme(ResolvedColorTheme.CreateEmergencyFallback(++_generation), null,
                ColorThemeChangeReason.Initialized);
        }

        private void InitializeLegacy()
        {
            _resolvedSchemes = ResolveSchemes();

            if (_resolvedSchemes.Length == 0)
            {
                Debug.LogWarning("ColorSchemeManager: No color schemes configured. Using default ColorModule from GlobalSettings.");
                return;
            }

            // Load saved preference or use default
            int savedIndex = PlayerPrefs.GetInt(PREF_ACTIVE_SCHEME, _defaultSchemeIndex);
            savedIndex = Mathf.Clamp(savedIndex, 0, _resolvedSchemes.Length - 1);

            // Set the initial scheme without triggering refresh (ColorID components aren't ready yet)
            SetSchemeInternal(savedIndex, notifyListeners: false);
        }

        /// <summary>
        /// Builds the usable scheme list from the serialized references, recovering from a prefab
        /// whose palette references do not resolve in this project.
        /// </summary>
        /// <returns>A dense array of non-null schemes; empty when none could be found.</returns>
        /// <remarks>
        /// This exists because the shipped Runtime Manager prefab serializes palette assets by
        /// GUID. If those GUIDs are not present in the consumer project — the fresh-install case,
        /// where the Quick Setup templates were installed under different GUIDs — Unity
        /// deserializes the array at full length with every element null. V1 then took the
        /// "no schemes configured" branch only when the array was empty, so a null-filled array
        /// fell through to <see cref="SetSchemeInternal"/>, logged one null-scheme error, and left
        /// the subsystem reporting success with no active scheme and scheme switching dead.
        /// <para/>
        /// Recovery is deliberately loud rather than silent: a project in this state is
        /// misconfigured and the packaging fault must be fixed, not papered over.
        /// </remarks>
        private ColorModule[] ResolveSchemes()
        {
            var resolved = new List<ColorModule>();
            int unresolvedCount = 0;

            if (_availableSchemes != null)
            {
                foreach (var scheme in _availableSchemes)
                {
                    if (scheme != null)
                        resolved.Add(scheme);
                    else
                        unresolvedCount++;
                }
            }

            if (unresolvedCount > 0)
            {
                Debug.LogError(
                    $"ColorSchemeManager: {unresolvedCount} of {_availableSchemes.Length} configured " +
                    "color schemes could not be resolved — the serialized palette assets are missing " +
                    "from this project. This usually means the Runtime Manager prefab references " +
                    "palette GUIDs that this project does not contain. Re-assign 'Available Schemes' " +
                    "on the Runtime Manager to the palettes under Assets/_MolcaSDK/Settings/.");
            }

            if (resolved.Count > 0)
                return resolved.ToArray();

            // Last resort: serve whatever palettes the settings graph owns, so a project with a
            // broken prefab reference still gets working theme switching.
            var settings = GlobalSettings.main;
            if (settings?.modules != null)
            {
                foreach (var module in settings.modules)
                {
                    if (module is ColorModule colorModule)
                        resolved.Add(colorModule);
                }

                if (resolved.Count > 0)
                {
                    Debug.LogWarning(
                        $"ColorSchemeManager: recovered {resolved.Count} color scheme(s) from " +
                        "GlobalSettings because none of the serialized references resolved.");
                }
            }

            return resolved.ToArray();
        }

        public override void Shutdown()
        {
            // The static legacy-provider override outlives this subsystem when domain reload is
            // disabled, so a stale adapter pointing at a dead snapshot would keep answering lookups
            // in the next play session. Clear it before anything else.
            ColorModule.RuntimeProviderOverride = null;
            _themeChanged = null;
            _schemeChanged = null;
            _activeTheme = null;
            _legacyAdapter = null;

            base.Shutdown();
        }

        #region V2 activation

        /// <summary>
        /// Resolves and publishes a variant, leaving the current theme untouched on failure.
        /// </summary>
        /// <param name="variantId">The variant to activate.</param>
        /// <param name="save">Whether to persist the choice.</param>
        /// <param name="reason">Why the change is happening, for the change payload.</param>
        /// <returns>The typed outcome.</returns>
        /// <remarks>
        /// The full snapshot is built <i>before</i> anything is published, so a failed activation
        /// cannot leave a partially-updated table behind — there is nothing to roll back. That is what
        /// makes "failed activation preserves the last known good theme" a structural property rather
        /// than a promise.
        /// </remarks>
        private ColorThemeActivationResult Activate(string variantId, bool save,
            ColorThemeChangeReason reason)
        {
            if (!IsV2)
            {
                return new ColorThemeActivationResult(ColorThemeActivation.SettingsUnavailable, null,
                    new[] { "No ColorThemeSettings module with a theme set is installed." });
            }

            if (string.IsNullOrEmpty(variantId))
            {
                return new ColorThemeActivationResult(ColorThemeActivation.UnknownVariant,
                    _activeTheme?.VariantId, new[] { "No variant ID was supplied." });
            }

            if (_activeTheme != null
                && string.Equals(_activeTheme.VariantId, variantId, StringComparison.OrdinalIgnoreCase))
            {
                return new ColorThemeActivationResult(ColorThemeActivation.AlreadyActive,
                    _activeTheme.VariantId);
            }

            var outcome = ColorThemeResolver.TryResolve(_themeSettings.ThemeSet, variantId,
                _generation + 1, out var theme, out var diagnostics);

            if (outcome != ColorThemeActivation.Activated)
            {
                // _activeTheme is deliberately not touched here.
                return new ColorThemeActivationResult(outcome, _activeTheme?.VariantId, diagnostics);
            }

            _generation++;
            string previous = _activeTheme?.VariantId;
            PublishTheme(theme, previous, reason);

            var state = _themeSettings.TypedState;
            if (state != null)
            {
                state.ActiveVariantId = theme.VariantId;
                state.LastKnownGoodVariantId = theme.VariantId;
            }

            if (save)
            {
                try
                {
                    _themeSettings.SaveSettings();
                }
                catch (Exception exception)
                {
                    // The variant *is* active; only the preference failed to stick. Reporting this as
                    // an activation failure would be wrong and would make callers roll back a
                    // perfectly good theme.
                    Debug.LogWarning($"[ColorTheme] Variant '{theme.VariantId}' activated but the "
                                     + $"preference could not be saved: {exception.Message}");
                    return new ColorThemeActivationResult(ColorThemeActivation.PersistenceFailed,
                        theme.VariantId, new[] { exception.Message });
                }
            }

            return new ColorThemeActivationResult(ColorThemeActivation.Activated, theme.VariantId,
                diagnostics);
        }

        /// <summary>
        /// Swaps in a new snapshot and notifies both the V2 and legacy change events.
        /// </summary>
        private void PublishTheme(ResolvedColorTheme theme, string previousVariantId,
            ColorThemeChangeReason reason)
        {
            _activeTheme = theme;
            _legacyAdapter = new LegacyColorProviderAdapter(_themeSettings?.ThemeSet, theme);

            // Lets ColorIDReference.Color and the legacy ColorUtility statics — which have no service
            // reference and cannot get one — resolve against V2 data instead of falling back to
            // ColorModule.ResolveActive() and fabricating an untracked palette.
            ColorModule.RuntimeProviderOverride = _legacyAdapter;

            RaiseThemeChanged(new ColorThemeChanged(previousVariantId, theme, reason));

            // Legacy subscribers — every existing ColorID component — listen on SchemeChanged. In V2
            // there is no ColorModule to hand them, and they ignore the argument anyway: they call
            // ApplyColors, which resolves through the override installed above.
            RaiseSchemeChanged(null);
        }

        private void RaiseThemeChanged(ColorThemeChanged payload)
        {
            var handlers = _themeChanged;
            if (handlers == null) return;

            // Invoked one handler at a time so a throwing subscriber cannot prevent the rest of the
            // bindings in the scene from refreshing — a single bad handler must not leave the UI
            // half-themed.
            foreach (Action<ColorThemeChanged> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(payload);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        #endregion

        #region Instance API (IColorThemeService)

        ColorThemeSet IColorThemeService.ThemeSet => _themeSettings?.ThemeSet;
        ResolvedColorTheme IColorThemeService.ActiveTheme => _activeTheme;
        string IColorThemeService.ActiveVariantId => _activeTheme?.VariantId;
        int IColorThemeService.Generation => _activeTheme?.Generation ?? 0;
        bool IColorThemeService.IsDegraded => _activeTheme?.IsDegraded ?? false;
        IColorProvider IColorThemeService.LegacyProvider => _legacyAdapter;

        string[] IColorThemeService.VariantIds =>
            _themeSettings?.ThemeSet != null ? _themeSettings.ThemeSet.GetVariantIds() : Array.Empty<string>();

        event Action<ColorThemeChanged> IColorThemeService.ThemeChanged
        {
            add => _themeChanged += value;
            remove => _themeChanged -= value;
        }

        bool IColorThemeService.TryResolve(string tokenId, out Color color)
        {
            if (_activeTheme != null) return _activeTheme.TryGetColor(tokenId, out color);
            color = Color.clear;
            return false;
        }

        Color IColorThemeService.Resolve(string tokenId)
        {
            if (_activeTheme != null && _activeTheme.TryGetColor(tokenId, out Color color)) return color;

            Debug.LogWarning($"[ColorTheme] Token '{tokenId}' does not resolve in variant "
                             + $"'{_activeTheme?.VariantId ?? "<none>"}'.");
            return Color.magenta;
        }

        bool IColorThemeService.SetVariant(string variantId, bool save) =>
            ((IColorThemeService)this).TrySetVariant(variantId, save, out _);

        bool IColorThemeService.TrySetVariant(string variantId, bool save,
            out ColorThemeActivationResult result)
        {
            if (IsV2 && !_themeSettings.AllowRuntimeSwitching)
            {
                result = new ColorThemeActivationResult(ColorThemeActivation.SettingsUnavailable,
                    _activeTheme?.VariantId,
                    new[] { "Runtime theme switching is disabled by ColorThemeSettings." });
                return result.HasUsableTheme;
            }

            result = Activate(variantId, save, ColorThemeChangeReason.VariantChanged);

            if (!result.Published && result.Outcome != ColorThemeActivation.AlreadyActive
                                  && result.Outcome != ColorThemeActivation.PersistenceFailed)
            {
                Debug.LogWarning($"[ColorTheme] Could not activate variant '{variantId}'. The previous "
                                 + $"theme remains active.\n{result}");
            }

            return result.HasUsableTheme;
        }

        void IColorThemeService.RefreshBindings()
        {
            if (_activeTheme == null) return;

            // Re-stamps the generation so bindings that skip already-applied generations still act on
            // this. Does not rebuild from the theme set — this refreshes targets, not authored data.
            _generation++;
            PublishTheme(_activeTheme.WithGeneration(_generation), _activeTheme.VariantId,
                ColorThemeChangeReason.Refreshed);
        }

        #endregion

        #region Instance API (IColorSchemeService)

        // Explicit implementations: the legacy statics keep these names
        // (protected-zone rule), so the instance API lives on the interface.

        // In V2 these are mapped onto variants so existing scheme-switching content — the shipped
        // Color Scheme Dropdown prefab included — drives the new model unchanged. ActiveScheme is the
        // one member that cannot be mapped: its type is ColorModule and V2 has none. It returns null
        // rather than fabricating one at runtime, which the read-only-ScriptableObject rule forbids;
        // callers needing colours in V2 use IColorThemeService, or LegacyProvider for legacy lookups.

        ColorModule IColorSchemeService.ActiveScheme => IsV2 ? null : ActiveSchemeCore;

        int IColorSchemeService.ActiveSchemeIndex => IsV2 ? ActiveVariantIndex : _activeSchemeIndex;

        string[] IColorSchemeService.SchemeNames => IsV2 ? VariantDisplayNames : SchemeNamesCore;

        int IColorSchemeService.SchemeCount => IsV2
            ? _themeSettings.ThemeSet.GetVariantIds().Length
            : _resolvedSchemes?.Length ?? 0;

        /// <summary>Index of the active variant in authored order, or -1.</summary>
        private int ActiveVariantIndex
        {
            get
            {
                if (_activeTheme == null || _themeSettings?.ThemeSet == null) return -1;

                var ids = _themeSettings.ThemeSet.GetVariantIds();
                for (int i = 0; i < ids.Length; i++)
                {
                    if (string.Equals(ids[i], _activeTheme.VariantId, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                return -1;
            }
        }

        /// <summary>Variant display names in authored order, for legacy scheme pickers.</summary>
        private string[] VariantDisplayNames
        {
            get
            {
                var themeSet = _themeSettings?.ThemeSet;
                if (themeSet == null) return Array.Empty<string>();

                var variants = themeSet.Variants;
                var names = new List<string>(variants.Count);
                foreach (var variant in variants)
                {
                    if (variant != null && !string.IsNullOrEmpty(variant.Id)) names.Add(variant.DisplayName);
                }
                return names.ToArray();
            }
        }

        event Action<ColorModule> IColorSchemeService.SchemeChanged
        {
            add => _schemeChanged += value;
            remove => _schemeChanged -= value;
        }

        void IColorSchemeService.SetScheme(int schemeIndex, bool save)
        {
            if (IsV2) SetVariantByIndex(schemeIndex, save);
            else SetSchemeCore(schemeIndex, save);
        }

        void IColorSchemeService.SetScheme(string schemeName, bool save)
        {
            if (!IsV2)
            {
                SetSchemeCore(schemeName, save);
                return;
            }

            // Accept either a variant ID or a display name: legacy callers passed ColorModule asset
            // names, which map to whichever of the two the author used when building the theme set.
            if (_themeSettings.ThemeSet.GetVariant(schemeName) != null)
            {
                ((IColorThemeService)this).SetVariant(schemeName, save);
                return;
            }

            foreach (var variant in _themeSettings.ThemeSet.Variants)
            {
                if (variant != null
                    && string.Equals(variant.DisplayName, schemeName, StringComparison.OrdinalIgnoreCase))
                {
                    ((IColorThemeService)this).SetVariant(variant.Id, save);
                    return;
                }
            }

            Debug.LogError($"ColorSchemeManager: No variant matches '{schemeName}'. Available: "
                           + $"{string.Join(", ", _themeSettings.ThemeSet.GetVariantIds())}.");
        }

        void IColorSchemeService.ToggleScheme(bool save) => CycleScheme(1, save);
        void IColorSchemeService.NextScheme(bool save) => CycleScheme(1, save);
        void IColorSchemeService.PreviousScheme(bool save) => CycleScheme(-1, save);

        void IColorSchemeService.RefreshAllColorIDs()
        {
            if (IsV2) ((IColorThemeService)this).RefreshBindings();
            else RaiseSchemeChanged(ActiveSchemeCore);
        }

        // No V2 equivalent: the return type is ColorModule. Returns null in V2 for the same reason
        // ActiveScheme does.
        ColorModule IColorSchemeService.GetScheme(int index) => IsV2 ? null : GetSchemeCore(index);
        ColorModule IColorSchemeService.GetScheme(string schemeName) => IsV2 ? null : GetSchemeCore(schemeName);

        private void SetVariantByIndex(int index, bool save)
        {
            var ids = _themeSettings.ThemeSet.GetVariantIds();
            if (index < 0 || index >= ids.Length)
            {
                Debug.LogError($"ColorSchemeManager: Invalid variant index {index}. Available: 0-{ids.Length - 1}");
                return;
            }
            ((IColorThemeService)this).SetVariant(ids[index], save);
        }

        /// <summary>Steps the active scheme/variant by <paramref name="offset"/>, wrapping.</summary>
        private void CycleScheme(int offset, bool save)
        {
            if (!IsV2)
            {
                if (offset >= 0) ToggleSchemeCore(save);
                else PreviousSchemeCore(save);
                return;
            }

            var ids = _themeSettings.ThemeSet.GetVariantIds();
            if (ids.Length < 2) return;

            int current = ActiveVariantIndex;
            // A negative modulo in C# stays negative, so the length is added before wrapping.
            int next = ((current + offset) % ids.Length + ids.Length) % ids.Length;
            ((IColorThemeService)this).SetVariant(ids[next], save);
        }

        private ColorModule ActiveSchemeCore =>
            _resolvedSchemes != null && _activeSchemeIndex >= 0 && _activeSchemeIndex < _resolvedSchemes.Length
                ? _resolvedSchemes[_activeSchemeIndex]
                : null;

        private string[] SchemeNamesCore
        {
            get
            {
                if (_resolvedSchemes == null)
                    return Array.Empty<string>();

                var names = new string[_resolvedSchemes.Length];
                for (int i = 0; i < _resolvedSchemes.Length; i++)
                {
                    names[i] = _resolvedSchemes[i] != null
                        ? _resolvedSchemes[i].DisplayName
                        : $"Unknown Scheme {i}";
                }
                return names;
            }
        }

        private void RaiseSchemeChanged(ColorModule newScheme)
        {
            _schemeChanged?.Invoke(newScheme);
        }

        private void SetSchemeCore(int schemeIndex, bool save)
        {
            if (_resolvedSchemes == null || schemeIndex < 0 || schemeIndex >= _resolvedSchemes.Length)
            {
                Debug.LogError($"ColorSchemeManager: Invalid scheme index {schemeIndex}. Available: 0-{(_resolvedSchemes?.Length ?? 0) - 1}");
                return;
            }

            if (_activeSchemeIndex == schemeIndex)
                return; // Already active

            SetSchemeInternal(schemeIndex, notifyListeners: true);

            if (save)
            {
                PlayerPrefs.SetInt(PREF_ACTIVE_SCHEME, schemeIndex);
                PlayerPrefs.Save();
            }
        }

        private void SetSchemeCore(string schemeName, bool save)
        {
            if (_resolvedSchemes == null)
                return;

            for (int i = 0; i < _resolvedSchemes.Length; i++)
            {
                if (_resolvedSchemes[i] != null && _resolvedSchemes[i].name == schemeName)
                {
                    SetSchemeCore(i, save);
                    return;
                }
            }

            Debug.LogError($"ColorSchemeManager: Scheme '{schemeName}' not found.");
        }

        private void ToggleSchemeCore(bool save)
        {
            if (_resolvedSchemes == null || _resolvedSchemes.Length < 2)
                return;

            int nextIndex = (_activeSchemeIndex + 1) % _resolvedSchemes.Length;
            SetSchemeCore(nextIndex, save);
        }

        private void PreviousSchemeCore(bool save)
        {
            if (_resolvedSchemes == null || _resolvedSchemes.Length < 2)
                return;

            int prevIndex = _activeSchemeIndex - 1;
            if (prevIndex < 0)
                prevIndex = _resolvedSchemes.Length - 1;

            SetSchemeCore(prevIndex, save);
        }

        private ColorModule GetSchemeCore(int index)
        {
            if (_resolvedSchemes == null || index < 0 || index >= _resolvedSchemes.Length)
                return null;

            return _resolvedSchemes[index];
        }

        private ColorModule GetSchemeCore(string schemeName)
        {
            if (_resolvedSchemes == null)
                return null;

            foreach (var scheme in _resolvedSchemes)
            {
                if (scheme != null && scheme.name == schemeName)
                    return scheme;
            }

            return null;
        }

        #endregion

        private void SetSchemeInternal(int schemeIndex, bool notifyListeners)
        {
            var newScheme = _resolvedSchemes[schemeIndex];
            if (newScheme == null)
            {
                Debug.LogError($"ColorSchemeManager: Scheme at index {schemeIndex} is null.");
                return;
            }

            _activeSchemeIndex = schemeIndex;

            // Set the new scheme as the active ColorModule
            ColorModule.SetActiveModule(newScheme);

            Debug.Log($"ColorSchemeManager: Activated scheme '{newScheme.name}' (index {schemeIndex})");

            if (notifyListeners)
            {
                RaiseSchemeChanged(newScheme);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Clamp against the serialized array, not the resolved one: OnValidate runs in edit
            // mode where Initialize has never run, so _resolvedSchemes is still empty and
            // clamping to it would silently rewrite the authored default index to 0.
            if (_availableSchemes != null)
            {
                _defaultSchemeIndex = Mathf.Clamp(_defaultSchemeIndex, 0, Mathf.Max(0, _availableSchemes.Length - 1));
            }
        }
#endif
    }
}
