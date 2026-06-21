using System;
using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;
using Molca.Settings;

namespace Molca.ColorID
{
    /// <summary>
    /// Centralized color management system that provides colors by ID.
    /// New code should use the <see cref="IColorProvider"/> _instance API on the
    /// active scheme (<see cref="IColorSchemeService.ActiveScheme"/>); the static
    /// members remain as obsolete compatibility shims.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Color Settings", menuName = "Molca/Settings/Color Settings", order = 10)]
    public class ColorModule : SettingModule, IColorProvider
    {
        [SerializeField, FormerlySerializedAs("schemeName")] private string _schemeName;

        [System.Serializable]
        public class ColorDefinition
        {
            [SerializeField] private string colorId;
            [SerializeField] private Color color;
            [SerializeField] private string description;

            public string ColorId => colorId;
            public Color Color => color;
            public string Description => description;

            public ColorDefinition(string id, Color color, string description = "")
            {
                this.colorId = id;
                this.color = color;
                this.description = description;
            }
        }

        [System.Serializable]
        public class ColorSwatch
        {
            [SerializeField] private string swatchName;
            [SerializeField] private bool isDefault;
            [SerializeField] private List<ColorDefinition> colorDefinitions = new List<ColorDefinition>();

            public string SwatchName => swatchName;
            public bool IsDefault => isDefault;
            public List<ColorDefinition> ColorDefinitions => colorDefinitions;

            public ColorSwatch(string name, bool isDefault = false)
            {
                this.swatchName = name;
                this.isDefault = isDefault;
                this.colorDefinitions = new List<ColorDefinition>();
            }

            public void AddColor(string colorId, Color color, string description = "")
            {
                colorDefinitions.Add(new ColorDefinition(colorId, color, description));
            }

            public bool RemoveColor(string colorId)
            {
                return colorDefinitions.RemoveAll(d => d.ColorId == colorId) > 0;
            }

            public ColorDefinition GetColor(string colorId)
            {
                return colorDefinitions.Find(d => d.ColorId == colorId);
            }
        }
        
        [SerializeField] private List<ColorDefinition> colorDefinitions = new List<ColorDefinition>(); // Legacy support
        [SerializeField, FormerlySerializedAs("colorSwatches")] private List<ColorSwatch> _colorSwatches = new List<ColorSwatch>();

        // Public properties for editor access
        public List<ColorDefinition> ColorDefinitions => colorDefinitions; // Legacy support
        public List<ColorSwatch> ColorSwatches => _colorSwatches;
        public Dictionary<string, Color> ColorCache => _colorCache;
        public int DefinitionCount => colorDefinitions?.Count ?? 0; // Legacy support
        public int CacheCount => _colorCache?.Count ?? 0;
        public string DisplayName => string.IsNullOrEmpty(_schemeName) ? name : _schemeName;

        private static ColorModule _instance;
        private static bool _isInitialized = false;
        
        /// <summary>Gets the active ColorModule, lazily resolving it if needed.</summary>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static ColorModule Instance => ResolveActive();

        /// <summary>
        /// Resolves the active ColorModule, lazily locating it via GlobalSettings or
        /// creating a default fallback.
        /// </summary>
        /// <remarks>
        /// Non-obsolete resolution point shared by the legacy static shims and
        /// in-package callers that must work without a RuntimeManager (edit-mode
        /// drawers, early Awake).
        /// </remarks>
        internal static ColorModule ResolveActive()
        {
            if (_instance == null)
            {
                // Try to find existing _instance from GlobalSettings first
                _instance = GlobalSettings.GetModule<ColorModule>();

                if (_instance == null)
                {
                    // Create default _instance if none exists
                    _instance = CreateInstance<ColorModule>();
                    _instance.InitializeDefaultColors();
                    Debug.LogWarning("ColorModule not found in GlobalSettings. Created default _instance.");
                }

                // Initialize if not already done
                if (!_isInitialized && _instance != null)
                {
                    _instance.Initialize();
                    _instance.BuildFromDefinitions();
                    _isInitialized = true;
                }
            }
            return _instance;
        }
        
