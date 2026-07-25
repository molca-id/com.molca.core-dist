using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// One catalog fetch shared by every Add-ons view. Browse and Installed render the same server
    /// response from different angles, so fetching it twice cost two round trips and could show the two
    /// tabs disagreeing about which version is newest.
    /// </summary>
    /// <remarks>
    /// Editor-lifetime cache with a short freshness window: the catalog changes only when someone
    /// publishes, and an explicit Refresh always bypasses it. Cleared after any install or removal so the
    /// post-reload render cannot describe a state the project has already left.
    /// </remarks>
    internal static class AddonCatalogCache
    {
        private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

        private static AddonCatalogResponse _catalog;
        private static string _channel;
        private static DateTime _fetchedUtc;

        /// <summary>The pre-release channel the last fetch requested.</summary>
        internal static string RequestedChannel => _channel ?? AddonChannels.Stable;

        /// <summary>
        /// Returns the cached catalog, fetching when missing, stale, or when the channel changed.
        /// </summary>
        /// <param name="client">Client used when a fetch is required.</param>
        /// <param name="channel">Requested channel; the server caps it at the license's ceiling.</param>
        /// <param name="forceRefresh">True to ignore any cached response.</param>
        /// <param name="cancellationToken">Cancels an in-flight fetch.</param>
        internal static async Awaitable<AddonOperationResult<AddonCatalogResponse>> GetAsync(
            AddonCatalogClient client, string channel, bool forceRefresh, CancellationToken cancellationToken)
        {
            bool usable = _catalog != null && !forceRefresh &&
                          string.Equals(_channel, channel, StringComparison.Ordinal) &&
                          DateTime.UtcNow - _fetchedUtc < Freshness;
            if (usable) return AddonOperationResult<AddonCatalogResponse>.Ok(_catalog);

            var result = await client.GetCatalogAsync(channel, cancellationToken);
            if (result.Success)
            {
                _catalog = result.Value;
                _channel = channel;
                _fetchedUtc = DateTime.UtcNow;
            }
            return result;
        }

        /// <summary>Drops the cached response so the next read refetches.</summary>
        internal static void Invalidate()
        {
            _catalog = null;
            _fetchedUtc = default;
        }
    }

    /// <summary>The pre-release ladder shared by the client and the control plane.</summary>
    internal static class AddonChannels
    {
        internal const string Stable = "stable";
        internal const string Beta = "beta";
        internal const string Internal = "internal";

        internal static readonly string[] Ladder = { Stable, Beta, Internal };

        /// <summary>Rank of a channel, or 0 for anything unrecognized.</summary>
        internal static int Rank(string channel) => Math.Max(0, Array.IndexOf(Ladder, channel ?? Stable));

        /// <summary>The channels a license may request, given the ceiling the server reported.</summary>
        internal static string[] Available(string maxChannel)
        {
            var available = new string[Rank(maxChannel) + 1];
            Array.Copy(Ladder, available, available.Length);
            return available;
        }

        /// <summary>Per-project channel preference; a developer choice, not a license fact.</summary>
        internal static string Preferred
        {
            get => EditorPrefs.GetString(PreferenceKey, Stable);
            set => EditorPrefs.SetString(PreferenceKey, value ?? Stable);
        }

        private const string PreferenceKey = "Molca.Addons.Channel";
    }
}
