using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>A single entry in the visible transcript.</summary>
    public enum ChatTurnKind { User, Assistant, Tool, Error, Prompt, Work }

    /// <summary>Structured details for a tool call shown in the visible transcript.</summary>
    public sealed class ChatToolSummary
    {
        /// <summary>The requested tool name.</summary>
        public string Name { get; }

        /// <summary>The raw JSON arguments supplied by the model.</summary>
        public string ArgumentsJson { get; }

        /// <summary>The raw tool result payload.</summary>
        public string ResultContent { get; }

        /// <summary>True when the tool returned an error result.</summary>
        public bool IsError { get; }

        /// <summary>The editor state required by the tool.</summary>
        public string Mode { get; }

        /// <summary>Whether the tool is read-only or mutating.</summary>
        public string Kind { get; }

        /// <summary>How this tool can be reverted (Irreversible / FileSnapshot / UnityUndo).</summary>
        public string Reversibility { get; }

        /// <summary>
        /// The <see cref="Molca.Editor.Mcp.McpUndoStack"/> entry id created when a
        /// <see cref="Molca.Editor.Mcp.McpToolReversibility.FileSnapshot"/> action ran, or <c>null</c>.
        /// Used by the chat window to offer a per-action Undo button.
        /// </summary>
        public string UndoEntryId { get; }

        /// <summary>
        /// The Unity Undo group index this action's changes occupy, for
        /// <see cref="Molca.Editor.Mcp.McpToolReversibility.UnityUndo"/> actions; <c>-1</c> otherwise.
        /// The chat window reverts to it via <see cref="UnityEditor.Undo.RevertAllDownToGroup"/>.
        /// </summary>
        public int UndoGroup { get; }

        /// <summary>Creates a transcript tool summary.</summary>
        public ChatToolSummary(string name, string argumentsJson, string resultContent, bool isError, string mode, string kind,
            string reversibility = "Unknown", string undoEntryId = null, int undoGroup = -1)
        {
            Name = name ?? string.Empty;
            ArgumentsJson = argumentsJson ?? "{}";
            ResultContent = resultContent ?? string.Empty;
            IsError = isError;
            Mode = mode ?? "Unknown";
            Kind = kind ?? "Unknown";
            Reversibility = reversibility ?? "Unknown";
            UndoEntryId = undoEntryId;
            UndoGroup = undoGroup;
        }
    }

    /// <summary>One visible transcript line (chat bubble or a compact tool-activity note).</summary>
    public sealed class ChatTurn
    {
        /// <summary>What kind of line this is.</summary>
        public ChatTurnKind Kind { get; }
        /// <summary>The rendered text.</summary>
        public string Text { get; }
        /// <summary>Structured tool-call detail, set only on <see cref="ChatTurnKind.Tool"/> turns.</summary>
        public ChatToolSummary ToolSummary { get; }
        /// <summary>Structured tool-call details, set only on <see cref="ChatTurnKind.Tool"/> turns.</summary>
        public IReadOnlyList<ChatToolSummary> ToolSummaries { get; }

        /// <summary>Intermediate assistant notes collapsed into a completed work summary.</summary>
        public IReadOnlyList<string> WorkItems { get; }

        /// <summary>
        /// Index into the controller's LLM history of the message this turn corresponds to, or <c>-1</c>
        /// when not anchored (Sprint 25.8). Set on real user-prompt turns so retry/edit can trim history
        /// precisely instead of scanning for "the last user message".
        /// </summary>
        public int HistoryIndex { get; }

        /// <summary>
        /// The answer the user gave to a <see cref="ChatTurnKind.Prompt"/> turn, or <c>null</c> until
        /// answered (Sprint 25.7). Mutable because the prompt turn is recorded before the user responds.
        /// </summary>
        public string PromptAnswer { get; set; }

        /// <summary>
        /// True on the tool-round-cap notice (Sprint 25.8), so the view can offer a one-click "Continue"
        /// instead of asking the user to type "continue".
        /// </summary>
        public bool CanContinue { get; set; }

        /// <summary>
        /// True when a <see cref="ChatTurnKind.Prompt"/> turn is an action-confirmation (Run/Cancel) rather
        /// than a genuine <c>molca_ask_user</c> question. The view collapses an answered confirmation to a
        /// one-line outcome, since the following Work row already lists what ran; real questions stay full.
        /// </summary>
        public bool IsConfirmation { get; set; }

        /// <summary>Creates a transcript turn.</summary>
        public ChatTurn(ChatTurnKind kind, string text, ChatToolSummary toolSummary = null)
            : this(kind, text, toolSummary == null ? null : new[] { toolSummary })
        {
        }

        /// <summary>Creates a transcript turn with one or more tool summaries and an optional history anchor.</summary>
        public ChatTurn(ChatTurnKind kind, string text, IReadOnlyList<ChatToolSummary> toolSummaries, int historyIndex = -1)
            : this(kind, text, toolSummaries, historyIndex, null)
        {
        }

        /// <summary>Creates a transcript turn with tools, an optional history anchor, and collapsed work notes.</summary>
        public ChatTurn(ChatTurnKind kind, string text, IReadOnlyList<ChatToolSummary> toolSummaries, int historyIndex,
            IReadOnlyList<string> workItems)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            ToolSummaries = toolSummaries ?? Array.Empty<ChatToolSummary>();
            ToolSummary = ToolSummaries.Count > 0 ? ToolSummaries[0] : null;
            HistoryIndex = historyIndex;
            WorkItems = workItems ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Drives the assistant conversation (Sprint 16.3): maintains history, runs the model → tool →
    /// result → model loop against the read-only registry tools, and exposes a visible transcript. Lives
    /// on the Unity main thread (the chat window drives it), so tool delegates run safely inline.
    /// </summary>
    public sealed class AssistantChatController
    {
        /// <summary>
        /// Fallback cap on model→tool→model rounds per turn when no setting is supplied. A "round" is one
        /// provider request; a model that calls one tool per round (common) hits roughly this many tool
        /// calls. Multi-step authoring (remove → add → set fields → add auxiliaries, repeated) needs
        /// headroom, so the real cap comes from <see cref="AssistantSettings.MaxToolRounds"/>.
        /// </summary>
        private const int DefaultMaxToolRounds = 25;

        /// <summary>Package path of the authored system prompt (Sprint 25.8) — editable without a recompile.</summary>
        private const string SystemPromptAssetPath =
            "Packages/com.molca.core/Editor/MCP/Assistant/AssistantSystemPrompt.txt";

        /// <summary>
        /// Fallback used only if the authored prompt asset cannot be loaded. Kept in sync with
        /// <c>AssistantSystemPrompt.txt</c> (the authoritative, runtime-editable copy) so the assistant
        /// behaves identically in the rare degraded case.
        /// </summary>
        private const string FallbackSystemPrompt =
            "You are the Molca in-editor assistant: a Unity engineer working inside a project built on the " +
            "Molca framework. You are not a general-purpose chatbot. " +
            "Scope: only help with work that touches this Unity project or the Molca framework — live " +
            "editor state, scenes, assets, components, scripts, builds, project settings, framework code, " +
            "and pinned context. Refuse anything that does not need the project (trivia, puzzles, general " +
            "creative writing, unrelated game or story brainstorming, world knowledge); keep the refusal " +
            "to one sentence and redirect to the project. Judge scope by the whole conversation, not the " +
            "latest message alone — a short reply like \"yes\" or \"the second one\" continues the current " +
            "task and is in scope. Reply in the user's language. " +
            "Grounding: whenever a tool can give accurate live state, call it and answer from the result " +
            "instead of guessing — molca_unity_* for scene/selection/asset/component discovery, and the " +
            "read-only tools for status, Doctor findings, sequence validation, the subsystem/service " +
            "graph, scene Ref Ids, and build info. Before adding/removing components or setting serialized " +
            "fields, inspect with molca_unity_component_types / _gameobject_components / _component_fields. " +
            "Codebase questions (\"how does X work\", \"what depends on Y\", \"where is Z configured\"): " +
            "first call molca_kg_status; if a graph exists use molca_kg_query (or molca_kg_path / " +
            "molca_kg_explain); if none exists, say so and suggest building it from the MCP settings tab " +
            "rather than inventing an answer. Seed queries with concrete class or file names, not vague " +
            "phrases; results list source as src=path:Lnn, so read that file with molca_read_source and " +
            "explain from the code. Do not repeat near-identical queries or loop. " +
            "Actions: use action tools only when available and the user asks; the editor handles " +
            "confirmation. Prefer Unity-Undo actions for reversible scene-object changes; treat scene " +
            "open/save as irreversible. When several tool calls are independent, issue them in one turn; " +
            "serialize only when a later call needs an earlier result. " +
            "When blocked by a decision that materially changes the work or a missing/ambiguous detail, " +
            "you MUST call molca_ask_user with the choices as short option labels and the question in the " +
            "question field, writing little other text; only fall back to a plain-text question if the " +
            "tool is unavailable. " +
            "Answering: lead with the direct answer in one or two sentences, then bullets for evidence and " +
            "next actions when helpful. Do not narrate hidden reasoning or announce tool calls; write tool " +
            "names plainly. Use a context link only when useful: [pin live selection](molca-context://selection-live), " +
            "[pin selection snapshot](molca-context://selection-snapshot), [pin active scene](molca-context://active-scene), " +
            "[pin framework graph](molca-context://framework-graph), [pin KG status](molca-context://kg-status), " +
            "[pin asset](molca-context://asset/GUID).";

        // Lazily loaded so prompt tuning means editing the .txt asset, not recompiling. Cached for the
        // session; falls back to the embedded prompt if the asset cannot be loaded.
        private static string _systemPrompt;
        private static string SystemPrompt => _systemPrompt ??= LoadSystemPrompt();

        private static string LoadSystemPrompt()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(SystemPromptAssetPath);
            var text = asset != null ? asset.text?.Trim() : null;
            return string.IsNullOrEmpty(text) ? FallbackSystemPrompt : text;
        }

        private readonly AssistantSettings _settings;
        private readonly List<LlmMessage> _history = new List<LlmMessage>();
        private readonly List<ChatTurn> _transcript = new List<ChatTurn>();
        private readonly List<AssistantContextItem> _pinnedContext = new List<AssistantContextItem>();

        // The in-flight molca_ask_user prompt (Sprint 25.6): non-null while the turn is paused waiting for
        // the user to choose. Completed by AnswerPending or cancelled with the turn.
        private AwaitableCompletionSource<string> _pendingPromptSource;
        private ChatTurn _pendingPromptTurn;

        // Latest vendor-reported prompt token count (Sprint 25.8); 0 until a turn completes non-streaming.
        private int _lastReportedPromptTokens;

        // The session this conversation is persisted under (Sprint 35). Assigned on construction (restored,
        // migrated, or freshly minted) and rotated by NewChat / SwitchToSession.
        private string _sessionId;

        // The current session's stored title. Empty until the LLM names the chat after its first exchange
        // (see GenerateTitleAsync); restored from the session header on load/switch so an already-named
        // session is never re-titled. Threaded into Persist so autosaves don't clobber it.
        private string _sessionTitle = string.Empty;

        /// <summary>Raised on the main thread whenever the transcript or busy state changes.</summary>
        public event Action Changed;

        /// <summary>Raised when the set of saved sessions changes (new/switched/deleted), so the header refreshes.</summary>
        public event Action SessionsChanged;

        /// <summary>Id of the session this conversation is currently saved under (Sprint 35).</summary>
        public string CurrentSessionId => _sessionId;

        /// <summary>
        /// The active session's title — an LLM-generated name once the chat has had its first exchange,
        /// otherwise empty. Used by the header so the displayed title matches what the switcher lists.
        /// </summary>
        public string CurrentSessionTitle => _sessionTitle;

        /// <summary>True while a turn is in flight.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>
        /// Partial assistant text accumulated during a streaming turn (Sprint 24.7). Empty when not
        /// streaming; the window renders it as a live assistant row while <see cref="IsBusy"/>.
        /// </summary>
        public string StreamingText { get; private set; } = string.Empty;

        /// <summary>The context items pinned for the next turn (Sprint 24.3).</summary>
        public IReadOnlyList<AssistantContextItem> PinnedContext => _pinnedContext;

        /// <summary>The raw text of the most recent user turn, or empty (used by retry, Sprint 24.6).</summary>
        public string LastUserText
        {
            get
            {
                for (var i = _transcript.Count - 1; i >= 0; i--)
                    if (_transcript[i].Kind == ChatTurnKind.User)
                        return _transcript[i].Text;
                return string.Empty;
            }
        }

        /// <summary>
        /// How mutating (Action) tool calls are authorized. <see cref="AssistantActionMode.Ask"/>
        /// (default) prompts before each one; <see cref="AssistantActionMode.Auto"/> runs allowlisted
        /// actions without prompting. Read-only tools are unaffected.
        /// </summary>
        public AssistantActionMode ActionMode { get; set; } = AssistantActionMode.Ask;

        /// <summary>The visible transcript.</summary>
        public IReadOnlyList<ChatTurn> Transcript => _transcript;

        /// <summary>
        /// The decision the model is currently asking the user to make via <c>molca_ask_user</c>, or
        /// <c>null</c> when no prompt is outstanding (Sprint 25.6). The window renders its choices and
        /// resolves it via <see cref="AnswerPending"/>.
        /// </summary>
        public AssistantUserPrompt PendingPrompt { get; private set; }

        /// <summary>True while a turn is paused waiting for the user to answer a <see cref="PendingPrompt"/>.</summary>
        public bool IsAwaitingUser => _pendingPromptSource != null;

        /// <summary>
        /// The tool currently executing, or <c>null</c> when none is running. Set around each tool call so
        /// the view can label the live progress row; cleared when the call returns.
        /// </summary>
        public string ActiveToolName { get; private set; }

        /// <summary>
        /// The latest <see cref="McpProgressReport"/> emitted by the running tool, or <c>null</c> when none
        /// has been reported. Drives the native progress row for long-running tools (build/deploy).
        /// </summary>
        public McpProgressReport? ActiveToolProgress { get; private set; }

        /// <summary>The system prompt sent to the provider.</summary>
        public static string SystemPromptText => SystemPrompt;

        /// <summary>Creates a controller bound to the given settings, restoring the most recent session.</summary>
        public AssistantChatController(AssistantSettings settings)
        {
            _settings = settings;

            // Migrate the legacy single-file session into the library on first run, then open the most
            // recently updated session. If there are none, start a fresh (unsaved-until-first-turn) session.
            AssistantSessionLibrary.MigrateLegacyIfNeeded();
            var mostRecent = AssistantSessionLibrary.ListSessions().FirstOrDefault();
            if (mostRecent != null &&
                AssistantSessionLibrary.TryLoad(mostRecent.Id, out var turns, out var history, out var context, out var meta))
            {
                _sessionId = mostRecent.Id;
                _sessionTitle = meta?.Title ?? string.Empty;
                _transcript.AddRange(turns);
                _history.AddRange(history);
                _pinnedContext.AddRange(context);
            }
            else
            {
                _sessionId = AssistantSessionLibrary.NewId();
            }
        }

        /// <summary>Clears the conversation history and transcript, but keeps the pinned context.</summary>
        public void Reset()
        {
            _history.Clear();
            _transcript.Clear();
            _sessionTitle = string.Empty;
            _lastReportedPromptTokens = 0;
            // Persist skips a fully-empty session, so drop its file outright to avoid restoring stale
            // content on reload; if pinned context remains, Persist still saves that.
            if (_pinnedContext.Count == 0 && !string.IsNullOrEmpty(_sessionId))
                AssistantSessionLibrary.Delete(_sessionId);
            else
                Persist();
            Changed?.Invoke();
            SessionsChanged?.Invoke();
        }

        /// <summary>
        /// Starts a brand-new chat under a fresh session id, preserving the previous conversation in the
        /// library (Sprint 35 — previously this discarded history). No-op while a turn is in flight.
        /// </summary>
        public void NewChat()
        {
            if (IsBusy) return;
            _history.Clear();
            _transcript.Clear();
            _pinnedContext.Clear();
            _sessionTitle = string.Empty;
            _lastReportedPromptTokens = 0;
            _sessionId = AssistantSessionLibrary.NewId();
            Changed?.Invoke();
            SessionsChanged?.Invoke();
        }

        /// <summary>The metadata of every saved session, most-recently-updated first (Sprint 35).</summary>
        public IReadOnlyList<SessionMeta> ListSessions() => AssistantSessionLibrary.ListSessions();

        /// <summary>
        /// Switches the active conversation to a saved session (Sprint 35). No-op while a turn is in flight
        /// or when already on that session. Loads its transcript/history/pinned context and notifies the view.
        /// </summary>
        public void SwitchToSession(string id)
        {
            if (IsBusy || string.IsNullOrEmpty(id) || id == _sessionId) return;
            if (!AssistantSessionLibrary.TryLoad(id, out var turns, out var history, out var context, out var meta))
                return;

            // Persist any in-progress (unsaved) edits to the current session before leaving it.
            Persist();

            _history.Clear();
            _transcript.Clear();
            _pinnedContext.Clear();
            _lastReportedPromptTokens = 0;

            _sessionId = id;
            _sessionTitle = meta?.Title ?? string.Empty;
            _transcript.AddRange(turns);
            _history.AddRange(history);
            _pinnedContext.AddRange(context);

            Changed?.Invoke();
            SessionsChanged?.Invoke();
        }

        /// <summary>
        /// Deletes a saved session (Sprint 35). If it is the active one, falls back to the next most-recent
        /// session, or a fresh empty chat when none remain. No-op while a turn is in flight.
        /// </summary>
        public void DeleteSession(string id)
        {
            if (IsBusy || string.IsNullOrEmpty(id)) return;
            AssistantSessionLibrary.Delete(id);

            if (id == _sessionId)
            {
                var next = AssistantSessionLibrary.ListSessions().FirstOrDefault();
                if (next != null &&
                    AssistantSessionLibrary.TryLoad(next.Id, out var turns, out var history, out var context, out var meta))
                {
                    _sessionId = next.Id;
                    _sessionTitle = meta?.Title ?? string.Empty;
                    _history.Clear(); _history.AddRange(history);
                    _transcript.Clear(); _transcript.AddRange(turns);
                    _pinnedContext.Clear(); _pinnedContext.AddRange(context);
                }
                else
                {
                    _history.Clear();
                    _transcript.Clear();
                    _pinnedContext.Clear();
                    _sessionTitle = string.Empty;
                    _sessionId = AssistantSessionLibrary.NewId();
                }
                _lastReportedPromptTokens = 0;
                Changed?.Invoke();
            }

            SessionsChanged?.Invoke();
        }

        /// <summary>Pins a context item for the next turn (replaces an equivalent existing pin).</summary>
        public void AddContext(AssistantContextItem item)
        {
            if (item == null) return;
            // Collapse duplicates of the singleton kinds (only one Selection/Scene/Graph/KG makes sense).
            if (item.Kind != AssistantContextKind.Asset)
                _pinnedContext.RemoveAll(c => c.Kind == item.Kind);
            else if (!string.IsNullOrEmpty(item.AssetGuid))
                _pinnedContext.RemoveAll(c => c.Kind == AssistantContextKind.Asset && c.AssetGuid == item.AssetGuid);
            _pinnedContext.Add(item);
            Persist();
            Changed?.Invoke();
        }

        /// <summary>Removes a pinned context item.</summary>
        public void RemoveContext(AssistantContextItem item)
        {
            if (item != null && _pinnedContext.Remove(item))
            {
                Persist();
                Changed?.Invoke();
            }
        }

        /// <summary>Drops the oldest conversation turn pair to relieve context pressure (Sprint 24.8).</summary>
        public void DropOldestTurn()
        {
            if (_history.Count == 0) return;
            // Remove the first user message and the assistant/tool messages up to the next user turn.
            _history.RemoveAt(0);
            while (_history.Count > 0 && _history[0].Role != LlmRole.User)
                _history.RemoveAt(0);
            Persist();
            Changed?.Invoke();
        }

        /// <summary>
        /// A rough token estimate (~4 chars/token) for the system prompt, conversation history, and
        /// pinned context, plus an optional pending user message (Sprint 24.8). Heuristic, not exact.
        /// </summary>
        public int EstimateContextTokens(string pendingUserText = null)
        {
            // Prefer the vendor-reported prompt size from the last turn (Sprint 25.8) — a real count for the
            // committed context — plus a cheap heuristic for the not-yet-sent input. Falls back to the pure
            // character heuristic before the first turn or when the provider didn't report usage.
            if (_lastReportedPromptTokens > 0)
                return _lastReportedPromptTokens + (pendingUserText?.Length ?? 0) / 4;

            var chars = SystemPrompt.Length;
            foreach (var m in _history)
            {
                chars += m.Text?.Length ?? 0;
                foreach (var r in m.ToolResults) chars += r.Content?.Length ?? 0;
                foreach (var c in m.ToolCalls) chars += c.ArgumentsJson?.Length ?? 0;
            }
            chars += pendingUserText?.Length ?? 0;

            // Approximate pinned-context cost cheaply — never resolve live items (a pinned Framework Graph
            // would otherwise rebuild on every keystroke/stream token).
            foreach (var item in _pinnedContext)
                chars += item.Kind == AssistantContextKind.Selection && !item.Live
                    ? (item.Snapshot?.Length ?? 0)
                    : 250;

            return chars / 4;
        }

        /// <summary>Persists the current transcript, history, and pinned context (Sprint 24.5).</summary>
        /// <summary>
        /// Saves the current conversation to its session file (Sprint 35). Skips empty sessions so a
        /// never-used "New chat" doesn't litter the library; titles are derived from the first user turn.
        /// </summary>
        private void Persist()
        {
            if (string.IsNullOrEmpty(_sessionId)) return;
            if (_transcript.Count == 0 && _history.Count == 0 && _pinnedContext.Count == 0) return;
            // Pass the stored title (empty until the chat is auto-named); Save preserves the on-disk title
            // when none is given, so autosaves never clobber an LLM-generated name.
            AssistantSessionLibrary.Save(_sessionId, _transcript, _history, _pinnedContext,
                title: string.IsNullOrWhiteSpace(_sessionTitle) ? null : _sessionTitle);
            SessionsChanged?.Invoke();
        }

        /// <summary>
        /// Sends a user message and runs the tool-call loop until the model produces a final answer or
        /// the tool-round cap is hit. Surfaces tool activity and errors in the transcript.
        /// </summary>
        public async Awaitable SendAsync(string userText, CancellationToken cancellationToken)
        {
            if (IsBusy || string.IsNullOrWhiteSpace(userText)) return;

            var status = _settings.GetStatus(out var statusMessage);
            if (status != AssistantConfigStatus.Configured)
            {
                AddTurn(ChatTurnKind.Error, statusMessage);
                return;
            }

            IsBusy = true;
            // Anchor the visible user turn to the history message it produces, so retry/edit can trim both
            // precisely later (Sprint 25.8) without scanning for "the last user message".
            var userHistoryIndex = _history.Count;
            _transcript.Add(new ChatTurn(ChatTurnKind.User, userText, (IReadOnlyList<ChatToolSummary>)null, userHistoryIndex));
            Changed?.Invoke();
            _history.Add(LlmMessage.UserText(AssistantEditorContext.WithContext(userText, _pinnedContext)));

            try
            {
                var provider = _settings.CreateProvider();
                var streaming = _settings.StreamResponses;
                var mcpSettings = MolcaEditorSettings.Instance.McpSettings;
                var registry = mcpSettings?.BuildRegistry();
                Func<string, bool> isActionAllowed = mcpSettings != null ? mcpSettings.IsActionAllowed : null;
                var tools = AssistantToolBridge.GetToolSpecs(registry, isActionAllowed);

                // Confirmation policy (Sprint 25 + later): Ask mode confirms every action through the
                // in-chat docked prompt bar. Auto mode runs allowlisted actions without prompting — except
                // irreversible ones (no undo to fall back on), which always confirm even in Auto. The async
                // confirmer encapsulates both modes; the sync one is unused.
                Func<McpToolDefinition, string, bool> confirmAction = null;
                Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync = ConfirmActionInModeAsync;

                var maxToolRounds = _settings.MaxToolRounds > 0 ? _settings.MaxToolRounds : DefaultMaxToolRounds;

                // Tool activity is shown inline, in execution order: each run of same-kind tool calls
                // (a read run or an action run) collapses into one Work turn that is committed at the
                // point it runs — not bundled into a single summary at the end of the turn. Intermediate
                // assistant notes render as their own turns between groups, so the transcript reads as a
                // sequential narrative and action Undo stays grouped per run.
                var pending = new List<ChatToolSummary>();
                var pendingIsAction = false;
                void FlushToolGroup()
                {
                    if (pending.Count == 0) return;
                    var batch = pending.ToArray();
                    var text = batch.Length == 1 ? "Worked through 1 step." : $"Worked through {batch.Length} steps.";
                    _transcript.Add(new ChatTurn(ChatTurnKind.Work, text, batch));
                    pending.Clear();
                    Changed?.Invoke();
                }
                void Append(ChatToolSummary summary, bool isAction)
                {
                    if (pending.Count > 0 && pendingIsAction != isAction) FlushToolGroup();
                    pendingIsAction = isAction;
                    pending.Add(summary);
                }

                for (var round = 0; round < maxToolRounds; round++)
                {
                    var request = new LlmRequest
                    {
                        System = SystemPrompt,
                        Messages = new List<LlmMessage>(_history),
                        Tools = tools,
                        Model = _settings.Model,
                        MaxTokens = _settings.MaxTokens
                    };

                    // While streaming, surface text deltas as a live row; clear the buffer once the
                    // full response lands so the finalized assistant turn replaces it.
                    IProgress<string> onDelta = streaming
                        ? new Progress<string>(delta =>
                        {
                            StreamingText += delta;
                            Changed?.Invoke();
                        })
                        : null;

                    var response = await provider.SendAsync(request, cancellationToken, onDelta);
                    StreamingText = string.Empty;

                    // Cache the vendor-reported prompt size so the token estimate can show a real number
                    // instead of the character heuristic (Sprint 25.8). Streaming responses report 0; the
                    // last non-zero value is kept.
                    if (response.PromptTokens > 0) _lastReportedPromptTokens = response.PromptTokens;

                    // Record the assistant turn (text + any tool calls) in history.
                    var assistantMsg = new LlmMessage { Role = LlmRole.Assistant, Text = response.Text, ToolCalls = response.ToolCalls };
                    _history.Add(assistantMsg);
                    if (!string.IsNullOrWhiteSpace(response.Text))
                    {
                        // Close any open tool group before the note/answer so ordering stays faithful.
                        FlushToolGroup();
                        AddTurn(ChatTurnKind.Assistant, response.Text);
                    }

                    if (!response.WantsToolUse)
                    {
                        FlushToolGroup();
                        break;
                    }

                    // Execute each requested tool and feed results back as one user turn.
                    var resultMsg = new LlmMessage { Role = LlmRole.User };
                    for (var callIndex = 0; callIndex < response.ToolCalls.Count; callIndex++)
                    {
                        var call = response.ToolCalls[callIndex];
                        McpToolDefinition toolDef = null;
                        var hasDef = registry != null && registry.TryGet(call.Name, out toolDef);
                        var isAction = hasDef && toolDef.Kind == McpToolKind.Action;
                        if (ShouldGroupActionConfirmations(isAction, isActionAllowed, toolDef))
                        {
                            var actionCalls = CollectConsecutiveActionCalls(response.ToolCalls, callIndex, registry, isActionAllowed);
                            if (actionCalls.Count > 1)
                            {
                                // Auto mode runs the batch without prompting — but only when every action in
                                // it is undoable; if any is irreversible, fall back to the single "Run all /
                                // Cancel" prompt (irreversible actions always confirm, even in Auto). Ask mode
                                // always prompts. Confirmed batches still execute as one undo group below.
                                var autoApprove = ActionMode == AssistantActionMode.Auto
                                    && actionCalls.All(c => IsUndoable(c.Tool));
                                var confirmed = autoApprove
                                    || await ConfirmActionsAsync(actionCalls, cancellationToken);
                                if (autoApprove)
                                {
                                    foreach (var actionCall in actionCalls)
                                        McpActionAuditLog.Record(actionCall.Tool.Name, actionCall.Call.ArgumentsJson, "chat", "auto-approved");
                                }
                                if (!confirmed)
                                {
                                    foreach (var actionCall in actionCalls)
                                    {
                                        McpActionAuditLog.Record(actionCall.Tool.Name, actionCall.Call.ArgumentsJson, "chat", "denied");
                                        var denied = new LlmToolResult(actionCall.Call.Id,
                                            "{\"error\":\"The user declined to run this action batch.\"}", isError: true);
                                        resultMsg.ToolResults.Add(denied);
                                        Append(BuildToolSummary(registry, actionCall.Call, denied), isAction: true);
                                    }
                                    callIndex += actionCalls.Count - 1;
                                    continue;
                                }

                                foreach (var actionCall in actionCalls)
                                {
                                    var grouped = await ExecuteToolCallAsync(registry, actionCall.Call, actionCall.Tool,
                                        cancellationToken, isActionAllowed, confirmAction, confirmActionAsync, approvedActionBatch: true);
                                    resultMsg.ToolResults.Add(grouped.Result);
                                    Append(grouped.Summary, isAction: true);
                                }
                                callIndex += actionCalls.Count - 1;
                                continue;
                            }
                        }
                        var reversibility = hasDef ? toolDef.Reversibility : McpToolReversibility.Irreversible;

                        // Capture pre-action undo state so we can correlate this call with what it produced:
                        //  • FileSnapshot tools push an McpUndoStack entry — compare the stack's top id.
                        //  • UnityUndo tools record a Unity Undo group — compare the current group index.
                        var undoBefore = isAction && reversibility == McpToolReversibility.FileSnapshot ? CurrentTopUndoId() : null;
                        var groupBefore = isAction && reversibility == McpToolReversibility.UnityUndo ? Undo.GetCurrentGroup() : -1;

                        ActiveToolName = call.Name;
                        ActiveToolProgress = null;
                        var result = await AssistantToolBridge.ExecuteAsync(registry, call, cancellationToken, isActionAllowed, confirmAction, AskUserAsync, confirmActionAsync, ReportToolProgress);
                        ActiveToolName = null;
                        ActiveToolProgress = null;
                        resultMsg.ToolResults.Add(result);

                        string undoId = null;
                        var undoGroup = -1;
                        if (isAction && !result.IsError)
                        {
                            if (reversibility == McpToolReversibility.FileSnapshot)
                            {
                                var undoAfter = CurrentTopUndoId();
                                if (!string.IsNullOrEmpty(undoAfter) && undoAfter != undoBefore)
                                    undoId = undoAfter;
                            }
                            else if (reversibility == McpToolReversibility.UnityUndo)
                            {
                                // The action created at least one group; revert down to its first group.
                                if (Undo.GetCurrentGroup() > groupBefore)
                                    undoGroup = groupBefore + 1;
                            }
                        }

                        var summary = BuildToolSummary(registry, call, result, undoId, undoGroup);
                        Append(summary, summary.Kind == nameof(McpToolKind.Action));
                    }
                    _history.Add(resultMsg);

                    if (round == maxToolRounds - 1)
                    {
                        FlushToolGroup();
                        _transcript.Add(new ChatTurn(ChatTurnKind.Error,
                            $"Reached the tool-call limit ({maxToolRounds} rounds) for this turn. The work so far is " +
                            "kept — click Continue to resume, or raise the limit in Assistant settings.")
                        { CanContinue = true });
                        Changed?.Invoke();
                    }
                }

                FlushToolGroup();

                // Once the chat has had its first exchange, ask the model for a short title so the switcher
                // and header show a meaningful name instead of the truncated first message. Best-effort and
                // run once per session; failures leave the derived-title fallback in place.
                await MaybeGenerateTitleAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AddTurn(ChatTurnKind.Error, "Cancelled.");
            }
            catch (Exception ex)
            {
                AddTurn(ChatTurnKind.Error, ex.Message);
            }
            finally
            {
                IsBusy = false;
                StreamingText = string.Empty;
                ActiveToolName = null;
                ActiveToolProgress = null;
                Persist();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Progress sink passed to <see cref="AssistantToolBridge.ExecuteAsync"/> for the running tool:
        /// stores the latest report and repaints the transcript so the live progress row updates.
        /// </summary>
        private void ReportToolProgress(McpProgressReport report)
        {
            ActiveToolProgress = report;
            Changed?.Invoke();
        }

        /// <summary>Upper bound on an auto-generated session title (characters), elided beyond this.</summary>
        private const int GeneratedTitleMaxLength = 48;

        /// <summary>
        /// Auto-names the session from its first exchange when it has no stored title yet. Best-effort: a
        /// failed or empty generation, or cancellation, leaves the derived-title fallback untouched. Runs at
        /// most once per session because a successful run sets <see cref="_sessionTitle"/>.
        /// </summary>
        private async Awaitable MaybeGenerateTitleAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_sessionTitle)) return;

            var firstUser = _transcript.FirstOrDefault(t => t.Kind == ChatTurnKind.User)?.Text;
            var firstAssistant = _transcript.FirstOrDefault(t => t.Kind == ChatTurnKind.Assistant)?.Text;
            if (string.IsNullOrWhiteSpace(firstUser) || string.IsNullOrWhiteSpace(firstAssistant)) return;

            try
            {
                var title = await GenerateTitleAsync(firstUser, firstAssistant, cancellationToken);
                if (string.IsNullOrWhiteSpace(title)) return;

                _sessionTitle = title;
                Persist();          // writes the title into the session header and raises SessionsChanged
                Changed?.Invoke();  // refresh the header so the new name shows immediately
            }
            catch (OperationCanceledException)
            {
                // Naming is incidental to the turn; a cancelled request just leaves the chat unnamed.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Assistant chat auto-title failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Asks the model for a concise session title summarizing the first exchange. Sends a small,
        /// tool-free, non-streaming request and sanitizes the reply into a single short line.
        /// </summary>
        private async Awaitable<string> GenerateTitleAsync(string userText, string assistantText, CancellationToken cancellationToken)
        {
            var provider = _settings.CreateProvider();
            var request = new LlmRequest
            {
                System =
                    "You name editor chat sessions. Read the exchange and reply with a short, specific title " +
                    "of 3 to 6 words naming the task or topic. Use Title Case. No surrounding quotes, no " +
                    "trailing punctuation, no preamble — output only the title.",
                Messages = new List<LlmMessage>
                {
                    LlmMessage.UserText(
                        "User:\n" + Truncate(userText, 800) +
                        "\n\nAssistant:\n" + Truncate(assistantText, 800) +
                        "\n\nTitle:")
                },
                Model = _settings.Model,
                MaxTokens = 32
            };

            var response = await provider.SendAsync(request, cancellationToken);
            return SanitizeTitle(response?.Text);
        }

        /// <summary>Trims a model title to a single clean line within <see cref="GeneratedTitleMaxLength"/>.</summary>
        private static string SanitizeTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var line = raw.Trim().Replace("\r", " ").Replace("\n", " ").Trim();
            line = line.Trim('"', '\'', '`', '.', ' ');
            if (string.IsNullOrWhiteSpace(line)) return null;
            return line.Length <= GeneratedTitleMaxLength
                ? line
                : line.Substring(0, GeneratedTitleMaxLength - 1).TrimEnd() + "…";
        }

        /// <summary>Caps a string to <paramref name="max"/> characters for the title prompt.</summary>
        private static string Truncate(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max);

        /// <summary>
        /// Re-runs the last user turn (Sprint 24.6): drops the trailing assistant/tool/error turns and the
        /// last user turn from both the transcript and history, then resends the same text. Optionally
        /// overrides the text for edit-and-resend.
        /// </summary>
        public async Awaitable RetryLastAsync(CancellationToken cancellationToken, string editedText = null)
        {
            if (IsBusy) return;

            // Find the last user turn in the transcript and trim everything from it onward.
            var idx = -1;
            for (var i = _transcript.Count - 1; i >= 0; i--)
                if (_transcript[i].Kind == ChatTurnKind.User) { idx = i; break; }
            if (idx < 0) return;

            var turn = _transcript[idx];
            var text = string.IsNullOrWhiteSpace(editedText) ? turn.Text : editedText;
            _transcript.RemoveRange(idx, _transcript.Count - idx);

            // Drop history from this turn's anchored message onward (Sprint 25.8). Fall back to scanning
            // for the last user message when the anchor is absent (e.g. a turn restored from an older
            // session before anchoring existed).
            var hidx = turn.HistoryIndex;
            if (hidx < 0 || hidx > _history.Count)
            {
                hidx = -1;
                for (var i = _history.Count - 1; i >= 0; i--)
                    if (_history[i].Role == LlmRole.User && !string.IsNullOrEmpty(_history[i].Text)) { hidx = i; break; }
            }
            if (hidx >= 0 && hidx <= _history.Count)
                _history.RemoveRange(hidx, _history.Count - hidx);

            Changed?.Invoke();
            await SendAsync(text, cancellationToken);
        }

        /// <summary>
        /// Pauses the turn and surfaces a <c>molca_ask_user</c> prompt (Sprint 25.6). Records the question
        /// as a <see cref="ChatTurnKind.Prompt"/> turn, then awaits the user's answer (delivered via
        /// <see cref="AnswerPending"/>) or cancellation. Passed to <see cref="AssistantToolBridge"/> as the
        /// interactive asker; the loop resumes with the returned answer as the tool result.
        /// </summary>
        /// <summary>The interactive asker passed to <see cref="AssistantToolBridge"/> for <c>molca_ask_user</c>.</summary>
        private Awaitable<string> AskUserAsync(AssistantUserPrompt prompt, CancellationToken cancellationToken)
            => PromptUserAsync(prompt, cancellationToken);

        /// <summary>
        /// Confirms an Action tool through the same docked prompt bar as <c>molca_ask_user</c> (Sprint 25
        /// follow-up), instead of a blocking modal dialog. Used in Ask mode; returns true only if the user
        /// chooses Run.
        /// </summary>
        private async Awaitable<bool> ConfirmActionAsync(McpToolDefinition tool, string args, CancellationToken cancellationToken)
        {
            var prompt = new AssistantUserPrompt(McpActionGuard.BuildPrompt(tool, args), new[] { "Run", "Cancel" });
            var answer = await PromptUserAsync(prompt, cancellationToken, isConfirmation: true);
            return string.Equals(answer, "Run", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Per-action confirmation that honors <see cref="ActionMode"/>: Auto mode auto-approves an
        /// allowlisted action <em>only if it is undoable</em>, otherwise (irreversible, or Ask mode) it
        /// confirms through the docked prompt bar. Irreversible actions thus always prompt, even in Auto.
        /// </summary>
        private async Awaitable<bool> ConfirmActionInModeAsync(McpToolDefinition tool, string args, CancellationToken cancellationToken)
        {
            if (ActionMode == AssistantActionMode.Auto && IsUndoable(tool))
            {
                McpActionAuditLog.Record(tool.Name, args, "chat", "auto-approved");
                return true;
            }
            return await ConfirmActionAsync(tool, args, cancellationToken);
        }

        /// <summary>True if the tool's effect can be reverted (a file snapshot or a Unity Undo group).</summary>
        private static bool IsUndoable(McpToolDefinition tool)
            => tool != null && (tool.Reversibility == McpToolReversibility.FileSnapshot
                                 || tool.Reversibility == McpToolReversibility.UnityUndo);

        /// <summary>
        /// Core of the interactive pause (Sprint 25.6): records the question as a <see cref="ChatTurnKind.Prompt"/>
        /// turn, surfaces it via <see cref="PendingPrompt"/> (rendered in the docked prompt bar), and awaits
        /// the user's answer — delivered by <see cref="AnswerPending"/> — or cancellation with the turn.
        /// Shared by <see cref="AskUserAsync"/> and <see cref="ConfirmActionAsync"/>.
        /// </summary>
        private async Awaitable<string> PromptUserAsync(AssistantUserPrompt prompt, CancellationToken cancellationToken,
            bool isConfirmation = false)
        {
            PendingPrompt = prompt;
            _pendingPromptSource = new AwaitableCompletionSource<string>();
            // Record the question as its own Prompt turn; the answer is written back onto this same turn
            // (not a separate user turn) so it doesn't masquerade as a real user prompt for retry/LastUserText.
            _pendingPromptTurn = new ChatTurn(ChatTurnKind.Prompt, prompt.Question) { IsConfirmation = isConfirmation };
            _transcript.Add(_pendingPromptTurn);
            Changed?.Invoke();

            using (cancellationToken.Register(() => _pendingPromptSource?.TrySetCanceled()))
            {
                try
                {
                    return await _pendingPromptSource.Awaitable;
                }
                finally
                {
                    _pendingPromptSource = null;
                    _pendingPromptTurn = null;
                    PendingPrompt = null;
                    Changed?.Invoke();
                }
            }
        }

        /// <summary>
        /// Answers the outstanding <see cref="PendingPrompt"/> with the user's choice (Sprint 25.6),
        /// writing it onto the prompt turn and resuming the paused turn. No-op if nothing is awaiting.
        /// </summary>
        public void AnswerPending(string answer)
        {
            var source = _pendingPromptSource;
            if (source == null) return;
            if (_pendingPromptTurn != null) _pendingPromptTurn.PromptAnswer = answer ?? string.Empty;
            source.TrySetResult(answer ?? string.Empty);
        }

        private static bool ShouldGroupActionConfirmations(bool isAction, Func<string, bool> isActionAllowed, McpToolDefinition tool)
            => isAction && tool != null && (isActionAllowed?.Invoke(tool.Name) ?? false);

        private static List<ActionCall> CollectConsecutiveActionCalls(
            IReadOnlyList<LlmToolCall> calls, int startIndex, McpToolRegistry registry, Func<string, bool> isActionAllowed)
        {
            var actionCalls = new List<ActionCall>();
            for (var i = startIndex; i < calls.Count; i++)
            {
                var call = calls[i];
                if (registry == null || !registry.TryGet(call.Name, out var tool) || tool.Kind != McpToolKind.Action)
                    break;
                if (!(isActionAllowed?.Invoke(tool.Name) ?? false))
                    break;
                actionCalls.Add(new ActionCall(call, tool));
            }
            return actionCalls;
        }

        private async Awaitable<bool> ConfirmActionsAsync(IReadOnlyList<ActionCall> calls, CancellationToken cancellationToken)
        {
            var items = new List<McpActionPromptItem>();
            foreach (var call in calls)
                items.Add(new McpActionPromptItem(call.Tool, call.Call.ArgumentsJson));

            var prompt = new AssistantUserPrompt(McpActionPromptFormatter.BuildBatchConfirmationPrompt(items), new[] { "Run all", "Cancel" });
            var answer = await PromptUserAsync(prompt, cancellationToken, isConfirmation: true);
            return string.Equals(answer, "Run all", StringComparison.OrdinalIgnoreCase);
        }

        private async Awaitable<ToolExecution> ExecuteToolCallAsync(
            McpToolRegistry registry, LlmToolCall call, McpToolDefinition toolDef, CancellationToken cancellationToken,
            Func<string, bool> isActionAllowed, Func<McpToolDefinition, string, bool> confirmAction,
            Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync,
            bool approvedActionBatch)
        {
            var isAction = toolDef != null && toolDef.Kind == McpToolKind.Action;
            var reversibility = toolDef != null ? toolDef.Reversibility : McpToolReversibility.Irreversible;
            var undoBefore = isAction && reversibility == McpToolReversibility.FileSnapshot ? CurrentTopUndoId() : null;
            var groupBefore = isAction && reversibility == McpToolReversibility.UnityUndo ? Undo.GetCurrentGroup() : -1;

            ActiveToolName = call.Name;
            ActiveToolProgress = null;
            var result = await AssistantToolBridge.ExecuteAsync(
                registry, call, cancellationToken, isActionAllowed,
                approvedActionBatch ? (_, __) => true : confirmAction,
                AskUserAsync,
                approvedActionBatch ? null : confirmActionAsync,
                ReportToolProgress);
            ActiveToolName = null;
            ActiveToolProgress = null;

            string undoId = null;
            var undoGroup = -1;
            if (isAction && !result.IsError)
            {
                if (reversibility == McpToolReversibility.FileSnapshot)
                {
                    var undoAfter = CurrentTopUndoId();
                    if (!string.IsNullOrEmpty(undoAfter) && undoAfter != undoBefore)
                        undoId = undoAfter;
                }
                else if (reversibility == McpToolReversibility.UnityUndo)
                {
                    if (Undo.GetCurrentGroup() > groupBefore)
                        undoGroup = groupBefore + 1;
                }
            }

            return new ToolExecution(result, BuildToolSummary(registry, call, result, undoId, undoGroup));
        }

        private void AddTurn(ChatTurnKind kind, string text, ChatToolSummary toolSummary = null)
        {
            _transcript.Add(new ChatTurn(kind, text, toolSummary));
            Changed?.Invoke();
        }

        private void AddTurn(ChatTurnKind kind, string text, IReadOnlyList<ChatToolSummary> toolSummaries)
        {
            _transcript.Add(new ChatTurn(kind, text, toolSummaries));
            Changed?.Invoke();
        }

        private static ChatToolSummary BuildToolSummary(McpToolRegistry registry, LlmToolCall call, LlmToolResult result, string undoEntryId = null, int undoGroup = -1)
        {
            string mode = "Unknown";
            string kind = "Unknown";
            string reversibility = "Unknown";
            if (registry != null && registry.TryGet(call.Name, out var tool))
            {
                mode = tool.Mode.ToString();
                kind = tool.Kind.ToString();
                reversibility = tool.Reversibility.ToString();
            }

            return new ChatToolSummary(
                call.Name,
                call.ArgumentsJson,
                result.Content,
                result.IsError,
                mode,
                kind,
                reversibility,
                undoEntryId,
                undoGroup);
        }

        private readonly struct ActionCall
        {
            public readonly LlmToolCall Call;
            public readonly McpToolDefinition Tool;

            public ActionCall(LlmToolCall call, McpToolDefinition tool)
            {
                Call = call;
                Tool = tool;
            }
        }

        private readonly struct ToolExecution
        {
            public readonly LlmToolResult Result;
            public readonly ChatToolSummary Summary;

            public ToolExecution(LlmToolResult result, ChatToolSummary summary)
            {
                Result = result;
                Summary = summary;
            }
        }

        /// <summary>The id of the newest <see cref="McpUndoStack"/> entry, or null if the stack is empty.</summary>
        private static string CurrentTopUndoId()
        {
            var entries = McpUndoStack.Entries;
            return entries.Count > 0 ? entries[entries.Count - 1].Id : null;
        }
    }
}
