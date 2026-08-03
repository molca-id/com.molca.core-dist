using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

namespace Molca.Localization
{
    /// <summary>Displays a Unity <see cref="LocalizedString"/> in a TextMeshPro component.</summary>
    [RequireComponent(typeof(TextMeshProUGUI))]
    [DisallowMultipleComponent]
    public class LocalizedText : MonoBehaviour
    {
        [SerializeField] protected LocalizedTextStyleInfo styleInfo;
        [SerializeField] protected LocalizedString localizedString;

        [Tooltip("The reference is supplied at runtime through SetLocalizedString. An empty slot is " +
                 "then an authoring decision rather than an omission, and the assignment is asserted " +
                 "at the end of each enabled lifetime.")]
        [SerializeField] protected bool runtimeAssigned;

        [Inject] private LocalizationManager _locMgr;

        protected TextMeshProUGUI tmpText;
        private bool _isInitialized;
        private bool _stringChangedSubscribed;
        private int _refreshGeneration;
        private bool _runtimeAssignmentObserved;
        private bool _runtimeAssignmentReported;

        /// <summary>Gets or sets the displayed TMP text.</summary>
        protected string Text
        {
            get => (tmpText ??= GetComponent<TextMeshProUGUI>()).text;
            set => (tmpText ??= GetComponent<TextMeshProUGUI>()).SetText(value);
        }

        /// <summary>Initializes and activates localization subscriptions.</summary>
        protected virtual async void OnEnable() // doctor:ignore async-void is a Unity lifecycle entry point and owns exceptions
        {
            try
            {
                await InitializeAsync();
                if (this == null || !isActiveAndEnabled)
                    return;

                _locMgr?.RegisterText(this);
                SubscribeToLocalizedString();
                UpdateLocalizedText();
            }
            catch (System.OperationCanceledException)
            {
                // The component lifetime ended while enabling.
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"Failed to initialize LocalizedText: {exception}");
            }
        }

        /// <summary>Deactivates subscriptions and invalidates pending refreshes.</summary>
        protected virtual void OnDisable()
        {
            _refreshGeneration++;
            _locMgr?.UnregisterText(this);
            UnsubscribeFromLocalizedString();
            ReportMissingRuntimeAssignment();
        }

        /// <summary>
        /// Reports a <see cref="RuntimeAssigned"/> label that nothing ever supplied a reference to.
        /// </summary>
        /// <remarks>
        /// <para>Whether some caller will invoke <see cref="SetLocalizedString"/> is not a fact about
        /// serialized data, so no audit can decide it — this is the half of the contract only the running
        /// application can see, and it is what makes the flag an assertion rather than a suppression.</para>
        /// <para>Checked at the end of the enabled lifetime rather than on a timer: a label that
        /// legitimately waits on a fetch is never accused mid-wait, only one that stayed blank for its
        /// whole visible life. Silent before <see cref="InitializeAsync"/> completes, because an
        /// enable/disable during bootstrap says nothing about the caller, and silent once reported, so a
        /// label cycled every frame cannot flood the log.</para>
        /// </remarks>
        private void ReportMissingRuntimeAssignment()
        {
            if (!runtimeAssigned || _runtimeAssignmentObserved || _runtimeAssignmentReported ||
                !_isInitialized)
                return;

            // A subclass may write the protected field directly instead of going through the setter;
            // the contract is about the reference arriving, not about which path delivered it.
            if (localizedString != null && !localizedString.IsEmpty)
                return;

            _runtimeAssignmentReported = true;
            Debug.LogWarning(
                $"LocalizedText '{name}' is marked Runtime Assigned, but nothing called " +
                "SetLocalizedString while it was enabled — it showed no text at all. Assign it from " +
                "code, or clear Runtime Assigned and author a LocalizedString.",
                this);
        }

        private async Awaitable InitializeAsync()
        {
            if (_isInitialized)
                return;

            tmpText = GetComponent<TextMeshProUGUI>();
            await RuntimeManager.WaitForInitialization();
            if (this == null)
                return;

            if (_locMgr == null)
                _locMgr = RuntimeManager.GetSubsystem<LocalizationManager>();
            _isInitialized = true;
        }

        /// <summary>Applies configured style changes in the editor.</summary>
        protected virtual void OnValidate()
        {
            ApplyStyle();
        }

        /// <summary>Called by <see cref="LocalizationManager"/> when the active language changes.</summary>
        /// <param name="lang">The new BCP-47 language code.</param>
        public virtual void OnRefresh(string lang)
        {
            ApplyLocalePresentation(lang);
            if (LocalizationManager.TryResolveOverlay(
                    localizedString,
                    lang,
                    out var overlayValue,
                    out _))
            {
                _refreshGeneration++;
                Text = overlayValue;
                RebuildLayout();
                return;
            }
            // Unity's StringChanged callback is the primary channel for a valid LocalizedString.
            if (_stringChangedSubscribed)
                return;
            UpdateLocalizedText();
        }

        /// <summary>Applies a text style and refreshes the text component.</summary>
        /// <param name="newStyle">Style to apply.</param>
        public virtual void SetStyle(LocalizedTextStyleInfo newStyle)
        {
            styleInfo = newStyle;
            ApplyStyle();
        }

        /// <summary>Applies font, style, and size settings from <see cref="styleInfo"/>.</summary>
        public virtual void ApplyStyle()
        {
            var text = tmpText ? tmpText : GetComponent<TextMeshProUGUI>();
            if (!text)
                return;

            if (styleInfo)
            {
                text.font = styleInfo.Font;
                text.fontStyle = styleInfo.Style;
                text.fontSize = styleInfo.PreferredSize;
                text.fontSizeMin = styleInfo.MinSize;
                text.fontSizeMax = styleInfo.MaxSize;
            }

            ApplyLocalePresentation(LocalizationManager.CurrentLanguage);
        }

