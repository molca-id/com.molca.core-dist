using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>Synchronous command body. Runs on the Unity main thread; returns a terminal result.</summary>
    /// <param name="context">The run context (arguments, cancellation, progress, transport).</param>
    /// <returns>The terminal <see cref="MolcaCommandResult"/>.</returns>
    public delegate MolcaCommandResult MolcaSyncCommandBody(MolcaCommandContext context);

    /// <summary>Asynchronous command body. Awaited on the main thread; returns a terminal result.</summary>
    /// <param name="context">The run context (arguments, cancellation, progress, transport).</param>
    /// <returns>An awaitable yielding the terminal <see cref="MolcaCommandResult"/>.</returns>
    public delegate Awaitable<MolcaCommandResult> MolcaAsyncCommandBody(MolcaCommandContext context);

    /// <summary>Optional post-execution verifier that proves the command's postcondition (§13).</summary>
    /// <param name="context">The run context.</param>
    /// <returns>An awaitable yielding the verification outcome.</returns>
    public delegate Awaitable<MolcaVerification> MolcaCommandVerifier(MolcaCommandContext context);

    /// <summary>
    /// The single transport-neutral metadata record describing one Molca Automation command. Drives
    /// discovery, mode-gating, policy, resource coordination, confirmation, audit, and every transport
    /// (CLI/Pipeline, Hub, MCP, Assistant, batch) from one registration (§8). Immutable once constructed.
    /// </summary>
    /// <remarks>
    /// A definition carries exactly one of <see cref="Execute"/> or <see cref="ExecuteAsync"/>. Both run
    /// on the Unity main thread; the async form is awaited without blocking it. Long-running commands are
    /// exposed to the CLI through the fire-and-poll run model (Phase 0 finding: the Pipeline request path
    /// caps at ~30s), never as a single awaited call. Produce definitions from a
    /// <see cref="MolcaCommandProvider"/>; the <see cref="MolcaCommandRegistry"/> rejects duplicate ids.
    /// </remarks>
    public sealed class MolcaCommandDefinition
    {
        /// <summary>Stable, globally-unique command id (e.g. <c>molca.doctor</c>). Registry rejects duplicates.</summary>
        public string Id { get; }

        /// <summary>Human/LLM-facing display name.</summary>
        public string DisplayName { get; }

        /// <summary>Human/LLM-facing description of what the command does.</summary>
        public string Description { get; }

        /// <summary>Grouping category for discovery UIs (e.g. <c>diagnostics</c>, <c>build</c>).</summary>
        public string Category { get; }

        /// <summary>JSON Schema (draft 2020-12 object) for the command's arguments, as a JSON string.</summary>
        public string InputSchemaJson { get; }

        /// <summary>Version of the result <c>data</c> payload this command produces.</summary>
        public int OutputSchemaVersion { get; }

        /// <summary>Editor state required to run. See <see cref="MolcaCommandMode"/>.</summary>
        public MolcaCommandMode Mode { get; }

        /// <summary>Read-only vs. action classification. See <see cref="MolcaCommandKind"/>.</summary>
        public MolcaCommandKind Kind { get; }

        /// <summary>How this action can be reverted. Meaningful only for actions.</summary>
        public MolcaCommandReversibility Reversibility { get; }

        /// <summary>Resources this command claims for the duration of its run (§13). Never null.</summary>
        public IReadOnlyList<MolcaResourceClaim> ResourceClaims { get; }

        /// <summary>Execution timeout in milliseconds; ≤ 0 uses the kernel default.</summary>
        public int ExecutionTimeoutMs { get; }

        /// <summary>Whether the command honors cancellation through its <see cref="MolcaCommandContext.CancellationToken"/>.</summary>
        public bool SupportsCancellation { get; }

        /// <summary>Whether an irreversible action requires interactive confirmation before running.</summary>
        public bool RequiresConfirmation { get; }

        /// <summary>Whether the command is safe to run in headless batch mode.</summary>
        public bool SafeInBatchMode { get; }

        /// <summary>Whether the command is safe to run against a connected development Player.</summary>
        public bool SafeAgainstDevelopmentPlayer { get; }

        /// <summary>Synchronous body, or null when this is an async command.</summary>
        public MolcaSyncCommandBody Execute { get; }

        /// <summary>Asynchronous body, or null when this is a synchronous command.</summary>
        public MolcaAsyncCommandBody ExecuteAsync { get; }

        /// <summary>Optional post-execution verifier, or null.</summary>
        public MolcaCommandVerifier Verify { get; }

        /// <summary>True when this command runs asynchronously (<see cref="ExecuteAsync"/> is set).</summary>
        public bool IsAsync => ExecuteAsync != null;

        /// <summary>True when this command declares any mutating resource claim.</summary>
        public bool IsMutating => Kind == MolcaCommandKind.Action || ResourceClaims.Any(IsMutatingClaim);

        /// <summary>
        /// Constructs an immutable command definition. Supply exactly one of <paramref name="execute"/>
        /// or <paramref name="executeAsync"/>.
        /// </summary>
        /// <param name="id">Stable unique command id.</param>
        /// <param name="displayName">Display name.</param>
        /// <param name="description">Description.</param>
        /// <param name="execute">Synchronous body (mutually exclusive with <paramref name="executeAsync"/>).</param>
        /// <param name="executeAsync">Asynchronous body (mutually exclusive with <paramref name="execute"/>).</param>
        /// <param name="category">Discovery category. Defaults to <c>general</c>.</param>
        /// <param name="inputSchemaJson">Arguments JSON Schema; empty becomes the no-arguments schema.</param>
        /// <param name="outputSchemaVersion">Result payload version. Defaults to 1.</param>
        /// <param name="mode">Required editor state. Defaults to <see cref="MolcaCommandMode.Any"/>.</param>
        /// <param name="kind">Read-only vs. action. Defaults to <see cref="MolcaCommandKind.ReadOnly"/>.</param>
        /// <param name="reversibility">Revert path for actions. Defaults to <see cref="MolcaCommandReversibility.None"/>.</param>
        /// <param name="resourceClaims">Declared resource claims. Defaults to <see cref="MolcaResourceClaim.ProjectRead"/>.</param>
        /// <param name="executionTimeoutMs">Execution timeout; ≤ 0 uses the kernel default.</param>
        /// <param name="supportsCancellation">Whether cancellation is honored. Defaults to false.</param>
        /// <param name="requiresConfirmation">Whether confirmation is required. Defaults to false.</param>
        /// <param name="safeInBatchMode">Whether safe headless. Defaults to true for read-only, false for actions.</param>
        /// <param name="safeAgainstDevelopmentPlayer">Whether safe against a dev Player. Defaults to false.</param>
        /// <param name="verify">Optional post-execution verifier.</param>
        /// <exception cref="ArgumentException">If the id is empty, or not exactly one executor is supplied.</exception>
        public MolcaCommandDefinition(
            string id,
            string displayName,
            string description,
            MolcaSyncCommandBody execute = null,
            MolcaAsyncCommandBody executeAsync = null,
            string category = null,
            string inputSchemaJson = null,
            int outputSchemaVersion = 1,
            MolcaCommandMode mode = MolcaCommandMode.Any,
            MolcaCommandKind kind = MolcaCommandKind.ReadOnly,
            MolcaCommandReversibility reversibility = MolcaCommandReversibility.None,
            IReadOnlyList<MolcaResourceClaim> resourceClaims = null,
            int executionTimeoutMs = 0,
            bool supportsCancellation = false,
            bool requiresConfirmation = false,
            bool? safeInBatchMode = null,
            bool safeAgainstDevelopmentPlayer = false,
            MolcaCommandVerifier verify = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Command id must be non-empty.", nameof(id));
            if ((execute == null) == (executeAsync == null))
                throw new ArgumentException(
                    $"Command '{id}' must supply exactly one of execute or executeAsync.", nameof(execute));

            Id = id;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
            Description = description ?? string.Empty;
            Category = string.IsNullOrWhiteSpace(category) ? "general" : category;
            InputSchemaJson = string.IsNullOrWhiteSpace(inputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : inputSchemaJson;
            OutputSchemaVersion = outputSchemaVersion;
            Mode = mode;
            Kind = kind;
            Reversibility = reversibility;
            ResourceClaims = resourceClaims != null && resourceClaims.Count > 0
                ? resourceClaims.ToArray()
                : new[] { MolcaResourceClaim.ProjectRead };
            ExecutionTimeoutMs = executionTimeoutMs;
            SupportsCancellation = supportsCancellation;
            RequiresConfirmation = requiresConfirmation;
            SafeInBatchMode = safeInBatchMode ?? kind == MolcaCommandKind.ReadOnly;
            SafeAgainstDevelopmentPlayer = safeAgainstDevelopmentPlayer;
            Execute = execute;
            ExecuteAsync = executeAsync;
            Verify = verify;
        }

        /// <summary>Whether a resource claim mutates shared state (and must therefore be serialized).</summary>
        /// <param name="claim">The claim to classify.</param>
        /// <returns>True if the claim is a mutating/exclusive resource.</returns>
        public static bool IsMutatingClaim(MolcaResourceClaim claim)
        {
            switch (claim)
            {
                case MolcaResourceClaim.ProjectRead:
                case MolcaResourceClaim.ExternalNetwork:
                case MolcaResourceClaim.DevelopmentPlayer:
                    return false;
                default:
                    return true;
            }
        }
    }
}