        /// <summary>
        /// Sets a specific ColorModule as the active _instance.
        /// Used by ColorSchemeManager for color scheme switching.
        /// </summary>
        /// <param name="module">The ColorModule to set as active.</param>
        public static void SetActiveModule(ColorModule module)
        {
            if (module == null)
            {
                Debug.LogError("Cannot set null ColorModule as active.");
                return;
            }

            _instance = module;
            _isInitialized = false;
            
            // Initialize the new module
            _instance.Initialize();
            _instance.BuildFromDefinitions();
            _isInitialized = true;
        }
        
        /// <summary>
        /// Force refresh the _instance reference.
        /// </summary>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static void RefreshInstance()
        {
            _instance = null;
            _isInitialized = false;
            var active = ResolveActive();
            active.Initialize();
            active.BuildFromDefinitions();
        }
        
        private Dictionary<string, Color> _colorCache = new Dictionary<string, Color>();

        #region Instance API (IColorProvider)

        // Explicit implementations: the legacy statics keep these names
        // (protected-zone rule), so the _instance lookup API lives on the interface.

        Color IColorProvider.GetColor(string colorId) => GetColorCore(colorId);
        Color IColorProvider.GetColor(string swatchName, string colorId) => GetColorCore(swatchName, colorId);
        Color IColorProvider.GetColor(string colorId, float alpha)
        {
            Color color = GetColorCore(colorId);
            color.a = alpha;
            return color;
        }
        Color IColorProvider.GetColor(string swatchName, string colorId, float alpha)
        {
            Color color = GetColorCore(swatchName, colorId);
            color.a = alpha;
            return color;
        }
        bool IColorProvider.HasColor(string colorId) => HasColorCore(colorId);
        bool IColorProvider.HasColor(string swatchName, string colorId) => HasColorCore(swatchName, colorId);
        string[] IColorProvider.GetAllColorIds() => GetAllColorIdsCore();
        string[] IColorProvider.GetColorIdsInSwatch(string swatchName) => GetColorIdsInSwatchCore(swatchName);
        string[] IColorProvider.GetSwatchNames() => GetSwatchNamesCore();

        private Color GetColorCore(string swatchName, string colorId)
        {
            string compositeKey = $"{swatchName}.{colorId}";
            if (_colorCache.TryGetValue(compositeKey, out Color color))
            {
                return color;
            }

            Debug.LogWarning($"Color with ID '{colorId}' not found in swatch '{swatchName}'.");
            return Color.magenta;
        }

        private Color GetColorCore(string colorId)
        {
            // Check if it's a composite key format "SwatchName.ColorId"
            if (colorId.Contains("."))
            {
                if (_colorCache.TryGetValue(colorId, out Color compositeColor))
                {
                    return compositeColor;
                }
            }

            // Try default swatch first (backward compatibility)
            string defaultKey = $"Default.{colorId}";
            if (_colorCache.TryGetValue(defaultKey, out Color color))
            {
                return color;
            }

            // Search non-default swatches in list order for deterministic behavior
            // This ensures lower-indexed swatches are checked first
            foreach (var swatch in _colorSwatches)
            {
                if (swatch.SwatchName == "Default") continue; // Skip default, already checked

                string compositeKey = $"{swatch.SwatchName}.{colorId}";
                if (_colorCache.TryGetValue(compositeKey, out Color swatchColor))
                {
                    return swatchColor;
                }
            }

            Debug.LogWarning($"Color with ID '{colorId}' not found in ColorModule.");
            return Color.magenta;
        }

        private bool HasColorCore(string swatchName, string colorId)
        {
            string compositeKey = $"{swatchName}.{colorId}";
            return _colorCache.ContainsKey(compositeKey);
        }

