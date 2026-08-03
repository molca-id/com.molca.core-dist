using System;
using Molca;
using Molca.ColorID;
using UnityEngine;
using UnityEngine.UI;

namespace Molca.App.UI
{
    /// <summary>
    /// Simple button that cycles through colour-theme variants.
    /// </summary>
    public class ColorSchemeToggle : MonoBehaviour
    {
        [Header("Optional Visual Feedback")]
        [Tooltip("Optional image to show the current variant icon.")]
        [SerializeField] private Image schemeIcon;
        
        [Tooltip("Icons for each variant, in the theme set's variant order.")]
        [SerializeField] private Sprite[] schemeIcons;

        [Header("Button")]
        [Tooltip("The button to use for toggling. If not set, will try to get from this GameObject.")]
        [SerializeField] private Button toggleButton;

        private IColorThemeService _theme;

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization(destroyCancellationToken);

                _theme = RuntimeManager.GetService<IColorThemeService>();
                if (_theme == null)
                {
                    Debug.LogWarning("ColorSchemeToggle: IColorThemeService not available.");
                    return;
                }

                if (toggleButton == null)
                    toggleButton = GetComponent<Button>();

                if (toggleButton != null)
                    toggleButton.onClick.AddListener(OnToggleClicked);

                // Subscribe to external variant changes to update visuals.
                _theme.ThemeChanged += OnThemeChanged;

                UpdateVisuals(_theme.ActiveVariantId);
            }
            catch (OperationCanceledException)
            {
                // Runtime initialization was cancelled during shutdown.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private void OnDestroy()
        {
            if (toggleButton != null)
                toggleButton.onClick.RemoveListener(OnToggleClicked);

            if (_theme != null)
                _theme.ThemeChanged -= OnThemeChanged;
        }

        private void OnToggleClicked()
        {
            if (_theme == null) return;

            string[] variantIds = _theme.VariantIds;
            if (variantIds == null || variantIds.Length < 2) return;

            int current = FindVariantIndex(variantIds, _theme.ActiveVariantId);
            int next = current < 0 ? 0 : (current + 1) % variantIds.Length;
            _theme.SetVariant(variantIds[next]);
        }

        private void OnThemeChanged(ColorThemeChanged change)
        {
            UpdateVisuals(change.ActiveVariantId);
        }

        private void UpdateVisuals(string activeVariantId)
        {
            if (schemeIcon == null || schemeIcons == null || _theme == null) return;

            int index = FindVariantIndex(_theme.VariantIds, activeVariantId);
            if (index >= 0 && index < schemeIcons.Length && schemeIcons[index] != null)
            {
                schemeIcon.sprite = schemeIcons[index];
            }
        }

        private static int FindVariantIndex(string[] variantIds, string activeVariantId)
        {
            if (variantIds == null || string.IsNullOrEmpty(activeVariantId)) return -1;

            for (int i = 0; i < variantIds.Length; i++)
            {
                if (string.Equals(variantIds[i], activeVariantId, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
    }
}
