using UnityEngine;

namespace Molca.Networking.Utils
{
    /// <summary>
    /// Instance API of the network-cache subsystem. Resolve via
    /// <c>RuntimeManager.GetService&lt;ICacheService&gt;()</c> or inject with
    /// <c>[Inject] ICacheService</c>. Replaces the legacy static surface on
    /// <see cref="CacheManager"/>, which remains as obsolete shims.
    /// </summary>
    public interface ICacheService
    {
        /// <summary>Root directory holding all cache files.</summary>
        string CachePath { get; }

        /// <summary>Total size in bytes of all indexed cache files.</summary>
        long CacheSize { get; }

        /// <summary>True once the cache index has been loaded and validated.</summary>
        bool IsReady { get; }

        /// <summary>Returns true if an entry with the given id exists in the index.</summary>
        bool IsCached(string id);

        /// <summary>
        /// Serializes <paramref name="data"/> (Texture2D, AudioClip, or byte[]) to a
        /// cache file and records it in the index under <paramref name="id"/>.
        /// </summary>
        /// <param name="id">Unique cache key; caching fails if it already exists.</param>
        /// <param name="data">The payload to cache.</param>
        /// <param name="version">Version stamp compared by <see cref="TryGetCache{T}"/>.</param>
        /// <param name="ext">Optional file extension; defaults to ".data".</param>
        /// <param name="encryption">Encrypt the file at rest via <c>Encryption</c>.</param>
        Awaitable Cache<T>(string id, T data, int version, string ext = null, bool encryption = false) where T : class;

        /// <summary>
        /// Attempts to load a cached entry. Fails (returns false) on missing id,
        /// outdated version, unreadable file, or unsupported type.
        /// </summary>
        bool TryGetCache<T>(string id, int version, out T result, bool encryption = false) where T : class;

        /// <summary>Resolves the on-disk path for a cached entry, or null if absent.</summary>
        Awaitable<string> GetCachePath(string id);

        /// <summary>Deletes all cache files and clears the index.</summary>
        void ClearCache();

        /// <summary>Returns a unique temp-file path inside the cache directory.</summary>
        string GetTempPath();
    }
}
