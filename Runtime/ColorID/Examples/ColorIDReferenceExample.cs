using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;
using Molca.ColorID;

namespace Molca.ColorID.Examples
{
    /// <summary>
    /// Example script demonstrating how to use ColorIDReference
    /// </summary>
    public class ColorIDReferenceExample : MonoBehaviour
    {
        [Header("Color References")]
        [SerializeField, FormerlySerializedAs("primaryColor")] private ColorIDReference _primaryColor = new ColorIDReference("Primary");
        [SerializeField, FormerlySerializedAs("secondaryColor")] private ColorIDReference _secondaryColor = new ColorIDReference("Secondary");
        [SerializeField, FormerlySerializedAs("accentColor")] private ColorIDReference _accentColor = new ColorIDReference("Accent");
        
        [Header("UI Components")]
        [SerializeField, FormerlySerializedAs("backgroundImage")] private Image _backgroundImage;
        [SerializeField, FormerlySerializedAs("titleText")] private TextMeshProUGUI _titleText;
        [SerializeField, FormerlySerializedAs("actionButton")] private Button _actionButton;
        
        [Header("Renderer Components")]
        [SerializeField, FormerlySerializedAs("targetRenderer")] private Renderer _targetRenderer;
        [SerializeField, FormerlySerializedAs("lineRenderer")] private LineRenderer _lineRenderer;
        
        private void Start()
        {
            ApplyColors();
        }

        /// <summary>
        /// Applies the referenced colors to various components
        /// </summary>
        [ContextMenu("Apply Colors")]
        public void ApplyColors()
        {
            // Apply to UI components
            if (_backgroundImage != null)
                _backgroundImage.color = _primaryColor.Color;
                
            if (_titleText != null)
                _titleText.color = _secondaryColor.Color;
                
            if (_actionButton != null)
            {
                var buttonColors = _actionButton.colors;
                buttonColors.normalColor = _accentColor.Color;
                buttonColors.highlightedColor = _accentColor.GetColorWithAlpha(0.8f);
                buttonColors.pressedColor = _accentColor.GetColorWithAlpha(0.6f);
                _actionButton.colors = buttonColors;
            }
            
            // Apply to renderer components
            if (_targetRenderer != null && _targetRenderer.material != null)
                _targetRenderer.material.color = _primaryColor.Color;
                
            if (_lineRenderer != null)
            {
                _lineRenderer.startColor = _accentColor.Color;
                _lineRenderer.endColor = _accentColor.GetColorWithAlpha(0.5f);
            }
        }

        /// <summary>
        /// Example of runtime color changes
        /// </summary>
        [ContextMenu("Cycle Colors")]
        public void CycleColors()
        {
            string[] availableColors = ColorIDReference.GetAvailableColorIds();
            if (availableColors.Length == 0) return;
            
            // Cycle through available colors
            int currentIndex = System.Array.IndexOf(availableColors, _primaryColor.ColorId);
            int nextIndex = (currentIndex + 1) % availableColors.Length;
            
            _primaryColor.ColorId = availableColors[nextIndex];
            ApplyColors();
        }

        /// <summary>
        /// Example of using ColorIDReference in code
        /// </summary>
        public void CreateDynamicColorReference()
        {
            // Create a new color reference dynamically
            var dynamicColor = new ColorIDReference("Success");
            
            // Use it directly
            if (_backgroundImage != null)
                _backgroundImage.color = dynamicColor;
                
            // Or get with custom alpha
            Color customAlphaColor = dynamicColor.GetColorWithAlpha(0.3f);
            
            // Check if the color ID is valid
            if (dynamicColor.IsValid())
            {
                Debug.Log($"Dynamic color '{dynamicColor.ColorId}' is valid");
            }
        }

        /// <summary>
        /// Example of implicit conversions
        /// </summary>
        public void DemonstrateImplicitConversions()
        {
            // Implicit conversion from string
            ColorIDReference colorRef = "Warning";
            
            // Implicit conversion to Color
            Color color = colorRef;
            
            // Use directly in assignments
            if (_backgroundImage != null)
                _backgroundImage.color = colorRef;
        }

        private void OnValidate()
        {
            // Apply colors when values change in inspector
            if (Application.isPlaying)
            {
                ApplyColors();
            }
        }
    }
} 