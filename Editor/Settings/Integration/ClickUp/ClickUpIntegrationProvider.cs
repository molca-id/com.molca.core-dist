using System;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// ClickUp integration: connects with a personal API token and pushes build/release activity.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Base class: <see cref="IntegrationProvider"/>.
    /// Registration: add the asset to <see cref="IntegrationSettings"/>' provider list. The secret token is
    /// stored in <see cref="IntegrationCredentialStore"/> (per-machine, never committed); only non-secret
    /// config (target list id, push toggles) is serialized on the asset.
    /// <para>
    /// Connection state (<see cref="IsConnected"/>) is session-scoped: it reflects a token validated via
    /// <see cref="ConnectAsync"/> during this editor session, and resets on domain reload — it never makes a
    /// network call on the render path.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "ClickUp Integration", menuName = "Molca/Editor/Integrations/ClickUp", order = 110)]
    public class ClickUpIntegrationProvider : IntegrationProvider
    {
        [Header("Target")]
        [Tooltip("ClickUp list id that build/release tasks and comments are posted to.")]
        [SerializeField] private string targetListId;

        [Tooltip("ClickUp folder id this project maps to. The Hub Tasks section lists tasks scoped to it.")]
        [SerializeField] private string targetFolderId;

        [Tooltip("ClickUp workspace ('team') id the target folder belongs to. Required when the token can " +
                 "access more than one workspace. Leave empty to use the first accessible workspace.")]
        [SerializeField] private string targetWorkspaceId;

        [Header("Automation")]
        [Tooltip("Post a task/comment to the target list when a build completes or fails.")]
        [SerializeField] private bool pushOnBuild = true;

        [Tooltip("Post the changelog entry to the target list when the project version is bumped.")]
        [SerializeField] private bool pushOnRelease = false;

        // Session-scoped cache; not serialized (resets on domain reload, as ConnectAsync repopulates it).
        [NonSerialized] private bool _connected;
        [NonSerialized] private string _connectedName;

        /// <inheritdoc/>
        public override string DisplayName => "ClickUp";

        /// <inheritdoc/>
        public override string Description => "Push build tasks & changelog";

        /// <inheritdoc/>
        public override string Glyph => "C";

        /// <inheritdoc/>
        public override string GlyphColor => "rgb(123, 104, 238)";

        /// <summary>The ClickUp list id that activity is posted to.</summary>
        public string TargetListId => targetListId;

        /// <summary>The ClickUp folder id this project's tasks are scoped to (Hub Tasks section).</summary>
        public string TargetFolderId => targetFolderId;

        /// <summary>
        /// The ClickUp workspace ("team") id the target folder belongs to, or empty to use the first
        /// accessible workspace. Required when the token can reach more than one workspace, because the
        /// filtered task endpoint is workspace-scoped.
        /// </summary>
        public string TargetWorkspaceId => targetWorkspaceId;

        /// <summary>Whether the inbound task view can be populated: a token is stored and a folder is set.</summary>
        public bool CanViewTasks => HasToken && !string.IsNullOrEmpty(targetFolderId);

        /// <summary>Whether a build event should push to ClickUp.</summary>
        public bool PushOnBuild => pushOnBuild;

        /// <summary>Whether a version bump should push to ClickUp.</summary>
        public bool PushOnRelease => pushOnRelease;

        /// <summary>True once the stored token has been validated in this editor session.</summary>
        public override bool IsConnected => _connected;

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

        /// <summary>Whether activity can actually be pushed right now (connected + a target list set).</summary>
        public bool CanPush => enabled && _connected && !string.IsNullOrEmpty(targetListId);

        /// <summary>
        /// Whether an automated build push should be attempted: enabled, opted in, a token is stored, and a
        /// target list is configured. Unlike <see cref="CanPush"/> this does not require a session-verified
        /// connection — the API call validates the token itself, so automation works in a fresh editor session.
        /// </summary>
        public override bool ShouldPushOnBuild
            => enabled && pushOnBuild && HasToken && !string.IsNullOrEmpty(targetListId);

        /// <summary>Whether an automated release (version-bump) push should be attempted.</summary>
        public override bool ShouldPushOnRelease
            => enabled && pushOnRelease && HasToken && !string.IsNullOrEmpty(targetListId);

        /// <summary>Stores the personal API token. Pass null/empty to clear it; does not validate.</summary>
        public void SetToken(string token)
        {
            IntegrationCredentialStore.SetToken(ProviderKey, token);
            // A changed token invalidates the previously verified session state.
            _connected = false;
            _connectedName = null;
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

            var client = CreateClient();
            if (client == null)
            {
                Debug.LogWarning("[ClickUp] No API token set; cannot connect.");
                return false;
            }

            var user = await client.GetAuthorizedUserAsync(cancellationToken);
            if (user == null)
                return false;

            _connected = true;
            _connectedName = !string.IsNullOrEmpty(user.username) ? user.username : user.email;
            return true;
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            IntegrationCredentialStore.ClearToken(ProviderKey);
            _connected = false;
            _connectedName = null;
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

            await PostTaskAsync(title, body.ToString(), "build", cancellationToken);
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

            await PostTaskAsync(title, body.ToString(), "release", cancellationToken);
        }

        // Shared post path for build/release activity. Failures are logged, never thrown into the router's
        // fire-and-forget call (cancellation is rethrown so the router can ignore it quietly).
        private async Awaitable PostTaskAsync(string title, string markdown, string kind, CancellationToken cancellationToken)
        {
            var client = CreateClient();
            if (client == null) return;

            var result = await client.CreateTaskAsync(targetListId, title, markdown, cancellationToken);
            if (result.Success)
                Debug.Log($"[ClickUp] Posted {kind} task '{title}'.");
            else
                Debug.LogWarning($"[ClickUp] {kind} push failed ({result.StatusCode}): {result.Error}");
        }

        // ---- Inbound: folder-scoped task viewing & status change (Sprint 31) ---------------------------

        /// <summary>Outcome of a task fetch: the tasks, the status set to populate dropdowns, and an error.</summary>
        /// <remarks>Internal because it surfaces internal <see cref="ClickUpModels"/> DTOs.</remarks>
        internal readonly struct TaskFetchResult
        {
            internal TaskFetchResult(bool success, ClickUpModels.ClickUpTask[] tasks, string[] statuses, string error)
            {
                Success = success;
                Tasks = tasks ?? Array.Empty<ClickUpModels.ClickUpTask>();
                Statuses = statuses ?? Array.Empty<string>();
                Error = error;
            }

            /// <summary>True when the fetch completed (an empty task list is still a success).</summary>
            public bool Success { get; }
            /// <summary>The fetched tasks.</summary>
            public ClickUpModels.ClickUpTask[] Tasks { get; }
            /// <summary>Distinct status names available in the folder, ordered, for status dropdowns.</summary>
            public string[] Statuses { get; }
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
        /// Resolves the workspace ("team") via <c>GET /team</c> and uses the first one — this mirrors the
        /// "one project = one folder" assumption. Failures surface in <see cref="TaskFetchResult.Error"/>;
        /// <see cref="OperationCanceledException"/> is rethrown so callers can ignore it quietly.
        /// </remarks>
        internal async Awaitable<TaskFetchResult> FetchTasksAsync(
            bool onlyMine = true, bool includeClosed = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(targetFolderId))
                return new TaskFetchResult(false, null, null, "No target folder id is configured.");

            var client = CreateClient();
            if (client == null)
                return new TaskFetchResult(false, null, null, "No API token is stored.");

            // Workspace is configurable because the filtered task endpoint is workspace-scoped and a token
            // may reach several workspaces. When set, trust it (and verify it's reachable); otherwise fall
            // back to the first accessible workspace.
            string teamId = targetWorkspaceId;
            if (string.IsNullOrEmpty(teamId))
            {
                var teams = await client.GetTeamsAsync(cancellationToken);
                if (teams.Length == 0)
                    return new TaskFetchResult(false, null, null, "No accessible workspace — check the token.");
                teamId = teams[0].id;
            }
            else
            {
                var teams = await client.GetTeamsAsync(cancellationToken);
                if (teams.Length == 0)
                    return new TaskFetchResult(false, null, null, "No accessible workspace — check the token.");
                if (!Array.Exists(teams, t => t != null && t.id == teamId))
                    return new TaskFetchResult(false, null, null,
                        $"Workspace id '{teamId}' isn't accessible with this token.");
            }

            long? assignee = null;
            if (onlyMine)
            {
                var user = await client.GetAuthorizedUserAsync(cancellationToken);
                if (user == null)
                    return new TaskFetchResult(false, null, null, "Could not resolve the token's user.");
                assignee = user.id;
            }

            var folder = await client.GetFolderAsync(targetFolderId, cancellationToken);
            var statuses = ExtractStatusNames(folder);

            var tasks = await client.GetTasksAsync(teamId, targetFolderId, assignee, includeClosed, cancellationToken);
            return new TaskFetchResult(true, tasks, statuses, null);
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

        /// <summary>
        /// Fetches the workspaces the stored token can access, so the inspector can offer a picker instead
        /// of requiring the user to find the workspace id by hand.
        /// </summary>
        /// <param name="cancellationToken">Cancels the fetch; cancellation is not an error.</param>
        /// <returns>The accessible workspaces, or an empty array when no token is set or the call failed.</returns>
        public async Awaitable<WorkspaceInfo[]> FetchWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null) return Array.Empty<WorkspaceInfo>();

            var teams = await client.GetTeamsAsync(cancellationToken);
            var result = new WorkspaceInfo[teams.Length];
            for (int i = 0; i < teams.Length; i++)
                result[i] = new WorkspaceInfo(teams[i].id, teams[i].name);
            return result;
        }

        /// <summary>
        /// Changes a task's status in ClickUp.
        /// </summary>
        /// <param name="taskId">The task to update.</param>
        /// <param name="statusName">The destination status name.</param>
        /// <param name="cancellationToken">Cancels the update; cancellation is not an error.</param>
        /// <returns><c>true</c> if the status was changed.</returns>
        public async Awaitable<bool> SetTaskStatusAsync(
            string taskId, string statusName, CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null) return false;

            var result = await client.UpdateTaskStatusAsync(taskId, statusName, cancellationToken);
            if (!result.Success)
                Debug.LogWarning($"[ClickUp] Status change failed ({result.StatusCode}): {result.Error}");
            return result.Success;
        }

        // Folder-level statuses are authoritative only when the folder overrides statuses; otherwise the
        // set is unioned across the folder's lists. Order is preserved by orderindex within each source.
        private static string[] ExtractStatusNames(ClickUpModels.Folder folder)
        {
            if (folder == null) return Array.Empty<string>();

            var ordered = new System.Collections.Generic.List<ClickUpModels.TaskStatus>();
            if (folder.override_statuses && folder.statuses != null)
            {
                ordered.AddRange(folder.statuses);
            }
            else if (folder.lists != null)
            {
                foreach (var list in folder.lists)
                {
                    if (list?.statuses != null)
                        ordered.AddRange(list.statuses);
                }
            }

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new System.Collections.Generic.List<string>();
            foreach (var status in ordered)
            {
                if (status == null || string.IsNullOrEmpty(status.status)) continue;
                if (seen.Add(status.status)) names.Add(status.status);
            }
            return names.ToArray();
        }
    }
}
