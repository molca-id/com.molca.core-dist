using System;
using Molca;
using Molca.Audio;
using Molca.Localization;
using Molca.Modals;
using UnityEngine;

namespace Molca.App.Testing
{
    public class Test_Playground : MonoBehaviour
    {
        public DialogAudioReference dialogAudioReference;
        public DynamicLocalization dynamicLocalization;
        public LocalizedText text;

        private float updateInterval = 1f;
        private float lastUpdate;
        private LocalizationManager localizationManager;

        private async void Start()
        {
            await RuntimeManager.WaitForInitialization();
            localizationManager = RuntimeManager.GetSubsystem<LocalizationManager>();
            
            // Add debugging information
            Debug.Log($"LocalizationManager found: {localizationManager != null}");
            if (localizationManager != null)
            {
                Debug.Log($"Localization system ready: {localizationManager.IsActive}");
                Debug.Log($"Available languages: {string.Join(", ", localizationManager.GetAvailableLanguages())}");
            }
            
            dynamicLocalization.Init("Test_Playground.Dynamic");
            text.SetLocalizedString(dynamicLocalization.locale);

            PlayDialog();

            for(int i = 0; i < 10; i++)
            {
                await Awaitable.WaitForSecondsAsync(UnityEngine.Random.Range(0.1f, 1f));
                RuntimeManager.GetService<ModalManager>().AddMessage($"Hello, Molca! {i}");
            }

        }

        private void Update()
        {
            if(Time.time - lastUpdate < updateInterval)
                return;

            lastUpdate = Time.time;
            
            foreach(var lang in localizationManager.GetAvailableLanguages())
            {
                switch(lang)
                {
                    case "en":
                        dynamicLocalization.SetTextForLanguage($"English: {DateTime.Now}", "en");
                        break;
                    case "id":
                        dynamicLocalization.SetTextForLanguage($"Indonesia: {DateTime.Now}", "id");
                        break;
                }
            }
        }

        public void PlayDialog()
        {
            if(dialogAudioReference.PlayDialog())
                Debug.Log("Dialog played");
            else
                Debug.Log("Dialog failed to play");
        }

        public void AddMessage()
        {
            RuntimeManager.GetService<ModalManager>().AddMessage($"Hello, Molca! {DateTime.Now}");
        }
    }
}
