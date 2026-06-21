using System;
using System.Threading;
using Molca.Settings.Integration.OAuth;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.GitHub
{
    /// <summary>
    /// Inspector for <see cref="GitHubIntegrationProvider"/> — the config surface the Hub card launches to.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/GitHub/</c>.
    /// Registration: <see cref="CustomEditor"/> for <see cref="GitHubIntegrationProvider"/>.
    /// Renders a masked token field (token persists in <see cref="IntegrationCredentialStore"/>, never on the
    /// asset), Connect/Test/Disconnect actions, the owner/repo, and the automation toggles. The token text box
    /// is local UI state only and is cleared after it is saved.
    /// </remarks>
    [CustomEditor(typeof(GitHubIntegrationProvider))]
    public class GitHubIntegrationProviderEditor : UnityEditor.Editor
    {
        private string _tokenInput = string.Empty;
        private bool _busy;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.None;

        // Device-flow UI state: the issued user code is shown until sign-in completes or is cleared.
        private DeviceCodeInfo _deviceCode;
        private bool _hasDeviceCode;

        public override void OnInspectorGUI()
        {
            var provider = (GitHubIntegrationProvider)target;

            EditorGUILayout.LabelField("GitHub Integration", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            DrawConnectionStatus(provider);
            EditorGUILayout.Space(6);

            DrawOAuthSection(provider);
            EditorGUILayout.Space(6);

            DrawTokenSection(provider);
            EditorGUILayout.Space(6);

            // Non-secret config lives on the asset; edit through SerializedObject so undo/dirty work normally.
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"), new GUIContent("Enabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("oauthClientId"),
                new GUIContent("OAuth Client Id", "Public GitHub OAuth App client id for device-flow sign-in. Not a secret."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("oauthScope"),
                new GUIContent("OAuth Scope", "Space-delimited scopes to request (e.g. \"repo read:org\")."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("owner"),
                new GUIContent("Owner", "Repository owner (user or organization)."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("repo"),
                new GUIContent("Repository", "Repository name."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnBuild"),
                new GUIContent("Issue on Failed Build"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnRelease"),
                new GUIContent("Publish Release on Version Bump"));
            serializedObject.ApplyModifiedProperties();

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
        }

        private void DrawConnectionStatus(GitHubIntegrationProvider provider)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Status", GUILayout.Width(60));
                EditorGUILayout.LabelField(provider.StatusMessage, EditorStyles.miniBoldLabel);
            }
        }

        private void DrawOAuthSection(GitHubIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Sign in with GitHub (device flow)", EditorStyles.miniBoldLabel);

            if (!provider.SupportsOAuth)
            {
                EditorGUILayout.HelpBox(
                    "Set an OAuth Client Id below to enable device-flow sign-in (no token paste, no secret). " +
                    "Until then, use a personal access token.", MessageType.None);
                return;
            }

            using (new EditorGUI.DisabledScope(_busy))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(provider.HasOAuthTokens ? "Re-authorize" : "Sign in with GitHub"))
                        _ = SignInAsync(provider);

                    using (new EditorGUI.DisabledScope(!provider.HasOAuthTokens))
                    {
                        if (GUILayout.Button("Sign out (OAuth)"))
                        {
                            provider.SignOut();
                            _hasDeviceCode = false;
                            SetMessage("Signed out and cleared the OAuth tokens.", MessageType.Info);
                        }
                    }
                }
            }

            if (_hasDeviceCode)
                DrawDeviceCodePanel();
        }

        private void DrawDeviceCodePanel()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"Enter code  {_deviceCode.UserCode}  at {_deviceCode.VerificationUri}\nWaiting for authorization…",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open browser"))
                    Application.OpenURL(_deviceCode.VerificationUri);
                if (GUILayout.Button("Copy code"))
                    EditorGUIUtility.systemCopyBuffer = _deviceCode.UserCode;
            }
        }

        // Awaitable-returning worker invoked with an explicit discard; body wrapped so exceptions cannot escape.
        private async Awaitable SignInAsync(GitHubIntegrationProvider provider)
        {
            _busy = true;
            _hasDeviceCode = false;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                var result = await provider.ConnectWithDeviceFlowAsync(info =>
                {
                    _deviceCode = info;
                    _hasDeviceCode = true;
                    Repaint();
                }, CancellationToken.None);

                _hasDeviceCode = false;
                if (result.Success)
                {
                    // Validate + populate the connected name for the status line.
                    await provider.ConnectAsync(CancellationToken.None);
                    SetMessage($"Signed in. {provider.StatusMessage}", MessageType.Info);
                }
                else if (result.Canceled)
                {
                    SetMessage("Sign-in canceled.", MessageType.None);
                }
                else
                {
                    SetMessage($"Sign-in failed: {result.Error}", MessageType.Error);
                }
            }
            catch (OperationCanceledException)
            {
                // Quietly ignore cancellation.
            }
            catch (Exception e)
            {
                SetMessage($"Sign-in error: {e.Message}", MessageType.Error);
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private void DrawTokenSection(GitHubIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Personal Access Token", EditorStyles.miniBoldLabel);

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
        private async Awaitable ConnectAsync(GitHubIntegrationProvider provider)
        {
            _busy = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                bool ok = await provider.ConnectAsync(CancellationToken.None);
                SetMessage(
                    ok ? $"Connected. {provider.StatusMessage}" : "Connection failed — check the token and repository.",
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
