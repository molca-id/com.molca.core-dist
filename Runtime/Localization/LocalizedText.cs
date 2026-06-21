using UnityEngine;
using TMPro;
using UnityEngine.Localization;
using UnityEngine.UI;

namespace Molca.Localization
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    [DisallowMultipleComponent]
    public class LocalizedText : MonoBehaviour
    {
        [SerializeField] protected LocalizedTextStyleInfo styleInfo;
        [SerializeField] protected LocalizedString localizedString;

        [Inject] private LocalizationManager _locMgr;

        protected TextMeshProUGUI tmpText;
        private bool _isInitialized;

        protected string Text
        {
            get => (tmpText ??= GetComponent<TextMeshProUGUI>()).text;
            set => (tmpText ??= GetComponent<TextMeshProUGUI>()).SetText(value);
        }

        protected virtual async void OnEnable()
        {
            await InitializeAsync();

            // Disabled (or destroyed) while initializing: OnDisable already ran with
            // _isInitialized still false, so it did not unsubscribe — subscribing now
            // would orphan the handler. A later re-enable resubscribes correctly.
            if (this == null || !isActiveAndEnabled) return;

            if (localizedString != null)
            {
                // -=/+= keeps this idempotent when a disable/enable cycle leaves a
                // previous activation's subscription behind.
                localizedString.StringChanged -= OnRefresh;
                localizedString.StringChanged += OnRefresh;
                UpdateLocalizedText();
            }
        }

        protected virtual void OnDisable()
        {
            if (_isInitialized)
            {
                _locMgr?.UnregisterText(this);
                if (localizedString != null)
                    localizedString.StringChanged -= OnRefresh;
            }
        }

        private async Awaitable InitializeAsync()
        {
            if (_isInitialized) return;

            tmpText = GetComponent<TextMeshProUGUI>();

            await RuntimeManager.WaitForInitialization();
            if (this == null) return;

            // _locMgr is populated by [Inject] during WaitForInitialization.
            // Fall back to GetSubsystem for objects spawned after bootstrap.
            if (_locMgr == null)
                _locMgr = RuntimeManager.GetSubsystem<LocalizationManager>();

            _locMgr?.RegisterText(this);
            _isInitialized = true;
        }

        protected virtual void OnValidate()
        {
            ApplyStyle();
        }

        /// <summary>Called by <see cref="LocalizationManager"/> when the active language changes.</summary>
        /// <param name="lang">The new BCP-47 language code.</param>
        public virtual void OnRefresh(string lang)
        {
            UpdateLocalizedText();
        }

        /// <summary>Applies <paramref name="newStyle"/> and refreshes the text component.</summary>
        public virtual void SetStyle(LocalizedTextStyleInfo newStyle)
        {
            styleInfo = newStyle;
            ApplyStyle();
        }

        /// <summary>Applies font, style, and size settings from <see cref="styleInfo"/>.</summary>
        public virtual void ApplyStyle()
        {
            if (!styleInfo) return;

            var text = tmpText ? tmpText : GetComponent<TextMeshProUGUI>();
            if (!text) return;

            text.font = styleInfo.Font;
            text.fontStyle = styleInfo.Style;
            text.fontSize = styleInfo.PreferredSize;
            text.fontSizeMin = styleInfo.MinSize;
            text.fontSizeMax = styleInfo.MaxSize;
        }

        /// <summary>
        /// Replaces the current <see cref="LocalizedString"/> and subscribes to change events.
        /// </summary>
        /// <param name="newLocalizedString">The new localized string to display.</param>
        public virtual void SetLocalizedString(LocalizedString newLocalizedString)
        {
            Text = string.Empty;

            if (localizedString != null)
                localizedString.StringChanged -= OnRefresh;

            localizedString = newLocalizedString;

            // Guard: only subscribe if already initialized to avoid a double-subscription
            // when OnEnable also subscribes after InitializeAsync completes.
            if (localizedString != null && _isInitialized)
                localizedString.StringChanged += OnRefresh;
        }

        /// <summary>Returns the current <see cref="LocalizedString"/> assigned to this component.</summary>
        public LocalizedString GetLocalizedString() => localizedString;

        /// <summary>Fetches and applies the current translation from the localization system.</summary>
        protected virtual async void UpdateLocalizedText()
        {
            if (localizedString == null || localizedString.IsEmpty)
                return;

            try
            {
                var handle = localizedString.GetLocalizedStringAsync();
                await RuntimeManager.AwaitHandle(handle);

                if (this == null || tmpText == null) return;

                Text = handle.Result;
                RebuildLayout();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to get localized string: {e.Message}");
            }
        }

        private async void RebuildLayout()
        {
            if (this == null || tmpText == null) return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(tmpText.rectTransform);

            if (tmpText.rectTransform.parent != null)
            {
                await Awaitable.NextFrameAsync();

                if (this == null || tmpText == null) return;
                LayoutRebuilder.ForceRebuildLayoutImmediate(tmpText.rectTransform.parent as RectTransform);
            }
        }
    }
}
