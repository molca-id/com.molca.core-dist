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
