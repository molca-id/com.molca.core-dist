#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using Molca.Editor.ContentPackage;

namespace Molca.Settings.Notification
{
    /// <summary>
    /// Notification provider for Addressables build events.
    /// Automatically sends notifications when Addressables content is built via AddressablesBuildUtility.
    /// </summary>
    [CreateAssetMenu(fileName = "Addressables Build Notification Provider", menuName = "Molca/SDK/Notifications/Addressables Build Notification Provider", order = 120)]
    public class AddressablesBuildNotificationProvider : DiscordNotificationProvider
    {
        private AddressablesBuildUtility.BuildOptions _currentBuildOptions;
        
        public override string DisplayName => "Addressables Build Notifications";

        [InitializeOnLoadMethod]
        private static void Init()
        {
            // Unsubscribe first to prevent duplicates
            AddressablesBuildUtility.OnBuildStarted -= OnBuildStarted;
            AddressablesBuildUtility.OnBuildCompleted -= OnBuildCompleted;
            
            // Subscribe to Addressables build events from AddressablesBuildUtility
            AddressablesBuildUtility.OnBuildStarted += OnBuildStarted;
            AddressablesBuildUtility.OnBuildCompleted += OnBuildCompleted;
        }

        private static void OnBuildStarted(AddressablesBuildUtility.BuildOptions options)
        {
            var notificationSettings = NotificationSettings.GetOrCreateSettings();
            var provider = notificationSettings.GetProvider<AddressablesBuildNotificationProvider>();
            
            if (provider != null && provider.IsEnabled)
            {
                provider._currentBuildOptions = options;
                provider.SendBuildStartNotification(options);
            }
        }

        private static void OnBuildCompleted(AddressablesBuildUtility.BuildResult result)
        {
            var notificationSettings = NotificationSettings.GetOrCreateSettings();
            var provider = notificationSettings.GetProvider<AddressablesBuildNotificationProvider>();
            
            if (provider != null && provider.IsEnabled)
            {
                provider.SendBuildCompleteNotification(result);
            }
        }

        private void SendBuildStartNotification(AddressablesBuildUtility.BuildOptions options)
        {
            var addressablesSettings = AddressableAssetSettingsDefaultObject.Settings;
            string profileName = addressablesSettings != null 
                ? addressablesSettings.profileSettings.GetProfileName(addressablesSettings.activeProfileId)
                : "Unknown";

            var embed = CreateEmbed(
                "Addressables Build Started",
                $"Addressables build started for {ProjectName}",
                0x0099ff // Blue
            );

            embed.AddField("Profile", profileName, true);
            embed.AddField("Clean Build", options.CleanBuild ? "Yes" : "No", true);
            
            if (options.TargetGroups != null && options.TargetGroups.Count > 0)
            {
                embed.AddField("Target Groups", options.TargetGroups.Count.ToString(), true);
            }
            else
            {
                embed.AddField("Groups", GetGroupCount().ToString(), true);
            }

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Addressables build start notification sent successfully.");
                }
            });
        }

        private void SendBuildCompleteNotification(AddressablesBuildUtility.BuildResult result)
        {
            var addressablesSettings = AddressableAssetSettingsDefaultObject.Settings;
            string profileName = addressablesSettings != null 
                ? addressablesSettings.profileSettings.GetProfileName(addressablesSettings.activeProfileId)
                : "Unknown";

            int color = result.Success ? 0x00ff00 : 0xff0000; // Green if success, Red if failure
            string title = result.Success ? "Addressables Build Completed" : "Addressables Build Failed";
            string description = result.Success 
                ? $"Addressables build for {ProjectName} completed successfully"
                : $"Addressables build for {ProjectName} failed";

            var embed = CreateEmbed(title, description, color);

            embed.AddField("Profile", profileName, true);
            embed.AddField("Duration", $"{result.Duration:F2}s", true);
            
            if (result.BuiltGroups != null && result.BuiltGroups.Count > 0)
            {
                embed.AddField("Built Groups", result.BuiltGroups.Count.ToString(), true);
            }

            if (result.Success && result.TotalSize > 0)
            {
                embed.AddField("Size", FormatFileSize(result.TotalSize), true);
            }

            if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                // Truncate error if too long (Discord has field value limits)
                string errorMessage = result.ErrorMessage.Length > 1000 
                    ? result.ErrorMessage.Substring(0, 997) + "..." 
                    : result.ErrorMessage;
                embed.AddField("Error", errorMessage, false);
            }

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Addressables build completion notification sent successfully.");
                }
            });
        }

        private int GetGroupCount()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || settings.groups == null)
                return 0;
            
            // Count non-default groups
            int count = 0;
            foreach (var group in settings.groups)
            {
                if (group != null && !group.IsDefaultGroup())
                    count++;
            }
            return count;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
}
#endif

