using System.Collections.Generic;
using Molca.Editor.Graph;
using Molca.Sequence;

namespace Molca.Editor.Validation.Validators
{
    /// <summary>
    /// Validates a sequence's control-flow topology over the <b>derived</b> flow-edge model that the
    /// graph editor draws (<see cref="StepGraphLayoutUtility.ComputeFlowEdges"/>) rather than a raw
    /// child walk — so branch/parallel semantics are correct (a <see cref="BranchingStep"/>'s children
    /// are mutually-exclusive fan-out edges, not an all-must-complete chain).
    /// </summary>
    /// <remarks>
    /// This reasons about the same edges <c>SequenceGraphView</c> renders, so the validator and the
    /// visual graph can never disagree. The runtime sequence model has no persisted edges (advancement
    /// is transform-hierarchy + step subclasses); a runtime edge model is deliberately <i>not</i>
    /// introduced. Checks: a control-flow container (<see cref="ParallelStep"/>/<see cref="BranchingStep"/>/
    /// <see cref="ConditionalStep"/>) with no child steps leads nowhere; a branch/conditional with a single
    /// option is likely a mistake; and any step not reachable from a root via the flow edges is detached.
    /// </remarks>
    public sealed class StructuralFlowValidator : ISequenceValidator
    {
        /// <inheritdoc/>
        public string Id => "core.structural-flow";

        /// <inheritdoc/>
        public string Description =>
            "Control-flow topology over the graph editor's derived flow edges: empty/degenerate "
            + "parallel & branch containers and steps detached from the flow.";

        /// <inheritdoc/>
        public IEnumerable<SequenceValidationFinding> Validate(SequenceValidationContext context)
        {
            var findings = new List<SequenceValidationFinding>();
            if (context == null) return findings;

            var steps = new List<Step>();
            foreach (var s in context.Steps)
                if (s != null) steps.Add(s);
            if (steps.Count == 0) return findings;

            var edges = StepGraphLayoutUtility.ComputeFlowEdges(steps);

            // Outgoing edges by origin step — the edge model's own view of "what runs after this".
            var outgoing = new Dictionary<Step, List<StepGraphLayoutUtility.StepFlowEdge>>();
            foreach (var edge in edges)
            {
                if (!outgoing.TryGetValue(edge.From, out var list))
                    outgoing[edge.From] = list = new List<StepGraphLayoutUtility.StepFlowEdge>();
                list.Add(edge);
            }

            CheckControlFlowContainers(context, steps, outgoing, findings);
            CheckReachability(context, steps, edges, findings);
            return findings;
        }

        // A Parallel/Branching/Conditional step's children are fan-out/branch edges. Zero of them means
        // the container resolves to nothing at runtime; exactly one branch is a degenerate (pointless) split.
        private void CheckControlFlowContainers(
            SequenceValidationContext context,
            List<Step> steps,
            Dictionary<Step, List<StepGraphLayoutUtility.StepFlowEdge>> outgoing,
            List<SequenceValidationFinding> findings)
        {
            foreach (var step in steps)
            {
                bool isParallel = step is ParallelStep;
                bool isBranch = step is BranchingStep || step is ConditionalStep;
                if (!isParallel && !isBranch) continue;

                int fanout = 0;
                if (outgoing.TryGetValue(step, out var outs))
                {
                    foreach (var e in outs)
                        if (e.Kind == StepFlowEdgeKind.ParallelFanout || e.Kind == StepFlowEdgeKind.Branch)
                            fanout++;
                }

                if (fanout == 0)
                {
                    findings.Add(new SequenceValidationFinding(
                        Id, "EmptyControlFlowContainer", SequenceValidationSeverity.Warning,
                        $"{(isParallel ? "Parallel" : "Branch")} step '{step.name}' has no child steps — "
                        + "it leads nowhere.", step));
                }
                else if (isBranch && fanout == 1)
                {
                    findings.Add(new SequenceValidationFinding(
                        Id, "DegenerateBranch", SequenceValidationSeverity.Info,
                        $"Branch step '{step.name}' has only one branch; a branch with a single option is "
                        + "equivalent to a normal step.", step));
                }
            }
        }

        // Topological reachability over the flow edges: seed from transform-roots (steps with no parent
        // step), then follow every edge. Anything not reached is detached from the sequence's flow.
        private void CheckReachability(
            SequenceValidationContext context,
            List<Step> steps,
            List<StepGraphLayoutUtility.StepFlowEdge> edges,
            List<SequenceValidationFinding> findings)
        {
            var set = new HashSet<Step>(steps);

            var adjacency = new Dictionary<Step, List<Step>>();
            foreach (var edge in edges)
            {
                if (!adjacency.TryGetValue(edge.From, out var list))
                    adjacency[edge.From] = list = new List<Step>();
                list.Add(edge.To);
            }

            var reachable = new HashSet<Step>();
            var queue = new Queue<Step>();
            foreach (var step in steps)
            {
                if (StepGraphLayoutUtility.FindDirectParentStep(step, set) == null && reachable.Add(step))
                    queue.Enqueue(step);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var next)) continue;
                foreach (var to in next)
                    if (reachable.Add(to)) queue.Enqueue(to);
            }

            foreach (var step in steps)
            {
                if (!reachable.Contains(step))
                {
                    findings.Add(new SequenceValidationFinding(
                        Id, "UnreachableStep", SequenceValidationSeverity.Warning,
                        $"Step '{step.name}' is not reachable from any root in the sequence's flow.", step));
                }
            }
        }
    }
}
