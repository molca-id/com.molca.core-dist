using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Rendering;

namespace Molca.App.Editor.ShaderVariant
{
    /// <summary>Describes a pass type supported by <see cref="ShaderVariantSplitTool"/>.</summary>
    [Serializable]
    public class ShaderEntry
    {
        public UnityEngine.Shader shader;
        public string materialScanFolder = "Assets";

        public bool includeForwardLit   = true;
        public bool includeShadowCaster = true;

        /// <summary>
        /// Per-material keywords (<c>shader_feature</c> / <c>shader_feature_local</c>).
        /// Populated by "Scan Materials"; each entry can be toggled off to reduce variant count.
        /// </summary>
        public List<string> materialKeywords = new();

        /// <summary>
        /// Pipeline keyword axes (<c>multi_compile</c>). Each axis is a set of mutually exclusive
        /// values; the first entry is the "off" / empty keyword.
        /// </summary>
        public List<KeywordAxis> pipelineAxes = new();
    }

    /// <summary>One <c>multi_compile</c> axis: mutually exclusive keyword alternatives.</summary>
    [Serializable]
    public class KeywordAxis
    {
        /// <summary>Display label shown in the tool UI (e.g. "Shadow").</summary>
        public string label;

        /// <summary>Keyword alternatives, e.g. <c>["", "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE"]</c>.</summary>
        public List<string> values = new();

        /// <summary>Per-value enable toggle. Unchecked values are excluded from the matrix.</summary>
        public List<bool> enabled = new();
    }

    /// <summary>A single resolved shader variant: sorted keyword set + pass type.</summary>
    public readonly struct VariantSpec
    {
        /// <summary>Sorted, space-joined keyword string (empty string = no keywords).</summary>
        public readonly string Keywords;

        /// <summary>The render pass this variant targets.</summary>
        public readonly PassType PassType;

        public VariantSpec(string keywords, PassType passType)
        {
            Keywords = keywords;
            PassType = passType;
        }
    }

    /// <summary>
    /// Builds the full combination matrix for a <see cref="ShaderEntry"/> without touching
    /// any Unity asset database — pure combinatorics, fully unit-testable.
    /// </summary>
    public static class VariantCombinator
    {
        /// <summary>
        /// Builds the variant matrix for <paramref name="entry"/>.
        /// Over-generation is intentional: the GPU driver silently ignores variants that don't
        /// match a compiled program at <c>WarmUp()</c> time.
        /// </summary>
        /// <param name="entry">The shader entry to expand.</param>
        /// <returns>Deduplicated list of <see cref="VariantSpec"/> values.</returns>
        public static List<VariantSpec> BuildMatrix(ShaderEntry entry)
        {
            var results = new HashSet<VariantSpec>(VariantSpecComparer.Instance);

            var enabledMaterialKws = entry.materialKeywords
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();

            foreach (var materialSet in PowerSet(enabledMaterialKws))
            {
                if (entry.includeForwardLit)
                {
                    foreach (var pipelineCombo in PipelineCombos(entry.pipelineAxes))
                    {
                        var kws = SortedJoin(materialSet.Concat(pipelineCombo));
                        results.Add(new VariantSpec(kws, PassType.ScriptableRenderPipelineDefaultUnlit));
                    }
                }

                if (entry.includeShadowCaster)
                {
                    // Shadow pass only crosses stereo axis; other axes don't apply.
                    var stereoAxis = entry.pipelineAxes.FirstOrDefault(a =>
                        a.label != null && a.label.ToLowerInvariant().Contains("stereo"));

                    var stereoValues = stereoAxis != null
                        ? EnabledValues(stereoAxis)
                        : new List<string> { string.Empty };

                    foreach (var stereoKw in stereoValues)
                    {
                        var kws = SortedJoin(materialSet.Append(stereoKw));
                        results.Add(new VariantSpec(kws, PassType.ShadowCaster));
                    }
                }
            }

            return results.ToList();
        }

        /// <summary>Returns all subsets of <paramref name="source"/> including the empty set.</summary>
        internal static IEnumerable<IEnumerable<string>> PowerSet(IList<string> source)
        {
            int count = source.Count;
            // 2^count subsets
            for (int mask = 0; mask < (1 << count); mask++)
            {
                var subset = new List<string>();
                for (int i = 0; i < count; i++)
                    if ((mask & (1 << i)) != 0)
                        subset.Add(source[i]);
                yield return subset;
            }
        }

        /// <summary>
        /// Returns the Cartesian product of all enabled values across all pipeline axes.
        /// Axes with no enabled values contribute a single empty string (treated as "off").
        /// </summary>
        internal static IEnumerable<IEnumerable<string>> PipelineCombos(List<KeywordAxis> axes)
        {
            if (axes == null || axes.Count == 0)
            {
                yield return Enumerable.Empty<string>();
                yield break;
            }

            IEnumerable<IEnumerable<string>> product = new[] { Enumerable.Empty<string>() };
            foreach (var axis in axes)
            {
                var values = EnabledValues(axis);
                var axisCopy = values; // closure capture
                product = product.SelectMany(combo => axisCopy.Select(v => combo.Append(v)));
            }
            foreach (var combo in product)
                yield return combo;
        }

        private static List<string> EnabledValues(KeywordAxis axis)
        {
            var values = new List<string>();
            for (int i = 0; i < axis.values.Count; i++)
            {
                bool isEnabled = i < axis.enabled.Count && axis.enabled[i];
                if (isEnabled)
                    values.Add(axis.values[i] ?? string.Empty);
            }
            // Always include at least the empty/"off" slot so the axis doesn't block the product.
            if (values.Count == 0)
                values.Add(string.Empty);
            return values;
        }

        private static string SortedJoin(IEnumerable<string> keywords)
        {
            var kws = keywords
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            return string.Join(" ", kws);
        }

        // VariantSpec equality by value so the HashSet deduplicates correctly.
        private sealed class VariantSpecComparer : IEqualityComparer<VariantSpec>
        {
            public static readonly VariantSpecComparer Instance = new();
            public bool Equals(VariantSpec x, VariantSpec y) =>
                x.PassType == y.PassType &&
                string.Equals(x.Keywords, y.Keywords, StringComparison.Ordinal);
            public int GetHashCode(VariantSpec obj) =>
                HashCode.Combine(obj.PassType, obj.Keywords);
        }
    }
}
