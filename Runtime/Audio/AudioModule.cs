using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Audio;
using Molca.Attributes;
using Molca.Settings;

namespace Molca.Audio
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-audio.png")]
    [CreateAssetMenu(fileName = "Audio Settings", menuName = "Molca/Settings/Audio", order = 10)]
    [System.Serializable]
    public class AudioModule : SettingModule
    {
        [SerializeField, FormerlySerializedAs("musicLibrary"), Expandable] private AudioLibrary _musicLibrary;
        [SerializeField, FormerlySerializedAs("sfxLibrary"), Expandable] private AudioLibrary _sfxLibrary;
        [SerializeField, FormerlySerializedAs("voiceLibrary"), Expandable] private AudioLibrary _voiceLibrary;

        public AudioLibrary MusicLibrary => _musicLibrary;
        public AudioLibrary SFXLibrary => _sfxLibrary;
        public AudioLibrary VoiceLibrary => _voiceLibrary;

        [Header("Audio Mixer")]
        public AudioMixer audioMixer;
        public AudioMixerGroup audioMixerMaster;
        public AudioMixerGroup audioMixerBGM;
        public AudioMixerGroup audioMixerSFX;
        public AudioMixerGroup audioMixerVO;

        private const string MASTER_VOLUME = "MasterVolume";
        private const string BGM_VOLUME = "BGMVolume";
        private const string SFX_VOLUME = "SFXVolume";
        private const string VO_VOLUME = "VOVolume";

        public float MasterVolume
        {
            get
            {
                if (audioMixer != null && audioMixer.GetFloat(MASTER_VOLUME, out float volume))
                    return DbToNormalizedVolume(volume);
                return 1f;
            }
        }

        public float MusicVolume
        {
            get
            {
                if (audioMixer != null && audioMixer.GetFloat(BGM_VOLUME, out float volume))
                    return DbToNormalizedVolume(volume);
                return 1f;
            }
        }

        public float SFXVolume
        {
            get
            {
                if (audioMixer != null && audioMixer.GetFloat(SFX_VOLUME, out float volume))
                    return DbToNormalizedVolume(volume);
                return 1f;
            }
        }

        public float VoiceVolume
        {
            get
            {
                if (audioMixer != null && audioMixer.GetFloat(VO_VOLUME, out float volume))
                    return DbToNormalizedVolume(volume);
                return 1f;
            }
        }

        public override void SaveSettings()
        {
            if (audioMixer == null) return;
            
            if (audioMixer.GetFloat(MASTER_VOLUME, out float masterVolume))
                SaveFloat(MASTER_VOLUME, masterVolume);
            if (audioMixer.GetFloat(BGM_VOLUME, out float bgmVolume))
                SaveFloat(BGM_VOLUME, bgmVolume);
            if (audioMixer.GetFloat(SFX_VOLUME, out float sfxVolume))
                SaveFloat(SFX_VOLUME, sfxVolume);
            if (audioMixer.GetFloat(VO_VOLUME, out float voVolume))
                SaveFloat(VO_VOLUME, voVolume);
        }

        public override async void LoadSettings()
        {
            if (audioMixer == null) return;

            // Wait for the audio mixer to be ready
            await Awaitable.NextFrameAsync();

            audioMixer.SetFloat(MASTER_VOLUME, LoadFloat(MASTER_VOLUME, 0f));
            audioMixer.SetFloat(BGM_VOLUME, LoadFloat(BGM_VOLUME, 0f));
            audioMixer.SetFloat(SFX_VOLUME, LoadFloat(SFX_VOLUME, 0f));
            audioMixer.SetFloat(VO_VOLUME, LoadFloat(VO_VOLUME, 0f));
        }

        /// <summary>
        /// Set the master volume.
        /// </summary>
        /// <param name="volume">The volume to set. 0-1</param>
        public void SetMasterVolume(float volume)
        {
            if (audioMixer != null)
            {
                float dbVolume = NormalizedToDbVolume(volume);  
                audioMixer.SetFloat(MASTER_VOLUME, dbVolume);
                SaveFloat(MASTER_VOLUME, dbVolume);
            }
        }

        /// <summary>
        /// Set the music volume.
        /// </summary>
        /// <param name="volume">The volume to set. 0-1</param>
        public void SetMusicVolume(float volume)
        {
            if (audioMixer != null)
            {
                float dbVolume = NormalizedToDbVolume(volume);
                audioMixer.SetFloat(BGM_VOLUME, dbVolume);
                SaveFloat(BGM_VOLUME, dbVolume);
            }
        }

        /// <summary>
        /// Set the SFX volume.
        /// </summary>
        /// <param name="volume">The volume to set. 0-1</param>
        public void SetSFXVolume(float volume)
        {
            if (audioMixer != null)
            {
                float dbVolume = NormalizedToDbVolume(volume);
                audioMixer.SetFloat(SFX_VOLUME, dbVolume);
                SaveFloat(SFX_VOLUME, dbVolume);
            }
        }

        /// <summary>
        /// Set the voice volume.
        /// </summary>
        /// <param name="volume">The volume to set. 0-1</param>
        public void SetVoiceVolume(float volume)
        {
            if (audioMixer != null)
            {
                float dbVolume = NormalizedToDbVolume(volume);
                audioMixer.SetFloat(VO_VOLUME, dbVolume);
                SaveFloat(VO_VOLUME, dbVolume);
            }
        }

        private float NormalizedToDbVolume(float normalizedValue)
        {
            if (normalizedValue <= 0.0001f)
                return -80f; // Unity's "silent" value

            // Convert [0,1] to dB using the inverse of DbToNormalizedVolume
            // normalizedValue = 10^(dB/20) -> dB = 20 * log10(normalizedValue)
            return 20f * Mathf.Log10(Mathf.Clamp01(normalizedValue));
        }

        private float DbToNormalizedVolume(float dbValue)
        {
            return Mathf.Pow(10f, dbValue / 20f);
        }
    }
}