using System;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// The <c>GET /content/v1/active</c> response — see <c>contracts/content-release-v1.md</c> §5.
    /// </summary>
    /// <remarks>
    /// This is a pointer, not content. It names the release, states the digest and signature the
    /// manifest must match, and carries the access material needed to fetch bytes. Nothing in it may
    /// change local state until <see cref="ReleaseManifestVerifier"/> has accepted the manifest it
    /// points at.
    ///
    /// A <see cref="status"/> of <c>none</c> is a normal outcome carrying a
    /// <see cref="ContentReleaseReason"/>, not a transport failure — a project that has never
    /// promoted a release, and a player one version too old for the current one, are both ordinary
    /// steady states.
    /// </remarks>
    [Serializable]
    public class ContentReleaseDescriptor
    {
        /// <summary><c>active</c> or <c>none</c>.</summary>
        public string status;

        /// <summary>When <see cref="status"/> is <c>none</c>, why (see <see cref="ContentReleaseReason"/>).</summary>
        public string reason;

        /// <summary>The active release identity.</summary>
        public string releaseId;

        /// <summary>SemVer content version of the active release.</summary>
        public string contentVersion;

        /// <summary>Channel the server resolved from the token policy, not from a client string.</summary>
        public string channel;

        /// <summary>Normalized platform of the resolved release.</summary>
        public string platform;

        /// <summary>Wire protocol major of the release document.</summary>
        public int protocolVersion;

        /// <summary>Absolute URL of the immutable signed manifest.</summary>
        public string manifestUrl;

        /// <summary>Lowercase hex SHA-256 the fetched manifest bytes must equal.</summary>
        public string manifestSha256;

        /// <summary>Compact <c>kid.payload.signature</c> token over the release envelope.</summary>
        public string signature;

        /// <summary>App version range advertised for the release.</summary>
        public ContentReleaseManifest.Compatibility compatibility;

        /// <summary>Material for fetching object bytes.</summary>
        public AccessMaterial access;

        /// <summary>True when the server resolved a release for this identity.</summary>
        public bool IsActive => string.Equals(status, "active", StringComparison.Ordinal);

        /// <summary>Access material for one release (contract §6).</summary>
        [Serializable]
        public class AccessMaterial
        {
            /// <summary>Which delivery shape the server is offering; see <see cref="ContentAccessMode"/>.</summary>
            public string mode;

            /// <summary>ISO 8601 instant after which the material stops working.</summary>
            public string expiresAt;

            /// <summary>Gateway mode: the release-scoped ticket to append to object routes.</summary>
            public string ticket;

            /// <summary>Gateway mode: absolute base URL of the release's object routes.</summary>
            public string baseUrl;

            /// <summary>
            /// Parses <see cref="expiresAt"/>, or returns <see cref="DateTime.MinValue"/> when it is
            /// absent or unparseable.
            /// </summary>
            /// <remarks>
            /// A missing or malformed expiry reads as already-expired rather than never-expiring.
            /// Expiry is measured server-side regardless (contract §6.3); this only decides when the
            /// client refreshes early, and being wrong in the other direction means the first object
            /// of an activation fails on a stale ticket.
            /// </remarks>
            public DateTime ExpiresAtUtc =>
                DateTime.TryParse(
                    expiresAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : DateTime.MinValue;
        }

        /// <summary>
        /// Parses a descriptor from a response body, or returns null when the body is not one.
        /// </summary>
        /// <param name="json">The raw response body.</param>
        public static ContentReleaseDescriptor Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonUtility.FromJson<ContentReleaseDescriptor>(json); }
            catch { return null; }
        }
    }
}
