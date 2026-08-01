using System.Threading;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>The signed manifest bytes and the token that signs them.</summary>
    public sealed class ReleaseManifestPayload
    {
        /// <summary>The manifest exactly as received. Never re-encoded before digesting.</summary>
        public byte[] Bytes { get; set; }

        /// <summary>The compact signature token that covers <see cref="Bytes"/>.</summary>
        public string Signature { get; set; }
    }

    /// <summary>Outcome of one control-plane call, carrying a contract reason on failure.</summary>
    /// <typeparam name="T">The value type on success.</typeparam>
    public sealed class ContentReleaseResponse<T> where T : class
    {
        /// <summary>True when <see cref="Value"/> is present.</summary>
        public bool Success { get; private set; }

        /// <summary>The value, or null on failure.</summary>
        public T Value { get; private set; }

        /// <summary>A <see cref="ContentReleaseReason"/> when <see cref="Success"/> is false.</summary>
        public string Reason { get; private set; }

        /// <summary>Operator-facing detail. Never contains a token, ticket, or signed URL.</summary>
        public string Detail { get; private set; }

        /// <summary>Builds a success.</summary>
        /// <param name="value">The value returned.</param>
        public static ContentReleaseResponse<T> Ok(T value) =>
            new ContentReleaseResponse<T> { Success = true, Value = value, Reason = "", Detail = "" };

        /// <summary>Builds a failure.</summary>
        /// <param name="reason">A <see cref="ContentReleaseReason"/>.</param>
        /// <param name="detail">Redacted, operator-facing detail.</param>
        public static ContentReleaseResponse<T> Fail(string reason, string detail) =>
            new ContentReleaseResponse<T> { Success = false, Reason = reason, Detail = detail ?? "" };
    }

    /// <summary>
    /// The runtime half of the content protocol: resolve the active release, fetch its signed
    /// manifest, and obtain access material.
    /// </summary>
    /// <remarks>
    /// An interface because the activation coordinator's interesting behaviour is what it does when
    /// these calls fail partway through, and that is unreasonable to provoke against a live server.
    ///
    /// No method here changes local state, and none of them verifies anything. Fetching and trusting
    /// are separated on purpose: a client that verified inside its fetch would make "we got bytes"
    /// and "we may act on them" the same condition, and every caller would inherit that conflation.
    /// </remarks>
    public interface IContentReleaseClient
    {
        /// <summary>Resolves the active release for this build's project, channel, and platform.</summary>
        /// <remarks>
        /// A descriptor with <c>status: none</c> is a <em>success</em> carrying a reason — a project
        /// with nothing promoted is not a failure to report as one.
        /// </remarks>
        /// <param name="platform">Normalized platform identifier (contract §1).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        Awaitable<ContentReleaseResponse<ContentReleaseDescriptor>> ResolveActiveAsync(
            string platform, CancellationToken cancellationToken = default);

        /// <summary>Fetches the signed manifest a descriptor points at.</summary>
        /// <param name="descriptor">The descriptor naming the manifest and its signature.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        Awaitable<ContentReleaseResponse<ReleaseManifestPayload>> FetchManifestAsync(
            ContentReleaseDescriptor descriptor, CancellationToken cancellationToken = default);

        /// <summary>Obtains fresh access material for a release.</summary>
        /// <param name="releaseId">The release to obtain access for.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        Awaitable<ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>> RequestAccessAsync(
            string releaseId, CancellationToken cancellationToken = default);
    }
}
