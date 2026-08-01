// No `using System;` here: this file already uses UnityEngine.Object unqualified, and importing System
// would make `Object` ambiguous with `object`. The one attribute below is fully qualified instead.
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Utility class for easy color management operations. <b>Deprecated</b> — see the remarks for
    /// per-method replacements.
    /// </summary>
    /// <remarks>
    /// <b>Legacy surface, scheduled for removal in Core 2.0.0.</b> Note that its name collides with
    /// <see cref="UnityEngine.ColorUtility"/>, so a file that has <c>using UnityEngine;</c> and
    /// <c>using Molca.ColorID;</c> must qualify either one.
    /// <para/>
    /// Every colour application here routes through <see cref="ColorTargetApplier"/>, so it uses the same
    /// subtype-specific channels as <see cref="ColorID"/> and never instantiates a material. What is wrong
    /// with it is the shape, not the application: every method resolves through hidden global state, takes
    /// a bare <c>colorId</c> string that no tool can validate, and applies a colour <i>once</i> — so
    /// nothing it touches follows a later variant switch.
    /// <para/>
    /// <b>Replacements.</b> Add a <see cref="ColorThemeBinding"/> to the object and give it a canonical
    /// token; it discovers its own targets, follows variant switches, and is visible to the audit. For a
    /// genuinely one-off colour, resolve explicitly through <see cref="IColorThemeService"/> and assign the
    /// result. <c>LerpColor</c> has no replacement by design: interpolating between two theme colours to
    /// produce a third that is in no variant is exactly what the token contract exists to prevent — bind
    /// the endpoints and animate a material or canvas property instead.
    /// <para/>
    /// The type is deprecated rather than deleted because deleting it would break compiling content; every
    /// method still works exactly as before.
    /// </remarks>
    [System.Obsolete("Molca.ColorID.ColorUtility resolves through hidden global state, applies colour once so it "
              + "does not follow variant switches, and is invisible to the colour audit. Use a "
              + "ColorThemeBinding with a canonical token, or resolve explicitly through "
              + "IColorThemeService. Scheduled for removal in Core 2.0.0.")]
    public static class ColorUtility
    {
        /// <summary>
        /// Applies a color ID to a GameObject and all its children
        /// </summary>
        /// <param name="gameObject">The GameObject to apply colors to</param>
        /// <param name="colorId">The color ID to apply</param>
        /// <param name="includeChildren">Whether to include children</param>
        public static void ApplyColorToGameObject(GameObject gameObject, string colorId, bool includeChildren = true)
        {
            if (gameObject == null) return;

            // Get or add ColorID component
            var colorID = gameObject.GetComponent<ColorID>();
            if (colorID == null)
            {
                colorID = gameObject.AddComponent<ColorID>();
            }

            colorID.SetColorId(colorId);

            // Apply to children if requested
            if (includeChildren)
            {
                foreach (Transform child in gameObject.transform)
                {
                    ApplyColorToGameObject(child.gameObject, colorId, true);
                }
            }
        }

        /// <summary>
        /// Applies a color ID to a specific component
        /// </summary>
        /// <param name="component">The component to apply color to</param>
        /// <param name="colorId">The color ID to apply</param>
        public static void ApplyColorToComponent(Component component, string colorId)
        {
            if (component == null) return;

            Color color = ColorModule.ResolveActiveProvider().GetColor(colorId);
            ApplyColorToComponent(component, color);
        }

        /// <summary>
        /// Applies a color ID to all components of a specific type in a GameObject
        /// </summary>
        /// <typeparam name="T">The type of component to apply color to</typeparam>
        /// <param name="gameObject">The GameObject to search in</param>
        /// <param name="colorId">The color ID to apply</param>
        /// <param name="includeChildren">Whether to include children</param>
        public static void ApplyColorToComponents<T>(GameObject gameObject, string colorId, bool includeChildren = true) where T : Component
        {
            if (gameObject == null) return;

            var components = includeChildren ? 
                gameObject.GetComponentsInChildren<T>() : 
                gameObject.GetComponents<T>();

            foreach (var component in components)
            {
                ApplyColorToComponent(component, colorId);
            }
        }

        /// <summary>
        /// Creates a ColorID component on a GameObject and configures it
        /// </summary>
        /// <param name="gameObject">The GameObject to add ColorID to</param>
        /// <param name="colorId">The default color ID</param>
        /// <param name="applyToChildren">Whether to apply to children</param>
        /// <param name="autoDetectTargets">
        /// Whether to detect colour targets immediately. When <c>false</c> the component is left
        /// with no targets until something calls <see cref="ColorID.Refresh"/> (or its own
        /// <c>Start</c> detects them because none are configured).
        /// </param>
        /// <returns>The created ColorID component</returns>
        /// <remarks>
        /// V1 accepted <paramref name="applyToChildren"/> and <paramref name="autoDetectTargets"/>
        /// and then ignored both, so callers silently got neither behaviour.
        /// </remarks>
        public static ColorID CreateColorID(GameObject gameObject, string colorId = "Primary", bool applyToChildren = true, bool autoDetectTargets = true)
        {
            if (gameObject == null) return null;

            var colorID = gameObject.GetComponent<ColorID>();
            if (colorID == null)
            {
                colorID = gameObject.AddComponent<ColorID>();
            }

            // Set the hierarchy policy before detection so the first scan honours it.
            colorID.ApplyToChildren = applyToChildren;
            colorID.SetColorId(colorId);

            if (autoDetectTargets)
            {
                colorID.Refresh();
            }

            return colorID;
        }

        /// <summary>
        /// Gets all ColorID components in a GameObject hierarchy
        /// </summary>
        /// <param name="gameObject">The root GameObject</param>
        /// <param name="includeChildren">Whether to include children</param>
        /// <returns>Array of ColorID components</returns>
        public static ColorID[] GetColorIDs(GameObject gameObject, bool includeChildren = true)
        {
            if (gameObject == null) return new ColorID[0];

            return includeChildren ? 
                gameObject.GetComponentsInChildren<ColorID>() : 
                gameObject.GetComponents<ColorID>();
        }

        /// <summary>
        /// Refreshes all ColorID components in a GameObject hierarchy
        /// </summary>
        /// <param name="gameObject">The root GameObject</param>
        /// <param name="includeChildren">Whether to include children</param>
        public static void RefreshColorIDs(GameObject gameObject, bool includeChildren = true)
        {
            var colorIDs = GetColorIDs(gameObject, includeChildren);
            foreach (var colorID in colorIDs)
            {
                colorID.Refresh();
            }
        }

        /// <summary>
        /// Applies a color with custom alpha to a component
        /// </summary>
        /// <param name="component">The component to apply color to</param>
        /// <param name="colorId">The color ID to apply</param>
        /// <param name="alpha">The alpha value to use</param>
        public static void ApplyColorToComponent(Component component, string colorId, float alpha)
        {
            if (component == null) return;

            Color color = ColorModule.ResolveActiveProvider().GetColor(colorId, alpha);
            ApplyColorToComponent(component, color);
        }

        /// <summary>
        /// Applies a color directly to a component
        /// </summary>
        /// <param name="component">The component to apply color to</param>
        /// <param name="color">The color to apply</param>
        /// <remarks>
        /// V1 tested <c>component is Renderer</c> first, which matched <see cref="LineRenderer"/>,
        /// <see cref="TrailRenderer"/> and <see cref="SpriteRenderer"/> before their own branches
        /// could run — making those branches unreachable — and then wrote
        /// <c>renderer.material.color</c>, instantiating a material per call.
        /// <see cref="ColorTargetApplier"/> resolves the channel from the most-derived type.
        /// </remarks>
        public static void ApplyColorToComponent(Component component, Color color)
        {
            if (component == null) return;

            ColorTargetApplier.Apply(component, color);
        }

        /// <summary>
        /// Creates a gradient between two color IDs
        /// </summary>
        /// <param name="startColorId">The starting color ID</param>
        /// <param name="endColorId">The ending color ID</param>
        /// <param name="t">The interpolation value (0-1)</param>
        /// <returns>The interpolated color</returns>
        public static Color LerpColor(string startColorId, string endColorId, float t)
        {
            IColorProvider provider = ColorModule.ResolveActiveProvider();
            Color startColor = provider.GetColor(startColorId);
            Color endColor = provider.GetColor(endColorId);
            return Color.Lerp(startColor, endColor, t);
        }

        /// <summary>
        /// Creates a gradient between two color IDs with custom alpha
        /// </summary>
        /// <param name="startColorId">The starting color ID</param>
        /// <param name="endColorId">The ending color ID</param>
        /// <param name="t">The interpolation value (0-1)</param>
        /// <param name="alpha">The alpha value to apply</param>
        /// <returns>The interpolated color with custom alpha</returns>
        public static Color LerpColor(string startColorId, string endColorId, float t, float alpha)
        {
            Color color = LerpColor(startColorId, endColorId, t);
            color.a = alpha;
            return color;
        }

        /// <summary>
        /// Checks if a GameObject has any ColorID components
        /// </summary>
        /// <param name="gameObject">The GameObject to check</param>
        /// <param name="includeChildren">Whether to include children</param>
        /// <returns>True if the GameObject has ColorID components</returns>
        public static bool HasColorID(GameObject gameObject, bool includeChildren = true)
        {
            if (gameObject == null) return false;

            var colorIDs = GetColorIDs(gameObject, includeChildren);
            return colorIDs.Length > 0;
        }

        /// <summary>
        /// Removes all ColorID components from a GameObject hierarchy
        /// </summary>
        /// <param name="gameObject">The root GameObject</param>
        /// <param name="includeChildren">Whether to include children</param>
        /// <remarks>
        /// Destruction is deferred with <see cref="Object.Destroy(Object)"/> at runtime and only
        /// immediate outside play mode. V1 always used <see cref="Object.DestroyImmediate(Object)"/>,
        /// which Unity explicitly warns against at runtime — from a public runtime-callable API it
        /// could tear a component down in the middle of another component's iteration over it.
        /// </remarks>
        public static void RemoveColorIDs(GameObject gameObject, bool includeChildren = true)
        {
            var colorIDs = GetColorIDs(gameObject, includeChildren);
            foreach (var colorID in colorIDs)
            {
                if (colorID == null) continue;

                if (Application.isPlaying)
                    Object.Destroy(colorID);
                else
                    Object.DestroyImmediate(colorID);
            }
        }
    }
} 