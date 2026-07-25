using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation.Compat
{
    /// <summary>
    /// Projects the released MCP tool surface into transport-neutral automation commands (Phase 1 §9.1),
    /// so the Pipeline adapter, Hub, and batch mode gain immediate access to every existing tool without
    /// migrating each provider. Preserves the exact MCP contract: tool name → command id, input JSON,
    /// mode, kind, reversibility, and timeout carry over unchanged, and the original MCP JSON result is
    /// returned verbatim inside the command result's <c>data</c> field.
    /// </summary>
    /// <remarks>
    /// This is a compatibility bridge, not the migration target — new capabilities are authored as neutral
    /// commands first and projected the other way (§9.2). Confirmation and audit are applied uniformly by
    /// the execution coordinator/policy over the mapped metadata (kind/reversibility/confirmation), so this
    /// adapter only has to carry that metadata across faithfully. Resource claims are inferred
    /// conservatively (read-only → <see cref="MolcaResourceClaim.ProjectRead"/>; every action →
    /// <see cref="MolcaResourceClaim.AssetDatabaseWrite"/>, i.e. treated as exclusive) because MCP tools do
    /// not declare resources; a migrated command declares its real claims.
    /// </remarks>
    public sealed class McpRegistryAutomationAdapter : MolcaCommandProvider
    {
        private readonly IReadOnlyList<McpToolDefinition> _tools;

        /// <inheritdoc/>
        public override string Namespace => "mcp-adapter";

        /// <inheritdoc/>
        public override string DisplayName => "MCP Tool Adapter";

        /// <summary>
        /// Creates an adapter over an explicit set of MCP tools (used by tests for deterministic parity
        /// assertions without touching project settings).
        /// </summary>
        /// <param name="tools">The MCP tools to project. Null becomes empty.</param>
        public McpRegistryAutomationAdapter(IEnumerable<McpToolDefinition> tools)
        {
            _tools = tools?.ToArray() ?? System.Array.Empty<McpToolDefinition>();
        }

        /// <summary>Creates an adapter over the project's configured MCP registry.</summary>
        public McpRegistryAutomationAdapter()
        {
            McpToolRegistry registry = McpSettings.GetOrCreateSettings().BuildRegistry();
            _tools = registry.Tools;
        }

        /// <inheritdoc/>
        public override IEnumerable<MolcaCommandDefinition> GetCommands() => _tools.Select(Project);

        /// <summary>
        /// Projects a single MCP tool into an equivalent neutral command, preserving its exact contract.
        /// Exposed so contract-parity tests can assert the mapping on one tool.
        /// </summary>
        /// <param name="tool">The MCP tool to project.</param>
        /// <returns>The equivalent <see cref="MolcaCommandDefinition"/>.</returns>
        public static MolcaCommandDefinition Project(McpToolDefinition tool)
        {
            var mode = MapMode(tool.Mode);
            var kind = MapKind(tool.Kind);
            var reversibility = MapReversibility(tool.Reversibility);
            var claims = kind == MolcaCommandKind.Action
                ? new[] { MolcaResourceClaim.AssetDatabaseWrite }
                : new[] { MolcaResourceClaim.ProjectRead };
            // §13: irreversible actions always require confirmation; undoable actions do not.
            var requiresConfirmation = kind == MolcaCommandKind.Action &&
                                       reversibility == MolcaCommandReversibility.None;

            if (tool.IsAsync)
            {
                return new MolcaCommandDefinition(
                    id: tool.Name,
                    displayName: tool.Name,
                    description: tool.Description,
                    executeAsync: ctx => RunAsync(tool, ctx),
                    category: "mcp",
                    inputSchemaJson: tool.InputSchemaJson,
                    mode: mode,
                    kind: kind,
                    reversibility: reversibility,
                    resourceClaims: claims,
                    executionTimeoutMs: tool.InvocationTimeoutMs,
                    supportsCancellation: false,
                    requiresConfirmation: requiresConfirmation);
            }

            return new MolcaCommandDefinition(
                id: tool.Name,
                displayName: tool.Name,
                description: tool.Description,
                execute: ctx => RunSync(tool, ctx),
                category: "mcp",
                inputSchemaJson: tool.InputSchemaJson,
                mode: mode,
                kind: kind,
                reversibility: reversibility,
                resourceClaims: claims,
                executionTimeoutMs: tool.InvocationTimeoutMs,
                supportsCancellation: false,
                requiresConfirmation: requiresConfirmation);
        }

        private static MolcaCommandResult RunSync(McpToolDefinition tool, MolcaCommandContext ctx)
        {
            string json = tool.Execute(ctx.ArgumentsJson());
            return WrapToolJson(tool.Name, json);
        }

        private static async Awaitable<MolcaCommandResult> RunAsync(McpToolDefinition tool, MolcaCommandContext ctx)
        {
            string json = await tool.ExecuteAsync(ctx.ArgumentsJson());
            return WrapToolJson(tool.Name, json);
        }

        /// <summary>
        /// Wraps an MCP tool's raw JSON string result as a succeeded command result whose <c>data</c> is
        /// the original JSON (parsed when valid, carried as a string value otherwise) — §9.1 "return the
        /// original MCP JSON inside the command result's data field".
        /// </summary>
        private static MolcaCommandResult WrapToolJson(string commandId, string json)
        {
            JToken data;
            try { data = string.IsNullOrWhiteSpace(json) ? new JObject() : JToken.Parse(json); }
            catch (Newtonsoft.Json.JsonException) { data = new JValue(json); }
            return MolcaCommandResult.Succeeded(commandId, data);
        }

        internal static MolcaCommandMode MapMode(McpToolMode mode) => mode switch
        {
            McpToolMode.Edit => MolcaCommandMode.Edit,
            McpToolMode.Play => MolcaCommandMode.Play,
            _ => MolcaCommandMode.Any
        };

        internal static MolcaCommandKind MapKind(McpToolKind kind) => kind switch
        {
            McpToolKind.Action => MolcaCommandKind.Action,
            _ => MolcaCommandKind.ReadOnly
        };

        internal static MolcaCommandReversibility MapReversibility(McpToolReversibility reversibility) => reversibility switch
        {
            McpToolReversibility.UnityUndo => MolcaCommandReversibility.UnityUndo,
            McpToolReversibility.FileSnapshot => MolcaCommandReversibility.FileSnapshot,
            _ => MolcaCommandReversibility.None
        };
    }
}
