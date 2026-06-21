using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.AddressableAssets;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Localization;
using Molca.Settings;
using Molca.Events;

namespace Molca.Audio
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-audio.png")]
    [CreateAssetMenu(fileName = "Dialog Audio Collection", menuName = "Molca/Audio/Dialog Audio Collection", order = 30)]
    public class DialogAudioCollection : ScriptableObject, IAudioCollection
    {
        [SerializeField, FormerlySerializedAs("collectionName")] private string _collectionName;
        [SerializeField, FormerlySerializedAs("description")] private string _description;
        [SerializeField, FormerlySerializedAs("addressableGroupName")] private string _addressableGroupName;
        [SerializeField, FormerlySerializedAs("entries")] private List<LocalizedAudioEntry> _entries = new List<LocalizedAudioEntry>();

        private Dictionary<string, LocalizedAudioEntry> _entryCache;
        private bool _isInitialized = false;

        public string CollectionName => _collectionName;
        public string Description => _description;
        public string AddressableGroupName => _addressableGroupName;
        public List<LocalizedAudioEntry> GetEntries() => _entries;

        private void OnLanguageChanged(string newLanguage)
        {
            // This can be used for preloading or cache management if needed
        }

        public void Initialize()
        {
            if (_isInitialized) return;

            _entryCache = new Dictionary<string, LocalizedAudioEntry>();
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.id))
                {
                    _entryCache[entry.id] = entry;
                }
            }

            // Subscribe to language changes if LocalizationManager is available
            TypedEvents.LanguageChanged.Register(OnLanguageChanged);
            _isInitialized = true;
        }

        public void Clear()
        {
            // Unsubscribe from language changes
            TypedEvents.LanguageChanged.Unregister(OnLanguageChanged);

            // Release all loaded assets from entries
            foreach (var entry in _entries)
            {
                if (entry != null)
                {
                    entry.ReleaseAllAssets();
                }
            }

            _entryCache = null;
            _isInitialized = false;
        }

        public LocalizedAudioEntry GetEntry(string id)
        {
            if (!_isInitialized)
            {
                Initialize();
            }
            
            return _entryCache.TryGetValue(id, out var entry) ? entry : null;
        }

        /// <summary>
        /// Gets the localized audio clip for the specified id and current language
        /// </summary>
        public async Awaitable<AudioClip> GetLocalizedClip(string id)
        {
            var entry = GetEntry(id);
            if (entry == null)
            {
                Debug.LogWarning($"Dialog audio entry with id '{id}' not found in collection '{_collectionName}'");
                return null;
            }

            return await entry.GetLocalizedClip();
        }

        /// <summary>
        /// Gets the audio clip for the specified id and specific language
        /// </summary>
        public async Awaitable<AudioClip> GetClipForLanguage(string id, string languageCode)
        {
            var entry = GetEntry(id);
            if (entry == null)
            {
                Debug.LogWarning($"Dialog audio entry with id '{id}' not found in collection '{_collectionName}'");
                return null;
            }

            return await entry.GetClipForLanguage(languageCode);
        }

        public void AddEntry(string id, Dictionary<string, AssetReferenceT<AudioClip>> languageClips = null)
        {
            if (string.IsNullOrEmpty(id)) return;
            
            var entry = new LocalizedAudioEntry
            {
                id = id
            };

            entry.Initialize();

            if (languageClips != null)
            {
                foreach (var clip in languageClips)
                {
                    entry.SetClipReference(clip.Key, clip.Value);
                }
            }

            _entries.Add(entry);
            if (_entryCache != null)
            {
                _entryCache[id] = entry;
            }
        }

        public void RemoveEntry(LocalizedAudioEntry entry)
        {
            if (entry == null) return;
            _entries.Remove(entry);
            if (_entryCache != null)
            {
                _entryCache.Remove(entry.id);
            }
        }

        public string[] GetAllDialogIds()
        {
            if (_entries == null) return new string[0];
            var ids = new List<string>();
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.id))
                    ids.Add(entry.id);
            }
            return ids.ToArray();
        }

        /// <summary>
        /// Implementation of IAudioCollection interface - returns all dialog IDs
        /// </summary>
        public string[] GetAllAudioIds()
        {
            return GetAllDialogIds();
        }

        /// <summary>
        /// Validates that all entries have clips for all available languages
        /// </summary>
        public bool ValidateEntries()
        {
            var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null)
            {
                Debug.LogError("LocalizationModule not found in GlobalSettings");
                return false;
            }

            bool isValid = true;
            foreach (var entry in _entries)
            {
                foreach (var languageCode in localizationModule.LanguageCode)
                {
                    if (!entry.HasClipForLanguage(languageCode))
                    {
                        Debug.LogWarning($"Entry '{entry.id}' is missing audio clip for language: {languageCode}");
                        isValid = false;
                    }
                }
            }
            return isValid;
        }

        /// <summary>
        /// Preloads all audio clips for the current language
        /// </summary>
        public async Awaitable PreloadCurrentLanguageClips()
        {
            var loadTasks = new List<Awaitable<AudioClip>>();
            foreach (var entry in _entries)
            {
                loadTasks.Add(entry.GetLocalizedClip());
            }

            // Wait for all clips to load
            foreach (var task in loadTasks)
            {
                await task;
            }
        }
    }
} 
