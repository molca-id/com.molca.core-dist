using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.Video;
using Molca;
using Molca.Attributes;
using Molca.Networking.Utils;

namespace Molca.App.Media
{
    [Serializable]
    public class MediaInfo
    {
        // Instance API of the network cache (Sprint 5.1 de-static).
        private static ICacheService Cache => RuntimeManager.GetService<ICacheService>();

        public enum Type
        {
            Unknown,
            Image,
            Video,
            Document
        }

        public string name;
        public int id;
        public Type type;
        public bool isAddressable;
        [HideIf(nameof(isAddressable))]
        public string url;
        [ShowIf(nameof(isAddressable))]
        public AssetReference asset;

        [HideInInspector]
        public string mime_type;
        [HideInInspector]
        public int version;

        private UnityEngine.Object _loadedAsset;
        private int _assetRefCount; // tracks current loaded asset reference count

        /// <summary>
        /// Runtime generated media info
        /// </summary>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <param name="type"></param>
        /// <param name="url"></param>
        public MediaInfo (string name, int id, Type type, string url)
        {
            this.name = name;
            this.id = id;
            this.type = type;
            this.isAddressable = false;
            this.url = url;
            this.asset = null;
        }

        public void Init()
        {
            if (mime_type.Contains("image")) type = Type.Image;
            else if (mime_type.Contains("vid")) type = Type.Video;
            else if (mime_type.Contains("pdf")) type = Type.Document;
            else type = Type.Unknown;
        }

        public void Unload()
        {
            Debug.Log($"Unloading {this}");
            if(isAddressable && asset.IsValid())
                asset.ReleaseAsset();
            else if (_loadedAsset != null && --_assetRefCount <= 0)
            {
                UnityEngine.Object.Destroy(_loadedAsset);
                _loadedAsset = null;
            }
        }

        public async Awaitable<Texture2D> GetTexture()
        {
            if (type == Type.Image)
            {
                if (isAddressable)
                {
                    AsyncOperationHandle async;
                    if (asset.OperationHandle.IsValid())
                        async = asset.OperationHandle;
                    else
                        async = asset.LoadAssetAsync<Texture2D>();

                    while (!async.IsDone)
                        await Awaitable.NextFrameAsync();


                    return async.Result as Texture2D;
                }
                else if (_loadedAsset != null)
                {
                    _assetRefCount++;
                    return _loadedAsset as Texture2D;
                }
                else
                {
                    string cacheId = $"image-{id}";
                    if (Cache == null || !Cache.TryGetCache(cacheId, version, out Texture2D txt2d, true)) // try get cached image
                    {
                        _loadedAsset = await RuntimeManager.GetSubsystem<MediaLoader>().GetTexture(url, version, cacheId);
                        if (_loadedAsset == null) // reset url if asset loading failed
                            url = null;
                    }
                    else
                    {
                        _loadedAsset = txt2d;
                    }

                    _assetRefCount++;
                    return _loadedAsset as Texture2D;
                }
            }
            else if (type == Type.Video)
            {
                _assetRefCount++;
                if (_loadedAsset == null)
                    _loadedAsset = await VideoHandler.GetThumbnail(this);
                return _loadedAsset  as Texture2D;
            }

            Debug.LogWarning("Failed to get texture, invalid media type.");
            return null;
        }

        public async Awaitable<bool> PrepareVideo(VideoPlayer vp)
        {
            if (type != Type.Video) return false;
            vp.Stop();
            if (isAddressable)
            {
                Debug.Log("Loading video clip from addressable..");
                AsyncOperationHandle async;
                if (asset.OperationHandle.IsValid())
                    async = asset.OperationHandle;
                else
                    async = asset.LoadAssetAsync<VideoClip>();

                while (!async.IsDone)
                    await Awaitable.NextFrameAsync();

                if(async.Result == null)
                    return false;

                vp.Stop();
                vp.source = VideoSource.VideoClip;
                vp.clip = async.Result as VideoClip;
                vp.Prepare();
                return true;
            }
            else
            {
                string cacheId = $"video-{id}";
                vp.url = await Cache.GetCachePath(cacheId); // try get cached video path
                if (string.IsNullOrEmpty(vp.url))
                {
                    vp.source = VideoSource.Url;
                    vp.url = url;
                }

                if (string.IsNullOrWhiteSpace(vp.url))
                {
                    url = null; // reset url if asset loading failed
                    return false;
                }

                //Debug.Log($"Preparing video URL: {vp.url}");
                vp.Prepare();
                float prepareTime = Time.time; // 5 second timeout to prepare videoplayer
                while (!vp.isPrepared && Time.time - prepareTime < 5f)
                    await Awaitable.NextFrameAsync();

                return vp.isPrepared;
            }
        }

        public async Awaitable<string> GetDocumentUrl()
        {
            string cacheId = $"document-{id}";
            return await RuntimeManager.GetSubsystem<MediaLoader>().GetDocumentPath(url, version, cacheId);
        }
    }
}