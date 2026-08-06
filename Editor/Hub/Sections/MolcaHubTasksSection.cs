using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Molca.Settings.Integration;
using Molca.Settings.Integration.ClickUp;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Tasks section for the Molca Hub Settings workspace: lists the ClickUp tasks scoped to the project's
    /// configured folder, and lets the user search, group, pin, focus, create, and re-status them inline.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Tasks rail section is active.
    /// <para>
    /// The section reads the single <see cref="ClickUpIntegrationProvider"/> from
    /// <see cref="IntegrationSettings"/>. When ClickUp is not configured (no token or no
    /// <see cref="ClickUpIntegrationProvider.TargetFolderId"/>) it shows a message with a launcher to the
    /// provider inspector instead of fetching. All network work runs through the provider's
    /// <see cref="Awaitable"/> APIs, is fire-and-forget with an explicit discard, wraps its body so
    /// exceptions cannot escape into Unity's synchronization context, and re-checks that the element is
    /// still attached after every <c>await</c> before touching the UI.
    /// </para>
    /// <para>
    /// <b>Two cancellation scopes, deliberately.</b> <c>_lifetime</c> is cancelled only when the section
    /// detaches; <c>_fetchCts</c> is linked to it and cancelled additionally whenever a newer fetch supersedes
    /// an older one. Writes (a status change, a task creation) use the <em>lifetime</em> token — never the fetch
    /// token — because a refresh must not abort an in-flight write. Sharing one source previously meant hitting
    /// Refresh mid-change reverted the dropdown even when ClickUp had already accepted the new status.
    /// </para>
    /// <para>
    /// Filtering, grouping, and ordering are applied to the fetched snapshot in memory, so changing the search
    /// text or grouping costs no network traffic. Only the "Only my tasks" and "Include closed" filters are
    /// server-side, because those change which tasks the API returns at all.
    /// </para>
    /// </remarks>
    internal sealed class MolcaHubTasksSection : VisualElement
    {
        /// <summary>How the fetched tasks are bucketed for display.</summary>
        private enum TaskGrouping
        {
            None = 0,
            Status = 1,
            List = 2
        }

        // Beyond this many assignee chips a row starts to crowd out the task name, so the rest collapse to "+N".
        private const int MaxAssigneeChips = 3;

        private readonly Action<MolcaHubSection> _navigate;
        private readonly VisualElement _listHost = new();
        private readonly Label _stateLabel = new();
        private readonly Label _updatedLabel = new();
        private readonly VisualElement _focusBanner = new();
        private readonly Label _focusLabel = new();
        private readonly VisualElement _createRow = new();

        private ToolbarSearchField _searchField;
        private EnumField _groupingField;
        private Toggle _onlyMineToggle;
        private Toggle _includeClosedToggle;
        private Button _refreshButton;
        private Button _newTaskButton;
        private TextField _newTaskName;

        // Cancelled and dropped on detach, recreated on demand; the scope every *write* runs under. Created
        // lazily rather than in a field initializer because UI Toolkit may detach and reattach an element (a
        // window dock or layout change), and a disposed source would then throw on its next Token read.
        private CancellationTokenSource _lifetime;
        // Linked to _lifetime, additionally cancelled when a newer fetch supersedes this one.
        private CancellationTokenSource _fetchCts;

        private bool _busy;
        private ClickUpModels.ClickUpTask[] _tasks = Array.Empty<ClickUpModels.ClickUpTask>();
        private ClickUpModels.TaskStatus[] _statuses = Array.Empty<ClickUpModels.TaskStatus>();
        private string _folderName;

        internal MolcaHubTasksSection(Action<MolcaHubSection> navigate)
        {
            _navigate = navigate;
            AddToClassList("molca-hub-tasks-section");

            BuildHeader();
            BuildFocusBanner();
            BuildControls();
            BuildCreateRow();

            var statusRow = new VisualElement();
            statusRow.AddToClassList("molca-hub-tasks-status-row");
            _stateLabel.AddToClassList("molca-hub-muted");
            statusRow.Add(_stateLabel);
            var statusSpacer = new VisualElement();
            statusSpacer.AddToClassList("molca-hub-spacer");
            statusRow.Add(statusSpacer);
            _updatedLabel.AddToClassList("molca-hub-tasks-updated");
            statusRow.Add(_updatedLabel);
            Add(statusRow);

            _listHost.AddToClassList("molca-hub-tasks-list");
            Add(_listHost);

            // Static event: unsubscribing on detach is mandatory, or this section outlives its window. Attach
            // re-subscribes so a detached-then-reattached section still tracks focus changes.
            ClickUpTaskFocus.Changed += OnFocusChanged;
            RegisterCallback<AttachToPanelEvent>(_ => OnAttach());
            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());

            RenderStateOrAutoFetch();
        }

        private void BuildHeader()
        {
            var title = new Label("Tasks");
            title.AddToClassList("molca-hub-integrations-title");
            Add(title);

            var subtitle = new Label(
                "Your ClickUp tasks for this project's folder. Change a status with the dropdown, click a name "
              + "to open it in ClickUp, pin a row to keep it on top, or focus a task so build activity comments "
              + "on it.");
            subtitle.AddToClassList("molca-hub-integrations-subtitle");
            Add(subtitle);
        }

        // The focused task is shown even when it is not in the current filter, because it drives build/release
        // comments — a focus you cannot see is a setting you cannot discover you left on.
        private void BuildFocusBanner()
        {
            _focusBanner.AddToClassList("molca-hub-task-focus-banner");

            var glyph = new Label("★");
            glyph.AddToClassList("molca-hub-task-focus-glyph");
            _focusBanner.Add(glyph);

            _focusLabel.AddToClassList("molca-hub-task-focus-label");
            _focusBanner.Add(_focusLabel);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            _focusBanner.Add(spacer);

            var open = new Button(() =>
            {
                string url = ClickUpTaskFocus.FocusedTaskUrl;
                if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
            })
            { text = "Open", tooltip = "Open the focused task in ClickUp." };
            open.AddToClassList("molca-hub-mini-button");
            _focusBanner.Add(open);

            var clear = new Button(ClickUpTaskFocus.ClearFocus)
            {
                text = "Clear",
                tooltip = "Stop focusing this task. Build activity stops commenting on it."
            };
            clear.AddToClassList("molca-hub-mini-button");
            _focusBanner.Add(clear);

            Add(_focusBanner);
            RefreshFocusBanner();
        }

        private void BuildControls()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-tasks-controls");

            // Client-side: filters the fetched snapshot, so it costs no request.
            _searchField = new ToolbarSearchField { tooltip = "Filter by task name, list, or tag." };
            _searchField.AddToClassList("molca-hub-tasks-search");
            _searchField.RegisterValueChangedCallback(_ => RenderTasks());
            row.Add(_searchField);

            _groupingField = new EnumField(TaskGrouping.Status) { tooltip = "Bucket the rows." };
            _groupingField.AddToClassList("molca-hub-tasks-grouping");
            _groupingField.RegisterValueChangedCallback(_ => RenderTasks());
            row.Add(_groupingField);

            // Server-side: these change which tasks the API returns, so they trigger a refetch.
            _onlyMineToggle = new Toggle("Only mine") { value = true };
            _onlyMineToggle.RegisterValueChangedCallback(_ => TriggerFetch());
            row.Add(_onlyMineToggle);

            _includeClosedToggle = new Toggle("Include closed") { value = false };
            _includeClosedToggle.RegisterValueChangedCallback(_ => TriggerFetch());
            row.Add(_includeClosedToggle);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            row.Add(spacer);

            _newTaskButton = new Button(ToggleCreateRow)
            {
                text = "+ New task",
                tooltip = "Create a task in the configured target list."
            };
            _newTaskButton.AddToClassList("molca-hub-mini-button");
            row.Add(_newTaskButton);

            _refreshButton = new Button(TriggerFetch) { text = "Refresh", tooltip = "Reload tasks from ClickUp." };
            _refreshButton.AddToClassList("molca-hub-mini-button");
            row.Add(_refreshButton);

            Add(row);
        }

        // Collapsed by default so the common case (reading tasks) is not pushed down by an authoring affordance.
        private void BuildCreateRow()
        {
            _createRow.AddToClassList("molca-hub-tasks-create-row");
            _createRow.style.display = DisplayStyle.None;

            _newTaskName = new TextField { tooltip = "The new task's title." };
            _newTaskName.AddToClassList("molca-hub-tasks-create-field");
            _createRow.Add(_newTaskName);

            var create = new Button(() => _ = CreateTaskAsync()) { text = "Create" };
            create.AddToClassList("molca-hub-mini-button");
            _createRow.Add(create);

            var cancel = new Button(() =>
            {
                _newTaskName.value = string.Empty;
                _createRow.style.display = DisplayStyle.None;
            })
            { text = "Cancel" };
            cancel.AddToClassList("molca-hub-mini-button");
            _createRow.Add(cancel);

            Add(_createRow);
        }

        private void ToggleCreateRow()
        {
            var provider = FindProvider();
            if (provider == null || string.IsNullOrEmpty(provider.TargetListId))
            {
                _stateLabel.text =
                    "Set a Target List on the ClickUp integration before creating tasks from the Hub.";
                return;
            }

            bool showing = _createRow.style.display == DisplayStyle.Flex;
            _createRow.style.display = showing ? DisplayStyle.None : DisplayStyle.Flex;
            if (!showing) _newTaskName.Focus();
        }

        // Looks up the single ClickUp provider; null when none is registered.
        private static ClickUpIntegrationProvider FindProvider()
        {
            var settings = IntegrationSettings.FindSettings();
            return settings == null ? null : settings.GetProvider<ClickUpIntegrationProvider>();
        }

        private void RenderStateOrAutoFetch()
        {
            var provider = FindProvider();
            if (provider == null)
            {
                ShowNotConfigured("ClickUp isn't set up yet. Add and configure the ClickUp integration first.");
                return;
            }

            if (!provider.CanViewTasks)
            {
                ShowNotConfigured(provider.HasToken
                    ? "Choose a target folder on the ClickUp integration to list this project's tasks."
                    : "Connect ClickUp with an API token, then choose a target folder.");
                return;
            }

            TriggerFetch();
        }

        // Shows a message plus a launcher to the ClickUp provider inspector, and disables fetch controls.
        private void ShowNotConfigured(string message)
        {
            _listHost.Clear();
            SetControlsEnabled(false);
            _stateLabel.text = message;
            _updatedLabel.text = string.Empty;

            var configure = new Button(OpenClickUpProvider) { text = "Configure ClickUp" };
            configure.AddToClassList("molca-hub-mini-button");
            _listHost.Add(configure);
        }

        private void OpenClickUpProvider()
        {
            var provider = FindProvider();
            if (provider != null)
            {
                Selection.activeObject = provider;
                EditorGUIUtility.PingObject(provider);
            }
            else
            {
                // No provider asset yet — send the user to the Integrations section to add one.
                _navigate?.Invoke(MolcaHubSection.Integrations);
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _onlyMineToggle?.SetEnabled(enabled);
            _includeClosedToggle?.SetEnabled(enabled);
            _refreshButton?.SetEnabled(enabled);
            _searchField?.SetEnabled(enabled);
            _groupingField?.SetEnabled(enabled);
            _newTaskButton?.SetEnabled(enabled);
        }

        private void TriggerFetch()
        {
            var provider = FindProvider();
            if (provider == null || !provider.CanViewTasks)
            {
                RenderStateOrAutoFetch();
                return;
            }
            if (_busy) return;
            _ = FetchAsync(provider);
        }

        private async Awaitable FetchAsync(ClickUpIntegrationProvider provider)
        {
            CancelInFlightFetch();
            _fetchCts = CancellationTokenSource.CreateLinkedTokenSource(EnsureLifetime().Token);
            var token = _fetchCts.Token;

            _busy = true;
            SetControlsEnabled(false);
            _listHost.Clear();
            _stateLabel.text = "Loading tasks…";

            try
            {
                var result = await provider.FetchTasksAsync(
                    _onlyMineToggle.value, _includeClosedToggle.value, token);

                if (panel == null || token.IsCancellationRequested) return;

                if (!result.Success)
                {
                    _tasks = Array.Empty<ClickUpModels.ClickUpTask>();
                    _statuses = Array.Empty<ClickUpModels.TaskStatus>();
                    ShowError(result.Error);
                    return;
                }

                _tasks = result.Tasks;
                _statuses = result.Statuses;
                _folderName = result.FolderName;
                _updatedLabel.text =
                    $"Updated {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}";
                RenderTasks();
            }
            catch (OperationCanceledException)
            {
                // Section detached or a newer fetch superseded this one — ignore quietly.
            }
            catch (Exception e)
            {
                if (panel != null) ShowError(e.Message);
            }
            finally
            {
                _busy = false;
                if (panel != null) SetControlsEnabled(true);
            }
        }

        // An error is visually distinct from "no tasks": the same muted grey for both made a failed fetch look
        // like an empty folder, which sent people to check ClickUp instead of their token.
        private void ShowError(string reason)
        {
            _listHost.Clear();
            _stateLabel.text = string.IsNullOrWhiteSpace(reason)
                ? "Couldn't load tasks."
                : $"Couldn't load tasks: {reason}";
            _stateLabel.AddToClassList("molca-hub-tasks-error");
            _updatedLabel.text = string.Empty;

            var retry = new Button(TriggerFetch) { text = "Retry" };
            retry.AddToClassList("molca-hub-mini-button");
            _listHost.Add(retry);
        }

        private void RenderTasks()
        {
            _listHost.Clear();
            _stateLabel.RemoveFromClassList("molca-hub-tasks-error");

            var provider = FindProvider();
            if (provider == null) return;

            var visible = ApplyFilter(_tasks);

            if (_tasks.Length == 0)
            {
                _stateLabel.text = _onlyMineToggle.value
                    ? "No tasks assigned to you in this folder."
                    : "No tasks in this folder.";
                return;
            }

            if (visible.Count == 0)
            {
                _stateLabel.text = $"No tasks match \"{_searchField.value}\".";
                return;
            }

            _stateLabel.text = visible.Count == _tasks.Length
                ? Pluralize(_tasks.Length)
                : $"Showing {visible.Count} of {Pluralize(_tasks.Length)}";

            if (!string.IsNullOrEmpty(_folderName))
                _stateLabel.text += $" in {_folderName}";

            var grouping = (TaskGrouping)_groupingField.value;
            if (grouping == TaskGrouping.None)
            {
                foreach (var task in Order(visible))
                    _listHost.Add(BuildTaskRow(provider, task));
                return;
            }

            foreach (var group in Group(visible, grouping))
            {
                _listHost.Add(BuildGroupHeader(group.Key, group.Value.Count, group.Value));
                foreach (var task in Order(group.Value))
                    _listHost.Add(BuildTaskRow(provider, task));
            }
        }

        private static string Pluralize(int count) => count == 1 ? "1 task" : $"{count} tasks";

        // Free-text filter across the fields a developer would actually search by.
        private List<ClickUpModels.ClickUpTask> ApplyFilter(ClickUpModels.ClickUpTask[] source)
        {
            // Null-filtered on both paths, not just the searched one: a JSON array can legitimately contain a
            // null element, and every renderer downstream dereferences the task.
            string query = _searchField?.value;
            if (string.IsNullOrWhiteSpace(query))
                return source.Where(t => t != null).ToList();

            query = query.Trim();
            return source.Where(t => t != null && Matches(t, query)).ToList();
        }

        private static bool Matches(ClickUpModels.ClickUpTask task, string query)
        {
            if (task == null) return false;
            if (Contains(task.name, query)) return true;
            if (Contains(task.list?.name, query)) return true;
            if (Contains(task.status?.status, query)) return true;
            if (task.tags != null && task.tags.Any(tag => Contains(tag?.name, query))) return true;
            if (task.assignees != null
                && task.assignees.Any(a => Contains(a?.username, query) || Contains(a?.email, query)))
                return true;
            return false;
        }

        private static bool Contains(string haystack, string needle)
            => !string.IsNullOrEmpty(haystack)
               && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // Focused first, then pinned, then workflow order, then soonest due, then name — so the rows a developer
        // marked as important cannot be pushed below the fold by an unrelated status change.
        private List<ClickUpModels.ClickUpTask> Order(List<ClickUpModels.ClickUpTask> source)
        {
            return source
                .OrderByDescending(t => ClickUpTaskFocus.IsFocused(t.id))
                .ThenByDescending(t => ClickUpTaskFocus.IsPinned(t.id))
                .ThenBy(StatusOrder)
                .ThenBy(DueOrder)
                .ThenBy(t => t.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The index into the folder's ordered status set is the workflow position. An unrecognized status sorts
        // last rather than first, so a stray status never displaces the open work.
        private int StatusOrder(ClickUpModels.ClickUpTask task)
        {
            int index = IndexOfStatus(task?.status?.status);
            return index < 0 ? int.MaxValue : index;
        }

        private int IndexOfStatus(string name)
            => string.IsNullOrEmpty(name)
                ? -1
                : Array.FindIndex(
                    _statuses,
                    s => s != null && string.Equals(s.status, name, StringComparison.OrdinalIgnoreCase));

        // The folder's definition of a status, which is where its color comes from.
        private ClickUpModels.TaskStatus FindStatusDefinition(string name)
        {
            int index = IndexOfStatus(name);
            return index < 0 ? null : _statuses[index];
        }

        // Tasks with no due date sort after every dated task rather than before them.
        private static long DueOrder(ClickUpModels.ClickUpTask task)
            => ClickUpTaskFormat.TryParseEpochMillis(task?.due_date, out var due)
                ? due.ToUnixTimeMilliseconds()
                : long.MaxValue;

        // Ordered buckets: statuses follow the folder's workflow order, lists are alphabetical.
        private List<KeyValuePair<string, List<ClickUpModels.ClickUpTask>>> Group(
            List<ClickUpModels.ClickUpTask> source, TaskGrouping grouping)
        {
            var buckets = new Dictionary<string, List<ClickUpModels.ClickUpTask>>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in source)
            {
                string key = grouping == TaskGrouping.Status
                    ? (string.IsNullOrEmpty(task.status?.status) ? "No status" : task.status.status)
                    : (string.IsNullOrEmpty(task.list?.name) ? "No list" : task.list.name);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<ClickUpModels.ClickUpTask>();
                    buckets[key] = bucket;
                }
                bucket.Add(task);
            }

            var ordered = buckets.ToList();
            if (grouping == TaskGrouping.Status)
            {
                ordered.Sort((a, b) =>
                {
                    int ai = IndexOfStatus(a.Key);
                    int bi = IndexOfStatus(b.Key);
                    if (ai < 0) ai = int.MaxValue;
                    if (bi < 0) bi = int.MaxValue;
                    return ai != bi
                        ? ai.CompareTo(bi)
                        : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                });
            }
            else
            {
                ordered.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            }

            return ordered;
        }

        private VisualElement BuildGroupHeader(
            string key, int count, List<ClickUpModels.ClickUpTask> members)
        {
            var header = new VisualElement();
            header.AddToClassList("molca-hub-task-group-header");

            // Prefer the folder's own definition of the status over any member's copy: after a status change the
            // moved task is the authority on nothing, and a group must read as the status it represents.
            string swatchHex = FindStatusDefinition(key)?.color
                ?? members.FirstOrDefault(t => !string.IsNullOrEmpty(t.status?.color))?.status?.color;
            if (ClickUpTaskFormat.TryParseHexColor(swatchHex, out var color))
            {
                var swatch = new VisualElement();
                swatch.AddToClassList("molca-hub-task-group-swatch");
                swatch.style.backgroundColor = color;
                header.Add(swatch);
            }

            var label = new Label($"{key} ({count})");
            label.AddToClassList("molca-hub-task-group-label");
            header.Add(label);

            return header;
        }

        private VisualElement BuildTaskRow(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-task-row");
            if (ClickUpTaskFocus.IsFocused(task.id))
                row.AddToClassList("molca-hub-task-row--focused");

            row.Add(BuildPinButton(task));
            row.Add(BuildFocusButton(task));

            var name = new Label(string.IsNullOrEmpty(task.name) ? "(untitled)" : task.name)
            {
                tooltip = "Open this task in ClickUp."
            };
            name.AddToClassList("molca-hub-task-name");
            if (!string.IsNullOrEmpty(task.url))
            {
                name.RegisterCallback<ClickEvent>(_ => Application.OpenURL(task.url));
                name.AddToClassList("molca-hub-task-name--link");
            }
            row.Add(name);

            AddBadges(row, task);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            row.Add(spacer);

            AddAssignees(row, task);
            row.Add(BuildStatusControl(provider, task));
            return row;
        }

        // Pinning is presentational and local, so it applies instantly with no request.
        private VisualElement BuildPinButton(ClickUpModels.ClickUpTask task)
        {
            bool pinned = ClickUpTaskFocus.IsPinned(task.id);
            var button = new Button(() =>
            {
                ClickUpTaskFocus.TogglePin(task.id);
                RenderTasks();
            })
            {
                text = pinned ? "📌" : "○",
                tooltip = pinned ? "Unpin — stop keeping this row on top." : "Pin this task to the top."
            };
            button.AddToClassList("molca-hub-task-icon-button");
            if (pinned) button.AddToClassList("molca-hub-task-icon-button--active");
            return button;
        }

        // Focus is singular and semantic: setting it here is what redirects build/release comments.
        private VisualElement BuildFocusButton(ClickUpModels.ClickUpTask task)
        {
            bool focused = ClickUpTaskFocus.IsFocused(task.id);
            var button = new Button(() =>
            {
                if (focused) ClickUpTaskFocus.ClearFocus();
                else ClickUpTaskFocus.SetFocus(task.id, task.name, task.url);
            })
            {
                text = focused ? "★" : "☆",
                tooltip = focused
                    ? "Unfocus this task."
                    : "Focus this task — build and release activity will comment on it."
            };
            button.AddToClassList("molca-hub-task-icon-button");
            if (focused) button.AddToClassList("molca-hub-task-icon-button--active");
            return button;
        }

        // Priority, due date, list, and tags — all already present in the fetched payload, so showing them
        // costs nothing extra.
        private static void AddBadges(VisualElement row, ClickUpModels.ClickUpTask task)
        {
            if (!string.IsNullOrEmpty(task.priority?.priority))
            {
                var badge = new Label(task.priority.priority);
                badge.AddToClassList("molca-hub-task-badge");
                badge.AddToClassList("molca-hub-task-priority-badge");
                if (ClickUpTaskFormat.TryParseHexColor(task.priority.color, out var color))
                {
                    badge.style.backgroundColor = color;
                    badge.style.color = ClickUpTaskFormat.ReadableForeground(color);
                }
                row.Add(badge);
            }

            string due = ClickUpTaskFormat.FormatRelativeDue(task.due_date, out bool overdue);
            if (!string.IsNullOrEmpty(due))
            {
                var badge = new Label(due) { tooltip = "Due date." };
                badge.AddToClassList("molca-hub-task-badge");
                badge.AddToClassList(overdue
                    ? "molca-hub-task-due-badge--overdue"
                    : "molca-hub-task-due-badge");
                row.Add(badge);
            }

            if (!string.IsNullOrEmpty(task.list?.name))
            {
                var badge = new Label(task.list.name);
                badge.AddToClassList("molca-hub-task-badge");
                badge.AddToClassList("molca-hub-task-list-badge");
                row.Add(badge);
            }

            if (task.tags == null) return;
            foreach (var tag in task.tags)
            {
                if (string.IsNullOrEmpty(tag?.name)) continue;
                var badge = new Label(tag.name);
                badge.AddToClassList("molca-hub-task-badge");
                badge.AddToClassList("molca-hub-task-tag-badge");
                if (ClickUpTaskFormat.TryParseHexColor(tag.tag_bg, out var bg))
                {
                    badge.style.backgroundColor = bg;
                    badge.style.color = ClickUpTaskFormat.TryParseHexColor(tag.tag_fg, out var fg)
                        ? fg
                        : ClickUpTaskFormat.ReadableForeground(bg);
                }
                row.Add(badge);
            }
        }

        private static void AddAssignees(VisualElement row, ClickUpModels.ClickUpTask task)
        {
            if (task.assignees == null || task.assignees.Length == 0) return;

            var host = new VisualElement();
            host.AddToClassList("molca-hub-task-assignees");

            int shown = Mathf.Min(task.assignees.Length, MaxAssigneeChips);
            for (int i = 0; i < shown; i++)
            {
                var user = task.assignees[i];
                var chip = new Label(ClickUpTaskFormat.Initials(user))
                {
                    tooltip = user?.username ?? user?.email ?? "Unknown assignee"
                };
                chip.AddToClassList("molca-hub-task-assignee");
                host.Add(chip);
            }

            if (task.assignees.Length > shown)
            {
                var more = new Label($"+{task.assignees.Length - shown}")
                {
                    tooltip = string.Join(
                        ", ",
                        task.assignees.Skip(shown).Select(a => a?.username ?? a?.email ?? "?"))
                };
                more.AddToClassList("molca-hub-task-assignee");
                more.AddToClassList("molca-hub-task-assignee--more");
                host.Add(more);
            }

            row.Add(host);
        }

        // A dropdown of the folder's statuses, tinted with the task's current status color. The current status
        // is included even if the folder set didn't enumerate it, so it always displays correctly.
        private VisualElement BuildStatusControl(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task)
        {
            string current = task.status?.status ?? string.Empty;

            var choices = _statuses.Where(s => !string.IsNullOrEmpty(s?.status))
                                   .Select(s => s.status)
                                   .ToList();
            if (!string.IsNullOrEmpty(current)
                && !choices.Any(s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, current);

            if (choices.Count == 0)
            {
                var label = new Label(string.IsNullOrEmpty(current) ? "—" : current);
                label.AddToClassList("molca-hub-task-status-static");
                return label;
            }

            var dropdown = new PopupField<string>(choices, current ?? choices[0]);
            dropdown.AddToClassList("molca-hub-task-status");
            TintStatusControl(dropdown, task.status?.color);
            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (string.Equals(evt.newValue, evt.previousValue, StringComparison.Ordinal)) return;
                _ = ChangeStatusAsync(provider, task, dropdown, evt.previousValue, evt.newValue);
            });
            return dropdown;
        }

        // Tints the field and, when present, its inner input element. The inner query is defensive: it targets a
        // UI Toolkit-internal class name, so a null result must degrade to the outer tint rather than throw.
        private static void TintStatusControl(PopupField<string> dropdown, string hexColor)
        {
            if (!ClickUpTaskFormat.TryParseHexColor(hexColor, out var color)) return;

            var foreground = ClickUpTaskFormat.ReadableForeground(color);
            dropdown.style.backgroundColor = color;
            dropdown.style.color = foreground;

            var input = dropdown.Q(className: "unity-popup-field__input");
            if (input == null) return;
            input.style.backgroundColor = color;
            input.style.color = foreground;
        }

        private async Awaitable ChangeStatusAsync(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task,
            PopupField<string> dropdown, string previous, string next)
        {
            dropdown.SetEnabled(false);
            try
            {
                // Lifetime token, not the fetch token: a refresh must never abort a write in progress.
                var result = await provider.SetTaskStatusAsync(task.id, next, EnsureLifetime().Token);

                if (panel == null) return;

                if (result.Success)
                {
                    // Adopt the destination status *definition*, not just its name — the color, type and
                    // orderindex all belong to the new status. Writing only the name left the row tinted with its
                    // previous status's color, which read as "nothing happened".
                    task.status ??= new ClickUpModels.TaskStatus();
                    task.status.status = next;

                    var definition = FindStatusDefinition(next);
                    if (definition != null)
                    {
                        task.status.color = definition.color;
                        task.status.type = definition.type;
                        task.status.orderindex = definition.orderindex;
                    }

                    // Re-render so the row actually moves: its group, its position within the group, and every
                    // group's count are all derived from the status we just changed. Without this the write
                    // succeeded but the list kept showing the task under its old status until a manual refresh.
                    // Safe here because the PopupField's value-changed dispatch completed before this await
                    // resumed; the old row (and this dropdown) are simply discarded.
                    RenderTasks();

                    // After RenderTasks, which owns _stateLabel — otherwise the count line overwrites this.
                    _stateLabel.RemoveFromClassList("molca-hub-tasks-error");
                    _stateLabel.text = $"Moved '{task.name}' to {next}.";
                }
                else
                {
                    dropdown.SetValueWithoutNotify(previous); // revert on failure
                    _stateLabel.AddToClassList("molca-hub-tasks-error");
                    _stateLabel.text = $"Couldn't move '{task.name}' to {next}: {result.Error}";
                }
            }
            catch (OperationCanceledException)
            {
                if (panel != null) dropdown.SetValueWithoutNotify(previous);
            }
            catch (Exception e)
            {
                if (panel == null) return;
                dropdown.SetValueWithoutNotify(previous);
                _stateLabel.AddToClassList("molca-hub-tasks-error");
                _stateLabel.text = $"Error changing status: {e.Message}";
            }
            finally
            {
                if (panel != null) dropdown.SetEnabled(true);
            }
        }

        private async Awaitable CreateTaskAsync()
        {
            var provider = FindProvider();
            string title = _newTaskName?.value?.Trim();

            if (provider == null || string.IsNullOrEmpty(title))
            {
                _stateLabel.text = "Enter a task title first.";
                return;
            }

            SetControlsEnabled(false);
            _createRow.SetEnabled(false);
            try
            {
                // Lifetime token: creating a task is a write and must survive a concurrent refresh.
                var result = await provider.CreateTaskAsync(
                    title, markdownDescription: null, listId: null,
                    cancellationToken: EnsureLifetime().Token);

                if (panel == null) return;

                if (result.Success)
                {
                    _newTaskName.value = string.Empty;
                    _createRow.style.display = DisplayStyle.None;
                    _stateLabel.RemoveFromClassList("molca-hub-tasks-error");
                    _stateLabel.text = $"Created '{title}'.";
                    TriggerFetch();
                }
                else
                {
                    _stateLabel.AddToClassList("molca-hub-tasks-error");
                    _stateLabel.text = $"Couldn't create the task: {result.Error}";
                }
            }
            catch (OperationCanceledException)
            {
                // Section detached — ignore quietly.
            }
            catch (Exception e)
            {
                if (panel != null)
                {
                    _stateLabel.AddToClassList("molca-hub-tasks-error");
                    _stateLabel.text = $"Error creating the task: {e.Message}";
                }
            }
            finally
            {
                if (panel != null)
                {
                    _createRow.SetEnabled(true);
                    SetControlsEnabled(true);
                }
            }
        }

        private void OnFocusChanged()
        {
            if (panel == null) return;
            RefreshFocusBanner();
            RenderTasks();
        }

        private void RefreshFocusBanner()
        {
            if (!ClickUpTaskFocus.HasFocus)
            {
                _focusBanner.style.display = DisplayStyle.None;
                return;
            }

            _focusBanner.style.display = DisplayStyle.Flex;
            string name = ClickUpTaskFocus.FocusedTaskName;
            _focusLabel.text = string.IsNullOrEmpty(name)
                ? $"Focused task {ClickUpTaskFocus.FocusedTaskId}"
                : $"Focused: {name}";
        }

        // Idempotent: UI Toolkit can raise AttachToPanel more than once over an element's life, and -= on an
        // unsubscribed handler is a no-op, so this cannot double-subscribe.
        private void OnAttach()
        {
            ClickUpTaskFocus.Changed -= OnFocusChanged;
            ClickUpTaskFocus.Changed += OnFocusChanged;
        }

        private void OnDetach()
        {
            ClickUpTaskFocus.Changed -= OnFocusChanged;
            CancelInFlightFetch();

            // Cancelling the lifetime source unwinds any in-flight write as well. Dropped to null rather than
            // left disposed, so a later reattach builds a fresh scope instead of reading a disposed source.
            if (_lifetime == null) return;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetime = null;
        }

        // The write/lifetime scope, created on first use and after a reattach.
        private CancellationTokenSource EnsureLifetime() => _lifetime ??= new CancellationTokenSource();

        private void CancelInFlightFetch()
        {
            if (_fetchCts == null) return;
            _fetchCts.Cancel();
            _fetchCts.Dispose();
            _fetchCts = null;
        }
    }
}
