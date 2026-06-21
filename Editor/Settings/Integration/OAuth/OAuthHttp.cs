using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// The outcome of an OAuth endpoint POST: transport/HTTP success plus the raw response body.
    /// </summary>
    public readonly struct OAuthHttpResult
    {
        /// <summary>Creates a result.</summary>
        public OAuthHttpResult(bool success, int statusCode, string body)
        {
            Success = success;
            StatusCode = statusCode;
            Body = body;
        }

        /// <summary>True when the request returned a 2xx status.</summary>
        public bool Success { get; }

        /// <summary>The HTTP status code (0 on a transport error).</summary>
        public int StatusCode { get; }

        /// <summary>The raw response body (JSON for these endpoints; may be empty).</summary>
        public string Body { get; }
    }

    /// <summary>
    /// Posts form parameters to an OAuth endpoint (device-code / token / refresh) and returns the raw
    /// result.
    /// </summary>
    /// <remarks>
    /// This is the single injectable seam for OAuth network I/O. <see cref="OAuthHttp.DefaultPoster"/>
    /// routes through <c>EditorHttpClient</c>; tests substitute a fake so the device-flow state machine,
    /// token exchange, and refresh paths are covered without live HTTP (mirrors how the Figma/ClickUp
    /// suites avoid the un-injectable transport).
    /// </remarks>
    /// <param name="url">The endpoint URL.</param>
    /// <param name="form">The form parameters to send.</param>
    /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
    /// <returns>The endpoint result.</returns>
    public delegate Awaitable<OAuthHttpResult> OAuthFormPoster(
        string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken);

    /// <summary>
    /// Shared OAuth HTTP helpers: the default endpoint poster plus form encoding.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// <para>
    /// The OAuth parameters are sent as <c>application/x-www-form-urlencoded</c> query parameters on a
    /// POST with <c>Accept: application/json</c>. GitHub's device/token endpoints and Figma's token
    /// endpoint both accept this shape, and it lets us reuse <c>EditorHttpClient</c> unchanged — whose
    /// only built-in POST body types are JSON and multipart, neither of which an OAuth token endpoint
    /// expects. The flows are loopback/PKCE/device (no embedded secret), so no confidential value rides
    /// the query string.
    /// </para>
    /// </remarks>
    public static class OAuthHttp
    {
        /// <summary>
        /// The production poster: routes through <c>EditorHttpClient</c> as a query-parameterized POST.
        /// </summary>
        public static readonly OAuthFormPoster DefaultPoster = PostAsync;

        private static async Awaitable<OAuthHttpResult> PostAsync(
            string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new HttpRequest
            {
                name = $"OAuth POST {url}",
                method = HttpMethod.POST,
                url = url,
                useFullUrl = true,
                expectedResponseType = ResponseType.Json
            };
            request.AddHeader("Accept", "application/json");
            if (form != null)
            {
                foreach (var pair in form)
                    request.AddParam(pair.Key, pair.Value);
            }

            try
            {
                var response = await EditorHttpClient.SendAsync(request);
                if (response == null)
                    return new OAuthHttpResult(false, 0, null);
                return new OAuthHttpResult(response.isSuccess, response.statusCode, response.text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OAuth] POST {url} failed: {e.Message}");
                return new OAuthHttpResult(false, 0, null);
            }
        }

        /// <summary>
        /// Encodes form parameters as an <c>application/x-www-form-urlencoded</c> string (handy when a
        /// caller needs the body form rather than query parameters).
        /// </summary>
        /// <param name="form">The parameters to encode.</param>
        /// <returns>The encoded string (empty when <paramref name="form"/> is null/empty).</returns>
        public static string EncodeForm(IReadOnlyDictionary<string, string> form)
        {
            if (form == null)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var pair in form)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a token-endpoint JSON response into a bundle, computing the absolute expiry from
        /// <c>expires_in</c> relative to <paramref name="nowUtc"/>.
        /// </summary>
        /// <param name="body">The raw JSON response body.</param>
        /// <param name="nowUtc">The reference "now" used to compute the absolute expiry.</param>
        /// <param name="bundle">The parsed bundle when this returns <c>true</c>.</param>
        /// <param name="error">The provider's <c>error</c>/<c>error_description</c> (or a parse error) on failure.</param>
        /// <returns><c>true</c> if an access token was parsed; otherwise <c>false</c>.</returns>
        public static bool TryParseTokenResponse(
            string body, DateTime nowUtc, out OAuthTokenBundle bundle, out string error)
        {
            bundle = null;
            error = null;

            if (string.IsNullOrWhiteSpace(body))
            {
                error = "Empty token response.";
                return false;
            }

            JObject json;
            try
            {
                json = JObject.Parse(body);
            }
            catch (Exception e)
            {
                error = $"Unparseable token response: {e.Message}";
                return false;
            }

            var providerError = json.Value<string>("error");
            if (!string.IsNullOrEmpty(providerError))
            {
                var description = json.Value<string>("error_description");
                error = string.IsNullOrEmpty(description) ? providerError : $"{providerError}: {description}";
                return false;
            }

            var accessToken = json.Value<string>("access_token");
            if (string.IsNullOrEmpty(accessToken))
            {
                error = "Token response contained no access_token.";
                return false;
            }

            // expires_in may be absent (non-expiring) or a number.
            long expiresIn = json["expires_in"] != null && json["expires_in"].Type != JTokenType.Null
                ? json.Value<long>("expires_in")
                : 0;

            bundle = OAuthTokenBundle.FromResponse(
                accessToken,
                json.Value<string>("refresh_token"),
                expiresIn,
                json.Value<string>("scope"),
                json.Value<string>("token_type"),
                nowUtc);
            return true;
        }
    }
}
