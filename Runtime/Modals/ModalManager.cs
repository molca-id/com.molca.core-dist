using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Molca.Utils;

namespace Molca.Modals
{
    [System.Serializable]
    public struct ModalEntry
    {
        public string key;
        public BaseModal prefab;
    }

    public class ModalManager : RuntimeSubsystem
    {
        public enum MessageType
        {
            Default = 0,
            Warning = 1,
            Error = 2
        }

        [System.Serializable]
        private struct MessageColors
        {
            public Color defaultColor;
            public Color warningColor;
            public Color errorColor;
        }

        [Header("General")]
        [SerializeField, Tooltip("Subscribe to Logger onLogs event, displaying it as messages.")]
        private bool hookLogger;
        [SerializeField] private MessageColors messageColors;

        #region MESSAGES PROPERTIES
        [Header("Messages")]
        [SerializeField] private RectTransform msgRoot;
        [SerializeField] private ModalMessage msgPrefab;

        private ObjectPool<ModalMessage> _messagePool;
        private readonly Queue<(string message, MessageType msgType, float duration)> _pendingMessages = new();
        private Coroutine _processMessagesCoroutine;
        private int _messageCount;
        #endregion

        #region CONFIRMATION PROPERTIES
        [Header("Confirmation")]
        [SerializeField] private GameObject modalPanel;
        [SerializeField] private Transform modalPanelContent;
        [SerializeField] private ModalConfirmation regularConfirmationPrefab;
        [SerializeField] private ModalConfirmation advancedConfirmationPrefab;
        #endregion

        #region SDK MODAL PROPERTIES
        [Header("SDK Modals")]
        [SerializeField] private ModalEntry[] modalPrefabs;
        private Dictionary<string, BaseModal> _modalPrefabLookup;
        #endregion

        #region LOADING PROPERTIES
        [Header("Loading")]
        [SerializeField] private RectTransform loadingRoot;
        [SerializeField] private ModalLoading loadingPrefab;
        [SerializeField] private GameObject fullScreenLoadingPanel;
        [SerializeField] private TextMeshProUGUI fullScreenLoadingMsg;

        private ObjectPool<ModalLoading> _loadingPool;
        private Dictionary<string, ModalLoading> _activeLoadings;
        private HashSet<BaseModal> _activeModals;
        #endregion

        // Logger event subscriptions stored so we can unsubscribe in Teardown.
        private Action<string> _onLogInfo;
        private Action<string> _onLogWarning;
        private Action<string> _onLogError;

        private bool IsValidAction(string msg) => isActive && msg.Length > 0;

        /// <summary>
        /// Replaces characters unsupported by the TMP font (surrogate pairs / emoji) with <c>[?]</c>
        /// to prevent TMP missing-glyph warnings from re-entering <see cref="LogHandler"/>.
        /// </summary>
        private static string SanitizeForTMP(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            System.Text.StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    sb ??= new System.Text.StringBuilder(text, 0, i, text.Length);
                    sb.Append("[?]");
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        i++;
                }
                else if (char.IsLowSurrogate(text[i]))
                {
                    sb ??= new System.Text.StringBuilder(text, 0, i, text.Length);
                    sb.Append("[?]");
                }
                else
                {
                    sb?.Append(text[i]);
                }
            }

