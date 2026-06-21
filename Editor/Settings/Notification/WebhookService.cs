using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using System.Collections.Generic;

namespace Molca.Settings
{
    /// <summary>
    /// Low-level webhook service for sending rich Discord notifications with embeds
    /// </summary>
    public static class WebhookService
    {
        /// <summary>
        /// Send a simple text message via webhook
        /// </summary>
        public static void SendTextNotification(string url, string message, System.Action<bool> onCompleted = null)
        {
            var payload = new DiscordPayload { content = message };
            SendWebhookRequest(url, payload, onCompleted);
        }

        /// <summary>
        /// Send a rich Discord embed notification
        /// </summary>
        public static void SendEmbedNotification(string url, DiscordEmbed embed, System.Action<bool> onCompleted = null)
        {
            var payload = new DiscordPayload { embeds = new List<DiscordEmbed> { embed } };
            SendWebhookRequest(url, payload, onCompleted);
        }

        /// <summary>
        /// Send a rich Discord embed notification with content (for mentions)
        /// </summary>
        public static void SendEmbedNotificationWithContent(string url, string content, DiscordEmbed embed, System.Action<bool> onCompleted = null)
        {
            var payload = new DiscordPayload
            {
                content = content,
                embeds = new List<DiscordEmbed> { embed }
            };
            SendWebhookRequest(url, payload, onCompleted);
        }

        /// <summary>
        /// Send multiple Discord embed notifications
        /// </summary>
        public static void SendEmbedNotifications(string url, List<DiscordEmbed> embeds, System.Action<bool> onCompleted = null)
        {
            var payload = new DiscordPayload { embeds = embeds };
            SendWebhookRequest(url, payload, onCompleted);
        }

        /// <summary>
        /// Send multiple Discord embed notifications with content (for mentions)
        /// </summary>
        public static void SendEmbedNotificationsWithContent(string url, string content, List<DiscordEmbed> embeds, System.Action<bool> onCompleted = null)
        {
            var payload = new DiscordPayload
            {
                content = content,
                embeds = embeds
            };
            SendWebhookRequest(url, payload, onCompleted);
        }

        private static void SendWebhookRequest(string url, DiscordPayload payload, System.Action<bool> onCompleted = null)
        {
            string jsonPayload = JsonUtility.ToJson(payload);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            var operation = request.SendWebRequest();
            operation.completed += (asyncOperation) =>
            {
                bool success = request.result == UnityWebRequest.Result.Success;
                if (!success)
                {
                    Debug.LogError($"Error sending webhook notification: {request.error}");
                }
                onCompleted?.Invoke(success);
                request.Dispose();
            };
        }

        /// <summary>
        /// Discord embed structure for rich notifications
        /// </summary>
        [System.Serializable]
        public class DiscordEmbed
        {
            public string title;
            public string description;
            public int color;
            public List<DiscordEmbedField> fields = new List<DiscordEmbedField>();
            public DiscordEmbedFooter footer;
            public DiscordEmbedAuthor author;
            public string thumbnail_url;
            public string image_url;
            public string url;

            public DiscordEmbed(string title = null, string description = null, int color = 0)
            {
                this.title = title;
                this.description = description;
                this.color = color;
            }

            public void AddField(string name, string value, bool inline = false)
            {
                fields.Add(new DiscordEmbedField { name = name, value = value, inline = inline });
            }
        }

        /// <summary>
        /// Field for Discord embed
        /// </summary>
        [System.Serializable]
        public class DiscordEmbedField
        {
            public string name;
            public string value;
            public bool inline;
        }

        /// <summary>
        /// Footer for Discord embed
        /// </summary>
        [System.Serializable]
        public class DiscordEmbedFooter
        {
            public string text;
            public string icon_url;
        }

        /// <summary>
        /// Author for Discord embed
        /// </summary>
        [System.Serializable]
        public class DiscordEmbedAuthor
        {
            public string name;
            public string url;
            public string icon_url;
        }

        [System.Serializable]
        private class DiscordPayload
        {
            public string content;
            public List<DiscordEmbed> embeds;
        }
    }
}

