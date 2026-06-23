using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// The composer card of the assistant chat window (Sprint 25.2): the pinned-context chip row, the
    /// multiline input, the action-mode field, the token estimate, and the Send/Stop buttons. Owns the
    /// input key handling (plain Enter sends, Shift+Enter inserts a newline) and the token estimate, so
    /// the window only mediates send/stop/add-context intents. Built over the named slots authored in
    /// <c>AssistantChatWindow.uxml</c>; cosmetic styling lives in the matching USS.
    /// </summary>
    public sealed class AssistantComposer
    {
        /// <summary>A token estimate above this draws a warning and suggests dropping old turns (Sprint 24.8).</summary>
        private const int TokenWarnThreshold = 120000;

        /// <summary>Project-scoped pref key for the selected <see cref="AssistantActionMode"/>.</summary>
        private const string ActionModePrefKey = "Assistant.ActionMode";

        private readonly AssistantChatController _controller;
        private readonly Action _onSend;

        private readonly TextField _input;
        private readonly Button _send;
        private readonly Button _stop;
        private readonly EnumField _modeField;
        private readonly Label _tokenEstimate;
        private readonly Button _compactionView;
        private readonly VisualElement _contextChips;

        /// <summary>Wires the composer slots under <paramref name="root"/> and their callbacks.</summary>
        public AssistantComposer(VisualElement root, AssistantChatController controller,
            Action onSend, Action onStop, Action onAddContext)
        {
            _controller = controller;
            _onSend = onSend;

            _input = root.Q<TextField>("input");
            _send = root.Q<Button>("send");
            _stop = root.Q<Button>("stop");
            _tokenEstimate = root.Q<Label>("token-estimate");
            _contextChips = root.Q<VisualElement>("context-chips");

            // A click-to-view affordance shown only after an auto-compaction this session (Sprint 46),
            // sitting beside the token estimate. Opens the generated summary / digest detail.
            _compactionView = new Button(ShowCompactionDetail);
            _compactionView.AddToClassList("chat-compaction-view");
            _compactionView.style.display = DisplayStyle.None;
            _tokenEstimate.parent?.Add(_compactionView);

            root.Q<Button>("add-context").clicked += onAddContext;
            _send.clicked += onSend;
            _stop.clicked += onStop;

            // The action-mode field needs the typed enum value, so it is built in code into its slot.
            _modeField = new EnumField(LoadActionMode())
            {
                tooltip = "Action authorization:\n• Ask — confirm every mutating tool call.\n• Auto — run allowlisted undoable actions without prompting (irreversible steps still confirm).\n• Plan — approve a multi-step task once, then run its undoable steps under one whole-task undo (irreversible steps still confirm).\n• Auto All — run every allowlisted action unprompted, including irreversible ones (cannot be undone). Use with care.\nRead-only tools always run."
            };
            _modeField.AddToClassList("chat-mode-field");
            _modeField.RegisterValueChangedCallback(OnActionModeChanged);
            root.Q<VisualElement>("mode-slot").Add(_modeField);

            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _input.RegisterValueChangedCallback(_ => UpdateTokenEstimate());
            var inputText = _input.Q(className: "unity-text-element");
            if (inputText != null)
            {
                inputText.style.whiteSpace = WhiteSpace.Normal;
                inputText.style.flexShrink = 1;
                inputText.style.minWidth = 0;
            }
        }

        /// <summary>The persisted action mode (defaults to <see cref="AssistantActionMode.Ask"/>).</summary>
        public static AssistantActionMode LoadActionMode()
            => (AssistantActionMode)MolcaEditorPrefs.GetInt(ActionModePrefKey, (int)AssistantActionMode.Ask);

        /// <summary>The current input text.</summary>
        public string Text
        {
            get => _input.value;
            set => _input.value = value;
        }

        /// <summary>Focuses the input field (used by edit-and-resend).</summary>
        public void FocusInput() => _input.Focus();

        /// <summary>Toggles Send/Stop visibility and input editability for the busy state.</summary>
        public void SetBusy(bool busy)
        {
            _send.style.display = busy ? DisplayStyle.None : DisplayStyle.Flex;
            _stop.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            _input.SetEnabled(!busy);
        }

        /// <summary>Rebuilds the pinned-context chip row from the controller's pinned set.</summary>
        public void RebuildContextChips()
        {
            _contextChips.Clear();
            foreach (var item in _controller.PinnedContext)
                _contextChips.Add(BuildChip(item));
        }

        /// <summary>Refreshes the token-estimate label, including the pending input text.</summary>
        public void UpdateTokenEstimate()
        {
            var estimate = _controller.EstimateContextTokens(_input?.value);

            // When auto-compaction is on, gauge against its configured threshold and tell the user the
            // Assistant will compact rather than asking them to prune manually (Sprint 46). Otherwise fall
            // back to the static advisory warning.
            if (_controller.AutoCompactEnabled)
            {
                var threshold = _controller.AutoCompactThreshold;
                var warn = estimate >= threshold;
                _tokenEstimate.text = warn
                    ? $"~{estimate:N0} / {threshold:N0} tokens — over limit; auto-compacting."
                    : $"~{estimate:N0} / {threshold:N0} tokens in context";
                _tokenEstimate.EnableInClassList("chat-token-estimate--warn", warn);
            }
            else
            {
                var warn = estimate >= TokenWarnThreshold;
                _tokenEstimate.text = warn
                    ? $"~{estimate:N0} tokens — large context; consider New chat or removing pinned items."
                    : $"~{estimate:N0} tokens in context";
                _tokenEstimate.EnableInClassList("chat-token-estimate--warn", warn);
            }

            // Append the session's estimated spend so cost stays legible, not just a single end-of-session
            // surprise (Sprint 49). Always labeled approximate.
            var cost = _controller.SessionEstimatedCostUsd;
            if (cost > 0)
                _tokenEstimate.text += $"  ·  ~{AssistantCostTable.FormatCost(cost)} this session";

            RefreshCompactionNotice();
        }

        /// <summary>Shows or hides the "context compacted" affordance based on the controller's last pass (Sprint 46).</summary>
        private void RefreshCompactionNotice()
        {
            var summarized = !string.IsNullOrEmpty(_controller.LastCompactionSummary);
            var digested = _controller.LastCompactionDigestedCount;
            var any = summarized || digested > 0;
            _compactionView.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            if (any)
                _compactionView.text = summarized ? "context compacted ✓ — view" : $"condensed {digested} results ✓ — view";
        }

        /// <summary>Opens the generated summary (or the digest count) from the most recent auto-compaction.</summary>
        private void ShowCompactionDetail()
        {
            var summary = _controller.LastCompactionSummary;
            var body = !string.IsNullOrEmpty(summary)
                ? "The earlier conversation was condensed to stay within the context limit. Summary kept in context:\n\n" + summary
                : $"Condensed {_controller.LastCompactionDigestedCount} older tool result(s) to stay within the context limit. The conversation text is unchanged.";
            EditorUtility.DisplayDialog("Context compacted", body, "Close");
        }

        private VisualElement BuildChip(AssistantContextItem item)
        {
            var chip = new VisualElement();
            chip.AddToClassList("chat-chip");

            var label = new Label(item.ChipLabel);
            label.AddToClassList("chat-chip__label");
            chip.Add(label);

            var remove = new Button(() => _controller.RemoveContext(item)) { text = "×" };
            remove.AddToClassList("chat-chip__remove");
            chip.Add(remove);
            return chip;
        }

        private void OnActionModeChanged(ChangeEvent<Enum> evt)
        {
            var mode = (AssistantActionMode)evt.newValue;
            MolcaEditorPrefs.SetInt(ActionModePrefKey, (int)mode);
            if (_controller != null) _controller.ActionMode = mode;
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            var isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
            if (!isEnter) return;

            // We own both Enter behaviours so the editor's navigation system can't turn Return into a
            // submit/blur: plain Enter sends, Shift+Enter inserts a newline at the caret.
            evt.StopImmediatePropagation();
            evt.StopPropagation();

            if (evt.shiftKey)
                InsertNewlineAtCaret();
            else
                _input.schedule.Execute(() => _onSend?.Invoke());
        }

        /// <summary>
        /// Inserts a line break at the current caret (replacing any selection) and advances the caret.
        /// Done manually because we stop the Return key event before the text editor can handle it, which
        /// is what otherwise let Shift+Enter fall through to navigation and unfocus the field.
        /// </summary>
        private void InsertNewlineAtCaret()
        {
            var value = _input.value ?? string.Empty;
            var start = 0;
            var end = value.Length;
            if (_input.Q(className: "unity-text-element") is TextElement textElement && textElement.selection != null)
            {
                var a = textElement.selection.cursorIndex;
                var b = textElement.selection.selectIndex;
                start = Mathf.Clamp(Mathf.Min(a, b), 0, value.Length);
                end = Mathf.Clamp(Mathf.Max(a, b), 0, value.Length);
            }

            _input.value = value.Substring(0, start) + "\n" + value.Substring(end);

            // Restore the caret just after the inserted newline once the value change is applied.
            var caret = start + 1;
            _input.schedule.Execute(() =>
            {
                if (_input.Q(className: "unity-text-element") is TextElement te && te.selection != null)
                {
                    te.selection.cursorIndex = caret;
                    te.selection.selectIndex = caret;
                }
            });
        }
    }
}
