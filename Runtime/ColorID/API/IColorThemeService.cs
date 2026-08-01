using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// The V2 runtime colour-theme API: resolve canonical tokens against the active variant, and
    /// switch variants.
    /// </summary>
    /// <remarks>
    /// <b>Implemented by:</b> the existing <see cref="ColorSchemeManager"/> subsystem.
    /// <b>Resolve with:</b> <c>RuntimeManager.GetService&lt;IColorThemeService&gt;()</c> or
    /// <c>[Inject]</c>. RuntimeManager's subsystem registration already exposes every non-framework
    /// interface a subsystem implements, so no second registration path is needed.
    /// <para/>
    /// Deliberately added <i>alongside</i> <see cref="IColorSchemeService"/> rather than replacing it:
    /// the old interface keeps working for the compatibility window, and one manager owns both so
    /// there is never a second competing source of truth for what theme is active.
    /// <para/>
    /// Not thread-safe; main thread only, like the rest of the Unity-facing surface.
    /// </remarks>
    public interface IColorThemeService
    {
        /// <summary>The authored theme set, or <c>null</c> when the project has not installed one.</summary>
        ColorThemeSet ThemeSet { get; }

        /// <summary>
        /// The active immutable snapshot, or <c>null</c> before initialization has published one.
        /// </summary>
        /// <remarks>
        /// Capture this once and read from it when applying many tokens together: it cannot change
        /// underfoot, whereas re-reading the property can pick up a newer activation mid-loop and
        /// produce a half-themed frame.
        /// </remarks>
        ResolvedColorTheme ActiveTheme { get; }

        /// <summary>The active variant ID, or <c>null</c> when nothing is active.</summary>
        string ActiveVariantId { get; }

        /// <summary>Every selectable variant ID, in authored order.</summary>
        string[] VariantIds { get; }

        /// <summary>
        /// The active snapshot's generation, or 0 when nothing is active.
        /// </summary>
        /// <remarks>
        /// Increases on every publish. A binding that records the generation it last applied can ignore
        /// a repeat notification for the same generation instead of redoing the work.
        /// </remarks>
        int Generation { get; }

        /// <summary>Whether the active theme is the degraded emergency fallback.</summary>
        bool IsDegraded { get; }

        /// <summary>
        /// Raised after a new snapshot is published.
        /// </summary>
        /// <remarks>
        /// Subscriber exceptions are isolated: one throwing handler cannot stop the rest of the
        /// bindings in the scene from refreshing. Unsubscribe on destroy.
        /// </remarks>
        event Action<ColorThemeChanged> ThemeChanged;

        /// <summary>Resolves a canonical token against the active theme.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> on failure.</param>
        /// <returns><c>false</c> when no theme is active or the token is absent.</returns>
        /// <remarks>
        /// Allocation-free and silent. Absence is a return value, not a log line and not magenta —
        /// deciding how to report it belongs to the caller, which is the main behavioural difference
        /// from the legacy <see cref="IColorProvider"/> surface.
        /// </remarks>
        bool TryResolve(string tokenId, out Color color);

        /// <summary>Resolves a canonical token, or returns a visible sentinel.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <returns>The resolved colour, or <see cref="Color.magenta"/> when it cannot be resolved.</returns>
        /// <remarks>
        /// The magenta sentinel is kept for parity with legacy behaviour, because a loudly wrong colour
        /// is easier to notice in a scene than a plausible one. Prefer
        /// <see cref="TryResolve"/> in new code.
        /// </remarks>
        Color Resolve(string tokenId);

        /// <summary>Activates a variant.</summary>
        /// <param name="variantId">The variant to activate; matched case-insensitively.</param>
        /// <param name="save">Whether to persist the choice as the user's preference.</param>
        /// <returns>
        /// <c>true</c> when the variant is active afterwards, including when it already was.
        /// </returns>
        /// <remarks>
        /// A failed activation leaves the previous snapshot in place. Use
        /// <see cref="TrySetVariant"/> when the reason for a failure matters.
        /// </remarks>
        bool SetVariant(string variantId, bool save = true);

        /// <summary>Activates a variant and reports precisely what happened.</summary>
        /// <param name="variantId">The variant to activate; matched case-insensitively.</param>
        /// <param name="save">Whether to persist the choice as the user's preference.</param>
        /// <param name="result">The typed outcome, including diagnostics on failure.</param>
        /// <returns><c>true</c> when a usable theme is active afterwards.</returns>
        bool TrySetVariant(string variantId, bool save, out ColorThemeActivationResult result);

        /// <summary>
        /// Re-publishes the active snapshot so every binding reapplies it.
        /// </summary>
        /// <remarks>
        /// Does not rebuild from the theme set — it re-notifies with the current snapshot under a new
        /// generation. For refreshing bindings whose targets changed, not for picking up edits to the
        /// theme asset.
        /// </remarks>
        void RefreshBindings();

        /// <summary>
        /// A legacy <see cref="IColorProvider"/> view of the active theme, or <c>null</c> in a
        /// legacy-only project.
        /// </summary>
        /// <remarks>
        /// Translates V1 <c>(swatch, colorId)</c> lookups into canonical token lookups through the
        /// theme set's alias map. This is what keeps 194 shipped <see cref="ColorID"/> components and
        /// every <see cref="ColorIDReference"/> resolving after a project migrates to V2, without
        /// rewriting their serialized data. New code must not use it.
        /// </remarks>
        IColorProvider LegacyProvider { get; }
    }
}
