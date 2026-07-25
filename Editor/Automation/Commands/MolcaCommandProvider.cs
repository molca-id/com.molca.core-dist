using System.Collections.Generic;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The extension seam for contributing Molca Automation commands. A provider exposes a namespaced set
    /// of <see cref="MolcaCommandDefinition"/>s to the shared <see cref="MolcaCommandRegistry"/>. Core ships
    /// its own providers (including the <c>McpRegistryAutomationAdapter</c> that projects existing MCP
    /// tools, §9.1); SDK forks and add-ons add their own by subclassing this type — never by editing the
    /// kernel. Mirrors the <see cref="Molca.Editor.Mcp.McpToolProvider"/> pattern.
    /// </summary>
    /// <remarks>
    /// Providers are discovered by <c>TypeCache</c> (non-abstract subclasses) or supplied explicitly to
    /// <see cref="MolcaCommandRegistry.Build"/>. Two providers may not share a <see cref="Namespace"/>, and
    /// two commands may not share an id — the registry rejects collisions at build. Secrets never live on a
    /// provider; read them from EditorPrefs/environment at run time (same rule as MCP providers).
    /// </remarks>
    public abstract class MolcaCommandProvider
    {
        /// <summary>
        /// The unique namespace owned by this provider (e.g. <c>molca</c>). Command ids are expected to be
        /// prefixed with it. The registry rejects two providers sharing a namespace.
        /// </summary>
        public abstract string Namespace { get; }

        /// <summary>Display name for discovery UIs. Defaults to <see cref="Namespace"/>.</summary>
        public virtual string DisplayName => Namespace;

        /// <summary>
        /// Returns the commands this provider contributes. Called once per registry build. Implementations
        /// return an empty sequence (never null) when the provider has nothing to contribute.
        /// </summary>
        /// <returns>The provider's command definitions; never null.</returns>
        public abstract IEnumerable<MolcaCommandDefinition> GetCommands();

        /// <summary>Whether this provider is currently enabled and should contribute commands.</summary>
        /// <returns>True to include this provider's commands in the build. Defaults to true.</returns>
        public virtual bool IsEnabled() => true;
    }
}
