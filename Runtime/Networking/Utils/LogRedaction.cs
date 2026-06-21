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
    }
}
