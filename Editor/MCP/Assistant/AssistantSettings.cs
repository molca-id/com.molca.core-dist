using System;
using System.Collections.Generic;
using System.IO;
using Molca.Editor.Licensing;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>Configuration status of the assistant, for the settings status dot (Sprint 16.7).</summary>
    public enum AssistantConfigStatus
    {
        /// <summary>Enabled, provider implemented, and an API key is available.</summary>
        Configured,
        /// <summary>Turned off.</summary>
        Disabled,
        /// <summary>Enabled but unusable — missing key or an unimplemented provider.</summary>
        Misconfigured
    }

    /// <summary>
    /// How the assistant exposes the MCP tool surface to the model (Sprint 68.9). <see cref="Tiered"/> sends a
    /// compact catalog + on-demand <c>molca_tool_schema</c> fetch (Sprint 67) — tiny per-request payload, but a
    /// fetch-then-call indirection a weak model may not navigate. <see cref="Flat"/> sends every tool's full
    /// schema directly (one-step calls) at a larger payload. <see cref="Auto"/> picks flat for the keyless
    /// <see cref="LlmProviderKind.Local"/> backend (small local models, free tokens) and tiered for cloud.
    /// </summary>
    /// <remarks>Members are appended for serialization stability — never reorder existing values.</remarks>
    public enum ToolExposureMode
    {
        /// <summary>Flat for the Local backend, tiered for cloud backends.</summary>
        Auto,
        /// <summary>Compact catalog + on-demand schema fetch (Sprint 67).</summary>
        Tiered,
        /// <summary>Every tool's full schema sent directly — no fetch step (Sprint 68.9).</summary>
        Flat
    }

    /// <summary>
    /// How the assistant transports tool calls to and from the model (Sprint 69). Function-calling uses the
    /// provider's structured tool-call fields and tool-role results. Text renders tool specs into the system
    /// prompt, parses XML tool calls from normal assistant text, and returns results as user-role text.
    /// </summary>
    /// <remarks>Members are appended for serialization stability - never reorder existing values.</remarks>
    public enum ToolCallTransport
    {
        /// <summary>Text for the Local backend, structured function-calling for cloud backends.</summary>
        Auto,
        /// <summary>Use provider-native structured function calling.</summary>
        FunctionCalling,
        /// <summary>Use the text/XML tool protocol intended for weaker local models.</summary>
        Text
    }

    /// <summary>
    /// Whether the assistant marks the stable request prefix (system prompt + tool specs) as cacheable so a
    /// multi-round turn re-sends it as a discounted cache read rather than full-price input (Sprint 74).
    /// </summary>
    /// <remarks>Members are appended for serialization stability — never reorder existing values.</remarks>
    public enum PromptCachingMode
    {
        /// <summary>On for cloud backends (Anthropic/OpenAI), off for the keyless Local backend.</summary>
        Auto,
        /// <summary>Always mark the stable prefix cacheable.</summary>
        On,
        /// <summary>Never request caching (every request billed as full-price input).</summary>
        Off
    }

    /// <summary>
    /// Pluggable web-search backend for <c>molca_web_search</c> (Sprint 75). The provider's subscription key
    /// is a secret and lives in <see cref="AssistantWebAuth"/> (project-scoped EditorPrefs / env var), never on
    /// the settings asset. <see cref="None"/> disables search (fetch is unaffected).
    /// </summary>
    /// <remarks>Members are appended for serialization stability — never reorder existing values.</remarks>
    public enum WebSearchProviderKind
    {
        /// <summary>No search backend configured — <c>molca_web_search</c> degrades to a clear policy result.</summary>
        None,
        /// <summary>Brave Search API (GET, <c>X-Subscription-Token</c> header).</summary>
        Brave,
        /// <summary>Tavily Search API (POST, <c>api_key</c> in the JSON body).</summary>
        Tavily
    }

    /// <summary>
    /// Authored configuration for the in-editor assistant chat (Sprint 16): provider, model, enable
    /// flag, and generation knobs. <b>Holds no secrets</b> — the API key lives in
    /// <see cref="AssistantApiAuth"/> (project-scoped EditorPrefs / env var), never on this asset.
    /// Mirrors <c>NotificationSettings</c> / <c>McpSettings</c>.
    /// </summary>
    /// <remarks>
    /// <b>Which backend a developer talks to is per machine.</b> The fields listed in
    /// <see cref="MolcaLocalSettings.Keys"/> — enable flag, provider, model, base URL, token ceiling,
    /// streaming, reasoning effort, compaction, local context window, sub-agent concurrency — resolve through
    /// <see cref="MolcaLocalSettings"/> and their setters write only there, so the serialized fields stay the
    /// project's authored defaults and picking a different model never dirties a tracked asset. They depend on
    /// which API key the developer holds and how much machine they have, so they cannot be one committed value.
    /// <para>
    /// The rest is project configuration and stays on the asset — in particular
    /// <see cref="WebHostAllowlist"/> and <see cref="WebToolsEnabled"/>, which bound editor network egress:
    /// that is a policy decision, and widening it should be visible in a diff rather than silently local.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    [CreateAssetMenu(fileName = "Assistant Settings", menuName = "Molca/Editor/Assistant Settings", order = 110)]
    public class AssistantSettings : ScriptableObject
    {
        [Tooltip("Project default — overridable per machine. Enable the in-editor assistant chat.")]
        [SerializeField] private bool enabled = false;

        [Tooltip("Project default — overridable per machine. LLM backend. OpenAI-compatible (OpenAI, DeepSeek, …) is the default; Anthropic is also supported; Local drives a self-hosted OpenAI-compatible runtime such as Ollama.")]
        [SerializeField] private LlmProviderKind provider = LlmProviderKind.OpenAI;

        [Tooltip("Project default — overridable per machine. Model id. Leave empty to use the provider's default. For Local/Ollama this is the pulled tag, e.g. gemma4:e4b.")]
        [SerializeField] private string model = "";

        [Tooltip("Project default — overridable per machine. OpenAI-compatible base URL (OpenAI and Local providers). Leave empty for the provider default; set to e.g. https://api.deepseek.com for DeepSeek or http://localhost:11434/v1 for Ollama.")]
        [SerializeField] private string baseUrl = "";

        [Tooltip("Project default — overridable per machine. Output token ceiling per response.")]
        [SerializeField] private int maxTokens = 16000;

        [Tooltip("Project default — overridable per machine. Stream assistant text incrementally (SSE) where the provider supports it. Falls back to non-streaming on tool-call turns and unsupported providers.")]
        [SerializeField] private bool streamResponses = true;

        [Tooltip("Maximum model→tool→model rounds per turn. A model that calls one tool per round hits roughly this many tool calls; multi-step authoring needs more headroom than read-only queries.")]
        [SerializeField] private int maxToolRounds = 25;

        [Tooltip("How tools are exposed to the model. Auto = flat for Local (full schemas sent directly, one-step calls — best for small local models) and tiered for cloud (compact catalog + on-demand schema fetch). Flat/Tiered force a mode.")]
        [SerializeField] private ToolExposureMode toolExposure = ToolExposureMode.Auto;

        [Tooltip("Local text-tool path only: present at most this many of the tools most relevant to the turn in the system prompt, instead of the whole ~180-tool list, so a small local model discriminates over a short menu. Parsing and execution still accept ANY real tool, so this never blocks a valid call. 0 = present the full list (previous behavior). Has no effect on cloud providers.")]
        [SerializeField] private int localPresentedToolLimit = 24;

        [Tooltip("How tool calls are transported. Auto = Text/XML for Local models and structured function-calling for cloud providers. Text returns tool results as normal user text for weaker local models.")]
        [SerializeField] private ToolCallTransport toolCallTransport = ToolCallTransport.Auto;

        [Tooltip("Mark the stable request prefix (system prompt + tool specs) as cacheable so a multi-round turn re-sends it as a discounted cache read instead of full-price input. Auto = on for cloud providers, off for the keyless Local backend.")]
        [SerializeField] private PromptCachingMode promptCaching = PromptCachingMode.Auto;

        [Tooltip("Project default — overridable per machine. Automatically summarize the oldest conversation turns when the estimated context size crosses the threshold, so long sessions keep working without manual pruning.")]
        [SerializeField] private bool autoCompact = true;

        [Tooltip("Project default — overridable per machine. Estimated prompt-token size that triggers auto-compaction before the next turn is sent. Matches the manual context warning by default.")]
        [SerializeField] private int autoCompactThreshold = 120000;

        [Tooltip("Project default — overridable per machine. Local backend only: the context window (num_ctx tokens) the local runtime is configured for. Set this to match your Ollama context length (Ollama app -> Settings -> Context length). Auto-compaction is aligned to this so a long local session compacts BEFORE the runtime silently truncates history, instead of at the much larger cloud threshold above. Ignored for cloud providers.")]
        [SerializeField] private int localContextWindow = 32768;

        [Tooltip("When auto-compacting, first condense old tool-result payloads (no model call) before paying for a turn summary — often enough on its own.")]
        [SerializeField] private bool compactToolResultsFirst = true;

        [Tooltip("How many of the most recent turns keep their tool results verbatim during the digest pass. Lower digests more aggressively; usually below the turn-summary keep count.")]
        [SerializeField] private int keepRecentToolResultTurns = 1;

        [Tooltip("Before answering, query the project knowledge graph with the user's message and inject the top result as transient grounding context. Requires a built graph; no-ops silently otherwise.")]
        [SerializeField] private bool proactiveRetrieval = true;

        [Tooltip("Approximate maximum tokens of retrieved context to inject per turn. Kept modest so grounding can't blow the context it informs.")]
        [SerializeField] private int retrievalTokenBudget = 4000;

        [Tooltip("Per-model USD-per-million-token price overrides for the session cost estimate. Model is substring-matched (e.g. 'claude-opus'); shipped defaults apply to anything not overridden.")]
        [SerializeField] private List<ModelPriceOverride> modelPriceOverrides = new List<ModelPriceOverride>();

        [Tooltip("Maximum read-only research sub-agents the model may spawn per turn. A hard cap so a runaway swarm can't cost more than it saves.")]
        [SerializeField] private int maxSubAgentsPerTurn = 4;

        [Tooltip("Project default — overridable per machine. How many concurrently-running sub-agents are kicked off together (the rest queue in batches).")]
        [SerializeField] private int subAgentConcurrency = 3;

        [Tooltip("Round (model→tool→model) cap per sub-agent. On reaching it the sub-agent returns its partial digest with a truncation note.")]
        [SerializeField] private int subAgentMaxRounds = 6;

        [Tooltip("Per-response output-token ceiling for a sub-agent (kept modest — a sub-agent returns a short digest).")]
        [SerializeField] private int subAgentMaxTokens = 2048;

        [Tooltip("Maximum HTTP attempts per model call, including the first (1 disables retry). A transient 429/5xx/timeout is retried with backoff up to this cap before the turn surfaces an error.")]
        [SerializeField] private int retryMaxAttempts = 3;

        [Tooltip("Seconds to wait for the model to START responding. With streaming on, this bounds only the wait for the first token — a response that keeps streaming is never cut off, however long it runs. With streaming off there is no progress signal, so this bounds the whole exchange and may need raising for a reasoning model on a large context.")]
        [SerializeField] private int requestTimeoutSeconds = 180;

        [Tooltip("Streaming only: the longest gap tolerated between streamed chunks before the response is treated as stalled and retried. This is what makes long turns safe — progress, not total elapsed time, decides whether the model is still working. 0 disables stall detection (a wedged stream would then hang until you press Stop).")]
        [SerializeField] private int streamStallTimeoutSeconds = 90;

        [Tooltip("Stop a turn after the model issues this many identical tool calls (same name + arguments). Guards against an unproductive loop burning every tool round; the turn stays resumable via Continue.")]
        [SerializeField] private int loopBreakThreshold = 4;

        [Tooltip("Per-tool-result character ceiling. A tool result longer than this is truncated (with a marker) as it returns, so one oversized payload can't bloat the rest of the turn.")]
        [SerializeField] private int maxToolResultChars = 100000;

        [Tooltip("Allow the assistant's read-only web tools (molca_web_fetch / molca_web_search) to make outbound HTTP requests. OFF by default — editor network egress is a policy choice. When on, fetch is still restricted to the host allowlist below.")]
        [SerializeField] private bool webToolsEnabled = false;

        [Tooltip("Hosts molca_web_fetch may request. An entry matches its exact host and any subdomain (e.g. 'unity3d.com' allows 'docs.unity3d.com'). A fetch to a host not listed is refused. Empty = nothing is allowed even when web tools are enabled.")]
        [SerializeField] private List<string> webHostAllowlist = new List<string>
        {
            "docs.unity3d.com",
            "docs.unity.com",
            "learn.microsoft.com",
            "docs.microsoft.com",
            "github.com",
            "raw.githubusercontent.com",
        };

        [Tooltip("Web-search backend for molca_web_search. None disables search (fetch is unaffected). The provider's subscription key is stored in project-scoped EditorPrefs / an env var, never on this asset.")]
        [SerializeField] private WebSearchProviderKind webSearchProvider = WebSearchProviderKind.None;

        [Tooltip("Maximum search results molca_web_search returns per query.")]
        [SerializeField] private int webSearchMaxResults = 5;

        [Tooltip("Project default — overridable per machine. Reasoning / extended-thinking budget for capable models. Off (default) sends no reasoning. Low/Medium/High map to an Anthropic thinking budget or an OpenAI reasoning_effort; non-reasoning models and the Local backend ignore it. Higher levels cost more output tokens and add latency, but improve hard multi-step and plan-mode answers.")]
        [SerializeField] private ReasoningEffort reasoningEffort = ReasoningEffort.Off;

        /// <summary>Whether the assistant is enabled on this machine (local override, else project default).</summary>
        public bool Enabled
        {
            get => MolcaLocalOverlay.GetBool(this, MolcaLocalSettings.Keys.AssistantEnabled, enabled);
            set => MolcaLocalOverlay.SetBool(this, MolcaLocalSettings.Keys.AssistantEnabled, ref enabled, value);
        }

        /// <summary>Selected LLM backend on this machine (local override, else project default).</summary>
        public LlmProviderKind Provider
        {
            get => MolcaLocalOverlay.GetEnum(this, MolcaLocalSettings.Keys.AssistantProvider, provider);
            set => MolcaLocalOverlay.SetEnum(this, MolcaLocalSettings.Keys.AssistantProvider, ref provider, value);
        }

        /// <summary>
        /// The configured model id before the provider-default fallback — the local override if set, else the
        /// project default. Blank is a meaningful value here ("use the provider's default"), so an empty local
        /// override is distinct from no override at all.
        /// </summary>
        public string ConfiguredModel
        {
            get => MolcaLocalOverlay.GetString(this, MolcaLocalSettings.Keys.AssistantModel, model);
            set => MolcaLocalOverlay.SetString(this, MolcaLocalSettings.Keys.AssistantModel, ref model, value);
        }

        /// <summary>The resolved model id (configured value, or the provider default if blank).</summary>
        public string Model
        {
            get
            {
                var configured = ConfiguredModel;
                return string.IsNullOrWhiteSpace(configured) ? DefaultModelFor(Provider) : configured;
            }
        }

        /// <summary>
        /// The configured base URL before the provider-default fallback — the local override if set, else the
        /// project default. Blank means "use the provider default"; see <see cref="ConfiguredModel"/>.
        /// </summary>
        public string ConfiguredBaseUrl
        {
            get => MolcaLocalOverlay.GetString(this, MolcaLocalSettings.Keys.AssistantBaseUrl, baseUrl);
            set => MolcaLocalOverlay.SetString(this, MolcaLocalSettings.Keys.AssistantBaseUrl, ref baseUrl, value);
        }

        /// <summary>Output token ceiling per response (local override, else project default), clamped.</summary>
        public int MaxTokens => Mathf.Clamp(
            MolcaLocalOverlay.GetInt(this, MolcaLocalSettings.Keys.AssistantMaxTokens, maxTokens), 256, 64000);

        /// <summary>Sets this machine's output token ceiling.</summary>
        /// <param name="value">The unclamped value; <see cref="MaxTokens"/> clamps on read.</param>
        public void SetMaxTokens(int value)
            => MolcaLocalOverlay.SetInt(this, MolcaLocalSettings.Keys.AssistantMaxTokens, ref maxTokens, value);

        /// <summary>Whether to stream assistant text incrementally where the provider supports it (Sprint 24.7).</summary>
        public bool StreamResponses
        {
            get => MolcaLocalOverlay.GetBool(this, MolcaLocalSettings.Keys.AssistantStreamResponses, streamResponses);
            set => MolcaLocalOverlay.SetBool(
                this, MolcaLocalSettings.Keys.AssistantStreamResponses, ref streamResponses, value);
        }

        /// <summary>Maximum model→tool→model rounds per turn, clamped to a safe range.</summary>
        public int MaxToolRounds => Mathf.Clamp(maxToolRounds, 1, 100);

        /// <summary>How the tool surface is exposed to the model (Sprint 68.9).</summary>
        public ToolExposureMode ToolExposure { get => toolExposure; set => toolExposure = value; }

        /// <summary>
        /// Maximum number of tools to <b>present</b> in the Local text-tool system prompt per turn (Sprint 89):
        /// only the most relevant tools for the current request are listed instead of the full registry, so a
        /// small local model chooses from a short menu. <c>0</c> disables narrowing (the full list is presented,
        /// the previous behavior). This governs presentation only — parsing and execution still accept any real
        /// registry tool, so a narrowing miss can never block a valid call — and it is ignored on cloud providers.
        /// Clamped to a safe range.
        /// </summary>
        public int LocalPresentedToolLimit => Mathf.Clamp(localPresentedToolLimit, 0, 500);

        /// <summary>How tool calls and tool results are transported between the assistant and model (Sprint 69).</summary>
        public ToolCallTransport ToolCallTransport { get => toolCallTransport; set => toolCallTransport = value; }

        /// <summary>How prompt caching of the stable request prefix is decided (Sprint 74).</summary>
        public PromptCachingMode PromptCaching { get => promptCaching; set => promptCaching = value; }

        /// <summary>
        /// Whether to mark the stable request prefix (system prompt + tool specs) as cacheable for the
        /// configured provider (Sprint 74). <see cref="PromptCachingMode.Auto"/> resolves to on for the cloud
        /// backends (Anthropic explicit breakpoints, OpenAI implicit prefix caching) and off for the keyless
        /// <see cref="LlmProviderKind.Local"/> backend, where a self-hosted runtime gains nothing from it.
        /// </summary>
        public bool EnablePromptCaching => promptCaching switch
        {
            PromptCachingMode.On => true,
            PromptCachingMode.Off => false,
            _ => Provider != LlmProviderKind.Local
        };

        /// <summary>
        /// Whether to send every tool's full schema directly (flat) rather than the tiered catalog +
        /// on-demand fetch (Sprint 68.9). Resolves <see cref="ToolExposureMode.Auto"/> to flat for the keyless
        /// <see cref="LlmProviderKind.Local"/> backend (small local models can't reliably navigate the tiered
        /// fetch-then-call step, and local tokens are free) and tiered for the cloud backends.
        /// </summary>
        public bool UseFlatToolExposure => toolExposure switch
        {
            ToolExposureMode.Flat => true,
            ToolExposureMode.Tiered => false,
            _ => Provider == LlmProviderKind.Local
        };

        /// <summary>
        /// Whether to use the Sprint-69 text/XML tool protocol instead of provider-native function calling.
        /// <see cref="ToolCallTransport.Auto"/> resolves to text for <see cref="LlmProviderKind.Local"/> so
        /// weaker local models see both calls and results as ordinary chat text, while cloud providers keep
        /// the proven structured function-calling path.
        /// </summary>
        public bool UseTextToolProtocol => toolCallTransport switch
        {
            ToolCallTransport.Text => true,
            ToolCallTransport.FunctionCalling => false,
            _ => Provider == LlmProviderKind.Local
        };

        /// <summary>
        /// Whether the assistant auto-summarizes the oldest turns once the estimated context size crosses
        /// <see cref="AutoCompactThreshold"/> (Sprint 45). When off, context grows until the user prunes
        /// manually or starts a new chat.
        /// </summary>
        public bool AutoCompact
        {
            get => MolcaLocalOverlay.GetBool(this, MolcaLocalSettings.Keys.AssistantAutoCompact, autoCompact);
            set => MolcaLocalOverlay.SetBool(
                this, MolcaLocalSettings.Keys.AssistantAutoCompact, ref autoCompact, value);
        }

        /// <summary>
        /// Estimated prompt-token size that triggers auto-compaction, clamped to a safe range. Compared
        /// against <see cref="AssistantChatController.EstimateContextTokens(string)"/> before each turn.
        /// </summary>
        public int AutoCompactThreshold => Mathf.Clamp(
            MolcaLocalOverlay.GetInt(
                this, MolcaLocalSettings.Keys.AssistantAutoCompactThreshold, autoCompactThreshold),
            8000, 1000000);

        /// <summary>Sets this machine's auto-compaction threshold.</summary>
        /// <param name="value">The unclamped value; <see cref="AutoCompactThreshold"/> clamps on read.</param>
        public void SetAutoCompactThreshold(int value)
            => MolcaLocalOverlay.SetInt(
                this, MolcaLocalSettings.Keys.AssistantAutoCompactThreshold, ref autoCompactThreshold, value);

        /// <summary>
        /// The context window (num_ctx tokens) the Local runtime is configured for (Sprint 89), clamped to a
        /// safe range. Set to match the Ollama context length so auto-compaction fires before the runtime
        /// truncates. Ignored for cloud providers (they have no fixed local window).
        /// </summary>
        public int LocalContextWindow => Mathf.Clamp(
            MolcaLocalOverlay.GetInt(
                this, MolcaLocalSettings.Keys.AssistantLocalContextWindow, localContextWindow),
            2048, 1000000);

        /// <summary>Sets this machine's local-runtime context window.</summary>
        /// <param name="value">The unclamped value; <see cref="LocalContextWindow"/> clamps on read.</param>
        public void SetLocalContextWindow(int value)
            => MolcaLocalOverlay.SetInt(
                this, MolcaLocalSettings.Keys.AssistantLocalContextWindow, ref localContextWindow, value);

        /// <summary>
        /// The effective auto-compaction threshold for <paramref name="provider"/> (Sprint 89). For the Local
        /// backend this is the smaller of <see cref="AutoCompactThreshold"/> and ~60% of
        /// <see cref="LocalContextWindow"/> — leaving ~40% of the window for the response and the current
        /// turn's growth — so a long local session compacts before Ollama silently truncates history at the
        /// much larger cloud threshold. Cloud providers return <see cref="AutoCompactThreshold"/> unchanged.
        /// </summary>
        /// <param name="provider">The active LLM backend.</param>
        /// <returns>The token size at which auto-compaction should trigger for this backend.</returns>
        public int EffectiveAutoCompactThreshold(LlmProviderKind provider)
        {
            var baseThreshold = AutoCompactThreshold;
            if (provider != LlmProviderKind.Local) return baseThreshold;
            var windowBudget = Mathf.Max(2000, Mathf.RoundToInt(LocalContextWindow * 0.6f));
            return Mathf.Min(baseThreshold, windowBudget);
        }

        /// <summary>
        /// Whether auto-compaction first digests old tool-result payloads (a free, no-model-call pass) before
        /// falling back to the paid turn-summary (Sprint 46). <see cref="AssistantChatController"/> tiers them.
        /// </summary>
        public bool CompactToolResultsFirst { get => compactToolResultsFirst; set => compactToolResultsFirst = value; }

        /// <summary>
        /// How many trailing turns keep their tool results verbatim during the digest pass, clamped to a safe
        /// range (Sprint 46). Typically below <see cref="AutoCompactThreshold"/>'s keep count so the digest
        /// reaches results the turn-summary would otherwise preserve.
        /// </summary>
        public int KeepRecentToolResultTurns => Mathf.Clamp(keepRecentToolResultTurns, 1, 10);

        /// <summary>
        /// Whether to query the knowledge graph with the user's message before answering and inject the
        /// result as transient grounding context (Sprint 47). No-ops when no graph is built.
        /// </summary>
        public bool ProactiveRetrieval { get => proactiveRetrieval; set => proactiveRetrieval = value; }

        /// <summary>Approximate maximum tokens of retrieved context to inject per turn, clamped (Sprint 47).</summary>
        public int RetrievalTokenBudget => Mathf.Clamp(retrievalTokenBudget, 500, 32000);

        /// <summary>
        /// Project-authored per-model price overrides for the session cost estimate (Sprint 53), consulted by
        /// <see cref="AssistantCostTable"/> before the shipped defaults. Never null.
        /// </summary>
        public IReadOnlyList<ModelPriceOverride> ModelPriceOverrides =>
            modelPriceOverrides ?? (modelPriceOverrides = new List<ModelPriceOverride>());

        /// <summary>Hard cap on read-only research sub-agents spawned per turn, clamped (Sprint 56).</summary>
        public int MaxSubAgentsPerTurn => Mathf.Clamp(maxSubAgentsPerTurn, 1, 16);

        /// <summary>How many sub-agents run concurrently within a batch, clamped (Sprint 56).</summary>
        public int SubAgentConcurrency => Mathf.Clamp(
            MolcaLocalOverlay.GetInt(
                this, MolcaLocalSettings.Keys.AssistantSubAgentConcurrency, subAgentConcurrency), 1, 8);

        /// <summary>Sets this machine's sub-agent concurrency.</summary>
        /// <param name="value">The unclamped value; <see cref="SubAgentConcurrency"/> clamps on read.</param>
        public void SetSubAgentConcurrency(int value)
            => MolcaLocalOverlay.SetInt(
                this, MolcaLocalSettings.Keys.AssistantSubAgentConcurrency, ref subAgentConcurrency, value);

        /// <summary>Per-sub-agent round cap, clamped (Sprint 56).</summary>
        public int SubAgentMaxRounds => Mathf.Clamp(subAgentMaxRounds, 1, 25);

        /// <summary>Per-response output-token ceiling for a sub-agent, clamped (Sprint 56).</summary>
        public int SubAgentMaxTokens => Mathf.Clamp(subAgentMaxTokens, 256, 16000);

        /// <summary>
        /// Maximum HTTP attempts per model call (including the first), clamped to a safe range (Sprint 68).
        /// <c>1</c> disables retry; higher values let <see cref="AssistantHttp"/> retry a transient
        /// 429/5xx/connection/timeout failure with jittered backoff before the turn surfaces an error.
        /// </summary>
        public int RetryMaxAttempts => Mathf.Clamp(retryMaxAttempts, 1, 10);

        /// <summary>
        /// How many identical tool calls (same name + normalized arguments) the model may issue in a turn
        /// before <see cref="AssistantChatController"/> breaks the unproductive loop and stops the turn with a
        /// resumable notice, clamped to a safe range (Sprint 68).
        /// </summary>
        public int LoopBreakThreshold => Mathf.Clamp(loopBreakThreshold, 2, 20);

        /// <summary>
        /// Seconds allowed for the model to begin responding. Bounds only the time-to-first-token when
        /// streaming; bounds the whole exchange when not. Clamped to 15…1800.
        /// </summary>
        public int RequestTimeoutSeconds => Mathf.Clamp(requestTimeoutSeconds, 15, 1800);

        /// <summary>
        /// Streaming only: longest tolerated gap between streamed chunks before the attempt is treated as
        /// stalled. Clamped to 15…900; <c>0</c> is preserved to mean "no stall detection".
        /// </summary>
        public int StreamStallTimeoutSeconds =>
            streamStallTimeoutSeconds <= 0 ? 0 : Mathf.Clamp(streamStallTimeoutSeconds, 15, 900);

        /// <summary>
        /// Per-tool-result character ceiling, clamped to a safe range (Sprint 68). A result longer than this is
        /// truncated with a marker as it returns, so a single oversized payload can't bloat the remaining rounds
        /// of the same turn (complements the pre-turn digest/compaction tiers).
        /// </summary>
        public int MaxToolResultChars => Mathf.Clamp(maxToolResultChars, 4000, 2000000);

        /// <summary>
        /// Whether the read-only web tools (<c>molca_web_fetch</c> / <c>molca_web_search</c>) may make outbound
        /// requests (Sprint 75). <b>Off by default</b> — editor network egress is a policy choice. When off, both
        /// tools return an actionable policy result instead of touching the network.
        /// </summary>
        public bool WebToolsEnabled { get => webToolsEnabled; set => webToolsEnabled = value; }

        /// <summary>
        /// Hosts <c>molca_web_fetch</c> is permitted to request (Sprint 75). Never null. An entry matches its
        /// exact host and any subdomain of it — see <see cref="IsHostAllowed(string)"/>.
        /// </summary>
        public IReadOnlyList<string> WebHostAllowlist =>
            webHostAllowlist ?? (webHostAllowlist = new List<string>());

        /// <summary>The configured web-search backend (Sprint 75). <see cref="WebSearchProviderKind.None"/> disables search.</summary>
        public WebSearchProviderKind WebSearchProvider { get => webSearchProvider; set => webSearchProvider = value; }

        /// <summary>Maximum search results <c>molca_web_search</c> returns per query, clamped (Sprint 75).</summary>
        public int WebSearchMaxResults => Mathf.Clamp(webSearchMaxResults, 1, 20);

        /// <summary>
        /// Requested reasoning / extended-thinking budget (Sprint 76). <see cref="ReasoningEffort.Off"/> by
        /// default (lowest cost/latency); mapped per vendor by the provider and ignored for non-reasoning
        /// models and the Local backend. Threaded onto each turn's <see cref="LlmRequest.Reasoning"/>.
        /// </summary>
        public ReasoningEffort ReasoningEffort
        {
            get => MolcaLocalOverlay.GetEnum(
                this, MolcaLocalSettings.Keys.AssistantReasoningEffort, reasoningEffort);
            set => MolcaLocalOverlay.SetEnum(
                this, MolcaLocalSettings.Keys.AssistantReasoningEffort, ref reasoningEffort, value);
        }

        /// <summary>
        /// Anthropic thinking-token budget for a reasoning level (Sprint 76), or <c>0</c> for
        /// <see cref="Molca.Editor.Mcp.Assistant.ReasoningEffort.Off"/>. Anthropic requires
        /// <c>max_tokens &gt; budget_tokens</c>, so the provider clamps the budget below the output ceiling; these
        /// are the nominal targets (Low ≈ 2k, Medium ≈ 8k, High ≈ 16k reasoning tokens).
        /// </summary>
        /// <param name="effort">The requested reasoning level.</param>
        /// <returns>The nominal thinking-token budget, or <c>0</c> when reasoning is off.</returns>
        public static int ThinkingBudgetFor(ReasoningEffort effort) => effort switch
        {
            ReasoningEffort.Low => 2048,
            ReasoningEffort.Medium => 8192,
            ReasoningEffort.High => 16384,
            _ => 0
        };

        /// <summary>
        /// The OpenAI <c>reasoning_effort</c> string for a reasoning level (Sprint 76), or <c>null</c> for
        /// <see cref="Molca.Editor.Mcp.Assistant.ReasoningEffort.Off"/> (the field is omitted entirely).
        /// </summary>
        /// <param name="effort">The requested reasoning level.</param>
        /// <returns><c>"low"</c>/<c>"medium"</c>/<c>"high"</c>, or <c>null</c> when reasoning is off.</returns>
        public static string OpenAiReasoningEffortFor(ReasoningEffort effort) => effort switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            _ => null
        };

        /// <summary>
        /// Whether <paramref name="host"/> is permitted by the fetch allowlist (Sprint 75). Case-insensitive.
        /// An allowlist entry matches the exact host or any subdomain of it (entry <c>unity3d.com</c> allows
        /// <c>docs.unity3d.com</c> but not <c>notunity3d.com</c>). A leading <c>"*."</c> or <c>"."</c> on an
        /// entry is tolerated. Returns <c>false</c> for a blank host or an empty allowlist.
        /// </summary>
        /// <param name="host">The request host (no scheme or port), e.g. <c>docs.unity3d.com</c>.</param>
        /// <returns><c>true</c> if a fetch to <paramref name="host"/> is allowed.</returns>
        public bool IsHostAllowed(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            foreach (var raw in WebHostAllowlist)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var entry = raw.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
                if (entry.Length == 0) continue;
                if (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>The default model id for a provider.</summary>
        public static string DefaultModelFor(LlmProviderKind p) => p switch
        {
            LlmProviderKind.MolcaFree => "molca/free",
            LlmProviderKind.Anthropic => "claude-opus-4-8",
            LlmProviderKind.OpenAI => "gpt-4o-mini",
            LlmProviderKind.Local => "gemma4:e4b",
            _ => ""
        };

        /// <summary>The default base URL for an OpenAI-compatible provider (OpenAI itself).</summary>
        public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";

        /// <summary>The default base URL for the Local provider (a stock Ollama OpenAI-compatible endpoint).</summary>
        public const string DefaultLocalBaseUrl = "http://localhost:11434/v1";

        /// <summary>The entitlement-authenticated OpenAI-compatible assistant route on the Molca server.</summary>
        public static string MolcaFreeBaseUrl =>
            DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/assistant/v1";

        /// <summary>The default base URL for a base-URL-driven provider.</summary>
        public static string DefaultBaseUrlFor(LlmProviderKind p) =>
            p == LlmProviderKind.Local ? DefaultLocalBaseUrl
                : p == LlmProviderKind.MolcaFree ? MolcaFreeBaseUrl
                : DefaultOpenAiBaseUrl;

        /// <summary>True if the selected provider is driven by a configurable OpenAI-compatible base URL.</summary>
        public bool UsesBaseUrl =>
            Provider == LlmProviderKind.OpenAI || Provider == LlmProviderKind.Local;

        /// <summary>
        /// True when the configured backend is a local model known to be unreliable at the assistant's
        /// tool-calling loop (e.g. Gemma 3n e2b/e4b). Such models answer read-only questions acceptably but
        /// frequently drop or malform tool calls, so multi-step authoring should not be relied on. Surfaced
        /// as a non-blocking warning in the Hub — the model still runs.
        /// </summary>
        public bool IsWeakToolModel => IsKnownWeakLocalToolModel(Provider, Model);

        /// <summary>
        /// Whether <paramref name="model"/> on <paramref name="p"/> is a local model known to be too small
        /// for reliable function-calling. Heuristic, matched case-insensitively against the Ollama tag:
        /// Gemma 3n (e2b/e4b) and other ≤2B-class tags.
        /// </summary>
        /// <param name="p">The selected provider; only <see cref="LlmProviderKind.Local"/> is considered.</param>
        /// <param name="model">The resolved model id / Ollama tag.</param>
        /// <returns><c>true</c> if the model is a known-weak local tool model.</returns>
        public static bool IsKnownWeakLocalToolModel(LlmProviderKind p, string model)
        {
            if (p != LlmProviderKind.Local || string.IsNullOrWhiteSpace(model)) return false;
            var m = model.ToLowerInvariant();
            // Gemma 3n (e2b/e4b) shipped without tool tuning and is weak at function calling. Match the
            // family prefix, NOT a bare "e4b"/"e2b" substring: Gemma 4's same-named edge tags
            // (gemma4:e2b / :e4b, released 2026-03) ARE trained for function calling and must not be flagged.
            // The generic ≤2B tags stay a rough heuristic for other tiny, non-tool-tuned models.
            return m.Contains("gemma3n")
                || m.Contains(":1b") || m.Contains(":2b");
        }

        /// <summary>The resolved OpenAI-compatible base URL (configured value, or the provider default if blank).</summary>
        public string BaseUrl
        {
            get
            {
                var active = Provider;
                if (active == LlmProviderKind.MolcaFree) return MolcaFreeBaseUrl;
                var configured = ConfiguredBaseUrl;
                return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrlFor(active) : configured.Trim();
            }
        }

        /// <summary>True if the selected provider has an implementation in this release.</summary>
        public bool IsProviderImplemented =>
            Provider == LlmProviderKind.Anthropic
            || Provider == LlmProviderKind.OpenAI
            || Provider == LlmProviderKind.Local
            || Provider == LlmProviderKind.MolcaFree;

        /// <summary>
        /// Reports the configuration status for the settings UI and validator (Sprint 16.7): missing key
        /// or an unimplemented provider surfaces as <see cref="AssistantConfigStatus.Misconfigured"/>
        /// rather than failing silently at chat time.
        /// </summary>
        public AssistantConfigStatus GetStatus(out string message)
        {
            if (!Enabled)
            {
                message = "Disabled.";
                return AssistantConfigStatus.Disabled;
            }
            var active = Provider;
            if (!IsProviderImplemented)
            {
                message = $"Provider '{active}' is not implemented in this release.";
                return AssistantConfigStatus.Misconfigured;
            }
            if (active == LlmProviderKind.MolcaFree)
            {
                var token = DevEntitlementStore.LoadEffective();
                if (DevEntitlementVerifier.Evaluate(
                        token, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                {
                    message = "Sign in to Molca to use the free assistant.";
                    return AssistantConfigStatus.Misconfigured;
                }
                message = $"Ready ({Model}, routed by Molca).";
                return AssistantConfigStatus.Configured;
            }
            // Local runtimes (Ollama) are keyless by default, so a missing key is not a misconfiguration.
            if (active != LlmProviderKind.Local && !AssistantApiAuth.HasKey(active))
            {
                message = $"No API key. Set it in the Assistant settings or via the {AssistantApiAuth.EnvVarFor(active)} env var.";
                return AssistantConfigStatus.Misconfigured;
            }
            message = active == LlmProviderKind.Local
                ? $"Ready ({Model} @ {BaseUrl})."
                : $"Ready ({Model}).";
            return AssistantConfigStatus.Configured;
        }

        /// <summary>
        /// Builds an <see cref="ILlmProvider"/> for the configured backend, resolving the key from
        /// <see cref="AssistantApiAuth"/>.
        /// </summary>
        /// <exception cref="NotImplementedException">If the selected provider is a reserved seam.</exception>
        public ILlmProvider CreateProvider()
        {
            var active = Provider;
            var key = AssistantApiAuth.GetKey(active);
            var attempts = RetryMaxAttempts;
            var timeouts = new LlmTimeouts(RequestTimeoutSeconds, StreamStallTimeoutSeconds);
            return active switch
            {
                LlmProviderKind.Anthropic => new AnthropicLlmProvider(key, attempts, timeouts),
                LlmProviderKind.OpenAI => new OpenAiCompatibleLlmProvider(BaseUrl, key, LlmProviderKind.OpenAI, requireApiKey: true, maxAttempts: attempts, timeouts: timeouts),
                // Local (Ollama): same OpenAI wire format, optional key (the header is omitted when blank).
                LlmProviderKind.Local => new OpenAiCompatibleLlmProvider(BaseUrl, key, LlmProviderKind.Local, requireApiKey: false, maxAttempts: attempts, timeouts: timeouts),
                LlmProviderKind.MolcaFree => new OpenAiCompatibleLlmProvider(
                    MolcaFreeBaseUrl,
                    DevEntitlementStore.LoadEffective(),
                    LlmProviderKind.MolcaFree,
                    requireApiKey: true,
                    maxAttempts: attempts,
                    additionalHeaders: new Dictionary<string, string>
                    {
                        ["X-Molca-Machine-Id"] = SystemInfo.deviceUniqueIdentifier
                    },
                    timeouts: timeouts),
                _ => throw new NotImplementedException(
                    $"LLM provider '{active}' is not implemented in this release.")
            };
        }

        /// <summary>True when this machine overrides the project default for <paramref name="key"/>.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public bool HasLocalOverride(string key) => MolcaLocalOverlay.IsOverridden(this, key);

        /// <summary>Drops this machine's override for <paramref name="key"/>, restoring the project default.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public void ClearLocalOverride(string key) => MolcaLocalSettings.Instance.Clear(key);

        /// <summary>The committed project default for <see cref="Provider"/>, ignoring any local override.</summary>
        public LlmProviderKind ProjectDefaultProvider => provider;

        /// <summary>The committed project default for <see cref="ReasoningEffort"/>, ignoring any local override.</summary>
        public ReasoningEffort ProjectDefaultReasoningEffort => reasoningEffort;

        /// <summary>Loads the existing assistant settings asset, creating one at the default path if absent.</summary>
        public static AssistantSettings GetOrCreateSettings()
            => MolcaEditorSettingsAsset.GetOrCreate<AssistantSettings>("Assistant Settings.asset");

        /// <summary>
        /// Locates the project's assistant settings asset without creating one (Sprint 75). Returns <c>null</c>
        /// when none exists. Used by the read-only web tools, which must not create config as a side effect —
        /// no settings means web egress is off (the shipped default).
        /// </summary>
        public static AssistantSettings FindSettings()
        {
            foreach (var guid in AssetDatabase.FindAssets($"t:{nameof(AssistantSettings)}"))
            {
                var found = AssetDatabase.LoadAssetAtPath<AssistantSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (found != null) return found;
            }
            return null;
        }
    }
}
