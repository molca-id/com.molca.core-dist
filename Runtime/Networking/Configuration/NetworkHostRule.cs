using System;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Matching rules for the allowed-host patterns authored on a
    /// <see cref="NetworkServiceDefinition"/> and on a <see cref="NetworkCredentialProfile"/>.
    /// </summary>
    /// <remarks>
    /// This is the gate that keeps a credential from reaching an unapproved host (plan §6.6).
    /// It is deliberately a narrow matcher rather than a glob or regex engine: a pattern is either
    /// an exact host or a single leading <c>*.</c> wildcard covering one or more subdomain labels.
    /// Anything richer would make "which hosts can see this token?" unanswerable by inspection.
    /// </remarks>
    public static class NetworkHostRule
    {
        /// <summary>Wildcard prefix accepted at the start of a pattern.</summary>
        private const string WildcardPrefix = "*.";

        /// <summary>
        /// Validates an authored host pattern.
        /// </summary>
        /// <param name="pattern">The pattern, for example <c>api.example.com</c> or <c>*.example.com</c>.</param>
        /// <param name="normalized">The lowercased, trimmed pattern on success; <c>null</c> on failure.</param>
        /// <param name="error">A human-readable reason on failure; <c>null</c> on success.</param>
        /// <returns><c>true</c> when the pattern is well formed.</returns>
        public static bool TryNormalizePattern(string pattern, out string normalized, out string error)
        {
            normalized = null;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = "Host pattern is empty.";
                return false;
            }

            string value = pattern.Trim().ToLowerInvariant();

            if (value == "*")
            {
                error = "'*' would allow every host. Name the hosts, or use a '*.domain' pattern.";
                return false;
            }

            string host = value.StartsWith(WildcardPrefix, StringComparison.Ordinal)
                ? value.Substring(WildcardPrefix.Length)
                : value;

            if (host.Length == 0)
            {
                error = $"Host pattern '{pattern}' has no domain after the wildcard.";
                return false;
            }

            if (host.IndexOf('*') >= 0)
            {
                error = $"Host pattern '{pattern}' may only use a single leading '*.' wildcard.";
                return false;
            }

            if (host.IndexOf('/') >= 0 || host.IndexOf(':') >= 0)
            {
                error = $"Host pattern '{pattern}' must be a bare host — no scheme, port, or path.";
                return false;
            }

            if (host[0] == '.' || host[host.Length - 1] == '.' || host.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                error = $"Host pattern '{pattern}' has an empty domain label.";
                return false;
            }

            // A wildcard must cover at least two labels, so "*.com" cannot hand a credential to
            // every host in a public suffix.
            if (value.StartsWith(WildcardPrefix, StringComparison.Ordinal) && host.IndexOf('.') < 0)
            {
                error = $"Host pattern '{pattern}' is too broad. A wildcard needs at least two labels, e.g. '*.example.com'.";
                return false;
            }

            normalized = value;
            error = null;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="host"/> is matched by <paramref name="pattern"/>.
        /// </summary>
        /// <param name="pattern">A pattern accepted by <see cref="TryNormalizePattern"/>.</param>
        /// <param name="host">The concrete host to test.</param>
        /// <returns><c>true</c> on an exact match, or when the pattern's wildcard covers the host.</returns>
        /// <remarks>
        /// A <c>*.example.com</c> pattern matches <c>a.example.com</c> and <c>a.b.example.com</c> but
        /// not the apex <c>example.com</c> — authoring the apex is an explicit, separate decision.
        /// </remarks>
        public static bool Matches(string pattern, string host)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(host))
                return false;

            if (string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!pattern.StartsWith(WildcardPrefix, StringComparison.Ordinal))
                return false;

            string suffix = pattern.Substring(WildcardPrefix.Length);

            // Require the '.' boundary so "*.example.com" cannot match "notexample.com".
            return host.Length > suffix.Length + 1
                && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - suffix.Length - 1] == '.';
        }

        /// <summary>
        /// Whether any pattern in <paramref name="patterns"/> matches <paramref name="host"/>.
        /// </summary>
        /// <param name="patterns">The allowed-host patterns; <c>null</c> or empty matches nothing.</param>
        /// <param name="host">The concrete host to test.</param>
        /// <returns><c>true</c> when at least one pattern matches.</returns>
        /// <remarks>
        /// An empty pattern set denies rather than allows. "No rules authored" must never read as
        /// "every host approved" for a credential scope.
        /// </remarks>
        public static bool MatchesAny(System.Collections.Generic.IReadOnlyList<string> patterns, string host)
        {
            if (patterns == null || string.IsNullOrEmpty(host))
                return false;

            for (int i = 0; i < patterns.Count; i++)
            {
                if (Matches(patterns[i], host))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Extracts the host from an absolute URI for use with <see cref="Matches"/>.
        /// </summary>
        /// <param name="absoluteUri">The URI to inspect.</param>
        /// <returns>The lowercased host, or <c>null</c> when <paramref name="absoluteUri"/> is not absolute.</returns>
        public static string HostOf(string absoluteUri) =>
            Uri.TryCreate(absoluteUri, UriKind.Absolute, out Uri uri) ? uri.Host.ToLowerInvariant() : null;
    }
}