        /// <summary>Applies the font and writing direction authored for a locale.</summary>
        public virtual void ApplyLocalePresentation(string localeCode) =>
            ApplyPresentationProfile(LocalizationManager.GetPresentationProfile(localeCode));

        /// <summary>Applies an explicit profile. Public for previews and deterministic fixtures.</summary>
        public virtual void ApplyPresentationProfile(LocalePresentationProfile profile)
        {
            var text = tmpText ? tmpText : GetComponent<TextMeshProUGUI>();
            if (!text)
                return;

            var styleFont = styleInfo ? styleInfo.Font : text.font;
            if (profile != null)
                text.font = profile.ResolvePrimaryFont(styleFont);
            text.isRightToLeftText = profile?.IsRightToLeft == true;
        }

        /// <summary>Replaces the current localized reference and immediately refreshes it.</summary>
        /// <param name="newLocalizedString">New localized string reference.</param>
        public virtual void SetLocalizedString(LocalizedString newLocalizedString)
        {
            _refreshGeneration++;
            UnsubscribeFromLocalizedString();
            localizedString = newLocalizedString;
            _runtimeAssignmentObserved |= newLocalizedString != null && !newLocalizedString.IsEmpty;
            Text = string.Empty;

            if (_isInitialized && isActiveAndEnabled)
            {
                SubscribeToLocalizedString();
                UpdateLocalizedText();
            }
        }

        /// <summary>Returns the localized reference assigned to this component.</summary>
        /// <returns>The current localized string reference.</returns>
        public LocalizedString GetLocalizedString() => localizedString;

        /// <summary>
        /// Whether the reference is supplied at runtime through <see cref="SetLocalizedString"/> rather
        /// than authored in the Inspector.
        /// </summary>
        /// <remarks>
        /// Declares intent about an empty slot, which serialized data cannot express on its own: the
        /// localization audit reads it to tell a deliberate blank from a forgotten one, and the component
        /// asserts at runtime that the promised assignment actually arrives. It is not a suppression
        /// switch — setting it on a label that also has an authored reference is itself an audit finding.
        /// </remarks>
        public bool RuntimeAssigned => runtimeAssigned;

        /// <summary>Returns the currently rendered text for non-mutating diagnostics.</summary>
        public string GetRenderedText() =>
            (tmpText ? tmpText : GetComponent<TextMeshProUGUI>())?.text ?? string.Empty;

        /// <summary>Measures candidate text against this component without applying it.</summary>
        public bool WouldOverflow(string candidate, out Vector2 available, out Vector2 preferred)
        {
            var text = tmpText ? tmpText : GetComponent<TextMeshProUGUI>();
            if (!text)
            {
                available = default;
                preferred = default;
                return false;
            }

            available = text.rectTransform.rect.size;
            preferred = text.GetPreferredValues(
                candidate ?? string.Empty,
                Mathf.Max(0f, available.x),
                Mathf.Infinity);
            return preferred.x > available.x + 0.5f ||
                   preferred.y > available.y + 0.5f;
        }

        /// <summary>Fetches and applies the current translation.</summary>
        protected virtual async void UpdateLocalizedText() // doctor:ignore async-void is a protected refresh entry point and owns exceptions
        {
            var generation = ++_refreshGeneration;
            if (localizedString == null || localizedString.IsEmpty)
            {
                if (this != null && generation == _refreshGeneration)
                    Text = string.Empty;
                return;
            }

            if (LocalizationManager.TryResolveOverlay(
                    localizedString,
                    LocalizationManager.CurrentLanguage,
                    out var overlayValue,
                    out _))
            {
                if (this != null && generation == _refreshGeneration)
                {
                    Text = overlayValue;
                    RebuildLayout();
                }
                return;
            }

            try
            {
                var handle = localizedString.GetLocalizedStringAsync();
                await RuntimeManager.AwaitHandle(handle);
                if (this == null || tmpText == null || !isActiveAndEnabled ||
                    generation != _refreshGeneration)
                    return;

                Text = handle.Result ?? string.Empty;
                RebuildLayout();
            }
            catch (System.OperationCanceledException)
            {
                // A newer refresh or component teardown superseded this result.
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"Failed to get localized string: {exception.Message}");
            }
        }

        private void SubscribeToLocalizedString()
        {
            if (localizedString == null || localizedString.IsEmpty)
                return;

            localizedString.StringChanged -= OnLocalizedStringChanged;
            localizedString.StringChanged += OnLocalizedStringChanged;
            _stringChangedSubscribed = true;
        }

        private void UnsubscribeFromLocalizedString()
        {
            if (localizedString != null)
                localizedString.StringChanged -= OnLocalizedStringChanged;
            _stringChangedSubscribed = false;
        }

        private void OnLocalizedStringChanged(string value)
        {
            if (this == null || !isActiveAndEnabled)
                return;

            _refreshGeneration++;
            ApplyLocalePresentation(LocalizationManager.CurrentLanguage);
            Text = LocalizationManager.TryResolveOverlay(
                localizedString,
                LocalizationManager.CurrentLanguage,
                out var overlayValue,
                out _)
                ? overlayValue
                : value ?? string.Empty;
            RebuildLayout();
        }

        private async void RebuildLayout() // doctor:ignore async-void is a UI callback helper and owns no faulting work
        {
            if (this == null || tmpText == null)
                return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(tmpText.rectTransform);
            if (tmpText.rectTransform.parent == null)
                return;

            await Awaitable.NextFrameAsync();
            if (this == null || tmpText == null || !isActiveAndEnabled)
                return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(tmpText.rectTransform.parent as RectTransform);
        }
    }
}
