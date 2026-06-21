using System.Collections.Generic;
using Molca.Sequence;
using UnityEngine;

namespace Molca.Editor.Graph
{
    /// <summary>
    /// Classifies a sequence-graph edge so the view can style it distinctly (Sprint 8.6).
    /// </summary>
    public enum StepFlowEdgeKind
    {
        /// <summary>Ordinary execution order: a step to its next sibling, or a parent to its first child.</summary>
        Sequential,

        /// <summary>A <see cref="ParallelStep"/> parent to one of its concurrently-activated children.</summary>
        ParallelFanout,

        /// <summary>A <see cref="BranchingStep"/>/<see cref="ConditionalStep"/> parent to one of its mutually-exclusive branches.</summary>
        Branch
    }

    /// <summary>
    /// GUI-free layout math for the sequence graph editor: parent resolution within a step
    /// set and layered top-down positioning. Kept free of any GraphView/UI Toolkit dependency
    /// so it stays unit-testable (the GraphView itself requires a live UI host).
    /// </summary>
    /// <remarks>
    /// Parent/child relationships are derived purely from the transform hierarchy within the
    /// provided set, mirroring <see cref="SequenceValidator"/> — no runtime initialization is
    /// required. The graph editor consumes <see cref="StepEditingService"/> for mutations; this
    /// utility only computes read-only layout positions.
    /// </remarks>
    public static class StepGraphLayoutUtility
    {
        /// <summary>Default horizontal spacing between sibling nodes, in graph units.</summary>
        public const float DefaultColumnSpacing = 240f;

        /// <summary>Default vertical spacing between hierarchy depths, in graph units.</summary>
        public const float DefaultRowSpacing = 170f;

        /// <summary>
        /// Finds the nearest ancestor <see cref="Step"/> of <paramref name="step"/> that is also
        /// present in <paramref name="stepSet"/>, walking up the transform hierarchy.
        /// </summary>
        /// <param name="step">The step whose parent to resolve.</param>
        /// <param name="stepSet">The set of steps under consideration (e.g. one controller's steps).</param>
        /// <returns>The nearest parent step within the set, or <c>null</c> if the step is a root.</returns>
        public static Step FindDirectParentStep(Step step, HashSet<Step> stepSet)
        {
            if (step == null) return null;
            Transform current = step.transform.parent;
            while (current != null)
            {
                var parentStep = current.GetComponent<Step>();
                if (parentStep != null && stepSet.Contains(parentStep)) return parentStep;
                current = current.parent;
            }
            return null;
        }

        /// <summary>
        /// Computes the hierarchy depth (0 = root) of every step in <paramref name="steps"/>,
        /// using transform-derived parentage within the set.
        /// </summary>
        /// <param name="steps">Steps to measure. Null entries are ignored.</param>
        /// <returns>A map from step to its depth.</returns>
        public static Dictionary<Step, int> ComputeDepths(IReadOnlyList<Step> steps)
        {
            var depths = new Dictionary<Step, int>();
            if (steps == null) return depths;

            var set = new HashSet<Step>();
            foreach (var s in steps)
            {
                if (s != null) set.Add(s);
            }

            foreach (var step in set)
            {
                depths[step] = DepthOf(step, set, depths);
            }
            return depths;
        }

        /// <summary>
        /// Computes a tidy layered top-down position for every step: depth drives the row (Y),
        /// while X is assigned by packing leaves left-to-right and centring each parent over the
        /// horizontal span of its children. Subtrees therefore never overlap, and a parent sits
        /// above its own descendants rather than over an unrelated sibling. Deterministic for a
        /// given input order so a rebuild keeps nodes stable.
        /// </summary>
        /// <param name="steps">Steps to lay out, in display order (e.g. transform/sibling order).</param>
        /// <param name="columnSpacing">Horizontal gap between adjacent leaf columns.</param>
        /// <param name="rowSpacing">Vertical gap between depths.</param>
        /// <returns>A map from step to its computed top-left position.</returns>
        public static Dictionary<Step, Vector2> ComputeLayeredPositions(
            IReadOnlyList<Step> steps,
            float columnSpacing = DefaultColumnSpacing,
            float rowSpacing = DefaultRowSpacing)
        {
            var positions = new Dictionary<Step, Vector2>();
            if (steps == null) return positions;

            var depths = ComputeDepths(steps);
            BuildHierarchy(steps, out var roots, out var childrenByParent);

            // Shared leaf cursor across the whole forest so disjoint root subtrees pack into
            // distinct column ranges instead of overlapping.
            int leafColumn = 0;
            foreach (var root in roots)
            {
                AssignSubtreePositions(root, depths, childrenByParent, positions,
                    columnSpacing, rowSpacing, ref leafColumn);
            }
            return positions;
        }

