using UnityEngine;
using UnityEngine.Serialization;
using Molca.Localization;
using Molca.Events;

namespace Molca.Audio
{
    /// <summary>
    /// Helper class for playing localized dialog audio from DialogAudioReference
    /// </summary>
    public class DialogAudioPlayer : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("audioSource")] private AudioSource _audioSource;
        [SerializeField, FormerlySerializedAs("dialogReference")] private DialogAudioReference _dialogReference;
        
        // Safety mechanism to prevent rapid successive calls
        private bool _isPlaying = false;
        private float _lastPlayTime = 0f;
        private const float MIN_PLAY_INTERVAL = 0.1f; // Minimum time between play attempts

        /// <summary>
        /// Plays the dialog audio from the assigned DialogAudioReference
        /// </summary>
        public void PlayDialog()
        {
            // Prevent rapid successive calls
            if (_isPlaying || Time.time - _lastPlayTime < MIN_PLAY_INTERVAL)
            {
                Debug.LogWarning($"Dialog play request ignored - already playing or too soon since last play: {_dialogReference?.DialogId}");
                return;
            }
            
            Debug.Log("Playing dialog: " + _dialogReference.DialogId);
            _lastPlayTime = Time.time;
            _isPlaying = true;
            
            bool result = _dialogReference.PlayDialog();
            Debug.Log("Dialog played: " + result);
            
            // Reset playing flag after a short delay to allow for async operations
            StartCoroutine(ResetPlayingFlag());
        }
        
        private System.Collections.IEnumerator ResetPlayingFlag()
        {
            yield return new WaitForSeconds(0.1f);
            _isPlaying = false;
        }

        /// <summary>
        /// Plays a dialog audio clip by ID using the current language
        /// </summary>
        public async void PlayFromAudioSource()
        {
            if (_dialogReference == null)
            {
                Debug.LogError("DialogAudioReference is not assigned!");
                return;
            }

            if (_audioSource == null)
            {
                Debug.LogError("AudioSource is not available!");
                return;
            }

            var dialogCollection = _dialogReference.GetDialogCollection();
            if (dialogCollection == null)
            {
                Debug.LogError("DialogAudioCollection not found!");
                return;
            }

            try
            {
                var clip = await dialogCollection.GetLocalizedClip(_dialogReference.DialogId);
                if (clip != null)
                {
                    _audioSource.clip = clip;
                    _audioSource.Play();
                }
                else
                {
                    Debug.LogWarning($"Failed to load dialog clip with ID: {_dialogReference.DialogId}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error playing dialog '{_dialogReference.DialogId}': {e.Message}");
            }
        }

        /// <summary>
        /// Stops the currently playing dialog
        /// </summary>
        public void StopDialog()
        {
            if (_audioSource != null && _audioSource.isPlaying)
            {
                _audioSource.Stop();
            }
        }

        /// <summary>
        /// Checks if a dialog is currently playing
        /// </summary>
        public bool IsPlaying => _audioSource != null && _audioSource.isPlaying;

        /// <summary>
        /// Gets the current playback progress (0-1)
        /// </summary>
        public float PlaybackProgress
        {
            get
            {
                if (_audioSource == null || _audioSource.clip == null)
                    return 0f;
                
                return _audioSource.time / _audioSource.clip.length;
            }
        }

        /// <summary>
        /// Validates the dialog reference setup
        /// </summary>
        public bool ValidateSetup()
        {
            if (_dialogReference == null)
            {
                Debug.LogError("DialogAudioReference is not assigned!");
                return false;
            }

            return _dialogReference.IsValid();
        }

        /// <summary>
        /// Sets a new dialog reference
        /// </summary>
        public void SetDialogReference(DialogAudioReference newReference)
        {
            _dialogReference = newReference;
        }

        /// <summary>
        /// Gets the current dialog reference
        /// </summary>
        public DialogAudioReference GetDialogReference()
        {
            return _dialogReference;
        }

        // Unity Editor helper methods
        #if UNITY_EDITOR
        [ContextMenu("Validate Dialog Reference")]
        private void ValidateInEditor()
        {
            if (ValidateSetup())
            {
                Debug.Log("Dialog reference validation passed!");
            }
        }
        #endif
    }
} 