        private bool HasColorCore(string colorId)
        {
            // Check if it's a composite key
            if (colorId.Contains("."))
            {
                return _colorCache.ContainsKey(colorId);
            }

            // Check default swatch first
            string defaultKey = $"Default.{colorId}";
            if (_colorCache.ContainsKey(defaultKey))
            {
                return true;
            }

            // Search non-default swatches in list order for deterministic behavior
            foreach (var swatch in _colorSwatches)
            {
                if (swatch.SwatchName == "Default") continue; // Skip default, already checked

                string compositeKey = $"{swatch.SwatchName}.{colorId}";
                if (_colorCache.ContainsKey(compositeKey))
                {
                    return true;
                }
            }

            return false;
        }

        private string[] GetAllColorIdsCore()
        {
            string[] ids = new string[_colorCache.Count];
            _colorCache.Keys.CopyTo(ids, 0);
            return ids;
        }

        private string[] GetColorIdsInSwatchCore(string swatchName)
        {
            var swatch = _colorSwatches.Find(s => s.SwatchName == swatchName);
            if (swatch != null)
            {
                return swatch.ColorDefinitions.ConvertAll(d => d.ColorId).ToArray();
            }
            return new string[0];
        }

        private string[] GetSwatchNamesCore()
        {
            return _colorSwatches.ConvertAll(s => s.SwatchName).ToArray();
        }

        #endregion

        #region Legacy static API (obsolete shims)

        /// <summary>
        /// Gets a color by swatch name and color ID
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID of the color to retrieve</param>
        /// <returns>The color, or white if not found</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static Color GetColor(string swatchName, string colorId)
        {
            return ResolveActive().GetColorCore(swatchName, colorId);
        }

        /// <summary>
        /// Gets a color by its ID (backward compatibility - searches default swatch first, then all swatches in order)
        /// Can also accept composite format "SwatchName.ColorId"
        /// </summary>
        /// <param name="colorId">The ID of the color to retrieve, or composite "SwatchName.ColorId"</param>
        /// <returns>The color, or white if not found</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static Color GetColor(string colorId)
        {
            return ResolveActive().GetColorCore(colorId);
        }

        /// <summary>
        /// Gets a color by swatch name and color ID with alpha
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID of the color to retrieve</param>
        /// <param name="alpha">The alpha value to apply</param>
        /// <returns>The color with specified alpha, or white if not found</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static Color GetColor(string swatchName, string colorId, float alpha)
        {
            Color color = ResolveActive().GetColorCore(swatchName, colorId);
            color.a = alpha;
            return color;
        }

        /// <summary>
        /// Gets a color by its ID with alpha (backward compatibility)
        /// </summary>
        /// <param name="colorId">The ID of the color to retrieve</param>
        /// <param name="alpha">The alpha value to apply</param>
        /// <returns>The color with specified alpha, or white if not found</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static Color GetColor(string colorId, float alpha)
        {
            Color color = ResolveActive().GetColorCore(colorId);
            color.a = alpha;
            return color;
        }

        /// <summary>
        /// Checks if a color ID exists in a specific swatch
        /// </summary>
        /// <param name="swatchName">The swatch name</param>
        /// <param name="colorId">The ID to check</param>
        /// <returns>True if the color ID exists in the swatch</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static bool HasColor(string swatchName, string colorId)
        {
            return ResolveActive().HasColorCore(swatchName, colorId);
        }

        /// <summary>
        /// Checks if a color ID exists (backward compatibility - searches default swatch first, then all swatches in order)
        /// </summary>
        /// <param name="colorId">The ID to check, or composite "SwatchName.ColorId"</param>
        /// <returns>True if the color ID exists</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static bool HasColor(string colorId)
        {
            return ResolveActive().HasColorCore(colorId);
        }

        /// <summary>
        /// Gets all available color IDs in composite format "SwatchName.ColorId"
        /// </summary>
        /// <returns>Array of all color IDs</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static string[] GetAllColorIds()
        {
            return ResolveActive().GetAllColorIdsCore();
        }

