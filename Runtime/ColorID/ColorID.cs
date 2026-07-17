using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using Molca.Attributes;

namespace Molca.ColorID
{
    /// <summary>
    /// Component that applies centralized colors to renderers and UI elements
    /// </summary>
    [AddComponentMenu("Molca/Utilities/Color ID")]
    public class ColorID : MonoBehaviour
    {
        [System.Serializable]
        public class ColorTarget
        {
            public enum TargetType
            {
                Auto,
                Renderer,
                Image,
                Text,
                TextMeshPro,
                RawImage,
                LineRenderer,
                TrailRenderer,
                ParticleSystem
            }

            [SerializeField, FormerlySerializedAs("targetType")] private TargetType _targetType = TargetType.Auto;
            [SerializeField, FormerlySerializedAs("targetComponent")] private Component _targetComponent;
            [SerializeField, FormerlySerializedAs("useAlpha")] private bool _useAlpha = true;
            [SerializeField, FormerlySerializedAs("customAlpha"), HideIf(nameof(_useAlpha))] private float _customAlpha = 1f;

            public TargetType Type => _targetType;
            public Component Component => _targetComponent;
            public bool UseAlpha => _useAlpha;
            public float CustomAlpha => _customAlpha;

            public ColorTarget(TargetType type, bool _useAlpha = true, float _customAlpha = 1f)
            {
                this._targetType = type;
                this._useAlpha = _useAlpha;
                this._customAlpha = _customAlpha;
            }

            /// <summary>
            /// Sets the target component for this color target
            /// </summary>
            /// <param name="component">The component to target</param>
            public void SetTargetComponent(Component component)
            {
                this._targetComponent = component;
            }
        }

        [Header("Color Configuration")]
        [SerializeField, FormerlySerializedAs("swatchName")] private string _swatchName = "Default";
        [SerializeField, FormerlySerializedAs("colorId")] private string _colorId = "Primary";
        [SerializeField, FormerlySerializedAs("applyToChildren")] private bool _applyToChildren = false;

        [SerializeField, FormerlySerializedAs("colorTargets")] private List<ColorTarget> _colorTargets = new List<ColorTarget>();

        private List<Component> _cachedTargets = new List<Component>();

        // Cached so OnDestroy can unsubscribe from the same service instance even
        // if the service registry is already unavailable during teardown.
        private IColorSchemeService _schemeService;

        public string SwatchName => _swatchName;
        public string ColorId => _colorId;

        private async void Start()
        {
            await RuntimeManager.WaitForInitialization();

            // If destroyed during the await, OnDestroy has already run — subscribing
            // now would leak a handler on a dead object into the static event.
            if (this == null) return;

            // Subscribe to color scheme changes
            _schemeService = RuntimeManager.GetService<IColorSchemeService>();
            if (_schemeService != null)
                _schemeService.SchemeChanged += OnSchemeChanged;
            
            // Rebuild cached targets from serialized colorTargets
            RebuildCachedTargets();
            
            // Only rebuild targets if none are configured yet
            // This preserves any manually configured targets
            if (_colorTargets.Count == 0)
            {
                RefreshTargets();
            }
            
            ApplyColors();
        }

        private void OnDestroy()
        {
            // Unsubscribe from color scheme changes
            if (_schemeService != null)
                _schemeService.SchemeChanged -= OnSchemeChanged;
        }

        /// <summary>
        /// Resolves the color provider through <see cref="IColorSchemeService.ActiveScheme"/>
        /// when the service is available (the scheme the user actually switched to),
        /// falling back to the legacy static resolution for objects that run before
        /// bootstrap or in edit mode.
        /// </summary>
        private IColorProvider ResolveColorProvider()
        {
            var scheme = _schemeService?.ActiveScheme;
            if (scheme != null)
                return scheme;
            return ColorModule.ResolveActive();
        }

        /// <summary>
        /// Called when the color scheme changes. Reapplies colors with the new scheme.
        /// </summary>
        /// <param name="newScheme">The new active ColorModule (can be null).</param>
        private void OnSchemeChanged(ColorModule newScheme)
        {
            ApplyColors();
        }
        
        /// <summary>
        /// Rebuilds the cached targets list from the serialized colorTargets
        /// </summary>
        private void RebuildCachedTargets()
        {
            _cachedTargets.Clear();
            foreach (var target in _colorTargets)
            {
                if (target.Component != null)
                {
                    _cachedTargets.Add(target.Component);
                }
            }
        }

        /// <summary>
        /// Refreshes the color targets and reapplies colors while preserving existing configurations
        /// </summary>
        public void Refresh()
        {
            RefreshTargets();
            ApplyColors();
        }

