using System;
using System.Collections.Generic;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using Molca.Settings.Integration.OAuth;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Thin editor-only wrapper over <see cref="EditorHttpClient"/> for the Figma REST API.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Registration: instantiated by <see cref="FigmaIntegrationProvider"/>; not an asset.
    /// <para>
    /// Figma personal access tokens are sent in the <c>X-Figma-Token</c> header (Figma does <b>not</b> use a
    /// <c>Bearer</c> token). The token is supplied at construction; callers source it from
    /// <see cref="IntegrationCredentialStore"/>. Flat list endpoints deserialize into <see cref="FigmaModels"/>
    /// via <c>JsonUtility</c>; the recursive, dynamically-keyed node and image endpoints return Newtonsoft
    /// <see cref="JObject"/> so callers can walk arbitrary node trees and node-id-keyed maps. All methods honor
    /// a <see cref="CancellationToken"/>; cancellation surfaces as <see cref="OperationCanceledException"/> and
    /// is not a failure.
    /// </para>
    /// </remarks>
    public sealed class FigmaApiClient
    {
        private const string BaseUrl = "https://api.figma.com";

        private readonly string _token;
        private readonly IntegrationCredentialKind _kind;

        /// <summary>Creates a client bound to a Figma credential.</summary>
        /// <param name="token">The credential value (raw).</param>
        /// <param name="kind">
        /// Whether <paramref name="token"/> is a personal access token (sent in <c>X-Figma-Token</c>) or an
        /// OAuth access token (sent as <c>Authorization: Bearer</c>). Figma uses different headers per kind,
        /// so the client cannot infer it from the value. Defaults to PAT for backward compatibility.
        /// </param>
        public FigmaApiClient(string token, IntegrationCredentialKind kind = IntegrationCredentialKind.PersonalAccessToken)
        {
            _token = token;
            _kind = kind;
        }

        /// <summary>
        /// Returns the auth header name/value Figma expects for a given credential kind: a PAT goes in
        /// <c>X-Figma-Token</c>, an OAuth access token in <c>Authorization: Bearer</c>.
        /// </summary>
        /// <param name="kind">The credential kind.</param>
        /// <param name="token">The credential value.</param>
        /// <returns>The header name and value to set.</returns>
        public static (string Name, string Value) AuthHeader(IntegrationCredentialKind kind, string token)
            => kind == IntegrationCredentialKind.OAuth
                ? ("Authorization", $"Bearer {token}")
                : ("X-Figma-Token", token);

        /// <summary>
        /// Fetches the authenticated user (<c>GET /v1/me</c>) — the cheapest way to validate the token.
        /// </summary>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The user, or <c>null</c> if the token is invalid or the call failed.</returns>
        internal async Awaitable<FigmaModels.FigmaUser> GetMeAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, "/v1/me", cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<FigmaModels.FigmaUser>(response.text);
        }

        /// <summary>
        /// Lists the projects in a team (<c>GET /v1/teams/:team_id/projects</c>).
        /// </summary>
        /// <param name="teamId">The Figma team id.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The projects response, or <c>null</c> on failure.</returns>
        internal async Awaitable<FigmaModels.ProjectsResponse> GetTeamProjectsAsync(
            string teamId, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, $"/v1/teams/{Uri.EscapeDataString(teamId)}/projects", cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<FigmaModels.ProjectsResponse>(response.text);
        }

        /// <summary>
        /// Lists the files in a project (<c>GET /v1/projects/:project_id/files</c>).
        /// </summary>
        /// <param name="projectId">The Figma project id.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The files response, or <c>null</c> on failure.</returns>
        internal async Awaitable<FigmaModels.FilesResponse> GetProjectFilesAsync(
            string projectId, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, $"/v1/projects/{Uri.EscapeDataString(projectId)}/files", cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<FigmaModels.FilesResponse>(response.text);
        }

        /// <summary>
        /// Fetches a file's structure to a caller-chosen depth (<c>GET /v1/files/:key?depth=N</c>) so frames can
        /// be enumerated even when they are nested inside <c>SECTION</c>/<c>GROUP</c> containers. Depth counts
        /// from the document: document(0) → canvas(1) → child(2) → grandchild(3) … so a frame inside one section
        /// needs <c>depth&gt;=3</c>.
        /// </summary>
        /// <param name="fileKey">The file key.</param>
        /// <param name="depth">
        /// How many node levels to return. Values <c>&lt;= 0</c> omit the query and let Figma return the full
        /// (potentially huge) tree; prefer a small bound (e.g. 4) that covers typical section nesting.
        /// </param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The parsed file response as a <see cref="JObject"/>, or <c>null</c> on failure.</returns>
        internal async Awaitable<JObject> GetFileFramesAsync(
            string fileKey, int depth, CancellationToken cancellationToken = default)
        {
            string query = depth > 0 ? $"?depth={depth}" : string.Empty;
            var response = await SendAsync(HttpMethod.GET, $"/v1/files/{Uri.EscapeDataString(fileKey)}{query}", cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeParse(response.text);
        }

        /// <summary>
        /// Fetches specific nodes from a file (<c>GET /v1/files/:key/nodes?ids=</c>) — never the whole file.
        /// </summary>
        /// <param name="fileKey">The file key.</param>
        /// <param name="nodeIds">The node ids to fetch.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>
        /// The parsed response as a <see cref="JObject"/> (a <c>nodes</c> map keyed by node id), or
        /// <c>null</c> on failure.
        /// </returns>
        internal async Awaitable<JObject> GetFileNodesAsync(
            string fileKey, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default)
        {
            if (nodeIds == null || nodeIds.Count == 0)
                return null;

            string ids = string.Join(",", nodeIds);
            var response = await SendAsync(
                HttpMethod.GET,
                $"/v1/files/{Uri.EscapeDataString(fileKey)}/nodes?ids={Uri.EscapeDataString(ids)}",
                cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeParse(response.text);
        }

        /// <summary>
        /// Requests CDN render URLs for nodes (<c>GET /v1/images/:key?ids=&amp;format=</c>).
        /// </summary>
        /// <param name="fileKey">The file key.</param>
        /// <param name="nodeIds">The node ids to render.</param>
        /// <param name="format">The image format (<c>png</c>, <c>svg</c>, <c>jpg</c>, <c>pdf</c>).</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>A map of node id → render URL (empty for nodes Figma could not render), or empty on failure.</returns>
        internal async Awaitable<Dictionary<string, string>> GetImagesAsync(
            string fileKey, IReadOnlyList<string> nodeIds, string format = "png",
            CancellationToken cancellationToken = default)
        {
            var map = new Dictionary<string, string>();
            if (nodeIds == null || nodeIds.Count == 0)
                return map;

            string ids = string.Join(",", nodeIds);
            var response = await SendAsync(
                HttpMethod.GET,
                $"/v1/images/{Uri.EscapeDataString(fileKey)}?ids={Uri.EscapeDataString(ids)}&format={Uri.EscapeDataString(format)}",
                cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return map;

            var parsed = SafeParse(response.text);
            if (parsed?["images"] is JObject images)
            {
                foreach (var pair in images)
                {
                    if (pair.Value != null && pair.Value.Type != JTokenType.Null)
                        map[pair.Key] = pair.Value.ToString();
                }
            }
            return map;
        }

        /// <summary>
        /// Downloads raw bytes from an arbitrary URL (the Figma image CDN), without the Figma auth header.
        /// </summary>
        /// <param name="url">The fully-qualified CDN URL returned by <see cref="GetImagesAsync"/>.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The downloaded bytes, or <c>null</c> on failure.</returns>
        public async Awaitable<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new HttpRequest
            {
                name = $"Figma download {url}",
                method = HttpMethod.GET,
                url = url,
                useFullUrl = true,
                expectedResponseType = ResponseType.Binary
            };

            try
            {
                var response = await EditorHttpClient.SendAsync(request);
                if (response == null || !response.isSuccess)
                    return null;
                return response.rawData;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Figma] Download failed: {e.Message}");
                return null;
            }
        }

        // Figma rate-limits per endpoint; bursts of project/files calls trip HTTP 429. Retry a bounded number
        // of times with exponential backoff, honoring a Retry-After header when present (Sprint 30.x hardening).
        private const int MaxRateLimitRetries = 4;
        private const float BaseBackoffSeconds = 1.5f;
        // Cap any single backoff (incl. a server Retry-After) so a multi-day cooldown can't hang the editor tool.
        private const float MaxBackoffSeconds = 30f;

        private async Awaitable<HttpResponse> SendAsync(
            HttpMethod method, string path, CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new HttpRequest
                {
                    name = $"Figma {method} {path}",
                    method = method,
                    url = BaseUrl + path,
                    useFullUrl = true,
                    expectedResponseType = ResponseType.Json
                };
                var (headerName, headerValue) = AuthHeader(_kind, _token);
                request.AddHeader(headerName, headerValue);

                HttpResponse response;
                // EditorHttpClient throws on transport errors; treat those as a failed (null) response so callers
                // can report a connection failure instead of crashing the editor flow.
                try
                {
                    response = await EditorHttpClient.SendAsync(request);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Figma] {method} {path} failed: {e.Message}");
                    return null;
                }

                if (response == null || response.statusCode != 429 || attempt >= MaxRateLimitRetries)
                    return response;

                float wait = ResolveBackoffSeconds(response, attempt);
                Debug.LogWarning($"[Figma] 429 rate-limited on {path}; retrying in {wait:0.#}s "
                               + $"(attempt {attempt + 1}/{MaxRateLimitRetries}).");
                await Awaitable.WaitForSecondsAsync(wait, cancellationToken);
            }
        }

        /// <summary>
        /// Computes the backoff before retrying a 429: the server's <c>Retry-After</c> seconds when present and
        /// parseable, otherwise exponential backoff from <see cref="BaseBackoffSeconds"/>. Always clamped to
        /// <see cref="MaxBackoffSeconds"/> — Figma can return a multi-day Retry-After, and blocking an editor
        /// tool on that would hang it; better to exhaust the retries quickly and surface the 429.
        /// </summary>
        private static float ResolveBackoffSeconds(HttpResponse response, int attempt)
        {
            string retryAfter = response.GetHeaderValue("Retry-After") ?? response.GetHeaderValue("retry-after");
            if (!string.IsNullOrEmpty(retryAfter)
                && int.TryParse(retryAfter, out int seconds) && seconds > 0)
                return Math.Min(seconds, MaxBackoffSeconds);

            return Math.Min(BaseBackoffSeconds * (1 << attempt), MaxBackoffSeconds); // 1.5, 3, 6, 12s
        }

        private static T SafeFromJson<T>(string json) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Figma] Failed to parse response: {e.Message}");
                return null;
            }
        }

        private static JObject SafeParse(string json)
        {
            try
            {
                return JObject.Parse(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Figma] Failed to parse node JSON: {e.Message}");
                return null;
            }
        }
    }
}
