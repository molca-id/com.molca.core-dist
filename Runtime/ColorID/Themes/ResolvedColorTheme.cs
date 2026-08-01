using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>How a token's value was arrived at in a resolved snapshot.</summary>
    public enum ColorResolutionSource
    {
        /// <summary>A literal colour authored on the variant.</summary>
        Literal = 0,

        /// <summary>Flattened from an alias to another token.</summary>
        Alias = 1,

        /// <summary>Flattened from an alias with an alpha multiplier applied.</summary>
        AliasWithAlpha = 2,

        /// <summary>
        /// Supplied by the emergency fallback because the theme set could not be resolved. Always
        /// accompanied by a reported degraded state.
        /// </summary>
        EmergencyFallback = 3
    }

    /// <summary>
    /// An immutable, fully-flattened lookup table for one variant: the result of activation.
    /// </summary>
    /// <remarks>
    /// Every alias is resolved once, here, at activation time. Steady-state lookup is a single
    /// dictionary hit that walks no graph and allocates nothing — which is the whole point of
    /// separating this from the authored <see cref="ColorThemeSet"/>.
    /// <para/>
    /// Instances are never mutated after construction. Switching variant builds a new snapshot and
    /// publishes it atomically, so a caller that captured the old one keeps reading a coherent set of
    /// colours rather than a half-updated table. <see cref="Generation"/> lets bindings recognise
    /// whether they have already applied a given snapshot.
    /// </remarks>
    public sealed class ResolvedColorTheme
    {
        private readonly Dictionary<string, Color> _colors;
        private readonly Dictionary<string, ColorResolutionSource> _sources;

        /// <summary>The stable ID of the theme set this snapshot came from.</summary>
        public string SetId { get; }

        /// <summary>The variant this snapshot represents.</summary>
        public string VariantId { get; }

        /// <summary>
        /// Monotonic counter, incremented for every published snapshot in a session.
        /// </summary>
        /// <remarks>
        /// Bindings ignore a change notification carrying a generation they have already applied,
        /// which makes duplicate notifications harmless rather than a source of redundant work.
        /// </remarks>
        public int Generation { get; }

        /// <summary>
        /// Deterministic fingerprint of the resolved contents.
        /// </summary>
        /// <remarks>
        /// Depends only on the set ID, variant ID and the resolved token/colour pairs — not on
        /// generation, wall-clock time or dictionary iteration order. Two snapshots built from the same
        /// authored data therefore compare equal, which is what lets generated artifacts (UI Toolkit
        /// stylesheets) be checked for staleness rather than regenerated blindly.
        /// </remarks>
        public string SourceFingerprint { get; }

        /// <summary>Number of tokens in this snapshot.</summary>
        public int TokenCount => _colors.Count;

        /// <summary>Whether this snapshot came from the emergency fallback rather than a theme set.</summary>
        public bool IsDegraded { get; }

        internal ResolvedColorTheme(string setId, string variantId, int generation,
            Dictionary<string, Color> colors, Dictionary<string, ColorResolutionSource> sources,
            bool isDegraded = false)
        {
            SetId = setId;
            VariantId = variantId;
            Generation = generation;
            _colors = colors ?? new Dictionary<string, Color>(StringComparer.Ordinal);
            _sources = sources ?? new Dictionary<string, ColorResolutionSource>(StringComparer.Ordinal);
            IsDegraded = isDegraded;
            SourceFingerprint = ComputeFingerprint(setId, variantId, _colors);
        }

        /// <summary>Looks up a token's resolved colour.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> when absent.</param>
        /// <returns><c>true</c> when the token is present in this snapshot.</returns>
        /// <remarks>
        /// Allocation-free and O(1). Absence is a <c>false</c> return, never an exception and never a
        /// magenta sentinel — the caller decides how to report it. That distinction is why new code
        /// uses this instead of the legacy provider API.
        /// </remarks>
        public bool TryGetColor(string tokenId, out Color color)
        {
            if (string.IsNullOrEmpty(tokenId))
            {
                color = Color.clear;
                return false;
            }
            return _colors.TryGetValue(tokenId, out color);
        }

        /// <summary>How a token's value was arrived at.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="source">The resolution source, when the token is present.</param>
        /// <returns><c>true</c> when the token is present in this snapshot.</returns>
        public bool TryGetSource(string tokenId, out ColorResolutionSource source)
        {
            if (string.IsNullOrEmpty(tokenId))
            {
                source = default;
                return false;
            }
            return _sources.TryGetValue(tokenId, out source);
        }

        /// <summary>Whether this snapshot contains the given token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public bool Contains(string tokenId) =>
            !string.IsNullOrEmpty(tokenId) && _colors.ContainsKey(tokenId);

        /// <summary>Every token ID in this snapshot, sorted for deterministic enumeration.</summary>
        /// <returns>A fresh array; safe for the caller to keep.</returns>
        public string[] GetTokenIds()
        {
            var ids = new string[_colors.Count];
            _colors.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }

        /// <summary>
        /// Returns an equivalent snapshot stamped with a new generation.
        /// </summary>
        /// <param name="generation">The generation to stamp.</param>
        /// <returns>A new instance; this one is unchanged.</returns>
        /// <remarks>
        /// Backs a bindings refresh: the colours are identical, but bindings that skip
        /// already-applied generations need a new number to act on. The dictionaries are shared rather
        /// than copied, which is safe precisely because nothing ever mutates them — and it keeps a
        /// refresh from allocating a second copy of a large token table.
        /// </remarks>
        internal ResolvedColorTheme WithGeneration(int generation) =>
            new ResolvedColorTheme(SetId, VariantId, generation, _colors, _sources, IsDegraded);

        /// <summary>
        /// Builds the minimal snapshot used when no theme set can be resolved at all.
        /// </summary>
        /// <param name="generation">The generation to stamp.</param>
        /// <returns>A degraded snapshot with a handful of legible neutral tokens.</returns>
        /// <remarks>
        /// Deliberately tiny and deliberately marked <see cref="IsDegraded"/>. Its purpose is to keep
        /// a misconfigured application legible enough to read the error that explains the
        /// misconfiguration — not to stand in for a theme. V1's equivalent path created an untracked
        /// fallback <see cref="ColorModule"/> ScriptableObject and then reported the subsystem as
        /// healthy, which is how a broken setup shipped looking fine.
        /// </remarks>
        internal static ResolvedColorTheme CreateEmergencyFallback(int generation)
        {
            var colors = new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                ["surface/canvas"] = new Color(0.12f, 0.12f, 0.13f, 1f),
                ["surface/panel"] = new Color(0.18f, 0.18f, 0.19f, 1f),
                ["text/primary"] = new Color(0.95f, 0.95f, 0.96f, 1f),
                ["text/muted"] = new Color(0.65f, 0.65f, 0.67f, 1f),
                ["border/default"] = new Color(0.35f, 0.35f, 0.37f, 1f)
            };

            var sources = new Dictionary<string, ColorResolutionSource>(StringComparer.Ordinal);
            foreach (var key in colors.Keys) sources[key] = ColorResolutionSource.EmergencyFallback;

            return new ResolvedColorTheme("<emergency-fallback>", "fallback", generation,
                colors, sources, isDegraded: true);
        }

        // Order-independent by construction: per-entry hashes are XOR-combined, so dictionary
        // iteration order cannot change the result. Sorting the keys first would also work but costs
        // an allocation on every activation.
        private static string ComputeFingerprint(string setId, string variantId,
            Dictionary<string, Color> colors)
        {
            unchecked
            {
                ulong accumulator = 1469598103934665603UL;
                accumulator = Mix(accumulator, setId);
                accumulator = Mix(accumulator, variantId);

                ulong entryXor = 0UL;
                foreach (var pair in colors)
                {
                    ulong entry = Mix(1469598103934665603UL, pair.Key);
                    // Quantize to 8-bit channels: two colours that serialize to the same displayed
                    // value must produce the same fingerprint, and float noise below that threshold
                    // is not a real content change.
                    entry = MixByte(entry, (byte)Mathf.RoundToInt(Mathf.Clamp01(pair.Value.r) * 255f));
                    entry = MixByte(entry, (byte)Mathf.RoundToInt(Mathf.Clamp01(pair.Value.g) * 255f));
                    entry = MixByte(entry, (byte)Mathf.RoundToInt(Mathf.Clamp01(pair.Value.b) * 255f));
                    entry = MixByte(entry, (byte)Mathf.RoundToInt(Mathf.Clamp01(pair.Value.a) * 255f));
                    entryXor ^= entry;
                }

                accumulator ^= entryXor;
                accumulator *= 1099511628211UL;
                return accumulator.ToString("x16");
            }
        }

        private static ulong Mix(ulong accumulator, string value)
        {
            if (string.IsNullOrEmpty(value)) return accumulator;
            unchecked
            {
                foreach (char c in value)
                {
                    accumulator ^= c;
                    accumulator *= 1099511628211UL;
                }
                return accumulator;
            }
        }

        private static ulong MixByte(ulong accumulator, byte value)
        {
            unchecked
            {
                accumulator ^= value;
                accumulator *= 1099511628211UL;
                return accumulator;
            }
        }
    }
}
