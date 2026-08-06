using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Inspector for <see cref="ClickUpIntegrationProvider"/> — the config surface the Hub card launches to.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Registration: <see cref="CustomEditor"/> for <see cref="ClickUpIntegrationProvider"/>.
    /// Renders a masked token field (token persists in <see cref="IntegrationCredentialStore"/>, never on the
    /// asset), Connect/Test/Disconnect actions, the cascading Workspace → Folder → List target pickers, and
    /// the automation settings. The dropdowns author the target ids by name (fetched via the provider's
    /// <see cref="Awaitable"/> APIs off the render path); there is no manual id entry. The token text box is
    /// local UI state only and is cleared after it is saved.
    /// <para>
    /// After a successful connect the panel names the account by <b>email</b> and lists the workspaces the token
    /// actually reaches. Both answer the question a stored token otherwise leaves open — <em>whose token is
    /// this, and what can it see?</em> — which is the usual cause of an empty task list.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(ClickUpIntegrationProvider))]
    public class ClickUpIntegrationProviderEditor : UnityEditor.Editor
    {
        private string _tokenInput = string.Empty;
        private bool _busy;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.None;

        // Cached workspaces for the top-level picker, and the folders of the selected workspace (flattened
        // across spaces, each carrying its lists). Both are fetched off the render path — on inspector open
        // and when a higher-level selection changes — never per-frame.
        private ClickUpIntegrationProvider.WorkspaceInfo[] _workspaces;
        private ClickUpIntegrationProvider.FolderInfo[] _folders;
        private bool _loadingWorkspaces;
        private bool _loadingFolders;

        // On open, load the workspaces (and the saved workspace's folders) so the Target pickers are populated.
        private void OnEnable()
        {
            var provider = (ClickUpIntegrationProvider)target;
            if (provider != null && provider.HasToken)
                _ = LoadWorkspacesAsync(provider);

            ClickUpTaskFocus.Changed += Repaint;
        }

        private void OnDisable()
        {
            // Static event — unsubscribing is mandatory or the destroyed editor is kept alive and repainted.
            ClickUpTaskFocus.Changed -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            var provider = (ClickUpIntegrationProvider)target;

            EditorGUILayout.LabelField("ClickUp Integration", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            DrawConnectionStatus(provider);
            EditorGUILayout.Space(6);

            DrawTokenSection(provider);
            EditorGUILayout.Space(6);

            // Non-secret config lives on the asset; edit through SerializedObject so undo/dirty work normally.
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"), new GUIContent("Enabled"));
            DrawTargetSection(provider);
            EditorGUILayout.Space(4);
            DrawAutomationSection(provider);
            serializedObject.ApplyModifiedProperties();

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
        }

        // Cascading Workspace → Folder → List pickers (the trickle-down hierarchy, workspace first). The ids
        // are still what's stored on the asset; the dropdowns just author them by name. Called between
        // serializedObject Update()/ApplyModifiedProperties(), so writes go through the normal undo/dirty path.
        private void DrawTargetSection(ClickUpIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);

            if (!provider.HasToken)
            {
                EditorGUILayout.HelpBox(
                    "Connect ClickUp with an API token to choose a workspace, folder, and list.",
                    MessageType.Info);
                return;
            }

            var wsProp = serializedObject.FindProperty("targetWorkspaceId");
            var folderProp = serializedObject.FindProperty("targetFolderId");
            var listProp = serializedObject.FindProperty("targetListId");

            // Workspace — the root of the cascade — plus a refresh button.
            using (new EditorGUILayout.HorizontalScope())
            {
                var wsOptions = ToOptions(_workspaces, w => w.Id, w => w.Name);
                bool changed = DrawIdDropdown(
                    new GUIContent("Workspace", "The ClickUp workspace ('team') this project maps to."),
                    wsProp, wsOptions, !_loadingWorkspaces, "workspace");

                using (new EditorGUI.DisabledScope(_busy || _loadingWorkspaces))
                {
                    if (GUILayout.Button(_loadingWorkspaces ? "…" : "↻", GUILayout.Width(28)))
                    {
                        // Drop the session cache too, or the refresh just re-serves the values it already had.
                        provider.InvalidateSessionCache();
                        _ = LoadWorkspacesAsync(provider);
                    }
                }

                if (changed)
                {
                    // A new workspace invalidates the folder and list beneath it.
                    folderProp.stringValue = string.Empty;
                    listProp.stringValue = string.Empty;
                    _folders = null;
                    serializedObject.ApplyModifiedProperties(); // persist before the fetch reads the field
                    serializedObject.Update();
                    _ = LoadFoldersAsync(provider, wsProp.stringValue);
                }
            }

            // Folder — flattened across the workspace's spaces, shown as "Space / Folder".
            bool wsSelected = !string.IsNullOrEmpty(wsProp.stringValue);
            var folderOptions = ToOptions(_folders, f => f.Id, f => f.Name);
            bool folderChanged = DrawIdDropdown(
                new GUIContent(_loadingFolders ? "Folder (loading…)" : "Folder",
                    "The folder whose tasks show in Hub → Tasks. Listed across all spaces in the workspace."),
                folderProp, folderOptions, wsSelected && !_loadingFolders, "folder");
            if (folderChanged)
                listProp.stringValue = string.Empty; // changing the folder invalidates the chosen list

            // List — the build/release post target — the lists inside the selected folder.
            var listOptions = new System.Collections.Generic.List<(string, string)>();
            if (_folders != null)
            {
                foreach (var f in _folders)
                {
                    if (f.Id != folderProp.stringValue) continue;
                    foreach (var l in f.Lists) listOptions.Add((l.Id, l.Name));
                    break;
                }
            }
            bool folderSelected = !string.IsNullOrEmpty(folderProp.stringValue);
            DrawIdDropdown(
                new GUIContent("List", "ClickUp list that new activity tasks are posted to."),
                listProp, listOptions, folderSelected, "list");
        }

        // Push toggles plus the push target, and — because the comment modes depend on state that lives outside
        // this asset — an explicit readout of what the current configuration would actually do.
        private void DrawAutomationSection(ClickUpIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Automation", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("pushOnBuild"), new GUIContent("Push on Build"));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("pushOnRelease"), new GUIContent("Push on Release"));

            var targetProp = serializedObject.FindProperty("pushTarget");
            EditorGUILayout.PropertyField(targetProp, new GUIContent("Push Target"));

            var pushTarget = (ClickUpPushTarget)targetProp.enumValueIndex;
            bool wantsFocus = pushTarget != ClickUpPushTarget.NewTaskInList;
            if (!wantsFocus) return;

            // Focus lives in ClickUpTaskFocus (per-machine), so it cannot be shown as a serialized field here.
            // Stating it plainly is the only way this mode is not silently inert.
            if (ClickUpTaskFocus.HasFocus)
            {
                string name = ClickUpTaskFocus.FocusedTaskName;
                EditorGUILayout.HelpBox(
                    $"Activity will comment on the focused task: {(string.IsNullOrEmpty(name) ? ClickUpTaskFocus.FocusedTaskId : name)}",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    pushTarget == ClickUpPushTarget.CommentOnFocusedTask
                        ? "No task is focused, so nothing will be pushed. Focus a task in Hub → Tasks, or switch "
                        + "the push target to fall back to a new task."
                        : "No task is focused, so activity falls back to a new task in the target list. Focus a "
                        + "task in Hub → Tasks to comment on it instead.",
                    pushTarget == ClickUpPushTarget.CommentOnFocusedTask
                        ? MessageType.Warning
                        : MessageType.Info);
            }
        }

        // Builds a (id, name) option list from a source array; null-safe.
        private static System.Collections.Generic.List<(string id, string name)> ToOptions<T>(
            T[] source, Func<T, string> id, Func<T, string> name)
        {
            var list = new System.Collections.Generic.List<(string id, string name)>();
            if (source != null)
                foreach (var item in source) list.Add((id(item), name(item)));
            return list;
        }

        // A name dropdown that authors an id SerializedProperty. Prepends a "select" placeholder, and surfaces
        // a saved id that isn't in the loaded options as "<id> (current)" so a stale or not-yet-loaded value is
        // visible rather than silently reset. Returns true when the selection changed.
        private bool DrawIdDropdown(
            GUIContent label, SerializedProperty prop,
            System.Collections.Generic.List<(string id, string name)> options, bool enabled, string noun)
        {
            var display = new System.Collections.Generic.List<string>
            {
                options.Count == 0 ? $"(no {noun}s)" : $"— Select {noun} —"
            };
            var ids = new System.Collections.Generic.List<string> { string.Empty };
            foreach (var o in options)
            {
                display.Add(string.IsNullOrEmpty(o.name) ? o.id : o.name);
                ids.Add(o.id);
            }

            int current = ids.IndexOf(prop.stringValue);
            if (current < 0)
            {
                // Saved id isn't among the loaded options (stale, or options not loaded yet): keep it visible.
                display.Add($"{prop.stringValue} (current)");
                ids.Add(prop.stringValue);
                current = ids.Count - 1;
            }

            int picked;
            using (new EditorGUI.DisabledScope(!enabled))
                picked = EditorGUILayout.Popup(label, current, display.ToArray());

            if (picked != current && picked >= 0 && picked < ids.Count)
            {
                prop.stringValue = ids[picked];
                return true;
            }
            return false;
        }

        // Awaitable-returning worker invoked with an explicit discard; body wrapped so exceptions cannot
        // escape into Unity's synchronization context. After loading, chains the saved workspace's folders.
        private async Awaitable LoadWorkspacesAsync(ClickUpIntegrationProvider provider)
        {
            _loadingWorkspaces = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                var result = await provider.FetchWorkspacesAsync(CancellationToken.None);
                if (this == null || target == null) return;

                if (!result.Success)
                {
                    // A failed listing and an empty account are different problems with different fixes.
                    _workspaces = Array.Empty<ClickUpIntegrationProvider.WorkspaceInfo>();
                    SetMessage($"Couldn't list workspaces: {result.Error}", MessageType.Error);
                    return;
                }

                _workspaces = result.Workspaces;
                if (_workspaces.Length == 0)
                    SetMessage("This token can't reach any workspace.", MessageType.Warning);

                // Populate the folder picker for the already-configured workspace.
                if (!string.IsNullOrEmpty(provider.TargetWorkspaceId))
                    _ = LoadFoldersAsync(provider, provider.TargetWorkspaceId);
            }
            catch (OperationCanceledException)
            {
                // Quietly ignore cancellation.
            }
            catch (Exception e)
            {
                SetMessage($"Failed to list workspaces: {e.Message}", MessageType.Error);
            }
            finally
            {
                _loadingWorkspaces = false;
                Repaint();
            }
        }

        // Loads the folders (with their lists) of a workspace for the Folder/List pickers.
        private async Awaitable LoadFoldersAsync(ClickUpIntegrationProvider provider, string workspaceId)
        {
            _loadingFolders = true;
            Repaint();
            try
            {
                var result = await provider.FetchFoldersAsync(workspaceId, CancellationToken.None);
                if (this == null || target == null) return;

                if (!result.Success)
                {
                    _folders = Array.Empty<ClickUpIntegrationProvider.FolderInfo>();
                    SetMessage($"Couldn't list folders: {result.Error}", MessageType.Error);
                    return;
                }

                _folders = result.Folders;
                if (_folders.Length == 0)
                    SetMessage("No folders in this workspace.", MessageType.Info);
            }
            catch (OperationCanceledException)
            {
                // Quietly ignore cancellation.
            }
            catch (Exception e)
            {
                SetMessage($"Failed to list folders: {e.Message}", MessageType.Error);
            }
            finally
            {
                _loadingFolders = false;
                Repaint();
            }
        }

        private void DrawConnectionStatus(ClickUpIntegrationProvider provider)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Status", GUILayout.Width(60));
                EditorGUILayout.LabelField(provider.StatusMessage, EditorStyles.miniBoldLabel);
            }

            if (!provider.IsConnected) return;

            if (!string.IsNullOrEmpty(provider.ConnectedEmail))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Account", GUILayout.Width(60));
                    EditorGUILayout.SelectableLabel(
                        provider.ConnectedEmail, EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }
            }

            if (_workspaces == null || _workspaces.Length == 0) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Reaches", GUILayout.Width(60));
                var names = new string[_workspaces.Length];
                for (int i = 0; i < _workspaces.Length; i++)
                    names[i] = string.IsNullOrEmpty(_workspaces[i].Name) ? _workspaces[i].Id : _workspaces[i].Name;
                EditorGUILayout.LabelField(string.Join(", ", names), EditorStyles.miniLabel);
            }
        }

        private void DrawTokenSection(ClickUpIntegrationProvider provider)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Personal API Token", EditorStyles.miniBoldLabel);
                if (GUILayout.Button("Get a token", EditorStyles.miniButton, GUILayout.Width(90)))
                    Application.OpenURL(ClickUpIntegrationProvider.TokenSettingsUrl);
            }

            // Condensed deliberately: the full OAuth-vs-token rationale is reference material, not configuration.
            // It lives in Documentation~/reference/CLICKUP.md, readable in the Hub's Docs tab.
            EditorGUILayout.LabelField(
                "ClickUp uses a personal token: its OAuth flow requires a server-side client secret, which an "
              + "editor tool cannot hold. See Hub → Docs → ClickUp Integration.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (provider.HasToken)
                {
                    EditorGUILayout.HelpBox(
                        "A token is stored for this machine (EditorUserSettings — not committed). " +
                        "Enter a new token below to replace it, or Disconnect to clear it.",
                        MessageType.None);
                }

                _tokenInput = EditorGUILayout.PasswordField("Token", _tokenInput);

                // Advisory, checked before the round-trip: catches a pasted workspace id or a truncated copy.
                if (!string.IsNullOrEmpty(_tokenInput)
                    && !ClickUpIntegrationProvider.LooksLikeToken(_tokenInput))
                {
                    EditorGUILayout.HelpBox(
                        $"That doesn't look like a ClickUp personal token — they start with "
                      + $"'{ClickUpIntegrationProvider.TokenPrefix}'. You can still save it.",
                        MessageType.Warning);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_tokenInput)))
                    {
                        if (GUILayout.Button("Save & Connect"))
                        {
                            provider.SetToken(_tokenInput.Trim());
                            _tokenInput = string.Empty;
                            GUIUtility.keyboardControl = 0;
                            _ = ConnectAsync(provider);
                        }
                    }

                    using (new EditorGUI.DisabledScope(!provider.HasToken))
                    {
                        if (GUILayout.Button("Test Connection"))
                            _ = ConnectAsync(provider);

                        if (GUILayout.Button("Disconnect"))
                        {
                            provider.Disconnect();
                            _workspaces = null;
                            _folders = null;
                            SetMessage("Disconnected and cleared the stored token.", MessageType.Info);
                        }
                    }
                }
            }

            if (_busy)
                EditorGUILayout.LabelField("Connecting…", EditorStyles.miniLabel);
        }

        // Awaitable-returning worker invoked with an explicit discard from UI callbacks; the body is wrapped
        // so exceptions cannot escape into Unity's synchronization context.
        private async Awaitable ConnectAsync(ClickUpIntegrationProvider provider)
        {
            _busy = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                bool ok = await provider.ConnectAsync(CancellationToken.None);
                if (this == null || target == null) return;

                SetMessage(
                    ok ? $"Connected. {provider.StatusMessage}" : "Connection failed — check the token and try again.",
                    ok ? MessageType.Info : MessageType.Error);

                // Refresh the pickers (and the "Reaches" readout) against the newly verified token.
                if (ok) _ = LoadWorkspacesAsync(provider);
            }
            catch (OperationCanceledException)
            {
                // Quietly ignore cancellation.
            }
            catch (Exception e)
            {
                SetMessage($"Connection error: {e.Message}", MessageType.Error);
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private void SetMessage(string message, MessageType type)
        {
            _lastMessage = message;
            _lastMessageType = type;
        }
    }
}
