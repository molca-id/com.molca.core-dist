using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.ColorID
{
    /// <summary>
    /// Runtime subsystem that manages color scheme switching (e.g., Light/Dark mode).
    /// Provides safe swapping of ColorModule instances and notifies all ColorID components.
    /// New code should use the <see cref="IColorSchemeService"/> instance API (resolve via
    /// <c>RuntimeManager.GetService&lt;IColorSchemeService&gt;()</c>); the static members
    /// remain as obsolete compatibility shims.
    /// </summary>
    public class ColorSchemeManager : RuntimeSubsystem, IColorSchemeService
    {
        private const string PREF_ACTIVE_SCHEME = "ColorScheme_Active";

        [Header("Color Schemes")]
        [SerializeField, FormerlySerializedAs("availableSchemes")] private ColorModule[] _availableSchemes;
        [SerializeField, FormerlySerializedAs("defaultSchemeIndex")] private int _defaultSchemeIndex = 0;

        private int _activeSchemeIndex = -1;
        private static ColorSchemeManager _instance;

        // Instance event — the IColorSchemeService API. Backing field is shared with
        // the obsolete static event via RaiseSchemeChanged.
        private event Action<ColorModule> _schemeChanged;

        /// <summary>
        /// Event fired when the color scheme changes. Passes the new active ColorModule.
        /// </summary>
        [Obsolete("Use IColorSchemeService.SchemeChanged (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static event Action<ColorModule> OnSchemeChanged;

        /// <summary>
        /// Gets the singleton instance of the ColorSchemeManager.
        /// </summary>
        [Obsolete("Use IColorSchemeService (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static ColorSchemeManager Instance => _instance;

        /// <summary>
        /// Gets the currently active color scheme.
        /// </summary>
        [Obsolete("Use IColorSchemeService.ActiveScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static ColorModule ActiveScheme => _instance != null ? _instance.ActiveSchemeCore : null;

        /// <summary>
        /// Gets the index of the currently active scheme.
        /// </summary>
        [Obsolete("Use IColorSchemeService.ActiveSchemeIndex (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static int ActiveSchemeIndex => _instance?._activeSchemeIndex ?? -1;

        /// <summary>
        /// Gets the names of all available color schemes.
        /// </summary>
        [Obsolete("Use IColorSchemeService.SchemeNames (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static string[] SchemeNames => _instance != null ? _instance.SchemeNamesCore : Array.Empty<string>();

        /// <summary>
        /// Gets the count of available schemes.
        /// </summary>
        [Obsolete("Use IColorSchemeService.SchemeCount (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static int SchemeCount => _instance?._availableSchemes?.Length ?? 0;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            _instance = this;

            // Validate schemes
            if (_availableSchemes == null || _availableSchemes.Length == 0)
            {
                Debug.LogWarning("ColorSchemeManager: No color schemes configured. Using default ColorModule from GlobalSettings.");
                finishCallback?.Invoke(this);
                return;
            }

            // Load saved preference or use default
            int savedIndex = PlayerPrefs.GetInt(PREF_ACTIVE_SCHEME, _defaultSchemeIndex);
            savedIndex = Mathf.Clamp(savedIndex, 0, _availableSchemes.Length - 1);

            // Set the initial scheme without triggering refresh (ColorID components aren't ready yet)
            SetSchemeInternal(savedIndex, notifyListeners: false);

            finishCallback?.Invoke(this);
        }

        public override void Shutdown()
        {
            base.Shutdown();
            _instance = null;
        }

        #region Instance API (IColorSchemeService)

        // Explicit implementations: the legacy statics keep these names
        // (protected-zone rule), so the instance API lives on the interface.

        ColorModule IColorSchemeService.ActiveScheme => ActiveSchemeCore;
        int IColorSchemeService.ActiveSchemeIndex => _activeSchemeIndex;
        string[] IColorSchemeService.SchemeNames => SchemeNamesCore;
        int IColorSchemeService.SchemeCount => _availableSchemes?.Length ?? 0;

        event Action<ColorModule> IColorSchemeService.SchemeChanged
        {
            add => _schemeChanged += value;
            remove => _schemeChanged -= value;
        }

        void IColorSchemeService.SetScheme(int schemeIndex, bool save) => SetSchemeCore(schemeIndex, save);
        void IColorSchemeService.SetScheme(string schemeName, bool save) => SetSchemeCore(schemeName, save);
        void IColorSchemeService.ToggleScheme(bool save) => ToggleSchemeCore(save);
        void IColorSchemeService.NextScheme(bool save) => ToggleSchemeCore(save);
        void IColorSchemeService.PreviousScheme(bool save) => PreviousSchemeCore(save);
        void IColorSchemeService.RefreshAllColorIDs() => RaiseSchemeChanged(ActiveSchemeCore);
        ColorModule IColorSchemeService.GetScheme(int index) => GetSchemeCore(index);
        ColorModule IColorSchemeService.GetScheme(string schemeName) => GetSchemeCore(schemeName);

        private ColorModule ActiveSchemeCore => _activeSchemeIndex >= 0 && _availableSchemes != null
            ? _availableSchemes[_activeSchemeIndex]
            : null;

        private string[] SchemeNamesCore
        {
            get
            {
                if (_availableSchemes == null)
                    return Array.Empty<string>();

                var names = new string[_availableSchemes.Length];
                for (int i = 0; i < _availableSchemes.Length; i++)
                {
                    names[i] = _availableSchemes[i] != null
                        ? _availableSchemes[i].DisplayName
                        : $"Unknown Scheme {i}";
                }
                return names;
            }
        }

        // Single raise point for both the instance event and the obsolete static one.
        private void RaiseSchemeChanged(ColorModule newScheme)
        {
            _schemeChanged?.Invoke(newScheme);
#pragma warning disable CS0618
            OnSchemeChanged?.Invoke(newScheme);
#pragma warning restore CS0618
        }

        private void SetSchemeCore(int schemeIndex, bool save)
        {
            if (_availableSchemes == null || schemeIndex < 0 || schemeIndex >= _availableSchemes.Length)
            {
                Debug.LogError($"ColorSchemeManager: Invalid scheme index {schemeIndex}. Available: 0-{(_availableSchemes?.Length ?? 0) - 1}");
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
            if (_availableSchemes == null)
                return;

            for (int i = 0; i < _availableSchemes.Length; i++)
            {
                if (_availableSchemes[i] != null && _availableSchemes[i].name == schemeName)
                {
                    SetSchemeCore(i, save);
                    return;
                }
            }

            Debug.LogError($"ColorSchemeManager: Scheme '{schemeName}' not found.");
        }

        private void ToggleSchemeCore(bool save)
        {
            if (_availableSchemes == null || _availableSchemes.Length < 2)
                return;

            int nextIndex = (_activeSchemeIndex + 1) % _availableSchemes.Length;
            SetSchemeCore(nextIndex, save);
        }

        private void PreviousSchemeCore(bool save)
        {
            if (_availableSchemes == null || _availableSchemes.Length < 2)
                return;

            int prevIndex = _activeSchemeIndex - 1;
            if (prevIndex < 0)
                prevIndex = _availableSchemes.Length - 1;

            SetSchemeCore(prevIndex, save);
        }

        private ColorModule GetSchemeCore(int index)
        {
            if (_availableSchemes == null || index < 0 || index >= _availableSchemes.Length)
                return null;

            return _availableSchemes[index];
        }

        private ColorModule GetSchemeCore(string schemeName)
        {
            if (_availableSchemes == null)
                return null;

            foreach (var scheme in _availableSchemes)
            {
                if (scheme != null && scheme.name == schemeName)
                    return scheme;
            }

            return null;
        }

        #endregion

        #region Legacy static API (obsolete shims)

        /// <summary>
        /// Sets the active color scheme by index.
        /// </summary>
        /// <param name="schemeIndex">Index of the scheme to activate.</param>
        /// <param name="save">Whether to save the preference.</param>
        [Obsolete("Use IColorSchemeService.SetScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void SetScheme(int schemeIndex, bool save = true)
        {
            if (_instance == null)
            {
                Debug.LogError("ColorSchemeManager: Instance not initialized.");
                return;
            }
            _instance.SetSchemeCore(schemeIndex, save);
        }

        /// <summary>
        /// Sets the active color scheme by name.
        /// </summary>
        /// <param name="schemeName">Name of the scheme (ColorModule asset name) to activate.</param>
        /// <param name="save">Whether to save the preference.</param>
        [Obsolete("Use IColorSchemeService.SetScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void SetScheme(string schemeName, bool save = true)
        {
            if (_instance == null)
            {
                Debug.LogError("ColorSchemeManager: Instance not initialized.");
                return;
            }
            _instance.SetSchemeCore(schemeName, save);
        }

        /// <summary>
        /// Toggles between schemes. Useful for simple Light/Dark mode switching.
        /// </summary>
        /// <param name="save">Whether to save the preference.</param>
        [Obsolete("Use IColorSchemeService.ToggleScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void ToggleScheme(bool save = true)
        {
            _instance?.ToggleSchemeCore(save);
        }

        /// <summary>
        /// Moves to the next scheme in a cyclic manner.
        /// </summary>
        /// <param name="save">Whether to save the preference.</param>
        [Obsolete("Use IColorSchemeService.NextScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void NextScheme(bool save = true)
        {
            _instance?.ToggleSchemeCore(save);
        }

        /// <summary>
        /// Moves to the previous scheme in a cyclic manner.
        /// </summary>
        /// <param name="save">Whether to save the preference.</param>
        [Obsolete("Use IColorSchemeService.PreviousScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void PreviousScheme(bool save = true)
        {
            _instance?.PreviousSchemeCore(save);
        }

        /// <summary>
        /// Forces a refresh of all ColorID components with the current scheme.
        /// Useful after dynamic changes or scene loads.
        /// </summary>
        [Obsolete("Use IColorSchemeService.RefreshAllColorIDs (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static void RefreshAllColorIDs()
        {
            _instance?.RaiseSchemeChanged(_instance.ActiveSchemeCore);
        }

        /// <summary>
        /// Gets a ColorModule by index without activating it.
        /// </summary>
        /// <param name="index">Index of the scheme.</param>
        /// <returns>The ColorModule at the specified index, or null if invalid.</returns>
        [Obsolete("Use IColorSchemeService.GetScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static ColorModule GetScheme(int index)
        {
            return _instance != null ? _instance.GetSchemeCore(index) : null;
        }

        /// <summary>
        /// Gets a ColorModule by name without activating it.
        /// </summary>
        /// <param name="schemeName">Name of the scheme.</param>
        /// <returns>The ColorModule with the specified name, or null if not found.</returns>
        [Obsolete("Use IColorSchemeService.GetScheme (RuntimeManager.GetService<IColorSchemeService>()).")]
        public static ColorModule GetScheme(string schemeName)
        {
            return _instance != null ? _instance.GetSchemeCore(schemeName) : null;
        }

        #endregion

        private void SetSchemeInternal(int schemeIndex, bool notifyListeners)
        {
            var newScheme = _availableSchemes[schemeIndex];
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
            if (_availableSchemes != null)
            {
                _defaultSchemeIndex = Mathf.Clamp(_defaultSchemeIndex, 0, Mathf.Max(0, _availableSchemes.Length - 1));
            }
        }
#endif
    }
}