        /// <summary>
        /// Gets all available color IDs for a specific swatch
        /// </summary>
        /// <param name="swatchName">The swatch name</param>
        /// <returns>Array of color IDs in the swatch</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static string[] GetColorIdsInSwatch(string swatchName)
        {
            return ResolveActive().GetColorIdsInSwatchCore(swatchName);
        }

        #endregion

        /// <summary>
        /// Adds a new color definition to a specific swatch
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID for the color</param>
        /// <param name="color">The color value</param>
        /// <param name="description">Optional description</param>
        public void AddColor(string swatchName, string colorId, Color color, string description = "")
        {
            if (string.IsNullOrEmpty(swatchName))
            {
                Debug.LogError("Swatch name cannot be null or empty.");
                return;
            }
            
            if (string.IsNullOrEmpty(colorId))
            {
                Debug.LogError("Color ID cannot be null or empty.");
                return;
            }

            var swatch = _colorSwatches.Find(s => s.SwatchName == swatchName);
            if (swatch == null)
            {
                Debug.LogError($"Swatch '{swatchName}' not found.");
                return;
            }

            string compositeKey = $"{swatchName}.{colorId}";
            if (_colorCache.ContainsKey(compositeKey))
            {
                Debug.LogWarning($"Color with ID '{colorId}' already exists in swatch '{swatchName}'. Updating existing color.");
                _colorCache[compositeKey] = color;
            }
            else
            {
                swatch.AddColor(colorId, color, description);
                _colorCache[compositeKey] = color;
            }
        }

        /// <summary>
        /// Adds a new color definition (legacy support - adds to default swatch)
        /// </summary>
        /// <param name="colorId">The ID for the color</param>
        /// <param name="color">The color value</param>
        /// <param name="description">Optional description</param>
        public void AddColor(string colorId, Color color, string description = "")
        {
            AddColor("Default", colorId, color, description);
        }

        /// <summary>
        /// Removes a color definition from a specific swatch
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID of the color to remove</param>
        public void RemoveColor(string swatchName, string colorId)
        {
            var swatch = _colorSwatches.Find(s => s.SwatchName == swatchName);
            if (swatch != null)
            {
                swatch.RemoveColor(colorId);
                string compositeKey = $"{swatchName}.{colorId}";
                _colorCache.Remove(compositeKey);
            }
        }

        /// <summary>
        /// Removes a color definition (legacy support - removes from default swatch)
        /// </summary>
        /// <param name="colorId">The ID of the color to remove</param>
        public void RemoveColor(string colorId)
        {
            RemoveColor("Default", colorId);
        }

        /// <summary>
        /// Updates a color definition and saves it to PlayerPrefs
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID of the color to update</param>
        /// <param name="color">The new color value</param>
        public void UpdateColor(string swatchName, string colorId, Color color)
        {
            if (string.IsNullOrEmpty(swatchName))
            {
                Debug.LogError("Swatch name cannot be null or empty.");
                return;
            }
            
            if (string.IsNullOrEmpty(colorId))
            {
                Debug.LogError("Color ID cannot be null or empty.");
                return;
            }

            string compositeKey = $"{swatchName}.{colorId}";
            if (_colorCache.ContainsKey(compositeKey))
            {
                _colorCache[compositeKey] = color;
                SaveColorToPlayerPrefs(swatchName, colorId, color);
            }
            else
            {
                Debug.LogWarning($"Color with ID '{colorId}' not found in swatch '{swatchName}'. Use AddColor instead.");
            }
        }

        /// <summary>
        /// Updates a color definition (legacy support - updates in default swatch)
        /// </summary>
        /// <param name="colorId">The ID of the color to update</param>
        /// <param name="color">The new color value</param>
        public void UpdateColor(string colorId, Color color)
        {
            UpdateColor("Default", colorId, color);
        }

