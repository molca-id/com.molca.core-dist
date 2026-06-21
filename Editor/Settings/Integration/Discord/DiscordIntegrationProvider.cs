using System;
using System.Text;
using System.Threading;
using Molca.Editor;
using Molca.Networking.Http.Models;
using Molca.Settings.Notification;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.Discord
{
    /// <summary>
    /// Discord integration: posts build/release activity to a webhook through the shared
    /// <see cref="WebhookService"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Discord/</c>.
    /// Base class: <see cref="IntegrationProvider"/>.
    /// Registration: add the asset to <see cref="IntegrationSettings"/>' provider list. The webhook URL is a
    /// secret and is stored in <see cref="IntegrationCredentialStore"/> (per-machine, never committed); only
    /// non-secret push toggles are serialized on the asset.
    /// <para>
    /// This is the <see cref="IntegrationProvider"/>-model path for Discord. The older
    /// <see cref="DiscordNotificationProvider"/>/<see cref="BuildNotificationProvider"/> path still works; to
    /// avoid double-posting, <see cref="ShouldPushOnBuild"/> stands down when an enabled
    /// <see cref="BuildNotificationProvider"/> already covers builds. Sending reuses <see cref="WebhookService"/>
    /// rather than forking the webhook plumbing.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Discord Integration", menuName = "Molca/Editor/Integrations/Discord", order = 110)]
    public class DiscordIntegrationProvider : IntegrationProvider
    {
        [Header("Automation")]
        [Tooltip("Post a message when a build completes or fails.")]
        [SerializeField] private bool pushOnBuild = true;

        [Tooltip("Post the changelog entry when the project version is bumped.")]
        [SerializeField] private bool pushOnRelease = false;

        // Session-scoped cache; not serialized (resets on domain reload, as ConnectAsync repopulates it).
        [NonSerialized] private bool _connected;
        [NonSerialized] private string _connectedName;

        /// <inheritdoc/>
        public override string DisplayName => "Discord";

        /// <inheritdoc/>
        public override string Description => "Build & release messages";

        /// <inheritdoc/>
        public override string Glyph => "D";

        /// <inheritdoc/>
        public override string GlyphColor => "rgb(88, 101, 242)";

        /// <summary>Whether a build event should post to Discord.</summary>
        public bool PushOnBuild => pushOnBuild;

        /// <summary>Whether a version bump should post to Discord.</summary>
        public bool PushOnRelease => pushOnRelease;

        /// <inheritdoc/>
        public override bool IsConnected => _connected;

        /// <inheritdoc/>
        public override string StatusMessage
        {
            get
            {
                if (_connected)
                    return string.IsNullOrEmpty(_connectedName) ? "Connected" : $"Connected to {_connectedName}";
                if (!HasWebhook)
                    return "Not configured";
                return "Webhook saved — not verified";
            }
        }

        /// <summary>Whether a webhook URL is stored (the Discord secret), regardless of verification.</summary>
        public bool HasWebhook => IntegrationCredentialStore.HasToken(ProviderKey);

        /// <inheritdoc/>
        public override bool ShouldPushOnBuild
            => enabled && pushOnBuild && HasWebhook && !DiscordBuildNotificationActive();

        /// <inheritdoc/>
        public override bool ShouldPushOnRelease => enabled && pushOnRelease && HasWebhook;

        /// <summary>Stores the webhook URL. Pass null/empty to clear it; does not validate.</summary>
        public void SetWebhook(string webhookUrl)
        {
            IntegrationCredentialStore.SetToken(ProviderKey, webhookUrl);
            // A changed webhook invalidates the previously verified session state.
            _connected = false;
            _connectedName = null;
        }

        /// <summary>The stored webhook URL, or <c>null</c>/empty when none is set.</summary>
        public string WebhookUrl => IntegrationCredentialStore.GetToken(ProviderKey);

        /// <inheritdoc/>
        public override async Awaitable<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            _connectedName = null;

            var webhook = WebhookUrl;
            if (string.IsNullOrEmpty(webhook))
            {
                Debug.LogWarning("[Discord] No webhook URL set; cannot connect.");
                return false;
            }

            // Lightweight probe: a GET on the webhook returns its metadata without posting a message.
            cancellationToken.ThrowIfCancellationRequested();
            var request = new HttpRequest
            {
                name = "Discord GET webhook",
                method = HttpMethod.GET,
                url = webhook,
                useFullUrl = true,
                expectedResponseType = ResponseType.Json
            };

            HttpResponse response;
            try
            {
                response = await EditorHttpClient.SendAsync(request);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Discord] Webhook probe failed: {e.Message}");
                return false;
            }

            if (response == null || !response.isSuccess)
                return false;

            _connected = true;
            _connectedName = ParseWebhookName(response.text);
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
            var message = new StringBuilder();
            message.AppendLine(activity.Succeeded
                ? $"✅ **Build succeeded:** {activity.ProjectName} {activity.Version}"
                : $"❌ **Build {activity.Result}:** {activity.ProjectName} {activity.Version}");
            message.AppendLine($"Platform: {activity.Platform} · Duration: {activity.Duration.Minutes}m {activity.Duration.Seconds}s · Errors: {activity.Errors}");
            message.Append($"Triggered by: {activity.TriggeredBy}");

            await SendAsync(message.ToString(), "build", cancellationToken);
        }

        /// <inheritdoc/>
        public override async Awaitable PushReleaseActivityAsync(
            ReleaseActivity activity, CancellationToken cancellationToken = default)
        {
            var message = new StringBuilder();
            message.AppendLine($"🚀 **Release {activity.Version}:** {activity.ProjectName}");
            message.Append($"Released by: {activity.TriggeredBy}");
            if (!string.IsNullOrWhiteSpace(activity.Notes))
            {
                message.AppendLine();
                message.Append(activity.Notes.Trim());
            }

            await SendAsync(message.ToString(), "release", cancellationToken);
        }

        // Wraps the callback-based WebhookService.SendTextNotification in an Awaitable so it composes with the
        // router's async fan-out.
        private async Awaitable SendAsync(string message, string kind, CancellationToken cancellationToken)
        {
            var webhook = WebhookUrl;
            if (string.IsNullOrEmpty(webhook))
                return;

            cancellationToken.ThrowIfCancellationRequested();

            var completion = new AwaitableCompletionSource<bool>();
            WebhookService.SendTextNotification(webhook, message, success => completion.SetResult(success));

            bool ok = await completion.Awaitable;
            if (ok)
                Debug.Log($"[Discord] Posted {kind} message.");
            else
                Debug.LogWarning($"[Discord] {kind} push failed.");
        }

        // De-dupe guard: if an enabled BuildNotificationProvider already posts builds to Discord, the
        // integration provider stands down on builds so the same event is not posted twice. Non-creating
        // lookup so merely evaluating this never spawns a NotificationSettings asset.
        private static bool DiscordBuildNotificationActive()
        {
            var guids = AssetDatabase.FindAssets("t:NotificationSettings");
            if (guids.Length == 0) return false;

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            var settings = AssetDatabase.LoadAssetAtPath<NotificationSettings>(path);
            var buildProvider = settings != null ? settings.GetProvider<BuildNotificationProvider>() : null;
            return buildProvider != null && buildProvider.IsEnabled;
        }

        private static string ParseWebhookName(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonUtility.FromJson<WebhookInfo>(json)?.name;
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        private class WebhookInfo
        {
            public string name;
        }
    }
}
