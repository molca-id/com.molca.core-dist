using System;
using System.Collections.Generic;
using System.Text;

namespace Molca.Networking.Utils
{
    /// <summary>
    /// Redaction helpers for networking logs: query-string values and sensitive
    /// header values are masked so credentials never land in log files.
    /// </summary>
    public static class LogRedaction
    {
        private const string MASK = "***";

        private static readonly HashSet<string> SensitiveHeaderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "authorization-token",
            "proxy-authorization",
            "x-auth-token",
            "x-api-key",
            "api-key",
            "token",
            "cookie",
            "set-cookie"
        };

        /// <summary>
        /// Masks every query-parameter value in <paramref name="url"/>
        /// (e.g. <c>?token=abc&amp;v=1</c> → <c>?token=***&amp;v=***</c>). Path and host are preserved.
        /// </summary>
        public static string RedactUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            int queryStart = url.IndexOf('?');
            if (queryStart < 0)
                return url;

            var builder = new StringBuilder(url.Length);
            builder.Append(url, 0, queryStart + 1);

            string query = url.Substring(queryStart + 1);
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (i > 0) builder.Append('&');
                int eq = pairs[i].IndexOf('=');
                if (eq < 0)
                {
                    builder.Append(pairs[i]);
                }
                else
                {
                    builder.Append(pairs[i], 0, eq + 1).Append(MASK);
                }
            }
            return builder.ToString();
        }

        /// <summary>Whether a header key carries credentials and must not be logged verbatim.</summary>
        public static bool IsSensitiveHeader(string key) =>
            !string.IsNullOrEmpty(key) && SensitiveHeaderKeys.Contains(key);

        /// <summary>Returns the header value, masked when the key is sensitive.</summary>
        public static string RedactHeaderValue(string key, string value) =>
            IsSensitiveHeader(key) ? MASK : value;

        // Substring markers that flag a field/param name as credential-bearing. Kept
        // narrow enough to avoid obvious false positives ("author", "spinner") while
        // over-redacting is always the safe direction for a diagnostics surface.
        private static readonly string[] SensitiveFieldMarkers =
        {
            "password", "passwd", "secret", "token", "credential", "apikey", "api_key", "api-key"
        };

        // Names that are sensitive only as an exact match (too short/ambiguous for
        // substring matching).
        private static readonly HashSet<string> SensitiveFieldExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pass", "pin", "otp", "auth"
        };

        /// <summary>
        /// Whether a body/form/query field name looks credential-bearing and its value
        /// must not be stored or logged verbatim.
        /// </summary>
        public static bool IsSensitiveField(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            if (SensitiveFieldExact.Contains(key))
                return true;
            foreach (string marker in SensitiveFieldMarkers)
            {
                if (key.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Matches "field": "value" pairs whose field name is credential-shaped, so the
        // value can be masked in place without parsing the (possibly invalid) JSON.
        private static readonly System.Text.RegularExpressions.Regex JsonCredentialPattern =
            new System.Text.RegularExpressions.Regex(
                "\"([^\"]*)\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Masks the values of credential-shaped fields in a JSON body
        /// (e.g. <c>{"user":"a","password":"x"}</c> → <c>{"user":"a","password":"***"}</c>).
        /// Text that is not JSON passes through unchanged apart from any matching
        /// <c>"name":"value"</c> pairs it happens to contain.
        /// </summary>
        public static string RedactJsonBody(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            return JsonCredentialPattern.Replace(json, match =>
                IsSensitiveField(match.Groups[1].Value)
                    ? $"\"{match.Groups[1].Value}\":\"{MASK}\""
                    : match.Value);
        }
    }
}