        // Post-order placement: a leaf takes the next free column; an internal node centres over
        // the mean X of its already-placed children. Returns the node's assigned X.
        private static float AssignSubtreePositions(
            Step step,
            Dictionary<Step, int> depths,
            Dictionary<Step, List<Step>> childrenByParent,
            Dictionary<Step, Vector2> positions,
            float columnSpacing,
            float rowSpacing,
            ref int leafColumn)
        {
            float x;
            if (!childrenByParent.TryGetValue(step, out var children) || children.Count == 0)
            {
                x = leafColumn * columnSpacing;
                leafColumn++;
            }
            else
            {
                float sum = 0f;
                foreach (var child in children)
                {
                    sum += AssignSubtreePositions(child, depths, childrenByParent, positions,
                        columnSpacing, rowSpacing, ref leafColumn);
                }
                x = sum / children.Count;
            }

            int depth = depths.TryGetValue(step, out int d) ? d : 0;
            positions[step] = new Vector2(x, depth * rowSpacing);
            return x;
        }

        /// <summary>
        /// Buckets <paramref name="steps"/> into top-level roots and a parent→children map, using
        /// transform-derived parentage within the set and preserving the caller's sibling order.
        /// </summary>
        private static void BuildHierarchy(
            IReadOnlyList<Step> steps,
            out List<Step> roots,
            out Dictionary<Step, List<Step>> childrenByParent)
        {
            roots = new List<Step>();
            childrenByParent = new Dictionary<Step, List<Step>>();
            if (steps == null) return;

            var set = new HashSet<Step>();
            foreach (var s in steps)
            {
                if (s != null) set.Add(s);
            }

            foreach (var step in steps)
            {
                if (step == null) continue;
                var parent = FindDirectParentStep(step, set);
                if (parent == null)
                {
                    roots.Add(step);
                }
                else
                {
                    if (!childrenByParent.TryGetValue(parent, out var list))
                        childrenByParent[parent] = list = new List<Step>();
                    list.Add(step);
                }
            }
        }

        /// <summary>
        /// A directed execution-order connection between two steps, used to draw the graph's edges.
        /// </summary>
        public readonly struct StepFlowEdge
        {
            /// <summary>The step the edge originates from (runs first).</summary>
            public readonly Step From;

            /// <summary>The step the edge points to (runs next, or starts in parallel/as a branch).</summary>
            public readonly Step To;

            /// <summary>How this edge relates the two steps — drives edge styling (Sprint 8.6).</summary>
            public readonly StepFlowEdgeKind Kind;

            /// <param name="from">Origin step.</param>
            /// <param name="to">Destination step.</param>
            /// <param name="kind">Edge relationship kind.</param>
            public StepFlowEdge(Step from, Step to, StepFlowEdgeKind kind = StepFlowEdgeKind.Sequential)
            {
                From = from;
                To = to;
                Kind = kind;
            }
        }

        /// <summary>
        /// Computes the execution-order flow edges for a step set, mirroring
        /// <c>SequenceController</c>'s advancement model:
        /// <list type="bullet">
        /// <item>Sequential siblings connect in order (each step → its next sibling).</item>
        /// <item>A normal parent connects to its <b>first</b> child (descent); the children then chain.</item>
        /// <item>A <see cref="ParallelStep"/> parent <b>fans out</b> to every child (they start together;
        /// no sibling chain among them).</item>
        /// </list>
        /// </summary>
        /// <param name="steps">Steps in display (sibling) order. Null entries are ignored.</param>
        /// <returns>The flow edges; empty if <paramref name="steps"/> is null/empty.</returns>
        /// <remarks>
        /// Topology only — the parallel/branch <i>styling</i> is Sprint 8.6. Parent/child membership is
        /// transform-derived within the set, matching <see cref="FindDirectParentStep"/>.
        /// </remarks>
        public static List<StepFlowEdge> ComputeFlowEdges(IReadOnlyList<Step> steps)
        {
            var edges = new List<StepFlowEdge>();
            if (steps == null) return edges;

            BuildHierarchy(steps, out var roots, out var childrenByParent);

            // Top-level roots run sequentially.
            AppendChain(roots, edges);

            foreach (var kvp in childrenByParent)
            {
                var parent = kvp.Key;
                var children = kvp.Value;
                if (children.Count == 0) continue;

                if (parent is ParallelStep)
                {
                    // Parallel children all activate at once: fan out, no sibling chain.
                    foreach (var child in children)
                        edges.Add(new StepFlowEdge(parent, child, StepFlowEdgeKind.ParallelFanout));
                }
                else if (parent is BranchingStep || parent is ConditionalStep)
                {
                    // Branch children are mutually exclusive (only one runs): fan out, no chain.
                    foreach (var child in children)
                        edges.Add(new StepFlowEdge(parent, child, StepFlowEdgeKind.Branch));
                }
                else
                {
                    // Sequential children: descend into the first, then chain the rest.
                    edges.Add(new StepFlowEdge(parent, children[0]));
                    AppendChain(children, edges);
                }
            }

            return edges;
        }

        private static void AppendChain(List<Step> ordered, List<StepFlowEdge> edges)
        {
            for (int i = 0; i < ordered.Count - 1; i++)
                edges.Add(new StepFlowEdge(ordered[i], ordered[i + 1]));
        }

        private static int DepthOf(Step step, HashSet<Step> set, Dictionary<Step, int> memo)
        {
            if (memo.TryGetValue(step, out int cached)) return cached;
            var parent = FindDirectParentStep(step, set);
            int depth = parent == null ? 0 : DepthOf(parent, set, memo) + 1;
            memo[step] = depth;
            return depth;
        }
    }
}
