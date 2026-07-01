using System;
using System.Threading;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Reusable in-editor assistant chat panel as a <see cref="VisualElement"/>. Owns the controller, the
    /// transcript/composer/asset-picker collaborators, and the prompt bar. Hosted by both the standalone
    /// <see cref="AssistantChatWindow"/> and the Molca Hub Assistant workspace (Sprint 26.10).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Assistant/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// The conversation is rendered by <see cref="AssistantTranscriptView"/>, the composer card by
    /// <see cref="AssistantComposer"/>, and the pinned-asset picker by <see cref="AssistantAssetPicker"/>.
    /// Lifecycle (cancelling an in-flight turn, disposing the asset picker, unsubscribing the controller)
    /// is keyed on <see cref="DetachFromPanelEvent"/> so only the visible host runs a controller — there is
    /// never duplicate long-running controller state across the window and the Hub.
    /// </remarks>
    public sealed class AssistantChatView : VisualElement
    {
        private const string AssetDir = "Packages/com.molca.core/Editor/MCP/Assistant/";

        private readonly AssistantSettings _settings;
        private readonly AssistantChatController _controller;
        private readonly AssistantAssetPicker _assetPicker = new AssistantAssetPicker();
        private readonly Action<string> _notify;
        private CancellationTokenSource _cts;

        private AssistantTranscriptView _transcript;
        private AssistantComposer _composer;
        private AssistantModelPicker _modelPicker;

        private Label _status;
        private Label _lastQuestionBanner;
        private VisualElement _toolProgressRow;
        private Label _toolProgressLabel;
        private ProgressBar _toolProgressBar;
        private VisualElement _promptBar;
        private Label _promptBarQuestion;
        private VisualElement _promptBarOptions;
        private TextField _promptBarInput;

        /// <summary>Creates the chat view.</summary>
        /// <param name="notify">Optional transient-notification sink (e.g. the host window's
        /// <c>ShowNotification</c>); ignored when null.</param>
        public AssistantChatView(Action<string> notify = null)
        {
            _notify = notify;
            _cts = new CancellationTokenSource();

            _settings = AssistantSettings.GetOrCreateSettings();
            _controller = new AssistantChatController(_settings) { ActionMode = AssistantComposer.LoadActionMode() };
            _controller.Changed += RefreshTranscript;
            // Plan-mode "Edit" refills the composer with the proposed plan so the user can revise (Sprint 48).
            _controller.PlanEditRequested += BeginEdit;

            AddToClassList("chat-root");
            style.flexGrow = 1;

            // Bring the shared design tokens (Sprint 27.1) onto this root before the chat sheet, so the
            // chat USS can resolve `--molca-*` instead of redefining the overlapping surface/link hex.
            MolcaEditorUi.Apply(this);

            var stylesheet = LoadAsset<StyleSheet>("AssistantChatWindow.uss");
            if (stylesheet != null) styleSheets.Add(stylesheet);

            var layout = LoadAsset<VisualTreeAsset>("AssistantChatWindow.uxml");
            if (layout != null) layout.CloneTree(this);

            _status = this.Q<Label>("status");
            _lastQuestionBanner = this.Q<Label>("last-question-banner");
            BuildToolProgressRow();

            // Primary header actions (decluttered, Sprint 35): Sessions switcher · New chat · overflow
            // (the copy/clear actions) · Settings. The copy actions moved behind the ⋯ menu to make room.
            var header = this.Q<VisualElement>("header");
            header.Add(CreateIconButton(ShowSessionsMenu, "d_UnityEditor.VersionControl", "Chat sessions"));
            header.Add(CreateIconButton(() => _controller.NewChat(), "d_Toolbar Plus", "New chat"));
            header.Add(CreateIconButton(ShowOverflowMenu, "_Menu", "More actions"));
            header.Add(CreateIconButton(() => Molca.Editor.Hub.MolcaHubWindow.Open(Molca.Editor.Hub.MolcaHubWorkspace.Settings), "d_SettingsIcon", "Open Molca settings"));

            _transcript = new AssistantTranscriptView(
                this.Q<ScrollView>("scroll"), _controller,
                onRetry: () => RetryLast(),
                onEdit: BeginEdit,
                onRefresh: RefreshTranscript,
                onNotify: message => _notify?.Invoke(message),
                onContinue: () => SendText("continue"));

            _composer = new AssistantComposer(this, _controller,
                onSend: SendCurrent, onStop: StopCurrent, onAddContext: ShowAddContextMenu);

            // In-window provider + model switcher (Sprint 71). Sits at the top of the composer card so
            // switching backends never requires a Hub round-trip; it applies to the next turn and persists.
            _modelPicker = new AssistantModelPicker(_settings, _controller, RefreshTranscript);
            var composerRoot = this.Q<VisualElement>("composer");
            composerRoot?.Insert(0, _modelPicker.Root);

            _promptBar = this.Q<VisualElement>("prompt-bar");
            _promptBarQuestion = this.Q<Label>("prompt-bar-question");
            _promptBarOptions = this.Q<VisualElement>("prompt-bar-options");
            _promptBarInput = this.Q<TextField>("prompt-bar-input");
            this.Q<Button>("prompt-bar-answer").clicked += AnswerFromPromptBar;
            _promptBarInput.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    AnswerFromPromptBar();
                }
            }, TrickleDown.TrickleDown);

            // DetachFromPanelEvent fires on transient reparenting too (docking, layout rebuilds, domain
            // reloads), so re-arm on re-attach rather than staying torn down — otherwise the reused view
            // has a null _cts (NRE on Send) and a dropped Changed subscription (stale transcript).
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                _cts ??= new CancellationTokenSource();
                if (_controller != null)
                {
                    _controller.Changed -= RefreshTranscript;
                    _controller.Changed += RefreshTranscript;
                }
                RefreshTranscript();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => Dispose());

            RefreshTranscript();
        }

        /// <summary>Cancels the in-flight turn and releases collaborators. Idempotent.</summary>
        private void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _assetPicker.Dispose();
            if (_controller != null)
            {
                _controller.Changed -= RefreshTranscript;
                _controller.PlanEditRequested -= BeginEdit;
            }
        }

        private static T LoadAsset<T>(string fileName) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(AssetDir + fileName);

        // ---- Context bar actions --------------------------------------------------------------------

        private void ShowAddContextMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Current Selection (live)"), false,
                () => _controller.AddContext(AssistantContextItem.ForSelection(true, null, "Selection (live)")));
            menu.AddItem(new GUIContent("Current Selection (snapshot)"), false,
                () => _controller.AddContext(AssistantContextItem.ForSelection(false, AssistantEditorContext.DescribeSelection(), SelectionLabel())));
            menu.AddItem(new GUIContent("Active Scene"), false,
                () => _controller.AddContext(AssistantContextItem.ForActiveScene(ActiveSceneLabel())));
            menu.AddItem(new GUIContent("Framework Graph"), false,
                () => _controller.AddContext(AssistantContextItem.ForFrameworkGraph()));
            menu.AddItem(new GUIContent("KG status"), false,
                () => _controller.AddContext(AssistantContextItem.ForKgStatus()));
            menu.AddSeparator(string.Empty);
            var currentAsset = CurrentProjectAssetSelection();
            if (currentAsset != null)
                menu.AddItem(new GUIContent("Current Project Asset"), false, () => PinAsset(currentAsset));
            else
                menu.AddDisabledItem(new GUIContent("Current Project Asset"));
            menu.AddItem(new GUIContent("Pick Asset…"), false, () => _assetPicker.Pick(PinAsset));
            menu.ShowAsContext();
        }

        private void PinAsset(UnityEngine.Object obj)
        {
            var item = CreateContextForObject(obj);
            if (item != null)
                _controller.AddContext(item);
        }

        /// <summary>
        /// Creates a pinned assistant context item for a Unity object, preferring stable AssetDatabase
        /// GUIDs for project assets and falling back to a selection snapshot for scene objects.
        /// </summary>
        public static AssistantContextItem CreateContextForObject(UnityEngine.Object obj)
        {
            if (obj == null) return null;

            var path = AssetDatabase.GetAssetPath(obj);
            var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid)
                ? AssistantContextItem.ForSelection(false, $"Scene object: {obj.name} ({obj.GetType().Name})", obj.name)
                : AssistantContextItem.ForAsset(guid, obj.name);
        }

        private static UnityEngine.Object CurrentProjectAssetSelection()
        {
            var active = Selection.activeObject;
            if (active == null) return null;

            var path = AssetDatabase.GetAssetPath(active);
            return string.IsNullOrEmpty(path) ? null : active;
        }

        private static string SelectionLabel()
        {
            var n = Selection.objects?.Length ?? 0;
            return n == 1 ? $"Selection: {Selection.activeObject?.name}" : $"Selection ({n})";
        }

        private static string ActiveSceneLabel()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? $"Scene: {scene.name}" : "Active Scene";
        }

        // ---- Generation controls --------------------------------------------------------------------

        private void SendCurrent()
        {
            if (_controller == null || _controller.IsBusy) return;
            var text = _composer.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            _composer.Text = string.Empty;
            SendText(text);
        }

        private async void SendText(string text)
        {
            if (_controller == null || _controller.IsBusy || string.IsNullOrWhiteSpace(text)) return;
            _cts ??= new CancellationTokenSource();
            try { await _controller.SendAsync(text, _cts.Token); }
            catch (OperationCanceledException) { /* Stop cancelled the turn — not an error. */ }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private void StopCurrent()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }

        private async void RetryLast(string editedText = null)
        {
            if (_controller == null || _controller.IsBusy) return;
            _cts ??= new CancellationTokenSource();
            try { await _controller.RetryLastAsync(_cts.Token, editedText); }
            catch (OperationCanceledException) { /* Stop cancelled the turn — not an error. */ }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private void BeginEdit(string text)
        {
            _composer.Text = text;
            _composer.FocusInput();
        }

        // ---- Refresh + header actions ---------------------------------------------------------------

        private void RefreshTranscript()
        {
            if (_transcript == null) return;

            _transcript.Render();
            _composer.RebuildContextChips();

            var status = _settings.GetStatus(out var message);
            var busy = _controller.IsBusy;
            // The provider/model now live in the in-window picker (Sprint 71), so the header shows the chat
            // title (not the redundant model line) prefixed by the config status dot; the full ready/model
            // detail is preserved on the tooltip. Prefer the LLM-generated session title, fall back to the
            // derived first-message title, then "New chat" until the chat is auto-named.
            var title = !string.IsNullOrWhiteSpace(_controller.CurrentSessionTitle)
                ? _controller.CurrentSessionTitle
                : AssistantSessionLibrary.DeriveTitle(_controller.Transcript);
            if (string.IsNullOrEmpty(title)) title = "New chat";
            _status.text = busy ? "Thinking…" : $"{StatusIcon(status)} {title}";
            _status.tooltip = message;
            _composer.SetBusy(busy);
            _modelPicker?.SetBusy(busy);
            _composer.UpdateTokenEstimate();
            UpdateLastQuestionBanner(busy);
            UpdateToolProgress(busy);
            UpdatePromptBar();
        }

        /// <summary>
        /// Builds the live tool-progress row (a status label + bar) and inserts it directly beneath the
        /// "last question" banner. Hidden until a running tool reports progress (build/deploy).
        /// </summary>
        private void BuildToolProgressRow()
        {
            _toolProgressRow = new VisualElement { name = "tool-progress" };
            _toolProgressRow.AddToClassList("chat-tool-progress");
            _toolProgressRow.style.display = DisplayStyle.None;

            _toolProgressLabel = new Label { name = "tool-progress-label" };
            _toolProgressLabel.AddToClassList("chat-tool-progress__label");
            _toolProgressRow.Add(_toolProgressLabel);

            _toolProgressBar = new ProgressBar { name = "tool-progress-bar" };
            _toolProgressBar.AddToClassList("chat-tool-progress__bar");
            _toolProgressRow.Add(_toolProgressBar);

            var parent = _lastQuestionBanner?.parent;
            if (parent != null)
                parent.Insert(parent.IndexOf(_lastQuestionBanner) + 1, _toolProgressRow);
            else
                Add(_toolProgressRow);
        }

        /// <summary>
        /// Shows or hides the live tool-progress row from the controller's latest report. A reported
        /// fraction drives the bar; indeterminate work (no fraction) shows a pulsing-style placeholder.
        /// </summary>
        private void UpdateToolProgress(bool busy)
        {
            if (_toolProgressRow == null) return;

            var progress = busy ? _controller.ActiveToolProgress : null;
            var toolName = busy ? _controller.ActiveToolName : null;

            // Show the row whenever a tool is running — not only when it reports progress. Most tools never
            // call McpProgress.Report, so without this the indicator only ever appeared for build/deploy/kg.
            var show = progress.HasValue || !string.IsNullOrEmpty(toolName);
            _toolProgressRow.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            if (progress.HasValue)
            {
                var report = progress.Value;
                _toolProgressLabel.text = string.IsNullOrEmpty(toolName)
                    ? report.Message
                    : $"{toolName}  ·  {report.Message}";

                if (report.Fraction.HasValue)
                {
                    _toolProgressBar.value = report.Fraction.Value * 100f;
                    _toolProgressBar.title = $"{Mathf.RoundToInt(report.Fraction.Value * 100f)}%";
                }
                else
                {
                    // Indeterminate: no meaningful fill, just a working indicator.
                    _toolProgressBar.value = 0f;
                    _toolProgressBar.title = "working…";
                }
            }
            else
            {
                // A running tool that doesn't report progress — an indeterminate "Running <tool>…" indicator.
                _toolProgressLabel.text = $"Running {toolName}…";
                _toolProgressBar.value = 0f;
                _toolProgressBar.title = "working…";
            }
        }

        private void UpdatePromptBar()
        {
            if (_promptBar == null) return;

            var prompt = _controller.IsAwaitingUser ? _controller.PendingPrompt : null;
            _promptBar.style.display = prompt != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (prompt == null) return;

            _promptBarQuestion.text = prompt.Question;
            _promptBarOptions.Clear();
            foreach (var option in prompt.Options)
            {
                var label = option;
                var button = new Button(() => _controller.AnswerPending(label)) { text = label };
                button.AddToClassList("chat-prompt-option");
                _promptBarOptions.Add(button);
            }
            _promptBarInput.value = string.Empty;
        }

        private void AnswerFromPromptBar()
        {
            if (!_controller.IsAwaitingUser) return;
            var answer = _promptBarInput.value;
            if (!string.IsNullOrWhiteSpace(answer)) _controller.AnswerPending(answer);
        }

        private void UpdateLastQuestionBanner(bool busy)
        {
            if (_lastQuestionBanner == null) return;
            var question = _controller.LastUserText;
            var show = busy && !string.IsNullOrWhiteSpace(question);
            _lastQuestionBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                _lastQuestionBanner.text = "↳ " + question.Replace("\n", " ");
                _lastQuestionBanner.tooltip = question;
            }
        }

        private static Button CreateIconButton(Action action, string iconName, string tooltip)
        {
            var button = new Button(action) { tooltip = tooltip };
            button.AddToClassList("chat-icon-button");
            var icon = EditorGUIUtility.IconContent(iconName, tooltip)?.image as Texture2D;
            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("chat-icon-button__image");
                image.pickingMode = PickingMode.Ignore;
                button.Add(image);
            }
            else
            {
                button.text = tooltip.Length > 0 ? tooltip.Substring(0, 1) : "?";
            }
            return button;
        }

        private void CopyLastAnswer() => EditorGUIUtility.systemCopyBuffer = AssistantTranscriptFormatter.LastAssistantAnswer(_controller.Transcript);
        private void CopyTranscript() => EditorGUIUtility.systemCopyBuffer = AssistantTranscriptFormatter.ToPlainText(_controller.Transcript);
        private void CopyDetailedTranscript() => EditorGUIUtility.systemCopyBuffer = AssistantTranscriptFormatter.ToPlainText(_controller.Transcript, includeToolPayloads: true);

        /// <summary>Sessions switcher (Sprint 35): New chat, the saved conversations, and a delete submenu.</summary>
        private void ShowSessionsMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("New chat"), false, () => _controller.NewChat());
            menu.AddSeparator(string.Empty);

            var sessions = _controller.ListSessions();
            var current = _controller.CurrentSessionId;
            if (sessions.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No saved chats"));
            }
            else
            {
                foreach (var s in sessions)
                {
                    var id = s.Id;
                    var tokens = s.InputTokens + s.OutputTokens;
                    // Show a per-session token total so spend is visible at a glance (Sprint 49).
                    var tokenSuffix = tokens > 0 ? $"  ·  {tokens / 1000.0:0.#}k tok" : string.Empty;
                    var label = $"{SanitizeMenuLabel(s.Title)}  ·  {RelativeTime(s.UpdatedAt)}{tokenSuffix}";
                    menu.AddItem(new GUIContent(label), id == current, () => _controller.SwitchToSession(id));
                }
                menu.AddSeparator(string.Empty);
                foreach (var s in sessions)
                {
                    var id = s.Id;
                    var title = s.Title;
                    menu.AddItem(new GUIContent($"Delete/{SanitizeMenuLabel(title)}"), false, () => ConfirmDeleteSession(id, title));
                }
            }
            menu.ShowAsContext();
        }

        /// <summary>Overflow menu (Sprint 35): the copy actions plus Clear conversation, moved off the header.</summary>
        private void ShowOverflowMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy last answer"), false, CopyLastAnswer);
            menu.AddItem(new GUIContent("Copy transcript"), false, CopyTranscript);
            menu.AddItem(new GUIContent("Copy transcript (with tool details)"), false, CopyDetailedTranscript);
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Clear conversation"), false, () => _controller.Reset());
            menu.ShowAsContext();
        }

        private void ConfirmDeleteSession(string id, string title)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "this chat" : $"\"{title}\"";
            if (EditorUtility.DisplayDialog("Delete chat", $"Delete {name}? This cannot be undone.", "Delete", "Cancel"))
                _controller.DeleteSession(id);
        }

        /// <summary>GenericMenu treats '/' as a submenu separator, so flatten it out of titles.</summary>
        private static string SanitizeMenuLabel(string s)
            => string.IsNullOrWhiteSpace(s) ? "Untitled" : s.Replace('/', ' ').Replace('\\', ' ');

        /// <summary>Compact relative time for the session list (e.g. "5m ago", "2d ago", "Mar 3").</summary>
        private static string RelativeTime(DateTime utc)
        {
            if (utc == DateTime.MinValue) return string.Empty;
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
            return utc.ToLocalTime().ToString("MMM d");
        }

        private static string StatusIcon(AssistantConfigStatus status) => status switch
        {
            AssistantConfigStatus.Configured => "●",
            AssistantConfigStatus.Misconfigured => "●",
            _ => "○"
        };
    }
}