            return sb?.ToString() ?? text;
        }

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            InitializeLogger();
            InitializePools();
            InitializeModalLookup();
            finishCallback?.Invoke(this);

            ShowFullScreenLoadingAsync();
        }

        private async void ShowFullScreenLoadingAsync()
        {
            ShowFullScreenLoading("Preparing system...");
            await RuntimeManager.WaitForInitialization();
            if (this != null)
                HideFullScreenLoading();
        }

        private void InitializeLogger()
        {
            if (!hookLogger) return;

            var logger = RuntimeManager.GetSubsystem<LogManager>();
            if (logger == null) return;

            _onLogInfo    = msg => AddMessage(msg, MessageType.Default);
            _onLogWarning = msg => AddMessage(msg, MessageType.Warning);
            _onLogError   = msg => AddMessage(msg, MessageType.Error);

            logger.onLogInfo    += _onLogInfo;
            logger.onLogWarning += _onLogWarning;
            logger.onLogError   += _onLogError;
        }

        private void InitializePools()
        {
            _messagePool   = new ObjectPool<ModalMessage>(msgPrefab, 10, msgRoot);
            _loadingPool   = new ObjectPool<ModalLoading>(loadingPrefab, 1, loadingRoot);
            _activeLoadings = new Dictionary<string, ModalLoading>();
            _activeModals   = new HashSet<BaseModal>();
        }

        private void InitializeModalLookup()
        {
            _modalPrefabLookup = new Dictionary<string, BaseModal>();

            if (modalPrefabs == null) return;

            foreach (var entry in modalPrefabs)
            {
                if (string.IsNullOrEmpty(entry.key) || entry.prefab == null) continue;

                if (_modalPrefabLookup.ContainsKey(entry.key))
                    Debug.LogWarning($"Duplicate modal key '{entry.key}' in ModalManager. Skipping duplicate.");
                else
                    _modalPrefabLookup[entry.key] = entry.prefab;
            }
        }

        public override void Teardown()
        {
            if (hookLogger)
            {
                var logger = RuntimeManager.GetSubsystem<LogManager>();
                if (logger != null)
                {
                    logger.onLogInfo    -= _onLogInfo;
                    logger.onLogWarning -= _onLogWarning;
                    logger.onLogError   -= _onLogError;
                }
            }

            base.Teardown();
        }

        #region MESSAGES
        /// <summary>
        /// Queues a toast message. Work is deferred one frame to avoid driving UI changes
        /// from inside a canvas graphic-rebuild loop.
        /// </summary>
        public void AddMessage(string message, MessageType msgType = MessageType.Default, float duration = 10f)
        {
            if (!IsValidAction(message)) return;

            _pendingMessages.Enqueue((SanitizeForTMP(message), msgType, duration));
            EnsureMessageQueueProcessing();
        }

        private void EnsureMessageQueueProcessing()
        {
            if (_processMessagesCoroutine != null) return;
            _processMessagesCoroutine = StartCoroutine(ProcessMessageQueueNextFrame());
        }

        private IEnumerator ProcessMessageQueueNextFrame()
        {
            yield return null;

            try
            {
                while (_pendingMessages.Count > 0)
                {
                    var (message, msgType, duration) = _pendingMessages.Dequeue();
                    if (!IsValidAction(message)) continue;

                    var msg = _messagePool.Get();
                    if (msg == null)
                    {
                        _messagePool.IncreaseSize(2);
                        msg = _messagePool.Get();
                    }

                    if (msg != null)
                    {
                        msg.transform.SetSiblingIndex(0);
                        msg.Initialize(message, GetMessageColor(msgType), _messagePool, duration, _messageCount++ % 2 == 0);
                    }
                }
            }
            finally
            {
                _processMessagesCoroutine = null;
            }
        }

        private Color GetMessageColor(MessageType msgType) => msgType switch
        {
            MessageType.Default => messageColors.defaultColor,
            MessageType.Warning => messageColors.warningColor,
            MessageType.Error   => messageColors.errorColor,
            _                   => messageColors.defaultColor
        };
        #endregion

        #region LOADING
        /// <summary>Adds a titled loading indicator to the loading root.</summary>
        public ModalLoading AddLoading(string title)
        {
            if (!IsValidAction(title)) return null;

            if (_activeLoadings.ContainsKey(title))
            {
                Debug.LogWarning($"Failed to add loading modal, title '{title}' already exists.");
                return _activeLoadings[title];
            }

            var loading = _loadingPool.Get();
            if (loading == null)
            {
                _loadingPool.IncreaseSize(1);
                loading = _loadingPool.Get();
            }

            if (loading == null)
            {
                Debug.LogError($"ModalManager: loading pool exhausted, cannot show '{title}'.");
                return null;
            }

            loading.transform.SetSiblingIndex(0);
            loading.Initialize(title);
            _activeLoadings.Add(title, loading);
            return loading;
        }

        /// <summary>Returns a loading indicator to the pool by title.</summary>
        public void RemoveLoading(string title)
        {
            if (!_activeLoadings.TryGetValue(title, out var loading))
            {
                Debug.LogWarning($"Failed to remove loading modal, title '{title}' doesn't exist.");
                return;
            }

            _loadingPool.Return(loading);
            _activeLoadings.Remove(title);
        }

        /// <summary>Shows the full-screen loading overlay with <paramref name="message"/>.</summary>
        public void ShowFullScreenLoading(string message)
        {
            fullScreenLoadingMsg.SetText(message);
            if (!fullScreenLoadingPanel.activeSelf)
                fullScreenLoadingPanel.SetActive(true);
        }

        /// <summary>Hides the full-screen loading overlay.</summary>
        public void HideFullScreenLoading()
        {
            fullScreenLoadingPanel.SetActive(false);
        }
        #endregion

        #region MODALS
        /// <summary>
        /// Called by <see cref="BaseModal.Close"/> to untrack a modal.
        /// Does not call <see cref="BaseModal.Close"/> — the caller owns that.
        /// </summary>
        public void CloseModal(BaseModal modal)
        {
            _activeModals.Remove(modal);

            if (_activeModals.Count == 0)
                modalPanel.SetActive(false);
        }

        /// <summary>Closes and destroys all currently active modals.</summary>
        public void CloseAllModals()
        {
            var modals = new List<BaseModal>(_activeModals);
            foreach (var modal in modals)
                modal.Close();

            // Safety clear in case any Close() override forgot to call base.
            _activeModals.Clear();
            modalPanel.SetActive(false);
        }

        /// <summary>Instantiates <paramref name="modalPrefab"/>, opens it, and tracks it.</summary>
        public BaseModal ShowModal(BaseModal modalPrefab, bool defaultParent = true)
        {
            modalPanel.SetActive(true);
            var modal = Instantiate(modalPrefab, defaultParent ? modalPanelContent : null);
            modal.Open();
            _activeModals.Add(modal);
            return modal;
        }

        /// <summary>Shows a modal registered under <paramref name="modalKey"/>.</summary>
        public BaseModal ShowModal(string modalKey)
        {
            if (!_modalPrefabLookup.TryGetValue(modalKey, out var prefab))
            {
                Debug.LogError($"Modal with key '{modalKey}' not found in ModalManager.");
                return null;
            }

            return ShowModal(prefab);
        }

        /// <summary>Shows and returns a typed modal registered under <paramref name="modalKey"/>.</summary>
        public T ShowModal<T>(string modalKey) where T : BaseModal => ShowModal(modalKey) as T;

        /// <summary>Shows the regular (title + message) confirmation dialog.</summary>
        public ModalConfirmation ShowRegularConfirmation(string title, string message, string yesText, string noText, Action onYes, Action onNo, bool showNoButton = true)
        {
            var modal = (ModalConfirmation)ShowModal(regularConfirmationPrefab);
            modal.Setup(title, string.Empty, message, string.Empty, yesText, noText, onYes, onNo, showNoButton);
            return modal;
        }

        /// <summary>Shows the advanced (title + subtitle + message + details) confirmation dialog.</summary>
        public ModalConfirmation ShowAdvancedConfirmation(string title, string subtitle, string mainMessage, string details, string yesText, string noText, Action onYes, Action onNo, bool showNoButton = true)
        {
            var modal = (ModalConfirmation)ShowModal(advancedConfirmationPrefab);
            modal.Setup(title, subtitle, mainMessage, details, yesText, noText, onYes, onNo, showNoButton);
            return modal;
        }
        #endregion
    }
}
