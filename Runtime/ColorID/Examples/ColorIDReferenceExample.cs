using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;
using Molca;
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
            
            // Apply to renderer components.
            // Never read _targetRenderer.material here: that instantiates a per-renderer copy of
            // the shared material. The applier writes through a MaterialPropertyBlock instead.
            if (_targetRenderer != null)
                ColorTargetApplier.ApplyToRenderer(_targetRenderer, _primaryColor.Color);


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

            // GetAvailableColorIds returns dotted composites ("Default.Primary"), so the current
            // value has to be composed the same way to be found, and the next value has to be
            // parsed back into both fields. Assigning the composite straight into ColorId (as this
            // example used to) produced "Default.Default.Primary" and resolved to magenta.
            string currentComposite = $"{_primaryColor.SwatchName}.{_primaryColor.ColorId}";
            int currentIndex = System.Array.IndexOf(availableColors, currentComposite);
            int nextIndex = (currentIndex + 1) % availableColors.Length;

            _primaryColor.SetFromComposite(availableColors[nextIndex]);
            ApplyColors();
        }

        /// <summary>
        /// Example of using ColorIDReference in code
        /// </summary>
        public void CreateDynamicColorReference()
        {
            // Create a new color reference dynamically
            var dynamicColor = new ColorIDReference("Success");
            
            // Read the colour explicitly. The implicit ColorIDReference -> Color conversion is deprecated:
            // it hides the fact that this resolves through global state that must already be initialized.
            if (_backgroundImage != null)
                _backgroundImage.color = dynamicColor.Color;


            // Or get with custom alpha
            Color customAlphaColor = dynamicColor.GetColorWithAlpha(0.3f);
            
            // Check if the color ID is valid
            if (dynamicColor.IsValid())
            {
                Debug.Log($"Dynamic color '{dynamicColor.ColorId}' is valid");
            }
        }

        /// <summary>
        /// Example of reading a legacy reference, and of the V2 shape that replaces it.
        /// </summary>
        /// <remarks>
        /// This method used to demonstrate the implicit <c>ColorIDReference</c> -> <see cref="Color"/>
        /// conversion. That conversion is deprecated and scheduled for removal in Core 2.0.0, so the
        /// example shows what to write instead — an example that teaches a deprecated idiom is worse than
        /// no example.
        /// <para/>
        /// The method name is kept as-is despite no longer demonstrating an implicit conversion, because a
        /// UnityEvent in a demo scene may reference it by name and a rename would break that silently.
        /// </remarks>
        public void DemonstrateImplicitConversions()
        {
            // Implicit conversion from string is still fine: it builds a reference, it does not resolve one.
            ColorIDReference colorRef = "Warning";

            // Reading .Color is the like-for-like replacement for the implicit conversion. It still depends
            // on ColorModule (or the V2 compatibility provider) already being initialized.
            if (_backgroundImage != null)
                _backgroundImage.color = colorRef.Color;

            // Better, where a theme service is reachable: failure is a return value, not magenta, and the
            // dependency on an initialized theme is visible at the call site.
            var themeService = RuntimeManager.GetService<IColorThemeService>();
            if (themeService != null && colorRef.TryResolve(themeService, out Color resolved)
                && _titleText != null)
            {
                _titleText.color = resolved;
            }

            // Best, for anything authored rather than computed: hold a ColorTokenReference instead, or drop
            // a ColorThemeBinding on the object so it follows variant switches without any code here.
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