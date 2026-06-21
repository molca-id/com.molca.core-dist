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
    /// the automation toggles. The dropdowns author the target ids by name (fetched via the provider's
    /// <see cref="Awaitable"/> APIs off the render path); there is no manual id entry. The token text box is
    /// local UI state only and is cleared after it is saved.
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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnBuild"), new GUIContent("Push on Build"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnRelease"), new GUIContent("Push on Release"));
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
                        _ = LoadWorkspacesAsync(provider);
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
                new GUIContent("List", "ClickUp list that build/release activity is posted to."),
                listProp, listOptions, folderSelected, "list");
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
                _workspaces = await provider.FetchWorkspacesAsync(CancellationToken.None);
                if (this == null || target == null) return;
                if (_workspaces.Length == 0)
                    SetMessage("No workspaces returned — check the token.", MessageType.Warning);

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
                var folders = await provider.FetchFoldersAsync(workspaceId, CancellationToken.None);
                if (this == null || target == null) return;
                _folders = folders;
                if (folders.Length == 0)
                    SetMessage("No folders found in this workspace.", MessageType.Info);
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
        }

        private void DrawTokenSection(ClickUpIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Personal API Token", EditorStyles.miniBoldLabel);

            EditorGUILayout.HelpBox(
                "Why a personal token (not OAuth sign-in like GitHub/Figma): ClickUp's OAuth is an " +
                "authorization-code flow that requires a confidential client_secret and supports neither " +
                "PKCE nor device flow. A distributable editor tool cannot embed a secret or host a callback, " +
                "so OAuth is unshippable here and the personal token is the supported path (Sprint 32). An " +
                "OAuth path behind user-supplied app credentials is deferred.", MessageType.None);
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
                SetMessage(
                    ok ? $"Connected. {provider.StatusMessage}" : "Connection failed — check the token and try again.",
                    ok ? MessageType.Info : MessageType.Error);
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
