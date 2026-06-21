using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Lifecycle/configuration status of a provider, surfaced as a status dot in the settings UI
    /// (mirrors the Notification Providers panel). A provider reports <see cref="Misconfigured"/>
    /// instead of silently producing no tools so configuration mistakes are visible.
    /// </summary>
    public enum McpProviderStatus
    {
        /// <summary>Enabled and ready to serve its tools.</summary>
        Configured,

        /// <summary>Intentionally turned off; contributes no tools.</summary>
        Disabled,

        /// <summary>Enabled but missing required configuration; will not function correctly.</summary>
        Misconfigured
    }

    /// <summary>
    /// Abstract base for an MCP tool provider. A provider is an authored
    /// <see cref="ScriptableObject"/> asset that contributes a namespaced set of tools to the
    /// shared <see cref="McpToolRegistry"/>. This is the single fork extension point: Core ships
    /// its own providers and SDK forks add their own (e.g. <c>molca.vr.*</c>) by subclassing this
    /// type — never by modifying Core (architecture.md layer model, applied to tooling). Mirrors the
    /// <see cref="Molca.Settings.SettingModule"/> / <see cref="BootstrapExtension"/> pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Secrets never live on the asset.</b> A provider SO holds authored configuration only
    /// (endpoints, model names, enable flags). API keys, tokens, and other credentials must be read
    /// from project-scoped <c>EditorPrefs</c> or environment variables at runtime — never declared as
    /// a <c>SerializeField</c> (the same rule as fork credentials and the OpenAI key, Sprints 4.5 /
    /// 16.2). A serialized secret would be committed with the asset and leak.
    /// </para>
    /// <para>
    /// Register a provider asset by adding it to the <see cref="McpSettings"/> provider list. The
    /// registry rejects duplicate <see cref="Namespace"/> values and duplicate tool names at load.
    /// </para>
    /// </remarks>
    public abstract class McpToolProvider : ScriptableObject
    {
        /// <summary>
        /// The unique namespace owned by this provider (e.g. <c>molca</c>, <c>molca.vr</c>). All of
        /// the provider's tool names are expected to be prefixed with it. Two providers may not share
        /// a namespace — the registry rejects collisions at load.
        /// </summary>
        public abstract string Namespace { get; }

        /// <summary>
        /// Returns the tools this provider contributes. Called once per registry build. Implementations
        /// should return an empty sequence (never null) when <see cref="GetStatus"/> is not
        /// <see cref="McpProviderStatus.Configured"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The default implementation <b>discovers tools by convention</b>: every zero-parameter method
        /// on the concrete provider type that returns a <see cref="McpToolDefinition"/> (public or
        /// non-public, instance or static) is invoked and its result yielded — so a provider adds a tool
        /// simply by declaring a <c>Create&lt;Tool&gt;Tool()</c> factory (optionally in its own partial
        /// file), with no central registration list to edit (Sprint 34). Results are sorted by
        /// <see cref="McpToolDefinition.Name"/> for deterministic ordering across reloads/machines, and
        /// cached per concrete <see cref="Type"/> (this method is called once per registry build).
        /// </para>
        /// <para>
        /// Providers that want explicit control (custom ordering, conditional tools) may still override
        /// this method; an override wins and the convention discovery is not used for that type.
        /// </para>
        /// </remarks>
        /// <returns>The provider's tool definitions; never null.</returns>
        public virtual IEnumerable<McpToolDefinition> GetTools() => DiscoverTools(GetType());

        /// <summary>Per-concrete-type cache of convention-discovered tools (built once, reused per build).</summary>
        private static readonly Dictionary<Type, McpToolDefinition[]> _discoveryCache = new();

        private const BindingFlags FactoryBindingFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        /// <summary>
        /// Reflects over <paramref name="providerType"/> (and its base provider types) for zero-parameter
        /// methods returning <see cref="McpToolDefinition"/>, invokes each, and returns the definitions
        /// ordered by name. Used by the default <see cref="GetTools"/>; exposed internally so the
        /// convention guard tests can assert discovery against a type directly.
        /// </summary>
        /// <param name="providerType">The concrete provider type to scan (instance factories are invoked on <c>this</c>).</param>
        /// <returns>The discovered tool definitions, ordered by <see cref="McpToolDefinition.Name"/>.</returns>
        internal IEnumerable<McpToolDefinition> DiscoverTools(Type providerType)
        {
            if (_discoveryCache.TryGetValue(providerType, out var cached))
                return cached;

            var tools = new List<McpToolDefinition>();
            // Walk the type hierarchy so factories declared on a base provider are also found; DeclaredOnly
            // per level avoids double-counting overridden/static members surfaced at multiple levels.
            for (var t = providerType; t != null && typeof(McpToolProvider).IsAssignableFrom(t); t = t.BaseType)
            {
                foreach (var method in t.GetMethods(FactoryBindingFlags))
                {
                    if (method.ReturnType != typeof(McpToolDefinition)) continue;
                    if (method.GetParameters().Length != 0) continue;
                    if (method.IsGenericMethodDefinition) continue;

                    var definition = (McpToolDefinition)method.Invoke(method.IsStatic ? null : this, null);
                    if (definition != null) tools.Add(definition);
                }
            }

            var ordered = tools
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToArray();
            _discoveryCache[providerType] = ordered;
            return ordered;
        }

        /// <summary>
        /// Reports the provider's configuration status for the settings UI status dot and the
        /// validator. Defaults to <see cref="McpProviderStatus.Configured"/>.
        /// </summary>
        /// <returns>The current <see cref="McpProviderStatus"/>.</returns>
        public virtual McpProviderStatus GetStatus() => McpProviderStatus.Configured;

        /// <summary>
        /// Short human-facing message describing the current status (e.g. why it is misconfigured),
        /// shown beside the status dot. Defaults to the status name.
        /// </summary>
        /// <returns>A status message; never null.</returns>
        public virtual string GetStatusMessage() => GetStatus().ToString();

        /// <summary>
        /// Display name for the provider in the settings UI. Defaults to the asset name.
        /// </summary>
        public virtual string DisplayName => name;
    }
}
