using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Molca.Modals;

namespace Molca.App
{
    /// <summary>
    /// Simple notification modal that shows an icon and text, then auto-closes after a few seconds.
    /// </summary>
    public class NotificationModal : BaseModal
    {
        [Header("UI Elements")]
        [Tooltip("The icon image to display")]
        [SerializeField] private Image iconImage;
        
        [Tooltip("The text to display")]
        [SerializeField] private TextMeshProUGUI messageText;
        
        [Header("Auto-Close Settings")]
        [Tooltip("Time in seconds before the modal auto-closes")]
        [SerializeField] private float autoCloseDelay = 3f;
        
        [Tooltip("Whether to auto-close the modal")]
        [SerializeField] private bool autoClose = true;

        public event Action onClose;

        public const string MODAL_NAME = "SimpleNotification";

        private float _closeTimer = 0f;

        public override void Open(bool showNoButton = true)
        {
            base.Open(showNoButton);
            
            // Reset timer
            _closeTimer = 0f;
            
            // Start auto-close timer if enabled
            if (autoClose)
            {
                _closeTimer = autoCloseDelay;
            }
        }

        private void Update()
        {
            if (autoClose && _closeTimer > 0f)
            {
                _closeTimer -= Time.deltaTime;
                
                if (_closeTimer <= 0f)
                {
                    Close();
                }
            }
        }

        public override void Close()
        {
            base.Close();
            onClose?.Invoke();
        }

        /// <summary>
        /// Sets the icon sprite for the modal.
        /// </summary>
        /// <param name="sprite">The sprite to display as icon</param>
        public void SetIcon(Sprite sprite)
        {
            if (iconImage != null)
            {
                iconImage.sprite = sprite;
            }
        }

        /// <summary>
        /// Sets the message text for the modal.
        /// </summary>
        /// <param name="message">The text message to display</param>
        public void SetMessage(string message)
        {
            if (messageText != null)
            {
                messageText.text = message;
                LayoutRebuilder.ForceRebuildLayoutImmediate(messageText.rectTransform);
            }
        }

        /// <summary>
        /// Sets both the icon and message at once.
        /// </summary>
        /// <param name="sprite">The sprite to display as icon</param>
        /// <param name="message">The text message to display</param>
        public void SetContent(Sprite sprite, string message)
        {
            SetIcon(sprite);
            SetMessage(message);
        }

        /// <summary>
        /// Sets the auto-close delay.
        /// </summary>
        /// <param name="delay">Time in seconds before auto-close</param>
        public void SetAutoCloseDelay(float delay)
        {
            autoCloseDelay = Mathf.Max(0f, delay);
        }

        /// <summary>
        /// Enables or disables auto-close.
        /// </summary>
        /// <param name="enabled">Whether to enable auto-close</param>
        public void SetAutoClose(bool enabled)
        {
            autoClose = enabled;
        }

        /// <summary>
        /// Manually triggers the close timer.
        /// </summary>
        public void StartCloseTimer()
        {
            if (autoClose)
            {
                _closeTimer = autoCloseDelay;
            }
        }

        /// <summary>
        /// Resets the close timer.
        /// </summary>
        public void ResetCloseTimer()
        {
            _closeTimer = autoCloseDelay;
        }
    }
} 