        /// <summary>
        /// Refreshes targets while preserving existing configurations
        /// </summary>
        private void RefreshTargets()
        {
            // Store existing target configurations
            var existingTargets = new Dictionary<Component, ColorTarget>();
            foreach (var target in _colorTargets)
            {
                if (target.Component != null)
                {
                    existingTargets[target.Component] = target;
                }
            }

            // Clear current lists
            _colorTargets.Clear();
            _cachedTargets.Clear();

            // Get components from this GameObject
            AddTargetsFromGameObjectPreservingConfig(gameObject, existingTargets);

            // Get components from children if enabled
            if (_applyToChildren)
            {
                foreach (Transform child in transform)
                {
                    AddTargetsFromGameObjectPreservingConfig(child.gameObject, existingTargets);
                }
            }
        }

        /// <summary>
        /// Applies colors to all detected targets
        /// </summary>
        public void ApplyColors()
        {
            for (int i = 0; i < _colorTargets.Count && i < _cachedTargets.Count; i++)
            {
                ApplyColorToTarget(_colorTargets[i], _cachedTargets[i]);
            }
        }

        /// <summary>
        /// Applies a specific color to a target
        /// </summary>
        /// <param name="target">The color target configuration</param>
        /// <param name="component">The component to apply color to</param>
        private void ApplyColorToTarget(ColorTarget target, Component component)
        {
            if (component == null) return;

            Color color = ResolveColorProvider().GetColor(_swatchName, _colorId);
            
            if (!target.UseAlpha)
            {
                color.a = target.CustomAlpha;
            }

            switch (target.Type)
            {
                case ColorTarget.TargetType.Renderer:
                    ApplyColorToRenderer(component as Renderer, color);
                    break;
                case ColorTarget.TargetType.Image:
                    ApplyColorToImage(component as Image, color);
                    break;
                case ColorTarget.TargetType.RawImage:
                    ApplyColorToRawImage(component as RawImage, color);
                    break;
                case ColorTarget.TargetType.Text:
                    ApplyColorToText(component as Text, color);
                    break;
                case ColorTarget.TargetType.TextMeshPro:
                    ApplyColorToTMPText(component as TMP_Text, color);
                    break;
                case ColorTarget.TargetType.LineRenderer:
                    ApplyColorToLineRenderer(component as LineRenderer, color);
                    break;
                case ColorTarget.TargetType.TrailRenderer:
                    ApplyColorToTrailRenderer(component as TrailRenderer, color);
                    break;
                case ColorTarget.TargetType.ParticleSystem:
                    ApplyColorToParticleSystem(component as ParticleSystem, color);
                    break;
                case ColorTarget.TargetType.Auto:
                    ApplyColorAuto(component, color);
                    break;
            }

            #if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(component);
            }
            #endif
        }

