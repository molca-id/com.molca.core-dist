using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>A slash-palette verb offered by the composer's <c>/</c> popup (Sprint 91.1d).</summary>
    public sealed class AssistantComposerCommand
    {
        /// <summary>The verb as typed (without the leading slash), e.g. <c>new</c>.</summary>
        public string Name { get; }

        /// <summary>One-line description shown beside the verb in the palette.</summary>
        public string Description { get; }

        /// <summary>Runs the verb. The composer clears the input before invoking.</summary>
        public Action Run { get; }

        /// <summary>Creates a palette command.</summary>
        public AssistantComposerCommand(string name, string description, Action run)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            Run = run;
        }
    }

    /// <summary>
    /// The composer card of the assistant chat window (Sprint 25.2, reworked Sprint 91.1): the
    /// pinned-context chip row, the auto-growing multiline input, the segmented action-mode control, the
    /// stat segments (tokens/cost/cache/reasoning), and the Send/Stop buttons. Owns the input key handling
    /// (plain Enter sends, Shift+Enter inserts a newline, Up/Down recall prompt history), the queued-message
    /// slot used while a turn is running, the <c>@</c>-mention / <c>/</c>-command suggestion popup, and
    /// asset/image drag-and-drop, so the window only mediates send/stop/add-context intents. Built over the
    /// named slots authored in <c>AssistantChatWindow.uxml</c>; cosmetic styling lives in the matching USS.
    /// </summary>
    public sealed class AssistantComposer
    {
        /// <summary>A token estimate above this draws a warning and suggests dropping old turns (Sprint 24.8).</summary>
        private const int TokenWarnThreshold = 120000;

        /// <summary>Project-scoped pref key for the selected <see cref="AssistantActionMode"/>.</summary>
        private const string ActionModePrefKey = "Assistant.ActionMode";

        /// <summary>Cap on recalled prompt-history entries (Sprint 91.1a).</summary>
        private const int HistoryCap = 50;

        /// <summary>Cap on rows shown in the suggestion popup.</summary>
        private const int SuggestCap = 8;

        private readonly AssistantChatController _controller;
        private readonly Action _onSend;
        private readonly VisualElement _viewRoot;
        private readonly VisualElement _composerRoot;

        private readonly TextField _input;
        private readonly Button _send;
        private readonly Button _stop;
        private readonly Label _tokenEstimate;
        private readonly Button _compactionView;
        private readonly VisualElement _contextChips;
        private readonly VisualElement _statsRow;
        private Label _statCost;
        private Label _statCache;
        private Label _statReasoning;

        // Segmented action-mode control (Sprint 91.1g): one button per mode, selected one accented.
        private readonly List<(AssistantActionMode mode, Button button)> _modeButtons = new List<(AssistantActionMode, Button)>();

        // Images staged for the next turn (Sprint 73) and their thumbnail row + attach button. Cleared once
        // the turn is sent.
        private readonly List<AssistantImageAttachment> _attachments = new List<AssistantImageAttachment>();
        private readonly VisualElement _attachThumbs;
        private readonly Button _attachButton;

        // Queued-message slot (Sprint 91.1b): text/images staged while a turn runs, auto-sent on completion.
        private readonly VisualElement _queuedRow;
        private readonly Label _queuedLabel;
        private string _queuedText;
        private List<AssistantImageAttachment> _queuedImages;

        // Prompt history (Sprint 91.1a): most recent last; -1 = not navigating.
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;

        // Suggestion popup (Sprint 91.1c/d): anchored above the composer, shared by @ and /.
        private VisualElement _suggest;
        private readonly List<(Label row, Action apply)> _suggestRows = new List<(Label, Action)>();
        private int _suggestIndex = -1;
        private string _suggestToken;
        private bool _busy;

        /// <summary>The slash-palette verbs offered when the input starts with <c>/</c> (set by the host view).</summary>
        public IReadOnlyList<AssistantComposerCommand> SlashCommands { get; set; }

        /// <summary>Wires the composer slots under <paramref name="root"/> and their callbacks.</summary>
        public AssistantComposer(VisualElement root, AssistantChatController controller,
            Action onSend, Action onStop, Action onAddContext)
        {
            _controller = controller;
            _onSend = onSend;
            _viewRoot = root;

            _composerRoot = root.Q<VisualElement>("composer");
            _input = root.Q<TextField>("input");
            _send = root.Q<Button>("send");
            _stop = root.Q<Button>("stop");
            _tokenEstimate = root.Q<Label>("token-estimate");
            _contextChips = root.Q<VisualElement>("context-chips");
            _statsRow = root.Q<VisualElement>("stats-row");
            _queuedRow = root.Q<VisualElement>("queued-row");
            _queuedLabel = root.Q<Label>("queued-label");
            root.Q<Button>("queued-cancel").clicked += CancelQueued;

            BuildStatSegments();

            // A click-to-view affordance shown only after an auto-compaction this session (Sprint 46),
            // sitting beside the token estimate. Opens the generated summary / digest detail.
            _compactionView = new Button(ShowCompactionDetail);
            _compactionView.AddToClassList("chat-compaction-view");
            _compactionView.style.display = DisplayStyle.None;
            _statsRow.Add(_compactionView);

            var addContext = root.Q<Button>("add-context");
            addContext.clicked += onAddContext;
            _send.clicked += onSend;
            _stop.clicked += onStop;

            // Attach-image affordance (Sprint 73). A thumbnail strip for staged images sits above the
            // context chip row.
            _attachThumbs = new VisualElement();
            _attachThumbs.AddToClassList("chat-attach-thumbs");
            _attachThumbs.style.flexDirection = FlexDirection.Row;
            _attachThumbs.style.flexWrap = Wrap.Wrap;
            _contextChips.parent?.Insert(_contextChips.parent.IndexOf(_contextChips), _attachThumbs);

            _attachButton = new Button(ShowAttachMenu) { text = "＋ Image" };
            _attachButton.AddToClassList("chat-attach-button");
            _attachButton.tooltip = "Attach an image (Scene/Game view, a file, or the selected texture) for a vision-capable model.";
            addContext.parent?.Add(_attachButton);

            BuildModeControl(root.Q<VisualElement>("mode-slot"));

            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _input.RegisterValueChangedCallback(_ =>
            {
                UpdateTokenEstimate();
                RefreshSuggestions();
            });
            var inputText = _input.Q(className: "unity-text-element");
            if (inputText != null)
            {
                inputText.style.whiteSpace = WhiteSpace.Normal;
                inputText.style.flexShrink = 1;
                inputText.style.minWidth = 0;
            }

            // Auto-grow behaviour (Sprint 91.1a): the USS min/max heights let the field grow with content;
            // past the cap the field scrolls internally instead of clipping.
            _input.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _input.textEdition.placeholder = "Ask about your project…   @ context · / commands";
            _input.textEdition.hidePlaceholderOnFocus = false;

            RegisterDragAndDrop();
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

        /// <summary>
        /// Reflects the busy state. The input stays editable — a message typed while busy is queued
        /// (Sprint 91.1b) — so this only swaps Send into its "Queue" form and shows Stop.
        /// </summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
            _send.text = busy ? "Queue" : "Send";
            _send.tooltip = busy
                ? "Stage this message; it sends automatically when the current turn finishes."
                : string.Empty;
            _stop.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---- Prompt history (Sprint 91.1a) ----------------------------------------------------------

        /// <summary>Records a sent prompt for Up/Down recall. Consecutive duplicates collapse.</summary>
        public void RecordHistory(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_history.Count > 0 && _history[_history.Count - 1] == text) { _historyIndex = -1; return; }
            _history.Add(text);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
            _historyIndex = -1;
        }

        private bool TryNavigateHistory(bool older)
        {
            // Only recall into an empty field, or continue an in-progress recall — never clobber a draft.
            if (_history.Count == 0) return false;
            if (_historyIndex < 0 && !string.IsNullOrEmpty(_input.value)) return false;

            if (older)
            {
                _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Mathf.Max(0, _historyIndex - 1);
            }
            else
            {
                if (_historyIndex < 0) return false;
                _historyIndex++;
                if (_historyIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    _input.value = string.Empty;
                    return true;
                }
            }

            _input.value = _history[_historyIndex];
            MoveCaretToEnd();
            return true;
        }

        private void MoveCaretToEnd()
        {
            var end = _input.value?.Length ?? 0;
            _input.schedule.Execute(() =>
            {
                if (_input.Q(className: "unity-text-element") is TextElement te && te.selection != null)
                {
                    te.selection.cursorIndex = end;
                    te.selection.selectIndex = end;
                }
            });
        }

        // ---- Queued message (Sprint 91.1b) ----------------------------------------------------------

        /// <summary>Whether a message is staged to auto-send after the current turn.</summary>
        public bool HasQueued => !string.IsNullOrWhiteSpace(_queuedText) || (_queuedImages != null && _queuedImages.Count > 0);

        /// <summary>
        /// Stages the current input text and attachments as the queued message (appending to an already
        /// queued one), then clears the composer. Returns false when there is nothing to queue.
        /// </summary>
        public bool TryQueueCurrent()
        {
            var text = _input.value;
            var hasText = !string.IsNullOrWhiteSpace(text);
            var hasImages = _attachments.Count > 0;
            if (!hasText && !hasImages) return false;

            if (hasText)
                _queuedText = string.IsNullOrWhiteSpace(_queuedText) ? text : _queuedText + "\n" + text;
            if (hasImages)
            {
                _queuedImages ??= new List<AssistantImageAttachment>();
                _queuedImages.AddRange(_attachments);
                _attachments.Clear();
                RebuildThumbs();
            }

            RecordHistory(text);
            _input.value = string.Empty;
            RefreshQueuedRow();
            return true;
        }

        /// <summary>
        /// Takes the queued message for sending. Returns false when nothing is queued; otherwise clears the
        /// slot and hands out the staged text/images.
        /// </summary>
        public bool TryDequeue(out string text, out IReadOnlyList<AssistantImageAttachment> images)
        {
            text = _queuedText;
            images = _queuedImages;
            if (!HasQueued) return false;
            _queuedText = null;
            _queuedImages = null;
            RefreshQueuedRow();
            return true;
        }

        /// <summary>Restores the queued message back into the composer instead of sending it.</summary>
        private void CancelQueued()
        {
            if (!HasQueued) return;
            if (!string.IsNullOrWhiteSpace(_queuedText))
                _input.value = string.IsNullOrEmpty(_input.value) ? _queuedText : _queuedText + "\n" + _input.value;
            if (_queuedImages != null)
            {
                _attachments.AddRange(_queuedImages);
                RebuildThumbs();
            }
            _queuedText = null;
            _queuedImages = null;
            RefreshQueuedRow();
            UpdateTokenEstimate();
            FocusInput();
        }

        private void RefreshQueuedRow()
        {
            var has = HasQueued;
            _queuedRow.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) return;
            var imageCount = _queuedImages?.Count ?? 0;
            var summary = string.IsNullOrWhiteSpace(_queuedText)
                ? $"{imageCount} image{(imageCount == 1 ? "" : "s")}"
                : FirstLine(_queuedText) + (imageCount > 0 ? $"  (+{imageCount} image{(imageCount == 1 ? "" : "s")})" : string.Empty);
            _queuedLabel.text = "Queued: " + summary;
            _queuedLabel.tooltip = _queuedText ?? string.Empty;
        }

        private static string FirstLine(string text)
        {
            var nl = text.IndexOf('\n');
            return (nl >= 0 ? text.Substring(0, nl) : text).TrimEnd();
        }

        // ---- Context chips + attachments ------------------------------------------------------------

        /// <summary>
        /// Rebuilds the pinned-context chip row from the controller's pinned set. Past six chips the row
        /// collapses to the first five plus a "+N" overflow chip listing the rest (Sprint 91.1h).
        /// </summary>
        public void RebuildContextChips()
        {
            _contextChips.Clear();
            var pinned = _controller.PinnedContext;
            const int visibleCap = 5;
            var collapse = pinned.Count > visibleCap + 1;
            for (var i = 0; i < pinned.Count && (!collapse || i < visibleCap); i++)
                _contextChips.Add(BuildChip(pinned[i]));

            if (collapse)
            {
                var more = new Button(() => ShowOverflowChips(pinned)) { text = $"+{pinned.Count - visibleCap}" };
                more.AddToClassList("chat-chip");
                more.AddToClassList("chat-chip--more");
                more.tooltip = "Show all pinned context";
                _contextChips.Add(more);
            }
            RefreshAttachAvailability();
        }

        private void ShowOverflowChips(IReadOnlyList<AssistantContextItem> pinned)
        {
            var menu = new GenericMenu();
            foreach (var item in pinned)
            {
                var captured = item;
                menu.AddItem(new GUIContent($"Remove/{captured.ChipLabel.Replace('/', ' ')}"), false,
                    () => _controller.RemoveContext(captured));
            }
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Remove all"), false, () =>
            {
                foreach (var item in new List<AssistantContextItem>(pinned))
                    _controller.RemoveContext(item);
            });
            menu.ShowAsContext();
        }

        /// <summary>Images staged for the next turn (Sprint 73); passed to the controller on send.</summary>
        public IReadOnlyList<AssistantImageAttachment> Attachments => _attachments;

        /// <summary>Discards all staged image attachments and refreshes the thumbnail strip (Sprint 73).</summary>
        public void ClearAttachments()
        {
            _attachments.Clear();
            RebuildThumbs();
            UpdateTokenEstimate();
        }

        /// <summary>Enables the attach button only when the configured model can accept images (Sprint 73).</summary>
        private void RefreshAttachAvailability()
        {
            if (_attachButton == null) return;
            var vision = _controller != null && _controller.SupportsVision;
            _attachButton.SetEnabled(vision);
            _attachButton.tooltip = vision
                ? "Attach an image (Scene/Game view, a file, or the selected texture)."
                : "The current model is not vision-capable. Switch to a vision model to attach images.";
        }

        private void ShowAttachMenu()
        {
            if (_controller == null || !_controller.SupportsVision) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Scene View"), false, () => Stage(AssistantImageCapture.TryCaptureSceneView));
            menu.AddItem(new GUIContent("Game View"), false, () => Stage(AssistantImageCapture.TryCaptureGameView));

            var selectedTexture = Selection.activeObject as Texture;
            if (selectedTexture != null)
                menu.AddItem(new GUIContent($"Selected Texture ({Selection.activeObject.name})"), false,
                    () => Stage((out AssistantImageAttachment a, out string e) =>
                        AssistantImageCapture.TryFromTexture(selectedTexture, Selection.activeObject.name, out a, out e)));
            else
                menu.AddDisabledItem(new GUIContent("Selected Texture"));

            menu.AddItem(new GUIContent("Image File…"), false, () =>
            {
                var path = EditorUtility.OpenFilePanel("Attach image", Application.dataPath, "png,jpg,jpeg");
                if (!string.IsNullOrEmpty(path))
                    Stage((out AssistantImageAttachment a, out string e) => AssistantImageCapture.TryFromFile(path, out a, out e));
            });
            menu.ShowAsContext();
        }

        private delegate bool CaptureFn(out AssistantImageAttachment attachment, out string error);

        private void Stage(CaptureFn capture)
        {
            if (capture(out var attachment, out var error) && attachment != null)
            {
                _attachments.Add(attachment);
                RebuildThumbs();
                UpdateTokenEstimate();
            }
            else if (!string.IsNullOrEmpty(error))
            {
                EditorUtility.DisplayDialog("Attach image", error, "OK");
            }
        }

        private void RebuildThumbs()
        {
            _attachThumbs.Clear();
            foreach (var attachment in _attachments)
                _attachThumbs.Add(BuildThumb(attachment));
        }

        private VisualElement BuildThumb(AssistantImageAttachment attachment)
        {
            var thumb = new VisualElement();
            thumb.AddToClassList("chat-attach-thumb");
            thumb.style.flexDirection = FlexDirection.Row;
            thumb.tooltip = $"{attachment.Label} — {attachment.Width}×{attachment.Height}";

            if (attachment.Preview != null)
            {
                var image = new Image { image = attachment.Preview, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("chat-attach-thumb__image");
                image.style.width = 40;
                image.style.height = 40;
                thumb.Add(image);
            }

            var label = new Label(attachment.Label);
            label.AddToClassList("chat-attach-thumb__label");
            thumb.Add(label);

            var remove = new Button(() =>
            {
                _attachments.Remove(attachment);
                RebuildThumbs();
                UpdateTokenEstimate();
            }) { text = "×" };
            remove.AddToClassList("chat-chip__remove");
            thumb.Add(remove);
            return thumb;
        }

        // ---- Drag-and-drop (Sprint 91.1e) -----------------------------------------------------------

        /// <summary>
        /// Accepts editor drags onto the composer card: project assets / scene objects pin as context;
        /// textures and image files stage as attachments when the model is vision-capable.
        /// </summary>
        private void RegisterDragAndDrop()
        {
            if (_composerRoot == null) return;

            _composerRoot.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (DragAndDrop.objectReferences.Length > 0 || DragAndDrop.paths.Length > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    evt.StopPropagation();
                }
            });

            _composerRoot.RegisterCallback<DragPerformEvent>(evt =>
            {
                var handled = false;
                var vision = _controller != null && _controller.SupportsVision;

                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj == null) continue;
                    if (vision && obj is Texture texture)
                    {
                        Stage((out AssistantImageAttachment a, out string e) =>
                            AssistantImageCapture.TryFromTexture(texture, obj.name, out a, out e));
                        handled = true;
                        continue;
                    }
                    var item = AssistantChatView.CreateContextForObject(obj);
                    if (item != null)
                    {
                        _controller.AddContext(item);
                        handled = true;
                    }
                }

                // External file drops arrive as paths with no object reference.
                if (DragAndDrop.objectReferences.Length == 0)
                {
                    foreach (var path in DragAndDrop.paths)
                    {
                        if (!vision || string.IsNullOrEmpty(path)) continue;
                        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;
                        Stage((out AssistantImageAttachment a, out string e) => AssistantImageCapture.TryFromFile(path, out a, out e));
                        handled = true;
                    }
                }

                if (handled)
                {
                    DragAndDrop.AcceptDrag();
                    evt.StopPropagation();
                }
            });
        }

        // ---- Stat segments (Sprint 91.1f) -----------------------------------------------------------

        /// <summary>Builds the cost/cache/reasoning segments beside the token estimate; each is tooltipped, and clicking the row shows the full detail.</summary>
        private void BuildStatSegments()
        {
            Label Segment(string ussSuffix)
            {
                var label = new Label();
                label.AddToClassList("chat-stat");
                label.AddToClassList("chat-stat--" + ussSuffix);
                label.style.display = DisplayStyle.None;
                _statsRow.Add(label);
                return label;
            }

            _tokenEstimate.AddToClassList("chat-stat");
            _statCost = Segment("cost");
            _statCache = Segment("cache");
            _statReasoning = Segment("reasoning");

            _statsRow.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target is Button || (evt.target as VisualElement)?.GetFirstAncestorOfType<Button>() != null)
                    return;
                ShowStatDetail();
            });
        }

        /// <summary>Refreshes the stat segments, including the pending input text.</summary>
        public void UpdateTokenEstimate()
        {
            var pendingImageTokens = 0;
            foreach (var attachment in _attachments)
                pendingImageTokens += AssistantCostTable.EstimateImageTokens(attachment.Width, attachment.Height);
            var estimate = _controller.EstimateContextTokens(_input?.value, pendingImageTokens);

            // When auto-compaction is on, gauge against its configured threshold and tell the user the
            // Assistant will compact rather than asking them to prune manually (Sprint 46). Otherwise fall
            // back to the static advisory warning.
            bool warn;
            if (_controller.AutoCompactEnabled)
            {
                var threshold = _controller.AutoCompactThreshold;
                warn = estimate >= threshold;
                _tokenEstimate.text = $"~{estimate:N0} / {threshold:N0} tok";
                _tokenEstimate.tooltip = warn
                    ? "Estimated context is over the auto-compact threshold; the assistant will compact the conversation."
                    : "Estimated tokens in context vs. the auto-compact threshold.";
            }
            else
            {
                warn = estimate >= TokenWarnThreshold;
                _tokenEstimate.text = $"~{estimate:N0} tok";
                _tokenEstimate.tooltip = warn
                    ? "Large context — consider New chat or removing pinned items."
                    : "Estimated tokens in context.";
            }
            _tokenEstimate.EnableInClassList("chat-token-estimate--warn", warn);

            // Session cost stays legible, not just a single end-of-session surprise (Sprint 49).
            var cost = _controller.SessionEstimatedCostUsd;
            _statCost.style.display = cost > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (cost > 0)
            {
                _statCost.text = "~" + AssistantCostTable.FormatCost(cost);
                _statCost.tooltip = "Estimated session spend at the current model's pricing (approximate).";
            }

            // Prompt-cache hit rate (Sprint 74): visible once any prompt tokens are served from cache.
            var hitRate = _controller.SessionCacheHitRate;
            _statCache.style.display = hitRate > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (hitRate > 0)
            {
                _statCache.text = $"{hitRate:P0} cached";
                _statCache.tooltip = "Share of prompt tokens served from the provider's prompt cache this session.";
            }

            // Reasoning-token share (Sprint 76): visible once any thinking tokens are billed.
            var reasoning = _controller.SessionReasoningTokens;
            _statReasoning.style.display = reasoning > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (reasoning > 0)
            {
                _statReasoning.text = $"{reasoning:N0} think";
                _statReasoning.tooltip = "Extended-reasoning tokens billed this session (they bill as output).";
            }

            RefreshCompactionNotice();
            RefreshAttachAvailability();
        }

        /// <summary>Shows the full stat detail as a menu — one legible line per figure (Sprint 91.1f).</summary>
        private void ShowStatDetail()
        {
            var menu = new GenericMenu();
            menu.AddDisabledItem(new GUIContent(_tokenEstimate.text + " in context (estimated)"));
            var cost = _controller.SessionEstimatedCostUsd;
            if (cost > 0)
                menu.AddDisabledItem(new GUIContent($"~{AssistantCostTable.FormatCost(cost)} estimated spend this session"));
            var hitRate = _controller.SessionCacheHitRate;
            if (hitRate > 0)
                menu.AddDisabledItem(new GUIContent($"{hitRate:P0} of prompt tokens served from cache"));
            var reasoning = _controller.SessionReasoningTokens;
            if (reasoning > 0)
                menu.AddDisabledItem(new GUIContent($"{reasoning:N0} extended-reasoning tokens billed"));
            menu.ShowAsContext();
        }

        /// <summary>Shows or hides the "context compacted" affordance based on the controller's last pass (Sprint 46).</summary>
        private void RefreshCompactionNotice()
        {
            var summarized = !string.IsNullOrEmpty(_controller.LastCompactionSummary);
            var digested = _controller.LastCompactionDigestedCount;
            var any = summarized || digested > 0;
            _compactionView.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            if (any)
                _compactionView.text = summarized ? "compacted ✓" : $"condensed {digested} ✓";
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

        // ---- Segmented action-mode control (Sprint 91.1g) -------------------------------------------

        private void BuildModeControl(VisualElement slot)
        {
            var row = new VisualElement();
            row.AddToClassList("chat-mode-seg");
            slot.Add(row);

            void AddSegment(AssistantActionMode mode, string label, string modifier, string tooltip)
            {
                Button button = null;
                button = new Button(() => SetMode(mode)) { text = label, tooltip = tooltip };
                button.AddToClassList("chat-mode-seg__btn");
                button.AddToClassList("chat-mode-seg__btn--" + modifier);
                row.Add(button);
                _modeButtons.Add((mode, button));
            }

            AddSegment(AssistantActionMode.Ask, "Ask", "ask",
                "Confirm every mutating tool call. Read-only tools always run.");
            AddSegment(AssistantActionMode.Auto, "Auto", "auto",
                "Run allowlisted undoable actions without prompting; irreversible steps still confirm.");
            AddSegment(AssistantActionMode.Plan, "Plan", "plan",
                "Approve a multi-step task once, then run its undoable steps under one whole-task undo; irreversible steps still confirm.");
            AddSegment(AssistantActionMode.AutoAll, "All", "all",
                "Run every allowlisted action unprompted, including irreversible ones (cannot be undone). Use with care.");

            // End-cap rounding is keyed on these classes because USS has no :first-child/:last-child — with
            // the pseudo-classes the rules simply never matched and the control rendered square-cornered.
            if (_modeButtons.Count > 0)
            {
                _modeButtons[0].button.AddToClassList("chat-mode-seg__btn--first");
                _modeButtons[_modeButtons.Count - 1].button.AddToClassList("chat-mode-seg__btn--last");
            }

            RefreshModeSelection(LoadActionMode());
        }

        private void SetMode(AssistantActionMode mode)
        {
            MolcaEditorPrefs.SetInt(ActionModePrefKey, (int)mode);
            if (_controller != null) _controller.ActionMode = mode;
            RefreshModeSelection(mode);
        }

        private void RefreshModeSelection(AssistantActionMode mode)
        {
            foreach (var (m, button) in _modeButtons)
                button.EnableInClassList("chat-mode-seg__btn--selected", m == mode);
        }

        // ---- Suggestion popup: @ mentions and / commands (Sprint 91.1c/d) ---------------------------

        /// <summary>
        /// Re-derives the popup from the current input: a trailing <c>@token</c> offers context sources and
        /// an asset search; a leading <c>/token</c> (with no space yet) offers the slash palette. Anything
        /// else hides the popup.
        /// </summary>
        private void RefreshSuggestions()
        {
            var text = _input.value ?? string.Empty;

            // Slash palette: the whole input is a single "/verb" token.
            if (text.StartsWith("/", StringComparison.Ordinal) && text.IndexOf(' ') < 0 && text.IndexOf('\n') < 0)
            {
                ShowSlashSuggestions(text.Substring(1));
                return;
            }

            // Mention: the token under the caret (approximated as the trailing token) starts with '@'.
            var token = TrailingToken(text);
            if (token != null && token.StartsWith("@", StringComparison.Ordinal))
            {
                ShowMentionSuggestions(token);
                return;
            }

            HideSuggestions();
        }

        /// <summary>The trailing whitespace-delimited token of <paramref name="text"/>, or null when empty/ends in whitespace.</summary>
        internal static string TrailingToken(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var last = text.Length - 1;
            if (char.IsWhiteSpace(text[last])) return null;
            var start = last;
            while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
            return text.Substring(start);
        }

        private void ShowSlashSuggestions(string query)
        {
            var commands = SlashCommands;
            if (commands == null || commands.Count == 0) { HideSuggestions(); return; }

            BeginSuggestions("/" + query);
            foreach (var command in commands)
            {
                if (!string.IsNullOrEmpty(query) && command.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var captured = command;
                AddSuggestion($"/{command.Name} — {command.Description}", () =>
                {
                    _input.value = string.Empty;
                    captured.Run?.Invoke();
                });
                if (_suggestRows.Count >= SuggestCap) break;
            }
            EndSuggestions();
        }

        private void ShowMentionSuggestions(string token)
        {
            var query = token.Substring(1);
            BeginSuggestions(token);

            void AddSource(string label, Func<AssistantContextItem> make)
            {
                if (!string.IsNullOrEmpty(query) && label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                AddSuggestion(label, () => ApplyMention(token, make()));
            }

            AddSource("Selection (live)", () => AssistantContextItem.ForSelection(true, null, "Selection (live)"));
            AddSource("Selection (snapshot)", () => AssistantContextItem.ForSelection(false, AssistantEditorContext.DescribeSelection(), "Selection"));
            AddSource("Active Scene", () => AssistantContextItem.ForActiveScene(ActiveSceneLabel()));
            AddSource("Framework Graph", AssistantContextItem.ForFrameworkGraph);
            AddSource("KG status", AssistantContextItem.ForKgStatus);

            // Asset search once the query is meaningful; name matches first, capped alongside the sources.
            if (query.Length >= 2)
            {
                foreach (var guid in AssetDatabase.FindAssets(query))
                {
                    if (_suggestRows.Count >= SuggestCap) break;
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    var capturedGuid = guid;
                    AddSuggestion($"{name}  ·  {path}", () =>
                        ApplyMention(token, AssistantContextItem.ForAsset(capturedGuid, name)));
                }
            }

            EndSuggestions();
        }

        private void ApplyMention(string token, AssistantContextItem item)
        {
            if (item != null) _controller.AddContext(item);

            // Strip the "@token" the user typed (it lives at the end of the input by construction).
            var text = _input.value ?? string.Empty;
            if (text.EndsWith(token, StringComparison.Ordinal))
                _input.value = text.Substring(0, text.Length - token.Length).TrimEnd() is var rest && rest.Length > 0 ? rest + " " : string.Empty;
            FocusInput();
            MoveCaretToEnd();
        }

        private static string ActiveSceneLabel()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? $"Scene: {scene.name}" : "Active Scene";
        }

        private void BeginSuggestions(string token)
        {
            _suggestToken = token;
            _suggestRows.Clear();
            if (_suggest == null)
            {
                _suggest = new VisualElement();
                _suggest.AddToClassList("chat-suggest");
                _suggest.style.position = Position.Absolute;
                _viewRoot.Add(_suggest);
            }
            _suggest.Clear();
        }

        private void AddSuggestion(string label, Action apply)
        {
            if (_suggestRows.Count >= SuggestCap) return;
            var row = new Label(label);
            row.AddToClassList("chat-suggest__row");
            var index = _suggestRows.Count;
            row.RegisterCallback<ClickEvent>(_ => ApplySuggestion(index));
            row.RegisterCallback<MouseEnterEvent>(_ => SelectSuggestion(index));
            _suggest.Add(row);
            _suggestRows.Add((row, apply));
        }

        private void EndSuggestions()
        {
            if (_suggestRows.Count == 0) { HideSuggestions(); return; }

            // Anchor the popup just above the composer card, matching its width.
            var composerBound = _composerRoot.layout;
            var rootBound = _viewRoot.layout;
            var local = _viewRoot.WorldToLocal(_composerRoot.worldBound.position);
            _suggest.style.left = local.x;
            _suggest.style.width = composerBound.width;
            _suggest.style.bottom = rootBound.height - local.y + 4;
            _suggest.style.display = DisplayStyle.Flex;
            SelectSuggestion(0);
        }

        private void HideSuggestions()
        {
            _suggestToken = null;
            _suggestIndex = -1;
            _suggestRows.Clear();
            if (_suggest != null) _suggest.style.display = DisplayStyle.None;
        }

        private bool SuggestionsVisible => _suggest != null && _suggest.style.display == DisplayStyle.Flex && _suggestRows.Count > 0;

        private void SelectSuggestion(int index)
        {
            _suggestIndex = Mathf.Clamp(index, 0, _suggestRows.Count - 1);
            for (var i = 0; i < _suggestRows.Count; i++)
                _suggestRows[i].row.EnableInClassList("chat-suggest__row--selected", i == _suggestIndex);
        }

        private void ApplySuggestion(int index)
        {
            if (index < 0 || index >= _suggestRows.Count) return;
            var apply = _suggestRows[index].apply;
            HideSuggestions();
            apply?.Invoke();
            UpdateTokenEstimate();
        }

        // ---- Input key handling ---------------------------------------------------------------------

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            // Popup navigation takes the keys first while visible.
            if (SuggestionsVisible)
            {
                switch (evt.keyCode)
                {
                    case KeyCode.UpArrow:
                        SelectSuggestion(_suggestIndex - 1);
                        evt.StopImmediatePropagation();
                        return;
                    case KeyCode.DownArrow:
                        SelectSuggestion(_suggestIndex + 1);
                        evt.StopImmediatePropagation();
                        return;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.Tab:
                        ApplySuggestion(_suggestIndex);
                        evt.StopImmediatePropagation();
                        return;
                    case KeyCode.Escape:
                        HideSuggestions();
                        evt.StopImmediatePropagation();
                        return;
                }
            }
            else if (evt.keyCode == KeyCode.Escape && _busy)
            {
                // Esc while a turn runs moves focus to Stop so a second keypress can cancel — without
                // making a stray Esc destructive by itself.
                _stop.Focus();
                evt.StopImmediatePropagation();
                return;
            }
            else if (evt.keyCode == KeyCode.UpArrow && TryNavigateHistory(older: true))
            {
                evt.StopImmediatePropagation();
                return;
            }
            else if (evt.keyCode == KeyCode.DownArrow && _historyIndex >= 0 && TryNavigateHistory(older: false))
            {
                evt.StopImmediatePropagation();
                return;
            }

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
