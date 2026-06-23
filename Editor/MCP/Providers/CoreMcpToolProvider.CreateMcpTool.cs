using System;
using System.IO;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Meta codegen Action tool that extends the MCP surface itself. Two modes (Sprint 33 / 34):
    /// <list type="bullet">
    /// <item><b>New provider</b> — when <c>providerClassName</c> names no existing type, authors a brand-new
    /// <see cref="McpToolProvider"/> subclass with one tool stub, written into the working area (an
    /// SDK/project fork) per the layer model.</item>
    /// <item><b>Extend existing</b> — when <c>providerClassName</c> names an existing provider in a writable
    /// (non-protected) location, appends a tool-only <i>partial</i> file beside it; convention discovery
    /// (Sprint 34) surfaces the new tool with no <c>GetTools()</c> edit and no settings-list change.</item>
    /// </list>
    /// Either way this never edits Core or an SDK layer: extending a provider that lives in a read-only
    /// package is rejected with a "subclass instead" message.
    /// <para>
    /// <b>Generate-only.</b> Backed by <see cref="McpToolScriptGenerationService"/>, which has no edit
    /// path — it can only write a new provider or append a new tool partial. There is intentionally no
    /// "modify existing tool" mode; changing a tool means editing its source file directly. Both the
    /// tool description and the duplicate-tool error surface this so the assistant doesn't expect an
    /// edit capability that doesn't exist.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Marked <see cref="McpToolReversibility.Irreversible"/> (a new source file is not on Unity's Undo
    /// stack); revert by deleting the file. The new tool/type is only usable AFTER the ensuing domain
    /// reload. In new-provider mode the caller must then create the provider asset and add it to the MCP
    /// Settings provider list (Action tools also need the action allowlist); in extend-existing mode the
    /// provider is already registered, so the tool appears after the reload with no further wiring.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_create_mcp_tool ────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateMcpToolScriptTool() => new McpToolDefinition(
            name: "molca_create_mcp_tool",
            description: "Adds a NEW MCP tool by generating C#. This tool only CREATES source — it cannot "
                       + "edit, modify, rename, or delete an existing tool. To change a tool that already "
                       + "exists, edit its source file with molca_edit_source (read it with molca_read_source "
                       + "first); to replace one, delete its file and create it anew. If 'providerClassName' names an "
                       + "EXISTING provider in a writable (non-package) location, appends a tool-only partial "
                       + "file beside it — convention discovery surfaces the new tool with no settings change "
                       + "(adding a tool whose name already exists is rejected). "
                       + "Otherwise scaffolds a brand-new fork McpToolProvider subclass (class name must "
                       + "end in 'McpToolProvider') with one read-only tool stub. Required: "
                       + "'providerClassName' and 'toolName' (e.g. 'molca_vr_status'); 'toolNamespace' "
                       + "(e.g. 'molca.vr') is required only when creating a new provider. Optional "
                       + "'folder' (new-provider only; defaults to the working-area Mcp folder) and "
                       + "'namespace' (C# namespace, new-provider only). Providers in read-only packages "
                       + "(Core/SDK) cannot be extended — subclass instead. The new tool/type is only "
                       + "usable AFTER Unity recompiles (requiresDomainReload=true); in new-provider mode "
                       + "the caller must then create the provider asset and register it in MCP Settings. "
                       + "Revert by deleting the file.",
            inputSchemaJson: CreateMcpToolSchema,
            execute: ExecuteCreateMcpTool,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private const string CreateMcpToolSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"providerClassName\":{\"type\":\"string\",\"description\":\"Existing provider type to extend, or a new class name ending in 'McpToolProvider'.\"}," +
            "\"toolNamespace\":{\"type\":\"string\",\"description\":\"New-provider only: the provider's owned namespace, e.g. 'molca.vr'. Must be unique across providers.\"}," +
            "\"toolName\":{\"type\":\"string\",\"description\":\"Fully-qualified name of the tool, e.g. 'molca_vr_status'.\"}," +
            "\"folder\":{\"type\":\"string\",\"description\":\"New-provider only: project-relative target folder; omit for the default working-area Mcp folder.\"}," +
            "\"namespace\":{\"type\":\"string\",\"description\":\"New-provider only: optional C# namespace to wrap the class in.\"}}," +
            "\"required\":[\"providerClassName\",\"toolName\"],\"additionalProperties\":false}";

        private static string ExecuteCreateMcpTool(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var providerClassName = args.Value<string>("providerClassName");
            var toolName = args.Value<string>("toolName");

            if (string.IsNullOrWhiteSpace(providerClassName))
                return Error("'providerClassName' is required.");

            // Existing-provider mode: append a tool-only partial beside a provider that already compiles.
            var existing = TypeCache.GetTypesDerivedFrom<McpToolProvider>()
                .FirstOrDefault(t => t.Name == providerClassName);
            if (existing != null)
                return ExecuteExtendExistingProvider(existing, toolName);

            // New-provider mode.
            var toolNamespace = args.Value<string>("toolNamespace");
            var folder = args.Value<string>("folder");
            var ns = args.Value<string>("namespace");

            var result = McpToolScriptGenerationService.CreateProviderScript(
                providerClassName, toolNamespace, toolName, folder, ns, McpProviderTypeExists);

            if (result.Error != null) return Error(result.Error);

            // Import so the new script enters the compile pipeline; the resulting domain reload is what
            // makes the provider type instantiable, hence the explicit caller warning.
            AssetDatabase.ImportAsset(result.Path, ImportAssetOptions.ForceSynchronousImport);

            return new JObject
            {
                ["mode"] = "newProvider",
                ["path"] = result.Path,
                ["providerClassName"] = providerClassName,
                ["toolNamespace"] = toolNamespace,
                ["toolName"] = toolName,
                ["requiresDomainReload"] = true,
                ["note"] = "Provider script created. Wait for Unity to recompile, then create the provider "
                         + "asset (Assets > Create > " + $"Molca/Editor/MCP/{providerClassName}) and add it to "
                         + "the MCP Settings provider list. Action tools must also be added to the action "
                         + "allowlist."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Extend-existing mode: writes a tool-only partial beside an existing provider after verifying it
        /// lives in a writable (non-protected) location and the tool name isn't already taken.
        /// </summary>
        private static string ExecuteExtendExistingProvider(Type providerType, string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return Error("'toolName' is required.");

            var scriptPath = FindProviderScriptPath(providerType);
            if (scriptPath == null)
                return Error($"Could not locate the source file for '{providerType.Name}'. "
                           + "Its main file must be named '" + providerType.Name + ".cs'.");

            if (IsProtectedPath(scriptPath))
                return Error($"'{providerType.Name}' lives in a read-only location ('{scriptPath}') and cannot "
                           + "be extended in place. Subclass McpToolProvider in your working area instead "
                           + "(call this tool with a new providerClassName ending in 'McpToolProvider').");

            // Reject a duplicate tool name early (the registry would otherwise fail the whole provider at load).
            var probe = ScriptableObject.CreateInstance(providerType) as McpToolProvider;
            try
            {
                if (probe != null && probe.GetTools().Any(t =>
                        string.Equals(t.Name, toolName, StringComparison.Ordinal)))
                    return Error($"Provider '{providerType.Name}' already defines a tool named '{toolName}'. "
                               + "This tool only creates new tools — it cannot edit an existing one. To change "
                               + $"'{toolName}', edit its source file with molca_edit_source (find it with "
                               + "molca_read_source); to replace it, delete that file and create the tool anew.");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            }

            var folder = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
            var result = McpToolScriptGenerationService.CreateToolPartial(
                providerType.Name, toolName, folder, providerType.Namespace);

            if (result.Error != null) return Error(result.Error);

            AssetDatabase.ImportAsset(result.Path, ImportAssetOptions.ForceSynchronousImport);

            return new JObject
            {
                ["mode"] = "extendExisting",
                ["path"] = result.Path,
                ["providerClassName"] = providerType.Name,
                ["toolName"] = toolName,
                ["requiresDomainReload"] = true,
                ["note"] = "Tool partial created beside the existing provider. Wait for Unity to recompile — "
                         + "convention discovery surfaces the new tool automatically (no MCP Settings change "
                         + "needed). Implement the Execute stub; if you make it an Action tool, add it to the "
                         + "action allowlist."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Resolves the project-relative path of the <see cref="MonoScript"/> that defines
        /// <paramref name="type"/> (its main file, whose name matches the class). Null if not found.
        /// </summary>
        private static string FindProviderScriptPath(Type type)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{type.Name} t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mono = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (mono != null && mono.GetClass() == type)
                    return path;
            }
            return null;
        }

        /// <summary>
        /// True if a project path is in a read-only protected zone (a UPM package, or an SDK layer under
        /// <c>Assets/_MolcaSDK/</c>) per architecture.md — such locations may only be subclassed, not edited.
        /// </summary>
        private static bool IsProtectedPath(string path) =>
            path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Assets/_MolcaSDK/", StringComparison.OrdinalIgnoreCase);

        private static bool McpProviderTypeExists(string name) =>
            TypeCache.GetTypesDerivedFrom<McpToolProvider>().Any(t => t.Name == name);
    }
}