        private void ApplyColorToRenderer(Renderer renderer, Color color)
        {
            if (renderer != null)
            {
                // Only apply colors in play mode to avoid modifying shared materials or creating instances
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    return;
                }
                #endif
                
                if (renderer.material != null)
                {
                    renderer.material.color = color;
                }
            }
        }

        private void ApplyColorToImage(Image image, Color color)
        {
            if (image != null)
            {
                image.color = color;
            }
        }

        private void ApplyColorToRawImage(RawImage rawImage, Color color)
        {
            if (rawImage != null)
            {
                rawImage.color = color;
            }
        }

        private void ApplyColorToText(Text text, Color color)
        {
            if (text != null)
            {
                text.color = color;
            }
        }

        private void ApplyColorToTMPText(TMP_Text tmpText, Color color)
        {
            if (tmpText != null)
            {
                tmpText.color = color;
            }
        }

        private void ApplyColorToLineRenderer(LineRenderer lineRenderer, Color color)
        {
            if (lineRenderer != null)
            {
                lineRenderer.startColor = color;
                lineRenderer.endColor = color;
            }
        }

        private void ApplyColorToTrailRenderer(TrailRenderer trailRenderer, Color color)
        {
            if (trailRenderer != null)
            {
                trailRenderer.startColor = color;
                trailRenderer.endColor = color;
            }
        }

        private void ApplyColorToParticleSystem(ParticleSystem particleSystem, Color color)
        {
            if (particleSystem != null)
            {
                var main = particleSystem.main;
                main.startColor = color;
            }
        }

        private void ApplyColorAuto(Component component, Color color)
        {
            // Try to determine the type and apply color accordingly
            if (component is Renderer renderer)
                ApplyColorToRenderer(renderer, color);
            else if (component is Image image)
                ApplyColorToImage(image, color);
            else if (component is RawImage rawImage)
                ApplyColorToRawImage(rawImage, color);
            else if (component is Text text)
                ApplyColorToText(text, color);
            else if (component is TMP_Text tmpText)
                ApplyColorToTMPText(tmpText, color);
            else if (component is LineRenderer lineRenderer)
                ApplyColorToLineRenderer(lineRenderer, color);
            else if (component is TrailRenderer trailRenderer)
                ApplyColorToTrailRenderer(trailRenderer, color);
            else if (component is ParticleSystem particleSystem)
                ApplyColorToParticleSystem(particleSystem, color);
        }

        /// <summary>
        /// Sets the swatch name and color ID for all targets
        /// </summary>
        /// <param name="swatchName">The swatch name to apply</param>
        /// <param name="colorId">The color ID to apply</param>
        public void SetColor(string _swatchName, string _colorId)
        {
            this._swatchName = _swatchName;
            this._colorId = _colorId;
            
            // Apply colors with the new swatch and color ID
            ApplyColors();
        }

        /// <summary>
        /// Sets the color ID for all targets (uses current swatch name)
        /// Supports composite format: "{swatchName}/{colorId}"
        /// </summary>
        /// <param name="colorId">The color ID to apply, or composite format "{swatchName}/{colorId}"</param>
        public void SetColorId(string _colorId)
        {
            // Check if the colorId contains a "/" indicating composite format
            if (_colorId.Contains("/"))
            {
                var parts = _colorId.Split('/');
                if (parts.Length == 2)
                {
                    this._swatchName = parts[0];
                    this._colorId = parts[1];
                }
                else
                {
                    // Invalid format, just use as colorId
                    this._colorId = _colorId;
                }
            }
            else
            {
                // Simple colorId, use current swatch name
                this._colorId = _colorId;
            }
            
            // Apply colors with the new color ID
            ApplyColors();
        }


        /// <summary>
        /// Gets all available color IDs from the ColorManager
        /// </summary>
        /// <returns>Array of available color IDs</returns>
        public string[] GetAvailableColorIds()
        {
            return ResolveColorProvider().GetAllColorIds();
        }

        private void AddTargetsFromGameObjectPreservingConfig(GameObject targetObject, Dictionary<Component, ColorTarget> existingTargets)
        {
            // Mesh Renderers
            var renderers = targetObject.GetComponents<Renderer>();
            foreach (var renderer in renderers)
            {
                // Check for material safely - use sharedMaterial to avoid creating instances
                if (renderer.sharedMaterial != null)
                {
                    ColorTarget target;
                    if (existingTargets.TryGetValue(renderer, out var existingTarget))
                    {
                        // Preserve existing configuration
                        target = existingTarget;
                    }
                    else
                    {
                        // Create new target with default settings
                        target = new ColorTarget(ColorTarget.TargetType.Renderer);
                        target.SetTargetComponent(renderer);
                    }
                    _colorTargets.Add(target);
                    _cachedTargets.Add(renderer);
                }
            }

            // UI Images
            var images = targetObject.GetComponents<Image>();
            foreach (var image in images)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(image, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.Image);
                    target.SetTargetComponent(image);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(image);
            }

            // UI Raw Images
            var rawImages = targetObject.GetComponents<RawImage>();
            foreach (var rawImage in rawImages)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(rawImage, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.RawImage);
                    target.SetTargetComponent(rawImage);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(rawImage);
            }

            // UI Text
            var texts = targetObject.GetComponents<Text>();
            foreach (var text in texts)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(text, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.Text);
                    target.SetTargetComponent(text);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(text);
            }

            // TextMeshPro
            var tmpTexts = targetObject.GetComponents<TMP_Text>();
            foreach (var tmpText in tmpTexts)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(tmpText, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.TextMeshPro);
                    target.SetTargetComponent(tmpText);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(tmpText);
            }

            // Line Renderers
            var lineRenderers = targetObject.GetComponents<LineRenderer>();
            foreach (var lineRenderer in lineRenderers)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(lineRenderer, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.LineRenderer);
                    target.SetTargetComponent(lineRenderer);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(lineRenderer);
            }

            // Trail Renderers
            var trailRenderers = targetObject.GetComponents<TrailRenderer>();
            foreach (var trailRenderer in trailRenderers)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(trailRenderer, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.TrailRenderer);
                    target.SetTargetComponent(trailRenderer);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(trailRenderer);
            }

            // Particle Systems
            var particleSystems = targetObject.GetComponents<ParticleSystem>();
            foreach (var particleSystem in particleSystems)
            {
                ColorTarget target;
                if (existingTargets.TryGetValue(particleSystem, out var existingTarget))
                {
                    target = existingTarget;
                }
                else
                {
                    target = new ColorTarget(ColorTarget.TargetType.ParticleSystem);
                    target.SetTargetComponent(particleSystem);
                }
                _colorTargets.Add(target);
                _cachedTargets.Add(particleSystem);
            }
        }

        #if UNITY_EDITOR
        private void OnValidate()
        {
            // Minimal validation to prevent crashes
            // Do NOT apply colors or refresh targets automatically
            // This should only happen when explicitly requested through the inspector
            
            // Skip validation during prefab editing, play mode, or when gameObject is not valid
            if (gameObject == null || transform == null || Application.isPlaying)
            {
                return;
            }
            
            // Skip during scene loading/unloading to prevent crashes
            if (!gameObject.scene.isLoaded)
            {
                return;
            }
            
            // Only do minimal validation - ensure swatch name has a default value
            if (string.IsNullOrEmpty(_swatchName))
            {
                _swatchName = "Default";
            }
            
            // Don't validate colorId here - let the editor handle it
            // This prevents cascading OnValidate calls during scene load/save
        }
        #endif
    }
} 