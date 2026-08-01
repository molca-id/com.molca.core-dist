using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Turns an authored <see cref="ColorThemeSet"/> plus a variant ID into an immutable
    /// <see cref="ResolvedColorTheme"/>, or explains precisely why it cannot.
    /// </summary>
    /// <remarks>
    /// All alias flattening happens here, once per activation, so steady-state lookup never walks a
    /// graph. Everything this class rejects — cycles, over-deep chains, missing alias targets,
    /// missing required tokens — is rejected <i>before</i> a snapshot is published, which is what
    /// makes "failed activation preserves the last known good theme" possible: the caller never
    /// receives a half-built table it has to roll back.
    /// <para/>
    /// Stateless and side-effect free: it reads the authored asset and returns a new object. It never
    /// writes to the theme set, marks anything dirty, or logs — the caller owns reporting.
    /// </remarks>
    public static class ColorThemeResolver
    {
        /// <summary>
        /// Maximum number of alias hops allowed from a token to its literal value.
        /// </summary>
        /// <remarks>
        /// A bound exists for author sanity rather than for performance: flattening is one-time, but a
        /// six-deep alias chain is impossible to reason about when deciding what a colour will look
        /// like. Four hops comfortably covers the intended shapes — a semantic token aliasing a
        /// semantic token aliasing a primitive, with an alpha step in between.
        /// </remarks>
        public const int MaxAliasDepth = 4;

        /// <summary>
        /// Attempts to build a snapshot for one variant.
        /// </summary>
        /// <param name="themeSet">The authored theme set. May be <c>null</c>.</param>
        /// <param name="variantId">The variant to resolve; matched case-insensitively.</param>
        /// <param name="generation">Generation to stamp on the resulting snapshot.</param>
        /// <param name="theme">The built snapshot, or <c>null</c> on failure.</param>
        /// <param name="diagnostics">
        /// Author-facing explanations. Populated on failure; may also carry non-fatal notes on success
        /// (for example an optional token that no variant supplies).
        /// </param>
        /// <returns>
        /// <see cref="ColorThemeActivation.Activated"/> on success, otherwise the specific reason.
        /// </returns>
        public static ColorThemeActivation TryResolve(ColorThemeSet themeSet, string variantId,
            int generation, out ResolvedColorTheme theme, out List<string> diagnostics)
        {
            theme = null;
            diagnostics = new List<string>();

            if (themeSet == null)
            {
                diagnostics.Add("No colour theme set is configured.");
                return ColorThemeActivation.SettingsUnavailable;
            }

            if (!themeSet.Validate(diagnostics))
                return ColorThemeActivation.InvalidThemeSet;

            var variant = themeSet.GetVariant(variantId);
            if (variant == null)
            {
                diagnostics.Add($"Theme set '{themeSet.DisplayName}' does not declare variant "
                                + $"'{variantId}'. Available: {string.Join(", ", themeSet.GetVariantIds())}.");
                return ColorThemeActivation.UnknownVariant;
            }

            // Index the variant's authored expressions before flattening: alias resolution needs
            // random access to sibling tokens, not sequential access.
            var expressions = new Dictionary<string, ColorExpression>(StringComparer.Ordinal);
            foreach (var value in variant.Values)
            {
                if (value?.Expression == null || string.IsNullOrEmpty(value.TokenId)) continue;
                expressions[value.TokenId] = value.Expression;
            }

            var colors = new Dictionary<string, Color>(expressions.Count, StringComparer.Ordinal);
            var sources = new Dictionary<string, ColorResolutionSource>(expressions.Count, StringComparer.Ordinal);

            // Reused across the whole flatten pass; holds the chain currently being walked so a cycle
            // can be reported as the actual path rather than just "a cycle exists somewhere".
            var chain = new List<string>();
            bool sawCycle = false;

            foreach (var pair in expressions)
            {
                chain.Clear();
                var outcome = Flatten(pair.Key, expressions, colors, sources, chain, diagnostics);
                if (outcome == FlattenOutcome.Cycle || outcome == FlattenOutcome.TooDeep) sawCycle = true;
            }

            if (sawCycle) return ColorThemeActivation.AliasCycle;

            // Required-token coverage is checked against the *resolved* table, not the authored list:
            // a token can be authored and still fail to resolve if its alias target was unresolvable.
            bool missingRequired = false;
            foreach (var definition in themeSet.TokenDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id)) continue;

                if (colors.ContainsKey(definition.Id)) continue;

                if (definition.Required)
                {
                    diagnostics.Add($"Variant '{variant.Id}' does not resolve required token "
                                    + $"'{definition.Id}'.");
                    missingRequired = true;
                }
                else
                {
                    diagnostics.Add($"Variant '{variant.Id}' does not resolve optional token "
                                    + $"'{definition.Id}'; lookups for it will report missing.");
                }
            }

            if (missingRequired) return ColorThemeActivation.MissingRequiredToken;

            theme = new ResolvedColorTheme(themeSet.StableSetId, variant.Id, generation, colors, sources);
            return ColorThemeActivation.Activated;
        }

        private enum FlattenOutcome
        {
            Resolved,
            MissingTarget,
            Cycle,
            TooDeep
        }

        /// <summary>
        /// Resolves one token to a literal colour, memoizing into <paramref name="colors"/>.
        /// </summary>
        /// <remarks>
        /// Depth-first with the in-progress chain carried explicitly. <paramref name="colors"/> doubles
        /// as the memo table, so a token reached through several aliases is flattened once — that is
        /// what keeps activation linear in token count rather than exponential in chain depth.
        /// </remarks>
        private static FlattenOutcome Flatten(string tokenId,
            Dictionary<string, ColorExpression> expressions,
            Dictionary<string, Color> colors,
            Dictionary<string, ColorResolutionSource> sources,
            List<string> chain,
            List<string> diagnostics)
        {
            if (colors.ContainsKey(tokenId)) return FlattenOutcome.Resolved;

            // Membership in the active chain — not in the memo table — is what a cycle is.
            if (chain.Contains(tokenId))
            {
                chain.Add(tokenId);
                diagnostics.Add($"Alias cycle: {string.Join(" -> ", chain)}");
                return FlattenOutcome.Cycle;
            }

            if (chain.Count >= MaxAliasDepth)
            {
                diagnostics.Add($"Alias chain deeper than {MaxAliasDepth} hops: "
                                + $"{string.Join(" -> ", chain)} -> {tokenId}");
                return FlattenOutcome.TooDeep;
            }

            if (!expressions.TryGetValue(tokenId, out var expression))
            {
                // Reported by the caller as a missing alias target with the referring token named;
                // a bare "token absent" note here would lose that context.
                return FlattenOutcome.MissingTarget;
            }

            if (expression.ExpressionKind == ColorExpression.Kind.Literal)
            {
                colors[tokenId] = expression.Literal;
                sources[tokenId] = ColorResolutionSource.Literal;
                return FlattenOutcome.Resolved;
            }

            chain.Add(tokenId);
            var targetOutcome = Flatten(expression.AliasTokenId, expressions, colors, sources, chain,
                diagnostics);
            chain.RemoveAt(chain.Count - 1);

            if (targetOutcome == FlattenOutcome.MissingTarget)
            {
                diagnostics.Add($"Token '{tokenId}' aliases '{expression.AliasTokenId}', which this "
                                + "variant does not supply a value for.");
                return FlattenOutcome.MissingTarget;
            }

            if (targetOutcome != FlattenOutcome.Resolved) return targetOutcome;

            Color resolved = colors[expression.AliasTokenId];

            if (expression.ExpressionKind == ColorExpression.Kind.AliasWithAlpha)
            {
                // Multiplies the target's alpha rather than replacing it, so an alpha alias over an
                // already-translucent token composes instead of overriding — Text/20 over a
                // half-transparent base is 20% of that half, which is what an author means.
                resolved.a = Mathf.Clamp01(resolved.a * expression.AlphaMultiplier);
                sources[tokenId] = ColorResolutionSource.AliasWithAlpha;
            }
            else
            {
                sources[tokenId] = ColorResolutionSource.Alias;
            }

            colors[tokenId] = resolved;
            return FlattenOutcome.Resolved;
        }

        /// <summary>
        /// Evaluates a theme set's contrast requirements against a resolved snapshot.
        /// </summary>
        /// <param name="themeSet">The theme set whose requirements are checked.</param>
        /// <param name="theme">The resolved snapshot to measure.</param>
        /// <returns>One result per applicable requirement, in authored order.</returns>
        /// <remarks>
        /// Read-only. Separate from <see cref="TryResolve"/> because a contrast failure is an
        /// accessibility finding whose severity the project decides, not a reason a theme cannot be
        /// activated — an application must still run while its palette is being fixed.
        /// </remarks>
        public static List<ColorContrastResult> EvaluateContrast(ColorThemeSet themeSet,
            ResolvedColorTheme theme)
        {
            var results = new List<ColorContrastResult>();
            if (themeSet == null || theme == null) return results;

            foreach (var requirement in themeSet.AccessibilityRequirements)
            {
                if (requirement == null || !requirement.AppliesTo(theme.VariantId)) continue;

                if (!theme.TryGetColor(requirement.ForegroundTokenId, out Color foreground)
                    || !theme.TryGetColor(requirement.BackgroundTokenId, out Color background))
                {
                    results.Add(ColorContrastResult.Incomplete(requirement, theme.VariantId,
                        "One of the paired tokens does not resolve in this variant."));
                    continue;
                }

                Color underSurface = background;
                if (ColorContrast.RequiresUnderSurface(foreground, background))
                {
                    if (string.IsNullOrEmpty(requirement.UnderSurfaceTokenId))
                    {
                        // Guessing here is worse than reporting nothing: a fabricated backdrop
                        // produces a confident ratio for something nobody can verify.
                        results.Add(ColorContrastResult.Incomplete(requirement, theme.VariantId,
                            $"Background '{requirement.BackgroundTokenId}' is translucent "
                            + $"(alpha {background.a:0.###}), so the ratio depends on what sits beneath "
                            + "it. Name an under-surface token on the requirement."));
                        continue;
                    }

                    if (!theme.TryGetColor(requirement.UnderSurfaceTokenId, out underSurface))
                    {
                        results.Add(ColorContrastResult.Incomplete(requirement, theme.VariantId,
                            $"Under-surface token '{requirement.UnderSurfaceTokenId}' does not resolve "
                            + "in this variant."));
                        continue;
                    }
                }

                float ratio = ColorContrast.RatioComposited(foreground, background, underSurface);
                results.Add(new ColorContrastResult(requirement, theme.VariantId, ratio,
                    ratio >= requirement.MinimumRatio, null));
            }

            return results;
        }
    }

    /// <summary>The measured outcome of one contrast requirement in one variant.</summary>
    public readonly struct ColorContrastResult
    {
        /// <summary>The requirement that was evaluated.</summary>
        public ColorContrastRequirement Requirement { get; }

        /// <summary>The variant it was evaluated in.</summary>
        public string VariantId { get; }

        /// <summary>The measured ratio, or 0 when <see cref="IsIncomplete"/>.</summary>
        public float Ratio { get; }

        /// <summary>Whether the measured ratio met the requirement.</summary>
        public bool Passed { get; }

        /// <summary>
        /// Why the requirement could not be measured, or <c>null</c> when it was.
        /// </summary>
        /// <remarks>
        /// An incomplete result is deliberately neither a pass nor a failure. Reporting it as a pass
        /// would manufacture false confidence; as a failure it would nag about something that may well
        /// be fine. It is a request for the author to supply missing information.
        /// </remarks>
        public string IncompleteReason { get; }

        /// <summary>Whether this requirement could not be measured.</summary>
        public bool IsIncomplete => !string.IsNullOrEmpty(IncompleteReason);

        /// <summary>Creates a measured result.</summary>
        public ColorContrastResult(ColorContrastRequirement requirement, string variantId, float ratio,
            bool passed, string incompleteReason)
        {
            Requirement = requirement;
            VariantId = variantId;
            Ratio = ratio;
            Passed = passed;
            IncompleteReason = incompleteReason;
        }

        /// <summary>Creates an unmeasurable result.</summary>
        /// <param name="requirement">The requirement that could not be measured.</param>
        /// <param name="variantId">The variant it applies to.</param>
        /// <param name="reason">What information is missing.</param>
        public static ColorContrastResult Incomplete(ColorContrastRequirement requirement,
            string variantId, string reason) =>
            new ColorContrastResult(requirement, variantId, 0f, false, reason);

        /// <summary>A one-line summary for validation reports.</summary>
        public override string ToString()
        {
            string pair = $"{Requirement?.ForegroundTokenId} on {Requirement?.BackgroundTokenId}";
            if (IsIncomplete) return $"[{VariantId}] {pair}: incomplete — {IncompleteReason}";
            return $"[{VariantId}] {pair}: {Ratio:0.00}:1 "
                   + $"(needs {Requirement?.MinimumRatio:0.00}:1) {(Passed ? "pass" : "FAIL")}";
        }
    }
}
