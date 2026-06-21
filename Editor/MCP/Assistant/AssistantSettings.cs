using System;
using System.IO;
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
    /// Authored configuration for the in-editor assistant chat (Sprint 16): provider, model, enable
    /// flag, and generation knobs. <b>Holds no secrets</b> — the API key lives in
    /// <see cref="AssistantApiAuth"/> (project-scoped EditorPrefs / env var), never on this asset.
    /// Mirrors <c>NotificationSettings</c> / <c>McpSettings</c>.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    [CreateAssetMenu(fileName = "Assistant Settings", menuName = "Molca/Editor/Assistant Settings", order = 110)]
    public class AssistantSettings : ScriptableObject
    {
        [Tooltip("Enable the in-editor assistant chat.")]
        [SerializeField] private bool enabled = false;

        [Tooltip("LLM backend. OpenAI-compatible (OpenAI, DeepSeek, …) is the default; Anthropic is also supported.")]
        [SerializeField] private LlmProviderKind provider = LlmProviderKind.OpenAI;

        [Tooltip("Model id. Leave empty to use the provider's default.")]
        [SerializeField] private string model = "";

        [Tooltip("OpenAI-compatible base URL (OpenAI provider only). Leave empty for OpenAI; set to e.g. https://api.deepseek.com for DeepSeek.")]
        [SerializeField] private string baseUrl = "";

        [Tooltip("Output token ceiling per response.")]
        [SerializeField] private int maxTokens = 16000;

        [Tooltip("Stream assistant text incrementally (SSE) where the provider supports it. Falls back to non-streaming on tool-call turns and unsupported providers.")]
        [SerializeField] private bool streamResponses = true;

        [Tooltip("Maximum model→tool→model rounds per turn. A model that calls one tool per round hits roughly this many tool calls; multi-step authoring needs more headroom than read-only queries.")]
        [SerializeField] private int maxToolRounds = 25;

        [Tooltip("Automatically summarize the oldest conversation turns when the estimated context size crosses the threshold, so long sessions keep working without manual pruning.")]
        [SerializeField] private bool autoCompact = true;

        [Tooltip("Estimated prompt-token size that triggers auto-compaction before the next turn is sent. Matches the manual context warning by default.")]
        [SerializeField] private int autoCompactThreshold = 120000;

        [Tooltip("When auto-compacting, first condense old tool-result payloads (no model call) before paying for a turn summary — often enough on its own.")]
        [SerializeField] private bool compactToolResultsFirst = true;

        [Tooltip("How many of the most recent turns keep their tool results verbatim during the digest pass. Lower digests more aggressively; usually below the turn-summary keep count.")]
        [SerializeField] private int keepRecentToolResultTurns = 1;

        [Tooltip("Before answering, query the project knowledge graph with the user's message and inject the top result as transient grounding context. Requires a built graph; no-ops silently otherwise.")]
        [SerializeField] private bool proactiveRetrieval = true;

        [Tooltip("Approximate maximum tokens of retrieved context to inject per turn. Kept modest so grounding can't blow the context it informs.")]
        [SerializeField] private int retrievalTokenBudget = 4000;

        /// <summary>Whether the assistant is enabled.</summary>
        public bool Enabled { get => enabled; set => enabled = value; }

        /// <summary>Selected LLM backend.</summary>
        public LlmProviderKind Provider { get => provider; set => provider = value; }

        /// <summary>The resolved model id (configured value, or the provider default if blank).</summary>
        public string Model => string.IsNullOrWhiteSpace(model) ? DefaultModelFor(provider) : model;

        /// <summary>Output token ceiling per response.</summary>
        public int MaxTokens => Mathf.Clamp(maxTokens, 256, 64000);

        /// <summary>Whether to stream assistant text incrementally where the provider supports it (Sprint 24.7).</summary>
        public bool StreamResponses { get => streamResponses; set => streamResponses = value; }

        /// <summary>Maximum model→tool→model rounds per turn, clamped to a safe range.</summary>
        public int MaxToolRounds => Mathf.Clamp(maxToolRounds, 1, 100);

        /// <summary>
        /// Whether the assistant auto-summarizes the oldest turns once the estimated context size crosses
        /// <see cref="AutoCompactThreshold"/> (Sprint 45). When off, context grows until the user prunes
        /// manually or starts a new chat.
        /// </summary>
        public bool AutoCompact { get => autoCompact; set => autoCompact = value; }

        /// <summary>
        /// Estimated prompt-token size that triggers auto-compaction, clamped to a safe range. Compared
        /// against <see cref="AssistantChatController.EstimateContextTokens(string)"/> before each turn.
        /// </summary>
        public int AutoCompactThreshold => Mathf.Clamp(autoCompactThreshold, 8000, 1000000);

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

        /// <summary>The default model id for a provider.</summary>
        public static string DefaultModelFor(LlmProviderKind p) => p switch
        {
            LlmProviderKind.Anthropic => "claude-opus-4-8",
            LlmProviderKind.OpenAI => "gpt-4o-mini",
            _ => ""
        };

        /// <summary>The default base URL for an OpenAI-compatible provider (OpenAI itself).</summary>
        public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";

        /// <summary>The resolved OpenAI-compatible base URL (configured value, or OpenAI's if blank).</summary>
        public string BaseUrl => string.IsNullOrWhiteSpace(baseUrl) ? DefaultOpenAiBaseUrl : baseUrl.Trim();

        /// <summary>True if the selected provider has an implementation in this release.</summary>
        public bool IsProviderImplemented =>
            provider == LlmProviderKind.Anthropic || provider == LlmProviderKind.OpenAI;

        /// <summary>
        /// Reports the configuration status for the settings UI and validator (Sprint 16.7): missing key
        /// or an unimplemented provider surfaces as <see cref="AssistantConfigStatus.Misconfigured"/>
        /// rather than failing silently at chat time.
        /// </summary>
        public AssistantConfigStatus GetStatus(out string message)
        {
            if (!enabled)
            {
                message = "Disabled.";
                return AssistantConfigStatus.Disabled;
            }
            if (!IsProviderImplemented)
            {
                message = $"Provider '{provider}' is not implemented in this release.";
                return AssistantConfigStatus.Misconfigured;
            }
            if (!AssistantApiAuth.HasKey(provider))
            {
                message = $"No API key. Set it in the Assistant settings or via the {AssistantApiAuth.EnvVarFor(provider)} env var.";
                return AssistantConfigStatus.Misconfigured;
            }
            message = $"Ready ({Model}).";
            return AssistantConfigStatus.Configured;
        }

        /// <summary>
        /// Builds an <see cref="ILlmProvider"/> for the configured backend, resolving the key from
        /// <see cref="AssistantApiAuth"/>.
        /// </summary>
        /// <exception cref="NotImplementedException">If the selected provider is a reserved seam.</exception>
        public ILlmProvider CreateProvider()
        {
            var key = AssistantApiAuth.GetKey(provider);
            return provider switch
            {
                LlmProviderKind.Anthropic => new AnthropicLlmProvider(key),
                LlmProviderKind.OpenAI => new OpenAiCompatibleLlmProvider(BaseUrl, key),
                _ => throw new NotImplementedException(
                    $"LLM provider '{provider}' is not implemented in this release. Use Anthropic or OpenAI.")
            };
        }

        /// <summary>Loads the existing assistant settings asset, creating one at the default path if absent.</summary>
        public static AssistantSettings GetOrCreateSettings()
            => MolcaEditorSettingsAsset.GetOrCreate<AssistantSettings>("Assistant Settings.asset");
    }
}
