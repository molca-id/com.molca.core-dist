using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// One step of a <see cref="MolcaComposedWorkflow"/>: a registered command invoked with fixed
    /// arguments. <see cref="Critical"/> keeps the kernel's semantics — a failed critical step halts the
    /// workflow; a non-critical failure is recorded and the run continues.
    /// </summary>
    public sealed class MolcaComposedWorkflowStep
    {
        /// <summary>Step id, unique within the workflow. Defaults to <c>{index}-{commandId}</c> when omitted.</summary>
        public string Id { get; set; }

        /// <summary>The registered command to invoke (e.g. <c>molca.doctor</c>).</summary>
        public string CommandId { get; set; }

        /// <summary>Arguments passed to the command. Null means none.</summary>
        public JObject Args { get; set; }

        /// <summary>Whether this step's failure halts the workflow. Defaults to true.</summary>
        public bool Critical { get; set; } = true;

        /// <summary>Serializes this step.</summary>
        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["commandId"] = CommandId,
            ["args"] = Args,
            ["critical"] = Critical
        };

        /// <summary>Deserializes a step; tolerant of missing optional fields.</summary>
        public static MolcaComposedWorkflowStep FromJson(JObject json) => new MolcaComposedWorkflowStep
        {
            Id = json?["id"]?.Type == JTokenType.String ? (string)json["id"] : null,
            CommandId = json?["commandId"]?.ToString(),
            Args = json?["args"] as JObject,
            Critical = json?["critical"]?.Type == JTokenType.Boolean ? (bool)json["critical"] : true
        };
    }

    /// <summary>
    /// A data-driven workflow (Sprint 93.1): an ordered list of registered-command invocations that an
    /// LLM, a person, or a saved asset can author as JSON, compiled onto the existing
    /// <see cref="MolcaWorkflowRunner"/> by <see cref="MolcaComposedWorkflowCompiler"/>. The composition's
    /// policy facets are always computed by the kernel from its members
    /// (<see cref="MolcaComposedWorkflowCompiler.Aggregate"/>) — never declared by the author.
    /// </summary>
    public sealed class MolcaComposedWorkflow
    {
        /// <summary>Stable id for the composition (kebab-case recommended, e.g. <c>release-preflight</c>).</summary>
        public string Id { get; set; }

        /// <summary>Human-facing display name.</summary>
        public string DisplayName { get; set; }

        /// <summary>What the workflow is for.</summary>
        public string Description { get; set; }

        /// <summary>When true, any Warning diagnostic fails the workflow (§11.1).</summary>
        public bool FailOnWarning { get; set; }

        /// <summary>The ordered steps.</summary>
        public List<MolcaComposedWorkflowStep> Steps { get; set; } = new List<MolcaComposedWorkflowStep>();

        /// <summary>Serializes the composition (the shape the <c>molca-workflow</c> artifact carries).</summary>
        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["displayName"] = DisplayName,
            ["description"] = Description,
            ["failOnWarning"] = FailOnWarning,
            ["steps"] = new JArray(Steps.Select(s => s.ToJson()))
        };

        /// <summary>Deserializes a composition. Returns null when <paramref name="json"/> is not an object.</summary>
        public static MolcaComposedWorkflow FromJson(JObject json)
        {
            if (json == null) return null;
            var workflow = new MolcaComposedWorkflow
            {
                Id = json["id"]?.ToString(),
                DisplayName = json["displayName"]?.ToString(),
                Description = json["description"]?.ToString(),
                FailOnWarning = json["failOnWarning"]?.Type == JTokenType.Boolean && (bool)json["failOnWarning"]
            };
            if (json["steps"] is JArray steps)
                foreach (var token in steps)
                    if (token is JObject step)
                        workflow.Steps.Add(MolcaComposedWorkflowStep.FromJson(step));
            return workflow;
        }

        /// <summary>Parses composition JSON text; returns null on malformed input.</summary>
        public static MolcaComposedWorkflow Parse(string json)
        {
            try { return FromJson(JObject.Parse(json)); }
            catch { return null; }
        }
    }

    /// <summary>The kernel-computed policy facets of a composed workflow (Sprint 93.2).</summary>
    public readonly struct MolcaComposedFacets
    {
        /// <summary>Strongest member kind: Action if any member is an Action.</summary>
        public MolcaCommandKind Kind { get; }

        /// <summary>The single compatible mode of all members (members declaring Any are neutral).</summary>
        public MolcaCommandMode Mode { get; }

        /// <summary>Union of all members' resource claims.</summary>
        public IReadOnlyList<MolcaResourceClaim> ResourceClaims { get; }

        /// <summary>
        /// Weakest member revert path across Action members (None &lt; CompensatingAction &lt;
        /// FileSnapshot &lt; UnityUndo). None when the composition has no Action member.
        /// </summary>
        public MolcaCommandReversibility Reversibility { get; }

        /// <summary>True when any member requires confirmation or is an irreversible Action.</summary>
        public bool RequiresConfirmation { get; }

        internal MolcaComposedFacets(MolcaCommandKind kind, MolcaCommandMode mode,
            IReadOnlyList<MolcaResourceClaim> claims, MolcaCommandReversibility reversibility, bool requiresConfirmation)
        {
            Kind = kind;
            Mode = mode;
            ResourceClaims = claims;
            Reversibility = reversibility;
            RequiresConfirmation = requiresConfirmation;
        }

        /// <summary>Serializes the facets for validation reports and proposal UI.</summary>
        public JObject ToJson() => new JObject
        {
            ["kind"] = Kind.ToString(),
            ["mode"] = Mode.ToString(),
            ["reversibility"] = Reversibility.ToString(),
            ["requiresConfirmation"] = RequiresConfirmation,
            ["resourceClaims"] = new JArray(ResourceClaims.Select(c => c.ToString()))
        };
    }

    /// <summary>The outcome of validating a composition against a command registry (Sprint 93.3).</summary>
    public sealed class MolcaComposedValidation
    {
        /// <summary>True when the composition compiles: every issue below Error severity.</summary>
        public bool IsValid => Issues.All(i => i.Severity != MolcaDiagnosticSeverity.Error);

        /// <summary>Per-step and workflow-level issues (stable codes, legible messages). Never null.</summary>
        public List<MolcaDiagnostic> Issues { get; } = new List<MolcaDiagnostic>();

        /// <summary>The aggregated facets (meaningful when <see cref="IsValid"/>).</summary>
        public MolcaComposedFacets Facets { get; internal set; }

        /// <summary>Serializes the report for tools and the proposal panel.</summary>
        public JObject ToJson() => new JObject
        {
            ["valid"] = IsValid,
            ["issues"] = new JArray(Issues.Select(i => i.ToJson())),
            ["facets"] = Facets.ToJson()
        };
    }

    /// <summary>
    /// Validates a <see cref="MolcaComposedWorkflow"/> against the command registry and compiles it into a
    /// <see cref="MolcaWorkflowDefinition"/> whose step bodies invoke the member commands (Sprint 93.1).
    /// </summary>
    /// <remarks>
    /// <b>No policy bypass by construction:</b> the compiled definition is executed through
    /// <see cref="MolcaWorkflowCommandAdapter"/> → the kernel executor like any other command, carrying
    /// the aggregated facets, so authorization, mode gating, resource leases, confirmation, and audit all
    /// apply to the composition as a whole. Step bodies then call the member command's body directly —
    /// deliberately: the workflow already holds the union of the members' resource claims, so re-entering
    /// the executor per step would self-deadlock on exclusive claims. Confirmation is likewise granted
    /// once, for the whole composition; that is why <see cref="Aggregate"/> escalates
    /// <c>RequiresConfirmation</c> when any member is confirmation-requiring or irreversible.
    /// </remarks>
    public static class MolcaComposedWorkflowCompiler
    {
        /// <summary>
        /// Validates the composition: unknown command ids, duplicate step ids, argument-schema violations
        /// (structural subset — see <see cref="MolcaJsonSchemaLite"/>), and mode conflicts all fail at
        /// propose time with per-step messages, so nothing runs on a malformed plan (Sprint 93.3).
        /// </summary>
        /// <param name="workflow">The composition to validate.</param>
        /// <param name="registry">The registry to resolve member commands against.</param>
        /// <returns>The validation report with the aggregated facets.</returns>
        public static MolcaComposedValidation Validate(MolcaComposedWorkflow workflow, MolcaCommandRegistry registry)
        {
            var report = new MolcaComposedValidation();
            if (workflow == null)
            {
                report.Issues.Add(new MolcaDiagnostic("compose.malformed", "The workflow payload is not a valid composition object."));
                return report;
            }
            if (string.IsNullOrWhiteSpace(workflow.Id))
                report.Issues.Add(new MolcaDiagnostic("compose.missing_id", "The workflow must declare a non-empty 'id'."));
            if (workflow.Steps == null || workflow.Steps.Count == 0)
            {
                report.Issues.Add(new MolcaDiagnostic("compose.no_steps", "The workflow must declare at least one step."));
                return report;
            }

            var members = new List<MolcaCommandDefinition>();
            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < workflow.Steps.Count; i++)
            {
                var step = workflow.Steps[i];
                var label = $"step {i + 1}";
                if (step == null || string.IsNullOrWhiteSpace(step.CommandId))
                {
                    report.Issues.Add(new MolcaDiagnostic("compose.missing_command", $"{label}: no 'commandId'."));
                    continue;
                }

                var stepId = EffectiveStepId(step, i);
                if (!stepIds.Add(stepId))
                    report.Issues.Add(new MolcaDiagnostic("compose.duplicate_step_id", $"{label}: duplicate step id '{stepId}'."));

                if (string.Equals(step.CommandId, workflow.Id, StringComparison.Ordinal))
                {
                    report.Issues.Add(new MolcaDiagnostic("compose.self_reference",
                        $"{label}: the workflow invokes itself ('{workflow.Id}')."));
                    continue;
                }

                if (registry == null || !registry.TryGet(step.CommandId, out var command))
                {
                    report.Issues.Add(new MolcaDiagnostic("compose.unknown_command",
                        $"{label}: no command with id '{step.CommandId}' is registered."));
                    continue;
                }
                members.Add(command);

                foreach (var error in MolcaJsonSchemaLite.Validate(step.Args, command.InputSchemaJson))
                    report.Issues.Add(new MolcaDiagnostic("compose.args_invalid",
                        $"{label} ({step.CommandId}): {error}"));
            }

            var facets = Aggregate(members, out var modeConflict);
            if (modeConflict)
                report.Issues.Add(new MolcaDiagnostic("compose.mode_conflict", DescribeModeConflict(members)));
            report.Facets = facets;
            return report;
        }

        /// <summary>
        /// Computes the composition's policy facets from its members (Sprint 93.2) — kernel-owned, never
        /// author-declared: strongest kind, the single compatible mode, the union of resource claims, the
        /// weakest Action revert path, and confirmation escalation for any confirmation-requiring or
        /// irreversible Action member.
        /// </summary>
        /// <param name="members">The resolved member commands.</param>
        /// <param name="modeConflict">True when members demand both Edit and Play.</param>
        /// <returns>The aggregated facets.</returns>
        public static MolcaComposedFacets Aggregate(IReadOnlyList<MolcaCommandDefinition> members, out bool modeConflict)
        {
            modeConflict = false;
            var kind = MolcaCommandKind.ReadOnly;
            var mode = MolcaCommandMode.Any;
            var claims = new HashSet<MolcaResourceClaim>();
            var requiresConfirmation = false;
            MolcaCommandReversibility? reversibility = null;

            foreach (var member in members ?? Array.Empty<MolcaCommandDefinition>())
            {
                if (member.Kind == MolcaCommandKind.Action)
                {
                    kind = MolcaCommandKind.Action;
                    // Weakest revert path wins: one irreversible member makes the composition irreversible.
                    var rank = ReversibilityRank(member.Reversibility);
                    if (reversibility == null || rank < ReversibilityRank(reversibility.Value))
                        reversibility = member.Reversibility;
                    if (member.Reversibility == MolcaCommandReversibility.None)
                        requiresConfirmation = true;
                }
                if (member.RequiresConfirmation) requiresConfirmation = true;

                if (member.Mode != MolcaCommandMode.Any)
                {
                    if (mode == MolcaCommandMode.Any) mode = member.Mode;
                    else if (mode != member.Mode) modeConflict = true;
                }

                foreach (var claim in member.ResourceClaims) claims.Add(claim);
            }

            if (claims.Count == 0) claims.Add(MolcaResourceClaim.ProjectRead);
            return new MolcaComposedFacets(
                kind, mode, claims.OrderBy(c => (int)c).ToArray(),
                reversibility ?? MolcaCommandReversibility.None, requiresConfirmation);
        }

        /// <summary>
        /// Names the commands on each side of a mode conflict, so the report says <em>where</em> to split
        /// rather than only that a split is needed. The boundary is not a limitation of composition: a
        /// Play-mode command requires the editor already playing, and entering Play mode triggers a domain
        /// reload no in-request run survives — so the Play-mode transition belongs to the caller/harness.
        /// </summary>
        private static string DescribeModeConflict(IReadOnlyList<MolcaCommandDefinition> members)
        {
            var edit = members.Where(m => m.Mode == MolcaCommandMode.Edit).Select(m => m.Id).Distinct().ToArray();
            var play = members.Where(m => m.Mode == MolcaCommandMode.Play).Select(m => m.Id).Distinct().ToArray();
            return $"The workflow mixes Edit-mode commands ({string.Join(", ", edit)}) with Play-mode commands "
                 + $"({string.Join(", ", play)}). A workflow runs in one mode: entering Play mode triggers a domain "
                 + "reload the run cannot survive, so split it at the boundary and let the caller own the "
                 + "Play-mode transition between the two workflows.";
        }

        private static int ReversibilityRank(MolcaCommandReversibility reversibility) => reversibility switch
        {
            MolcaCommandReversibility.None => 0,
            MolcaCommandReversibility.CompensatingAction => 1,
            MolcaCommandReversibility.FileSnapshot => 2,
            MolcaCommandReversibility.UnityUndo => 3,
            _ => 0
        };

        /// <summary>
        /// Compiles a validated composition into a runnable <see cref="MolcaWorkflowDefinition"/>. Throws
        /// when validation reports an error — call <see cref="Validate"/> first and surface its report.
        /// </summary>
        /// <param name="workflow">The composition.</param>
        /// <param name="registry">The registry to resolve members against.</param>
        /// <returns>The kernel workflow with aggregated facets.</returns>
        /// <exception cref="InvalidOperationException">If the composition does not validate.</exception>
        public static MolcaWorkflowDefinition Compile(MolcaComposedWorkflow workflow, MolcaCommandRegistry registry)
        {
            var validation = Validate(workflow, registry);
            if (!validation.IsValid)
                throw new InvalidOperationException(
                    "The composed workflow does not validate: " +
                    string.Join("; ", validation.Issues.Where(i => i.Severity == MolcaDiagnosticSeverity.Error).Select(i => i.Message)));

            var steps = new List<MolcaWorkflowStep>(workflow.Steps.Count);
            for (var i = 0; i < workflow.Steps.Count; i++)
            {
                var step = workflow.Steps[i];
                registry.TryGet(step.CommandId, out var command);
                steps.Add(new MolcaWorkflowStep(
                    EffectiveStepId(step, i),
                    $"{command.DisplayName}",
                    MakeStepBody(command, step.Args),
                    critical: step.Critical));
            }

            var facets = validation.Facets;
            return new MolcaWorkflowDefinition(
                workflow.Id,
                string.IsNullOrWhiteSpace(workflow.DisplayName) ? workflow.Id : workflow.DisplayName,
                workflow.Description ?? "Composed workflow.",
                steps,
                failOnWarning: workflow.FailOnWarning,
                mode: facets.Mode,
                kind: facets.Kind,
                resourceClaims: facets.ResourceClaims,
                reversibility: facets.Reversibility,
                requiresConfirmation: facets.RequiresConfirmation);
        }

        /// <summary>
        /// A step body invoking one member command. The child context shares the run id, cancellation,
        /// transport, and confirmation state; the member's result maps onto the step contract (pass =
        /// Succeeded), with its diagnostics and data folded into the workflow bundle.
        /// </summary>
        private static MolcaWorkflowStepBody MakeStepBody(MolcaCommandDefinition command, JObject args)
        {
            return async context =>
            {
                var child = new MolcaCommandContext(
                    context.RunId, command.Id, args != null ? (JObject)args.DeepClone() : new JObject(),
                    context.CancellationToken, context.Transport, context.IsBatchMode, context.IsConfirmed,
                    context.ReportProgress);

                MolcaCommandResult result;
                if (command.ExecuteAsync != null) result = await command.ExecuteAsync(child);
                else result = command.Execute(child);

                if (result == null)
                    return MolcaStepResult.Fail("compose.step_null_result", $"'{command.Id}' returned no result.");
                if (result.Status == MolcaCommandStatus.Succeeded)
                    return MolcaStepResult.Pass(result.Data, result.Diagnostics);
                return MolcaStepResult.Fail(
                    result.Diagnostics != null && result.Diagnostics.Count > 0
                        ? result.Diagnostics
                        : new[] { new MolcaDiagnostic("compose.step_failed", $"'{command.Id}' ended {result.Status}.") },
                    result.Data);
            };
        }

        private static string EffectiveStepId(MolcaComposedWorkflowStep step, int index)
            => string.IsNullOrWhiteSpace(step?.Id) ? $"{index + 1}-{step?.CommandId}" : step.Id.Trim();
    }

    /// <summary>
    /// Structural validation of command arguments against the command's JSON Schema (Sprint 93.3):
    /// object shape, <c>required</c> members, per-property primitive <c>type</c>, and
    /// <c>additionalProperties: false</c>. Deliberately a subset — the kernel carries schemas as strings
    /// and ships no full draft-2020-12 validator; this catches the failure modes that matter at propose
    /// time (missing/misnamed/mistyped arguments) with legible messages.
    /// </summary>
    internal static class MolcaJsonSchemaLite
    {
        /// <summary>Validates <paramref name="args"/> against <paramref name="schemaJson"/>; yields human-readable errors.</summary>
        public static IEnumerable<string> Validate(JObject args, string schemaJson)
        {
            JObject schema;
            try { schema = string.IsNullOrWhiteSpace(schemaJson) ? null : JObject.Parse(schemaJson); }
            catch { yield break; } // an unparseable schema is the command's bug, not the caller's
            if (schema == null) yield break;

            args ??= new JObject();
            var properties = schema["properties"] as JObject;

            if (schema["required"] is JArray required)
                foreach (var name in required)
                    if (args[name.ToString()] == null)
                        yield return $"missing required argument '{name}'";

            var additionalAllowed = schema["additionalProperties"]?.Type != JTokenType.Boolean
                                    || (bool)schema["additionalProperties"];
            foreach (var property in args.Properties())
            {
                var declared = properties?[property.Name] as JObject;
                if (declared == null)
                {
                    if (!additionalAllowed)
                        yield return $"unknown argument '{property.Name}'";
                    continue;
                }
                var expected = declared["type"]?.ToString();
                if (!string.IsNullOrEmpty(expected) && !TypeMatches(property.Value, expected))
                    yield return $"argument '{property.Name}' should be {expected}, got {property.Value.Type}";
            }
        }

        private static bool TypeMatches(JToken value, string type) => type switch
        {
            "string" => value.Type == JTokenType.String,
            "boolean" => value.Type == JTokenType.Boolean,
            "integer" => value.Type == JTokenType.Integer,
            "number" => value.Type == JTokenType.Integer || value.Type == JTokenType.Float,
            "array" => value.Type == JTokenType.Array,
            "object" => value.Type == JTokenType.Object,
            _ => true // unrecognized/composite schema types are not checked
        };
    }
}
