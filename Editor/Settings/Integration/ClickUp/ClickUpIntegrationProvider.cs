using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// ClickUp integration: connects with a personal API token, lists the project folder's tasks, and reports
    /// build/release activity either as a new task or as a comment on the focused task.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Base class: <see cref="IntegrationProvider"/>.
    /// Registration: add the asset to <see cref="IntegrationSettings"/>' provider list. The secret token is
    /// stored in <see cref="IntegrationCredentialStore"/> (per-machine, never committed); only non-secret
    /// config (target ids, push toggles, push target) is serialized on the asset. Personal preferences — the
    /// focused task and pinned tasks — live in <see cref="ClickUpTaskFocus"/> and are deliberately <b>not</b>
    /// serialized here, because they are per-developer and a committed field would churn across a team.
    /// <para>
    /// Connection state (<see cref="IsConnected"/>) is session-scoped: it reflects a token validated via
    /// <see cref="ConnectAsync"/> during this editor session, and resets on domain reload — it never makes a
    /// network call on the render path.
    /// </para>
    /// <para>
    /// <b>Session cache.</b> The authorized user and the accessible workspace list are stable for the life of a
    /// token, so they are cached after the first successful read. Without this, every task refresh — including
    /// every flip of the "Only my tasks" toggle — spent two extra round-trips re-deriving values that had not
    /// changed. The cache is dropped whenever the token changes and can be dropped explicitly via
    /// <see cref="InvalidateSessionCache"/>.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "ClickUp Integration", menuName = "Molca/Editor/Integrations/ClickUp", order = 110)]
    public class ClickUpIntegrationProvider : IntegrationProvider
    {
        /// <summary>The prefix every ClickUp personal API token carries.</summary>
        /// <remarks>
        /// Used for a cheap local sanity check before spending a round-trip. Checked as a warning, never as a
        /// hard rejection — ClickUp is free to mint a differently-prefixed token and this integration should not
        /// be the reason a valid one is refused.
        /// </remarks>
        public const string TokenPrefix = "pk_";

        /// <summary>The page a user creates a personal API token on.</summary>
        public const string TokenSettingsUrl = "https://app.clickup.com/settings/apps";

        [Header("Target")]
        [Tooltip("ClickUp list id that build/release tasks are posted to.")]
        [SerializeField] private string targetListId;

        [Tooltip("ClickUp folder id this project maps to. The Hub Tasks section lists tasks scoped to it.")]
        [SerializeField] private string targetFolderId;

        [Tooltip("ClickUp workspace ('team') id the target folder belongs to. Required when the token can " +
                 "access more than one workspace. Leave empty to use the first accessible workspace.")]
        [SerializeField] private string targetWorkspaceId;

        [Header("Automation")]
        [Tooltip("Post build activity to ClickUp when a build completes or fails.")]
        [SerializeField] private bool pushOnBuild = true;

        [Tooltip("Post the changelog entry to ClickUp when the project version is bumped.")]
        [SerializeField] private bool pushOnRelease = false;

        [Tooltip("Where activity is reported: a new task in the target list, or a comment on the task you " +
                 "currently have focused in Hub → Tasks.")]
        [SerializeField] private ClickUpPushTarget pushTarget = ClickUpPushTarget.NewTaskInList;

        // Session-scoped state; not serialized (resets on domain reload, as ConnectAsync repopulates it).
        [NonSerialized] private bool _connected;
        [NonSerialized] private string _connectedName;
        [NonSerialized] private string _connectedEmail;

        // Session cache — see the <remarks> on the class. Valid only while the stored token is unchanged.
        [NonSerialized] private ClickUpModels.User _cachedUser;
        [NonSerialized] private ClickUpModels.Team[] _cachedTeams;

        /// <inheritdoc/>
        public override string DisplayName => "ClickUp";

        /// <inheritdoc/>
        public override string Description => "Track tasks & report builds";

        /// <inheritdoc/>
        public override string Glyph => "C";

        /// <inheritdoc/>
        public override string GlyphColor => "rgb(123, 104, 238)";

        /// <summary>The ClickUp list id that new activity tasks are posted to.</summary>
        public string TargetListId => targetListId;

        /// <summary>The ClickUp folder id this project's tasks are scoped to (Hub Tasks section).</summary>
        public string TargetFolderId => targetFolderId;

        /// <summary>
        /// The ClickUp workspace ("team") id the target folder belongs to, or empty to use the first
        /// accessible workspace. Required when the token can reach more than one workspace, because the
        /// filtered task endpoint is workspace-scoped.
        /// </summary>
        public string TargetWorkspaceId => targetWorkspaceId;

        /// <summary>Where build/release activity is reported.</summary>
        public ClickUpPushTarget PushTarget => pushTarget;

        /// <summary>Whether the inbound task view can be populated: a token is stored and a folder is set.</summary>
        public bool CanViewTasks => HasToken && !string.IsNullOrEmpty(targetFolderId);

        /// <summary>Whether a build event should push to ClickUp.</summary>
        public bool PushOnBuild => pushOnBuild;

        /// <summary>Whether a version bump should push to ClickUp.</summary>
        public bool PushOnRelease => pushOnRelease;

        /// <summary>True once the stored token has been validated in this editor session.</summary>
        public override bool IsConnected => _connected;

        /// <summary>
        /// The email of the account the token belongs to, once verified this session; otherwise <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Surfaced alongside the username because the username alone does not answer the question people
        /// actually have about a stored token — <em>which account is this?</em> — on a workspace where several
        /// accounts share a display name.
        /// </remarks>
        public string ConnectedEmail => _connectedEmail;

        /// <inheritdoc/>
        public override string StatusMessage
        {
            get
            {
                if (_connected)
                    return string.IsNullOrEmpty(_connectedName) ? "Connected" : $"Connected as {_connectedName}";
                if (!IntegrationCredentialStore.HasToken(ProviderKey))
                    return "Not configured";
                return "Token saved — not verified";
            }
        }

        /// <summary>Whether a token is stored, regardless of whether it has been verified this session.</summary>
        public bool HasToken => IntegrationCredentialStore.HasToken(ProviderKey);

        /// <summary>Whether activity can actually be pushed right now (connected + a reachable destination).</summary>
        public bool CanPush => enabled && _connected && HasPushDestination;

        /// <summary>
        /// Whether the configured <see cref="PushTarget"/> currently has somewhere to write.
        /// </summary>
        /// <remarks>
        /// This is why a push target is not merely cosmetic: in <see cref="ClickUpPushTarget.NewTaskInList"/> a
        /// target list is required, while the comment modes require a focused task instead. Reporting readiness
        /// against the wrong one would either block a correctly configured project or promise a push that has
        /// nowhere to land.
        /// </remarks>
        public bool HasPushDestination => pushTarget switch
        {
            ClickUpPushTarget.NewTaskInList => !string.IsNullOrEmpty(targetListId),
            ClickUpPushTarget.CommentOnFocusedTask => ClickUpTaskFocus.HasFocus,
            ClickUpPushTarget.CommentOnFocusedTaskOrNewTask
                => ClickUpTaskFocus.HasFocus || !string.IsNullOrEmpty(targetListId),
            _ => false
        };

        /// <summary>
        /// Whether an automated build push should be attempted: enabled, opted in, a token is stored, and the
        /// push target has a destination. Unlike <see cref="CanPush"/> this does not require a session-verified
        /// connection — the API call validates the token itself, so automation works in a fresh editor session.
        /// </summary>
        public override bool ShouldPushOnBuild
            => enabled && pushOnBuild && HasToken && HasPushDestination;

        /// <summary>Whether an automated release (version-bump) push should be attempted.</summary>
        public override bool ShouldPushOnRelease
            => enabled && pushOnRelease && HasToken && HasPushDestination;

        /// <summary>Stores the personal API token. Pass null/empty to clear it; does not validate.</summary>
        public void SetToken(string token)
        {
            IntegrationCredentialStore.SetToken(ProviderKey, token);
            // A changed token invalidates both the verified session state and everything derived from it.
            _connected = false;
            _connectedName = null;
            _connectedEmail = null;
            InvalidateSessionCache();
        }

        /// <summary>
        /// Whether a token string looks like a ClickUp personal API token.
        /// </summary>
        /// <param name="token">The candidate token.</param>
        /// <returns><c>true</c> when it carries the expected <see cref="TokenPrefix"/>.</returns>
        /// <remarks>
        /// Advisory only. Callers should warn on <c>false</c> and still allow the save — see the remark on
        /// <see cref="TokenPrefix"/>. The check exists to catch the common mistakes (pasting a workspace id, an
        /// OAuth client id, or a truncated copy) without a round-trip.
        /// </remarks>
        public static bool LooksLikeToken(string token)
            => !string.IsNullOrWhiteSpace(token)
               && token.Trim().StartsWith(TokenPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Drops the cached authorized user and workspace list so the next call re-reads them from ClickUp.
        /// </summary>
        /// <remarks>Call after changing anything server-side that the cache would otherwise hide.</remarks>
        public void InvalidateSessionCache()
        {
            _cachedUser = null;
            _cachedTeams = null;
        }

        /// <summary>Creates an API client bound to the stored token, or <c>null</c> when no token is set.</summary>
        public ClickUpApiClient CreateClient()
        {
            var token = IntegrationCredentialStore.GetToken(ProviderKey);
            return string.IsNullOrEmpty(token) ? null : new ClickUpApiClient(token);
        }

        /// <inheritdoc/>
        public override async Awaitable<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            _connectedName = null;
            _connectedEmail = null;
            InvalidateSessionCache();

            var client = CreateClient();
            if (client == null)
            {
                Debug.LogWarning("[ClickUp] No API token set; cannot connect.");
                return false;
            }

            var user = await client.GetAuthorizedUserAsync(cancellationToken);
            if (!user.Success)
            {
                Debug.LogWarning($"[ClickUp] Connect failed: {user.Error}");
                return false;
            }

            _cachedUser = user.Value;
            _connected = true;
            _connectedName = !string.IsNullOrEmpty(user.Value.username) ? user.Value.username : user.Value.email;
            _connectedEmail = user.Value.email;
            return true;
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            IntegrationCredentialStore.ClearToken(ProviderKey);
            _connected = false;
            _connectedName = null;
            _connectedEmail = null;
            InvalidateSessionCache();
        }

        /// <inheritdoc/>
        public override async Awaitable PushBuildActivityAsync(
            BuildActivity activity, CancellationToken cancellationToken = default)
        {
            string title = activity.Succeeded
                ? $"Build succeeded: {activity.ProjectName} {activity.Version}"
                : $"Build {activity.Result}: {activity.ProjectName} {activity.Version}";

            var body = new StringBuilder();
            body.AppendLine($"**Project:** {activity.ProjectName}");
            body.AppendLine($"**Version:** {activity.Version}");
            body.AppendLine($"**Platform:** {activity.Platform}");
            body.AppendLine($"**Result:** {activity.Result}");
            body.AppendLine($"**Duration:** {activity.Duration.Minutes}m {activity.Duration.Seconds}s");
            if (activity.SizeBytes > 0)
                body.AppendLine($"**Size:** {activity.SizeBytes / (1024 * 1024)} MB");
            body.AppendLine($"**Errors:** {activity.Errors}");
            body.AppendLine($"**Triggered by:** {activity.TriggeredBy}");

            await PostActivityAsync(title, body.ToString(), "build", cancellationToken);
        }

        /// <inheritdoc/>
        public override async Awaitable PushReleaseActivityAsync(
            ReleaseActivity activity, CancellationToken cancellationToken = default)
        {
            string title = $"Release {activity.Version}: {activity.ProjectName}";

            var body = new StringBuilder();
            body.AppendLine($"**Project:** {activity.ProjectName}");
            body.AppendLine($"**Version:** {activity.Version}");
            body.AppendLine($"**Released by:** {activity.TriggeredBy}");
            if (!string.IsNullOrWhiteSpace(activity.Notes))
            {
                body.AppendLine();
                body.AppendLine(activity.Notes.Trim());
            }

            await PostActivityAsync(title, body.ToString(), "release", cancellationToken);
        }

        // Shared post path for build/release activity, routed by PushTarget. Failures are logged, never thrown
        // into the router's fire-and-forget call (cancellation is rethrown so the router can ignore it quietly).
        private async Awaitable PostActivityAsync(
            string title, string markdown, string kind, CancellationToken cancellationToken)
        {
            var client = CreateClient();
            if (client == null) return;

            string focusedTaskId = ClickUpTaskFocus.FocusedTaskId;
            bool wantsComment = pushTarget != ClickUpPushTarget.NewTaskInList;

            if (wantsComment && !string.IsNullOrEmpty(focusedTaskId))
            {
                // The comment leads with the title because a ClickUp comment has no subject line of its own.
                string comment = $"**{title}**\n\n{markdown}";
                var commented = await client.CreateTaskCommentAsync(focusedTaskId, comment, cancellationToken);
                if (commented.Success)
                {
                    Debug.Log($"[ClickUp] Commented {kind} activity on focused task "
                            + $"'{ClickUpTaskFocus.FocusedTaskName ?? focusedTaskId}'.");
                    return;
                }

                Debug.LogWarning($"[ClickUp] {kind} comment failed ({commented.StatusCode}): {commented.Error}");

                // CommentOnFocusedTask is the quiet mode: a failed comment must not silently become a new task.
                if (pushTarget == ClickUpPushTarget.CommentOnFocusedTask) return;
            }
            else if (pushTarget == ClickUpPushTarget.CommentOnFocusedTask)
            {
                // Nothing focused and no fallback requested — reporting nothing is the configured behavior.
                return;
            }

            if (string.IsNullOrEmpty(targetListId))
            {
                Debug.LogWarning(
                    $"[ClickUp] Skipped the {kind} push: no target list is configured and "
                  + (wantsComment ? "no task is focused." : "the push target requires one."));
                return;
            }

            var result = await client.CreateTaskAsync(targetListId, title, markdown, cancellationToken);
            if (result.Success)
                Debug.Log($"[ClickUp] Posted {kind} task '{title}'.");
            else
                Debug.LogWarning($"[ClickUp] {kind} push failed ({result.StatusCode}): {result.Error}");
        }

        // ---- Inbound: folder-scoped task viewing & status change ---------------------------------------

        /// <summary>Outcome of a task fetch: the tasks, the status set to populate dropdowns, and an error.</summary>
        /// <remarks>Internal because it surfaces internal <see cref="ClickUpModels"/> DTOs.</remarks>
        internal readonly struct TaskFetchResult
        {
            internal TaskFetchResult(
                bool success, ClickUpModels.ClickUpTask[] tasks, ClickUpModels.TaskStatus[] statuses,
                string folderName, string error)
            {
                Success = success;
                Tasks = tasks ?? Array.Empty<ClickUpModels.ClickUpTask>();
                Statuses = statuses ?? Array.Empty<ClickUpModels.TaskStatus>();
                FolderName = folderName;
                Error = error;
            }

            /// <summary>True when the fetch completed (an empty task list is still a success).</summary>
            public bool Success { get; }
            /// <summary>The fetched tasks.</summary>
            public ClickUpModels.ClickUpTask[] Tasks { get; }
            /// <summary>
            /// The distinct statuses available in the folder, ordered by ClickUp's <c>orderindex</c>. The array
            /// order is the workflow order, so a caller may use an index into it as a sort key.
            /// </summary>
            /// <remarks>
            /// Carries the full <see cref="ClickUpModels.TaskStatus"/> — not just the name — because a UI that
            /// lets the user change a status has to re-render the moved row in the <em>destination</em> status's
            /// color. With names alone the only available color was the one the task already had, so a moved row
            /// kept its old tint and looked like it had not moved at all.
            /// </remarks>
            public ClickUpModels.TaskStatus[] Statuses { get; }
            /// <summary>The folder's display name, for labelling the view; may be <c>null</c>.</summary>
            public string FolderName { get; }
            /// <summary>A human-readable failure reason when <see cref="Success"/> is false.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Fetches the tasks shown in the Hub Tasks section: scoped to <see cref="TargetFolderId"/> and, by
        /// default, assigned to the token's user. Also resolves the folder's status set for the dropdowns.
        /// </summary>
        /// <param name="onlyMine">When true (default), limits results to the authorized user's tasks.</param>
        /// <param name="includeClosed">Whether tasks in a closed/done status are included.</param>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <remarks>
        /// The workspace and the authorized user come from the session cache, so a repeat fetch (a filter flip,
        /// a manual refresh) costs the folder read plus the task pages and nothing else. Failures surface in
        /// <see cref="TaskFetchResult.Error"/> with ClickUp's own wording;
        /// <see cref="OperationCanceledException"/> is rethrown so callers can ignore it quietly.
        /// </remarks>
        internal async Awaitable<TaskFetchResult> FetchTasksAsync(
            bool onlyMine = true, bool includeClosed = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(targetFolderId))
                return Failed("No target folder id is configured.");

            var client = CreateClient();
            if (client == null)
                return Failed("No API token is stored.");

            var team = await ResolveWorkspaceIdAsync(client, cancellationToken);
            if (!team.Success)
                return Failed(team.Error);

            long? assignee = null;
            if (onlyMine)
            {
                var user = await GetUserAsync(client, cancellationToken);
                if (!user.Success)
                    return Failed($"Could not resolve the token's user: {user.Error}");
                assignee = user.Value.id;
            }

            var folder = await client.GetFolderAsync(targetFolderId, cancellationToken);
            if (!folder.Success)
                return Failed($"Could not read the target folder: {folder.Error}");

            var tasks = await client.GetTasksAsync(
                team.Value, targetFolderId, assignee, includeClosed, cancellationToken);
            if (!tasks.Success)
                return Failed(tasks.Error);

            return new TaskFetchResult(
                true, tasks.Value, ExtractStatuses(folder.Value), folder.Value?.name, null);

            static TaskFetchResult Failed(string error) => new TaskFetchResult(false, null, null, null, error);
        }

        /// <summary>A workspace the token can access, for the inspector's workspace picker.</summary>
        public readonly struct WorkspaceInfo
        {
            internal WorkspaceInfo(string id, string name)
            {
                Id = id;
                Name = name;
            }

            /// <summary>The workspace ("team") id.</summary>
            public string Id { get; }
            /// <summary>The workspace display name.</summary>
            public string Name { get; }
        }

        /// <summary>Outcome of a workspace listing: the workspaces, or why the listing failed.</summary>
        /// <remarks>
        /// Distinguishing "this token reaches no workspaces" from "the listing call failed" matters: the first
        /// is an account problem and the second is usually a bad token or no network. Reporting both as an empty
        /// list — the previous behavior — sent users to fix the wrong thing.
        /// </remarks>
        public readonly struct WorkspaceFetchResult
        {
            internal WorkspaceFetchResult(bool success, WorkspaceInfo[] workspaces, string error)
            {
                Success = success;
                Workspaces = workspaces ?? Array.Empty<WorkspaceInfo>();
                Error = error;
            }

            /// <summary>True when the listing completed (an empty list is still a success).</summary>
            public bool Success { get; }
            /// <summary>The accessible workspaces.</summary>
            public WorkspaceInfo[] Workspaces { get; }
            /// <summary>A human-readable failure reason when <see cref="Success"/> is false.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Fetches the workspaces the stored token can access, so the inspector can offer a picker instead
        /// of requiring the user to find the workspace id by hand.
        /// </summary>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The accessible workspaces, or a failure reason.</returns>
        public async Awaitable<WorkspaceFetchResult> FetchWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null)
                return new WorkspaceFetchResult(false, null, "No API token is stored.");

            var teams = await GetTeamsAsync(client, cancellationToken);
            if (!teams.Success)
                return new WorkspaceFetchResult(false, null, teams.Error);

            var result = new WorkspaceInfo[teams.Value.Length];
            for (int i = 0; i < teams.Value.Length; i++)
                result[i] = new WorkspaceInfo(teams.Value[i].id, teams.Value[i].name);
            return new WorkspaceFetchResult(true, result, null);
        }

        /// <summary>A list within a folder, for the inspector's list picker.</summary>
        public readonly struct ListInfo
        {
            internal ListInfo(string id, string name)
            {
                Id = id;
                Name = name;
            }

            /// <summary>The list id.</summary>
            public string Id { get; }
            /// <summary>The list display name.</summary>
            public string Name { get; }
        }

        /// <summary>
        /// A folder in the workspace, flattened across spaces, with its lists — for the inspector's folder
        /// and list pickers.
        /// </summary>
        public readonly struct FolderInfo
        {
            internal FolderInfo(string id, string name, ListInfo[] lists)
            {
                Id = id;
                Name = name;
                Lists = lists ?? Array.Empty<ListInfo>();
            }

            /// <summary>The folder id.</summary>
            public string Id { get; }
            /// <summary>The display name, qualified by space as "Space / Folder" to disambiguate.</summary>
            public string Name { get; }
            /// <summary>The lists inside this folder.</summary>
            public ListInfo[] Lists { get; }
        }

        /// <summary>Outcome of a folder listing: the folders, or why the listing failed.</summary>
        public readonly struct FolderFetchResult
        {
            internal FolderFetchResult(bool success, FolderInfo[] folders, string error)
            {
                Success = success;
                Folders = folders ?? Array.Empty<FolderInfo>();
                Error = error;
            }

            /// <summary>True when the listing completed (an empty list is still a success).</summary>
            public bool Success { get; }
            /// <summary>The folders found, qualified by space name.</summary>
            public FolderInfo[] Folders { get; }
            /// <summary>A human-readable failure reason when <see cref="Success"/> is false.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Fetches every folder in a workspace (flattened across its spaces), each carrying its lists, for the
        /// inspector's cascading Workspace → Folder → List pickers.
        /// </summary>
        /// <param name="workspaceId">The workspace ("team") id to enumerate folders for.</param>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>
        /// The folders qualified by their space name ("Space / Folder"), ordered by space then folder, or a
        /// failure reason.
        /// </returns>
        /// <remarks>
        /// A space whose folder listing fails is skipped with a warning rather than failing the whole call — one
        /// space the token cannot read should not hide every folder in the workspace. The call only fails
        /// outright when the space listing itself fails.
        /// </remarks>
        public async Awaitable<FolderFetchResult> FetchFoldersAsync(
            string workspaceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(workspaceId))
                return new FolderFetchResult(false, null, "No workspace is selected.");

            var client = CreateClient();
            if (client == null)
                return new FolderFetchResult(false, null, "No API token is stored.");

            var spaces = await client.GetSpacesAsync(workspaceId, cancellationToken);
            if (!spaces.Success)
                return new FolderFetchResult(false, null, spaces.Error);

            var result = new List<FolderInfo>();

            foreach (var space in spaces.Value)
            {
                if (space == null) continue;

                var folders = await client.GetFoldersAsync(space.id, cancellationToken);
                if (!folders.Success)
                {
                    Debug.LogWarning(
                        $"[ClickUp] Skipped space '{space.name ?? space.id}': {folders.Error}");
                    continue;
                }

                foreach (var folder in folders.Value)
                {
                    if (folder == null) continue;

                    var lists = Array.Empty<ListInfo>();
                    if (folder.lists != null)
                    {
                        lists = new ListInfo[folder.lists.Length];
                        for (int i = 0; i < folder.lists.Length; i++)
                            lists[i] = new ListInfo(folder.lists[i]?.id, folder.lists[i]?.name);
                    }

                    string display = string.IsNullOrEmpty(space.name) ? folder.name : $"{space.name} / {folder.name}";
                    result.Add(new FolderInfo(folder.id, display, lists));
                }
            }

            return new FolderFetchResult(true, result.ToArray(), null);
        }

        /// <summary>
        /// Changes a task's status in ClickUp.
        /// </summary>
        /// <param name="taskId">The task to update.</param>
        /// <param name="statusName">The destination status name.</param>
        /// <param name="cancellationToken">Cancels the update; cancellation is not an error.</param>
        /// <returns>The outcome, carrying ClickUp's reason when it failed.</returns>
        public async Awaitable<ClickUpApiClient.Result> SetTaskStatusAsync(
            string taskId, string statusName, CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null)
                return new ClickUpApiClient.Result(false, 0, null, "No API token is stored.");

            var result = await client.UpdateTaskStatusAsync(taskId, statusName, cancellationToken);
            if (!result.Success)
                Debug.LogWarning($"[ClickUp] Status change failed ({result.StatusCode}): {result.Error}");
            return result;
        }

        /// <summary>
        /// Creates a task in a list, defaulting to <see cref="TargetListId"/>.
        /// </summary>
        /// <param name="name">The task title.</param>
        /// <param name="markdownDescription">Optional Markdown body.</param>
        /// <param name="listId">The destination list; falls back to <see cref="TargetListId"/> when empty.</param>
        /// <param name="cancellationToken">Cancels the request; cancellation is not an error.</param>
        /// <returns>The outcome, carrying the new task id on success and ClickUp's reason on failure.</returns>
        public async Awaitable<ClickUpApiClient.Result> CreateTaskAsync(
            string name, string markdownDescription = null, string listId = null,
            CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null)
                return new ClickUpApiClient.Result(false, 0, null, "No API token is stored.");

            string destination = string.IsNullOrWhiteSpace(listId) ? targetListId : listId;
            if (string.IsNullOrWhiteSpace(destination))
            {
                return new ClickUpApiClient.Result(
                    false, 0, null, "No list id — set a Target List on the ClickUp integration.");
            }

            return await client.CreateTaskAsync(destination, name, markdownDescription, cancellationToken);
        }

        // Resolves the workspace id to query: the configured one (verified reachable) or the first accessible.
        // One cached /team read serves both paths — the previous version issued the same request in each branch.
        private async Awaitable<ClickUpApiClient.ApiResult<string>> ResolveWorkspaceIdAsync(
            ClickUpApiClient client, CancellationToken cancellationToken)
        {
            var teams = await GetTeamsAsync(client, cancellationToken);
            if (!teams.Success)
                return ClickUpApiClient.ApiResult<string>.Fail(teams.StatusCode, teams.Error);

            if (teams.Value.Length == 0)
            {
                return ClickUpApiClient.ApiResult<string>.Fail(
                    0, "No accessible workspace — check the token.");
            }

            if (string.IsNullOrEmpty(targetWorkspaceId))
                return ClickUpApiClient.ApiResult<string>.Ok(teams.Value[0].id);

            if (!Array.Exists(teams.Value, t => t != null && t.id == targetWorkspaceId))
            {
                return ClickUpApiClient.ApiResult<string>.Fail(
                    0, $"Workspace id '{targetWorkspaceId}' isn't accessible with this token.");
            }

            return ClickUpApiClient.ApiResult<string>.Ok(targetWorkspaceId);
        }

        // Cached /team read. The workspace list a token can reach does not change within an editor session in
        // any way this integration needs to observe.
        private async Awaitable<ClickUpApiClient.ApiResult<ClickUpModels.Team[]>> GetTeamsAsync(
            ClickUpApiClient client, CancellationToken cancellationToken)
        {
            if (_cachedTeams != null)
                return ClickUpApiClient.ApiResult<ClickUpModels.Team[]>.Ok(_cachedTeams);

            var teams = await client.GetTeamsAsync(cancellationToken);
            if (teams.Success)
                _cachedTeams = teams.Value;
            return teams;
        }

        // Cached /user read, shared by ConnectAsync's verification and the "only my tasks" filter.
        private async Awaitable<ClickUpApiClient.ApiResult<ClickUpModels.User>> GetUserAsync(
            ClickUpApiClient client, CancellationToken cancellationToken)
        {
            if (_cachedUser != null)
                return ClickUpApiClient.ApiResult<ClickUpModels.User>.Ok(_cachedUser);

            var user = await client.GetAuthorizedUserAsync(cancellationToken);
            if (user.Success)
                _cachedUser = user.Value;
            return user;
        }

        // Folder-level statuses are authoritative only when the folder overrides statuses; otherwise the set is
        // unioned across the folder's lists. Each source is sorted by orderindex first so the resulting array is
        // in workflow order (open → in progress → done) and can be used as a sort key. Deduplication keeps the
        // first definition of a name, so the retained entry is the one carrying that status's color.
        // Internal rather than private so the EditMode tests can pin the order and the surviving colors: the Hub
        // re-tints a moved row from these definitions, so losing either would silently reintroduce the bug where
        // a re-statused task kept its old color and looked unmoved.
        internal static ClickUpModels.TaskStatus[] ExtractStatuses(ClickUpModels.Folder folder)
        {
            if (folder == null) return Array.Empty<ClickUpModels.TaskStatus>();

            var sources = new List<ClickUpModels.TaskStatus[]>();
            if (folder.override_statuses && folder.statuses != null)
            {
                sources.Add(folder.statuses);
            }
            else if (folder.lists != null)
            {
                foreach (var list in folder.lists)
                {
                    if (list?.statuses != null) sources.Add(list.statuses);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ClickUpModels.TaskStatus>();
            foreach (var source in sources)
            {
                var ordered = new List<ClickUpModels.TaskStatus>(source);
                ordered.Sort((a, b) => (a?.orderindex ?? 0).CompareTo(b?.orderindex ?? 0));

                foreach (var status in ordered)
                {
                    if (status == null || string.IsNullOrEmpty(status.status)) continue;
                    if (seen.Add(status.status)) result.Add(status);
                }
            }
            return result.ToArray();
        }
    }
}
