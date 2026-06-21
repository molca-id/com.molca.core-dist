using UnityEngine;
using UnityEditor;

namespace Molca.Settings.Notification
{
    /// <summary>
    /// Notification provider for Unity light baking events.
    /// Sends notifications when light baking starts and completes.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Light Baking Notification Provider", menuName = "Molca/Editor/Notifications/Light Baking Notification Provider", order = 110)]
    public class LightBakingNotificationProvider : DiscordNotificationProvider
    {
        private static float s_startTime;
        private static LightBakingNotificationProvider s_instance;

        public override string DisplayName => "Light Baking Notifications";

        [InitializeOnLoadMethod]
        private static void RegisterCallbacks()
        {
            Lightmapping.bakeStarted += OnLightBakeStarted;
            Lightmapping.bakeCompleted += OnLightBakeCompleted;
        }

        private static void OnLightBakeStarted()
        {
            s_startTime = Time.realtimeSinceStartup;

            var notificationSettings = NotificationSettings.GetOrCreateSettings();
            var provider = notificationSettings.GetProvider<LightBakingNotificationProvider>();

            if (provider != null && provider.IsEnabled)
            {
                provider.SendLightBakeStartNotification();
            }
        }

        private static void OnLightBakeCompleted()
        {
            float duration = Time.realtimeSinceStartup - s_startTime;

            var notificationSettings = NotificationSettings.GetOrCreateSettings();
            var provider = notificationSettings.GetProvider<LightBakingNotificationProvider>();

            if (provider != null && provider.IsEnabled)
            {
                provider.SendLightBakeCompleteNotification(duration);
            }
        }

        private void SendLightBakeStartNotification()
        {
            var embed = CreateEmbed(
                "Light Baking Started",
                $"Light baking started for {ProjectName}",
                0x0099ff // Blue
            );

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Light baking start notification sent successfully.");
                }
            });
        }

        private void SendLightBakeCompleteNotification(float duration)
        {
            var embed = CreateEmbed(
                "Light Baking Completed",
                $"Light baking completed for {ProjectName}",
                0x00ff00 // Green
            );

            embed.AddField("Duration", $"{duration:F2} seconds", true);

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Light baking completion notification sent successfully.");
                }
            });
        }
    }
}
