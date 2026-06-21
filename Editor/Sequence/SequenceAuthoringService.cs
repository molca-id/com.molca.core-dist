using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>Whether authoring may overwrite existing steps.</summary>
    public enum AuthoringMode
    {
        /// <summary>Fail if any planned Ref Id already exists.</summary>
        Create,

        /// <summary>Update existing steps (fields/parent) and create missing ones.</summary>
        Merge,
    }

    /// <summary>One auxiliary in a <see cref="PlannedStep"/>.</summary>
    public sealed class PlannedAuxiliary
    {
        /// <summary>StepAuxiliary type name (short or full).</summary>
        public string Type;

        /// <summary>Serialized field values to set on the auxiliary (name → scalar string).</summary>
        public Dictionary<string, string> Fields = new();
    }

    /// <summary>One step in a <see cref="SequenceAuthoringPlan"/>, in Core's vocabulary.</summary>
    public sealed class PlannedStep
    {
        /// <summary>Desired Ref Id; if empty, one is auto-generated (such a step can't be a parent target).</summary>
        public string RefId;

        /// <summary>Step type name (short or full), e.g. <c>PressButtonStep</c>.</summary>
        public string Type;

        /// <summary>Ref Id of the parent step (a planned step or an existing scene step); null = controller root.</summary>
        public string ParentRefId;

        /// <summary>Optional GameObject name (defaults to the type/Ref Id).</summary>
        public string Name;

        /// <summary>Serialized field values to set (name → scalar string).</summary>
        public Dictionary<string, string> Fields = new();

        /// <summary>Auxiliaries to attach (new steps only in <see cref="AuthoringMode.Merge"/>).</summary>
        public List<PlannedAuxiliary> Auxiliaries = new();

        /// <summary>Transient: the resolved/created step, set during apply. Not part of the plan input.</summary>
        internal Step Resolved;
    }

    /// <summary>A declarative whole-graph authoring plan applied by <see cref="SequenceAuthoringService"/>.</summary>
    public sealed class SequenceAuthoringPlan
    {
        /// <summary>Create-vs-merge behavior.</summary>
        public AuthoringMode Mode = AuthoringMode.Create;

        /// <summary>The steps to author, in declaration order.</summary>
        public List<PlannedStep> Steps = new();

        /// <summary>Run validate→safe-fix→re-validate after applying (default true).</summary>
        public bool Remediate = true;

        /// <summary>Apply safe fixes during convergence (default true).</summary>
        public bool ApplySafeFixes = true;
    }

    /// <summary>The result of authoring: plan issues (if any → nothing applied), what changed, and convergence.</summary>
    public sealed class SequenceAuthoringResult
    {
        /// <summary>Pre-apply plan problems. Non-empty means <see cref="Applied"/> is false and the scene is untouched.</summary>
        public List<string> PlanIssues = new();

        /// <summary>Whether the plan was applied.</summary>
        public bool Applied;

        /// <summary>An apply-time failure message (the partial apply was rolled back); null on success.</summary>
        public string Error;

        /// <summary>Ref Ids of steps created.</summary>
        public List<string> CreatedRefIds = new();

        /// <summary>Ref Ids of pre-existing steps updated (merge mode).</summary>
        public List<string> UpdatedRefIds = new();

        /// <summary>Validation error/warning counts immediately after apply (pre safe-fix).</summary>
        public int BeforeErrors, BeforeWarnings;

        /// <summary>Validation error/warning counts after convergence.</summary>
        public int AfterErrors, AfterWarnings;

        /// <summary>True when no errors remain after convergence.</summary>
        public bool Valid;

        /// <summary>Residual findings after convergence (suggestion-enriched).</summary>
        public List<SequenceValidationFinding> Residual = new();

        /// <summary>Revert mechanisms used (Unity-Undo etc.) for honest revert reporting.</summary>
        public HashSet<FixReversibility> RevertMechanisms = new();

        /// <summary>True if any applied fix needs a scene reload.</summary>
        public bool RequiresSceneReload;
    }

    /// <summary>
    /// Applies a declarative <see cref="SequenceAuthoringPlan"/> to a <see cref="SequenceController"/>
    /// transactionally, then converges it to a clean validation state. The Core half of the
    /// "Spec→Sequence generator": the agent decides the plan (spec→plan); this turns plan→validated graph.
    /// </summary>
    /// <remarks>
    /// Reuses the Sprint-19 services (<see cref="StepEditingService"/>, <see cref="StepFieldEditingService"/>,
    /// <see cref="AuxiliaryEditingService"/>) — no reinvented mutation logic. Knows nothing of specs,
    /// connectors, or LLMs.
    /// </remarks>
    public static class SequenceAuthoringService
    {
        /// <summary>Test-only seam: if set, invoked mid-apply to exercise transactional rollback.</summary>
        internal static Action ApplyFaultInjector;

        /// <summary>
        /// Validates <paramref name="plan"/> against <paramref name="controller"/> without mutating
        /// anything: type resolution, duplicate planned Ref Ids, parent resolution, and mode constraints.
        /// </summary>
        /// <param name="controller">The target controller.</param>
        /// <param name="plan">The plan to check.</param>
        /// <returns>A list of issue messages; empty when the plan is applicable.</returns>
        public static List<string> ValidatePlan(SequenceController controller, SequenceAuthoringPlan plan)
        {
            var issues = new List<string>();
            if (controller == null) { issues.Add("No controller."); return issues; }
            if (plan == null || plan.Steps == null || plan.Steps.Count == 0) { issues.Add("Plan has no steps."); return issues; }

            var plannedRefIds = new HashSet<string>();
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var s = plan.Steps[i];
                var where = string.IsNullOrEmpty(s.RefId) ? $"step #{i} ('{s.Type}')" : $"step '{s.RefId}'";

                if (ResolveStepType(s.Type) == null)
                    issues.Add($"{where}: unknown step type '{s.Type}'.");

                if (!string.IsNullOrEmpty(s.RefId) && !plannedRefIds.Add(s.RefId))
                    issues.Add($"Duplicate planned Ref Id '{s.RefId}'.");

                if (s.Auxiliaries != null)
                    foreach (var aux in s.Auxiliaries)
                        if (ResolveAuxiliaryType(aux.Type) == null)
                            issues.Add($"{where}: unknown auxiliary type '{aux.Type}'.");
            }

            // Parent resolution: each parentRefId must be a planned Ref Id or an existing scene step.
            foreach (var s in plan.Steps)
            {
                if (string.IsNullOrEmpty(s.ParentRefId)) continue;
                if (!plannedRefIds.Contains(s.ParentRefId) && FindByRefId(controller, s.ParentRefId) == null)
                    issues.Add($"Step '{s.RefId ?? s.Type}': parentRefId '{s.ParentRefId}' resolves to no planned or existing step.");
            }

            // Create mode: planned Ref Ids must not already exist.
            if (plan.Mode == AuthoringMode.Create)
                foreach (var s in plan.Steps)
                    if (!string.IsNullOrEmpty(s.RefId) && FindByRefId(controller, s.RefId) != null)
                        issues.Add($"Step '{s.RefId}' already exists; use mode 'merge' to update it.");

            return issues;
        }

        /// <summary>
        /// Validates, applies (transactionally), and converges <paramref name="plan"/>.
        /// </summary>
        /// <param name="controller">The target controller.</param>
        /// <param name="plan">The plan to author.</param>
        /// <returns>The authoring result (plan issues, what changed, convergence).</returns>
        public static SequenceAuthoringResult Author(SequenceController controller, SequenceAuthoringPlan plan)
        {
            var result = new SequenceAuthoringResult();

            result.PlanIssues = ValidatePlan(controller, plan);
            if (result.PlanIssues.Count > 0) return result; // zero mutation on any plan error

            if (!ApplyTransactional(controller, plan, result)) return result; // rolled back on failure
            result.Applied = true;

            Converge(controller, plan, result);
            return result;
        }

        // One Undo group spanning every nested service call; reverted wholesale on any exception so a
        // partial graph is never left behind.
        private static bool ApplyTransactional(
            SequenceController controller, SequenceAuthoringPlan plan, SequenceAuthoringResult result)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Author Sequence");

            try
            {
                var byRefId = new Dictionary<string, Step>();
                var newSteps = new HashSet<Step>();

                // Pass 1 — resolve existing (merge) / create missing steps; stamp planned Ref Ids.
                foreach (var planned in plan.Steps)
                {
                    Step step = null;
                    if (plan.Mode == AuthoringMode.Merge && !string.IsNullOrEmpty(planned.RefId))
                        step = FindByRefId(controller, planned.RefId);

                    if (step != null)
                    {
                        result.UpdatedRefIds.Add(planned.RefId);
                    }
                    else
                    {
                        step = StepEditingService.AddStep(
                            controller, ResolveStepType(planned.Type), parent: null, name: planned.Name);
                        if (!string.IsNullOrEmpty(planned.RefId))
                        {
                            Undo.RecordObject(step, "Set Ref Id");
                            step.RefId = planned.RefId;
                            EditorUtility.SetDirty(step);
                        }
                        result.CreatedRefIds.Add(step.RefId);
                        newSteps.Add(step);
                    }

                    if (!string.IsNullOrEmpty(step.RefId)) byRefId[step.RefId] = step;
                    planned.Resolved = step;
                }

                ApplyFaultInjector?.Invoke();

                // Pass 2 — reparent by parentRefId (planned map first, then existing scene steps).
                foreach (var planned in plan.Steps)
                {
                    if (string.IsNullOrEmpty(planned.ParentRefId)) continue;
                    var parent = byRefId.TryGetValue(planned.ParentRefId, out var p) ? p
                        : FindByRefId(controller, planned.ParentRefId);
                    if (parent != null)
                        StepEditingService.ReparentSteps(new[] { planned.Resolved }, parent.transform);
                }

                // Pass 3 — fields + auxiliaries (auxiliaries: new steps only, to avoid merge duplication).
                foreach (var planned in plan.Steps)
                {
                    var step = planned.Resolved;
                    if (planned.Fields is { Count: > 0 })
                        StepFieldEditingService.SetFields(step, planned.Fields);

                    if (planned.Auxiliaries == null || !newSteps.Contains(step)) continue;
                    foreach (var aux in planned.Auxiliaries)
                    {
                        int index = AuxiliaryEditingService.AddAuxiliary(step, ResolveAuxiliaryType(aux.Type));
                        if (index >= 0 && aux.Fields is { Count: > 0 })
                            AuxiliaryEditingService.SetAuxiliaryFields(step, index, aux.Fields);
                    }
                }

                Undo.CollapseUndoOperations(group);
                return true;
            }
            catch (Exception ex)
            {
                Undo.RevertAllDownToGroup(group); // all-or-nothing
                result.Error = ex.Message;
                result.CreatedRefIds.Clear();
                result.UpdatedRefIds.Clear();
                Debug.LogError($"[SequenceAuthoringService] apply failed, rolled back: {ex}");
                return false;
            }
        }

        private static void Converge(
            SequenceController controller, SequenceAuthoringPlan plan, SequenceAuthoringResult result)
        {
            var before = SequenceValidatorRegistry.Run(controller);
            result.BeforeErrors = before.Count(f => f.Severity == SequenceValidationSeverity.Error);
            result.BeforeWarnings = before.Count(f => f.Severity == SequenceValidationSeverity.Warning);

            if (plan.Remediate && plan.ApplySafeFixes)
            {
                var pass = SequenceFixRegistry.ApplyFixes(controller, before, RemediationPolicy.SafeOnly);
                foreach (var m in pass.Mechanisms) result.RevertMechanisms.Add(m);
                result.RequiresSceneReload |= pass.RequiresSceneReload;
            }
            result.RevertMechanisms.Add(FixReversibility.UnityUndo); // the apply itself is Undo-revertible

            var ctx = new SequenceValidationContext(controller, controller.GetComponentsInChildren<Step>(true));
            var after = SequenceValidatorRegistry.Run(controller);
            SequenceFindingEnricher.Enrich(after, ctx);
            result.Residual = after;
            result.AfterErrors = after.Count(f => f.Severity == SequenceValidationSeverity.Error);
            result.AfterWarnings = after.Count(f => f.Severity == SequenceValidationSeverity.Warning);
            result.Valid = result.AfterErrors == 0;
        }

        private static Type ResolveStepType(string name) =>
            string.IsNullOrEmpty(name) ? null
            : TypeCache.GetTypesDerivedFrom<Step>().Append(typeof(Step))
                .FirstOrDefault(t => !t.IsAbstract && (t.Name == name || t.FullName == name));

        private static Type ResolveAuxiliaryType(string name) =>
            string.IsNullOrEmpty(name) ? null
            : TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                .FirstOrDefault(t => !t.IsAbstract && (t.Name == name || t.FullName == name));

        private static Step FindByRefId(SequenceController controller, string refId) =>
            controller.GetComponentsInChildren<Step>(true).FirstOrDefault(s => s.RefId == refId);
    }
}
