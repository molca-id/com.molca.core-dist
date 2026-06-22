using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>A single entry in the visible transcript.</summary>
    /// <remarks>Members are appended for serialization stability — never reorder existing values.</remarks>
    public enum ChatTurnKind { User, Assistant, Tool, Error, Prompt, Work, Notice, Plan }

    /// <summary>
    /// Execution status of a single <see cref="PlanStep"/> in a structured plan turn (Sprint 52). The
    /// transcript view renders a status glyph per step and the controller advances these in place as the
    /// approved plan runs.
    /// </summary>
    /// <remarks>Members are appended for serialization stability — never reorder existing values.</remarks>
    public enum PlanStepStatus
    {
        /// <summary>Not started yet.</summary>
        Pending,
        /// <summary>Currently executing.</summary>
        Running,
        /// <summary>Completed successfully.</summary>
        Done,
        /// <summary>Failed during execution.</summary>
        Failed,
        /// <summary>Skipped after a failure (user chose Skip).</summary>
        Skipped
    }

    /// <summary>
    /// One ordered step of a structured plan proposed via <c>molca_propose_plan</c> (Sprint 52). The
    /// <see cref="Status"/> is mutated in place as the approved plan executes; <see cref="Summary"/> is
    /// editable before approval through the transcript view's inline Edit mode.
    /// </summary>
    public sealed class PlanStep
    {
        /// <summary>Stable id the model assigned to the step (used for display and correlation).</summary>
        public string Id { get; set; }

        /// <summary>Human-readable one-line description of what the step does.</summary>
        public string Summary { get; set; }

        /// <summary>Live execution status, advanced by the controller as the plan runs.</summary>
        public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;

        /// <summary>Creates a plan step.</summary>
        public PlanStep(string id, string summary, PlanStepStatus status = PlanStepStatus.Pending)
        {
            Id = id ?? string.Empty;
            Summary = summary ?? string.Empty;
            Status = status;
        }
    }

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
        public int HistoryIndex { get; internal set; }

        /// <summary>
        /// The answer the user gave to a <see cref="ChatTurnKind.Prompt"/> turn, or <c>null</c> until
        /// answered (Sprint 25.7). Mutable because the prompt turn is recorded before the user responds.
        /// </summary>
        public string PromptAnswer { get; set; }

        /// <summary>
        /// Optional expandable detail for a turn — currently the generated summary text behind a
        /// <see cref="ChatTurnKind.Notice"/> compaction line (Sprint 46), shown collapsed by default.
        /// <c>null</c> when the turn has no expandable body. Persisted with the transcript.
        /// </summary>
        public string Detail { get; set; }

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

        /// <summary>
        /// True on a retrieval <see cref="ChatTurnKind.Notice"/> (Sprint 47), so the view can offer a "Pin"
        /// action that promotes the retrieved context (held in <see cref="Detail"/>) to a persistent pin.
        /// </summary>
        public bool CanPin { get; set; }

        /// <summary>
        /// True on the plan-completed <see cref="ChatTurnKind.Notice"/> (Sprint 48), so the view can offer a
        /// single "Undo task" that reverts every reversible change the approved plan made this turn.
        /// </summary>
        public bool CanUndoTask { get; set; }

        /// <summary>
        /// The ordered steps of a <see cref="ChatTurnKind.Plan"/> turn (Sprint 52), or <c>null</c> on every
        /// other kind. Mutated in place: statuses advance as the plan runs and the list is edited inline
        /// (reorder/delete/retext) before approval. Persisted with the transcript.
        /// </summary>
        public List<PlanStep> PlanSteps { get; set; }

        /// <summary>
        /// True once the user approved this <see cref="ChatTurnKind.Plan"/> turn (Sprint 52). Gates inline
        /// editing (only an unapproved plan is editable) and drives the view's running/complete styling.
        /// </summary>
        public bool PlanApproved { get; set; }

        /// <summary>
        /// The whole-task file-snapshot undo entry id captured when this <see cref="ChatTurnKind.Plan"/> turn
        /// was approved (Sprint 52/48), or <c>null</c>. Persisted so "Undo task" survives a domain reload.
        /// </summary>
        public string PlanUndoFileId { get; set; }

        /// <summary>
        /// The Unity Undo group captured when this plan was approved (Sprint 52/48), or <c>-1</c>. Persisted
        /// so "Undo task" survives a domain reload.
        /// </summary>
        public int PlanUndoGroup { get; set; } = -1;

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
        private readonly Func<ILlmProvider> _providerFactory;
        private readonly bool _usesInjectedProviderFactory;
        private readonly List<LlmMessage> _history = new List<LlmMessage>();
        private readonly List<ChatTurn> _transcript = new List<ChatTurn>();
        private readonly List<AssistantContextItem> _pinnedContext = new List<AssistantContextItem>();

        // The in-flight molca_ask_user prompt (Sprint 25.6): non-null while the turn is paused waiting for
        // the user to choose. Completed by AnswerPending or cancelled with the turn.
        private AwaitableCompletionSource<string> _pendingPromptSource;
        private ChatTurn _pendingPromptTurn;

        // Latest vendor-reported prompt token count (Sprint 25.8); 0 until a turn completes non-streaming.
        private int _lastReportedPromptTokens;

        // Cumulative tokens billed across this session (Sprint 49): input is summed from each request's
        // vendor-reported prompt size (you pay per call); output is estimated from the response text when the
        // vendor doesn't report it. Restored from the session header on load, persisted on save.
        private long _sessionInputTokens;
        private long _sessionOutputTokens;

        // Surfacing state for the most recent auto-compaction (Sprint 46): the generated summary text (null
        // when the last relief was digest-only) and how many tool results were digested. Drives the composer
        // "context compacted" affordance; reset to defaults the moment a new chat/session begins.
        private string _lastCompactionSummary;
        private int _lastCompactionDigestedCount;

        // Transient grounding context retrieved for the in-flight turn (Sprint 47): injected into the request
        // and counted in the estimate, but never stored in _history or persisted. Cleared when the turn ends.
        private string _pendingRetrievedContext;

        // Plan-mode state for the in-flight turn (Sprint 48): set once the user approves the plan, with the
        // undo bracket captured at that moment so the whole task reverts in one click. Reset each turn.
        private bool _planApprovedThisTurn;
        private string _planUndoIdBefore;
        private int _planUndoGroupBefore = -1;
        // The bracket of the most recently executed approved plan, for the "Undo task" affordance.
        private string _lastPlanUndoFileId;
        private int _lastPlanUndoGroup = -1;

        // The structured plan turn proposed via molca_propose_plan for the in-flight turn (Sprint 52), or
        // null when the model has not proposed one. Its per-step statuses are advanced in place as the
        // approved plan executes, and it carries the persisted whole-task undo bracket. Reset each turn.
        private ChatTurn _activePlanTurn;

        // Set when the user chose Abort or Undo task on a failed plan step (Sprint 52): remaining tool calls
        // in the round are answered with an abort result (so no tool_use is left unanswered) and no further
        // round is started. Reset each turn.
        private bool _planAbortRequested;

        /// <summary>The tool-result payload returned for plan calls left unrun after the user aborts (Sprint 52).</summary>
        private const string PlanAbortedResultJson = "{\"error\":\"Plan aborted by the user; this step was not run.\"}";

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

        /// <summary>
        /// Test seam for the docked prompt/confirmation pause. When set, the controller delegates prompts to
        /// this handler instead of waiting on the UI, while preserving the production prompt path by default.
        /// </summary>
        internal Func<AssistantUserPrompt, bool, CancellationToken, Awaitable<string>> PromptUserAsyncOverride { get; set; }

        /// <summary>
        /// Test seam for the action-confirmation policy. Defaults to <see cref="ConfirmActionInModeAsync"/>,
        /// which applies Ask/Auto/Plan behavior unchanged.
        /// </summary>
        internal Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> ConfirmActionInModeAsyncOverride { get; set; }

        /// <summary>
        /// Test seam for proactive retrieval, avoiding a graphify subprocess while still exercising request
        /// injection and token accounting.
        /// </summary>
        internal Func<string, int, CancellationToken, Awaitable<RetrievedContext>> RetrieveContextAsyncOverride { get; set; }

        /// <summary>The current provider-neutral model history, exposed to EditMode tests only.</summary>
        internal IReadOnlyList<LlmMessage> History => _history;

        /// <summary>Creates a controller bound to the given settings, restoring the most recent session.</summary>
        public AssistantChatController(AssistantSettings settings)
            : this(settings, () => settings.CreateProvider(), false)
        {
        }

        /// <summary>
        /// Creates a controller that resolves its LLM provider through <paramref name="providerFactory"/>.
        /// </summary>
        /// <remarks>
        /// Intended as an EditMode-test seam: injected providers bypass credential validation so full turns can
        /// run deterministically without network or user secrets. Production call sites should use
        /// <see cref="AssistantChatController(AssistantSettings)"/>.
        /// </remarks>
        /// <param name="settings">Assistant configuration used for model, limits, compaction, and retrieval.</param>
        /// <param name="providerFactory">Factory that returns the provider used for turn, compaction, and title requests.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> or <paramref name="providerFactory"/> is null.</exception>
        public AssistantChatController(AssistantSettings settings, Func<ILlmProvider> providerFactory)
            : this(settings, providerFactory, true)
        {
        }

        private AssistantChatController(AssistantSettings settings, Func<ILlmProvider> providerFactory, bool usesInjectedProviderFactory)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _usesInjectedProviderFactory = usesInjectedProviderFactory;

            // Migrate the legacy single-file session into the library on first run, then open the most
            // recently updated session. If there are none, start a fresh (unsaved-until-first-turn) session.
            AssistantSessionLibrary.MigrateLegacyIfNeeded();
            var mostRecent = AssistantSessionLibrary.ListSessions().FirstOrDefault();
            if (mostRecent != null &&
                AssistantSessionLibrary.TryLoad(mostRecent.Id, out var turns, out var history, out var context, out var meta))
            {
                _sessionId = mostRecent.Id;
                _sessionTitle = meta?.Title ?? string.Empty;
                RestoreSessionTokens(meta);
                _transcript.AddRange(turns);
                _history.AddRange(history);
                _pinnedContext.AddRange(context);
            }
            else
            {
                _sessionId = AssistantSessionLibrary.NewId();
            }
        }

        /// <summary>Restores the cumulative token counters from a loaded session header (Sprint 49).</summary>
        private void RestoreSessionTokens(SessionMeta meta)
        {
            _sessionInputTokens = meta?.InputTokens ?? 0;
            _sessionOutputTokens = meta?.OutputTokens ?? 0;
        }

        /// <summary>Clears the conversation history and transcript, but keeps the pinned context.</summary>
        public void Reset()
        {
            _history.Clear();
            _transcript.Clear();
            _sessionTitle = string.Empty;
            _lastReportedPromptTokens = 0;
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            ResetCompactionState();
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
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            ResetCompactionState();
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
            ResetCompactionState();

            _sessionId = id;
            _sessionTitle = meta?.Title ?? string.Empty;
            RestoreSessionTokens(meta);
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
                    RestoreSessionTokens(meta);
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
                    _sessionInputTokens = 0;
                    _sessionOutputTokens = 0;
                }
                _lastReportedPromptTokens = 0;
                ResetCompactionState();
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
            // Transient retrieved context (Sprint 47) is injected into the request but not stored in history,
            // so add it explicitly on both paths to keep the threshold check (and the composer readout) honest.
            var retrievedChars = _pendingRetrievedContext?.Length ?? 0;

            if (_lastReportedPromptTokens > 0)
                return _lastReportedPromptTokens + (pendingUserText?.Length ?? 0) / 4 + retrievedChars / 4;

            var chars = SystemPrompt.Length + retrievedChars;
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
                title: string.IsNullOrWhiteSpace(_sessionTitle) ? null : _sessionTitle,
                inputTokens: _sessionInputTokens, outputTokens: _sessionOutputTokens);
            SessionsChanged?.Invoke();
        }

        /// <summary>
        /// Sends a user message and runs the tool-call loop until the model produces a final answer or
        /// the tool-round cap is hit. Surfaces tool activity and errors in the transcript.
        /// </summary>
        public async Awaitable SendAsync(string userText, CancellationToken cancellationToken)
        {
            if (IsBusy || string.IsNullOrWhiteSpace(userText)) return;

            if (!_usesInjectedProviderFactory)
            {
                var status = _settings.GetStatus(out var statusMessage);
                if (status != AssistantConfigStatus.Configured)
                {
                    AddTurn(ChatTurnKind.Error, statusMessage);
                    return;
                }
            }

            IsBusy = true;
            // Plan approval and its undo bracket are per-turn (Sprint 48); reset before this turn begins.
            _planApprovedThisTurn = false;
            _planUndoIdBefore = null;
            _planUndoGroupBefore = -1;
            // The structured plan turn (Sprint 52) is likewise per-turn.
            _activePlanTurn = null;
            _planAbortRequested = false;
            // Anchor the visible user turn to the history message it produces, so retry/edit can trim both
            // precisely later (Sprint 25.8) without scanning for "the last user message".
            var userHistoryIndex = _history.Count;
            _transcript.Add(new ChatTurn(ChatTurnKind.User, userText, (IReadOnlyList<ChatToolSummary>)null, userHistoryIndex));
            Changed?.Invoke();
            _history.Add(LlmMessage.UserText(AssistantEditorContext.WithContext(userText, _pinnedContext)));

            try
            {
                var provider = CreateProvider();
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
                Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync =
                    ConfirmActionInModeAsyncOverride ?? ConfirmActionInModeAsync;

                var maxToolRounds = _settings.MaxToolRounds > 0 ? _settings.MaxToolRounds : DefaultMaxToolRounds;

                // Proactively ground this turn in the project (Sprint 47): query the knowledge graph with the
                // user's message and inject the result as transient context for the turn (regenerated each
                // turn, never pinned or persisted). Best-effort — degrades to nothing when no graph is built.
                // Done before compaction so the added size is accounted for in the threshold check.
                await RetrieveTurnContextAsync(userText, cancellationToken);

                // Before spending tokens on this turn, summarize the oldest turns if the running context has
                // grown past the configured threshold (Sprint 45). Keeps long sessions usable without the
                // user manually pruning. Best-effort: a failed summary leaves the history untouched.
                await MaybeCompactAsync(provider, cancellationToken);

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
                        Messages = BuildRequestMessages(),
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

                    // Accumulate session token spend (Sprint 49): input is the vendor-reported prompt size of
                    // this request (billed per call), output is estimated from the response text when the
                    // vendor doesn't report it. Both feed the read-only telemetry and cost estimate.
                    _sessionInputTokens += response.PromptTokens;
                    // Prefer the vendor's real output count (Sprint 53); fall back to the char heuristic only
                    // when it is not reported (e.g. a streaming endpoint without usage).
                    _sessionOutputTokens += response.CompletionTokens > 0
                        ? response.CompletionTokens
                        : EstimateTokenCount(response.Text);

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
                        // Once the user aborts a failed plan (Sprint 52), every remaining tool_use in this
                        // round is answered with an abort result so the model's call/result pairing stays valid.
                        if (_planAbortRequested)
                        {
                            resultMsg.ToolResults.Add(new LlmToolResult(call.Id, PlanAbortedResultJson, isError: true));
                            continue;
                        }
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
                                var allUndoable = actionCalls.All(c => IsUndoable(c.Tool));
                                var autoApprove = ActionMode == AssistantActionMode.Auto && allUndoable;
                                // Plan mode runs an all-undoable batch under one task approval; a batch with an
                                // irreversible action falls through to the explicit "Run all / Cancel" prompt.
                                var planApprove = ActionMode == AssistantActionMode.Plan && allUndoable
                                    && await EnsurePlanApprovedAsync(cancellationToken);
                                var confirmed = autoApprove || planApprove
                                    || await ConfirmActionsAsync(actionCalls, cancellationToken);
                                if (autoApprove || planApprove)
                                {
                                    var disposition = planApprove ? "plan-approved" : "auto-approved";
                                    foreach (var actionCall in actionCalls)
                                        McpActionAuditLog.Record(actionCall.Tool.Name, actionCall.Call.ArgumentsJson, "chat", disposition);
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
                                    // After an abort, answer the remaining batch members without running them.
                                    if (_planAbortRequested)
                                    {
                                        var ar = new LlmToolResult(actionCall.Call.Id, PlanAbortedResultJson, isError: true);
                                        resultMsg.ToolResults.Add(ar);
                                        Append(BuildToolSummary(registry, actionCall.Call, ar), isAction: true);
                                        continue;
                                    }
                                    var groupedStep = _planApprovedThisTurn ? BeginNextPlanStep() : null;
                                    var grouped = await ExecuteToolCallAsync(registry, actionCall.Call, actionCall.Tool,
                                        cancellationToken, isActionAllowed, confirmAction, confirmActionAsync, approvedActionBatch: true);
                                    var groupedResult = grouped.Result;
                                    var groupedSummary = grouped.Summary;
                                    // A failed structured-plan step halts with Skip/Retry/Undo task/Abort (Sprint 52).
                                    if (groupedStep != null && groupedResult.IsError)
                                    {
                                        groupedResult = await HandlePlanStepFailureAsync(registry, actionCall.Call, actionCall.Tool,
                                            groupedStep, groupedResult, isActionAllowed, confirmAction, confirmActionAsync, cancellationToken);
                                        groupedSummary = BuildToolSummary(registry, actionCall.Call, groupedResult);
                                    }
                                    CompletePlanStep(groupedStep, !groupedResult.IsError);
                                    resultMsg.ToolResults.Add(groupedResult);
                                    Append(groupedSummary, isAction: true);
                                    // Without a structured plan, a failed step re-gates the legacy plan (Sprint 48).
                                    if (groupedResult.IsError && _activePlanTurn == null) _planApprovedThisTurn = false;
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
                        // Correlate this call to the active plan's next pending step (Sprint 52), so the
                        // checklist advances live as execution proceeds. Order-based mapping keeps the action
                        // tool schemas untouched (no injected planStepId arg).
                        var planStep = isAction && _planApprovedThisTurn ? BeginNextPlanStep() : null;

                        var result = await AssistantToolBridge.ExecuteAsync(registry, call, cancellationToken, isActionAllowed, confirmAction, AskUserAsync, confirmActionAsync, ReportToolProgress, ProposePlanAsync);
                        ActiveToolName = null;
                        ActiveToolProgress = null;

                        // A failed approved-plan step halts the run with Skip / Retry / Undo task / Abort
                        // (Sprint 52), replacing the soft re-gate. Retry re-runs the same call in place.
                        if (planStep != null && result.IsError)
                            result = await HandlePlanStepFailureAsync(registry, call, toolDef, planStep, result,
                                isActionAllowed, confirmAction, confirmActionAsync, cancellationToken);
                        CompletePlanStep(planStep, !result.IsError);
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
                        // Without a structured plan, a failed step re-gates the legacy plan (Sprint 48); with a
                        // structured plan, the failure UX above already decided how to proceed.
                        if (isAction && result.IsError && _activePlanTurn == null) _planApprovedThisTurn = false;
                    }
                    _history.Add(resultMsg);

                    // The user aborted a failed plan step (Sprint 52): the round's calls are all answered, so
                    // history is valid — stop here instead of starting another round.
                    if (_planAbortRequested)
                    {
                        FlushToolGroup();
                        break;
                    }

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

                // If an approved plan ran reversible actions this turn, offer a single "Undo task" that
                // reverts the whole bracket captured at approval (Sprint 48).
                MaybeAddPlanUndoNotice();

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
                // The retrieved block is transient — drop it so it never persists or leaks into the next turn.
                _pendingRetrievedContext = null;
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

        /// <summary>Real user prompts kept verbatim at the tail of history when auto-compacting (Sprint 45).</summary>
        private const int KeepRecentUserTurns = 2;

        /// <summary>
        /// Summarizes the oldest turns into a single compact note when the estimated context size has grown
        /// past <see cref="AssistantSettings.AutoCompactThreshold"/> (Sprint 45). No-op when auto-compaction
        /// is disabled, the context is still small, or there are too few turns to safely trim.
        /// </summary>
        /// <param name="provider">The active provider, reused for the tool-free summary request.</param>
        /// <param name="cancellationToken">The turn's bootstrap-lifetime token.</param>
        /// <remarks>
        /// Cuts only on a real user-prompt boundary so a tool_use/tool_result pair is never split (which
        /// every vendor rejects), and re-anchors the surviving transcript turns' <see cref="ChatTurn.HistoryIndex"/>
        /// so retry/edit keeps working. Best-effort: a failed or empty summary leaves history untouched.
        /// </remarks>
        private async Awaitable MaybeCompactAsync(ILlmProvider provider, CancellationToken cancellationToken)
        {
            if (!_settings.AutoCompact) return;
            if (EstimateContextTokens() < _settings.AutoCompactThreshold) return;

            // Tier 1 (cheap, no LLM call): digest tool-result payloads older than the most-recent turn(s)
            // into one-line stubs. These age fastest and dominate token cost, so this alone often drops the
            // context back under threshold — preserving the user/assistant reasoning the summary would lose.
            if (_settings.CompactToolResultsFirst)
            {
                if (TryDigestOldToolResults(_history, _settings.KeepRecentToolResultTurns, out var digestedCount))
                {
                    _lastReportedPromptTokens = 0; // digested payloads change the prompt size; re-estimate fresh
                    _lastCompactionDigestedCount = digestedCount;
                    _transcript.Add(new ChatTurn(ChatTurnKind.Notice, DigestNoticeText(digestedCount)));
                    Persist();
                    Changed?.Invoke();

                    // If digesting alone brought us under the threshold, skip the (paid) turn-summary entirely.
                    if (EstimateContextTokens() < _settings.AutoCompactThreshold) return;
                }
            }

            // Tier 2 (paid): summarize the oldest turns into a single compact note.
            if (!TryPlanCompaction(_history, KeepRecentUserTurns, out var cut)) return;

            var older = _history.GetRange(0, cut);
            string summary;
            try
            {
                summary = await SummarizeHistoryAsync(provider, older, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // honor cancellation; the outer turn handler reports it
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Assistant auto-compaction failed; keeping full history: {ex.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(summary)) return;

            ApplyCompaction(_history, _transcript, cut, summary);
            _lastCompactionSummary = summary;

            // The vendor token count is now stale (it measured the pre-compaction prompt); force the estimate
            // back to the heuristic until the next response reports a fresh number.
            _lastReportedPromptTokens = 0;

            Persist();
            Changed?.Invoke();
        }

        /// <summary>
        /// The generated summary of the most recent turn-summary compaction, or <c>null</c> when the last
        /// relief was digest-only or none has occurred (Sprint 46). Drives the composer's "view" affordance.
        /// </summary>
        public string LastCompactionSummary => _lastCompactionSummary;

        /// <summary>How many tool results the most recent digest pass condensed (Sprint 46); 0 if none.</summary>
        public int LastCompactionDigestedCount => _lastCompactionDigestedCount;

        /// <summary>Whether auto-compaction is enabled, for the composer's threshold readout (Sprint 46).</summary>
        public bool AutoCompactEnabled => _settings.AutoCompact;

        /// <summary>The configured auto-compaction threshold in tokens, for the composer readout (Sprint 46).</summary>
        public int AutoCompactThreshold => _settings.AutoCompactThreshold;

        /// <summary>Cumulative input (prompt) tokens billed across this session (Sprint 49).</summary>
        public long SessionInputTokens => _sessionInputTokens;

        /// <summary>Cumulative output (completion) tokens across this session, estimated where unreported (Sprint 49).</summary>
        public long SessionOutputTokens => _sessionOutputTokens;

        /// <summary>Estimated USD spend for this session under the configured model's pricing (Sprint 49).</summary>
        public double SessionEstimatedCostUsd =>
            AssistantCostTable.EstimateCost(_settings.Model, _sessionInputTokens, _sessionOutputTokens, _settings.ModelPriceOverrides);

        /// <summary>Rough token count (~4 chars/token) for text the vendor didn't report usage for (Sprint 49).</summary>
        private static long EstimateTokenCount(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

        /// <summary>Clears the last-compaction surfacing state when the conversation changes (Sprint 46).</summary>
        private void ResetCompactionState()
        {
            _lastCompactionSummary = null;
            _lastCompactionDigestedCount = 0;
        }

        /// <summary>The transcript notice text shown when proactive retrieval injects grounding context (Sprint 47).</summary>
        internal const string RetrievalNoticeText = "Retrieved project context for this question.";

        /// <summary>
        /// Runs the proactive retrieval pass for the current turn (Sprint 47): when enabled, queries the
        /// knowledge graph and stores the result in <see cref="_pendingRetrievedContext"/> for injection,
        /// recording a pinnable <see cref="ChatTurnKind.Notice"/> so the user can see (and keep) what was
        /// injected. Best-effort: a no-result/failed/disabled pass leaves the turn ungrounded.
        /// </summary>
        private async Awaitable RetrieveTurnContextAsync(string userText, CancellationToken cancellationToken)
        {
            if (!_settings.ProactiveRetrieval) return;

            var retrieved = RetrieveContextAsyncOverride != null
                ? await RetrieveContextAsyncOverride(userText, _settings.RetrievalTokenBudget, cancellationToken)
                : await AssistantContextRetriever.RetrieveAsync(userText, _settings.RetrievalTokenBudget, cancellationToken);
            if (!retrieved.HasContent) return;

            _pendingRetrievedContext = retrieved.Text;
            _transcript.Add(new ChatTurn(ChatTurnKind.Notice, RetrievalNoticeText)
            {
                Detail = retrieved.Text,
                CanPin = true
            });
            Changed?.Invoke();
        }

        /// <summary>
        /// Builds the request message list (Sprint 47): a copy of <see cref="_history"/> with the current
        /// turn's user message prefixed by the transient retrieved context, if any. The prefix lives only in
        /// the request copy so it is never persisted and does not accumulate across turns.
        /// </summary>
        private List<LlmMessage> BuildRequestMessages()
        {
            var messages = new List<LlmMessage>(_history);
            if (string.IsNullOrEmpty(_pendingRetrievedContext)) return messages;

            // Prefix the most recent genuine user prompt (the current turn) — a fresh message instance so the
            // stored history is untouched.
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m.Role != LlmRole.User || (m.ToolResults != null && m.ToolResults.Count > 0)) continue;
                messages[i] = new LlmMessage
                {
                    Role = LlmRole.User,
                    Text = AssistantContextRetriever.RetrievedContextHeader + "\n" + _pendingRetrievedContext +
                           "\n\n" + m.Text,
                    ToolCalls = m.ToolCalls
                };
                break;
            }
            return messages;
        }

        /// <summary>
        /// Promotes the transient retrieved context behind a retrieval notice to a persistent pinned item
        /// (Sprint 47), so it survives future turns instead of being regenerated. Invoked from the view's
        /// "Pin" affordance on a <see cref="ChatTurnKind.Notice"/> with <see cref="ChatTurn.CanPin"/>.
        /// </summary>
        public void PinRetrievedContext(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            AddContext(AssistantContextItem.ForRetrieved(text, "Retrieved context"));
        }

        /// <summary>Prefix prepended to the synthesized summary message that replaces the compacted slice.</summary>
        internal const string CompactionSummaryPrefix =
            "[Summary of the earlier conversation, condensed to save context]\n";

        /// <summary>The one-line assistant acknowledgement inserted after the summary to keep roles alternating.</summary>
        internal const string CompactionAckText = "Understood — continuing from that summary.";

        /// <summary>The transcript notice line recorded when a turn is auto-compacted.</summary>
        internal const string CompactionNoticeText =
            "Compacted earlier turns into a summary to stay within the context limit.";

        /// <summary>Prefix marking an already-digested tool result so the digest pass never re-digests it (Sprint 46).</summary>
        internal const string ToolResultDigestPrefix = "[digested] ";

        /// <summary>Tool results shorter than this are left alone — digesting them would not reclaim tokens (Sprint 46).</summary>
        internal const int ToolResultDigestMinChars = 200;

        /// <summary>The transcript notice line for a digest-only relief pass (Sprint 46).</summary>
        internal static string DigestNoticeText(int digestedCount) =>
            $"Condensed {digestedCount} older tool result{(digestedCount == 1 ? "" : "s")} to stay within the context limit.";

        /// <summary>
        /// Finds the cut index for a relief pass: the start of the <paramref name="keepRecentUserTurns"/>-th
        /// most recent genuine user prompt (Sprint 45/46). User-role text turns are boundaries; tool-result
        /// user turns are excluded so a cut never lands between an assistant's tool call and its result.
        /// </summary>
        /// <returns>The boundary index, or <c>0</c> when there is no slice old enough to act on.</returns>
        private static int ComputeUserPromptCut(IReadOnlyList<LlmMessage> history, int keepRecentUserTurns)
        {
            if (history == null || keepRecentUserTurns < 1) return 0;

            var userTurns = new List<int>();
            for (var i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m.Role == LlmRole.User && (m.ToolResults == null || m.ToolResults.Count == 0))
                    userTurns.Add(i);
            }
            if (userTurns.Count <= keepRecentUserTurns) return 0;
            return userTurns[userTurns.Count - keepRecentUserTurns];
        }

        /// <summary>
        /// Chooses where to cut <paramref name="history"/> for the turn-summary (Sprint 45): the start of the
        /// <paramref name="keepRecentUserTurns"/>-th most recent genuine user prompt. Cutting on a user-prompt
        /// boundary guarantees the kept tail starts cleanly and no tool_use/tool_result pair is split — which
        /// every vendor rejects.
        /// </summary>
        /// <param name="history">The full LLM message history.</param>
        /// <param name="keepRecentUserTurns">How many trailing user prompts (and their turns) to keep verbatim.</param>
        /// <param name="cut">The index to summarize up to (exclusive); valid only when this returns <c>true</c>.</param>
        /// <returns><c>true</c> when there is an older slice worth summarizing; otherwise <c>false</c>.</returns>
        internal static bool TryPlanCompaction(IReadOnlyList<LlmMessage> history, int keepRecentUserTurns, out int cut)
        {
            cut = ComputeUserPromptCut(history, keepRecentUserTurns);
            return cut > 0;
        }

        /// <summary>
        /// Condenses tool-result payloads older than the most recent <paramref name="keepRecentUserTurns"/>
        /// user turns into one-line stubs, in place (Sprint 46). The tool call/result ids and message
        /// structure are preserved (only <see cref="LlmToolResult.Content"/> is shortened), so no vendor
        /// rejects the rewritten history. Already-digested and short results are skipped. This is a pure
        /// transform with no LLM call — the cheap first tier of context relief.
        /// </summary>
        /// <param name="history">The history to rewrite in place.</param>
        /// <param name="keepRecentUserTurns">Trailing user turns whose tool results stay verbatim (typically &lt; the summary keep count, so the digest reaches results the summary would otherwise preserve).</param>
        /// <param name="digestedCount">The number of tool results condensed; valid only when this returns <c>true</c>.</param>
        /// <returns><c>true</c> when at least one tool result was condensed; otherwise <c>false</c>.</returns>
        internal static bool TryDigestOldToolResults(List<LlmMessage> history, int keepRecentUserTurns, out int digestedCount)
        {
            digestedCount = 0;
            var cut = ComputeUserPromptCut(history, keepRecentUserTurns);
            if (cut <= 0) return false;

            for (var i = 0; i < cut; i++)
            {
                var results = history[i].ToolResults;
                if (results == null) continue;
                for (var r = 0; r < results.Count; r++)
                {
                    var result = results[r];
                    var content = result.Content ?? string.Empty;
                    if (content.StartsWith(ToolResultDigestPrefix, StringComparison.Ordinal)) continue;
                    if (content.Length < ToolResultDigestMinChars) continue;

                    var digest = $"{ToolResultDigestPrefix}{(result.IsError ? "error" : "ok")}, {content.Length} chars elided";
                    results[r] = new LlmToolResult(result.ToolCallId, digest, result.IsError);
                    digestedCount++;
                }
            }
            return digestedCount > 0;
        }

        /// <summary>
        /// Replaces <paramref name="history"/>[0..<paramref name="cut"/>) with a summary note plus an assistant
        /// acknowledgement, re-anchors the surviving <paramref name="transcript"/> turns' history indices, and
        /// records a <see cref="ChatTurnKind.Notice"/> line (Sprint 45).
        /// </summary>
        /// <param name="history">The history to rewrite in place; messages before <paramref name="cut"/> are removed.</param>
        /// <param name="transcript">The visible transcript whose anchors are remapped and that receives the notice.</param>
        /// <param name="cut">The exclusive upper bound of the slice being summarized (from <see cref="TryPlanCompaction"/>).</param>
        /// <param name="summary">The model-produced summary of the removed slice.</param>
        internal static void ApplyCompaction(List<LlmMessage> history, List<ChatTurn> transcript, int cut, string summary)
        {
            // The ack keeps roles alternating so the kept slice (which begins with a user prompt) stays valid
            // for every provider, and it marks the boundary clearly for the model.
            var replacement = new List<LlmMessage>
            {
                LlmMessage.UserText(CompactionSummaryPrefix + summary),
                new LlmMessage { Role = LlmRole.Assistant, Text = CompactionAckText }
            };
            history.RemoveRange(0, cut);
            history.InsertRange(0, replacement);

            // The kept slice shifted by (replacement.Count - cut); turns that lived inside the summarized slice
            // are no longer addressable, so drop their anchor.
            var delta = replacement.Count - cut;
            if (transcript != null)
            {
                foreach (var turn in transcript)
                {
                    if (turn.HistoryIndex < 0) continue;
                    turn.HistoryIndex = turn.HistoryIndex >= cut ? turn.HistoryIndex + delta : -1;
                }
                transcript.Add(new ChatTurn(ChatTurnKind.Notice, CompactionNoticeText) { Detail = summary });
            }
        }

        /// <summary>
        /// Asks the model to condense a slice of conversation history into a compact, faithful summary
        /// (Sprint 45). Sends a small, tool-free, non-streaming request so compaction never recurses into
        /// tool use or streaming bookkeeping.
        /// </summary>
        /// <param name="provider">The active provider to send the summary request through.</param>
        /// <param name="messages">The oldest history slice being replaced.</param>
        /// <param name="cancellationToken">The turn's bootstrap-lifetime token.</param>
        /// <returns>The summary text, or <c>null</c> if the model returned nothing usable.</returns>
        private async Awaitable<string> SummarizeHistoryAsync(
            ILlmProvider provider, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken)
        {
            var request = new LlmRequest
            {
                System =
                    "You compress an editor assistant's conversation history. Produce a concise but faithful " +
                    "summary the assistant can rely on to continue the session: preserve the user's goals, key " +
                    "decisions, facts established, file/asset names, and any unfinished work or open questions. " +
                    "Use short bullet points. Do not invent details. Output only the summary.",
                Messages = new List<LlmMessage> { LlmMessage.UserText(RenderHistoryForSummary(messages)) },
                Model = _settings.Model,
                MaxTokens = 1024
            };

            var response = await provider.SendAsync(request, cancellationToken);
            return response?.Text?.Trim();
        }

        /// <summary>Renders a history slice into a plain-text transcript for the summary prompt, bounding each part.</summary>
        private static string RenderHistoryForSummary(IReadOnlyList<LlmMessage> messages)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in messages)
            {
                if (!string.IsNullOrWhiteSpace(m.Text))
                    sb.Append(m.Role == LlmRole.User ? "User: " : "Assistant: ")
                      .AppendLine(Truncate(m.Text, 1500));

                foreach (var call in m.ToolCalls)
                    sb.Append("Assistant called tool ").Append(call.Name).Append(' ')
                      .AppendLine(Truncate(call.ArgumentsJson, 300));

                foreach (var result in m.ToolResults)
                    sb.Append("Tool result: ").AppendLine(Truncate(result.Content, 600));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Asks the model for a concise session title summarizing the first exchange. Sends a small,
        /// tool-free, non-streaming request and sanitizes the reply into a single short line.
        /// </summary>
        private async Awaitable<string> GenerateTitleAsync(string userText, string assistantText, CancellationToken cancellationToken)
        {
            var provider = CreateProvider();
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
            if (ActionMode == AssistantActionMode.Plan && IsUndoable(tool))
            {
                // Plan mode: confirm the whole task once, then run its undoable actions unprompted.
                if (!await EnsurePlanApprovedAsync(cancellationToken)) return false;
                McpActionAuditLog.Record(tool.Name, args, "chat", "plan-approved");
                return true;
            }
            // Ask mode, or an irreversible action under Auto/Plan — always confirm individually.
            return await ConfirmActionAsync(tool, args, cancellationToken);
        }

        /// <summary>
        /// Surfaces the Approve / Edit / Cancel plan prompt once per turn and, on approval, captures the undo
        /// bracket so the whole task can be reverted in one click (Sprint 48). Subsequent calls in the same
        /// turn return the cached approval. <c>Edit</c> and <c>Cancel</c> both decline; <c>Edit</c> also flags
        /// the view to refocus the composer so the user can revise and resend.
        /// </summary>
        private async Awaitable<bool> EnsurePlanApprovedAsync(CancellationToken cancellationToken)
        {
            if (_planApprovedThisTurn) return true;

            var prompt = new AssistantUserPrompt(
                "Approve this plan and run its steps? The actions above run without further prompts; "
                + "irreversible steps still confirm individually, and you can undo the whole task afterward.",
                new[] { "Approve", "Edit", "Cancel" });
            var answer = await PromptUserAsync(prompt, cancellationToken, isConfirmation: true);

            if (string.Equals(answer, "Edit", StringComparison.OrdinalIgnoreCase))
            {
                // Let the user revise: surface the proposed plan back into the composer for editing.
                PlanEditRequested?.Invoke(LastAssistantText);
                return false;
            }
            if (!string.Equals(answer, "Approve", StringComparison.OrdinalIgnoreCase))
                return false; // Cancel / dismissed

            _planApprovedThisTurn = true;
            _planUndoIdBefore = CurrentTopUndoId();
            _planUndoGroupBefore = Undo.GetCurrentGroup();
            return true;
        }

        /// <summary>
        /// Surfaces a structured plan (Sprint 52) for <c>molca_propose_plan</c>: records a reviewable
        /// <see cref="ChatTurnKind.Plan"/> turn, then pauses for Approve / Edit / Cancel through the docked
        /// prompt bar (the same pause mechanism as confirmations). On approval the plan is locked in, the
        /// whole-task undo bracket is captured, and the disposition (with the possibly-edited steps) is
        /// returned as the tool result so the model executes the revision. Used as the bridge's plan surface.
        /// </summary>
        private async Awaitable<string> ProposePlanAsync(IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
        {
            var planTurn = new ChatTurn(ChatTurnKind.Plan, "Proposed plan")
            {
                PlanSteps = steps != null ? new List<PlanStep>(steps) : new List<PlanStep>()
            };
            _activePlanTurn = planTurn;
            _transcript.Add(planTurn);
            Changed?.Invoke();

            var prompt = new AssistantUserPrompt(
                "Approve this plan and run its steps? Approve runs them under one undo bracket "
                + "(irreversible steps still confirm individually); Edit revises the steps first; Cancel stops.",
                new[] { "Approve", "Edit", "Cancel" });
            var answer = await PromptUserAsync(prompt, cancellationToken, isConfirmation: true);

            if (string.Equals(answer, "Approve", StringComparison.OrdinalIgnoreCase))
            {
                _planApprovedThisTurn = true;
                _planUndoIdBefore = CurrentTopUndoId();
                _planUndoGroupBefore = Undo.GetCurrentGroup();
                planTurn.PlanApproved = true;
                Changed?.Invoke();
                return BuildPlanDisposition("approved", planTurn.PlanSteps);
            }
            if (string.Equals(answer, "Edit", StringComparison.OrdinalIgnoreCase))
            {
                // The model is told the plan needs revision; the next assistant turn re-proposes it.
                return BuildPlanDisposition("edit_requested", planTurn.PlanSteps);
            }

            // Cancel / dismissed: drop the active plan so subsequent actions confirm individually.
            _activePlanTurn = null;
            return "{\"disposition\":\"cancelled\"}";
        }

        /// <summary>Builds the <c>molca_propose_plan</c> tool result describing the user's disposition and (edited) steps.</summary>
        private static string BuildPlanDisposition(string disposition, IReadOnlyList<PlanStep> steps)
        {
            var arr = new JArray();
            if (steps != null)
                foreach (var s in steps)
                    if (s != null)
                        arr.Add(new JObject { ["id"] = s.Id, ["summary"] = s.Summary });
            return new JObject { ["disposition"] = disposition, ["steps"] = arr }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Marks the active plan's next <see cref="PlanStepStatus.Pending"/> step as
        /// <see cref="PlanStepStatus.Running"/> and returns it (Sprint 52). Order-based correlation: each
        /// executed action advances the next outstanding step. Returns <c>null</c> when no plan is active or
        /// every step has already run.
        /// </summary>
        private PlanStep BeginNextPlanStep()
        {
            if (_activePlanTurn?.PlanSteps == null) return null;
            foreach (var step in _activePlanTurn.PlanSteps)
            {
                if (step.Status != PlanStepStatus.Pending) continue;
                step.Status = PlanStepStatus.Running;
                Changed?.Invoke();
                return step;
            }
            return null;
        }

        /// <summary>Marks a running plan step <see cref="PlanStepStatus.Done"/>/<see cref="PlanStepStatus.Failed"/>, leaving a Skipped step alone (Sprint 52).</summary>
        private void CompletePlanStep(PlanStep step, bool succeeded)
        {
            if (step == null || step.Status == PlanStepStatus.Skipped) return;
            step.Status = succeeded ? PlanStepStatus.Done : PlanStepStatus.Failed;
            Changed?.Invoke();
        }

        /// <summary>
        /// Handles a failed approved-plan step (Sprint 52) by halting the run and offering Retry / Skip /
        /// Undo task / Abort through the docked prompt bar — replacing Sprint 48's soft re-gate. Retry re-runs
        /// the same call in place; Skip marks the step skipped and continues; Undo task reverts the whole
        /// bracket then aborts; Abort stops the run. Returns the latest tool result for the model.
        /// </summary>
        private async Awaitable<LlmToolResult> HandlePlanStepFailureAsync(
            McpToolRegistry registry, LlmToolCall call, McpToolDefinition toolDef, PlanStep step, LlmToolResult result,
            Func<string, bool> isActionAllowed, Func<McpToolDefinition, string, bool> confirmAction,
            Func<McpToolDefinition, string, CancellationToken, Awaitable<bool>> confirmActionAsync, CancellationToken cancellationToken)
        {
            while (result.IsError)
            {
                var prompt = new AssistantUserPrompt(
                    $"Plan step \"{step.Summary}\" failed. Retry it, skip it and continue, undo the whole task, or abort the run?",
                    new[] { "Retry", "Skip", "Undo task", "Abort" });
                var answer = await PromptUserAsync(prompt, cancellationToken, isConfirmation: true);

                if (string.Equals(answer, "Retry", StringComparison.OrdinalIgnoreCase))
                {
                    step.Status = PlanStepStatus.Running;
                    Changed?.Invoke();
                    var exec = await ExecuteToolCallAsync(registry, call, toolDef, cancellationToken,
                        isActionAllowed, confirmAction, confirmActionAsync, approvedActionBatch: true);
                    result = exec.Result;
                    continue;
                }
                if (string.Equals(answer, "Skip", StringComparison.OrdinalIgnoreCase))
                {
                    step.Status = PlanStepStatus.Skipped;
                    Changed?.Invoke();
                    return result; // surface the failure to the model, but let the plan continue
                }
                if (string.Equals(answer, "Undo task", StringComparison.OrdinalIgnoreCase))
                    UndoApprovedPlanBracket();

                // Undo task or Abort (or dismissed): stop the run after the round's calls are answered.
                _planAbortRequested = true;
                return result;
            }
            return result;
        }

        /// <summary>
        /// Reverts every reversible change made since the active plan was approved (Sprint 52), using the
        /// in-progress bracket captured at approval. Used by the failure UX's "Undo task" before aborting.
        /// </summary>
        private void UndoApprovedPlanBracket()
        {
            var fileId = FirstUndoIdAfter(McpUndoStack.Entries, _planUndoIdBefore);
            var group = Undo.GetCurrentGroup() > _planUndoGroupBefore ? _planUndoGroupBefore + 1 : -1;
            if (!string.IsNullOrEmpty(fileId) && McpUndoStack.Contains(fileId))
                McpUndoStack.UndoTo(fileId);
            if (group >= 0 && Undo.GetCurrentGroup() >= group)
                Undo.RevertAllDownToGroup(group);
        }

        /// <summary>The text of the most recent assistant turn (the proposed plan), or empty (Sprint 48).</summary>
        private string LastAssistantText
        {
            get
            {
                for (var i = _transcript.Count - 1; i >= 0; i--)
                    if (_transcript[i].Kind == ChatTurnKind.Assistant)
                        return _transcript[i].Text ?? string.Empty;
                return string.Empty;
            }
        }

        /// <summary>Raised when the user picks "Edit" on a plan prompt, to refill the composer (Sprint 48).</summary>
        public event Action<string> PlanEditRequested;

        /// <summary>The transcript notice text shown when an approved plan finishes running (Sprint 48).</summary>
        internal const string PlanCompletedNoticeText = "Plan complete. Undo the whole task if needed.";

        /// <summary>
        /// After an approved plan ran, records a single "Undo task" notice that brackets every reversible
        /// change since approval (Sprint 48): the first <see cref="McpUndoStack"/> entry created after the
        /// captured top, plus the first Unity Undo group after the captured one. No-op when nothing reversible
        /// was produced.
        /// </summary>
        private void MaybeAddPlanUndoNotice()
        {
            if (!_planApprovedThisTurn) return;

            // First file-snapshot entry created after approval (entries are oldest-first).
            var fileId = FirstUndoIdAfter(McpUndoStack.Entries, _planUndoIdBefore);

            // First Unity Undo group created after approval.
            var group = Undo.GetCurrentGroup() > _planUndoGroupBefore ? _planUndoGroupBefore + 1 : -1;

            if (string.IsNullOrEmpty(fileId) && group < 0) return; // nothing reversible ran

            _lastPlanUndoFileId = fileId;
            _lastPlanUndoGroup = group;

            // Persist the bracket on the structured plan turn (Sprint 52) so "Undo task" survives a domain
            // reload, not just the current in-memory session.
            if (_activePlanTurn != null)
            {
                _activePlanTurn.PlanUndoFileId = fileId;
                _activePlanTurn.PlanUndoGroup = group;
            }

            _transcript.Add(new ChatTurn(ChatTurnKind.Notice, PlanCompletedNoticeText) { CanUndoTask = true });
            Changed?.Invoke();
        }

        /// <summary>
        /// The id of the first undo entry created after <paramref name="afterId"/> (Sprint 48): entries are
        /// oldest-first, so this is the entry just past the captured bracket top — or the oldest entry when
        /// <paramref name="afterId"/> is null/absent (the stack was empty at approval). Returns <c>null</c>
        /// when nothing was created after the bracket. Pure and testable.
        /// </summary>
        internal static string FirstUndoIdAfter(IReadOnlyList<McpUndoStack.Entry> entries, string afterId)
        {
            if (entries == null || entries.Count == 0) return null;

            var startIndex = 0;
            if (!string.IsNullOrEmpty(afterId))
            {
                startIndex = entries.Count; // afterId not found → treat as "nothing after"
                for (var i = 0; i < entries.Count; i++)
                    if (entries[i].Id == afterId) { startIndex = i + 1; break; }
            }
            return startIndex < entries.Count ? entries[startIndex].Id : null;
        }

        /// <summary>
        /// Reverts every reversible change made by the most recent approved plan (Sprint 48), using the
        /// bracket captured at approval — file snapshots via <see cref="McpUndoStack.UndoTo"/> and Unity
        /// changes via <see cref="UnityEditor.Undo.RevertAllDownToGroup"/>. Invoked by the "Undo task" button.
        /// </summary>
        public void UndoApprovedPlan()
        {
            if (!string.IsNullOrEmpty(_lastPlanUndoFileId) && McpUndoStack.Contains(_lastPlanUndoFileId))
                McpUndoStack.UndoTo(_lastPlanUndoFileId);
            if (_lastPlanUndoGroup >= 0 && Undo.GetCurrentGroup() >= _lastPlanUndoGroup)
                Undo.RevertAllDownToGroup(_lastPlanUndoGroup);

            _lastPlanUndoFileId = null;
            _lastPlanUndoGroup = -1;
            Persist();
            Changed?.Invoke();
        }

        /// <summary>
        /// Reverts the whole-task bracket persisted on a structured <see cref="ChatTurnKind.Plan"/> turn
        /// (Sprint 52), so "Undo task" works even after a domain reload (the in-memory bracket is gone). The
        /// bracket is cleared on the turn afterward so it cannot be applied twice.
        /// </summary>
        public void UndoApprovedPlan(ChatTurn planTurn)
        {
            if (planTurn == null) { UndoApprovedPlan(); return; }

            if (!string.IsNullOrEmpty(planTurn.PlanUndoFileId) && McpUndoStack.Contains(planTurn.PlanUndoFileId))
                McpUndoStack.UndoTo(planTurn.PlanUndoFileId);
            if (planTurn.PlanUndoGroup >= 0 && Undo.GetCurrentGroup() >= planTurn.PlanUndoGroup)
                Undo.RevertAllDownToGroup(planTurn.PlanUndoGroup);

            planTurn.PlanUndoFileId = null;
            planTurn.PlanUndoGroup = -1;
            Persist();
            Changed?.Invoke();
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
            if (PromptUserAsyncOverride != null)
                return await PromptUserAsyncOverride(prompt, isConfirmation, cancellationToken);

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

        private ILlmProvider CreateProvider() => _providerFactory();
    }
}
