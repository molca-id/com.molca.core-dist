using System;
using System.Text;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using UnityEngine;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Thin editor-only wrapper over <see cref="EditorHttpClient"/> for the ClickUp v2 REST API.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Registration: instantiated by <see cref="ClickUpIntegrationProvider"/>; not an asset.
    /// <para>
    /// ClickUp personal API tokens are sent as a raw <c>Authorization</c> header value (not a
    /// <c>Bearer</c> token). The token is supplied at construction; callers source it from
    /// <see cref="IntegrationCredentialStore"/>. All methods honor a <see cref="CancellationToken"/>;
    /// cancellation surfaces as <see cref="OperationCanceledException"/> and is not a failure.
    /// </para>
    /// </remarks>
    public sealed class ClickUpApiClient
    {
        private const string BaseUrl = "https://api.clickup.com/api/v2";

        private readonly string _token;

        /// <summary>Creates a client bound to a personal API token.</summary>
        /// <param name="token">The ClickUp personal API token (raw, not prefixed with "Bearer").</param>
        public ClickUpApiClient(string token)
        {
            _token = token;
        }

        /// <summary>Result of a non-deserializing call: HTTP success plus the created entity id when available.</summary>
        public readonly struct Result
        {
            public Result(bool success, int statusCode, string id, string error)
            {
                Success = success;
                StatusCode = statusCode;
                Id = id;
                Error = error;
            }

            /// <summary>True when the request returned a 2xx status.</summary>
            public bool Success { get; }
            /// <summary>The HTTP status code.</summary>
            public int StatusCode { get; }
            /// <summary>The created entity id, when the endpoint returns one.</summary>
            public string Id { get; }
            /// <summary>Error text when <see cref="Success"/> is false.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Fetches the authorized user for the token (<c>GET /user</c>) — the cheapest way to validate it.
        /// </summary>
        /// <returns>The user, or <c>null</c> if the token is invalid or the call failed.</returns>
        internal async Awaitable<ClickUpModels.User> GetAuthorizedUserAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, "/user", null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            var parsed = SafeFromJson<ClickUpModels.UserResponse>(response.text);
            return parsed?.user;
        }

        /// <summary>
        /// Fetches the workspaces ("teams") the token can access (<c>GET /team</c>).
        /// </summary>
        /// <returns>The workspaces, or an empty array on failure.</returns>
        internal async Awaitable<ClickUpModels.Team[]> GetTeamsAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, "/team", null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return Array.Empty<ClickUpModels.Team>();

            var parsed = SafeFromJson<ClickUpModels.TeamsResponse>(response.text);
            return parsed?.teams ?? Array.Empty<ClickUpModels.Team>();
        }

        /// <summary>
        /// Creates a task in a list (<c>POST /list/{listId}/task</c>).
        /// </summary>
        /// <param name="listId">The destination ClickUp list id.</param>
        /// <param name="name">The task title.</param>
        /// <param name="markdownDescription">Optional Markdown task body.</param>
        public async Awaitable<Result> CreateTaskAsync(
            string listId, string name, string markdownDescription, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.CreateTaskRequest
            {
                name = name,
                markdown_description = markdownDescription ?? string.Empty
            });

            var response = await SendAsync(HttpMethod.POST, $"/list/{listId}/task", payload, cancellationToken);
            return ToResult(response);
        }

        /// <summary>
        /// Posts a comment on an existing task (<c>POST /task/{taskId}/comment</c>).
        /// </summary>
        /// <param name="taskId">The target task id.</param>
        /// <param name="commentText">The comment body.</param>
        public async Awaitable<Result> CreateTaskCommentAsync(
            string taskId, string commentText, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.CreateCommentRequest
            {
                comment_text = commentText,
                notify_all = false
            });

            var response = await SendAsync(HttpMethod.POST, $"/task/{taskId}/comment", payload, cancellationToken);
            return ToResult(response);
        }

        /// <summary>
        /// Fetches a folder with its lists and status set (<c>GET /folder/{folderId}</c>).
        /// </summary>
        /// <param name="folderId">The ClickUp folder id (one folder per Unity project).</param>
        /// <returns>The folder, or <c>null</c> if the id is invalid or the call failed.</returns>
        /// <remarks>
        /// The folder-level status set is only populated when the folder overrides statuses; otherwise
        /// callers derive the available statuses from each <see cref="ClickUpModels.FolderList"/>.
        /// </remarks>
        internal async Awaitable<ClickUpModels.Folder> GetFolderAsync(
            string folderId, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(HttpMethod.GET, $"/folder/{folderId}", null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return null;

            return SafeFromJson<ClickUpModels.Folder>(response.text);
        }

        /// <summary>
        /// Fetches tasks in a folder via the filtered team view
        /// (<c>GET /team/{teamId}/task?folder_ids[]={folderId}</c>), optionally limited to one assignee.
        /// </summary>
        /// <param name="teamId">The workspace ("team") id the folder belongs to.</param>
        /// <param name="folderId">The folder to scope tasks to.</param>
        /// <param name="assigneeUserId">When non-null, only tasks assigned to this user id are returned.</param>
        /// <param name="includeClosed">Whether tasks in a closed/done status are included.</param>
        /// <returns>The matching tasks, or an empty array on failure.</returns>
        internal async Awaitable<ClickUpModels.ClickUpTask[]> GetTasksAsync(
            string teamId, string folderId, long? assigneeUserId, bool includeClosed,
            CancellationToken cancellationToken = default)
        {
            var query = new StringBuilder($"/team/{teamId}/task?folder_ids[]={Uri.EscapeDataString(folderId)}");
            query.Append("&subtasks=true");
            if (includeClosed)
                query.Append("&include_closed=true");
            if (assigneeUserId.HasValue)
                query.Append($"&assignees[]={assigneeUserId.Value}");

            var response = await SendAsync(HttpMethod.GET, query.ToString(), null, cancellationToken);
            if (response == null || !response.isSuccess || string.IsNullOrEmpty(response.text))
                return Array.Empty<ClickUpModels.ClickUpTask>();

            var parsed = SafeFromJson<ClickUpModels.TasksResponse>(response.text);
            return parsed?.tasks ?? Array.Empty<ClickUpModels.ClickUpTask>();
        }

        /// <summary>
        /// Changes a task's status (<c>PUT /task/{taskId}</c>).
        /// </summary>
        /// <param name="taskId">The task to update.</param>
        /// <param name="statusName">The destination status name (must exist in the task's status set).</param>
        public async Awaitable<Result> UpdateTaskStatusAsync(
            string taskId, string statusName, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.UpdateTaskStatusRequest
            {
                status = statusName
            });

            var response = await SendAsync(HttpMethod.PUT, $"/task/{taskId}", payload, cancellationToken);
            return ToResult(response);
        }

        private async Awaitable<HttpResponse> SendAsync(
            HttpMethod method, string path, string jsonBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new HttpRequest
            {
                name = $"ClickUp {method} {path}",
                method = method,
                url = BaseUrl + path,
                useFullUrl = true,
                expectedResponseType = ResponseType.Json
            };
            request.AddHeader("Authorization", _token);

            if (!string.IsNullOrEmpty(jsonBody))
                request.SetJsonBody(jsonBody);

            // EditorHttpClient throws on transport errors; treat those as a failed (null) response so
            // callers can report a connection failure instead of crashing the editor flow.
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
                Debug.LogWarning($"[ClickUp] {method} {path} failed: {e.Message}");
                return null;
            }
        }

        private static Result ToResult(HttpResponse response)
        {
            if (response == null)
                return new Result(false, 0, null, "No response (transport error)");

            string id = null;
            if (response.isSuccess && !string.IsNullOrEmpty(response.text))
                id = SafeFromJson<ClickUpModels.CreatedResponse>(response.text)?.id;

            return new Result(response.isSuccess, response.statusCode, id,
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
                Debug.LogWarning($"[ClickUp] Failed to parse response: {e.Message}");
                return null;
            }
        }
    }
}
