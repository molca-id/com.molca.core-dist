using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Molca.Editor;

namespace Molca.Settings.Notification
{
    /// <summary>
    /// Editor Notification provider for Unity build process events.
    /// Sends notifications when builds start, complete, or fail.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Build Notification Provider", menuName = "Molca/Editor/Notifications/Build Notification Provider", order = 110)]
    public class BuildNotificationProvider : DiscordNotificationProvider
    {
        public override string DisplayName => "Build Process Notifications";

        /// <summary>
        /// Static wrapper class that implements Unity's build callbacks.
        /// This is necessary because Unity's IPreprocessBuildWithReport and IPostprocessBuildWithReport
        /// don't work properly on ScriptableObject instances - Unity scans for types, not instances.
        /// </summary>
        private class BuildCallbackHandler : IPreprocessBuildWithReport, IPostprocessBuildWithReport
        {
            public int callbackOrder => 0;

            public void OnPreprocessBuild(BuildReport report)
            {
                var notificationSettings = NotificationSettings.GetOrCreateSettings();
                var provider = notificationSettings.GetProvider<BuildNotificationProvider>();

                if (provider != null)
                {
                    provider.OnPreprocessBuildInternal(report);
                }
            }

            public void OnPostprocessBuild(BuildReport report)
            {
                var notificationSettings = NotificationSettings.GetOrCreateSettings();
                var provider = notificationSettings.GetProvider<BuildNotificationProvider>();

                if (provider != null)
                {
                    provider.OnPostprocessBuildInternal(report);
                }
            }
        }

        private void OnPreprocessBuildInternal(BuildReport report)
        {
            // Version sync, changelog append, and platform version codes are owned by
            // BuildVersionPreprocessor so they run for every build (Build Manager, File > Build, CI),
            // not only when this notification provider asset exists. This provider only reports.
            SendBuildStartNotification(report);
        }

        private void OnPostprocessBuildInternal(BuildReport report)
        {
            // IPostprocessBuildWithReport fires for failed/cancelled builds too, so the
            // report result replaces the old EditorApplication.update polling.
            if (report.summary.result == BuildResult.Failed || report.summary.result == BuildResult.Cancelled)
            {
                if (IsEnabled)
                {
                    SendBuildFailureNotification();
                }
                return;
            }

            // Build-number increment is owned by BuildVersionPostprocessor.
            SendBuildCompleteNotification(report);
        }

        private void SendBuildStartNotification(BuildReport report)
        {
            if (!IsEnabled) return;

            var editorSettings = MolcaEditorSettings.Instance;
            var buildOptions = report.summary.options;
            bool isDevelopmentBuild = (buildOptions & BuildOptions.Development) != 0;

            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(report.summary.platform);
            var scriptingBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup));
            bool isIL2CPP = scriptingBackend == ScriptingImplementation.IL2CPP;

            string version = "Unknown";
            if (editorSettings?.VersionSettings != null)
            {
                version = editorSettings.VersionSettings.GetFullVersionString();
            }

            var embed = CreateEmbed(
                "Build Started",
                $"Build started for {ProjectName} on {report.summary.platform}",
                0x0099ff // Blue
            );

            embed.AddField("Version", version, true);
            embed.AddField("Development", isDevelopmentBuild.ToString(), true);
            embed.AddField("IL2CPP", isIL2CPP.ToString(), true);

            var editorUserName = CloudProjectSettings.userName;
            var triggeredBy = string.IsNullOrWhiteSpace(editorUserName)
                ? System.Environment.UserName
                : editorUserName;
            embed.AddField("Triggered by", triggeredBy, true);

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Build start notification sent successfully.");
                }
            });
        }

        private void SendBuildCompleteNotification(BuildReport report)
        {
            if (!IsEnabled) return;

            var editorSettings = MolcaEditorSettings.Instance;
            var summary = report.summary;

            int color = summary.totalErrors == 0 && summary.totalSize > 0 ? 0x00ff00 :
                       summary.totalErrors > 0 ? 0xff0000 :
                       0x808080;

            var embed = CreateEmbed(
                "Build Completed",
                $"Build for {ProjectName} on {summary.platform} completed",
                color
            );

            string version = "Unknown";
            if (editorSettings?.VersionSettings != null)
            {
                version = editorSettings.VersionSettings.GetFullVersionString();
            }

            embed.AddField("Version", version, true);
            embed.AddField("Duration", $"{summary.totalTime.Minutes}m {summary.totalTime.Seconds}s", true);

            if (summary.totalSize > 0)
            {
                embed.AddField("Size", $"{summary.totalSize / (1024 * 1024)} MB", true);
            }

            embed.AddField("Errors", summary.totalErrors.ToString(), true);

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Build completion notification sent successfully.");
                }
            });
        }

        private void SendBuildFailureNotification()
        {
            var embed = CreateEmbed(
                "Build Failed",
                $"Build for {ProjectName} failed before completion. Check Unity console for details.",
                0xff0000 // Red
            );

            SendEmbedNotification(embed, (success) =>
            {
                if (success)
                {
                    Debug.Log("Build failure notification sent successfully.");
                }
            });
        }
    }
}
