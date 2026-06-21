using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using Newtonsoft.Json;
using System.Linq;
using Molca.Utils;

namespace Molca.Networking.Utils
{
    /// <summary>
    /// Network-cache subsystem. New code should use the <see cref="ICacheService"/>
    /// instance API (resolve via <c>RuntimeManager.GetService&lt;ICacheService&gt;()</c>);
    /// the static members remain as obsolete compatibility shims.
    /// </summary>
    public class CacheManager : RuntimeSubsystem, ICacheService
    {
        private static CacheManager _instance;
        private const string CACHE_PATH = "Network Cache";
        private Dictionary<string, CacheData> _caches = new Dictionary<string, CacheData>();
        private long _cacheSize;
        // Guards every read/write of _caches and _cacheSize. Network-thread provider
        // callbacks (WS/SSE/SocketIO) can drive cache writes concurrently with the
        // main thread, which would otherwise corrupt the dictionary or the size counter.
        private readonly object _cacheLock = new object();

        [SerializeField, FormerlySerializedAs("cacheFlags")] private CachingSelection _cacheFlags;
        [SerializeField, FormerlySerializedAs("clearCacheOnExit")] private bool _clearCacheOnExit;

        [Obsolete("Use ICacheService.CachePath (RuntimeManager.GetService<ICacheService>()).")]
        public static string CachePath => CachePathInternal;
        [Obsolete("Use ICacheService.CacheSize (RuntimeManager.GetService<ICacheService>()).")]
        public static long CacheSize => _instance != null ? _instance.CacheSizeInternal : 0;
        [Obsolete("Use ICacheService.IsReady (RuntimeManager.GetService<ICacheService>()).")]
        public static bool IsReady => _instance != null;
        [Obsolete("Use ICacheService.IsCached (RuntimeManager.GetService<ICacheService>()).")]
        public static bool IsCached(string id) => _instance != null && _instance.IsCachedInternal(id);

        private long CacheSizeInternal { get { lock (_cacheLock) return _cacheSize; } }
        private bool IsCachedInternal(string id) { lock (_cacheLock) return _caches.ContainsKey(id); }

        // The path doesn't depend on instance state; shared by shim and instance API.
        private static string CachePathInternal => Path.Combine(Application.persistentDataPath, CACHE_PATH);

        [Flags]
        public enum CachingSelection
        {
            None = 0,
            Texture = 1 << 0,
            AudioClip = 1 << 1,
            Data = 1 << 2
        }

        public override async Awaitable InitializeAsync(System.Threading.CancellationToken cancellationToken)
        {
            await RequestPermissions();
            await InitializeCache();

            _instance = this;
        }

        public override void Teardown()
        {
            // Drop the legacy-shim singleton so a torn-down subsystem can't be reached.
            if (_instance == this)
                _instance = null;
            base.Teardown();
        }

        private async Awaitable RequestPermissions()
        {
#if UNITY_ANDROID
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageWrite))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageWrite);
            }
