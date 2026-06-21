using System;
using System.Collections.Generic;

namespace Molca.Utilities
{
    /// <summary>
    /// Generic topological sort utility. Produces a deterministic ordering of nodes
    /// such that every node appears after all of its declared dependencies, with a
    /// caller-supplied tiebreaker controlling order between unrelated nodes.
    /// </summary>
    /// <remarks>
    /// Uses Kahn's algorithm. On encountering a dependency cycle, the algorithm emits
    /// every reachable acyclic node first and then appends the cycle participants in
    /// tiebreaker order via <paramref name="cycleParticipants"/>; the caller decides
    /// what to do (e.g., log a warning and continue, or fall back to a different
    /// ordering). Dependencies referenced by <paramref name="getDependencies"/> that
    /// are not present in <paramref name="nodes"/> are silently ignored — the sort
    /// only orders nodes the caller asked about.
    /// </remarks>
    public static class TopologicalSort
    {
        /// <summary>
        /// Sorts <paramref name="nodes"/> so each node follows all of its dependencies.
        /// </summary>
        /// <typeparam name="T">Element type. Equality is via <see cref="EqualityComparer{T}.Default"/>.</typeparam>
        /// <param name="nodes">All nodes to be sorted.</param>
        /// <param name="getDependencies">
        /// Function returning the dependencies of a given node. Dependencies not in
        /// <paramref name="nodes"/> are ignored.
        /// </param>
        /// <param name="tiebreaker">
        /// Comparer used to order nodes that are mutually unrelated in the dependency
        /// graph. Smaller values come first. Pass a comparer that emits the desired
        /// "earliest" element first.
        /// </param>
        /// <param name="cycleParticipants">
        /// Populated with any nodes that participate in a cycle and therefore could
        /// not be placed in topological order. Empty if the input is acyclic.
        /// </param>
        /// <returns>The nodes in topological order, with cycle participants appended last.</returns>
        public static List<T> Sort<T>(
            IReadOnlyList<T> nodes,
            Func<T, IEnumerable<T>> getDependencies,
            IComparer<T> tiebreaker,
            out List<T> cycleParticipants)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));
            if (getDependencies == null) throw new ArgumentNullException(nameof(getDependencies));
            if (tiebreaker == null) throw new ArgumentNullException(nameof(tiebreaker));

            var nodeSet = new HashSet<T>(nodes);
            var inDegree = new Dictionary<T, int>(nodes.Count);
            var dependents = new Dictionary<T, List<T>>(nodes.Count);

            foreach (var n in nodes)
            {
                inDegree[n] = 0;
                dependents[n] = new List<T>();
            }

            foreach (var n in nodes)
            {
                var deps = getDependencies(n);
                if (deps == null) continue;
                foreach (var dep in deps)
                {
                    if (dep == null) continue;
                    if (!nodeSet.Contains(dep)) continue; // dependency outside the input set — ignore
                    if (EqualityComparer<T>.Default.Equals(dep, n)) continue; // self-edge — ignore
                    inDegree[n]++;
                    dependents[dep].Add(n);
                }
            }

            // Pending nodes ordered by tiebreaker. Linear-scan O(n^2) is fine for the
            // small node counts this utility is built for (a dozen-ish subsystems).
            var ready = new List<T>();
            foreach (var n in nodes)
            {
                if (inDegree[n] == 0) InsertSorted(ready, n, tiebreaker);
            }

            var result = new List<T>(nodes.Count);
            while (ready.Count > 0)
            {
                var next = ready[0];
                ready.RemoveAt(0);
                result.Add(next);

                foreach (var dependent in dependents[next])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                    {
                        InsertSorted(ready, dependent, tiebreaker);
                    }
                }
            }

            cycleParticipants = new List<T>();
            if (result.Count < nodes.Count)
            {
                foreach (var n in nodes)
                {
                    if (inDegree[n] > 0) InsertSorted(cycleParticipants, n, tiebreaker);
                }
                result.AddRange(cycleParticipants);
            }

            return result;
        }

        private static void InsertSorted<T>(List<T> list, T item, IComparer<T> comparer)
        {
            int i = 0;
            while (i < list.Count && comparer.Compare(list[i], item) <= 0) i++;
            list.Insert(i, item);
        }
    }
}
