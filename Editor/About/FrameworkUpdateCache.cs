using System;
using System.Threading;
using Molca.Editor.Addons;
using UnityEngine;

namespace Molca.Editor.About
{
    /// <summary>
    /// One framework-update fetch shared by every surface that wants it (the About section, the optional
    /// activity chip), with a freshness window so reopening the section does not re-dial the control plane.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Same shape as
    /// <see cref="AddonCatalogCache"/>, with a much longer window: releases are published on the order of
    /// weeks, so a six-hour answer is as good as a fresh one and an editor left open all day makes at most a
    /// handful of requests. An explicit Refresh always bypasses it. Editor-lifetime, main thread.
    /// </remarks>
    internal static class FrameworkUpdateCache
    {
        private static readonly TimeSpan Freshness = TimeSpan.FromHours(6);

        private static FrameworkUpdateResponse _response;
        private static string _requestedChannel;
        private static DateTime _fetchedUtc;

        /// <summary>The cached response, or <c>null</c> when nothing has been fetched this editor session.</summary>
        internal static FrameworkUpdateResponse Cached => _response;

        /// <summary>The channel the cached response was actually served on.</summary>
        internal static string CachedChannel => _response?.channel ?? string.Empty;

        /// <summary>True when a fetch would be served from cache rather than the network.</summary>
        /// <param name="channel">The channel that would be requested.</param>
        /// <remarks>
        /// Keyed on the channel that was <em>requested</em>, not the one served: a stable license asking for
        /// beta is answered with stable, and keying on the answer would make that request refetch forever.
        /// </remarks>
        internal static bool IsFresh(string channel) =>
            _response != null && string.Equals(_requestedChannel, channel, StringComparison.Ordinal) &&
            DateTime.UtcNow - _fetchedUtc < Freshness;

        /// <summary>
        /// Returns the cached response, fetching when missing, stale, or when the requested channel changed.
        /// </summary>
        /// <param name="client">Client used when a fetch is required.</param>
        /// <param name="channel">Requested channel; the server caps it at the license ceiling.</param>
        /// <param name="forceRefresh">True to ignore any cached response.</param>
        /// <param name="cancellationToken">Cancels an in-flight fetch.</param>
        internal static async Awaitable<AddonOperationResult<FrameworkUpdateResponse>> GetAsync(
            FrameworkUpdateClient client, string channel, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!forceRefresh && IsFresh(channel))
                return AddonOperationResult<FrameworkUpdateResponse>.Ok(_response);

            var result = await client.GetLatestAsync(channel, cancellationToken);
            if (!result.Success) return result;

            _response = result.Value;
            _requestedChannel = channel;
            _fetchedUtc = DateTime.UtcNow;
            FrameworkUpdatePreferences.RecordCheck(result.Value.latest?.version, CachedChannel, _fetchedUtc);
            return result;
        }

        /// <summary>Drops the cached response so the next read refetches. Used after an upgrade is applied.</summary>
        internal static void Invalidate()
        {
            _response = null;
            _requestedChannel = null;
            _fetchedUtc = default;
        }
    }
}
