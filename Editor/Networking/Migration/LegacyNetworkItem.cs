using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Object = UnityEngine.Object;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>Which legacy artifact a scanned item came from.</summary>
    public enum LegacyNetworkItemKind
    {
        /// <summary>The project's <c>HttpModule.BaseUrl</c> — the single global origin.</summary>
        GlobalBaseUrl = 0,

        /// <summary>An <c>HttpRequestAsset</c>.</summary>
        RequestAsset,

        /// <summary>An <c>HttpDataProvider</c>, which points at an <c>HttpRequestAsset</c>.</summary>
        HttpProvider,

        /// <summary>An <c>SSEProvider</c>.</summary>
        SseProvider,

        /// <summary>A <c>WebSocketDataProvider</c>.</summary>
        WebSocketProvider,

        /// <summary>A <c>SocketIODataProvider</c>.</summary>
        SocketIoProvider
    }

    /// <summary>
    /// One legacy networking artifact found in the project, described in catalog terms.
    /// </summary>
    /// <remarks>
    /// Read-only: scanning never mutates or deletes anything. Everything the migration plan and the Hub
    /// need is captured here, so the plan is a pure function of the report and can be recomputed and
    /// diffed without touching the project again.
    /// <para>
    /// Carries no credential value. <see cref="DeclaresCredential"/> records only that the artifact opts
    /// into authentication, which is what determines whether migrating it needs a credential profile.
    /// </para>
    /// </remarks>
    public sealed class LegacyNetworkItem
    {
        /// <summary>Which legacy artifact this describes.</summary>
        public LegacyNetworkItemKind Kind { get; }

        /// <summary>The asset to select when the author chooses <b>Open</b>. May be <c>null</c>.</summary>
        public Object Asset { get; }

        /// <summary>GUID of <see cref="Asset"/>, or empty. The stable migration identity of this item.</summary>
        public string AssetGuid { get; }

        /// <summary>Human-readable label, typically the asset name.</summary>
        public string DisplayName { get; }

        /// <summary>The URL exactly as authored, before any scheme is inferred.</summary>
        public string AuthoredUrl { get; }

        /// <summary>
        /// The absolute URL this artifact actually reaches, with the scheme the provider would prepend.
        /// Empty for a relative URL, which resolves against the global base URL instead.
        /// </summary>
        public string EffectiveUrl { get; }

        /// <summary>Lowercased host of <see cref="EffectiveUrl"/>, or empty when it is relative.</summary>
        public string Host { get; }

        /// <summary>The protocol this artifact speaks.</summary>
        public NetworkProtocols Protocol { get; }

        /// <summary>HTTP method. Meaningful for <see cref="NetworkProtocols.Http"/> only.</summary>
        public HttpMethod Method { get; }

        /// <summary>Whether the artifact opts into authentication. Never a credential value.</summary>
        public bool DeclaresCredential { get; }

        /// <summary>Observations worth reporting to the author. Never <c>null</c>.</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>Whether this artifact reaches an absolute URL of its own rather than the base URL.</summary>
        public bool IsAbsolute => !string.IsNullOrEmpty(Host);

        /// <summary>Creates an item.</summary>
        /// <param name="kind">Which legacy artifact this describes.</param>
        /// <param name="asset">The asset, or <c>null</c>.</param>
        /// <param name="assetGuid">GUID of the asset, or <c>null</c>.</param>
        /// <param name="displayName">Human-readable label.</param>
        /// <param name="authoredUrl">The URL as authored.</param>
        /// <param name="effectiveUrl">The absolute URL actually reached, or <c>null</c> when relative.</param>
        /// <param name="protocol">The protocol spoken.</param>
        /// <param name="method">HTTP method, for HTTP artifacts.</param>
        /// <param name="declaresCredential">Whether the artifact opts into authentication.</param>
        /// <param name="notes">Observations for the author, or <c>null</c>.</param>
        public LegacyNetworkItem(
            LegacyNetworkItemKind kind,
            Object asset,
            string assetGuid,
            string displayName,
            string authoredUrl,
            string effectiveUrl,
            NetworkProtocols protocol,
            HttpMethod method = HttpMethod.GET,
            bool declaresCredential = false,
            IReadOnlyList<string> notes = null)
        {
            Kind = kind;
            Asset = asset;
            AssetGuid = assetGuid ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            AuthoredUrl = authoredUrl ?? string.Empty;
            EffectiveUrl = effectiveUrl ?? string.Empty;
            Protocol = protocol;
            Method = method;
            DeclaresCredential = declaresCredential;
            Notes = notes ?? Array.Empty<string>();

            Host = Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out Uri uri)
                ? uri.Host.ToLowerInvariant()
                : string.Empty;
        }

        /// <summary>Renders the item for the dry-run report.</summary>
        public override string ToString()
        {
            string target = IsAbsolute ? EffectiveUrl : $"(relative) {AuthoredUrl}";
            string auth = DeclaresCredential ? " [authenticated]" : string.Empty;
            return $"{Kind} '{DisplayName}': {Protocol} {target}{auth}";
        }
    }
}
