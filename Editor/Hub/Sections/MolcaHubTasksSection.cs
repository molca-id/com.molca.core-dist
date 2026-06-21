using System;
using System.Linq;
using System.Threading;
using Molca.Settings.Integration;
using Molca.Settings.Integration.ClickUp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Tasks section for the Molca Hub Settings workspace: lists the ClickUp tasks scoped to the
    /// project's configured folder and lets the user change a task's status inline.
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
    /// </remarks>
    internal sealed class MolcaHubTasksSection : VisualElement
    {
        private readonly Action<MolcaHubSection> _navigate;
        private readonly VisualElement _listHost = new();
        private readonly Label _stateLabel = new();
        private Toggle _onlyMineToggle;
        private Toggle _includeClosedToggle;
        private Button _refreshButton;

        // A token cancelled when the section detaches, so an in-flight fetch/update unwinds quietly.
        private CancellationTokenSource _cts;
        private bool _busy;

        internal MolcaHubTasksSection(Action<MolcaHubSection> navigate)
        {
            _navigate = navigate;
            AddToClassList("molca-hub-tasks-section");

            BuildHeader();
            BuildControls();

            _stateLabel.AddToClassList("molca-hub-muted");
            Add(_stateLabel);

            _listHost.AddToClassList("molca-hub-tasks-list");
            Add(_listHost);

            RegisterCallback<DetachFromPanelEvent>(_ => CancelInFlight());

            RenderStateOrAutoFetch();
        }

        private void BuildHeader()
        {
            var title = new Label("Tasks");
            title.AddToClassList("molca-hub-integrations-title");
            Add(title);

            var subtitle = new Label(
                "Your ClickUp tasks for this project's folder. Change a task's status with the dropdown; click its name to open it in ClickUp.");
            subtitle.AddToClassList("molca-hub-integrations-subtitle");
            Add(subtitle);
        }

        private void BuildControls()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-tasks-controls");

            _onlyMineToggle = new Toggle("Only my tasks") { value = true };
            _onlyMineToggle.RegisterValueChangedCallback(_ => TriggerFetch());
            row.Add(_onlyMineToggle);

            _includeClosedToggle = new Toggle("Include closed") { value = false };
            _includeClosedToggle.RegisterValueChangedCallback(_ => TriggerFetch());
            row.Add(_includeClosedToggle);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            row.Add(spacer);

            _refreshButton = new Button(TriggerFetch) { text = "Refresh", tooltip = "Reload tasks from ClickUp." };
            _refreshButton.AddToClassList("molca-hub-mini-button");
            row.Add(_refreshButton);

            Add(row);
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
                    ? "Set a Target Folder Id on the ClickUp integration to list this project's tasks."
                    : "Connect ClickUp with an API token, then set a Target Folder Id.");
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
            CancelInFlight();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

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
                    _stateLabel.text = $"Couldn't load tasks: {result.Error}";
                    return;
                }

                RenderTasks(provider, result);
            }
            catch (OperationCanceledException)
            {
                // Section detached or a newer fetch superseded this one — ignore quietly.
            }
            catch (Exception e)
            {
                if (panel != null) _stateLabel.text = $"Error loading tasks: {e.Message}";
            }
            finally
            {
                _busy = false;
                if (panel != null) SetControlsEnabled(true);
            }
        }

        private void RenderTasks(ClickUpIntegrationProvider provider, ClickUpIntegrationProvider.TaskFetchResult result)
        {
            _listHost.Clear();

            if (result.Tasks.Length == 0)
            {
                _stateLabel.text = _onlyMineToggle.value
                    ? "No tasks assigned to you in this folder."
                    : "No tasks in this folder.";
                return;
            }

            _stateLabel.text = $"{result.Tasks.Length} task(s).";

            foreach (var task in result.Tasks)
                _listHost.Add(BuildTaskRow(provider, task, result.Statuses));
        }

        private VisualElement BuildTaskRow(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task, string[] statuses)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-task-row");

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

            if (task.list != null && !string.IsNullOrEmpty(task.list.name))
            {
                var listBadge = new Label(task.list.name);
                listBadge.AddToClassList("molca-hub-task-list-badge");
                row.Add(listBadge);
            }

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            row.Add(spacer);

            row.Add(BuildStatusControl(provider, task, statuses));
            return row;
        }

        // A dropdown of the folder's statuses, defaulting to the task's current status. The current status
        // is included even if the folder set didn't enumerate it, so it always displays correctly.
        private VisualElement BuildStatusControl(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task, string[] statuses)
        {
            string current = task.status?.status ?? string.Empty;

            var choices = statuses.ToList();
            if (!string.IsNullOrEmpty(current) && !choices.Any(s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, current);

            if (choices.Count == 0)
            {
                var label = new Label(string.IsNullOrEmpty(current) ? "—" : current);
                label.AddToClassList("molca-hub-task-status-static");
                return label;
            }

            var dropdown = new PopupField<string>(choices, current ?? choices[0]);
            dropdown.AddToClassList("molca-hub-task-status");
            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (string.Equals(evt.newValue, evt.previousValue, StringComparison.Ordinal)) return;
                _ = ChangeStatusAsync(provider, task, dropdown, evt.previousValue, evt.newValue);
            });
            return dropdown;
        }

        private async Awaitable ChangeStatusAsync(
            ClickUpIntegrationProvider provider, ClickUpModels.ClickUpTask task,
            PopupField<string> dropdown, string previous, string next)
        {
            dropdown.SetEnabled(false);
            try
            {
                bool ok = await provider.SetTaskStatusAsync(task.id, next, _cts?.Token ?? CancellationToken.None);

                if (panel == null) return;

                if (ok)
                {
                    // Keep the local model in sync so a later re-render reflects the change.
                    task.status ??= new ClickUpModels.TaskStatus();
                    task.status.status = next;
                }
                else
                {
                    dropdown.SetValueWithoutNotify(previous); // revert on failure
                    _stateLabel.text = $"Failed to change status of '{task.name}'.";
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
                _stateLabel.text = $"Error changing status: {e.Message}";
            }
            finally
            {
                if (panel != null) dropdown.SetEnabled(true);
            }
        }

        private void CancelInFlight()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }
}
