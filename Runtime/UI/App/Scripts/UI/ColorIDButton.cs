using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using Molca.ColorID;
using Molca;

namespace Molca.App.UI
{
    /// <summary>
    /// A <see cref="Button"/> whose per-state colours are canonical colour tokens rather than fixed
    /// <see cref="Color"/> values, reapplied whenever the active theme variant changes.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/UI/App/Scripts/UI/</c>.
    /// <b>Base class:</b> <see cref="Button"/>.
    /// <b>Registration:</b> added by authoring. Resolves <see cref="IColorThemeService"/> through
    /// <c>RuntimeManager.GetService</c> after <c>RuntimeManager.WaitForInitialization()</c>, subscribes to
    /// <see cref="IColorThemeService.ThemeChanged"/>, and unsubscribes on destroy.
    /// <para/>
    /// <b>Colour is written to <see cref="Selectable.targetGraphic"/> and nowhere else.</b> Before V2 this
    /// component drove a sibling <c>ColorID</c> component, which could paint several targets and descend
    /// into children. A button with no <see cref="Selectable.targetGraphic"/> assigned therefore no longer
    /// recolours anything — that is an authoring problem to fix on the object, not something this
    /// component guesses its way around by searching the hierarchy.
    /// <para/>
    /// <see cref="ColorThemeBinding"/> is not a substitute here: it applies one fixed token per binding and
    /// exposes no setter, whereas a state button changes token per interaction state.
    /// </remarks>
    [AddComponentMenu("Molca/UI/Color ID Button")]
    public class ColorIDButton : Button
    {
        [Header("Color Token Configuration")]
        [SerializeField] private ColorTokenReference normalColor = new ColorTokenReference("action/primary/fill");
        [SerializeField] private ColorTokenReference highlightedColor = new ColorTokenReference("surface/panel");
        [SerializeField] private ColorTokenReference pressedColor = new ColorTokenReference("surface/raised");
        [SerializeField] private ColorTokenReference selectedColor = new ColorTokenReference("action/primary/fill");
        [SerializeField] private ColorTokenReference disabledColor = new ColorTokenReference("action/disabled/fill");

        [Header("Toggle Configuration")]
        [SerializeField] private bool isToggleButton = false;
        [SerializeField] private bool isOn = false;
        [SerializeField] private bool excludeFromGroup = false;

        [Header("Events")]

        [Header("Toggle Events")]
        public UnityEvent<bool> onToggleChanged;
        public UnityEvent onToggleOn;
        public UnityEvent onToggleOff;

        [Header("Hover Events")]
        public UnityEvent onPointerEnter;
        public UnityEvent onPointerExit;

        // Cached so OnDestroy unsubscribes from the same instance even if the service registry is already
        // gone during teardown.
        private IColorThemeService _themeService;

        // The token last requested. A pointer event can arrive before initialization completes, in which
        // case there is no service to resolve against yet; replaying this once the service arrives is what
        // keeps the button from sitting on its authored colour until the next interaction.
        private ColorTokenReference _pendingToken;

        private ColorIDButtonGroup buttonGroup;

        public bool IsToggleButton => isToggleButton;
        public bool IsOn
        {
            get => isOn;
            set => SetToggleState(value, true);
        }

