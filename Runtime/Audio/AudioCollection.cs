using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.AddressableAssets;
using System;
using System.Collections.Generic;

namespace Molca.Audio
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-audio.png")]
    [CreateAssetMenu(fileName = "Audio Collection", menuName = "Molca/Audio/Audio Collection", order = 30)]
    public class AudioCollection : ScriptableObject, IAudioCollection
    {
        [Serializable]
        public class AudioEntry
        {
            public string id;
            public AssetReferenceT<AudioClip> clipReference;
            public string description;

            // Cache for loaded asset to prevent multiple loads
            private AudioClip _loadedClip = null;

            public async Awaitable<AudioClip> GetClip()
            {
                // Check if we already have the clip loaded
                if (_loadedClip != null)
                {
                    return _loadedClip;
                }

                // Check if the asset is already loaded by the AssetReference
                if (clipReference.Asset != null)
                {
                    var clip = clipReference.Asset as AudioClip;
                    if (clip != null)
                    {
                        _loadedClip = clip;
                        return clip;
                    }
                }

                // Check if the AssetReference is already being loaded
                if (clipReference.OperationHandle.IsValid())
                {
                    // Wait for the existing operation to complete
                    await RuntimeManager.AwaitHandle(clipReference.OperationHandle);
                    
                    if (clipReference.Asset != null)
                    {
                        var clip = clipReference.Asset as AudioClip;
                        if (clip != null)
                        {
                            _loadedClip = clip;
                            return clip;
                        }
                    }
                    
                    // If we get here, the operation completed but didn't return a valid asset
                    Debug.LogWarning($"AssetReference operation completed but no valid AudioClip found for id '{id}'");
                    return null;
                }

                // Load the asset if not already loaded
                var async = clipReference.LoadAssetAsync();
                await RuntimeManager.AwaitHandle(async);
                
                if (async.Result != null)
                {
                    _loadedClip = async.Result;
                }
                
                return async.Result;
            }

            public void ReleaseAsset()
            {
                if (clipReference != null && clipReference.RuntimeKeyIsValid() && clipReference.OperationHandle.IsValid())
                {
                    clipReference.ReleaseAsset();
                    _loadedClip = null;
                }
            }
        }

        [SerializeField, FormerlySerializedAs("collectionName")] private string _collectionName;
        [SerializeField, FormerlySerializedAs("description")] private string _description;
        [SerializeField, FormerlySerializedAs("addressableGroupName")] private string _addressableGroupName;
        [SerializeField, FormerlySerializedAs("entries")] private List<AudioEntry> _entries = new List<AudioEntry>();

        private Dictionary<string, AudioEntry> _entryCache;

        public string CollectionName => _collectionName;
        public string Description => _description;
        public string AddressableGroupName => _addressableGroupName;
        public List<AudioEntry> GetEntries() => _entries;

        public void Initialize()
        {
            _entryCache = new Dictionary<string, AudioEntry>();
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.id))
                {
                    _entryCache[entry.id] = entry;
                }
            }
        }

        public void Clear()
        {
            // Release all loaded assets from entries
            foreach (var entry in _entries)
            {
                if (entry != null)
                {
                    entry.ReleaseAsset();
                }
            }

            _entryCache = null;
        }

        public AudioEntry GetEntry(string id)
        {
            if (_entryCache == null)
            {
                Initialize();
            }

            return _entryCache.TryGetValue(id, out var entry) ? entry : null;
        }

        public void AddEntry(string id, AssetReferenceT<AudioClip> clipReference)
        {
            if (string.IsNullOrEmpty(id) || clipReference == null) return;
            // Config SOs are read-only at runtime: the serialized entry list is
            // authored data (editor mutation would persist; player mutation diverges).
            if (AudioAuthoringGuard.IsRuntime)
            {
                Debug.LogError($"[AudioCollection] '{name}': AddEntry is an edit-time authoring operation; runtime mutation of the serialized entry list is not allowed. Ignored.");
                return;
            }

            var entry = new AudioEntry
            {
                id = id,
                clipReference = clipReference
            };
            _entries.Add(entry);
            if (_entryCache != null)
            {
                _entryCache[id] = entry;
            }
        }

        public void RemoveEntry(AudioEntry entry)
        {
            if (entry == null) return;
            if (AudioAuthoringGuard.IsRuntime)
            {
                Debug.LogError($"[AudioCollection] '{name}': RemoveEntry is an edit-time authoring operation; runtime mutation of the serialized entry list is not allowed. Ignored.");
                return;
            }
            _entries.Remove(entry);
            if (_entryCache != null)
            {
                _entryCache.Remove(entry.id);
            }
        }

        public string[] GetAllAudioIds()
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
    }
} 