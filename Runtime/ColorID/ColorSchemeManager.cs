using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.ColorID
{
    /// <summary>
    /// Runtime subsystem that manages color scheme switching (e.g., Light/Dark mode).
    /// Provides safe swapping of ColorModule instances and notifies all ColorID components.
    /// Resolve via the <see cref="IColorSchemeService"/> instance API
    /// (<c>RuntimeManager.GetService&lt;IColorSchemeService&gt;()</c> or <c>[Inject]</c>).
    /// </summary>
    public class ColorSchemeManager : RuntimeSubsystem, IColorSchemeService
    {
        private const string PREF_ACTIVE_SCHEME = "ColorScheme_Active";

        [Header("Color Schemes")]
        [SerializeField, FormerlySerializedAs("availableSchemes")] private ColorModule[] _availableSchemes;
        [SerializeField, FormerlySerializedAs("defaultSchemeIndex")] private int _defaultSchemeIndex = 0;

        private int _activeSchemeIndex = -1;

        // Instance event — the IColorSchemeService API.
        private event Action<ColorModule> _schemeChanged;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
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

        private void RaiseSchemeChanged(ColorModule newScheme)
        {
            _schemeChanged?.Invoke(newScheme);
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
