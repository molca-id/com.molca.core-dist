using System;
using UnityEngine;
using Molca;
using Molca.Networking.Auth;
using Molca.Modals;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Networking.Utils;

namespace Molca.App.Media
{
    public class MediaLoader : RuntimeSubsystem
    {
        // Instance API of the network cache (Sprint 5.1 de-static).
        private static ICacheService Cache => RuntimeManager.GetService<ICacheService>();

        // Instance API of the HTTP subsystem — used to send per-request clones so the
        // configured HttpRequestAsset ScriptableObjects are never mutated at runtime.
        private static IHttpClient HttpService => RuntimeManager.GetService<IHttpClient>();

        [Header("HTTP Request Assets")]
        [SerializeField]
        private HttpRequestAsset getDocumentRequest;
        [SerializeField]
        private HttpRequestAsset getTexture2DRequest;
        [SerializeField]
        private HttpRequestAsset getVideoRequest;

        private AuthManager _authManager;
        private ModalManager _modalManager;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            _authManager = RuntimeManager.GetSubsystem<AuthManager>();
            _modalManager = RuntimeManager.GetSubsystem<ModalManager>();

            // Validate HTTP request assets
            if (getDocumentRequest == null || getTexture2DRequest == null || getVideoRequest == null)
            {
                Debug.LogWarning("MediaLoader: Some HTTP request assets are not configured. Media loading may fail.");
            }

            finishCallback?.Invoke(this);
        }

        /// <summary>
        /// Handle image loading, load from cache if it's exist and update if cache's version is lower than provided version.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        public async Awaitable<Texture2D> GetTexture(string url, int version = 1, string cacheId = null)
        {
            if (!_authManager.IsAuthenticated)
                return null;

            if(string.IsNullOrWhiteSpace(url))
            {
                Debug.LogError("URL can't be null.");
                return null;
            }

            cacheId ??= url; // use url as cache in place of cache id if it's null
            if (Cache != null && Cache.TryGetCache(cacheId, version, out Texture2D texture))
                return texture;

            if (getTexture2DRequest == null)
            {
                Debug.LogError("GetTexture2D HTTP request asset is not configured.");
                return null;
            }

            // Clone the read-only asset before mutating — the ScriptableObject must never be written at runtime.
            var request = getTexture2DRequest.CreateRequest();
            request.url = url.Replace("\\", "");
            request.expectedResponseType = ResponseType.Texture;
            request.AddHeader("Authorization", $"Bearer {_authManager.AuthToken}");

            Texture2D data = null;
            var loading = _modalManager.AddLoading(url);
            
            try
            {
                // Use Send with progress callback for better progress tracking
                var tcs = new AwaitableCompletionSource<HttpResponse>();
                
                HttpService.Send(request,
                    onSuccess: (response) => {
                        if (response.texture != null)
                        {
                            data = response.texture;
                        }
                        else
                        {
                            Debug.LogError($"Failed to load texture from url: {url}. Status: {response.statusCode} {response.statusMessage}");
                        }
                        tcs.SetResult(response);
                    },
                    onError: (error) => {
                        Debug.LogError($"Failed to load texture from url: {url}. Error: {error}");
                        tcs.SetException(new Exception(error));
                    },
                    onProgress: (progress) => {
                        loading.Refresh($"Downloading image {progress:P1}", progress);
                    }
                );
                
                await tcs.Awaitable;
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception loading texture from url: {url}.");
                Debug.LogException(e);
            }
            finally
            {
                _modalManager.RemoveLoading(url);
            }
            
            if (data != null) 
                await Cache.Cache(cacheId, data, version, encryption: true);
            return data;
        }

