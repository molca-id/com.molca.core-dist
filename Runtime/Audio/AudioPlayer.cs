using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Molca.Audio
{
    [AddComponentMenu("Molca/Audio/Audio Player")]
    public class AudioPlayer : MonoBehaviour
    {
        [System.Serializable]
        public class AudioEvent
        {
            public string name;
            public AudioReference audioReference;
        }

        [System.Serializable]
        public class AudioTrigger
        {
            public string name;
            public AudioEvent audioEvent;
            public TriggerType triggerType;
            public UnityEvent onAudioComplete;

            public enum TriggerType
            {
                OnStart,
                OnEnable,
                OnDisable,
                OnDestroy,
                OnTriggerEnter,
                OnTriggerExit,
                OnCollisionEnter,
                OnCollisionExit,
                OnButtonClick,
                OnPointerEnter,
                OnPointerExit,
                OnPointerClick,
                Manual
            }
        }

        [Header("Audio Source")]
        [SerializeField, FormerlySerializedAs("customAudioSource")] private AudioSource _customAudioSource;
        [SerializeField, FormerlySerializedAs("useCustomAudioSource")] private bool _useCustomAudioSource = false;

        [Header("Audio Events")]
        [SerializeField, FormerlySerializedAs("audioEvents")] private AudioEvent[] _audioEvents;
        
        [Header("Triggers")]
        [SerializeField, FormerlySerializedAs("triggers")] private AudioTrigger[] _triggers;

        private void Start()
        {
            SetupAudioSource();
            SetupTriggers();
            PlayTriggers(AudioTrigger.TriggerType.OnStart);
        }

        private void SetupAudioSource()
        {
            if (_useCustomAudioSource)
            {
                // If custom audio source is not assigned, try to get it from this GameObject
                if (_customAudioSource == null)
                {
                    _customAudioSource = GetComponent<AudioSource>();
                    
                    // If still null, add one
                    if (_customAudioSource == null)
                    {
                        _customAudioSource = gameObject.AddComponent<AudioSource>();
                        Debug.Log($"Added AudioSource component to {gameObject.name} for AudioPlayer");
                    }
                }
            }
        }

        private void OnEnable()
        {
            PlayTriggers(AudioTrigger.TriggerType.OnEnable);
        }

        private void OnDisable()
        {
            PlayTriggers(AudioTrigger.TriggerType.OnDisable);
        }

        private void OnDestroy()
        {
            PlayTriggers(AudioTrigger.TriggerType.OnDestroy);
        }

        private void OnTriggerEnter(Collider other)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnTriggerEnter);
        }

        private void OnTriggerExit(Collider other)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnTriggerExit);
        }

        private void OnCollisionEnter(Collision collision)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnCollisionEnter);
        }

        private void OnCollisionExit(Collision collision)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnCollisionExit);
        }

        private void SetupTriggers()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.triggerType == AudioTrigger.TriggerType.OnButtonClick)
                {
                    var button = GetComponent<Button>();
                    if (button != null)
                    {
                        button.onClick.AddListener(() => PlayAudio(trigger.audioEvent));
                    }
                }
            }
        }

        private void PlayTriggers(AudioTrigger.TriggerType type)
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.triggerType == type)
                {
                    PlayAudio(trigger.audioEvent);
                }
            }
        }

        public void PlayAudio(string eventName)
        {
            var audioEvent = System.Array.Find(_audioEvents, e => e.name == eventName);
            if (audioEvent != null)
            {
                PlayAudio(audioEvent);
            }
            else
            {
                Debug.LogWarning($"Audio event '{eventName}' not found on {gameObject.name}");
            }
        }

        public void PlayAudio(AudioEvent audioEvent)
        {
            if (audioEvent == null || audioEvent.audioReference == null) return;
            
            if (_useCustomAudioSource && _customAudioSource != null)
            {
                // Use custom AudioSource with correct mixer group
                audioEvent.audioReference.PlayWithSource(_customAudioSource);
            }
            else
            {
                // Use AudioManager's built-in sources
                audioEvent.audioReference.Play();
            }
        }

        /// <summary>
        /// Plays audio using the custom AudioSource with specified volume and loop settings
        /// </summary>
        /// <param name="eventName">Name of the audio event to play</param>
        /// <param name="volume">Volume multiplier (0-1)</param>
        /// <param name="loop">Whether to loop the audio (only applies to music)</param>
        public void PlayAudioWithSource(string eventName, float volume = 1f, bool loop = false)
        {
            var audioEvent = System.Array.Find(_audioEvents, e => e.name == eventName);
            if (audioEvent != null)
            {
                PlayAudioWithSource(audioEvent, volume, loop);
            }
            else
            {
                Debug.LogWarning($"Audio event '{eventName}' not found on {gameObject.name}");
            }
        }

        /// <summary>
        /// Plays audio using the custom AudioSource with specified volume and loop settings
        /// </summary>
        /// <param name="audioEvent">The audio event to play</param>
        /// <param name="volume">Volume multiplier (0-1)</param>
        /// <param name="loop">Whether to loop the audio (only applies to music)</param>
        public void PlayAudioWithSource(AudioEvent audioEvent, float volume = 1f, bool loop = false)
        {
            if (audioEvent == null || audioEvent.audioReference == null) return;
            
            if (_customAudioSource != null)
            {
                audioEvent.audioReference.PlayWithSource(_customAudioSource, volume);
            }
            else
            {
                Debug.LogWarning($"Custom AudioSource is not assigned on {gameObject.name}. Using AudioManager's built-in sources.");
                audioEvent.audioReference.Play();
            }
        }

        /// <summary>
        /// Sets up the custom AudioSource with the correct mixer group for the specified audio event
        /// </summary>
        /// <param name="eventName">Name of the audio event</param>
        public void SetupAudioSource(string eventName)
        {
            var audioEvent = System.Array.Find(_audioEvents, e => e.name == eventName);
            if (audioEvent != null)
            {
                SetupAudioSource(audioEvent);
            }
            else
            {
                Debug.LogWarning($"Audio event '{eventName}' not found on {gameObject.name}");
            }
        }

        /// <summary>
        /// Sets up the custom AudioSource with the correct mixer group for the specified audio event
        /// </summary>
        /// <param name="audioEvent">The audio event</param>
        public void SetupAudioSource(AudioEvent audioEvent)
        {
            if (audioEvent == null || audioEvent.audioReference == null) return;
            
            if (_customAudioSource != null)
            {
                audioEvent.audioReference.SetupAudioSource(_customAudioSource);
            }
            else
            {
                Debug.LogWarning($"Custom AudioSource is not assigned on {gameObject.name}");
            }
        }

        /// <summary>
        /// Sets the custom AudioSource to use
        /// </summary>
        /// <param name="audioSource">The AudioSource to use for playback</param>
        public void SetCustomAudioSource(AudioSource audioSource)
        {
            _customAudioSource = audioSource;
            _useCustomAudioSource = audioSource != null;
        }

        /// <summary>
        /// Gets the currently assigned custom AudioSource
        /// </summary>
        /// <returns>The custom AudioSource, or null if not assigned</returns>
        public AudioSource GetCustomAudioSource()
        {
            return _customAudioSource;
        }

        /// <summary>
        /// Enables or disables the use of custom AudioSource
        /// </summary>
        /// <param name="useCustom">Whether to use custom AudioSource</param>
        public void SetUseCustomAudioSource(bool useCustom)
        {
            _useCustomAudioSource = useCustom;
        }

        // IPointerHandler implementations
        public void OnPointerEnter(PointerEventData eventData)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnPointerEnter);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnPointerExit);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            PlayTriggers(AudioTrigger.TriggerType.OnPointerClick);
        }
    }
} 