#endif
            await Awaitable.NextFrameAsync();
        }

        private async Awaitable InitializeCache()
        {
            if (!Directory.Exists(CachePathInternal))
                Directory.CreateDirectory(CachePathInternal);

            if (!PlayerPrefs.HasKey(CACHE_PATH))
                return;

            LoadCacheData();
            await ValidateCacheFiles();
        }

        private void LoadCacheData()
        {
            // Corrupt or truncated JSON must not break initialization — recover with
            // an empty index (files are revalidated/orphaned harmlessly).
            try
            {
                _caches = JsonConvert.DeserializeObject<Dictionary<string, CacheData>>(PlayerPrefs.GetString(CACHE_PATH));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CacheManager] Cache index is corrupt, resetting: {e.Message}");
                _caches = null;
            }

            if (_caches == null)
            {
                _caches = new Dictionary<string, CacheData>();
                PlayerPrefs.DeleteKey(CACHE_PATH);
            }
            Debug.Log($"Cache count: {_caches.Count}");
        }

        private async Awaitable ValidateCacheFiles()
        {
            int index = 0;
            while (index < _caches.Count)
            {
                string key = _caches.Keys.ElementAt(index);
                if (File.Exists(_caches[key].path))
                {
                    _cacheSize += new FileInfo(_caches[key].path).Length;
                    index++;
                }
                else
                {
                    Debug.LogWarning($"File doesn't exist at cache path: {_caches[key].path}");
                    _caches.Remove(key);
                }

                if (index % 10 == 0) // Process in chunks to avoid freezing
                    await Awaitable.NextFrameAsync();
            }
            Debug.Log($"Loaded cache count: {_caches.Count}");
        }

        #region Instance API (ICacheService)

        // Explicit implementations: the legacy statics keep these names
        // (protected-zone rule), so the instance API lives on the interface.

        string ICacheService.CachePath => CachePathInternal;
        long ICacheService.CacheSize => CacheSizeInternal;
        bool ICacheService.IsReady => _instance == this;
        bool ICacheService.IsCached(string id) => IsCachedInternal(id);

        Awaitable ICacheService.Cache<T>(string id, T data, int version, string ext, bool encryption)
            => CacheCore(id, data, version, ext, encryption);

        bool ICacheService.TryGetCache<T>(string id, int version, out T result, bool encryption)
            => TryGetCacheCore(id, version, out result, encryption);

        Awaitable<string> ICacheService.GetCachePath(string id) => GetCachePathCore(id);
        void ICacheService.ClearCache() => ClearCacheCore();
        string ICacheService.GetTempPath() => GetTempPathInternal();

        // Path generation has no instance state; shared by the shim, the instance
        // API, and in-assembly callers (CacheData).
        internal static string GetTempPathInternal() => Path.Combine(CachePathInternal, Path.GetRandomFileName());

        private async Awaitable CacheCore<T>(string id, T data, int version, string ext, bool encryption) where T : class
        {
            if (!ValidateCacheRequest(id, data))
                return;

            byte[] bytes = ConvertToBytes(data);
            if (bytes == null)
                return;

            string filename = GetFilename(id, ext);
            string cachePath = Path.Combine(CachePathInternal, filename);

            await SaveCache(cachePath, bytes, encryption);
            StoreCacheData(id, cachePath, version, encryption, bytes.LongLength);
        }

        private bool ValidateCacheRequest<T>(string id, T data) where T : class
        {
            if (data == null)
            {
                Debug.LogError("Caching failed, data can't be null.");
                return false;
            }

            lock (_cacheLock)
            {
                if (_caches.ContainsKey(id))
                {
                    Debug.LogWarning($"Cache with id: \"{id}\" already exist");
                    return false;
                }
            }

            return true;
        }

        private byte[] ConvertToBytes<T>(T data) where T : class
        {
            if (_cacheFlags.HasFlag(CachingSelection.Texture) && data is Texture2D texture)
                return texture.EncodeToJPG();

            if (_cacheFlags.HasFlag(CachingSelection.AudioClip) && data is AudioClip audioClip)
                return AudioUtility.GetByteArrayFromAudioClip(audioClip);

            if (_cacheFlags.HasFlag(CachingSelection.Data) && data is byte[] bytes)
                return bytes;

            Debug.LogError($"Caching failed, type is not supported: {typeof(T)}");
            return null;
        }

        private static async Awaitable SaveCache(string cachePath, byte[] bytes, bool encryption)
        {
            await File.WriteAllBytesAsync(cachePath, encryption ? Encryption.EncryptData(bytes) : bytes);
        }

        private void StoreCacheData(string id, string cachePath, int version, bool encryption, long size)
        {
            lock (_cacheLock)
            {
                // Re-check under the lock: a concurrent caller may have raced the
                // pre-write ValidateCacheRequest check and already stored this id.
                if (_caches.ContainsKey(id))
                {
                    Debug.LogWarning($"Cache with id: \"{id}\" already exist");
                    return;
                }
                _caches.Add(id, new CacheData(cachePath, version, encryption));
                _cacheSize += size;
            }
            Debug.Log($"Cache saved at path: {cachePath}");
        }

        private bool TryGetCacheCore<T>(string id, int version, out T result, bool encryption) where T : class
        {
            result = null;

            // Snapshot the entry under the lock; file I/O happens outside it.
            string cachePath;
            lock (_cacheLock)
            {
                if (!_caches.TryGetValue(id, out var entry))
                {
                    Debug.LogWarning($"Cache with id: \"{id}\" doesn't exist");
                    return false;
                }
                if (entry.version < version)
                {
                    Debug.Log($"Cache with id: \"{id}\" is outdated, need udpate.");
                    return false;
                }
                cachePath = entry.path;
            }

            byte[] data;
            try
            {
                data = encryption ? Encryption.DecryptData(File.ReadAllBytes(cachePath)) : File.ReadAllBytes(cachePath);
            }
            catch (Exception e)
            {
                // Deleted/locked/undecryptable file — treat as a cache miss and drop the entry.
                Debug.LogWarning($"[CacheManager] Failed to read cache \"{id}\": {e.Message}");
                lock (_cacheLock) _caches.Remove(id);
                return false;
            }

            if (typeof(T) == typeof(Texture2D))
                result = GetTexture(data) as T;
            else if (typeof(T) == typeof(AudioClip))
                result = AudioUtility.CreateAudioClipFromByteArray(data) as T;
            else if (typeof(T) == typeof(byte[]))
                result = data as T;

            return result != null;
        }

        private async Awaitable<string> GetCachePathCore(string id)
        {
            CacheData entry;
            lock (_cacheLock)
            {
                if (!_caches.TryGetValue(id, out entry))
                {
                    Debug.LogWarning($"No cache with key: {id}");
                    return null;
                }
            }
            return await entry.GetPath();
        }

        private void ClearCacheCore()
        {
            // Detach the entries under the lock, then delete files outside it.
            List<CacheData> entries;
            lock (_cacheLock)
            {
                entries = new List<CacheData>(_caches.Values);
                _caches.Clear();
                _cacheSize = 0;
            }

            foreach (var cache in entries)
            {
                try
                {
                    File.Delete(cache.path);
                    Debug.Log($"Cache cleared at path: {cache.path}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CacheManager] Failed to delete cache file {cache.path}: {e.Message}");
                }
            }
        }

        #endregion

        #region Legacy static API (obsolete shims)

        [Obsolete("Use ICacheService.Cache (RuntimeManager.GetService<ICacheService>()).")]
        public static async Awaitable Cache<T>(string id, T data, int version, string ext = null, bool encryption = false) where T : class
        {
            if (_instance == null)
            {
                Debug.LogError("Caching isn't ready.");
                return;
            }
            await _instance.CacheCore(id, data, version, ext, encryption);
        }

        [Obsolete("Use ICacheService.TryGetCache (RuntimeManager.GetService<ICacheService>()).")]
        public static bool TryGetCache<T>(string id, int version, out T result, bool encryption = false) where T : class
        {
            result = null;
            if (_instance == null)
            {
                Debug.LogError("Caching isn't ready.");
                return false;
            }
            return _instance.TryGetCacheCore(id, version, out result, encryption);
        }

        [Obsolete("Use ICacheService.GetCachePath (RuntimeManager.GetService<ICacheService>()).")]
        public static async Awaitable<string> GetCachePath(string id)
        {
            if (_instance == null)
            {
                Debug.LogError("Caching isn't ready.");
                return null;
            }
            return await _instance.GetCachePathCore(id);
        }

        [Obsolete("Use ICacheService.ClearCache (RuntimeManager.GetService<ICacheService>()).")]
        public static void ClearCache()
        {
            _instance?.ClearCacheCore();
        }

        [Obsolete("Use ICacheService.GetTempPath (RuntimeManager.GetService<ICacheService>()).")]
        public static string GetTempPath() => GetTempPathInternal();

        #endregion

        private static Texture2D GetTexture(byte[] textureData)
        {
            Texture2D cachedTexture = new Texture2D(16, 16); // Set temporary width and height
            if (!cachedTexture.LoadImage(textureData))
                Debug.Log($"Failed to load texture, byte size: {textureData.Length}");
            return cachedTexture;
        }

        private void OnApplicationQuit()
        {
            CleanupCache();
            SaveCacheData();
        }

        private void OnApplicationFocus(bool focus)
        {
            if (focus)
                return;

            foreach (var cache in SnapshotCacheValues())
                cache.Clear();
        }

        private void CleanupCache()
        {
            foreach (var cache in SnapshotCacheValues())
                cache.Clear();

            if (_clearCacheOnExit)
                ClearCacheCore();
        }

        private List<CacheData> SnapshotCacheValues()
        {
            lock (_cacheLock)
                return new List<CacheData>(_caches.Values);
        }

        private void SaveCacheData()
        {
            if (!_clearCacheOnExit)
            {
                string json;
                int count;
                lock (_cacheLock)
                {
                    json = JsonConvert.SerializeObject(_caches);
                    count = _caches.Count;
                }
                PlayerPrefs.SetString(CACHE_PATH, json);
                PlayerPrefs.Save();
                Debug.Log($"Saved cache count: {count}");
            }
        }

        private static string GetFilename(string id, string ext = null)
        {
            return Path.ChangeExtension(Path.GetRandomFileName(), string.IsNullOrEmpty(ext) ? ".data" : ext);
        }
    }
}