        /// <summary>
        /// Handle video loading, load from cache if it's exist and update if cache's version is lower than provided version.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        public async Awaitable<string> GetVideoPath(string url, int version = 1, string cacheId = null)
        {
            cacheId ??= url; // use url as cache in place of cache id if it's null
            if (ShouldCacheReturnImmediate(url, cacheId, out string result))
                return result;

            if (getVideoRequest == null)
            {
                Debug.LogError("GetVideo HTTP request asset is not configured.");
                return await Cache.GetCachePath(cacheId);
            }

            // Clone the read-only asset before mutating — the ScriptableObject must never be written at runtime.
            var request = getVideoRequest.CreateRequest();
            request.url = url.Replace("\\", "");
            request.expectedResponseType = ResponseType.Binary;
            request.AddHeader("Authorization", $"Bearer {_authManager.AuthToken}");

            byte[] data = null;
            var loading = _modalManager.AddLoading(url);
            
            try
            {
                // Use Send with progress callback for better progress tracking
                var tcs = new AwaitableCompletionSource<HttpResponse>();
                
                HttpService.Send(request,
                    onSuccess: (response) => {
                        if (response.rawData != null)
                        {
                            data = response.rawData;
                        }
                        else
                        {
                            Debug.LogError($"Failed to load video from url: {url}. Status: {response.statusCode} {response.statusMessage}");
                        }
                        tcs.SetResult(response);
                    },
                    onError: (error) => {
                        Debug.LogError($"Failed to load video from url: {url}. Error: {error}");
                        tcs.SetException(new Exception(error));
                    },
                    onProgress: (progress) => {
                        loading.Refresh($"Downloading video {progress:P1}", progress);
                    }
                );
                
                await tcs.Awaitable;
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception loading video from url: {url}.");
                Debug.LogException(e);
            }
            finally
            {
                _modalManager.RemoveLoading(url);
            }
            
            if (data != null) 
                await Cache.Cache(cacheId, data, version, ".mp4", true);
            return await Cache.GetCachePath(cacheId);
        }

        /// <summary>
        /// Handle document loading, load from cache if it's exist and update if cache's version is lower than provided version.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        public async Awaitable<string> GetDocumentPath(string url, int version = 1, string cacheId = null)
        {
            cacheId ??= url; // use url as cache in place of cache id if it's null
            if (ShouldCacheReturnImmediate(url, cacheId, out string result))
                return result;
            else if (Cache != null && Cache.IsCached(cacheId))
            {
                return await Cache.GetCachePath(cacheId);
            }

            if (getDocumentRequest == null)
            {
                Debug.LogError("GetDocument HTTP request asset is not configured.");
                return await Cache.GetCachePath(cacheId);
            }

            // Clone the read-only asset before mutating — the ScriptableObject must never be written at runtime.
            var request = getDocumentRequest.CreateRequest();
            request.url = url.Replace("\\", "");
            request.expectedResponseType = ResponseType.Binary;
            request.AddHeader("Authorization", $"Bearer {_authManager.AuthToken}");

            byte[] data = null;
            var loading = _modalManager.AddLoading(url);
            
            try
            {
                // Use Send with progress callback for better progress tracking
                var tcs = new AwaitableCompletionSource<HttpResponse>();
                
                HttpService.Send(request,
                    onSuccess: (response) => {
                        if (response.rawData != null)
                        {
                            data = response.rawData;
                        }
                        else
                        {
                            Debug.LogError($"Failed to load document from url: {url}. Status: {response.statusCode} {response.statusMessage}");
                        }
                        tcs.SetResult(response);
                    },
                    onError: (error) => {
                        Debug.LogError($"Failed to load document from url: {url}. Error: {error}");
                        tcs.SetException(new Exception(error));
                    },
                    onProgress: (progress) => {
                        loading.Refresh($"Downloading document {progress:P1}", progress);
                    }
                );
                
                await tcs.Awaitable;
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception loading document from url: {url}.");
                Debug.LogException(e);
            }
            finally
            {
                _modalManager.RemoveLoading(url);
            }
            
            if (data != null) 
                await Cache.Cache(cacheId, data, version, ".pdf", true);
            return await Cache.GetCachePath(cacheId);
        }

        private bool ShouldCacheReturnImmediate(string url, string cacheId, out string result)
        {
            result = null; 
            if (string.IsNullOrWhiteSpace(url))
            {
                Debug.LogError("URL can't be null.");
                return true;
            }
            else if (!url.Contains("http")) // check if it's a web url, if not treat it as file path and return immediately
            {
                result = url;
                return true;
            }
            else if (!_authManager.IsAuthenticated)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}