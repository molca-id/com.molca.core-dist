using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using UnityEngine;

// Aliased rather than importing System.Diagnostics wholesale: that namespace also defines `Debug`, which would
// make every UnityEngine.Debug.LogWarning call in this file ambiguous.
using Stopwatch = System.Diagnostics.Stopwatch;

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
    /// <para>
    /// <b>Failures always carry a reason.</b> Reads return <see cref="ApiResult{T}"/> and writes return
    /// <see cref="Result"/>; both prefer ClickUp's own <c>err</c> text (see
    /// <see cref="ClickUpModels.ErrorResponse"/>) over the bare status code, because "Status not found" is
    /// actionable where "400" is not. No method returns an empty collection to mean "it failed" — an empty
    /// <see cref="ApiResult{T}.Value"/> on a successful result genuinely means the account has none.
    /// </para>
    /// <para>
    /// <b>Rate limiting.</b> ClickUp caps a personal token at roughly 100 requests/minute and answers 429 past
    /// that. <see cref="SendAsync"/> retries 429/503 a bounded number of times, honoring <c>Retry-After</c>
    /// when present and backing off exponentially when it is not.
    /// </para>
    /// <para>
    /// <b>Cancellation is cooperative, not immediate.</b> <see cref="EditorHttpClient.SendAsync"/> takes no
    /// token, so an in-flight HTTP request always runs to completion; the token is honored between attempts,
    /// during backoff waits, and between pagination pages. Callers therefore must not assume a cancelled
    /// operation has stopped talking to the network — only that its result will be discarded.
    /// </para>
    /// </remarks>
    public sealed class ClickUpApiClient
    {
        private const string BaseUrl = "https://api.clickup.com/api/v2";

        // Total attempts per request, including the first. Three keeps a transient 429 recoverable without
        // parking the editor on a service that is genuinely down.
        private const int MaxAttempts = 3;

        // Upper bound on an honored Retry-After, so a buggy or hostile header cannot stall the editor.
        private const float MaxRetryAfterSeconds = 30f;

        // The filtered task endpoint pages at 100. This cap bounds a pathological folder at 2000 tasks; hitting
        // it is reported rather than silently truncating (see GetTasksAsync).
        private const int MaxTaskPages = 20;

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
            /// <summary>Error text when <see cref="Success"/> is false — ClickUp's own message when it sent one.</summary>
            public string Error { get; }
        }

        /// <summary>Outcome of a read: the deserialized value, or a human-readable failure reason.</summary>
        /// <typeparam name="T">The deserialized payload type.</typeparam>
        /// <remarks>
        /// Exists so a failed read is distinguishable from a genuinely empty one. Callers that render "none
        /// found" must check <see cref="Success"/> first, or they will report an access failure as an empty
        /// account.
        /// </remarks>
        internal readonly struct ApiResult<T>
        {
            private ApiResult(bool success, T value, int statusCode, string error)
            {
                Success = success;
                Value = value;
                StatusCode = statusCode;
                Error = error;
            }

            /// <summary>True when the request completed with a 2xx status and parsed.</summary>
            public bool Success { get; }
            /// <summary>The payload; meaningful only when <see cref="Success"/> is true.</summary>
            public T Value { get; }
            /// <summary>The HTTP status code, or 0 for a transport failure.</summary>
            public int StatusCode { get; }
            /// <summary>The failure reason when <see cref="Success"/> is false.</summary>
            public string Error { get; }

            internal static ApiResult<T> Ok(T value) => new ApiResult<T>(true, value, 200, null);

            internal static ApiResult<T> Fail(int statusCode, string error)
                => new ApiResult<T>(false, default, statusCode, error);
        }

        /// <summary>
        /// Fetches the authorized user for the token (<c>GET /user</c>) — the cheapest way to validate it.
        /// </summary>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The user, or a failure carrying ClickUp's reason (e.g. an invalid token).</returns>
        internal async Awaitable<ApiResult<ClickUpModels.User>> GetAuthorizedUserAsync(
            CancellationToken cancellationToken = default)
        {
            var read = await ReadAsync<ClickUpModels.UserResponse>(HttpMethod.GET, "/user", cancellationToken);
            if (!read.Success)
                return ApiResult<ClickUpModels.User>.Fail(read.StatusCode, read.Error);

            var user = read.Value?.user;
            return user == null
                ? ApiResult<ClickUpModels.User>.Fail(read.StatusCode, "ClickUp returned no user for this token.")
                : ApiResult<ClickUpModels.User>.Ok(user);
        }

        /// <summary>
        /// Fetches the workspaces ("teams") the token can access (<c>GET /team</c>).
        /// </summary>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The workspaces, or a failure carrying ClickUp's reason.</returns>
        internal async Awaitable<ApiResult<ClickUpModels.Team[]>> GetTeamsAsync(
            CancellationToken cancellationToken = default)
        {
            var read = await ReadAsync<ClickUpModels.TeamsResponse>(HttpMethod.GET, "/team", cancellationToken);
            return read.Success
                ? ApiResult<ClickUpModels.Team[]>.Ok(read.Value?.teams ?? Array.Empty<ClickUpModels.Team>())
                : ApiResult<ClickUpModels.Team[]>.Fail(read.StatusCode, read.Error);
        }

        /// <summary>
        /// Fetches the spaces in a workspace (<c>GET /team/{teamId}/space</c>).
        /// </summary>
        /// <param name="teamId">The workspace ("team") id.</param>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The spaces, or a failure carrying ClickUp's reason.</returns>
        internal async Awaitable<ApiResult<ClickUpModels.Space[]>> GetSpacesAsync(
            string teamId, CancellationToken cancellationToken = default)
        {
            var read = await ReadAsync<ClickUpModels.SpacesResponse>(
                HttpMethod.GET, $"/team/{Uri.EscapeDataString(teamId ?? string.Empty)}/space", cancellationToken);
            return read.Success
                ? ApiResult<ClickUpModels.Space[]>.Ok(read.Value?.spaces ?? Array.Empty<ClickUpModels.Space>())
                : ApiResult<ClickUpModels.Space[]>.Fail(read.StatusCode, read.Error);
        }

        /// <summary>
        /// Fetches the folders in a space, each with its lists (<c>GET /space/{spaceId}/folder</c>).
        /// </summary>
        /// <param name="spaceId">The space id.</param>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The folders (with their <see cref="ClickUpModels.Folder.lists"/>), or a failure reason.</returns>
        internal async Awaitable<ApiResult<ClickUpModels.Folder[]>> GetFoldersAsync(
            string spaceId, CancellationToken cancellationToken = default)
        {
            var read = await ReadAsync<ClickUpModels.FoldersResponse>(
                HttpMethod.GET, $"/space/{Uri.EscapeDataString(spaceId ?? string.Empty)}/folder", cancellationToken);
            return read.Success
                ? ApiResult<ClickUpModels.Folder[]>.Ok(read.Value?.folders ?? Array.Empty<ClickUpModels.Folder>())
                : ApiResult<ClickUpModels.Folder[]>.Fail(read.StatusCode, read.Error);
        }

        /// <summary>
        /// Fetches a folder with its lists and status set (<c>GET /folder/{folderId}</c>).
        /// </summary>
        /// <param name="folderId">The ClickUp folder id (one folder per Unity project).</param>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The folder, or a failure carrying ClickUp's reason.</returns>
        /// <remarks>
        /// The folder-level status set is only populated when the folder overrides statuses; otherwise
        /// callers derive the available statuses from each <see cref="ClickUpModels.FolderList"/>.
        /// </remarks>
        internal async Awaitable<ApiResult<ClickUpModels.Folder>> GetFolderAsync(
            string folderId, CancellationToken cancellationToken = default)
            => await ReadAsync<ClickUpModels.Folder>(
                HttpMethod.GET, $"/folder/{Uri.EscapeDataString(folderId ?? string.Empty)}", cancellationToken);

        /// <summary>
        /// Fetches every task in a folder via the filtered team view
        /// (<c>GET /team/{teamId}/task?folder_ids[]={folderId}</c>), following pagination to the last page.
        /// </summary>
        /// <param name="teamId">The workspace ("team") id the folder belongs to.</param>
        /// <param name="folderId">The folder to scope tasks to.</param>
        /// <param name="assigneeUserId">When non-null, only tasks assigned to this user id are returned.</param>
        /// <param name="includeClosed">Whether tasks in a closed/done status are included.</param>
        /// <param name="cancellationToken">Cancels the fetch between pages; cancellation is not an error.</param>
        /// <returns>The matching tasks across all pages, or a failure when the first page fails.</returns>
        /// <remarks>
        /// The endpoint pages at 100 tasks and marks the end with <see cref="ClickUpModels.TasksResponse.last_page"/>.
        /// Before this followed pagination, a folder with more than 100 tasks silently showed only the first
        /// page. Truncation is now only possible at <see cref="MaxTaskPages"/>, and it is logged rather than
        /// silent. A page that fails <em>after</em> page 0 returns the tasks gathered so far and logs the gap,
        /// because discarding several hundred successfully fetched tasks is worse than reporting a partial list.
        /// </remarks>
        internal async Awaitable<ApiResult<ClickUpModels.ClickUpTask[]>> GetTasksAsync(
            string teamId, string folderId, long? assigneeUserId, bool includeClosed,
            CancellationToken cancellationToken = default)
        {
            var collected = new List<ClickUpModels.ClickUpTask>();
            bool sawLastPage = false;

            for (int page = 0; page < MaxTaskPages && !sawLastPage; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = await ReadAsync<ClickUpModels.TasksResponse>(
                    HttpMethod.GET,
                    BuildTaskQuery(teamId, folderId, assigneeUserId, includeClosed, page),
                    cancellationToken);

                if (!read.Success)
                {
                    // Page 0 failing means we have nothing to show — report it. A later page failing means we
                    // have a usable partial list; keep it, but never pretend it is complete.
                    if (page == 0)
                        return ApiResult<ClickUpModels.ClickUpTask[]>.Fail(read.StatusCode, read.Error);

                    Debug.LogWarning(
                        $"[ClickUp] Task page {page} failed ({read.StatusCode}): {read.Error}. "
                      + $"Showing the {collected.Count} task(s) fetched so far — the list is incomplete.");
                    break;
                }

                var pageTasks = read.Value?.tasks;
                if (pageTasks != null && pageTasks.Length > 0)
                    collected.AddRange(pageTasks);

                // Trust last_page, but also stop on an empty page so a backend that never sets the flag cannot
                // spin this loop to the cap.
                sawLastPage = (read.Value?.last_page ?? true) || pageTasks == null || pageTasks.Length == 0;

                if (!sawLastPage && page == MaxTaskPages - 1)
                {
                    Debug.LogWarning(
                        $"[ClickUp] Stopped after {MaxTaskPages} pages ({collected.Count} tasks) without reaching "
                      + "the last page. Narrow the view (assignee or closed-task filter) to see the rest.");
                }
            }

            return ApiResult<ClickUpModels.ClickUpTask[]>.Ok(collected.ToArray());
        }

        /// <summary>Builds one page of the filtered team-task query.</summary>
        /// <remarks>
        /// Internal rather than private so the EditMode tests can pin the query shape without a live request.
        /// The <c>page</c> parameter is the whole point of the pagination fix, and a silently dropped
        /// <c>page=</c> would look identical to the old truncating behavior.
        /// </remarks>
        internal static string BuildTaskQuery(
            string teamId, string folderId, long? assigneeUserId, bool includeClosed, int page)
        {
            var query = new StringBuilder(
                $"/team/{Uri.EscapeDataString(teamId ?? string.Empty)}/task"
              + $"?folder_ids[]={Uri.EscapeDataString(folderId ?? string.Empty)}");
            query.Append("&subtasks=true");
            query.Append($"&page={page}");
            if (includeClosed)
                query.Append("&include_closed=true");
            if (assigneeUserId.HasValue)
                query.Append($"&assignees[]={assigneeUserId.Value}");
            return query.ToString();
        }

        /// <summary>
        /// Creates a task in a list (<c>POST /list/{listId}/task</c>).
        /// </summary>
        /// <param name="listId">The destination ClickUp list id.</param>
        /// <param name="name">The task title.</param>
        /// <param name="markdownDescription">Optional Markdown task body.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        public async Awaitable<Result> CreateTaskAsync(
            string listId, string name, string markdownDescription, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.CreateTaskRequest
            {
                name = name,
                markdown_description = markdownDescription ?? string.Empty
            });

            var response = await SendAsync(
                HttpMethod.POST, $"/list/{Uri.EscapeDataString(listId ?? string.Empty)}/task",
                payload, cancellationToken);
            return ToResult(response);
        }

        /// <summary>
        /// Posts a comment on an existing task (<c>POST /task/{taskId}/comment</c>).
        /// </summary>
        /// <param name="taskId">The target task id.</param>
        /// <param name="commentText">The comment body.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <remarks>
        /// Used by <see cref="ClickUpIntegrationProvider.PushTarget"/> modes that report build/release activity
        /// onto the focused task instead of creating a new task per build.
        /// </remarks>
        public async Awaitable<Result> CreateTaskCommentAsync(
            string taskId, string commentText, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.CreateCommentRequest
            {
                comment_text = commentText,
                notify_all = false
            });

            var response = await SendAsync(
                HttpMethod.POST, $"/task/{Uri.EscapeDataString(taskId ?? string.Empty)}/comment",
                payload, cancellationToken);
            return ToResult(response);
        }

        /// <summary>
        /// Changes a task's status (<c>PUT /task/{taskId}</c>).
        /// </summary>
        /// <param name="taskId">The task to update.</param>
        /// <param name="statusName">The destination status name (must exist in the task's status set).</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        public async Awaitable<Result> UpdateTaskStatusAsync(
            string taskId, string statusName, CancellationToken cancellationToken = default)
        {
            var payload = JsonUtility.ToJson(new ClickUpModels.UpdateTaskStatusRequest
            {
                status = statusName
            });

            var response = await SendAsync(
                HttpMethod.PUT, $"/task/{Uri.EscapeDataString(taskId ?? string.Empty)}",
                payload, cancellationToken);
            return ToResult(response);
        }

        // Shared GET-and-deserialize path: one place that turns a response into either a parsed value or a
        // reason, so no read method can accidentally swallow a failure.
        private async Awaitable<ApiResult<T>> ReadAsync<T>(
            HttpMethod method, string path, CancellationToken cancellationToken) where T : class
        {
            var response = await SendAsync(method, path, null, cancellationToken);

            if (response == null)
                return ApiResult<T>.Fail(0, "No response (transport error) — check the network connection.");
            if (!response.isSuccess)
                return ApiResult<T>.Fail(response.statusCode, DescribeFailure(response));
            if (string.IsNullOrEmpty(response.text))
                return ApiResult<T>.Fail(response.statusCode, "ClickUp returned an empty response body.");

            var parsed = SafeFromJson<T>(response.text);
            return parsed == null
                ? ApiResult<T>.Fail(response.statusCode, "Could not parse ClickUp's response.")
                : ApiResult<T>.Ok(parsed);
        }

        private async Awaitable<HttpResponse> SendAsync(
            HttpMethod method, string path, string jsonBody, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Rebuilt per attempt rather than reused: HttpRequest carries mutable header/body state and is
                // not documented as safe to re-send.
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

                HttpResponse response;

                // EditorHttpClient throws on transport errors; treat those as a failed (null) response so
                // callers can report a connection failure instead of crashing the editor flow.
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
                    Debug.LogWarning($"[ClickUp] {method} {path} failed: {e.Message}");
                    return null;
                }

                if (attempt >= MaxAttempts || !IsRetryable(response))
                    return response;

                float wait = TryGetRetryAfterSeconds(response, out float retryAfter)
                    ? Mathf.Min(retryAfter, MaxRetryAfterSeconds)
                    : Mathf.Min(Mathf.Pow(2f, attempt), MaxRetryAfterSeconds);

                Debug.LogWarning(
                    $"[ClickUp] {method} {path} returned {response.statusCode} (rate limited); "
                  + $"retrying in {wait:0.#}s (attempt {attempt + 1} of {MaxAttempts}).");

                await DelayAsync(wait, cancellationToken);
            }
        }

        /// <summary>Waits roughly <paramref name="seconds"/> of wall-clock time, cancellably.</summary>
        /// <remarks>
        /// Deliberately built on <see cref="Awaitable.NextFrameAsync"/> plus a <see cref="Stopwatch"/> rather than
        /// <c>Awaitable.WaitForSecondsAsync</c>. This runs in the editor outside play mode, where time-based
        /// awaitables are driven by the player loop and cannot be relied on to resume — a backoff that never
        /// completes would hang the calling editor flow on exactly the rare path (a rate limit) that is hardest to
        /// reproduce. <c>NextFrameAsync</c> is the same primitive <see cref="EditorHttpClient"/> already polls on
        /// in edit mode, so it is known to tick here. <see cref="Stopwatch"/> rather than <c>Time.*</c> because the
        /// engine clock does not advance outside play mode.
        /// </remarks>
        private static async Awaitable DelayAsync(float seconds, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        // 429 is ClickUp's rate limit; 503 is a transient backend refusal. Everything else — including 401 and
        // 404 — is the caller's problem and must not be retried.
        private static bool IsRetryable(HttpResponse response)
            => response != null && (response.statusCode == 429 || response.statusCode == 503);

        // Reads Retry-After in either the delta-seconds or HTTP-date form (RFC 9110 §10.2.3). Deliberately a
        // local copy: the runtime HttpClient has an equivalent helper, but it is `internal` to the runtime
        // assembly and therefore unreachable from editor code.
        private static bool TryGetRetryAfterSeconds(HttpResponse response, out float seconds)
        {
            seconds = 0f;
            string value = response?.GetHeaderValue("Retry-After");
            if (string.IsNullOrEmpty(value))
                return false;

            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int delta))
            {
                seconds = Mathf.Max(0, delta);
                return true;
            }

            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
            {
                seconds = Mathf.Max(0f, (float)(when - DateTimeOffset.UtcNow).TotalSeconds);
                return true;
            }

            return false;
        }

        private static Result ToResult(HttpResponse response)
        {
            if (response == null)
                return new Result(false, 0, null, "No response (transport error)");

            string id = null;
            if (response.isSuccess && !string.IsNullOrEmpty(response.text))
                id = SafeFromJson<ClickUpModels.CreatedResponse>(response.text)?.id;

            return new Result(response.isSuccess, response.statusCode, id,
                response.isSuccess ? null : DescribeFailure(response));
        }

        /// <summary>
        /// Turns a failed response into the most specific message available: ClickUp's own <c>err</c> text
        /// (with its <c>ECODE</c>) when it sent one, else the transport error, else the status line.
        /// </summary>
        /// <remarks>
        /// Internal so the EditMode tests can assert this precedence order without a live request — the reason
        /// a user sees is the whole point of the method, so it is worth pinning down.
        /// </remarks>
        internal static string DescribeFailure(HttpResponse response)
        {
            if (response == null) return "No response (transport error)";

            // Only attempt a parse on something that actually looks like a JSON object. A proxy's HTML error
            // page would otherwise take the throwing path in SafeFromJson and log a parse warning on every
            // failed request — noise that says nothing about the real problem.
            if (LooksLikeJsonObject(response.text))
            {
                var error = SafeFromJson<ClickUpModels.ErrorResponse>(response.text);
                if (!string.IsNullOrWhiteSpace(error?.err))
                {
                    return string.IsNullOrWhiteSpace(error.ECODE)
                        ? error.err
                        : $"{error.err} ({error.ECODE})";
                }
            }

            if (!string.IsNullOrWhiteSpace(response.errorMessage)) return response.errorMessage;
            if (!string.IsNullOrWhiteSpace(response.statusMessage)) return response.statusMessage;
            return $"HTTP {response.statusCode}";
        }

        // Cheap shape test so a non-JSON body never reaches the parser. Not a validator — just enough to tell an
        // error envelope apart from an HTML page or a plain-text gateway message.
        private static bool LooksLikeJsonObject(string text)
            => !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{", StringComparison.Ordinal);

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
