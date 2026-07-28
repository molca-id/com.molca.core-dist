using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Core's own MCP tool provider, owning the <c>molca</c> namespace. Exposes the read-only
    /// introspection suite (Sprint 14–15): status, Doctor findings, the live subsystem/service graph,
    /// scene Ref Ids, and build info — plus the content/settings authoring action tools. (Sequence's own
    /// tools — validate/author/remediate/fix a sequence, edit steps/auxiliaries — moved to
    /// <c>com.molca.sequence</c>'s <c>SequenceMcpToolProvider</c>, owning <c>molca.sequence</c>.) Each
    /// tool is defined in its own partial file (<c>CoreMcpToolProvider.&lt;Tool&gt;.cs</c>) as a
    /// <c>Create&lt;Tool&gt;Tool()</c> factory and is surfaced automatically by the convention-based
    /// discovery in <see cref="McpToolProvider.GetTools"/> (Sprint 34) — there is no central registration
    /// list to keep in sync.
    /// </summary>
    /// <remarks>
    /// Most tools are <see cref="McpToolKind.ReadOnly"/>; the mutating tools (e.g.
    /// <c>molca_trigger_build</c>, the content/settings authoring tools, and the
    /// <c>molca_create_mcp_tool</c> meta-codegen) are <see cref="McpToolKind.Action"/> tools, gated by the
    /// allowlist + confirmation guardrails (Sprint 17). SDK forks and add-on packages add their own tools
    /// by subclassing <see cref="McpToolProvider"/> under their own namespace — never by editing this
    /// provider.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    [CreateAssetMenu(fileName = "Core MCP Provider", menuName = "Molca/Editor/MCP/Core Provider", order = 110)]
    public partial class CoreMcpToolProvider : McpToolProvider
    {
        /// <inheritdoc/>
        public override string Namespace => "molca";

        // Tools are discovered by convention from the Create*Tool() factories across this type's partial
        // files (see McpToolProvider.GetTools). No GetTools() override is needed here.
    }
}
