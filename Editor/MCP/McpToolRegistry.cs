using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Flattens the tools contributed by a set of <see cref="McpToolProvider"/> assets into one
    /// lookup, rejecting duplicate provider namespaces and duplicate fully-qualified tool names at
    /// build time (same spirit as the <c>[DependsOn]</c> cycle detection: collisions fail loudly
    /// rather than silently shadowing). This is the single capability layer both front-ends (the IDE
    /// proxy and the in-editor assistant) consume.
    /// </summary>
    /// <remarks>
    /// A registry is a pure data structure with no GUI or transport dependencies, so it is directly
    /// unit-testable. Build one with <see cref="Build"/> from the providers configured in
    /// <see cref="McpSettings"/>. Inspect <see cref="Errors"/> after building to surface collisions.
    /// </remarks>
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, McpToolDefinition> _tools;
        private readonly List<string> _errors;

        private McpToolRegistry(Dictionary<string, McpToolDefinition> tools, List<string> errors)
        {
            _tools = tools;
            _errors = errors;
        }

        /// <summary>All registered tools, in deterministic name order.</summary>
        public IReadOnlyList<McpToolDefinition> Tools => _tools.Values.OrderBy(t => t.Name).ToList();

        /// <summary>Collision and configuration errors encountered while building the registry.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>True if any provider namespace or tool name collided, or a provider was malformed.</summary>
        public bool HasErrors => _errors.Count > 0;

        /// <summary>
        /// Looks up a tool by its fully-qualified name.
        /// </summary>
        /// <param name="name">The tool name (e.g. <c>molca_status</c>).</param>
        /// <param name="tool">The resolved definition, or null if not found.</param>
        /// <returns>True if a tool with that name is registered.</returns>
        public bool TryGet(string name, out McpToolDefinition tool)
            => _tools.TryGetValue(name ?? string.Empty, out tool);

        /// <summary>
        /// Builds a registry from the given providers. Skips null entries and providers whose
        /// <see cref="McpToolProvider.GetStatus"/> is not <see cref="McpProviderStatus.Configured"/>.
        /// Duplicate namespaces and duplicate tool names are recorded in <see cref="Errors"/> and the
        /// colliding tool is dropped, so a misconfigured fork cannot shadow a Core tool.
        /// </summary>
        /// <param name="providers">The provider assets to flatten. Null or empty yields an empty registry.</param>
        /// <returns>A new registry. Always non-null.</returns>
        public static McpToolRegistry Build(IEnumerable<McpToolProvider> providers)
        {
            var tools = new Dictionary<string, McpToolDefinition>();
            var errors = new List<string>();
            var seenNamespaces = new HashSet<string>();

            if (providers == null)
                return new McpToolRegistry(tools, errors);

            foreach (var provider in providers)
            {
                if (provider == null)
                {
                    errors.Add("Null provider entry in the MCP provider list.");
                    continue;
                }

                var ns = provider.Namespace;
                if (string.IsNullOrWhiteSpace(ns))
                {
                    errors.Add($"Provider '{provider.DisplayName}' declares an empty namespace.");
                    continue;
                }

                if (!seenNamespaces.Add(ns))
                {
                    errors.Add($"Duplicate provider namespace '{ns}' (provider '{provider.DisplayName}'). Ignored.");
                    continue;
                }

                // Disabled/misconfigured providers contribute no tools but are not an error here —
                // their status is surfaced separately in the settings UI and validator.
                if (provider.GetStatus() != McpProviderStatus.Configured)
                    continue;

                IEnumerable<McpToolDefinition> providerTools;
                try
                {
                    providerTools = provider.GetTools() ?? Enumerable.Empty<McpToolDefinition>();
                }
                catch (System.Exception ex)
                {
                    errors.Add($"Provider '{provider.DisplayName}' threw while enumerating tools: {ex.Message}");
                    continue;
                }

                foreach (var tool in providerTools)
                {
                    if (tool == null)
                    {
                        errors.Add($"Provider '{ns}' returned a null tool definition.");
                        continue;
                    }

                    if (tools.ContainsKey(tool.Name))
                    {
                        errors.Add($"Duplicate tool name '{tool.Name}' (provider '{ns}'). Ignored.");
                        continue;
                    }

                    tools[tool.Name] = tool;
                }
            }

            return new McpToolRegistry(tools, errors);
        }
    }
}
