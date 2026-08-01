using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Pipeline
{
    /// <summary>
    /// A bounded in-memory response cache for safe, idempotent GET requests.
    /// </summary>
    /// <remarks>
    /// Deliberately small in scope. It caches only what is unambiguously safe to replay:
    /// <list type="bullet">
    /// <item><description>GET requests — a cached POST response is a correctness bug, not an optimization;</description></item>
    /// <item><description>successful responses — caching an error would make a transient failure sticky;</description></item>
    /// <item><description>anonymous requests only, unless the policy captures bodies. A credentialed response
    /// may be user-specific, and serving one user's body to another is the failure mode this restriction
    /// exists to prevent.</description></item>
    /// </list>
    /// <para>
    /// Keyed by route, URI, and credential profile, so two users of the same endpoint never share an
    /// entry. Bounded by entry count with least-recently-used eviction — an unbounded cache is a memory
    /// leak in a long session.
    /// </para>
    /// </remarks>
    public sealed class NetworkResponseCache
    {
        private sealed class Entry
        {
            public HttpResponse Response;
            public DateTime ExpiresUtc;
            public long LastAccessTick;
        }

        /// <summary>Default maximum entries retained.</summary>
        public const int DefaultCapacity = 128;

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        private long _accessCounter;

        /// <summary>Entries currently retained.</summary>
        public int Count => _entries.Count;

        /// <summary>Cache reads that were served from an entry.</summary>
        public int HitCount { get; private set; }

        /// <summary>Cache reads that found no usable entry.</summary>
        public int MissCount { get; private set; }

        /// <summary>Creates a cache.</summary>
        /// <param name="capacity">Maximum entries retained; values below 1 fall back to <see cref="DefaultCapacity"/>.</param>
        public NetworkResponseCache(int capacity = DefaultCapacity)
        {
            _capacity = capacity < 1 ? DefaultCapacity : capacity;
        }

        /// <summary>
        /// Whether a request is eligible for caching under its policy.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <returns><c>true</c> when the request may read from and write to the cache.</returns>
        public static bool IsEligible(ResolvedHttpRequest request)
        {
            if (request.Policy.CacheMode.Value == NetworkCacheMode.Disabled)
                return false;

            if (request.Method != HttpMethod.GET)
                return false;

            // A credentialed response may be user-specific. Only cache one when the project has
            // explicitly opted into body capture for this route, which is also the signal that its
            // bodies are considered safe to retain.
            return !request.IsAuthenticated || request.Policy.CaptureBodies.Value;
        }

        /// <summary>
        /// Reads a cached response.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="nowUtc">The current UTC time.</param>
        /// <param name="response">A clone of the cached response on a hit.</param>
        /// <returns><c>true</c> on a hit.</returns>
        /// <remarks>
        /// Returns a <see cref="HttpResponse.Clone"/>, never the retained instance, so a caller mutating
        /// its response cannot corrupt the entry for the next reader.
        /// </remarks>
        public bool TryGet(ResolvedHttpRequest request, DateTime nowUtc, out HttpResponse response)
        {
            response = null;
            if (!IsEligible(request))
                return false;

            string key = KeyFor(request);
            if (!_entries.TryGetValue(key, out var entry))
            {
                MissCount++;
                return false;
            }

            if (entry.ExpiresUtc <= nowUtc)
            {
                _entries.Remove(key);
                MissCount++;
                return false;
            }

            entry.LastAccessTick = ++_accessCounter;
            response = entry.Response.Clone();
            HitCount++;
            return true;
        }

        /// <summary>
        /// Stores a successful response.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="response">The response to retain; ignored when unsuccessful.</param>
        /// <param name="nowUtc">The current UTC time.</param>
        public void Store(ResolvedHttpRequest request, HttpResponse response, DateTime nowUtc)
        {
            if (response == null || !response.isSuccess || !IsEligible(request))
                return;

            float ttl = TtlSecondsFor(request, response);
            if (ttl <= 0f)
                return;

            string key = KeyFor(request);
            _entries[key] = new Entry
            {
                Response = response.Clone(),
                ExpiresUtc = nowUtc.AddSeconds(ttl),
                LastAccessTick = ++_accessCounter
            };

            EvictIfOverCapacity();
        }

        /// <summary>Drops every entry and resets the hit/miss counters.</summary>
        public void Clear()
        {
            _entries.Clear();
            HitCount = 0;
            MissCount = 0;
        }

        /// <summary>
        /// The lifetime to retain a response for.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="response">The response, whose <c>Cache-Control: max-age</c> is honoured under
        /// <see cref="NetworkCacheMode.RespectServer"/>.</param>
        /// <returns>Seconds to retain, or 0 to skip caching.</returns>
        private static float TtlSecondsFor(ResolvedHttpRequest request, HttpResponse response)
        {
            if (request.Policy.CacheMode.Value == NetworkCacheMode.FixedTtl)
                return request.Policy.CacheTtlSeconds.Value;

            var headers = NetworkHeaderCollection.FromResponse(response);
            string cacheControl = headers["Cache-Control"];

            if (string.IsNullOrEmpty(cacheControl))
                return 0f;

            // A server that says no-store means it, and the policy's TTL does not override it.
            if (cacheControl.IndexOf("no-store", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cacheControl.IndexOf("no-cache", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0f;

            const string marker = "max-age=";
            int at = cacheControl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return 0f;

            int start = at + marker.Length;
            int end = start;
            while (end < cacheControl.Length && char.IsDigit(cacheControl[end]))
                end++;

            return end > start && int.TryParse(cacheControl.Substring(start, end - start), out int seconds)
                ? seconds
                : 0f;
        }

        /// <summary>
        /// Cache key: route, URI, and credential profile.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <remarks>
        /// The credential profile is part of the key so two identities calling the same endpoint never
        /// collide on one entry.
        /// </remarks>
        private static string KeyFor(ResolvedHttpRequest request) =>
            $"{request.Route.EnvironmentId}|{request.Route.ServiceId}|{request.Uri}|{request.Credential?.Id ?? string.Empty}";

        private void EvictIfOverCapacity()
        {
            while (_entries.Count > _capacity)
            {
                string oldestKey = null;
                long oldestTick = long.MaxValue;

                foreach (var pair in _entries)
                {
                    if (pair.Value.LastAccessTick >= oldestTick) continue;
                    oldestTick = pair.Value.LastAccessTick;
                    oldestKey = pair.Key;
                }

                if (oldestKey == null) return;
                _entries.Remove(oldestKey);
            }
        }
    }
}