        /// <summary>
        /// Gets a color from PlayerPrefs if available, otherwise from default definitions
        /// </summary>
        /// <param name="colorId">The ID of the color to retrieve</param>
        /// <returns>The color value</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static Color GetColorWithFallback(string colorId)
        {
            Color savedColor = LoadColorFromPlayerPrefs("Default", colorId);
            if (savedColor != Color.clear) // Color.clear indicates no saved color found
            {
                return savedColor;
            }

            return ResolveActive().GetColorCore(colorId);
        }

        /// <summary>
        /// Adds a new swatch
        /// </summary>
        /// <param name="swatchName">The name of the swatch to add</param>
        /// <returns>True if the swatch was added successfully</returns>
        public bool AddSwatch(string swatchName)
        {
            if (string.IsNullOrEmpty(swatchName))
            {
                Debug.LogError("Swatch name cannot be null or empty.");
                return false;
            }

            if (_colorSwatches.Exists(s => s.SwatchName == swatchName))
            {
                Debug.LogWarning($"Swatch '{swatchName}' already exists.");
                return false;
            }

            _colorSwatches.Add(new ColorSwatch(swatchName, false));
            return true;
        }

        /// <summary>
        /// Removes a swatch (cannot remove default swatch)
        /// </summary>
        /// <param name="swatchName">The name of the swatch to remove</param>
        /// <returns>True if the swatch was removed successfully</returns>
        public bool RemoveSwatch(string swatchName)
        {
            var swatch = _colorSwatches.Find(s => s.SwatchName == swatchName);
            
            if (swatch == null)
            {
                Debug.LogWarning($"Swatch '{swatchName}' not found.");
                return false;
            }

            if (swatch.IsDefault)
            {
                Debug.LogError("Cannot remove the default swatch.");
                return false;
            }

            // Remove all colors in the swatch from the cache
            foreach (var colorDef in swatch.ColorDefinitions)
            {
                string compositeKey = $"{swatchName}.{colorDef.ColorId}";
                _colorCache.Remove(compositeKey);
            }

            _colorSwatches.Remove(swatch);
            return true;
        }

        /// <summary>
        /// Gets all swatch names
        /// </summary>
        /// <returns>Array of swatch names</returns>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static string[] GetSwatchNames()
        {
            return ResolveActive().GetSwatchNamesCore();
        }

        /// <summary>
        /// Gets a swatch by name
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <returns>The swatch, or null if not found</returns>
        public ColorSwatch GetSwatch(string swatchName)
        {
            return _colorSwatches.Find(s => s.SwatchName == swatchName);
        }

        /// <summary>
        /// Forces a refresh of the color cache
        /// </summary>
        public void RefreshCache()
        {
            BuildFromDefinitions();
        }

        /// <summary>
        /// Clears the saved PlayerPrefs value for a specific color in a swatch, forcing it to use the default definition
        /// </summary>
        /// <param name="swatchName">The name of the swatch</param>
        /// <param name="colorId">The ID of the color to clear</param>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static void ClearSavedColor(string swatchName, string colorId)
        {
            ClearColorFromPlayerPrefs(swatchName, colorId);
            if (_instance != null)
            {
                _instance.RefreshCache();
            }
        }

        /// <summary>
        /// Clears the saved PlayerPrefs value for a specific color (legacy support - clears from default swatch)
        /// </summary>
        /// <param name="colorId">The ID of the color to clear</param>
        [Obsolete("Use IColorSchemeService.ActiveScheme + _instance members (IColorProvider).")]
        public static void ClearSavedColor(string colorId)
        {
            ClearColorFromPlayerPrefs("Default", colorId);
            if (_instance != null)
            {
                _instance.RefreshCache();
            }
        }

