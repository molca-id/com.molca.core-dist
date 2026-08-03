using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Runtime subsystem that owns the active colour theme and variant switching.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/</c>.
    /// <b>Base class:</b> <see cref="RuntimeSubsystem"/>.
    /// <b>Registration:</b> discovered by <see cref="RuntimeManager"/> and exposed as
    /// <see cref="IColorThemeService"/> through automatic subsystem-interface registration.
    /// <para/>
    /// Variants resolve through <see cref="ColorThemeResolver"/> into immutable snapshots. A complete
    /// snapshot is published atomically, so a failed activation cannot leave the application with a
    /// half-updated theme.
    /// </remarks>
    public class ColorSchemeManager : RuntimeSubsystem, IColorThemeService
    {
        private ColorThemeSettings _themeSettings;
        private ResolvedColorTheme _activeTheme;
        private int _generation;
        private event Action<ColorThemeChanged> _themeChanged;

        private bool HasThemeSet => _themeSettings != null && _themeSettings.ThemeSet != null;

        /// <inheritdoc/>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            // GlobalSettings has already initialized its modules and states by the time subsystems run.
            _themeSettings = GlobalSettings.GetModule<ColorThemeSettings>();

            if (!HasThemeSet)
            {
                Debug.LogError("[ColorTheme] No ColorThemeSettings module with a theme set is installed. "
                               + "Install a colour theme before entering Play Mode.");
                finishCallback?.Invoke(this);
                return;
            }

            InitializeTheme();
            finishCallback?.Invoke(this);
        }

        /// <summary>
        /// Activates the persisted variant, or the authored default, or the emergency fallback.
        /// </summary>
        private void InitializeTheme()
        {
            var state = _themeSettings.TypedState;
            string requested = state?.ActiveVariantId ?? _themeSettings.DefaultVariantId;
            string authoredDefault = _themeSettings.DefaultVariantId;

            var result = Activate(requested, save: false, ColorThemeChangeReason.Initialized);

            if (!result.Published
                && !string.Equals(requested, authoredDefault, StringComparison.OrdinalIgnoreCase))
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

        /// <inheritdoc/>
        public override void Shutdown()
        {
            _themeChanged = null;
            _activeTheme = null;
            _themeSettings = null;
            base.Shutdown();
        }

        /// <summary>
        /// Resolves and publishes a variant, leaving the current theme untouched on failure.
        /// </summary>
        private ColorThemeActivationResult Activate(string variantId, bool save,
            ColorThemeChangeReason reason)
        {
            if (!HasThemeSet)
            {
                return new ColorThemeActivationResult(ColorThemeActivation.SettingsUnavailable,
                    _activeTheme?.VariantId,
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
                    // Activation succeeded; only persistence failed. Keep the usable theme published.
                    Debug.LogWarning($"[ColorTheme] Variant '{theme.VariantId}' activated but the "
                                     + $"preference could not be saved: {exception.Message}");
                    return new ColorThemeActivationResult(ColorThemeActivation.PersistenceFailed,
                        theme.VariantId, new[] { exception.Message });
                }
            }

            return new ColorThemeActivationResult(ColorThemeActivation.Activated, theme.VariantId,
                diagnostics);
        }

        private void PublishTheme(ResolvedColorTheme theme, string previousVariantId,
            ColorThemeChangeReason reason)
        {
            _activeTheme = theme;
            RaiseThemeChanged(new ColorThemeChanged(previousVariantId, theme, reason));
        }

        private void RaiseThemeChanged(ColorThemeChanged payload)
        {
            var handlers = _themeChanged;
            if (handlers == null) return;

            // One bad subscriber must not prevent the remaining bindings from refreshing.
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

        ColorThemeSet IColorThemeService.ThemeSet => _themeSettings?.ThemeSet;
        ResolvedColorTheme IColorThemeService.ActiveTheme => _activeTheme;
        string IColorThemeService.ActiveVariantId => _activeTheme?.VariantId;
        int IColorThemeService.Generation => _activeTheme?.Generation ?? 0;
        bool IColorThemeService.IsDegraded => _activeTheme?.IsDegraded ?? false;

        string[] IColorThemeService.VariantIds =>
            _themeSettings?.ThemeSet != null
                ? _themeSettings.ThemeSet.GetVariantIds()
                : Array.Empty<string>();

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
            if (_themeSettings != null && !_themeSettings.AllowRuntimeSwitching)
            {
                result = new ColorThemeActivationResult(ColorThemeActivation.SettingsUnavailable,
                    _activeTheme?.VariantId,
                    new[] { "Runtime theme switching is disabled by ColorThemeSettings." });
                return result.HasUsableTheme;
            }

            result = Activate(variantId, save, ColorThemeChangeReason.VariantChanged);

            if (!result.Published
                && result.Outcome != ColorThemeActivation.AlreadyActive
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

            _generation++;
            PublishTheme(_activeTheme.WithGeneration(_generation), _activeTheme.VariantId,
                ColorThemeChangeReason.Refreshed);
        }
    }
}
