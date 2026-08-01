using System;
using System.Text;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Validation and normalization rules for the stable identifiers that key every
    /// catalog entity — environments, services, policy profiles, credential profiles,
    /// endpoint collections, and endpoints.
    /// </summary>
    /// <remarks>
    /// IDs are the primary keys of the catalog and are compared with
    /// <see cref="StringComparison.Ordinal"/> everywhere. An empty ID is invalid input,
    /// never a default — the catalog's default environment is an explicit field instead.
    /// Display names are free text and are not governed by these rules.
    /// </remarks>
    public static class NetworkIds
    {
        /// <summary>Maximum permitted ID length in characters.</summary>
        public const int MaxLength = 64;

        /// <summary>
        /// Prefix reserved for framework-generated entries. Author-created IDs must not use it,
        /// so migration can always mint a colliding-free entry.
        /// </summary>
        public const string ReservedPrefix = "molca-";

        /// <summary>ID of the service synthesized by legacy migration from <c>HttpModule.BaseUrl</c>.</summary>
        public const string LegacyDefaultServiceId = "molca-legacy-default";

        /// <summary>
        /// Validates an ID against the kebab-case rules: lowercase ASCII letters, digits, and
        /// single interior hyphens; starts with a letter; does not end with a hyphen;
        /// 1–<see cref="MaxLength"/> characters.
        /// </summary>
        /// <param name="id">The candidate identifier.</param>
        /// <param name="error">
        /// On failure, a human-readable reason suitable for a validation finding; <c>null</c> on success.
        /// </param>
        /// <returns><c>true</c> when <paramref name="id"/> is a well-formed identifier.</returns>
        public static bool IsValid(string id, out string error)
        {
            if (string.IsNullOrEmpty(id))
            {
                error = "Identifier is empty. Every catalog entity needs an explicit stable ID.";
                return false;
            }

            if (id.Length > MaxLength)
            {
                error = $"Identifier '{id}' is {id.Length} characters; the maximum is {MaxLength}.";
                return false;
            }

            char first = id[0];
            if (first < 'a' || first > 'z')
            {
                error = $"Identifier '{id}' must start with a lowercase ASCII letter.";
                return false;
            }

            if (id[id.Length - 1] == '-')
            {
                error = $"Identifier '{id}' must not end with a hyphen.";
                return false;
            }

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool isLower = c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';

                if (isLower || isDigit)
                    continue;

                if (c != '-')
                {
                    error = $"Identifier '{id}' contains '{c}'. Use lowercase letters, digits, and hyphens only.";
                    return false;
                }

                // A hyphen is only legal between two non-hyphen characters; the leading and
                // trailing positions were already rejected above.
                if (id[i - 1] == '-')
                {
                    error = $"Identifier '{id}' contains consecutive hyphens.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>Whether <paramref name="id"/> uses the framework-reserved <see cref="ReservedPrefix"/>.</summary>
        /// <param name="id">The identifier to test; <c>null</c> returns <c>false</c>.</param>
        public static bool IsReserved(string id) =>
            id != null && id.StartsWith(ReservedPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Best-effort conversion of free text into a valid identifier: lowercased, non-alphanumeric
        /// runs collapsed to single hyphens, leading digits/hyphens trimmed, truncated to
        /// <see cref="MaxLength"/>.
        /// </summary>
        /// <param name="text">Arbitrary text, typically a display name or a host name.</param>
        /// <param name="fallback">
        /// Returned when <paramref name="text"/> yields nothing usable. Must itself be a valid ID.
        /// </param>
        /// <returns>A valid identifier derived from <paramref name="text"/>, or <paramref name="fallback"/>.</returns>
        /// <remarks>
        /// Used by migration and import to propose IDs. It is a suggestion generator, not a
        /// validator — callers still resolve collisions and re-check with <see cref="IsValid"/>.
        /// </remarks>
        public static string Suggest(string text, string fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;

            var builder = new StringBuilder(Math.Min(text.Length, MaxLength));
            bool pendingHyphen = false;

            foreach (char raw in text)
            {
                char c = char.ToLowerInvariant(raw);
                bool isLower = c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';

                if (isLower || isDigit)
                {
                    // Digits may not lead, so drop them until the first letter has been emitted.
                    if (builder.Length == 0 && !isLower)
                        continue;

                    if (pendingHyphen && builder.Length > 0)
                        builder.Append('-');

                    pendingHyphen = false;
                    builder.Append(c);

                    if (builder.Length == MaxLength)
                        break;
                    continue;
                }

                pendingHyphen = true;
            }

            return builder.Length == 0 ? fallback : builder.ToString();
        }

        /// <summary>
        /// Appends a numeric suffix until <paramref name="candidate"/> no longer collides,
        /// truncating as needed to stay within <see cref="MaxLength"/>.
        /// </summary>
        /// <param name="candidate">The preferred identifier.</param>
        /// <param name="isTaken">Predicate returning <c>true</c> when an identifier is already in use.</param>
        /// <returns>An identifier for which <paramref name="isTaken"/> returns <c>false</c>.</returns>
        public static string MakeUnique(string candidate, Func<string, bool> isTaken)
        {
            if (isTaken == null) return candidate;
            if (!isTaken(candidate)) return candidate;

            for (int suffix = 2; ; suffix++)
            {
                string tail = "-" + suffix.ToString();
                string head = candidate.Length + tail.Length <= MaxLength
                    ? candidate
                    : candidate.Substring(0, MaxLength - tail.Length).TrimEnd('-');

                string next = head + tail;
                if (!isTaken(next))
                    return next;
            }
        }
    }
}
