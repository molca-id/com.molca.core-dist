using UnityEngine;
using UnityEngine.Serialization;
using System;

namespace Molca.ColorID
{
    /// <summary>
    /// Simplified color reference field that gets colors from ColorModule
    /// </summary>
    [Serializable]
    public class ColorIDReference
    {
        [SerializeField, FormerlySerializedAs("swatchName")] private string _swatchName = "Default";
        [SerializeField, FormerlySerializedAs("colorId")] private string _colorId = "Primary";

        /// <summary>
        /// The swatch name to reference from ColorModule
        /// </summary>
        public string SwatchName
        {
            get => _swatchName ??= "Default";
            set => _swatchName = value;
        }

        /// <summary>
        /// The color ID to reference from ColorModule
        /// </summary>
        public string ColorId
        {
            get => _colorId ??= "Primary";
            set => _colorId = value;
        }

        /// <summary>
        /// Gets the color from ColorModule
        /// </summary>
        public Color Color
        {
            get => ((IColorProvider)ColorModule.ResolveActive()).GetColor(_swatchName, _colorId);
        }

        /// <summary>
        /// Creates a new ColorIDReference with the specified color ID and swatch
        /// </summary>
        /// <param name="colorId">The color ID to reference</param>
        /// <param name="swatchName">The swatch name (defaults to "Default")</param>
        public ColorIDReference(string _colorId = "Primary", string _swatchName = "Default")
        {
            this._colorId = _colorId;
            this._swatchName = _swatchName;
        }

        /// <summary>
        /// Gets the color with a specific alpha value
        /// </summary>
        /// <param name="alpha">The alpha value to apply</param>
        /// <returns>The color with the specified alpha</returns>
        public Color GetColorWithAlpha(float alpha)
        {
            return ((IColorProvider)ColorModule.ResolveActive()).GetColor(_swatchName, _colorId, Mathf.Clamp01(alpha));
        }

        /// <summary>
        /// Checks if the referenced color ID exists in ColorModule
        /// </summary>
        /// <returns>True if the color ID exists</returns>
        public bool IsValid()
        {
            return ((IColorProvider)ColorModule.ResolveActive()).HasColor(_swatchName, _colorId);
        }

        /// <summary>
        /// Gets all available color IDs from ColorModule
        /// </summary>
        /// <returns>Array of available color IDs</returns>
        public static string[] GetAvailableColorIds()
        {
            return ((IColorProvider)ColorModule.ResolveActive()).GetAllColorIds();
        }

        /// <summary>
        /// Implicit conversion to Color
        /// </summary>
        public static implicit operator Color(ColorIDReference reference)
        {
            return reference.Color;
        }

        /// <summary>
        /// Implicit conversion from string to ColorIDReference
        /// </summary>
        public static implicit operator ColorIDReference(string _colorId)
        {
            return new ColorIDReference(_colorId);
        }
    }
}