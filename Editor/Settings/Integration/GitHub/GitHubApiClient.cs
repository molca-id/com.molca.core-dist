using System;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using UnityEngine;

namespace Molca.Settings.Integration.GitHub
{
    /// <summary>
    /// Thin editor-only wrapper over <see cref="EditorHttpClient"/> for the GitHub REST API.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/GitHub/</c>.
    /// Registration: instantiated by <see cref="GitHubIntegrationProvider"/>; not an asset.
    /// <para>
    /// Personal access tokens (classic or fine-grained) are sent as a <c>Bearer</c> token. GitHub requires
    /// the <c>Accept: application/vnd.github+json</c>, <c>X-GitHub-Api-Version</c>, and <c>User-Agent</c>
    /// headers — the last is mandatory or the API returns 403. All methods honor a
    /// <see cref="CancellationToken"/>; cancellation surfaces as <see cref="OperationCanceledException"/> and
    /// is not a failure.
    /// </para>
    /// </remarks>
    public sealed class GitHubApiClient
    {
        private const string BaseUrl = "https://api.github.com";
        private const string ApiVersion = "2022-11-28";
        private const string UserAgent = "Molca-Editor";

        private readonly string _token;

        /// <summary>Creates a client bound to a GitHub credential.</summary>
        /// <param name="token">
        /// The credential value. GitHub sends both a personal access token and an OAuth access token as a
        /// <c>Bearer</c> token, so unlike Figma there is no per-kind header switch.
        /// </param>
        public GitHubApiClient(string token)
        {
            _token = token;
        }

        /// <summary>
        /// Returns the auth header name/value GitHub expects: both PAT and OAuth tokens are sent as
        /// <c>Authorization: Bearer</c>.
        /// </summary>
        /// <param name="kind">The credential kind (does not affect the header for GitHub).</param>
        /// <param name="token">The credential value.</param>
        /// <returns>The header name and value to set.</returns>
        public static (string Name, string Value) AuthHeader(
            Molca.Settings.Integration.OAuth.IntegrationCredentialKind kind, string token)
            => ("Authorization", $"Bearer {token}");

        /// <summary>Result of a non-deserializing call: HTTP success plus the created entity URL when available.</summary>
        public readonly struct Result
        {
            public Result(bool success, int statusCode, string url, string error)
            {
                Success = success;
                StatusCode = statusCode;
                Url = url;
                Error = error;
            }

            /// <summary>True when the request returned a 2xx status.</summary>
            public bool Success { get; }
            /// <summary>The HTTP status code.</summary>
            public int StatusCode { get; }
            /// <summary>The created entity's web URL, when the endpoint returns one.</summary>
            public string Url { get; }
            /// <summary>Error text when <see cref="Success"/> is false.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Fetches the authenticated user (<c>GET /user</c>) — the cheapest way to validate the token.
        /// </summary>
        /// <returns>The user, or <c>null</c> if the token is invalid or the call failed.</returns>
        internal async Awaitable<GitHubModels.AuthUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, "/user", null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<GitHubModels.AuthUser>(response.text);
        }

        /// <summary>
        /// Fetches a repository (<c>GET /repos/{owner}/{repo}</c>) to confirm access.
        /// </summary>
        /// <returns>The repository, or <c>null</c> if not found/accessible.</returns>
        internal async Awaitable<GitHubModels.Repository> GetRepositoryAsync(
            string owner, string repo, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, $"/repos/{owner}/{repo}", null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<GitHubModels.Repository>(response.text);
        }

        /// <summary>
        /// Opens an issue (<c>POST /repos/{owner}/{repo}/issues</c>).
        /// </summary>
        public async Awaitable<Result> CreateIssueAsync(
            string owner, string repo, string title, string body, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new GitHubModels.CreateIssueRequest
            {
                title = title,
                body = body ?? string.Empty
            });

            var response = await SendAsync(HttpMethod.POST, $"/repos/{owner}/{repo}/issues", payload, cancellationToken);
            return ToResult(response);
        }

        /// <summary>
        /// Publishes a release (<c>POST /repos/{owner}/{repo}/releases</c>).
        /// </summary>
        public async Awaitable<Result> CreateReleaseAsync(
            string owner, string repo, string tagName, string name, string body, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new GitHubModels.CreateReleaseRequest
            {
                tag_name = tagName,
                name = name,
                body = body ?? string.Empty
            });

            var response = await SendAsync(HttpMethod.POST, $"/repos/{owner}/{repo}/releases", payload, cancellationToken);
            return ToResult(response);
        }

        private async Awaitable<HttpResponse> SendAsync(
            HttpMethod method, string path, string jsonBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new HttpRequest
            {
                name = $"GitHub {method} {path}",
                method = method,
                url = BaseUrl + path,
                useFullUrl = true,
                expectedResponseType = ResponseType.Json
            };
            request.AddHeader("Authorization", $"Bearer {_token}");
            request.AddHeader("Accept", "application/vnd.github+json");
            request.AddHeader("X-GitHub-Api-Version", ApiVersion);
            request.AddHeader("User-Agent", UserAgent);

            if (!string.IsNullOrEmpty(jsonBody))
                request.SetJsonBody(jsonBody);

            // EditorHttpClient throws on transport errors; treat those as a failed (null) response so callers
            // can report a connection failure instead of crashing the editor flow.
            try
            {
                return await EditorHttpClient.SendAsync(request);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitHub] {method} {path} failed: {e.Message}");
                return null;
            }
        }

        private static Result ToResult(HttpResponse response)
        {
            if (response == null)
                return new Result(false, 0, null, "No response (transport error)");

            string url = null;
            if (response.isSuccess && !string.IsNullOrEmpty(response.text))
                url = SafeFromJson<GitHubModels.CreatedIssue>(response.text)?.html_url;

            return new Result(response.isSuccess, response.statusCode, url,
                response.isSuccess ? null : (response.errorMessage ?? response.statusMessage));
        }

        private static T SafeFromJson<T>(string json) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitHub] Failed to parse response: {e.Message}");
                return null;
            }
        }
    }
}

