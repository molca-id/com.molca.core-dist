using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Validates the MCP provider configuration on every domain reload, in parity with
    /// <c>BootstrapAssetValidator</c>: reports null entries in the provider list, duplicate provider
    /// types, and duplicate tool namespaces so collisions and misconfigurations fail loudly at load
    /// rather than silently dropping tools at request time.
    /// </summary>
    public static class McpAssetValidator
    {
        [InitializeOnLoadMethod]
        private static void RegisterValidator()
        {
            // Defer until the AssetDatabase is fully ready after the reload.
            EditorApplication.delayCall += Validate;
        }

        private static void Validate()
        {
            var settings = MolcaEditorSettings.Instance.McpSettings;
            if (settings == null)
                return; // MCP is optional; absence is not an error.

            var providers = settings.Providers;
            var seenTypes = new HashSet<System.Type>();
            var seenNamespaces = new Dictionary<string, string>();

            for (var i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (provider == null)
                {
                    Debug.LogError($"[Molca MCP] Provider list entry {i} is null in '{settings.name}'.");
                    continue;
                }

                var type = provider.GetType();
                if (!seenTypes.Add(type))
                    Debug.LogError($"[Molca MCP] Duplicate provider type '{type.Name}' in '{settings.name}'.");

                var ns = provider.Namespace;
                if (string.IsNullOrWhiteSpace(ns))
                {
                    Debug.LogError($"[Molca MCP] Provider '{provider.DisplayName}' declares an empty namespace.");
                }
                else if (seenNamespaces.TryGetValue(ns, out var firstOwner))
                {
                    Debug.LogError($"[Molca MCP] Duplicate provider namespace '{ns}' " +
                                   $"('{provider.DisplayName}' collides with '{firstOwner}').");
                }
                else
                {
                    seenNamespaces[ns] = provider.DisplayName;
                }
            }

            // Surface tool-name collisions detected while flattening the registry.
            var registry = settings.BuildRegistry();
            foreach (var error in registry.Errors)
                Debug.LogError($"[Molca MCP] {error}");
        }
    }
}
