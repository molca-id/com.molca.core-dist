using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Molca.ColorID
{
    /// <summary>
    /// Utility class for easy color management operations
    /// </summary>
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

            Color color = ((IColorProvider)ColorModule.ResolveActive()).GetColor(colorId);

            if (component is Renderer renderer)
            {
                if (renderer.material != null)
                    renderer.material.color = color;
            }
            else if (component is Image image)
            {
                image.color = color;
            }
            else if (component is RawImage rawImage)
            {
                rawImage.color = color;
            }
            else if (component is Text text)
            {
                text.color = color;
            }
            else if (component is TMP_Text tmpText)
            {
                tmpText.color = color;
            }
            else if (component is LineRenderer lineRenderer)
            {
                lineRenderer.startColor = color;
                lineRenderer.endColor = color;
            }
            else if (component is TrailRenderer trailRenderer)
            {
                trailRenderer.startColor = color;
                trailRenderer.endColor = color;
            }
            else if (component is ParticleSystem particleSystem)
            {
                var main = particleSystem.main;
                main.startColor = color;
            }
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
        /// <param name="autoDetectTargets">Whether to auto-detect targets</param>
        /// <returns>The created ColorID component</returns>
        public static ColorID CreateColorID(GameObject gameObject, string colorId = "Primary", bool applyToChildren = true, bool autoDetectTargets = true)
        {
            if (gameObject == null) return null;

            var colorID = gameObject.GetComponent<ColorID>();
            if (colorID == null)
            {
                colorID = gameObject.AddComponent<ColorID>();
            }

            // Configure the ColorID component
            colorID.SetColorId(colorId);
            
            // Note: We can't directly set the private fields, but the ColorID component
            // will handle the configuration through its public methods

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

            Color color = ((IColorProvider)ColorModule.ResolveActive()).GetColor(colorId, alpha);
            ApplyColorToComponent(component, color);
        }

        /// <summary>
        /// Applies a color directly to a component
        /// </summary>
        /// <param name="component">The component to apply color to</param>
        /// <param name="color">The color to apply</param>
        public static void ApplyColorToComponent(Component component, Color color)
        {
            if (component == null) return;

            if (component is Renderer renderer)
            {
                if (renderer.material != null)
                    renderer.material.color = color;
            }
            else if (component is Image image)
            {
                image.color = color;
            }
            else if (component is RawImage rawImage)
            {
                rawImage.color = color;
            }
            else if (component is Text text)
            {
                text.color = color;
            }
            else if (component is TMP_Text tmpText)
            {
                tmpText.color = color;
            }
            else if (component is LineRenderer lineRenderer)
            {
                lineRenderer.startColor = color;
                lineRenderer.endColor = color;
            }
            else if (component is TrailRenderer trailRenderer)
            {
                trailRenderer.startColor = color;
                trailRenderer.endColor = color;
            }
            else if (component is ParticleSystem particleSystem)
            {
                var main = particleSystem.main;
                main.startColor = color;
            }
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
            IColorProvider provider = ColorModule.ResolveActive();
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
        public static void RemoveColorIDs(GameObject gameObject, bool includeChildren = true)
        {
            var colorIDs = GetColorIDs(gameObject, includeChildren);
            foreach (var colorID in colorIDs)
            {
                if (colorID != null)
                {
                    Object.DestroyImmediate(colorID);
                }
            }
        }
    }
} 