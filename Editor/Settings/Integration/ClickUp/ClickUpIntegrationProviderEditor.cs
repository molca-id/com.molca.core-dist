using System;
using System.Linq;
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
    /// asset), Connect/Test/Disconnect actions, the target list id, and the automation toggles. The token text
    /// box is local UI state only and is cleared after it is saved.
    /// </remarks>
    [CustomEditor(typeof(ClickUpIntegrationProvider))]
    public class ClickUpIntegrationProviderEditor : UnityEditor.Editor
    {
        private string _tokenInput = string.Empty;
        private bool _busy;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.None;

        // Cached workspace list for the picker; populated on demand via "List".
        private ClickUpIntegrationProvider.WorkspaceInfo[] _workspaces;
        private bool _loadingWorkspaces;

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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetListId"),
                new GUIContent("Target List Id", "ClickUp list id that build/release activity is posted to."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetFolderId"),
                new GUIContent("Target Folder Id", "ClickUp folder id this project maps to; the Hub Tasks section lists tasks scoped to it."));
            DrawWorkspaceField((ClickUpIntegrationProvider)target);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnBuild"), new GUIContent("Push on Build"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnRelease"), new GUIContent("Push on Release"));
            serializedObject.ApplyModifiedProperties();

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
        }

        // Workspace id field plus a "List" button that fetches the token's workspaces and offers a picker,
        // so a multi-workspace user doesn't have to hunt down the id. Called between serializedObject
        // Update()/ApplyModifiedProperties(), so writes go through the same undo/dirty path as the fields.
        private void DrawWorkspaceField(ClickUpIntegrationProvider provider)
        {
            var prop = serializedObject.FindProperty("targetWorkspaceId");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(prop, new GUIContent(
                    "Target Workspace Id",
                    "Workspace ('team') the target folder belongs to. Required when the token can access " +
                    "more than one workspace. Leave empty to use the first accessible one."));

                using (new EditorGUI.DisabledScope(_busy || _loadingWorkspaces || !provider.HasToken))
                {
                    if (GUILayout.Button(_loadingWorkspaces ? "…" : "List", GUILayout.Width(50)))
                        _ = LoadWorkspacesAsync(provider);
                }
            }

            if (_workspaces != null && _workspaces.Length > 0)
            {
                var names = _workspaces.Select(w => $"{w.Name} ({w.Id})").ToArray();
                int current = Array.FindIndex(_workspaces, w => w.Id == prop.stringValue);
                int picked = EditorGUILayout.Popup(new GUIContent("Pick Workspace"), current, names);
                if (picked >= 0 && picked != current)
                    prop.stringValue = _workspaces[picked].Id;
            }
        }

        // Awaitable-returning worker invoked with an explicit discard; body wrapped so exceptions cannot
        // escape into Unity's synchronization context.
        private async Awaitable LoadWorkspacesAsync(ClickUpIntegrationProvider provider)
        {
            _loadingWorkspaces = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                _workspaces = await provider.FetchWorkspacesAsync(CancellationToken.None);
                if (_workspaces.Length == 0)
                    SetMessage("No workspaces returned — check the token.", MessageType.Warning);
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
