using System;
using System.Threading;
using Molca.Settings.Integration.OAuth;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Inspector for <see cref="FigmaIntegrationProvider"/>: the connect-via-token config surface the Hub
    /// Integrations card launches to.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// The token field is masked and routed through <see cref="IntegrationCredentialStore"/> (never serialized
    /// on the asset). Only non-secret defaults (file key, team id, output folder) are drawn as serialized
    /// properties. Connect/Test run <see cref="FigmaIntegrationProvider.ConnectAsync"/>; cancellation is not
    /// surfaced as an error.
    /// </remarks>
    [CustomEditor(typeof(FigmaIntegrationProvider))]
    public class FigmaIntegrationProviderEditor : UnityEditor.Editor
    {
        private string _tokenInput = string.Empty;
        private bool _busy;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.None;

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var provider = (FigmaIntegrationProvider)target;

            EditorGUILayout.LabelField("Figma Integration", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Status", GUILayout.Width(60));
                EditorGUILayout.LabelField(provider.StatusMessage, EditorStyles.miniBoldLabel);
            }

            EditorGUILayout.Space(6);
            DrawOAuthSection(provider);
            EditorGUILayout.Space(6);
            DrawTokenSection(provider);
            EditorGUILayout.Space(6);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"), new GUIContent("Enabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("oauthClientId"),
                new GUIContent("OAuth Client Id", "Public Figma OAuth app client id for browser sign-in. Not a secret."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("oauthScope"),
                new GUIContent("OAuth Scope", "Space-delimited scopes to request (e.g. \"file_read\")."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("teamId"), new GUIContent("Team Id"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultFileKey"), new GUIContent("Default File Key"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("outputFolder"), new GUIContent("Output Folder"));
            serializedObject.ApplyModifiedProperties();

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
        }

        private void DrawOAuthSection(FigmaIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Sign in with Figma (browser)", EditorStyles.miniBoldLabel);

            if (!provider.SupportsOAuth)
            {
                EditorGUILayout.HelpBox(
                    "Set an OAuth Client Id below to enable browser sign-in (loopback + PKCE, no secret). " +
                    "Until then, use a personal access token.", MessageType.None);
                return;
            }

            using (new EditorGUI.DisabledScope(_busy))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(provider.HasOAuthTokens ? "Re-authorize" : "Sign in with Figma"))
                        _ = SignInAsync(provider);

                    using (new EditorGUI.DisabledScope(!provider.HasOAuthTokens))
                    {
                        if (GUILayout.Button("Sign out (OAuth)"))
                        {
                            provider.SignOut();
                            SetMessage("Signed out and cleared the OAuth tokens.", MessageType.Info);
                        }
                    }
                }
            }

            if (_busy)
                EditorGUILayout.HelpBox("Waiting for the browser to complete authorization…", MessageType.Info);
        }

        // Awaitable-returning worker invoked with an explicit discard; body wrapped so exceptions cannot escape.
        private async Awaitable SignInAsync(FigmaIntegrationProvider provider)
        {
            _busy = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                var result = await provider.BeginAuthorizationAsync(CancellationToken.None);
                if (result.Success)
                {
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
            catch (OperationCanceledException) { }
            catch (Exception e) { SetMessage($"Sign-in error: {e.Message}", MessageType.Error); }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private void DrawTokenSection(FigmaIntegrationProvider provider)
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

        private async Awaitable ConnectAsync(FigmaIntegrationProvider provider)
        {
            _busy = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                bool ok = await provider.ConnectAsync(CancellationToken.None);
                SetMessage(
                    ok ? $"Connected. {provider.StatusMessage}" : "Connection failed — check the token.",
                    ok ? MessageType.Info : MessageType.Error);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { SetMessage($"Connection error: {e.Message}", MessageType.Error); }
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
