using Molca;
using Molca.Settings;
using TMPro;
using UnityEngine;
using System.Collections.Generic;
using Molca.Localization;

namespace Molca.App.UI
{
    [RequireComponent(typeof(TMP_Dropdown))]
    public class LanguageDropdown : MonoBehaviour
    {
        private TMP_Dropdown dropdown;
        private LocalizationModule localizationModule;

        private async void Start()
        {
            await RuntimeManager.WaitForInitialization();

            dropdown = GetComponent<TMP_Dropdown>();

            // Get the LocalizationModule (adjust this to your actual access pattern)
            localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null || localizationModule.Languages == null)
                return;

            var options = new List<TMP_Dropdown.OptionData>();
            foreach (var entry in localizationModule.Languages)
            {
                // Use entry.Code as label, entry.Flag as image
                options.Add(new TMP_Dropdown.OptionData(entry.Name, entry.Flag, Color.white));
            }

            dropdown.options = options;
            dropdown.value = dropdown.options.FindIndex(option => option.text == localizationModule.ActiveLanguageEntry.Name);
            dropdown.RefreshShownValue();

            dropdown.onValueChanged.AddListener(OnLanguageChanged);
        }

        private void OnLanguageChanged(int index)
        {
            LocalizationManager.SetLanguage(localizationModule.Languages[index].Code);
        }
    }
}