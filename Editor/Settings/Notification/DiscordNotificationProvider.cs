using UnityEngine;
using System;

namespace Molca.Settings.Notification
{
    /// <summary>
    /// Base class for Discord webhook notification providers.
    /// Handles Discord-specific features like embeds, mentions, and thread IDs.
    /// For other services (Slack, Teams, etc.), create a separate base class.
    /// </summary>
    public abstract class DiscordNotificationProvider : NotificationProvider
    {
        [Header("Webhook Configuration")]
        [SerializeField] protected string webhookUrl;
        [SerializeField] protected string threadId;

        [Header("Mentions")]
        [SerializeField] protected MentionType mentionType = MentionType.None;
        [SerializeField] protected string mentionId;

        private DateTime? lastNotificationTime;
        private const double MINIMUM_NOTIFICATION_INTERVAL = 1;

        public override bool IsEnabled => enabled && !string.IsNullOrEmpty(webhookUrl);

        /// <summary>
        /// Get project name from MolcaProjectSettings
        /// </summary>
        protected string ProjectName
        {
            get
            {
#if UNITY_EDITOR
                var projectSettings = MolcaProjectSettings.Instance;
                return projectSettings != null ? projectSettings.ProjectName : "Molca Project";
#else
                return "Molca Project";
#endif
            }
        }

        /// <summary>
        /// Get the full webhook URL including thread ID if configured
        /// </summary>
        protected string GetWebhookUrl()
        {
            if (string.IsNullOrEmpty(webhookUrl))
                return null;

            var url = webhookUrl;
            if (!string.IsNullOrEmpty(threadId))
            {
                url += $"?thread_id={threadId}";
            }
            return url;
        }

        /// <summary>
        /// Get mention string based on configuration
        /// </summary>
        protected string GetMentionString()
        {
            if (mentionType == MentionType.None || string.IsNullOrEmpty(mentionId))
            {
                return null;
            }

            switch (mentionType)
            {
                case MentionType.User:
                    return $"<@{mentionId}>";
                case MentionType.Role:
                    return $"<@&{mentionId}>";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Apply mentions to message
        /// </summary>
        protected string ApplyMentions(string message)
        {
            var mentionString = GetMentionString();
            if (!string.IsNullOrEmpty(mentionString))
            {
                return $"{mentionString} {message}";
            }
            return message;
        }

        /// <summary>
        /// Send a simple text notification
        /// </summary>
        protected void SendTextNotification(string message, Action<bool> onCompleted = null)
        {
            if (!IsEnabled)
            {
                Debug.LogWarning($"{DisplayName}: Webhook is not enabled or configured");
                onCompleted?.Invoke(false);
                return;
            }

            if (lastNotificationTime.HasValue && DateTime.Now - lastNotificationTime.Value < TimeSpan.FromSeconds(MINIMUM_NOTIFICATION_INTERVAL))
            {
                Debug.LogWarning($"{DisplayName}: Notification sent too recently. Skipping.");
                onCompleted?.Invoke(false);
                return;
            }

            var fullMessage = ApplyMentions(message);
            WebhookService.SendTextNotification(GetWebhookUrl(), fullMessage, onCompleted);
            lastNotificationTime = DateTime.Now;
        }

        /// <summary>
        /// Send a rich embed notification
        /// </summary>
        protected void SendEmbedNotification(WebhookService.DiscordEmbed embed, Action<bool> onCompleted = null)
        {
            if (!IsEnabled)
            {
                Debug.LogWarning($"{DisplayName}: Webhook is not enabled or configured");
                onCompleted?.Invoke(false);
                return;
            }

            if (lastNotificationTime.HasValue && DateTime.Now - lastNotificationTime.Value < TimeSpan.FromSeconds(MINIMUM_NOTIFICATION_INTERVAL))
            {
                Debug.LogWarning($"{DisplayName}: Notification sent too recently. Skipping.");
                onCompleted?.Invoke(false);
                return;
            }

            var mentionString = GetMentionString();
            WebhookService.SendEmbedNotificationWithContent(GetWebhookUrl(), mentionString, embed, onCompleted);
            lastNotificationTime = DateTime.Now;
        }

        /// <summary>
        /// Create a Discord embed with project footer
        /// </summary>
        protected WebhookService.DiscordEmbed CreateEmbed(string title, string description, int color)
        {
            var embed = new WebhookService.DiscordEmbed(title, description, color);
            embed.footer = new WebhookService.DiscordEmbedFooter
            {
                text = ProjectName
            };
            return embed;
        }

        /// <summary>
        /// Get color for notification type
        /// </summary>
        protected int GetColorForType(NotificationType type)
        {
            switch (type)
            {
                case NotificationType.Success:
                    return 0x00ff00; // Green
                case NotificationType.Warning:
                    return 0xffff00; // Yellow
                case NotificationType.Error:
                    return 0xff0000; // Red
                case NotificationType.Info:
                default:
                    return 0x0099ff; // Blue
            }
        }

        public override void SendNotification(NotificationData notification, Action<bool> onCompleted = null)
        {
            var embed = CreateEmbed(notification.title, notification.message, GetColorForType(notification.type));

            // Add metadata as fields
            if (notification.metadata != null)
            {
                foreach (var kvp in notification.metadata)
                {
                    embed.AddField(kvp.Key, kvp.Value, true);
                }
            }

            SendEmbedNotification(embed, onCompleted);
        }

        public override bool ValidateConfiguration()
        {
            return base.ValidateConfiguration() && !string.IsNullOrEmpty(webhookUrl);
        }

        public override string GetStatusMessage()
        {
            if (!enabled)
                return "Disabled";
            if (string.IsNullOrEmpty(webhookUrl))
                return "Not configured - missing webhook URL";
            return "Enabled and configured";
        }

        public enum MentionType
        {
            None,
            User,
            Role
        }
    }
}

