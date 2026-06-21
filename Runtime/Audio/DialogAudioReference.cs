using UnityEngine;
using UnityEngine.Serialization;
using Molca.Settings;
using Molca.Localization;
using System.Linq;
using System.Collections.Generic; // Added for Dictionary

namespace Molca.Audio
{
    [System.Serializable]
    public class DialogAudioReference
    {
        [SerializeField, FormerlySerializedAs("collectionName")] private string _collectionName;
        [SerializeField, FormerlySerializedAs("dialogId")] private string _dialogId;
        [SerializeField, FormerlySerializedAs("enabled")] private bool _enabled = false;
        public bool Enabled { get => _enabled; set => _enabled = value; }

        public string CollectionName => _collectionName;
        public string DialogId => _dialogId;
        
        // Safety mechanism to prevent rapid successive calls
        private static Dictionary<string, float> _lastPlayTimes = new Dictionary<string, float>();
        private const float MIN_PLAY_INTERVAL = 0.1f; // Minimum time between play attempts

        public DialogAudioReference() { }

        public DialogAudioReference(string _collectionName, string _dialogId)
        {
            this._collectionName = _collectionName;
            this._dialogId = _dialogId;
        }

        /// <summary>
        /// Gets the DialogAudioCollection for this reference
        /// </summary>
        public DialogAudioCollection GetDialogCollection()
        {
            var audioModule = GlobalSettings.GetModule<AudioModule>();
            if (audioModule?.VoiceLibrary == null) return null;

            return audioModule.VoiceLibrary.GetCollections()
                .OfType<DialogAudioCollection>()
                .FirstOrDefault(c => c.CollectionName == _collectionName);
        }

        /// <summary>
        /// Plays the dialog audio using the current language
        /// </summary>
        public bool PlayDialog()
        {
            if (!_enabled || !IsValid())
                return false;

            // Create a unique key for this dialog
            string dialogKey = $"{_collectionName}_{_dialogId}";
            
            // Prevent rapid successive calls
            if (_lastPlayTimes.TryGetValue(dialogKey, out float lastPlayTime))
            {
                if (Time.time - lastPlayTime < MIN_PLAY_INTERVAL)
                {
                    Debug.LogWarning($"Dialog play request ignored - too soon since last play: {_dialogId}");
                    return false;
                }
            }
            
            _lastPlayTimes[dialogKey] = Time.time;
            RuntimeManager.GetSubsystem<AudioManager>().PlayVoice(_collectionName, _dialogId);
            return true;
        }

        /// <summary>
        /// Checks if the dialog reference is valid and has clips for all available languages
        /// </summary>
        public bool IsValid()
        {
            if (!_enabled || string.IsNullOrEmpty(_collectionName) || string.IsNullOrEmpty(_dialogId))
                return false;

            var dialogCollection = GetDialogCollection();
            if (dialogCollection == null)
                return false;

            var entry = dialogCollection.GetEntry(_dialogId);
            if (entry == null)
                return false;

            var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null)
                return false;

            // Check if we have clips for all available languages
            return localizationModule.LanguageCode.All(lang => entry.HasClipForLanguage(lang));
        }

        /// <summary>
        /// Gets the available languages for this dialog
        /// </summary>
        public string[] GetAvailableLanguages()
        {
            if (string.IsNullOrEmpty(_collectionName) || string.IsNullOrEmpty(_dialogId))
                return new string[0];

            var dialogCollection = GetDialogCollection();
            if (dialogCollection == null)
                return new string[0];

            var entry = dialogCollection.GetEntry(_dialogId);
            if (entry == null)
                return new string[0];

            return entry.GetAvailableLanguages();
        }
        
        /// <summary>
        /// Clears the play time tracking for all dialogs
        /// </summary>
        public static void ClearAllPlayTimeTracking()
        {
            _lastPlayTimes.Clear();
        }
        
        /// <summary>
        /// Clears the play time tracking for this specific dialog
        /// </summary>
        public void ClearThisDialogPlayTimeTracking()
        {
            string dialogKey = $"{_collectionName}_{_dialogId}";
            _lastPlayTimes.Remove(dialogKey);
        }
    }
} 