        private void BuildFromDefinitions()
        {
            _colorCache.Clear();

            // Build cache from swatches
            foreach (var swatch in _colorSwatches)
            {
                foreach (var definition in swatch.ColorDefinitions)
                {
                    string compositeKey = $"{swatch.SwatchName}.{definition.ColorId}";
                    _colorCache[compositeKey] = definition.Color;
                }
            }

            // Assets still carrying legacy colorDefinitions (never opened in the
            // editor since the swatch migration) resolve through Default-swatch keys
            // without mutating the serialized lists. Swatch entries win on conflict.
            foreach (var definition in colorDefinitions)
            {
                if (string.IsNullOrEmpty(definition?.ColorId))
                    continue;
                string legacyKey = $"Default.{definition.ColorId}";
                if (!_colorCache.ContainsKey(legacyKey))
                    _colorCache[legacyKey] = definition.Color;
            }
        }

        private void BuildColorCache()
        {
            _colorCache.Clear();
            
            // Build cache from swatches with PlayerPrefs fallback
            foreach (var swatch in _colorSwatches)
            {
                foreach (var definition in swatch.ColorDefinitions)
                {
                    if (!string.IsNullOrEmpty(definition.ColorId))
                    {
                        string compositeKey = $"{swatch.SwatchName}.{definition.ColorId}";
                        Color savedColor = LoadColorFromPlayerPrefs(swatch.SwatchName, definition.ColorId);
                        if (savedColor != Color.clear)
                        {
                            _colorCache[compositeKey] = savedColor;
                        }
                        else
                        {
                            _colorCache[compositeKey] = definition.Color;
                        }
                    }
                }
            }
        }

        private void SaveColorToPlayerPrefs(string swatchName, string colorId, Color color)
        {
            string key = FieldKey($"{swatchName}.Color_{colorId}");
            PlayerPrefs.SetFloat($"{key}_R", color.r);
            PlayerPrefs.SetFloat($"{key}_G", color.g);
            PlayerPrefs.SetFloat($"{key}_B", color.b);
            PlayerPrefs.SetFloat($"{key}_A", color.a);
        }

        private static Color LoadColorFromPlayerPrefs(string swatchName, string colorId)
        {
            string key = $"Setting.{typeof(ColorModule).FullName}.{swatchName}.Color_{colorId}";
            
            if (!PlayerPrefs.HasKey($"{key}_R"))
            {
                return Color.clear; // Indicates no saved color found
            }

            float r = PlayerPrefs.GetFloat($"{key}_R");
            float g = PlayerPrefs.GetFloat($"{key}_G");
            float b = PlayerPrefs.GetFloat($"{key}_B");
            float a = PlayerPrefs.GetFloat($"{key}_A");

            return new Color(r, g, b, a);
        }

        private static void ClearColorFromPlayerPrefs(string swatchName, string colorId)
        {
            string key = $"Setting.{typeof(ColorModule).FullName}.{swatchName}.Color_{colorId}";
            PlayerPrefs.DeleteKey($"{key}_R");
            PlayerPrefs.DeleteKey($"{key}_G");
            PlayerPrefs.DeleteKey($"{key}_B");
            PlayerPrefs.DeleteKey($"{key}_A");
        }

        public override void SaveSettings()
        {
            // Save all current colors to PlayerPrefs
            foreach (var swatch in _colorSwatches)
            {
                foreach (var definition in swatch.ColorDefinitions)
                {
                    string compositeKey = $"{swatch.SwatchName}.{definition.ColorId}";
                    if (_colorCache.TryGetValue(compositeKey, out Color color))
                    {
                        SaveColorToPlayerPrefs(swatch.SwatchName, definition.ColorId, color);
                    }
                }
            }
            PlayerPrefs.Save();
        }

        public override void LoadSettings()
        {
#if UNITY_EDITOR
            // The destructive swatch migration rewrites serialized lists, so it may
            // only run as an edit-mode authoring step (SO cardinal rule). At runtime,
            // un-migrated legacy definitions are folded into the lookup cache
            // non-destructively by BuildFromDefinitions.
            if (!Application.isPlaying)
                MigrateLegacyColors();
#endif
            BuildFromDefinitions();
        }

