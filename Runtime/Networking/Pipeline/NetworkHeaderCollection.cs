using System;
using System.Collections;
using System.Collections.Generic;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Pipeline
{
    /// <summary>
    /// An immutable, case-insensitive header collection.
    /// </summary>
    /// <remarks>
    /// HTTP header names are case-insensitive by specification, but
    /// <see cref="HttpResponse.headers"/> is an ordinal <see cref="Dictionary{TKey,TValue}"/> — so
    /// <c>GetHeaderValue("content-type")</c> misses a server-sent <c>Content-Type</c>. The routed
    /// pipeline normalizes into this type rather than changing that public field's behaviour
    /// (plan §2.1 item 11, §6.4 step 9).
    /// <para>
    /// Preserves the first-seen casing of each name for display, while comparing and looking up
    /// case-insensitively.
    /// </para>
    /// </remarks>
    public sealed class NetworkHeaderCollection : IReadOnlyCollection<KeyValuePair<string, string>>
    {
        /// <summary>An empty collection.</summary>
        public static readonly NetworkHeaderCollection Empty =
            new NetworkHeaderCollection(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private readonly Dictionary<string, string> _headers;

        private NetworkHeaderCollection(Dictionary<string, string> headers)
        {
            _headers = headers;
        }

        /// <summary>Number of distinct header names.</summary>
        public int Count => _headers.Count;

        /// <summary>
        /// Reads a header value.
        /// </summary>
        /// <param name="name">The header name, compared case-insensitively.</param>
        /// <returns>The value, or <c>null</c> when absent.</returns>
        public string this[string name] =>
            name != null && _headers.TryGetValue(name, out string value) ? value : null;

        /// <summary>Whether a header is present, compared case-insensitively.</summary>
        /// <param name="name">The header name.</param>
        public bool Contains(string name) => name != null && _headers.ContainsKey(name);

        /// <summary>
        /// Reads a header value.
        /// </summary>
        /// <param name="name">The header name, compared case-insensitively.</param>
        /// <param name="value">The value on success.</param>
        /// <returns><c>true</c> when the header is present.</returns>
        public bool TryGetValue(string name, out string value)
        {
            if (name == null)
            {
                value = null;
                return false;
            }
            return _headers.TryGetValue(name, out value);
        }

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Builds a collection from name/value pairs. Later entries overwrite earlier ones with the
        /// same name, case-insensitively.
        /// </summary>
        /// <param name="pairs">The pairs to include; <c>null</c> yields <see cref="Empty"/>.</param>
        public static NetworkHeaderCollection From(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null) return Empty;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                    headers[pair.Key] = pair.Value;
            }
            return headers.Count == 0 ? Empty : new NetworkHeaderCollection(headers);
        }

        /// <summary>
        /// Normalizes a response's headers into a case-insensitive collection.
        /// </summary>
        /// <param name="response">The response to read; <c>null</c> yields <see cref="Empty"/>.</param>
        public static NetworkHeaderCollection FromResponse(HttpResponse response) =>
            response?.headers == null ? Empty : From(response.headers);

        /// <summary>
        /// Merges header layers, lowest precedence first. A later layer's value wins.
        /// </summary>
        /// <param name="layers">The layers to merge; <c>null</c> entries are skipped.</param>
        /// <returns>The merged collection.</returns>
        /// <remarks>
        /// Used to apply service default headers beneath the caller's own request headers, so an
        /// explicit request header always wins over a service default (plan §6.4 step 3).
        /// </remarks>
        public static NetworkHeaderCollection Merge(params IEnumerable<KeyValuePair<string, string>>[] layers)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (layers == null) return Empty;

            foreach (var layer in layers)
            {
                if (layer == null) continue;

                foreach (var pair in layer)
                {
                    if (!string.IsNullOrEmpty(pair.Key))
                        headers[pair.Key] = pair.Value;
                }
            }
            return headers.Count == 0 ? Empty : new NetworkHeaderCollection(headers);
        }

        /// <summary>
        /// Enabled headers from a <see cref="HttpRequest"/>, as mergeable pairs.
        /// </summary>
        /// <param name="request">The request to read; <c>null</c> yields an empty sequence.</param>
        public static IEnumerable<KeyValuePair<string, string>> FromRequest(HttpRequest request)
        {
            if (request?.headers == null) yield break;

            foreach (var header in request.headers)
            {
                if (header != null && header.isEnabled && !string.IsNullOrEmpty(header.key))
                    yield return new KeyValuePair<string, string>(header.key, header.value);
            }
        }

        /// <summary>
        /// Enabled default headers from a service definition, as mergeable pairs.
        /// </summary>
        /// <param name="headers">The service's default headers; <c>null</c> yields an empty sequence.</param>
        public static IEnumerable<KeyValuePair<string, string>> FromServiceDefaults(IReadOnlyList<HttpHeader> headers)
        {
            if (headers == null) yield break;

            for (int i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                if (header != null && header.isEnabled && !string.IsNullOrEmpty(header.key))
                    yield return new KeyValuePair<string, string>(header.key, header.value);
            }
        }

        /// <summary>Copies these headers into a mutable dictionary with the same comparer.</summary>
        /// <returns>A new case-insensitive dictionary.</returns>
        public Dictionary<string, string> ToDictionary() =>
            new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
    }
}
