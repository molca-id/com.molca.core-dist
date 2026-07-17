using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Audio
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-audio.png")]
    [CreateAssetMenu(fileName = "Audio Library", menuName = "Molca/Audio/Audio Library", order = 30)]
    public class AudioLibrary : ScriptableObject
    {
        public enum AudioType
        {
            Music,
            SFX,
            Voice
        }

        [SerializeField, FormerlySerializedAs("audioType")] private AudioType _audioType;
        [SerializeField, FormerlySerializedAs("collections")] private List<ScriptableObject> _collections = new List<ScriptableObject>();

        private Dictionary<string, IAudioCollection> _collectionCache;
        private Dictionary<string, Dictionary<string, object>> _entryCache; // collectionName -> entryId -> entry

        public AudioType Type => _audioType;
        
        /// <summary>
        /// Validates that Voice libraries only contain DialogAudioCollections
        /// </summary>
        public bool HasVoiceValidationError
        {
            get
            {
                if (_audioType != AudioType.Voice) return false;
                
                foreach (var collection in _collections)
                {
                    if (collection != null && !(collection is DialogAudioCollection))
                    {
                        return true;
                    }
                }
                return false;
            }
        }
        
        public List<IAudioCollection> GetCollections() 
        {
            var result = new List<IAudioCollection>();
            foreach (var collection in _collections)
            {
                if (collection is IAudioCollection audioCollection)
                {
                    result.Add(audioCollection);
                }
            }
            return result;
        }

        public void Initialize()
        {
            _collectionCache = new Dictionary<string, IAudioCollection>();
            _entryCache = new Dictionary<string, Dictionary<string, object>>();

            foreach (var collection in _collections)
            {
                if (collection != null && collection is IAudioCollection audioCollection)
                {
                    audioCollection.Clear();
                    audioCollection.Initialize();
                    _collectionCache[audioCollection.CollectionName] = audioCollection;

                    // Cache entries by collection name and entry id
                    if (collection is AudioCollection regularCollection)
                    {
                        var collectionEntries = new Dictionary<string, object>();
                        foreach (var entry in regularCollection.GetEntries())
                        {
                            if (!string.IsNullOrEmpty(entry.id))
                            {
                                collectionEntries[entry.id] = entry;
                            }
                        }
                        _entryCache[audioCollection.CollectionName] = collectionEntries;
                    }
                    else if (collection is DialogAudioCollection dialogCollection)
                    {
                        var collectionEntries = new Dictionary<string, object>();
                        foreach (var entry in dialogCollection.GetEntries())
                        {
                            if (!string.IsNullOrEmpty(entry.id))
                            {
                                collectionEntries[entry.id] = entry;
                            }
                        }
                        _entryCache[audioCollection.CollectionName] = collectionEntries;
                    }
                }
            }
        }

        public void Clear()
        {
            foreach (var collection in _collections)
            {
                if (collection != null && collection is IAudioCollection audioCollection)
                {
                    audioCollection.Clear();
                }
            }

            _collectionCache = null;
            _entryCache = null;
        }

        /// <summary>
        /// Gets an entry from a specific collection by collection name and entry id
        /// </summary>
        public AudioCollection.AudioEntry GetEntry(string collectionName, string id)
        {
            if (_entryCache == null)
            {
                Initialize();
            }

            if (_entryCache.TryGetValue(collectionName, out var collectionEntries))
            {
                if (collectionEntries.TryGetValue(id, out var entry))
                {
                    return entry as AudioCollection.AudioEntry;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a LocalizedAudioEntry from a DialogAudioCollection by collection name and entry id
        /// </summary>
        public LocalizedAudioEntry GetLocalizedEntry(string collectionName, string id)
        {
            if (_audioType != AudioType.Voice)
                return null;

            if (_entryCache == null)
            {
                Initialize();
            }

            if (_entryCache.TryGetValue(collectionName, out var collectionEntries))
            {
                if (collectionEntries.TryGetValue(id, out var entry))
                {
                    return entry as LocalizedAudioEntry;
                }
            }

            return null;
        }

        public IAudioCollection GetCollection(string name)
        {
            if (_collectionCache == null)
            {
                Initialize();
            }

            return _collectionCache.TryGetValue(name, out var collection) ? collection : null;
        }

        public void AddCollection(IAudioCollection collection)
        {
            if (collection == null || !(collection is ScriptableObject scriptableObject)) return;
            // Config SOs are read-only at runtime: mutating the serialized list in
            // play mode persists in the editor and silently diverges in a player.
            if (AudioAuthoringGuard.IsRuntime)
            {
                Debug.LogError($"[AudioLibrary] '{name}': AddCollection is an edit-time authoring operation; runtime mutation of the serialized collection list is not allowed. Ignored.");
                return;
            }
            _collections.Add(scriptableObject);
            if (_collectionCache != null)
            {
                _collectionCache[collection.CollectionName] = collection;
            }
        }

        public void RemoveCollection(IAudioCollection collection)
        {
            if (collection == null || !(collection is ScriptableObject scriptableObject)) return;
            if (AudioAuthoringGuard.IsRuntime)
            {
                Debug.LogError($"[AudioLibrary] '{name}': RemoveCollection is an edit-time authoring operation; runtime mutation of the serialized collection list is not allowed. Ignored.");
                return;
            }
            _collections.Remove(scriptableObject);
            if (_collectionCache != null)
            {
                _collectionCache.Remove(collection.CollectionName);
            }
        }

        public string[] GetAllAudioIds()
        {
            if (_collectionCache == null)
            {
                Initialize();
            }
            
            var allIds = new List<string>();
            
            // Add IDs from all collections
            foreach (var collection in GetCollections())
            {
                allIds.AddRange(collection.GetAllAudioIds());
            }
            
            return allIds.ToArray();
        }

        /// <summary>
        /// Manually removes non-DialogAudioCollections from Voice libraries
        /// </summary>
        public void RemoveNonDialogCollections()
        {
            if (AudioAuthoringGuard.IsRuntime)
            {
                Debug.LogError($"[AudioLibrary] '{name}': RemoveNonDialogCollections is an edit-time authoring operation; runtime mutation of the serialized collection list is not allowed. Ignored.");
                return;
            }
            if (_audioType == AudioType.Voice)
            {
                for (int i = _collections.Count - 1; i >= 0; i--)
                {
                    var collection = _collections[i];
                    if (collection != null && !(collection is DialogAudioCollection))
                    {
                        Debug.Log($"AudioLibrary '{name}': Removed incompatible collection '{collection.name}' of type {collection.GetType().Name} from Voice library.");
                        _collections.RemoveAt(i);
                    }
                }
            }
        }
    }
} 