        public void LoadFromPlayerPrefs()
        {
            BuildColorCache();
        }

        public override void ResetToDefaults()
        {
            // Clear all saved colors from PlayerPrefs
            foreach (var swatch in _colorSwatches)
            {
                foreach (var definition in swatch.ColorDefinitions)
                {
                    ClearColorFromPlayerPrefs(swatch.SwatchName, definition.ColorId);
                }
            }
            
            // Remove all custom swatches (keep only default)
            _colorSwatches.RemoveAll(s => !s.IsDefault);
            
            // Reset default swatch to original colors
            var defaultSwatch = _colorSwatches.Find(s => s.IsDefault);
            if (defaultSwatch != null)
            {
                defaultSwatch.ColorDefinitions.Clear();
                PopulateDefaultSwatchColors(defaultSwatch);
            }
            else
            {
                // If somehow default swatch doesn't exist, reinitialize everything
                InitializeDefaultColors();
            }
            
            // Rebuild cache with default colors
            BuildFromDefinitions();
            PlayerPrefs.Save();
        }

        private void InitializeDefaultColors()
        {
            _colorSwatches.Clear();
            
            // Create default swatch
            var defaultSwatch = new ColorSwatch("Default", true);
            PopulateDefaultSwatchColors(defaultSwatch);
            _colorSwatches.Add(defaultSwatch);
            
            BuildFromDefinitions();
        }

        private void PopulateDefaultSwatchColors(ColorSwatch swatch)
        {
            swatch.AddColor("Primary", new Color(0.2f, 0.6f, 1f), "Primary brand color");
            swatch.AddColor("Secondary", new Color(0.8f, 0.8f, 0.8f), "Secondary brand color");
            swatch.AddColor("Accent", new Color(1f, 0.6f, 0.2f), "Accent color");
            swatch.AddColor("Success", new Color(0.2f, 0.8f, 0.2f), "Success/positive color");
            swatch.AddColor("Warning", new Color(1f, 0.8f, 0.2f), "Warning color");
            swatch.AddColor("Error", new Color(1f, 0.2f, 0.2f), "Error/negative color");
            swatch.AddColor("Text", new Color(0.1f, 0.1f, 0.1f), "Default text color");
            swatch.AddColor("Background", new Color(1f, 1f, 1f), "Default background color");
            swatch.AddColor("Disabled", new Color(0.5f, 0.5f, 0.5f), "Disabled state color");
            swatch.AddColor("Clear", new Color(0f, 0f, 0f, 0f), "Transparent/clear color");
        }

#if UNITY_EDITOR
        // Edit-mode-only authoring step: rewrites the serialized swatch lists.
        // Runtime lookups handle un-migrated assets via BuildFromDefinitions.
        private void MigrateLegacyColors()
        {
            // If we have legacy colorDefinitions but no swatches, migrate them
            if (colorDefinitions.Count > 0 && (_colorSwatches == null || _colorSwatches.Count == 0))
            {
                Debug.Log("Migrating legacy color definitions to Default swatch...");
                
                _colorSwatches = new List<ColorSwatch>();
                var defaultSwatch = new ColorSwatch("Default", true);
                
                foreach (var definition in colorDefinitions)
                {
                    defaultSwatch.AddColor(definition.ColorId, definition.Color, definition.Description);
                }
                
                _colorSwatches.Add(defaultSwatch);
                colorDefinitions.Clear(); // Clear legacy list
                
                #if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
                #endif
            }
            
            // Ensure default swatch exists and is at index 0
            if (_colorSwatches == null || _colorSwatches.Count == 0)
            {
                InitializeDefaultColors();
            }
            else
            {
                var defaultSwatch = _colorSwatches.Find(s => s.IsDefault);
                if (defaultSwatch == null)
                {
                    // Create default swatch if it doesn't exist
                    defaultSwatch = new ColorSwatch("Default", true);
                    _colorSwatches.Insert(0, defaultSwatch);
                }
                else if (_colorSwatches.IndexOf(defaultSwatch) != 0)
                {
                    // Move default swatch to index 0
                    _colorSwatches.Remove(defaultSwatch);
                    _colorSwatches.Insert(0, defaultSwatch);
                }
            }
        }
#endif

