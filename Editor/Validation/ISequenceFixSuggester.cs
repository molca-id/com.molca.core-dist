using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// Produces remediation suggestions for a finding on demand (Sprint 41) — e.g. the nearest existing
    /// Ref Ids for an unresolved reference. Kept separate from <see cref="ISequenceValidator"/> so that
    /// <c>Validate()</c> stays pure and cheap (no scene-wide distance scans on the hot path); suggestions
    /// are computed only when a report is assembled.
    /// </summary>
    /// <remarks>Implementations must have a public parameterless constructor; discovered by <c>TypeCache</c>.</remarks>
    public interface ISequenceFixSuggester
    {
        /// <summary>The finding categories this suggester can advise on.</summary>
        IReadOnlyCollection<string> HandledCategories { get; }

        /// <summary>
        /// Returns ordered suggestions for <paramref name="finding"/> (best first); empty when none.
        /// </summary>
        /// <param name="finding">The finding to advise on.</param>
        /// <param name="context">The validation context (controller, steps, scene index).</param>
        /// <returns>Suggestions, never null.</returns>
        IReadOnlyList<string> Suggest(SequenceValidationFinding finding, SequenceValidationContext context);
    }

    /// <summary>
    /// Discovers <see cref="ISequenceFixSuggester"/> implementations by <c>TypeCache</c> and aggregates
    /// their suggestions per finding category.
    /// </summary>
    public static class SequenceFixSuggesterRegistry
    {
        private static List<ISequenceFixSuggester> _suggesters;

        /// <summary>The discovered suggesters.</summary>
        public static IReadOnlyList<ISequenceFixSuggester> Suggesters
        {
            get { EnsureDiscovered(); return _suggesters; }
        }

        /// <summary>Clears the discovery cache so the next access re-scans. Intended for tests.</summary>
        public static void Reset() => _suggesters = null;

        /// <summary>
        /// Aggregates suggestions for <paramref name="finding"/> from every suggester handling its
        /// category, de-duplicated and preserving best-first order.
        /// </summary>
        /// <param name="finding">The finding to advise on.</param>
        /// <param name="context">The validation context.</param>
        /// <returns>Merged suggestions; empty if none.</returns>
        public static IReadOnlyList<string> SuggestFor(
            SequenceValidationFinding finding, SequenceValidationContext context)
        {
            if (finding == null) return Array.Empty<string>();

            var merged = new List<string>();
            foreach (var suggester in Suggesters)
            {
                if (!suggester.HandledCategories.Contains(finding.Category)) continue;
                try
                {
                    foreach (var s in suggester.Suggest(finding, context))
                        if (!string.IsNullOrEmpty(s) && !merged.Contains(s)) merged.Add(s);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SequenceFixSuggesterRegistry] Suggester '{suggester.GetType().Name}' threw: {ex}");
                }
            }
            return merged;
        }

        private static void EnsureDiscovered()
        {
            if (_suggesters != null) return;

            var found = new List<ISequenceFixSuggester>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<ISequenceFixSuggester>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                try { found.Add((ISequenceFixSuggester)Activator.CreateInstance(type)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SequenceFixSuggesterRegistry] '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }
            _suggesters = found;
        }
    }
}
