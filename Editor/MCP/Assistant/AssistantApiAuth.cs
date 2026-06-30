using System;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Stores the assistant's LLM API key. The key is a secret, so — per the framework credential rule
    /// (Sprints 4.5 / 16.2) — it is never a SerializeField on any ScriptableObject. It lives in
    /// project-scoped <see cref="MolcaEditorPrefs"/> (per-project, not committed), keyed per provider so
    /// switching backends doesn't clobber another's key. An environment variable, when set, takes
    /// precedence so CI / shared machines can supply the key without it touching EditorPrefs at all.
    /// </summary>
    public static class AssistantApiAuth
    {
        /// <summary>Returns the env var name checked first for a given provider's key.</summary>
        public static string EnvVarFor(LlmProviderKind provider) => provider switch
        {
            LlmProviderKind.Anthropic => "ANTHROPIC_API_KEY",
            LlmProviderKind.OpenAI => "OPENAI_API_KEY",
            // Local runtimes are usually keyless; the env var only matters for a secured/remote Ollama.
            LlmProviderKind.Local => "OLLAMA_API_KEY",
            _ => null
        };

        private static string PrefKey(LlmProviderKind provider) => $"Assistant.ApiKey.{provider}";

        /// <summary>
        /// The effective key for <paramref name="provider"/>: the environment variable if set, otherwise
        /// the project-scoped EditorPrefs value, otherwise empty.
        /// </summary>
        public static string GetKey(LlmProviderKind provider)
        {
            var envName = EnvVarFor(provider);
            if (!string.IsNullOrEmpty(envName))
            {
                var fromEnv = Environment.GetEnvironmentVariable(envName);
                if (!string.IsNullOrEmpty(fromEnv))
                    return fromEnv;
            }
            return MolcaEditorPrefs.GetString(PrefKey(provider), string.Empty);
        }

        /// <summary>Whether a key is available (env var or stored) for the provider.</summary>
        public static bool HasKey(LlmProviderKind provider) => !string.IsNullOrEmpty(GetKey(provider));

        /// <summary>Whether the active key comes from an environment variable (so the UI shows it as read-only).</summary>
        public static bool IsFromEnv(LlmProviderKind provider)
        {
            var envName = EnvVarFor(provider);
            return !string.IsNullOrEmpty(envName) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName));
        }

        /// <summary>Stores the key in project-scoped EditorPrefs. Pass empty to clear.</summary>
        public static void SetKey(LlmProviderKind provider, string key)
        {
            if (string.IsNullOrEmpty(key))
                MolcaEditorPrefs.DeleteKey(PrefKey(provider));
            else
                MolcaEditorPrefs.SetString(PrefKey(provider), key);
        }
    }
}