        #if UNITY_EDITOR
        [UnityEditor.MenuItem("Molca/ColorID/Apply Colors to all IDs", priority = 50)]
        private static void ApplyColorsToColorID()
        {
            // Refresh all ColorID references in the current scene
            var allColorIds = FindObjectsByType<ColorID>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int refreshedCount = 0;
            foreach (var colorId in allColorIds)
            {
                if (colorId != null)
                {
                    UnityEditor.EditorUtility.SetDirty(colorId);
                    colorId.ApplyColors();
                    refreshedCount++;
                }
            }
        }

        [UnityEditor.MenuItem("Molca/ColorID/Scan and select invalid IDs", priority = 51)]
        private static void ScanInvalidColorIds()
        {
            var allColorIds = FindObjectsByType<ColorID>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var invalidColorIds = new List<ColorID>();
            foreach (var colorId in allColorIds)
            {
                if (!ResolveActive()._colorCache.TryGetValue($"{colorId.SwatchName}.{colorId.ColorId}", out Color color))
                {
                    invalidColorIds.Add(colorId);
                }
            }
            if (invalidColorIds.Count > 0)
            {
                UnityEditor.Selection.objects = invalidColorIds.ConvertAll(c => c.gameObject).ToArray();
                UnityEditor.EditorUtility.DisplayDialog(
                    "Invalid ColorIDs Found",
                    $"Found {invalidColorIds.Count} ColorID(s) with missing color values. They have been selected in the hierarchy.",
                    "OK"
                );
            }
            else
            {
                UnityEditor.EditorUtility.DisplayDialog(
                    "All ColorIDs Valid",
                    "All ColorID components reference valid colors.",
                    "OK"
                );
            }
        }

        public static ColorModule GetOrCreateSettings()
        {
            var settings = GlobalSettings.GetModule<ColorModule>();
            if (settings == null)
            {
                settings = CreateInstance<ColorModule>();
                if (!System.IO.Directory.Exists("Assets/_Molca/Resources"))
                    System.IO.Directory.CreateDirectory("Assets/_Molca/Resources");
                UnityEditor.AssetDatabase.CreateAsset(settings, "Assets/_Molca/Resources/ColorManager.asset");
                UnityEditor.AssetDatabase.SaveAssets();
                
                // Add to GlobalSettings if it exists
                var globalSettings = GlobalSettings.GetOrCreateSettings();
                if (globalSettings != null)
                {
                    var modulesList = new List<SettingModule>(globalSettings.modules ?? new SettingModule[0]);
                    modulesList.Add(settings);
                    globalSettings.modules = modulesList.ToArray();
                    UnityEditor.EditorUtility.SetDirty(globalSettings);
                }
            }
            return settings;
        }

        private void OnValidate()
        {
            // Migrate legacy colors if needed
            MigrateLegacyColors();
            
            // Refresh cache when color definitions are modified in editor
            if (Application.isPlaying)
            {
                // In play mode, refresh the cache immediately
                RefreshCache();
            }
            else
            {
                // In edit mode, clear any saved PlayerPrefs for modified colors
                // This ensures that the current definition values take precedence
                foreach (var swatch in _colorSwatches)
                {
                    foreach (var definition in swatch.ColorDefinitions)
                    {
                        if (!string.IsNullOrEmpty(definition.ColorId))
                        {
                            ClearColorFromPlayerPrefs(swatch.SwatchName, definition.ColorId);
                        }
                    }
                }
                
                // Refresh the cache and mark as dirty
                RefreshCache();
                ApplyColorsToColorID();
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }
        #endif
    }
} 