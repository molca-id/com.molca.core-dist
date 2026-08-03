using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Runtime colour-theme API for resolving canonical tokens and switching variants.
    /// </summary>
    /// <remarks>
    /// <b>Implemented by:</b> the <see cref="ColorSchemeManager"/> subsystem.
    /// <b>Resolve with:</b> <c>RuntimeManager.GetService&lt;IColorThemeService&gt;()</c> or
    /// <c>[Inject]</c>. RuntimeManager exposes subsystem interfaces automatically.
    /// Not thread-safe; main thread only, like the rest of the Unity-facing surface.
    /// </remarks>
    public interface IColorThemeService
    {
        /// <summary>The authored theme set, or <c>null</c> when none is installed.</summary>
        ColorThemeSet ThemeSet { get; }

        /// <summary>The active immutable snapshot, or <c>null</c> before initialization.</summary>
        /// <remarks>
        /// Capture this once when applying many tokens together so a variant change cannot produce a
        /// half-themed frame.
        /// </remarks>
        ResolvedColorTheme ActiveTheme { get; }

        /// <summary>The active variant ID, or <c>null</c> when nothing is active.</summary>
        string ActiveVariantId { get; }

        /// <summary>Every selectable variant ID, in authored order.</summary>
        string[] VariantIds { get; }

        /// <summary>The active snapshot's generation, or 0 when nothing is active.</summary>
        int Generation { get; }

        /// <summary>Whether the active theme is the degraded emergency fallback.</summary>
        bool IsDegraded { get; }

        /// <summary>Raised after a new snapshot is published.</summary>
        /// <remarks>Subscriber exceptions are isolated. Unsubscribe on destroy.</remarks>
        event Action<ColorThemeChanged> ThemeChanged;

        /// <summary>Resolves a canonical token against the active theme.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> on failure.</param>
        /// <returns><c>false</c> when no theme is active or the token is absent.</returns>
        bool TryResolve(string tokenId, out Color color);

        /// <summary>Resolves a canonical token, or returns a visible sentinel.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <returns>The resolved colour, or <see cref="Color.magenta"/> when it cannot be resolved.</returns>
        Color Resolve(string tokenId);

        /// <summary>Activates a variant.</summary>
        /// <param name="variantId">The variant to activate; matched case-insensitively.</param>
        /// <param name="save">Whether to persist the choice as the user's preference.</param>
        /// <returns><c>true</c> when a usable theme is active afterwards.</returns>
        bool SetVariant(string variantId, bool save = true);

        /// <summary>Activates a variant and reports precisely what happened.</summary>
        /// <param name="variantId">The variant to activate; matched case-insensitively.</param>
        /// <param name="save">Whether to persist the choice as the user's preference.</param>
        /// <param name="result">The typed outcome, including diagnostics on failure.</param>
        /// <returns><c>true</c> when a usable theme is active afterwards.</returns>
        bool TrySetVariant(string variantId, bool save, out ColorThemeActivationResult result);

        /// <summary>Re-publishes the active snapshot so every binding reapplies it.</summary>
        /// <remarks>
        /// This re-notifies with the current snapshot under a new generation; it does not rebuild the
        /// theme from authored assets.
        /// </remarks>
        void RefreshBindings();
    }
}
