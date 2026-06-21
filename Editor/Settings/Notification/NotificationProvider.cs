using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Notification
{
    /// <summary>
    /// Abstract base class for all notification providers.
    /// Extend this class to create custom notification systems (Discord, Slack, Email, etc.)
    /// Similar to SettingModule pattern for extensibility.
    /// </summary>
    public abstract class NotificationProvider : ScriptableObject
    {
        [SerializeField] protected bool enabled = true;

        /// <summary>
        /// Unique identifier for this notification provider
        /// </summary>
        protected string ProviderId { get; private set; }

        /// <summary>
        /// Initialize the notification provider
        /// </summary>
        public virtual void Initialize()
        {
            ProviderId = GetType().FullName;
        }

        /// <summary>
        /// Check if this notification provider is enabled and properly configured
        /// </summary>
        public abstract bool IsEnabled { get; }

        /// <summary>
        /// Display name for this notification provider
        /// </summary>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Send a notification with the given data
        /// </summary>
        /// <param name="notification">The notification data to send</param>
        /// <param name="onCompleted">Callback with success/failure result</param>
        public abstract void SendNotification(NotificationData notification, System.Action<bool> onCompleted = null);

        /// <summary>
        /// Validate the configuration of this provider
        /// </summary>
        public virtual bool ValidateConfiguration()
        {
            return IsEnabled;
        }

        /// <summary>
        /// Get configuration status message
        /// </summary>
        public virtual string GetStatusMessage()
        {
            return IsEnabled ? "Enabled and configured" : "Disabled";
        }
    }

    /// <summary>
    /// Standard notification data structure
    /// </summary>
    [System.Serializable]
    public class NotificationData
    {
        public string title;
        public string message;
        public NotificationType type;
        public System.Collections.Generic.Dictionary<string, string> metadata;

        public NotificationData(string title, string message, NotificationType type = NotificationType.Info)
        {
            this.title = title;
            this.message = message;
            this.type = type;
            this.metadata = new System.Collections.Generic.Dictionary<string, string>();

            // Automatically capture user information
            var editorUserName = CloudProjectSettings.userName;
            var triggeredBy = string.IsNullOrWhiteSpace(editorUserName)
                ? System.Environment.UserName
                : editorUserName;
            AddMetadata("Triggered By", triggeredBy);
            AddMetadata("Machine", System.Environment.MachineName);
        }

        public void AddMetadata(string key, string value)
        {
            metadata[key] = value;
        }
    }

    /// <summary>
    /// Standard notification types
    /// </summary>
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }
}
