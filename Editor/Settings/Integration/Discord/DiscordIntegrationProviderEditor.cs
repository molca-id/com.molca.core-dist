using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.Discord
{
    /// <summary>
    /// Inspector for <see cref="DiscordIntegrationProvider"/> — the config surface the Hub card launches to.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Discord/</c>.
    /// Registration: <see cref="CustomEditor"/> for <see cref="DiscordIntegrationProvider"/>.
    /// Renders a masked webhook field (the webhook URL persists in <see cref="IntegrationCredentialStore"/>,
    /// never on the asset), Connect/Test/Disconnect actions, and the automation toggles. The webhook text box
    /// is local UI state only and is cleared after it is saved.
    /// </remarks>
    [CustomEditor(typeof(DiscordIntegrationProvider))]
    public class DiscordIntegrationProviderEditor : UnityEditor.Editor
    {
        private string _webhookInput = string.Empty;
        private bool _busy;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.None;

        public override void OnInspectorGUI()
        {
            var provider = (DiscordIntegrationProvider)target;

            EditorGUILayout.LabelField("Discord Integration", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Status", GUILayout.Width(60));
                EditorGUILayout.LabelField(provider.StatusMessage, EditorStyles.miniBoldLabel);
            }
            EditorGUILayout.Space(6);

            DrawWebhookSection(provider);
            EditorGUILayout.Space(6);

            // Non-secret config lives on the asset; edit through SerializedObject so undo/dirty work normally.
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"), new GUIContent("Enabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnBuild"), new GUIContent("Push on Build"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pushOnRelease"), new GUIContent("Push on Release"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.HelpBox(
                "If a Build Notification Provider (Discord) is also enabled, this integration stands down on " +
                "builds to avoid posting twice.", MessageType.None);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Why no OAuth sign-in here: the Discord credential is an incoming-webhook URL, not a user " +
                "token. OAuth (webhook.incoming) would only auto-provision that URL for marginal gain, so it " +
                "is intentionally out of scope (Sprint 32). GitHub and Figma use OAuth because their " +
                "credential is a user token.", MessageType.None);

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
        }

        private void DrawWebhookSection(DiscordIntegrationProvider provider)
        {
            EditorGUILayout.LabelField("Webhook URL", EditorStyles.miniBoldLabel);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (provider.HasWebhook)
                {
                    EditorGUILayout.HelpBox(
                        "A webhook is stored for this machine (EditorUserSettings — not committed). " +
                        "Enter a new URL below to replace it, or Disconnect to clear it.",
                        MessageType.None);
                }

                _webhookInput = EditorGUILayout.PasswordField("Webhook", _webhookInput);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_webhookInput)))
                    {
                        if (GUILayout.Button("Save & Connect"))
                        {
                            provider.SetWebhook(_webhookInput.Trim());
                            _webhookInput = string.Empty;
                            GUIUtility.keyboardControl = 0;
                            _ = ConnectAsync(provider);
                        }
                    }

                    using (new EditorGUI.DisabledScope(!provider.HasWebhook))
                    {
                        if (GUILayout.Button("Test Connection"))
                            _ = ConnectAsync(provider);

                        if (GUILayout.Button("Disconnect"))
                        {
                            provider.Disconnect();
                            SetMessage("Disconnected and cleared the stored webhook.", MessageType.Info);
                        }
                    }
                }
            }

            if (_busy)
                EditorGUILayout.LabelField("Connecting…", EditorStyles.miniLabel);
        }

        // Awaitable-returning worker invoked with an explicit discard from UI callbacks; the body is wrapped
        // so exceptions cannot escape into Unity's synchronization context.
        private async Awaitable ConnectAsync(DiscordIntegrationProvider provider)
        {
            _busy = true;
            SetMessage(null, MessageType.None);
            Repaint();
            try
            {
                bool ok = await provider.ConnectAsync(CancellationToken.None);
                SetMessage(
                    ok ? $"Connected. {provider.StatusMessage}" : "Connection failed — check the webhook URL.",
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
