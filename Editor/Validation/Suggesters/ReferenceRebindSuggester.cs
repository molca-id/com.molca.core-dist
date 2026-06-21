using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Validation.Suggesters
{
    /// <summary>
    /// Suggests the nearest existing Ref Ids (by Levenshtein distance) for an
    /// <c>UnresolvedReference</c> finding, so a near-miss typo can be rebound. Moved out of
    /// <c>ReferenceResolutionValidator</c> (Sprint 41) so validation stays a cheap O(n) pass and the
    /// scene-wide distance scan runs only when a report is assembled.
    /// </summary>
    public sealed class ReferenceRebindSuggester : ISequenceFixSuggester
    {
        private const int MaxSuggestions = 3;

        /// <inheritdoc/>
        public IReadOnlyCollection<string> HandledCategories { get; } = new[] { "UnresolvedReference" };

        /// <inheritdoc/>
        public IReadOnlyList<string> Suggest(SequenceValidationFinding finding, SequenceValidationContext context)
        {
            var step = finding?.Step;
            if (step == null || context == null) return Array.Empty<string>();

            var known = context.AllKnownRefIds();
            var results = new List<string>();

            // Re-scan the step (and its auxiliaries) for the references that don't resolve, and suggest
            // near matches for each. The validator no longer carries this — we recompute on demand.
            foreach (var rf in EnumerateUnresolved(step, context))
                foreach (var near in NearestRefIds(rf, known))
                    if (!results.Contains(near)) results.Add(near);

            return results.Take(MaxSuggestions).ToList();
        }

        private static IEnumerable<string> EnumerateUnresolved(Molca.Sequence.Step step, SequenceValidationContext context)
        {
            foreach (var rf in SceneReferenceReflection.Enumerate(step))
                if (rf.Value.IsValid && context.ResolveRefId(rf.Value.RefId).Count == 0)
                    yield return rf.Value.RefId;

            foreach (var aux in step.Auxiliaries)
            {
                if (aux == null) continue;
                foreach (var rf in SceneReferenceReflection.Enumerate(aux))
                    if (rf.Value.IsValid && context.ResolveRefId(rf.Value.RefId).Count == 0)
                        yield return rf.Value.RefId;
            }
        }

        // Closest known Ref Ids to a near-miss, by Levenshtein distance, bounded so unrelated ids aren't
        // suggested. Tolerance scales with id length (a longer id tolerates more typos).
        private static IEnumerable<string> NearestRefIds(string refId, IReadOnlyCollection<string> known)
        {
            if (string.IsNullOrEmpty(refId) || known == null || known.Count == 0)
                return Array.Empty<string>();

            int tolerance = Math.Max(2, refId.Length / 3);
            return known
                .Where(id => !string.IsNullOrEmpty(id) && id != refId)
                .Select(id => (id, dist: Levenshtein(refId, id)))
                .Where(x => x.dist <= tolerance)
                .OrderBy(x => x.dist)
                .ThenBy(x => x.id, StringComparer.Ordinal)
                .Select(x => x.id);
        }

        private static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }
    }
}
