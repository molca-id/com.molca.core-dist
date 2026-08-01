using System;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Normalization and validation for the absolute origins authored on a
    /// <see cref="NetworkServiceBinding"/>, and for joining an endpoint's relative path onto one.
    /// </summary>
    /// <remarks>
    /// The first release deliberately requires explicit absolute origins rather than a
    /// <c>${variable}</c> template language (plan §5.3): host allowlisting, credential scope, and
    /// production checks all need a concrete host at author time.
    /// </remarks>
    public static class NetworkOrigin
    {
        /// <summary>Schemes accepted for an HTTP-family origin.</summary>
        private static readonly string[] HttpSchemes = { "http", "https" };

        /// <summary>Schemes accepted for a WebSocket-family origin.</summary>
        private static readonly string[] WebSocketSchemes = { "ws", "wss" };

        /// <summary>
        /// Validates and normalizes an authored origin: trims whitespace, removes a trailing
        /// slash, lowercases scheme and host, and drops any default port.
        /// </summary>
        /// <param name="origin">The authored origin, for example <c>https://api.example.com/v1</c>.</param>
        /// <param name="allowWebSocketSchemes">
        /// When <c>true</c>, <c>ws</c>/<c>wss</c> are accepted in addition to <c>http</c>/<c>https</c>.
        /// </param>
        /// <param name="normalized">The normalized origin on success; <c>null</c> on failure.</param>
        /// <param name="error">A human-readable reason on failure; <c>null</c> on success.</param>
        /// <returns><c>true</c> when the origin is a usable absolute URI.</returns>
        public static bool TryNormalize(
            string origin,
            bool allowWebSocketSchemes,
            out string normalized,
            out string error)
        {
            normalized = null;

            if (string.IsNullOrWhiteSpace(origin))
            {
                error = "Origin is empty.";
                return false;
            }

            string trimmed = origin.Trim();

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
            {
                error = $"'{trimmed}' is not an absolute URI. Include the scheme, for example https://api.example.com.";
                return false;
            }

            if (!IsSchemeAllowed(uri.Scheme, allowWebSocketSchemes))
            {
                string allowed = allowWebSocketSchemes ? "http, https, ws, wss" : "http, https";
                error = $"Scheme '{uri.Scheme}' is not supported here. Allowed: {allowed}.";
                return false;
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                error = $"'{trimmed}' has no host.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "An origin must not carry a query string or fragment; author those on the endpoint.";
                return false;
            }

            // GetLeftPart(Authority) already lowercases scheme/host and elides the default port.
            string authority = uri.GetLeftPart(UriPartial.Authority);
            string path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');

            normalized = authority + path;
            error = null;
            return true;
        }

        /// <summary>Whether <paramref name="scheme"/> is one of the accepted schemes.</summary>
        /// <param name="scheme">The scheme to test, case-insensitively.</param>
        /// <param name="allowWebSocketSchemes">Whether <c>ws</c>/<c>wss</c> count as accepted.</param>
        public static bool IsSchemeAllowed(string scheme, bool allowWebSocketSchemes)
        {
            if (string.IsNullOrEmpty(scheme)) return false;
            if (Contains(HttpSchemes, scheme)) return true;
            return allowWebSocketSchemes && Contains(WebSocketSchemes, scheme);
        }

        /// <summary>
        /// Whether a scheme encrypts the connection. Production environments require this
        /// (plan §7.13, §12.2).
        /// </summary>
        /// <param name="scheme">The scheme to test, case-insensitively.</param>
        public static bool IsSecureScheme(string scheme) =>
            string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Joins an endpoint's relative path onto a normalized origin.
        /// </summary>
        /// <param name="normalizedOrigin">An origin already accepted by <see cref="TryNormalize"/>.</param>
        /// <param name="relativePath">
        /// The endpoint's path. May be empty, and may carry a leading slash — both resolve to the
        /// origin's own path.
        /// </param>
        /// <param name="joined">The combined absolute URI on success; <c>null</c> on failure.</param>
        /// <param name="error">A human-readable reason on failure; <c>null</c> on success.</param>
        /// <returns><c>true</c> when the result is a well-formed absolute URI.</returns>
        /// <remarks>
        /// Concatenates rather than using <see cref="Uri"/>'s relative-resolution rules, because
        /// <c>new Uri(base, "users")</c> silently discards the last segment of the base path — which
        /// would make an origin of <c>https://host/v1</c> resolve to <c>https://host/users</c>.
        /// Rejects an absolute <paramref name="relativePath"/>: escaping the service origin is a
        /// route decision, not a path decision.
        /// </remarks>
        public static bool TryJoin(
            string normalizedOrigin,
            string relativePath,
            out string joined,
            out string error)
        {
            joined = null;

            if (string.IsNullOrEmpty(normalizedOrigin))
            {
                error = "Origin is empty.";
                return false;
            }

            string path = relativePath == null ? string.Empty : relativePath.Trim();

            if (path.Length > 0 && Uri.TryCreate(path, UriKind.Absolute, out _))
            {
                error = $"Relative path '{path}' is absolute. Target another service instead of overriding the origin.";
                return false;
            }

            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                // "//host/x" is protocol-relative and would replace the authority.
                error = $"Relative path '{path}' is protocol-relative and would replace the host.";
                return false;
            }

            string candidate = path.Length == 0
                ? normalizedOrigin
                : normalizedOrigin.TrimEnd('/') + "/" + path.TrimStart('/');

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri))
            {
                error = $"'{candidate}' is not a valid absolute URI.";
                return false;
            }

            joined = uri.ToString();
            error = null;
            return true;
        }

        private static bool Contains(string[] values, string candidate)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
