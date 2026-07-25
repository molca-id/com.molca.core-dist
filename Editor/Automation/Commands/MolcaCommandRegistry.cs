using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Flattens the commands contributed by a set of <see cref="MolcaCommandProvider"/>s into one lookup,
    /// rejecting duplicate provider namespaces and duplicate command ids at build time (a collision fails
    /// loudly into <see cref="Errors"/> rather than silently shadowing). This is the single capability layer
    /// every transport consumes. A pure data structure with no GUI or transport dependency, so it is
    /// directly unit-testable — mirrors <see cref="Molca.Editor.Mcp.McpToolRegistry"/>.
    /// </summary>
    public sealed class MolcaCommandRegistry
    {
        private readonly Dictionary<string, MolcaCommandDefinition> _commands;
        private readonly List<string> _errors;

        private MolcaCommandRegistry(Dictionary<string, MolcaCommandDefinition> commands, List<string> errors)
        {
            _commands = commands;
            _errors = errors;
        }

        /// <summary>All registered commands, in deterministic id order.</summary>
        public IReadOnlyList<MolcaCommandDefinition> Commands =>
            _commands.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

        /// <summary>Collision and configuration errors encountered while building the registry.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>True if any namespace or command id collided, or a provider was malformed.</summary>
        public bool HasErrors => _errors.Count > 0;

        /// <summary>Looks up a command by its stable id.</summary>
        /// <param name="id">The command id (e.g. <c>molca.doctor</c>).</param>
        /// <param name="command">The resolved definition, or null if not found.</param>
        /// <returns>True if a command with that id is registered.</returns>
        public bool TryGet(string id, out MolcaCommandDefinition command)
            => _commands.TryGetValue(id ?? string.Empty, out command);

        /// <summary>
        /// Builds a registry from the given providers. Skips null and disabled providers. Duplicate
        /// namespaces and duplicate command ids are recorded in <see cref="Errors"/> and the colliding
        /// command is dropped, so a misconfigured fork cannot shadow a Core command.
        /// </summary>
        /// <param name="providers">The providers to flatten. Null or empty yields an empty registry.</param>
        /// <returns>A new registry. Always non-null.</returns>
        public static MolcaCommandRegistry Build(IEnumerable<MolcaCommandProvider> providers)
        {
            var commands = new Dictionary<string, MolcaCommandDefinition>(StringComparer.Ordinal);
            var errors = new List<string>();
            var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);

            if (providers == null)
                return new MolcaCommandRegistry(commands, errors);

            foreach (var provider in providers)
            {
                if (provider == null)
                {
                    errors.Add("Null provider entry in the automation provider list.");
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

                if (!SafeIsEnabled(provider, errors))
                    continue;

                IEnumerable<MolcaCommandDefinition> providerCommands;
                try
                {
                    providerCommands = provider.GetCommands() ?? Enumerable.Empty<MolcaCommandDefinition>();
                }
                catch (Exception ex)
                {
                    errors.Add($"Provider '{provider.DisplayName}' threw while enumerating commands: {ex.Message}");
                    continue;
                }

                foreach (var command in providerCommands)
                {
                    if (command == null)
                    {
                        errors.Add($"Provider '{ns}' returned a null command definition.");
                        continue;
                    }

                    if (commands.ContainsKey(command.Id))
                    {
                        errors.Add($"Duplicate command id '{command.Id}' (provider '{ns}'). Ignored.");
                        continue;
                    }

                    commands[command.Id] = command;
                }
            }

            return new MolcaCommandRegistry(commands, errors);
        }

        private static bool SafeIsEnabled(MolcaCommandProvider provider, List<string> errors)
        {
            try { return provider.IsEnabled(); }
            catch (Exception ex)
            {
                errors.Add($"Provider '{provider.DisplayName}' threw from IsEnabled (skipped): {ex.Message}");
                return false;
            }
        }
    }
}
