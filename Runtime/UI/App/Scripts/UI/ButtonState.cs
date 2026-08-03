using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using Molca;
using Molca.Audio;
using Molca.ColorID;

namespace Molca.App.UI
{
    [RequireComponent(typeof(Button))]
    public class ButtonState : MonoBehaviour
    {
        public bool exludeFromGroup;
        [SerializeField]
        private bool _isOn;
        [SerializeField]
        private TextMeshProUGUI labelText;
        [SerializeField]
        private Image backgroundImage;

        [SerializeField]
        private bool interpolateColor;
        [SerializeField]
        private ColorTokenReference onColor;
        [SerializeField]
        private ColorTokenReference offColor;

        [SerializeField]
        private bool toggleBackground;
        [SerializeField]
        private Sprite onSprite;
        [SerializeField]
        private Sprite offSprite;

        private const string SFX_COLLECTION_NAME = "Base";
        private const string SFX_CLICK_NAME = "UI Click";

        public UnityEvent<bool> onStateChanged;
        public UnityEvent onStateOn;
        public UnityEvent onStateOff;

        public Action<ButtonState> onClicked;

        // Cached so OnDestroy unsubscribes from the same instance even if the service registry is already
        // gone during teardown.
        private IColorThemeService _themeService;

        public bool isOn
        {
            get => _isOn;
            set
            {
                //Debug.Log($"{gameObject.name} isOn: {value}");
                _isOn = value;
                ApplyStateColor();
                if (toggleBackground && onSprite)
                    backgroundImage.sprite = isOn ? onSprite : offSprite;
                InvokeStateEvent();
            }
        }

        private async void Start()
        {
            try
            {
                //Debug.Log($"{gameObject.name} => Waiting RM Ready.");
                await RuntimeManager.WaitForInitialization();
                //yield return new WaitUntil(RuntimeManager.IsReady); BUG: Home's ListView Toggle don't go pass this line

                // If destroyed during the await, OnDestroy has already run — subscribing now would leak a
                // handler on a dead object.
                if (this == null) return;

                _themeService = RuntimeManager.GetService<IColorThemeService>();
                if (_themeService != null)
                    _themeService.ThemeChanged += OnThemeChanged;

                isOn = isOn;
                GetComponent<Button>().onClick.AddListener(OnClicked);
                //Debug.Log($"{gameObject.name} => RM Ready.");
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnDestroy()
        {
            if (_themeService != null) _themeService.ThemeChanged -= OnThemeChanged;
        }

        // The state has not changed, only the palette it resolves through — reapply without re-running the
        // sprite swap or re-raising the state events.
        private void OnThemeChanged(ColorThemeChanged change) => ApplyStateColor();

        /// <summary>
        /// Resolves the token for the current state and writes it to the label and, when configured, the
        /// background.
        /// </summary>
        /// <remarks>
        /// An unresolvable token leaves both targets untouched: a token missing from the active variant is a
        /// validation finding, not a reason to paint the label transparent. Before initialization completes
        /// there is no service to resolve against, so the authored colours stand until <see cref="Start"/>
        /// finishes.
        /// </remarks>
        private void ApplyStateColor()
        {
            var token = isOn ? onColor : offColor;
            if (!token.TryResolve(_themeService, out Color color)) return;

            if (labelText)
                labelText.color = color;
            if (toggleBackground && !onSprite)
                RuntimeManager.RunCoroutine(SetColor(color));
        }

        private void OnClicked()
        {
            RuntimeManager.GetSubsystem<AudioManager>()?.PlaySFX(SFX_COLLECTION_NAME, SFX_CLICK_NAME);
            isOn = !isOn;
            onClicked?.Invoke(this);
        }

        /// <summary>
        /// Use this to notify the state group
        /// </summary>
        /// <param name="value"></param>
        public void SetState(bool value)
        {
            if (isOn == value) return;

            isOn = value;
            onClicked?.Invoke(this);
        }

        public void InvokeStateEvent()
        {
            onStateChanged?.Invoke(isOn);
            if (isOn)
                onStateOn?.Invoke();
            else
                onStateOff?.Invoke();
        }

        private IEnumerator SetColor(Color target)
        {
            if(!interpolateColor)
            {
                backgroundImage.color = target;
                yield break;
            }

            Color start = backgroundImage.color;
            float a = 0f;
            while(a < 1f)
            {
                a += Time.deltaTime * 5f;
                backgroundImage.color = Color.Lerp(start, target, a);
                yield return new WaitForEndOfFrame();
            }
        }
    }
}