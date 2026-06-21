using UnityEngine;
using UnityEngine.Serialization;
using Molca.Settings;

namespace Molca.Audio
{
    [System.Serializable]
    public class AudioReference
    {
        [SerializeField, FormerlySerializedAs("collectionName")] private string _collectionName;
        [SerializeField, FormerlySerializedAs("audioId")] private string _audioId;
        [SerializeField, FormerlySerializedAs("audioType")] private AudioLibrary.AudioType _audioType;
        [SerializeField, FormerlySerializedAs("enabled")] private bool _enabled = true;
        public bool Enabled { get => _enabled; set => _enabled = value; }

        public string CollectionName => _collectionName;
        public string AudioId => _audioId;
        public AudioLibrary.AudioType AudioType => _audioType;

        public AudioReference() { }

        public AudioReference(string _collectionName, string _audioId, AudioLibrary.AudioType _audioType)
        {
            this._collectionName = _collectionName;
            this._audioId = _audioId;
            this._audioType = _audioType;
        }

        public void Play()
        {
            if (!_enabled) return;

            var audio = RuntimeManager.GetSubsystem<AudioManager>();
            if (audio == null)
            {
                Debug.LogError("AudioManager instance not found!");
                return;
            }

            switch (_audioType)
            {
                case AudioLibrary.AudioType.Music:
                    audio.PlayMusic(_collectionName, _audioId);
                    break;
                case AudioLibrary.AudioType.SFX:
                    audio.PlaySFX(_collectionName, _audioId);
                    break;
                case AudioLibrary.AudioType.Voice:
                    audio.PlayVoice(_collectionName, _audioId);
                    break;
            }
        }

        /// <summary>
        /// Plays the audio using a custom AudioSource with correct mixer group assignment
        /// </summary>
        /// <param name="audioSource">The AudioSource to play from</param>
        /// <param name="volume">Volume multiplier (0-1)</param>
        public void PlayWithSource(AudioSource audioSource, float volume = 1f)
        {
            if (!_enabled) return;

            var audio = RuntimeManager.GetSubsystem<AudioManager>();
            if (audio == null)
            {
                Debug.LogError("AudioManager instance not found!");
                return;
            }

            if (audioSource == null)
            {
                Debug.LogError("AudioSource is null!");
                return;
            }

            switch (_audioType)
            {
                case AudioLibrary.AudioType.Music:
                    audio.PlayFromSource(audioSource, _collectionName, _audioId, _audioType, volume);
                    break;
                case AudioLibrary.AudioType.SFX:
                    audio.PlaySFX(_collectionName, _audioId, volume, audioSource);
                    break;
                case AudioLibrary.AudioType.Voice:
                    audio.PlayVoice(_collectionName, _audioId, volume, audioSource);
                    break;
            }
        }

        /// <summary>
        /// Sets up an AudioSource with the correct mixer group for this audio reference
        /// </summary>
        /// <param name="audioSource">The AudioSource to configure</param>
        public void SetupAudioSource(AudioSource audioSource)
        {
            if (!_enabled) return;

            var audio = RuntimeManager.GetSubsystem<AudioManager>();
            if (audio == null)
            {
                Debug.LogError("AudioManager instance not found!");
                return;
            }

            if (audioSource == null)
            {
                Debug.LogError("AudioSource is null!");
                return;
            }

            audio.SetupAudioSource(audioSource, _audioType);
        }
    }
} 