using System;
using System.Collections.Generic;
using Molca.Editor.Utils;
using Molca.Sequence;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Category of a <see cref="SequenceFinding"/>.
    /// </summary>
    public enum SequenceFindingType
    {
        /// <summary>An auxiliary entry is null or its SerializeReference type no longer resolves.</summary>
        BrokenAuxiliary,

        /// <summary>A step has a null or whitespace Ref Id.</summary>
        EmptyRefId,

        /// <summary>Two or more steps share the same Ref Id.</summary>
        DuplicateRefId,

        /// <summary>A disabled parent step has active, enabled child steps that can never run.</summary>
        InactiveParentWithActiveChildren,
    }

    /// <summary>
    /// Severity of a <see cref="SequenceFinding"/>.
    /// </summary>
    public enum SequenceFindingSeverity
    {
        Warning,
        Error,
    }

    /// <summary>
    /// One validation problem found by <see cref="SequenceValidator"/>, consumable by any
    /// view (visualizer tree badges, graph editor node badges, importer dry-run report).
    /// </summary>
    public sealed class SequenceFinding
    {
        /// <summary>Category of the problem.</summary>
        public SequenceFindingType Type { get; }

        /// <summary>Severity of the problem.</summary>
        public SequenceFindingSeverity Severity { get; }

        /// <summary>The step the problem was found on.</summary>
        public Step Step { get; }

        /// <summary>
        /// Index into <see cref="Molca.Sequence.Step.Auxiliaries"/> for
        /// <see cref="SequenceFindingType.BrokenAuxiliary"/>; -1 otherwise.
        /// </summary>
        public int AuxiliaryIndex { get; }

        /// <summary>Human-readable description of the problem.</summary>
        public string Message { get; }

        /// <summary>
        /// Whether <see cref="SequenceValidator.TryFixBrokenAuxiliary"/> can repair this
        /// finding (broken auxiliaries — needs a known replacement type supplied by the caller).
        /// </summary>
        public bool HasFix => Type == SequenceFindingType.BrokenAuxiliary;

        /// <summary>
        /// Whether <see cref="SequenceValidator.TryAutoFix"/> can repair this finding with no caller
        /// input: empty/duplicate Ref Ids (regenerated) and inactive parents (re-enabled). Broken
        /// auxiliaries are excluded — they need a replacement type and so are <see cref="HasFix"/> only.
        /// </summary>
        public bool IsAutoFixable =>
            Type == SequenceFindingType.EmptyRefId
            || Type == SequenceFindingType.DuplicateRefId
            || Type == SequenceFindingType.InactiveParentWithActiveChildren;

        /// <param name="type">Category of the problem.</param>
        /// <param name="severity">Severity of the problem.</param>
        /// <param name="step">The step the problem was found on.</param>
        /// <param name="message">Human-readable description.</param>
        /// <param name="auxiliaryIndex">Auxiliary index for broken-auxiliary findings; -1 otherwise.</param>
        public SequenceFinding(SequenceFindingType type, SequenceFindingSeverity severity, Step step, string message, int auxiliaryIndex = -1)
        {
            Type = type;
            Severity = severity;
            Step = step;
            Message = message;
            AuxiliaryIndex = auxiliaryIndex;
        }
    }

    /// <summary>
    /// GUI-free validation of a sequence's data integrity: broken auxiliaries,
    /// empty/duplicate Ref Ids, and inactive parents gating active children.
    /// Shared by the visualizer, the graph editor, and the step importer.
    /// </summary>
    public static class SequenceValidator
    {
        /// <summary>
        /// Validates every step under <paramref name="controller"/>'s hierarchy
        /// (including inactive GameObjects).
        /// </summary>
        /// <param name="controller">The controller whose steps to validate.</param>
        /// <returns>All findings, empty when the sequence is clean.</returns>
        public static List<SequenceFinding> Validate(SequenceController controller)
        {
            if (controller == null) return new List<SequenceFinding>();
            return Validate(controller.GetComponentsInChildren<Step>(true));
        }

        /// <summary>
        /// Validates the given steps. Parent/child relationships are derived from the
        /// transform hierarchy within the set, so the steps need no prior initialization.
        /// </summary>
        /// <param name="steps">Steps to validate. Null entries are skipped.</param>
        /// <returns>All findings, empty when the steps are clean.</returns>
        public static List<SequenceFinding> Validate(IReadOnlyList<Step> steps)
        {
            var findings = new List<SequenceFinding>();
            if (steps == null) return findings;

            var stepsByRefId = new Dictionary<string, List<Step>>();
            var stepSet = new HashSet<Step>();
            foreach (var step in steps)
            {
                if (step != null) stepSet.Add(step);
            }

            foreach (var step in stepSet)
            {
                ValidateAuxiliaries(step, findings);

                // Collect Ref Ids for the duplicate pass; report empties immediately.
                if (string.IsNullOrWhiteSpace(step.RefId))
                {
                    findings.Add(new SequenceFinding(
                        SequenceFindingType.EmptyRefId, SequenceFindingSeverity.Error, step,
                        $"Step '{step.name}' has an empty Ref Id."));
                }
                else
                {
                    if (!stepsByRefId.TryGetValue(step.RefId, out var list))
                    {
                        list = new List<Step>();
                        stepsByRefId[step.RefId] = list;
                    }
                    list.Add(step);
                }

                ValidateParentActivation(step, stepSet, findings);
            }

            foreach (var pair in stepsByRefId)
            {
                if (pair.Value.Count < 2) continue;
                foreach (var step in pair.Value)
                {
                    findings.Add(new SequenceFinding(
                        SequenceFindingType.DuplicateRefId, SequenceFindingSeverity.Error, step,
                        $"Ref Id '{pair.Key}' is shared by {pair.Value.Count} steps; references will resolve to an arbitrary one."));
                }
            }

            return findings;
        }

        /// <summary>
        /// Repairs a <see cref="SequenceFindingType.BrokenAuxiliary"/> finding by rewriting
        /// the auxiliary's SerializeReference type in the scene YAML to
        /// <paramref name="newType"/> (via <see cref="AuxiliaryTypeFixerUtility"/>).
        /// The scene must be saved and reloaded for the change to take effect — callers
        /// should follow up with <see cref="AuxiliaryTypeFixerUtility.PromptSceneReload"/>.
        /// </summary>
        /// <param name="finding">A broken-auxiliary finding produced by this validator.</param>
        /// <param name="newType">The concrete <see cref="Molca.Sequence.Auxiliary.StepAuxiliary"/> type to assign.</param>
        /// <returns><c>true</c> if the scene YAML was updated.</returns>
        public static bool TryFixBrokenAuxiliary(SequenceFinding finding, Type newType)
        {
            if (finding == null || !finding.HasFix || finding.Step == null || newType == null) return false;
            return AuxiliaryTypeFixerUtility.FixAuxiliaryTypeInYaml(finding.Step, finding.AuxiliaryIndex, newType);
        }

        /// <summary>
        /// Repairs an <see cref="SequenceFinding.IsAutoFixable"/> finding with no caller input, as one
        /// undo group: regenerates the Ref Id for <see cref="SequenceFindingType.EmptyRefId"/> /
        /// <see cref="SequenceFindingType.DuplicateRefId"/>, and re-enables the step component for
        /// <see cref="SequenceFindingType.InactiveParentWithActiveChildren"/>. In-memory and revertible
        /// via Unity Undo (unlike <see cref="TryFixBrokenAuxiliary"/>, which rewrites the scene YAML).
        /// </summary>
        /// <param name="finding">An auto-fixable finding produced by this validator.</param>
        /// <returns><c>true</c> if the finding was repaired.</returns>
        public static bool TryAutoFix(SequenceFinding finding)
        {
            if (finding == null || !finding.IsAutoFixable || finding.Step == null) return false;

            switch (finding.Type)
            {
                case SequenceFindingType.EmptyRefId:
                case SequenceFindingType.DuplicateRefId:
                    UnityEditor.Undo.RecordObject(finding.Step, "Regenerate Ref Id");
                    finding.Step.RefId = Molca.ReferenceSystem.ReferenceGenerator.GenerateUniqueId(finding.Step.RefType);
                    UnityEditor.EditorUtility.SetDirty(finding.Step);
                    return true;

                case SequenceFindingType.InactiveParentWithActiveChildren:
                    UnityEditor.Undo.RecordObject(finding.Step, "Enable Step");
                    finding.Step.enabled = true;
                    UnityEditor.EditorUtility.SetDirty(finding.Step);
                    return true;

                default:
                    return false;
            }
        }

        private static void ValidateAuxiliaries(Step step, List<SequenceFinding> findings)
        {
            var auxiliaries = step.Auxiliaries;
            for (int i = 0; i < auxiliaries.Count; i++)
            {
                var auxiliary = auxiliaries[i];
                // Null = SerializeReference whose type was deleted/renamed (deserializes to null);
                // IsAuxiliaryTypeValid catches resolvable-but-unusable types.
                if (auxiliary == null)
                {
                    findings.Add(new SequenceFinding(
                        SequenceFindingType.BrokenAuxiliary, SequenceFindingSeverity.Error, step,
                        $"Auxiliary {i} on step '{step.name}' is null — its type was likely deleted or renamed.", i));
                }
                else if (!step.IsAuxiliaryTypeValid(auxiliary))
                {
                    findings.Add(new SequenceFinding(
                        SequenceFindingType.BrokenAuxiliary, SequenceFindingSeverity.Error, step,
                        $"Auxiliary {i} ('{auxiliary.GetType().Name}') on step '{step.name}' has an unresolvable type.", i));
                }
            }
        }

        private static void ValidateParentActivation(Step step, HashSet<Step> stepSet, List<SequenceFinding> findings)
        {
            // Only flag the disabled component case: an inactive GameObject already
            // deactivates the whole subtree, so children cannot be "active" under it.
            if (step.enabled || !step.gameObject.activeInHierarchy) return;

            int activeChildren = 0;
            foreach (var child in EnumerateChildSteps(step, stepSet))
            {
                if (child.enabled && child.gameObject.activeInHierarchy) activeChildren++;
            }
            if (activeChildren == 0) return;

            findings.Add(new SequenceFinding(
                SequenceFindingType.InactiveParentWithActiveChildren, SequenceFindingSeverity.Warning, step,
                $"Step '{step.name}' is disabled but has {activeChildren} active child step(s) that will never be reached."));
        }

        /// <summary>
        /// Enumerates steps in <paramref name="stepSet"/> whose nearest step ancestor
        /// (by transform) is <paramref name="parent"/>.
        /// </summary>
        private static IEnumerable<Step> EnumerateChildSteps(Step parent, HashSet<Step> stepSet)
        {
            foreach (var candidate in stepSet)
            {
                if (candidate == parent) continue;
                if (FindNearestParentStep(candidate, stepSet) == parent) yield return candidate;
            }
        }

        private static Step FindNearestParentStep(Step step, HashSet<Step> stepSet)
        {
            Transform current = step.transform.parent;
            while (current != null)
            {
                var parentStep = current.GetComponent<Step>();
                if (parentStep != null && stepSet.Contains(parentStep)) return parentStep;
                current = current.parent;
            }
            return null;
        }
    }
}
