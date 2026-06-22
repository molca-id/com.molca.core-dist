using System;
using System.Collections.Generic;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// A project-authored price override for a model (Sprint 53), so a project can correct drifted or
    /// contracted pricing without a code change. <see cref="Model"/> is substring-matched against the model
    /// id (case-insensitive), exactly like the shipped table, and the first match wins.
    /// </summary>
    [Serializable]
    public sealed class ModelPriceOverride
    {
        /// <summary>Model id (or substring) this override applies to, e.g. "claude-opus" or a dated variant.</summary>
        public string Model;

        /// <summary>USD per million input (prompt) tokens.</summary>
        public double InputPerMillion;

        /// <summary>USD per million output (completion) tokens.</summary>
        public double OutputPerMillion;
    }

    /// <summary>
    /// A small, best-effort price table for estimating assistant API spend from token counts (Sprint 49).
    /// Prices are USD per million tokens and are <b>estimates only</b> — they drift as vendors change pricing,
    /// and an unknown model falls back to a conservative default. Cost is always presented as approximate.
    /// </summary>
    public static class AssistantCostTable
    {
        /// <summary>USD per million input / output tokens for one model.</summary>
        public readonly struct Price
        {
            public readonly double InputPerMillion;
            public readonly double OutputPerMillion;
            public Price(double inputPerMillion, double outputPerMillion)
            {
                InputPerMillion = inputPerMillion;
                OutputPerMillion = outputPerMillion;
            }
        }

        // Published list prices at time of writing; substring-matched against the model id so dated variants
        // (e.g. "claude-haiku-4-5-20251001") resolve to the family entry. Order matters: longest/most-specific
        // keys first so "opus" doesn't shadow a more specific match.
        private static readonly (string Key, Price Price)[] Table =
        {
            ("claude-opus", new Price(15.0, 75.0)),
            ("claude-sonnet", new Price(3.0, 15.0)),
            ("claude-haiku", new Price(0.80, 4.0)),
            ("claude-fable", new Price(3.0, 15.0)),
            ("gpt-4o-mini", new Price(0.15, 0.60)),
            ("gpt-4o", new Price(2.5, 10.0)),
            ("deepseek", new Price(0.27, 1.10)),
        };

        /// <summary>The fallback price for an unrecognized model — a mid-range guess, clearly an estimate.</summary>
        private static readonly Price Default = new Price(3.0, 15.0);

        /// <summary>Resolves the price for a model id by substring match, or the conservative default.</summary>
        public static Price PriceFor(string model)
        {
            if (!string.IsNullOrEmpty(model))
            {
                var id = model.ToLowerInvariant();
                foreach (var (key, price) in Table)
                    if (id.Contains(key))
                        return price;
            }
            return Default;
        }

        /// <summary>
        /// Resolves the price for a model id (Sprint 53), consulting project <paramref name="overrides"/>
        /// first (substring-matched, first match wins) before the shipped table and the default.
        /// </summary>
        public static Price PriceFor(string model, IReadOnlyList<ModelPriceOverride> overrides)
        {
            if (overrides != null && !string.IsNullOrEmpty(model))
            {
                foreach (var o in overrides)
                {
                    if (o == null || string.IsNullOrWhiteSpace(o.Model)) continue;
                    if (model.IndexOf(o.Model, StringComparison.OrdinalIgnoreCase) >= 0)
                        return new Price(o.InputPerMillion, o.OutputPerMillion);
                }
            }
            return PriceFor(model);
        }

        /// <summary>Estimated USD cost for the given token counts under <paramref name="model"/>'s pricing.</summary>
        public static double EstimateCost(string model, long inputTokens, long outputTokens)
            => EstimateCost(model, inputTokens, outputTokens, null);

        /// <summary>
        /// Estimated USD cost for the given token counts (Sprint 53), preferring a matching project
        /// <paramref name="overrides"/> entry over the shipped table.
        /// </summary>
        public static double EstimateCost(string model, long inputTokens, long outputTokens, IReadOnlyList<ModelPriceOverride> overrides)
        {
            var price = PriceFor(model, overrides);
            return Math.Max(0, inputTokens) / 1_000_000.0 * price.InputPerMillion
                 + Math.Max(0, outputTokens) / 1_000_000.0 * price.OutputPerMillion;
        }

        /// <summary>Formats an estimated cost compactly (e.g. "$0.0123"), for read-only telemetry surfaces.</summary>
        public static string FormatCost(double usd) =>
            usd >= 0.01 ? $"${usd:0.00}" : $"${usd:0.0000}";
    }
}
