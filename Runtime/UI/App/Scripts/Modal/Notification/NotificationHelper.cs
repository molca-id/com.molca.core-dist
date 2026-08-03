using UnityEngine;
using Molca.Modals;
using Molca.Localization;
using UnityEngine.Events;
using UnityEngine.Serialization;
using Molca;

namespace Molca.App
{
    /// <summary>
    /// Simple helper for creating and managing SimpleNotificationModal instances.
    /// </summary>
    public class NotificationHelper : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("defaultAutoClose")] private bool autoClose = true;

        [Tooltip("Default auto-close delay for notifications")]
        [SerializeField, FormerlySerializedAs("defaultAutoCloseDelay")] private float autoCloseDelay = 3f;
        
        [Tooltip("Default icon for notifications")]
        [SerializeField, FormerlySerializedAs("defaultIcon")] private Sprite icon;

        [Tooltip("Default message for notifications")]
        [SerializeField, FormerlySerializedAs("defaultMessage")] private LocalizedValue message;

        public UnityEvent onNotificationCreated;
        public UnityEvent onNotificationClosed;

        private NotificationModal _currentModal;
        private LocalizedValueBinding _messageBinding;

        private async void Start()
        {
            if (message == null)
                return;
            _messageBinding = message.CreateBinding();
            _messageBinding.ValueChanged += OnDefaultMessageChanged;
            try
            {
                await _messageBinding.RefreshAsync();
            }
            catch (System.Exception exception)
            {
                if (this != null)
                    Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (_messageBinding != null)
            {
                _messageBinding.ValueChanged -= OnDefaultMessageChanged;
                _messageBinding.Dispose();
                _messageBinding = null;
            }
            CloseAllNotifications();
        }

        public void CreateDefaultNotification()
        {
            CreateNotification(
                _messageBinding?.LastSuccessfulResult ?? message?.String ?? string.Empty,
                icon,
                autoCloseDelay,
                autoClose);
        }

        private void OnDefaultMessageChanged(string value)
        {
            if (_currentModal != null)
                _currentModal.SetContent(icon, value);
        }

        /// <summary>
        /// Creates and shows a notification with the given message.
        /// </summary>
        /// <param name="message">The message to display</param>
        /// <param name="icon">Optional icon sprite (uses default if null)</param>
        /// <param name="autoCloseDelay">Optional auto-close delay (uses default if 0)</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateNotification(string message, Sprite icon = null, float autoCloseDelay = 0f, bool autoClose = true)
        {
            var modalManager = RuntimeManager.GetService<ModalManager>();
            if (modalManager == null)
            {
                Debug.LogError("ModalManager not found");
                return null;
            }

            // Close any existing modal
            CloseCurrentNotification();

            // Create new modal
            _currentModal = modalManager.ShowModal(NotificationModal.MODAL_NAME) as NotificationModal;
            
            if (_currentModal != null)
            {
                // Set content
                Sprite iconToUse = icon != null ? icon : this.icon;
                _currentModal.SetContent(iconToUse, message);
                
                _currentModal.SetAutoClose(autoClose);
                _currentModal.SetAutoCloseDelay(autoCloseDelay);
                _currentModal.onClose += OnNotificationClosed;
                
                Debug.Log($"Notification created: {message}");
                onNotificationCreated.Invoke();
            }
            else
            {
                Debug.LogError("Could not create SimpleNotificationModal");
            }

            return _currentModal;
        }

        private void OnNotificationClosed()
        {
            onNotificationClosed?.Invoke();
        }

        /// <summary>
        /// Creates and shows a success notification.
        /// </summary>
        /// <param name="message">The success message</param>
        /// <param name="autoCloseDelay">Optional auto-close delay</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateSuccessNotification(string message, float autoCloseDelay = 0f)
        {
            // You can assign a success icon in the inspector or use default
            return CreateNotification(message, icon, autoCloseDelay);
        }

        /// <summary>
        /// Creates and shows a warning notification.
        /// </summary>
        /// <param name="message">The warning message</param>
        /// <param name="autoCloseDelay">Optional auto-close delay</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateWarningNotification(string message, float autoCloseDelay = 0f)
        {
            // You can assign a warning icon in the inspector or use default
            return CreateNotification(message, icon, autoCloseDelay);
        }

        /// <summary>
        /// Creates and shows an error notification.
        /// </summary>
        /// <param name="message">The error message</param>
        /// <param name="autoCloseDelay">Optional auto-close delay</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateErrorNotification(string message, float autoCloseDelay = 0f)
        {
            // You can assign an error icon in the inspector or use default
            return CreateNotification(message, icon, autoCloseDelay);
        }

        /// <summary>
        /// Creates and shows an info notification.
        /// </summary>
        /// <param name="message">The info message</param>
        /// <param name="autoCloseDelay">Optional auto-close delay</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateInfoNotification(string message, float autoCloseDelay = 0f)
        {
            // You can assign an info icon in the inspector or use default
            return CreateNotification(message, icon, autoCloseDelay);
        }

        /// <summary>
        /// Closes the current notification if one is active.
        /// </summary>
        public void CloseCurrentNotification()
        {
            if (_currentModal != null)
            {
                _currentModal.Close();
                _currentModal = null;
            }
        }

        /// <summary>
        /// Closes all active notifications.
        /// </summary>
        public void CloseAllNotifications()
        {
            CloseCurrentNotification();
            
            // Find and close any other SimpleNotificationModal instances
            var allModals = FindObjectsByType<NotificationModal>(FindObjectsSortMode.None);
            foreach (var modal in allModals)
            {
                if (modal != null && modal != _currentModal)
                {
                    modal.Close();
                }
            }
        }

        /// <summary>
        /// Checks if there's currently an active notification.
        /// </summary>
        /// <returns>True if a notification is currently active</returns>
        public bool HasActiveNotification()
        {
            return _currentModal != null && _currentModal.gameObject.activeInHierarchy;
        }

        /// <summary>
        /// Gets the current active notification.
        /// </summary>
        /// <returns>The current modal instance, or null if none active</returns>
        public NotificationModal GetCurrentNotification()
        {
            return _currentModal;
        }

        /// <summary>
        /// Sets the default auto-close delay for future notifications.
        /// </summary>
        /// <param name="delay">The delay in seconds</param>
        public void SetDefaultAutoCloseDelay(float delay)
        {
            autoCloseDelay = Mathf.Max(0f, delay);
        }

        /// <summary>
        /// Sets the default icon for future notifications.
        /// </summary>
        /// <param name="icon">The default icon sprite</param>
        public void SetDefaultIcon(Sprite icon)
        {
            this.icon = icon;
        }

        /// <summary>
        /// Creates a notification that doesn't auto-close.
        /// </summary>
        /// <param name="message">The message to display</param>
        /// <param name="icon">Optional icon sprite</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreatePersistentNotification(string message, Sprite icon = null)
        {
            var modal = CreateNotification(message, icon, 0f);
            if (modal != null)
            {
                modal.SetAutoClose(false);
            }
            return modal;
        }

        /// <summary>
        /// Creates a notification with custom content and settings.
        /// </summary>
        /// <param name="message">The message to display</param>
        /// <param name="icon">The icon sprite</param>
        /// <param name="autoClose">Whether to auto-close</param>
        /// <param name="autoCloseDelay">Auto-close delay (ignored if autoClose is false)</param>
        /// <returns>The created modal instance</returns>
        public NotificationModal CreateCustomNotification(string message, Sprite icon, bool autoClose, float autoCloseDelay = 0f)
        {
            var modal = CreateNotification(message, icon, autoClose ? autoCloseDelay : 0f);
            if (modal != null)
            {
                modal.SetAutoClose(autoClose);
            }
            return modal;
        }
    }
}
