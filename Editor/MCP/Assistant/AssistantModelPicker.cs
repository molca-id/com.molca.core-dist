using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// The in-window provider + model picker for the assistant chat (Sprint 71). A compact row —
    /// provider dropdown, an editable model field with a discovered/known-model dropdown, and (for the
    /// <see cref="LlmProviderKind.Local"/> backend) a Detect action plus a reachability hint — that lets the
    /// user switch backends without leaving the chat. Selecting applies immediately to the live controller
    /// by writing <c>provider</c>/<c>model</c> onto <see cref="AssistantSettings"/> through the same
    /// <see cref="SerializedObject"/> path the Hub uses, so <see cref="ToolCallTransport.Auto"/> re-resolves
    /// and the change persists.
    /// </summary>
    /// <remarks>
    /// The picker never surfaces, enters, or stores an API key — a keyless cloud provider simply shows as
    /// <see cref="AssistantConfigStatus.Misconfigured"/> with a pointer to the Hub key row. Switching is
    /// disabled while a turn is in flight (<see cref="SetBusy"/>) and applies on the next turn, so in-flight
    /// history is never corrupted. Built entirely in code and inserted into the composer by
    /// <see cref="AssistantChatView"/>.
    /// </remarks>
    public sealed class AssistantModelPicker
    {
        private static readonly (LlmProviderKind Kind, string Label)[] Providers =
        {
            (LlmProviderKind.OpenAI, "OpenAI / Compatible"),
            (LlmProviderKind.Anthropic, "Anthropic"),
            (LlmProviderKind.Local, "Local (Ollama)")
        };

        private readonly AssistantSettings _settings;
        private readonly AssistantChatController _controller;
        private readonly Action _onChanged;

        private readonly VisualElement _root;
        private readonly DropdownField _providerField;
        private readonly TextField _modelField;
        private readonly Button _modelMenuButton;
        private readonly Button _detectButton;
        private readonly DropdownField _reasoningField;
        private readonly Label _hint;

        private static readonly (ReasoningEffort Effort, string Label)[] ReasoningLevels =
        {
            (ReasoningEffort.Off, "Reasoning: Off"),
            (ReasoningEffort.Low, "Reasoning: Low"),
            (ReasoningEffort.Medium, "Reasoning: Medium"),
            (ReasoningEffort.High, "Reasoning: High")
        };

        private CancellationTokenSource _discoveryCts;
        private bool _suppressCallbacks;

        /// <summary>The picker row, to be inserted into the composer.</summary>
        public VisualElement Root => _root;

        /// <summary>Builds the picker bound to <paramref name="settings"/> and the live <paramref name="controller"/>.</summary>
        /// <param name="settings">The assistant settings the selection is written to.</param>
        /// <param name="controller">The live controller, consulted for the in-flight (busy) guard.</param>
        /// <param name="onChanged">Invoked after a selection is applied so the host can refresh its status line.</param>
        public AssistantModelPicker(AssistantSettings settings, AssistantChatController controller, Action onChanged)
        {
            _settings = settings;
            _controller = controller;
            _onChanged = onChanged;

            // Column: a controls row, with the status/hint on its own line beneath so a long message
            // (e.g. a Misconfigured warning) can never cramp the provider/model controls.
            _root = new VisualElement { name = "model-picker" };
            _root.AddToClassList("chat-model-picker");

            var controls = new VisualElement { name = "model-picker-controls" };
            controls.AddToClassList("chat-model-picker__controls");
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;
            _root.Add(controls);

            var providerChoices = new List<string>();
            foreach (var p in Providers) providerChoices.Add(p.Label);
            _providerField = new DropdownField(providerChoices, LabelFor(_settings.Provider))
            {
                tooltip = "LLM backend. Switching applies to the next turn and persists to the Assistant settings."
            };
            _providerField.AddToClassList("chat-model-picker__provider");
            _providerField.style.flexShrink = 0;
            _providerField.RegisterValueChangedCallback(OnProviderChanged);
            controls.Add(_providerField);

            _modelField = new TextField { value = ResolvedModel() };
            _modelField.AddToClassList("chat-model-picker__model");
            _modelField.style.flexGrow = 1;
            _modelField.style.minWidth = 0;
            _modelField.isDelayed = true; // Persist on Enter/blur, not per keystroke.
            _modelField.tooltip = "Model id. Pick from the ▾ list or type any value (a pulled Ollama tag, a cloud model name).";
            _modelField.RegisterValueChangedCallback(OnModelChanged);
            controls.Add(_modelField);

            _modelMenuButton = new Button(OpenModelMenu) { text = "▾" };
            _modelMenuButton.AddToClassList("chat-model-picker__menu");
            _modelMenuButton.style.flexShrink = 0;
            _modelMenuButton.tooltip = "Choose from discovered / known models";
            controls.Add(_modelMenuButton);

            _detectButton = new Button(() => RefreshDiscovery(forceRefresh: true, openMenu: false)) { text = "Detect" };
            _detectButton.AddToClassList("chat-model-picker__detect");
            _detectButton.style.flexShrink = 0;
            _detectButton.tooltip = "Ping the local runtime and list its pulled models";
            controls.Add(_detectButton);

            // Reasoning / extended-thinking budget (Sprint 76). Applies to the next turn; ignored by
            // non-reasoning models and the Local backend.
            var reasoningChoices = new List<string>();
            foreach (var r in ReasoningLevels) reasoningChoices.Add(r.Label);
            _reasoningField = new DropdownField(reasoningChoices, ReasoningLabelFor(_settings.ReasoningEffort))
            {
                tooltip = "Reasoning / extended-thinking budget for capable models (Anthropic thinking, OpenAI reasoning_effort). Higher = better hard-task answers, more output tokens and latency. Ignored by non-reasoning and Local models."
            };
            _reasoningField.AddToClassList("chat-model-picker__reasoning");
            _reasoningField.style.flexShrink = 0;
            _reasoningField.RegisterValueChangedCallback(OnReasoningChanged);
            controls.Add(_reasoningField);

            // Status/hint on its own line (hidden when empty), so it wraps freely without stealing width.
            _hint = new Label();
            _hint.AddToClassList("chat-model-picker__hint");
            _hint.style.display = DisplayStyle.None;
            _root.Add(_hint);

            SyncProviderDependentUi();
            // Populate the local hint / cache on open without forcing a menu.
            RefreshDiscovery(forceRefresh: false, openMenu: false);
        }

        /// <summary>Disables the picker while a turn is in flight so a switch can't corrupt in-flight history.</summary>
        public void SetBusy(bool busy) => _root.SetEnabled(!busy);

        private void OnProviderChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallbacks) return;
            var provider = KindFor(evt.newValue);

            // Switching backends: default the model to the provider's default so the field is never left
            // pointing at the previous provider's tag. The user can immediately pick or type another.
            var model = AssistantSettings.DefaultModelFor(provider);
            AssistantModelCatalog.ApplySelection(_settings, provider, model);

            _suppressCallbacks = true;
            _modelField.SetValueWithoutNotify(model);
            _suppressCallbacks = false;

            SyncProviderDependentUi();
            WarnIfMisconfigured();
            _onChanged?.Invoke();
            RefreshDiscovery(forceRefresh: false, openMenu: false);
        }

        private void OnModelChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallbacks) return;
            AssistantModelCatalog.ApplySelection(_settings, _settings.Provider, evt.newValue);
            WarnIfMisconfigured();
            _onChanged?.Invoke();
        }

        private void OnReasoningChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallbacks) return;
            AssistantModelCatalog.ApplyReasoning(_settings, ReasoningEffortFor(evt.newValue));
            _onChanged?.Invoke();
        }

        private static string ReasoningLabelFor(ReasoningEffort effort)
        {
            foreach (var r in ReasoningLevels) if (r.Effort == effort) return r.Label;
            return ReasoningLevels[0].Label;
        }

        private static ReasoningEffort ReasoningEffortFor(string label)
        {
            foreach (var r in ReasoningLevels) if (r.Label == label) return r.Effort;
            return ReasoningEffort.Off;
        }

        /// <summary>Opens a menu of discovered/known models (running discovery first), filling the field on pick.</summary>
        private async void OpenModelMenu()
        {
            try { await RefreshDiscoveryAsync(forceRefresh: false, openMenu: true); }
            catch (OperationCanceledException) { /* dropdown closed / provider switched — not an error. */ }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private async void RefreshDiscovery(bool forceRefresh, bool openMenu)
        {
            try { await RefreshDiscoveryAsync(forceRefresh, openMenu); }
            catch (OperationCanceledException) { /* superseded discovery — not an error. */ }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private async System.Threading.Tasks.Task RefreshDiscoveryAsync(bool forceRefresh, bool openMenu)
        {
            _discoveryCts?.Cancel();
            _discoveryCts?.Dispose();
            _discoveryCts = new CancellationTokenSource();
            var ct = _discoveryCts.Token;

            if (_settings.Provider == LlmProviderKind.Local && forceRefresh)
                SetHint("Detecting…", warn: false);

            var result = await AssistantModelCatalog.DiscoverAsync(_settings, forceRefresh, ct);
            ct.ThrowIfCancellationRequested();

            UpdateHint(result);
            if (openMenu) ShowModelMenu(result.Models);
        }

        private void ShowModelMenu(IReadOnlyList<string> models)
        {
            var menu = new GenericMenu();
            var current = ResolvedModel();
            if (models == null || models.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(_settings.Provider == LlmProviderKind.Local
                    ? "No models discovered — type a tag"
                    : "No known models — type a model id"));
            }
            else
            {
                foreach (var model in models)
                {
                    var value = model;
                    menu.AddItem(new GUIContent(value), value == current, () => SetModel(value));
                }
            }
            menu.ShowAsContext();
        }

        private void SetModel(string model)
        {
            _suppressCallbacks = true;
            _modelField.SetValueWithoutNotify(model);
            _suppressCallbacks = false;
            AssistantModelCatalog.ApplySelection(_settings, _settings.Provider, model);
            WarnIfMisconfigured();
            _onChanged?.Invoke();
        }

        private void UpdateHint(ModelCatalogResult result)
        {
            // A live Misconfigured state (missing cloud key) takes precedence over the discovery message.
            if (WarnIfMisconfigured()) return;

            var warn = result != null && _settings.Provider == LlmProviderKind.Local && !result.Reachable;
            SetHint(result?.Message ?? string.Empty, warn);
        }

        /// <summary>Shows a Misconfigured hint (and returns true) when the current selection can't run.</summary>
        private bool WarnIfMisconfigured()
        {
            var status = _settings.GetStatus(out var message);
            var misconfigured = status == AssistantConfigStatus.Misconfigured;
            if (misconfigured)
                SetHint(message + " Set the key in the Molca Hub settings.", warn: true);
            return misconfigured;
        }

        /// <summary>Sets the hint line, hiding it entirely when the message is empty so no blank row is left.</summary>
        private void SetHint(string text, bool warn)
        {
            var has = !string.IsNullOrEmpty(text);
            _hint.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _hint.text = text ?? string.Empty;
            _hint.EnableInClassList("chat-model-picker__hint--warn", has && warn);
        }

        private void SyncProviderDependentUi()
        {
            // Detect is only meaningful for a discoverable local endpoint.
            _detectButton.style.display =
                _settings.Provider == LlmProviderKind.Local ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private string ResolvedModel()
        {
            // Prefer the raw stored field so an empty value shows the provider default without persisting it.
            return string.IsNullOrWhiteSpace(_settings.Model)
                ? AssistantSettings.DefaultModelFor(_settings.Provider)
                : _settings.Model;
        }

        private static string LabelFor(LlmProviderKind kind)
        {
            foreach (var p in Providers) if (p.Kind == kind) return p.Label;
            return Providers[0].Label;
        }

        private static LlmProviderKind KindFor(string label)
        {
            foreach (var p in Providers) if (p.Label == label) return p.Kind;
            return LlmProviderKind.OpenAI;
        }
    }
}
