using System;
using Molca;
using Molca.ColorID;
using TMPro;
using UnityEngine;
using System.Collections.Generic;

namespace Molca.App.UI
{
    /// <summary>
    /// Dropdown UI component for switching between colour-theme variants.
    /// </summary>
    [RequireComponent(typeof(TMP_Dropdown))]
    public class ColorSchemeDropdown : MonoBehaviour
    {
        [Tooltip("Optional icons for each variant, in the theme set's variant order.")]
        [SerializeField] private Sprite[] schemeIcons;

        private TMP_Dropdown _dropdown;
        private IColorThemeService _theme;

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization(destroyCancellationToken);

                _dropdown = GetComponent<TMP_Dropdown>();
                _theme = RuntimeManager.GetService<IColorThemeService>();
                if (_theme == null)
                {
                    Debug.LogWarning("ColorSchemeDropdown: IColorThemeService not available.");
                    return;
                }

                if (_theme.VariantIds.Length == 0)
                {
                    Debug.LogWarning("ColorSchemeDropdown: No color theme variants are available.");
                    return;
                }

                PopulateDropdown();

                _dropdown.onValueChanged.AddListener(OnVariantSelected);

                // Subscribe to external variant changes to keep the dropdown in sync.
                _theme.ThemeChanged += OnExternalThemeChanged;
            }
            catch (OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnDestroy()
        {
            if (_dropdown != null)
                _dropdown.onValueChanged.RemoveListener(OnVariantSelected);

            if (_theme != null)
                _theme.ThemeChanged -= OnExternalThemeChanged;
        }

        private void PopulateDropdown()
        {
            string[] variantIds = _theme.VariantIds;
            var options = new List<TMP_Dropdown.OptionData>(variantIds.Length);

            for (int i = 0; i < variantIds.Length; i++)
            {
                Sprite icon = (schemeIcons != null && i < schemeIcons.Length) ? schemeIcons[i] : null;
                var variant = _theme.ThemeSet?.GetVariant(variantIds[i]);
                string label = variant != null && !string.IsNullOrWhiteSpace(variant.DisplayName)
                    ? variant.DisplayName
                    : variantIds[i];
                options.Add(new TMP_Dropdown.OptionData(label, icon, Color.white));
            }

            _dropdown.options = options;
            int activeIndex = FindVariantIndex(variantIds, _theme.ActiveVariantId);
            if (activeIndex >= 0)
                _dropdown.SetValueWithoutNotify(activeIndex);
            _dropdown.RefreshShownValue();
        }

        private void OnVariantSelected(int index)
        {
            string[] variantIds = _theme?.VariantIds;
            if (variantIds == null || index < 0 || index >= variantIds.Length) return;

            _theme.SetVariant(variantIds[index]);
        }

        /// <summary>
        /// Called when the variant is changed externally (e.g., via code or another UI).
        /// Keeps the dropdown in sync.
        /// </summary>
        private void OnExternalThemeChanged(ColorThemeChanged change)
        {
            if (_dropdown == null || _theme == null) return;

            int activeIndex = FindVariantIndex(_theme.VariantIds, change.ActiveVariantId);
            if (activeIndex < 0 || _dropdown.value == activeIndex) return;

            _dropdown.SetValueWithoutNotify(activeIndex);
            _dropdown.RefreshShownValue();
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
