using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

namespace Molca.Localization
{
    /// <summary>
    /// Explicit opt-in RTL adapter. It only changes declared targets and never mirrors arbitrary UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LocalizedLayoutDirectionAdapter : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI[] textTargets;
        [SerializeField] private HorizontalLayoutGroup[] horizontalLayouts;

        private void OnEnable()
        {
            LocalizationSettings.SelectedLocaleChanged += OnLocaleChanged;
            Apply(LocalizationManager.CurrentLanguage);
        }

        private void OnDisable()
        {
            LocalizationSettings.SelectedLocaleChanged -= OnLocaleChanged;
        }

        public void Apply(string localeCode)
        {
            var rightToLeft =
                LocalizationManager.GetPresentationProfile(localeCode)?.IsRightToLeft == true;
            Apply(rightToLeft);
        }

        /// <summary>Applies a known direction. Public for previews and deterministic fixtures.</summary>
        public void Apply(bool rightToLeft)
        {
            foreach (var text in textTargets ?? System.Array.Empty<TextMeshProUGUI>())
                if (text != null)
                    text.isRightToLeftText = rightToLeft;
            foreach (var layout in horizontalLayouts ?? System.Array.Empty<HorizontalLayoutGroup>())
            {
                if (layout == null)
                    continue;
                layout.reverseArrangement = rightToLeft;
                layout.childAlignment = Mirror(layout.childAlignment, rightToLeft);
            }
        }

        private void OnLocaleChanged(Locale locale) =>
            Apply(locale?.Identifier.Code);

        private static TextAnchor Mirror(TextAnchor anchor, bool rightToLeft)
        {
            if (!rightToLeft)
                return anchor switch
                {
                    TextAnchor.UpperRight => TextAnchor.UpperLeft,
                    TextAnchor.MiddleRight => TextAnchor.MiddleLeft,
                    TextAnchor.LowerRight => TextAnchor.LowerLeft,
                    _ => anchor,
                };
            return anchor switch
            {
                TextAnchor.UpperLeft => TextAnchor.UpperRight,
                TextAnchor.MiddleLeft => TextAnchor.MiddleRight,
                TextAnchor.LowerLeft => TextAnchor.LowerRight,
                _ => anchor,
            };
        }
    }
}