        // async void is permitted only as a Unity entry point, and only as a thin shim that cannot let an
        // exception escape into Unity's synchronization context unobserved.
        protected override async void Start()
        {
            base.Start();
            transition = Transition.None;

            try
            {
                await RuntimeManager.WaitForInitialization();

                // If destroyed during the await, OnDestroy has already run — subscribing now would leak a
                // handler on a dead object.
                if (this == null) return;

                _themeService = RuntimeManager.GetService<IColorThemeService>();
                if (_themeService == null)
                {
                    Debug.LogWarning(
                        "[ColorIDButton] No IColorThemeService is available. This project has not installed "
                        + "a ColorThemeSettings module, so canonical colour tokens cannot resolve.", this);
                    return;
                }

                _themeService.ThemeChanged += OnThemeChanged;
                UpdateColors();
            }
            catch (OperationCanceledException)
            {
                // Bootstrap torn down (quit during initialization). Cancellation is not an error.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        protected override void OnDestroy()
        {
            if (_themeService != null) _themeService.ThemeChanged -= OnThemeChanged;
            base.OnDestroy();
        }

        // Reapply the state colour under the new variant rather than recomputing which state we are in:
        // the interaction state has not changed, only the palette it resolves through.
        private void OnThemeChanged(ColorThemeChanged change) => SetColor(_pendingToken);

        public override void OnPointerClick(PointerEventData eventData)
        {
            base.OnPointerClick(eventData);

            if (isToggleButton && interactable)
            {
                Toggle();
            }
        }

        public void Toggle()
        {
            if (!isToggleButton) return;
            SetToggleState(!isOn, true);
        }

        public void SetToggleState(bool state, bool notifyGroup = true)
        {
            if (isOn == state) return;

            isOn = state;

            // Update visual state
            if (isToggleButton)
            {
                SetColor(isOn ? selectedColor : normalColor);
            }

            // Notify button group if needed
            if (notifyGroup && buttonGroup != null)
            {
                buttonGroup.OnButtonToggled(this);
            }

            // Invoke events
            onToggleChanged?.Invoke(isOn);
            if (isOn)
                onToggleOn?.Invoke();
            else
                onToggleOff?.Invoke();
        }

        public override void OnPointerEnter(PointerEventData eventData)
        {
            base.OnPointerEnter(eventData);
            if (interactable)
            {
                SetColor(highlightedColor);
            }
            onPointerEnter?.Invoke();
        }

        public override void OnPointerExit(PointerEventData eventData)
        {
            base.OnPointerExit(eventData);
            if (interactable)
            {
                // Return to appropriate color based on toggle state
                if (isToggleButton && isOn)
                    SetColor(selectedColor);
                else
                    SetColor(normalColor);
            }
            onPointerExit?.Invoke();
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (interactable)
            {
                SetColor(pressedColor);
            }
        }

        public override void OnPointerUp(PointerEventData eventData)
        {
            base.OnPointerUp(eventData);
            if (interactable)
            {
                SetColor(highlightedColor);
            }
        }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            if (interactable)
            {
                // For toggle buttons, maintain current state color
                if (!isToggleButton)
                    SetColor(selectedColor);
            }
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            base.OnDeselect(eventData);
            if (interactable)
            {
                // Return to appropriate color based on toggle state
                if (isToggleButton && isOn)
                    SetColor(selectedColor);
                else if (!isToggleButton)
                    SetColor(normalColor);
            }
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);

            if (!interactable)
            {
                SetColor(disabledColor);
            }
            else
            {
                // Restore appropriate color when interactable is re-enabled
                UpdateColors();
            }
        }

        // Override the interactable property to handle color updates
        public new bool interactable
        {
            get => base.interactable;
            set
            {
                if (base.interactable != value)
                {
                    base.interactable = value;

                    // Update colors when interactable state changes
                    if (value)
                    {
                        // Re-enabled - restore appropriate color
                        UpdateColors();
                    }
                    else
                    {
                        // Disabled - set disabled color
                        SetColor(disabledColor);
                    }
                }
            }
        }

        /// <summary>
        /// Resolves <paramref name="token"/> against the active theme and writes it to
        /// <see cref="Selectable.targetGraphic"/>.
        /// </summary>
        /// <param name="token">The token to apply.</param>
        /// <remarks>
        /// Records the request even when it cannot be satisfied yet, so the colour appears once the theme
        /// service is available rather than waiting for the next interaction. An unresolvable token leaves
        /// the graphic untouched — a broken asset should surface as a validation finding, not as a
        /// transparent button.
        /// </remarks>
        private void SetColor(ColorTokenReference token)
        {
            _pendingToken = token;

            if (targetGraphic == null) return;
            if (!token.TryResolve(_themeService, out Color color)) return;

            targetGraphic.color = color;
        }

        private void UpdateColors()
        {
            if (!IsInteractable())
            {
                SetColor(disabledColor);
                return;
            }

            if (isToggleButton)
            {
                SetColor(isOn ? selectedColor : normalColor);
            }
            else
            {
                SetColor(normalColor);
            }
        }

        /// <summary>Repoints the normal-state token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public void SetNormalColor(string tokenId)
        {
            normalColor = new ColorTokenReference(tokenId);
            if (currentSelectionState == SelectionState.Normal && !isToggleButton)
            {
                UpdateColors();
            }
        }

        /// <summary>Repoints the highlighted-state token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public void SetHighlightedColor(string tokenId)
        {
            highlightedColor = new ColorTokenReference(tokenId);
        }

        /// <summary>Repoints the pressed-state token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public void SetPressedColor(string tokenId)
        {
            pressedColor = new ColorTokenReference(tokenId);
        }

        /// <summary>Repoints the selected-state token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public void SetSelectedColor(string tokenId)
        {
            selectedColor = new ColorTokenReference(tokenId);
            if (isToggleButton && isOn)
            {
                UpdateColors();
            }
        }

        /// <summary>Repoints the disabled-state token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public void SetDisabledColor(string tokenId)
        {
            disabledColor = new ColorTokenReference(tokenId);
        }

        // Button group management
        internal void RegisterWithGroup(ColorIDButtonGroup group)
        {
            buttonGroup = group;
        }

        internal void UnregisterFromGroup()
        {
            buttonGroup = null;
        }

        public bool ExcludeFromGroup => excludeFromGroup;
    }
}
