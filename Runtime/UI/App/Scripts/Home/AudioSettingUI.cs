using Molca;
using Molca.Audio;
using Molca.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace Molca.App.UI
{
    public class AudioSettingUI : MonoBehaviour
    {
        [SerializeField] private Slider masterVolume;
        [SerializeField] private Slider musicVolume;
        [SerializeField] private Slider sfxVolume;
        [SerializeField] private Slider voiceVolume;

        private float minValue, maxValue;
        private AudioManager _audioManager;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        private async void Start()
        {
            try
            {
                minValue = masterVolume.minValue;
                maxValue = masterVolume.maxValue;

                await RuntimeManager.WaitForInitialization();
                _audioManager = RuntimeManager.GetSubsystem<AudioManager>();
                if (_audioManager == null) return;

                masterVolume.value = _audioManager.MasterVolume * maxValue;
                musicVolume.value = _audioManager.MusicVolume * maxValue;
                sfxVolume.value = _audioManager.SFXVolume * maxValue;
                voiceVolume.value = _audioManager.VoiceVolume * maxValue;

                masterVolume.onValueChanged.AddListener(OnMasterVolumeChanged);
                musicVolume.onValueChanged.AddListener(OnMusicVolumeChanged);
                sfxVolume.onValueChanged.AddListener(OnSFXVolumeChanged);
                voiceVolume.onValueChanged.AddListener(OnVoiceVolumeChanged);
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnMasterVolumeChanged(float value)
        {
            _audioManager?.SetMasterVolume(Mathf.InverseLerp(minValue, maxValue, value));
        }

        private void OnMusicVolumeChanged(float value)
        {
            _audioManager?.SetMusicVolume(Mathf.InverseLerp(minValue, maxValue, value));
        }

        private void OnSFXVolumeChanged(float value)
        {
            _audioManager?.SetSFXVolume(Mathf.InverseLerp(minValue, maxValue, value));
        }

        private void OnVoiceVolumeChanged(float value)
        {
            _audioManager?.SetVoiceVolume(Mathf.InverseLerp(minValue, maxValue, value));
        }
    }
}