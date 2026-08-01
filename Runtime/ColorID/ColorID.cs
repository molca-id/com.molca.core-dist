using System;
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
                ParticleSystem,

                // Appended, never reordered: this enum is serialized by ordinal, so inserting
                // above any existing entry would silently re-target 194 shipped components.
                // Before this existed, sprites were collected as generic renderers and coloured
                // through renderer.material — which is not where a SpriteRenderer's tint lives.
                SpriteRenderer
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

        // Cached so OnDestroy can unsubscribe from the same service instance even
        // if the service registry is already unavailable during teardown.
        private IColorSchemeService _schemeService;

        // Present only in a V2 project; supplies the legacy-to-canonical translation for this
        // component's serialized (swatch, colorId) pair.
        private IColorThemeService _themeService;

        public string SwatchName => _swatchName;
        public string ColorId => _colorId;

        /// <summary>
        /// The authored target list, read-only.
        /// </summary>
        /// <remarks>
        /// Exposed for migration tooling, which needs each target's own <see cref="ColorTarget.UseAlpha"/>
        /// and <see cref="ColorTarget.CustomAlpha"/> to produce an equivalent
        /// <see cref="ColorThemeBinding"/> — that alpha is per target, and collapsing it to one value per
        /// component would change what renders. Read-only because the list is rebuilt by
        /// <see cref="Refresh"/>; mutating it from outside would be overwritten without warning.
        /// </remarks>
        public IReadOnlyList<ColorTarget> ColorTargets => _colorTargets;

        /// <summary>
        /// Whether target detection covers the complete descendant hierarchy as well as this
        /// GameObject. Changing it takes effect on the next <see cref="Refresh"/>.
        /// </summary>
        public bool ApplyToChildren
        {
            get => _applyToChildren;
            set => _applyToChildren = value;
        }

        // async void is permitted only as a Unity entry point, and only as a thin shim that
        // cannot let an exception escape into Unity's synchronization context unobserved.
        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();

                // If destroyed during the await, OnDestroy has already run — subscribing
                // now would leak a handler on a dead object into the static event.
                if (this == null) return;

                // Subscribe to color scheme changes. One subscription covers both generations:
                // in V2 the manager also raises SchemeChanged, so this component reapplies on a
                // variant switch without knowing V2 exists.
                _schemeService = RuntimeManager.GetService<IColorSchemeService>();
                if (_schemeService != null)
                    _schemeService.SchemeChanged += OnSchemeChanged;

                _themeService = RuntimeManager.GetService<IColorThemeService>();

                // Only detect targets if none are configured yet.
                // This preserves any manually configured targets.
                if (_colorTargets.Count == 0)
                {
                    RefreshTargets();
                }

                ApplyColors();
            }
            catch (OperationCanceledException)
            {
                // Bootstrap was torn down (application quit during initialization).
                // Cancellation is not an error — exit quietly.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from color scheme changes
            if (_schemeService != null)
                _schemeService.SchemeChanged -= OnSchemeChanged;
        }

        /// <summary>
        /// Resolves the provider this component's <c>(swatch, colorId)</c> pair looks up against.
        /// </summary>
        /// <remarks>
        /// Ordered most-specific first:
        /// <list type="number">
        /// <item><description>
        /// the V2 theme service's legacy adapter, which translates this component's pair to a canonical
        /// token through the theme set's alias map — the path a migrated project takes;
        /// </description></item>
        /// <item><description>
        /// the active <see cref="ColorModule"/> from <see cref="IColorSchemeService"/> — a legacy
        /// project after bootstrap;
        /// </description></item>
        /// <item><description>
        /// static resolution, for objects running before bootstrap or in edit mode. This also picks up
        /// the V2 override, so an edit-mode preview matches the running application.
        /// </description></item>
        /// </list>
        /// </remarks>
        private IColorProvider ResolveColorProvider()
        {
            var legacyProvider = _themeService?.LegacyProvider;
            if (legacyProvider != null)
                return legacyProvider;

            var scheme = _schemeService?.ActiveScheme;
            if (scheme != null)
                return scheme;

            return ColorModule.ResolveActiveProvider();
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
        /// <remarks>
        /// When <c>_applyToChildren</c> is set this walks the <b>complete descendant hierarchy</b>,
        /// matching the authoring label and documentation. V1 processed only immediate children.
        /// </remarks>
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

            _colorTargets.Clear();

            // Get components from this GameObject
            AddTargetsFromGameObjectPreservingConfig(gameObject, existingTargets);

            // Get components from the full descendant hierarchy if enabled
            if (_applyToChildren)
            {
                AddTargetsFromDescendants(transform, existingTargets);
            }
        }

        /// <summary>
        /// Recursively collects targets from every descendant of <paramref name="parent"/>.
        /// </summary>
        /// <param name="parent">The transform whose descendants are scanned (exclusive).</param>
        /// <param name="existingTargets">Previously configured targets, preserved by component.</param>
        private void AddTargetsFromDescendants(Transform parent,
            Dictionary<Component, ColorTarget> existingTargets)
        {
            foreach (Transform child in parent)
            {
                AddTargetsFromGameObjectPreservingConfig(child.gameObject, existingTargets);
                AddTargetsFromDescendants(child, existingTargets);
            }
        }

        /// <summary>
        /// Applies colors to all configured targets
        /// </summary>
        /// <remarks>
        /// Each target carries its own component reference, so a target whose component has been
        /// removed is skipped in place and cannot shift another target's configuration onto the
        /// wrong component. V1 walked a parallel cache list that omitted null entries while
        /// indexing both lists together, which is exactly how that skew happened.
        /// </remarks>
        public void ApplyColors()
        {
            // Resolve once: the colour is the same for every target on this component, and
            // resolving per target multiplied the missing-key warning for unresolved pairs.
            Color resolved = ResolveColorProvider().GetColor(_swatchName, _colorId);

            for (int i = 0; i < _colorTargets.Count; i++)
            {
                ApplyColorToTarget(_colorTargets[i], resolved);
            }
        }

        /// <summary>
        /// Applies a specific color to a target
        /// </summary>
        /// <param name="target">The color target configuration, carrying its own component.</param>
        /// <param name="resolved">The colour resolved for this component's swatch/ID pair.</param>
        private void ApplyColorToTarget(ColorTarget target, Color resolved)
        {
            var component = target?.Component;
            if (component == null) return;

            Color color = resolved;
            if (!target.UseAlpha)
            {
                color.a = Mathf.Clamp01(target.CustomAlpha);
            }

            // The configured target type narrows *which* component we expected, but the channel
            // is always chosen from the component's real most-derived type. That keeps a
            // mis-typed target (e.g. Renderer configured on a SpriteRenderer) correct instead of
            // routing it through the wrong channel.
            ColorApplyOutcome outcome = target.Type == ColorTarget.TargetType.Renderer
                ? ColorTargetApplier.ApplyToRenderer(component as Renderer, color)
                : ColorTargetApplier.Apply(component, color);

            switch (outcome)
            {
                case ColorApplyOutcome.UnsupportedTarget:
                    Debug.LogWarning($"[ColorID] '{name}': no colour channel is supported for " +
                                     $"target component of type '{component.GetType().Name}'. " +
                                     "Remove the target or use a supported component type.", this);
                    break;
                case ColorApplyOutcome.MissingShaderProperty:
                    Debug.LogWarning($"[ColorID] '{name}': the shared material on " +
                                     $"'{component.name}' has neither a '_BaseColor' nor a " +
                                     "'_Color' shader property, so the colour cannot be applied.",
                                     this);
                    break;
            }

            #if UNITY_EDITOR
            // Only dirty targets whose colour actually lives in serialized data. The generic
            // renderer path writes a MaterialPropertyBlock, which edit mode does not persist —
            // dirtying the scene for it would mark files changed with nothing to save.
            if (!Application.isPlaying && outcome == ColorApplyOutcome.Applied)
            {
                UnityEditor.EditorUtility.SetDirty(component);
            }
            #endif
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
            // One parser for every composite spelling in the codebase. V1 accepted only
            // "Swatch/Color" here while the provider emitted and accepted "Swatch.Color",
            // so a dotted value round-tripped from GetAllColorIds() was stored as a bare ID
            // and then failed lookup.
            if (TryParseComposite(_colorId, out string parsedSwatch, out string parsedColorId))
            {
                this._swatchName = parsedSwatch;
                this._colorId = parsedColorId;
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
        /// Parses a composite colour identifier into its swatch and colour parts.
        /// </summary>
        /// <param name="composite">
        /// A composite identifier in either supported spelling — <c>"Swatch/Color"</c> (used by the
        /// editor drawers and <see cref="SetColorId"/>) or <c>"Swatch.Color"</c> (used by
        /// <see cref="ColorModule"/>'s cache keys and <c>GetAllColorIds()</c>).
        /// </param>
        /// <param name="swatchName">The parsed swatch name, or <c>null</c> when not composite.</param>
        /// <param name="colorId">The parsed colour ID, or <c>null</c> when not composite.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="composite"/> is a well-formed composite identifier with
        /// exactly one separator and non-empty parts on both sides; otherwise <c>false</c>, meaning
        /// the caller should treat the input as a bare colour ID.
        /// </returns>
        public static bool TryParseComposite(string composite, out string swatchName, out string colorId)
        {
            swatchName = null;
            colorId = null;

            if (string.IsNullOrEmpty(composite)) return false;

            // Last separator wins so a swatch name is never split by a colour ID that
            // happens to contain the other delimiter.
            int separator = composite.LastIndexOfAny(CompositeSeparators);
            if (separator <= 0 || separator >= composite.Length - 1) return false;

            swatchName = composite.Substring(0, separator);
            colorId = composite.Substring(separator + 1);
            return true;
        }

        /// <summary>Both accepted composite separators, in one place.</summary>
        private static readonly char[] CompositeSeparators = { '/', '.' };


        /// <summary>
        /// Gets all available color IDs from the ColorManager
        /// </summary>
        /// <returns>Array of available color IDs</returns>
        public string[] GetAvailableColorIds()
        {
            return ResolveColorProvider().GetAllColorIds();
        }

        /// <summary>
        /// Collects every supported colour target on <paramref name="targetObject"/>, reusing the
        /// previously authored <see cref="ColorTarget"/> for a component that is still present.
        /// </summary>
        /// <remarks>
        /// Each component is claimed by exactly one target type. Specialised renderer subtypes
        /// (sprite, line, trail, particle) are skipped by the generic renderer pass so a single
        /// component can never be collected twice under two different type configurations.
        /// </remarks>
        private void AddTargetsFromGameObjectPreservingConfig(GameObject targetObject, Dictionary<Component, ColorTarget> existingTargets)
        {
            // Order matters only for readability now that claims are exclusive.
            AddTypedTargets<Image>(targetObject, ColorTarget.TargetType.Image, existingTargets);
            AddTypedTargets<RawImage>(targetObject, ColorTarget.TargetType.RawImage, existingTargets);
            AddTypedTargets<Text>(targetObject, ColorTarget.TargetType.Text, existingTargets);
            AddTypedTargets<TMP_Text>(targetObject, ColorTarget.TargetType.TextMeshPro, existingTargets);
            AddTypedTargets<SpriteRenderer>(targetObject, ColorTarget.TargetType.SpriteRenderer, existingTargets);
            AddTypedTargets<LineRenderer>(targetObject, ColorTarget.TargetType.LineRenderer, existingTargets);
            AddTypedTargets<TrailRenderer>(targetObject, ColorTarget.TargetType.TrailRenderer, existingTargets);
            AddTypedTargets<ParticleSystem>(targetObject, ColorTarget.TargetType.ParticleSystem, existingTargets);

            // Generic renderers last, and only those no specialised type already owns.
            var renderers = targetObject.GetComponents<Renderer>();
            foreach (var renderer in renderers)
            {
                if (ColorTargetApplier.IsSpecializedRenderer(renderer)) continue;

                // sharedMaterial, never material: reading .material instantiates a copy.
                if (renderer.sharedMaterial == null) continue;

                AddTarget(renderer, ColorTarget.TargetType.Renderer, existingTargets);
            }
        }

        /// <summary>
        /// Adds a target for every <typeparamref name="T"/> component on <paramref name="targetObject"/>.
        /// </summary>
        /// <typeparam name="T">The component type claimed by <paramref name="type"/>.</typeparam>
        private void AddTypedTargets<T>(GameObject targetObject, ColorTarget.TargetType type,
            Dictionary<Component, ColorTarget> existingTargets) where T : Component
        {
            var components = targetObject.GetComponents<T>();
            foreach (var component in components)
            {
                AddTarget(component, type, existingTargets);
            }
        }

        /// <summary>
        /// Appends a target for <paramref name="component"/>, preserving its previous configuration
        /// (target type, alpha policy) when one was already authored for that exact component.
        /// </summary>
        private void AddTarget(Component component, ColorTarget.TargetType type,
            Dictionary<Component, ColorTarget> existingTargets)
        {
            if (existingTargets.TryGetValue(component, out var existingTarget))
            {
                _colorTargets.Add(existingTarget);
                return;
            }

            var target = new ColorTarget(type);
            target.SetTargetComponent(component);
            _colorTargets.Add(target);
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