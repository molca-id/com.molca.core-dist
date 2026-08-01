using Molca.Networking.Http.Models;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Stable failure classification shared by the routed runtime pipeline and by
    /// authoring diagnostics, so a validation finding and a runtime failure describe
    /// the same problem with the same word.
    /// </summary>
    /// <remarks>
    /// This is additive to <see cref="HttpErrorKind"/>, which legacy callers keep using
    /// unchanged. Map one to the other with <see cref="NetworkErrorCategoryExtensions"/>.
    /// </remarks>
    public enum NetworkErrorCategory
    {
        /// <summary>No failure.</summary>
        None = 0,

        /// <summary>The catalog itself is invalid or absent — malformed IDs, missing catalog, bad origin.</summary>
        Configuration,

        /// <summary>A route could not be resolved: unknown environment or service, or a missing/disabled binding.</summary>
        RouteResolution,

        /// <summary>A security rule rejected the request — disallowed host, insecure scheme, forbidden redirect.</summary>
        SecurityPolicy,

        /// <summary>Credential acquisition or authorization failed.</summary>
        Authentication,

        /// <summary>Connection-level failure with no HTTP status (DNS, socket, TLS handshake).</summary>
        Connectivity,

        /// <summary>An overall or attempt deadline elapsed.</summary>
        Timeout,

        /// <summary>The caller cancelled. Not an error condition.</summary>
        Cancellation,

        /// <summary>The exchange completed with a failing HTTP status.</summary>
        HttpStatus,

        /// <summary>A request body could not be encoded or a response body could not be decoded.</summary>
        Serialization,

        /// <summary>A cache read or write failed. Never fails the request on its own.</summary>
        Cache,

        /// <summary>An observer or diagnostic sink threw. Recorded separately; never changes completion.</summary>
        Observer,

        /// <summary>Unclassified failure.</summary>
        Unknown
    }

    /// <summary>Conversions between <see cref="NetworkErrorCategory"/> and the legacy <see cref="HttpErrorKind"/>.</summary>
    public static class NetworkErrorCategoryExtensions
    {
        /// <summary>
        /// Maps a legacy <see cref="HttpErrorKind"/> onto its <see cref="NetworkErrorCategory"/>.
        /// </summary>
        /// <param name="kind">The legacy classification.</param>
        /// <returns>The equivalent category; <see cref="NetworkErrorCategory.None"/> for success.</returns>
        public static NetworkErrorCategory ToCategory(this HttpErrorKind kind)
        {
            switch (kind)
            {
                case HttpErrorKind.None: return NetworkErrorCategory.None;
                case HttpErrorKind.Network: return NetworkErrorCategory.Connectivity;
                case HttpErrorKind.Timeout: return NetworkErrorCategory.Timeout;
                case HttpErrorKind.Canceled: return NetworkErrorCategory.Cancellation;
                case HttpErrorKind.Http4xx: return NetworkErrorCategory.HttpStatus;
                case HttpErrorKind.Http5xx: return NetworkErrorCategory.HttpStatus;
                case HttpErrorKind.Serialization: return NetworkErrorCategory.Serialization;
                case HttpErrorKind.Auth: return NetworkErrorCategory.Authentication;
                default: return NetworkErrorCategory.Unknown;
            }
        }

        /// <summary>
        /// Maps a <see cref="NetworkErrorCategory"/> back onto the nearest legacy
        /// <see cref="HttpErrorKind"/>, for surfacing routed failures to legacy callers.
        /// </summary>
        /// <param name="category">The routed category.</param>
        /// <param name="statusCode">
        /// The HTTP status when <paramref name="category"/> is <see cref="NetworkErrorCategory.HttpStatus"/>;
        /// decides between <see cref="HttpErrorKind.Http4xx"/> and <see cref="HttpErrorKind.Http5xx"/>.
        /// </param>
        /// <returns>The closest legacy kind.</returns>
        /// <remarks>
        /// Lossy by design. Categories with no legacy equivalent
        /// (<see cref="NetworkErrorCategory.Configuration"/>, <see cref="NetworkErrorCategory.RouteResolution"/>,
        /// <see cref="NetworkErrorCategory.SecurityPolicy"/>) collapse to
        /// <see cref="HttpErrorKind.Network"/> — the kind legacy callers already treat as
        /// "the request never reached the server", which is exactly what happened.
        /// </remarks>
        public static HttpErrorKind ToLegacyKind(this NetworkErrorCategory category, int statusCode = 0)
        {
            switch (category)
            {
                case NetworkErrorCategory.None: return HttpErrorKind.None;
                case NetworkErrorCategory.Timeout: return HttpErrorKind.Timeout;
                case NetworkErrorCategory.Cancellation: return HttpErrorKind.Canceled;
                case NetworkErrorCategory.Authentication: return HttpErrorKind.Auth;
                case NetworkErrorCategory.Serialization: return HttpErrorKind.Serialization;
                case NetworkErrorCategory.HttpStatus:
                    return statusCode >= 500 ? HttpErrorKind.Http5xx : HttpErrorKind.Http4xx;
                default:
                    return HttpErrorKind.Network;
            }
        }

        /// <summary>
        /// Whether a failure in this category means the request never produced a server response,
        /// and therefore may be reported as a connection error to legacy <c>ConnectionError</c> subscribers.
        /// </summary>
        /// <param name="category">The category to test.</param>
        /// <remarks>
        /// Plan §6.4: an HTTP failure must not raise a connection error. Only genuine
        /// connectivity problems qualify.
        /// </remarks>
        public static bool IsConnectivityFailure(this NetworkErrorCategory category) =>
            category == NetworkErrorCategory.Connectivity;
    }
}
