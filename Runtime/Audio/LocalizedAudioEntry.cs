using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.AddressableAssets;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Localization;
using Molca.Settings;

namespace Molca.Audio
{
    [Serializable]
    public class LocalizedAudioEntry
    {
        public string id;
        public string description;
        
        [Header("Localized Audio Clips")]
        [SerializeField, FormerlySerializedAs("languageClips")] private List<LanguageAudioClip> _languageClips = new List<LanguageAudioClip>();

        // Cache for loaded assets to prevent multiple loads
        private Dictionary<string, AudioClip> _loadedClips = new Dictionary<string, AudioClip>();

        [Serializable]
        private class LanguageAudioClip
        {
            public string languageCode;
            public AssetReferenceT<AudioClip> clipReference;
        }

        /// <summary>
        /// Edit-time authoring step that rebuilds the per-language clip slots from the
        /// configured languages. <b>Destructive</b>: wipes every authored clip
        /// reference. At runtime it therefore only resets the load cache — the
        /// serialized clip list is asset data and calling this on a loaded asset in
        /// play mode would silently destroy its clip wiring.
        /// </summary>
        public void Initialize()
        {
            if (AudioAuthoringGuard.IsRuntime)
            {
                // Runtime "init" = clear the loaded-clip cache; never the serialized list.
                _loadedClips.Clear();
                return;
            }

            var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null)
            {
                Debug.LogError("LocalizationModule not found in GlobalSettings");
                return;
            }

            // Clear existing clips
            _languageClips.Clear();
            _loadedClips.Clear();

            // Add entries for each available language
            foreach (var languageCode in localizationModule.LanguageCode)
            {
                _languageClips.Add(new LanguageAudioClip { languageCode = languageCode });
            }
        }

        public AssetReferenceT<AudioClip> GetClipReferenceForLanguage(string languageCode)
        {
            var clip = _languageClips.Find(c => c.languageCode == languageCode);
            return clip?.clipReference;
        }

        public void SetClipReference(string languageCode, AssetReferenceT<AudioClip> clipReference)
        {
            var clip = _languageClips.Find(c => c.languageCode == languageCode);
            if (clip != null)
            {
                clip.clipReference = clipReference;
            }
            else
            {
                _languageClips.Add(new LanguageAudioClip 
                { 
                    languageCode = languageCode,
                    clipReference = clipReference
                });
            }
        }

        public bool HasClipForLanguage(string languageCode)
        {
            var clip = _languageClips.Find(c => c.languageCode == languageCode);
            return clip != null && clip.clipReference != null && clip.clipReference.RuntimeKeyIsValid();
        }

        /// <summary>
        /// Gets the audio clip for the current language
        /// </summary>
        public async Awaitable<AudioClip> GetLocalizedClip()
        {
            return await GetClipForLanguage(LocalizationManager.CurrentLanguage);
        }

        /// <summary>
        /// Gets the audio clip for a specific language
        /// </summary>
        public async Awaitable<AudioClip> GetClipForLanguage(string languageCode)
        {
            // Check if we already have the clip loaded
            if (_loadedClips.TryGetValue(languageCode, out var cachedClip))
            {
                return cachedClip;
            }

            var clipRef = GetClipReferenceForLanguage(languageCode);
            if (clipRef == null || !clipRef.RuntimeKeyIsValid())
            {
                Debug.LogWarning($"No valid audio clip reference found for language '{languageCode}' and id '{id}'");
                return null;
            }

            // Check if the asset is already loaded by the AssetReference
            if (clipRef.Asset != null)
            {
                var clip = clipRef.Asset as AudioClip;
                if (clip != null)
                {
                    _loadedClips[languageCode] = clip;
                    return clip;
                }
            }

            // Check if the AssetReference is already being loaded
            if (clipRef.OperationHandle.IsValid())
            {
                // Wait for the existing operation to complete
                await RuntimeManager.AwaitHandle(clipRef.OperationHandle);
                
                if (clipRef.Asset != null)
                {
                    var clip = clipRef.Asset as AudioClip;
                    if (clip != null)
                    {
                        _loadedClips[languageCode] = clip;
                        return clip;
                    }
                }
                
                // If we get here, the operation completed but didn't return a valid asset
                Debug.LogWarning($"AssetReference operation completed but no valid AudioClip found for language '{languageCode}' and id '{id}'");
                return null;
            }

            // Load the asset if not already loaded
            var async = clipRef.LoadAssetAsync();
            await RuntimeManager.AwaitHandle(async);
            
            if (async.Result != null)
            {
                _loadedClips[languageCode] = async.Result;
            }
            
            return async.Result;
        }

        public string[] GetAvailableLanguages()
        {
            return _languageClips
                .Where(c => c.clipReference != null && c.clipReference.RuntimeKeyIsValid())
                .Select(c => c.languageCode)
                .ToArray();
        }

        /// <summary>
        /// Releases the audio clip asset for the current language
        /// </summary>
        public void ReleaseAsset()
        {
            ReleaseAsset(LocalizationManager.CurrentLanguage);
        }

        /// <summary>
        /// Releases the audio clip asset for a specific language
        /// </summary>
        public void ReleaseAsset(string languageCode)
        {
            var clipRef = GetClipReferenceForLanguage(languageCode);
            if (clipRef != null && clipRef.RuntimeKeyIsValid())
            {
                clipRef.ReleaseAsset();
                _loadedClips.Remove(languageCode);
            }
        }

        /// <summary>
        /// Releases all loaded assets
        /// </summary>
        public void ReleaseAllAssets()
        {
            foreach (var clipRef in _languageClips)
            {
                if (clipRef.clipReference != null && clipRef.clipReference.RuntimeKeyIsValid() && clipRef.clipReference.OperationHandle.IsValid())
                {
                    clipRef.clipReference.ReleaseAsset();
                }
            }
            _loadedClips.Clear();
        }
    }
} 