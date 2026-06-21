using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// General Unity editor MCP tool provider, owning the <c>molca.unity</c> namespace.
    /// Tool families are defined in partial files (<c>UnityMcpToolProvider.&lt;Family&gt;.cs</c>)
    /// as <c>Create&lt;Tool&gt;Tool()</c> factories and surfaced automatically by the convention-based
    /// discovery in <see cref="McpToolProvider.GetTools"/> (Sprint 34).
    /// </summary>
    /// <remarks>
    /// These tools cover Unity-native editor authoring and discovery surfaces that are not specific
    /// to Molca sequences, content packages, or other Core framework domains.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    [CreateAssetMenu(fileName = "Unity MCP Provider", menuName = "Molca/Editor/MCP/Unity Provider", order = 110)]
    public sealed partial class UnityMcpToolProvider : McpToolProvider
    {
        /// <inheritdoc/>
        public override string Namespace => "molca.unity";

        // Tools are discovered by convention from the Create*Tool() factories across this type's partial
        // files (see McpToolProvider.GetTools). No GetTools() override is needed here.
    }
}
