using UnityEngine;
using Molca.Audio;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Plays audio clips when step events occur using the AudioManager system.
    /// Attach this to the same GameObject as a Step component for automatic event hooking.
    /// </summary>
    [AuxiliaryMenu("Base/Audio Player")]
    public class StepAudioPlayer : StepAuxiliary
    {
        [Header("Audio Settings")]
        [Tooltip("Audio reference for begin audio.")]
        public AudioReference beginAudio;
        
        [Tooltip("Audio reference for complete audio.")]
        public AudioReference completeAudio;

        [Tooltip("Audio reference for begin audio.")]
        public DialogAudioReference dialogAudio;
        
        [Header("Playback Options")]
        [Tooltip("Volume for begin audio (0-1).")]
        [Range(0f, 1f)]
        public float beginVolume = 1f;
        
        [Tooltip("Volume for complete audio (0-1).")]
        [Range(0f, 1f)]
        public float completeVolume = 1f;
        
        [Tooltip("Volume multiplier for all audio (0-1).")]
        [Range(0f, 1f)]
        public float globalVolumeMultiplier = 1f;
        
        [Tooltip("If true, will stop any currently playing audio when a new step event occurs.")]
        public bool stopPreviousAudio = true;
        
        [Tooltip("If true, will wait for the previous audio to finish before playing new audio.")]
        public bool waitForAudioToFinish = false;
        
        [Tooltip("If true, will use a local AudioSource instead of the AudioManager's sources.")]
        public bool useLocalAudioSource = false;
        
        private AudioSource _localAudioSource;
        private bool _isPlaying = false;
        private AudioManager _audioManager;

        public override void OnStepBegin()
        {
            PlayAudio(beginAudio, beginVolume, "begin");
            dialogAudio.PlayDialog();
        }

        public override void OnStepCompleted()
        {
            PlayAudio(completeAudio, completeVolume, "complete");
        }
        
        protected override void OnInitialize()
        {
            // Resolve via subsystem registry rather than the obsolete static.
            _audioManager = RuntimeManager.GetSubsystem<AudioManager>();
            if (_audioManager == null)
            {
                Debug.LogError($"StepAudioPlayer requires AudioManager to be available.", Step);
                return;
            }
            
            // Set up local AudioSource if requested
            if (useLocalAudioSource)
            {
                _localAudioSource = GetComponent<AudioSource>();
                if (_localAudioSource == null)
                {
                    _localAudioSource = gameObject.AddComponent<AudioSource>();
                }
                
                // Configure AudioSource
                _localAudioSource.playOnAwake = false;
                _localAudioSource.loop = false;
            }
        }

        public override void OnStepUpdate()
        {
            // Check if audio has finished playing (only for local AudioSource)
            if (useLocalAudioSource && _isPlaying && _localAudioSource != null && !_localAudioSource.isPlaying)
            {
                _isPlaying = false;
            }
        }
        
        private void PlayAudio(AudioReference audioRef, float volume, string eventName)
        {
            if (audioRef == null || !audioRef.Enabled) return;
            
            // Check if we should wait for current audio to finish
            if (waitForAudioToFinish && _isPlaying)
            {
                Debug.Log($"StepAudioPlayer: Waiting for current audio to finish before playing {eventName} audio.", Step);
                return;
            }
            
            // Stop previous audio if requested
            if (stopPreviousAudio && useLocalAudioSource && _localAudioSource != null && _localAudioSource.isPlaying)
            {
                _localAudioSource.Stop();
                _isPlaying = false;
            }
            
            // Apply global volume multiplier
            float finalVolume = volume * globalVolumeMultiplier;
            
            // Play the audio using AudioReference
            if (useLocalAudioSource && _localAudioSource != null)
            {
                // Set up the AudioSource with proper mixer group
                audioRef.SetupAudioSource(_localAudioSource);
                
                // Use local AudioSource with AudioReference
                audioRef.PlayWithSource(_localAudioSource, finalVolume);
                _isPlaying = true;
            }
            else
            {
                // Use AudioReference's built-in play method
                audioRef.Play();
            }
            
            Debug.Log($"StepAudioPlayer: Playing {eventName} audio '{audioRef.AudioId}' from collection '{audioRef.CollectionName}' on {gameObject.name}.", Step);
        }
        
        /// <summary>
        /// Manually play a specific audio reference.
        /// </summary>
        public void PlayCustomAudio(AudioReference audioRef, float volume = 1f)
        {
            PlayAudio(audioRef, volume, "custom");
        }
        
        /// <summary>
        /// Stop the currently playing audio.
        /// </summary>
        public void StopAudio()
        {
            if (useLocalAudioSource && _localAudioSource != null && _localAudioSource.isPlaying)
            {
                _localAudioSource.Stop();
                _isPlaying = false;
            }
        }
        
        /// <summary>
        /// Check if audio is currently playing.
        /// </summary>
        public bool IsPlaying => _isPlaying;
        
        /// <summary>
        /// Get the local AudioSource component (if using local AudioSource).
        /// </summary>
        public AudioSource LocalAudioSource => _localAudioSource;
        
        /// <summary>
        /// Get the AudioManager instance being used.
        /// </summary>
        public AudioManager AudioManager => _audioManager;
    }
} 