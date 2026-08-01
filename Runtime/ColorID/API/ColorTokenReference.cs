using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// A serialized reference to a canonical colour token. Data only — it holds an ID, not a colour.
    /// </summary>
    /// <remarks>
    /// The V2 replacement for <see cref="ColorIDReference"/>, and deliberately narrower than it in two
    /// ways:
    /// <list type="bullet">
    /// <item><description>
    /// <b>No implicit conversion to <see cref="Color"/>.</b> <c>ColorIDReference</c> has one, and it is
    /// the single most costly convenience in the V1 API: writing <c>image.color = myRef;</c> silently
    /// resolves through hidden global state, which works after bootstrap and returns magenta before it,
    /// with nothing at the call site to suggest ordering matters. Requiring an explicit provider makes
    /// that dependency visible.
    /// </description></item>
    /// <item><description>
    /// <b>No static resolution path.</b> Every read takes an <see cref="IColorThemeService"/> (or a
    /// <see cref="ResolvedColorTheme"/>), so tests and tools can resolve against a chosen theme instead
    /// of whatever global happens to be installed.
    /// </description></item>
    /// </list>
    /// A struct so an unassigned field costs nothing and cannot be null.
    /// </remarks>
    [Serializable]
    public struct ColorTokenReference : IEquatable<ColorTokenReference>
    {
        [SerializeField] private string _tokenId;

        /// <summary>The canonical token ID, or <c>null</c>/empty when unassigned.</summary>
        public string TokenId => _tokenId;

        /// <summary>Whether this reference names a token at all.</summary>
        /// <remarks>
        /// Only checks for a value; it says nothing about whether the token exists in the active theme.
        /// An assigned-but-unresolvable reference is a validation finding, not an empty one.
        /// </remarks>
        public bool IsAssigned => !string.IsNullOrEmpty(_tokenId);

        /// <summary>Whether the referenced ID is well-formed, regardless of any theme.</summary>
        public bool IsWellFormed => ColorTokenId.IsValid(_tokenId);

        /// <summary>Creates a reference to a canonical token.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        public ColorTokenReference(string tokenId) => _tokenId = tokenId;

        /// <summary>Resolves this reference against a theme service.</summary>
        /// <param name="service">The theme service. May be <c>null</c>.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> on failure.</param>
        /// <returns>
        /// <c>false</c> when the reference is unassigned, the service is unavailable or has no active
        /// theme, or the active theme does not contain the token.
        /// </returns>
        public bool TryResolve(IColorThemeService service, out Color color)
        {
            var theme = service?.ActiveTheme;
            if (theme == null || !IsAssigned)
            {
                color = Color.clear;
                return false;
            }
            return theme.TryGetColor(_tokenId, out color);
        }

        /// <summary>Resolves this reference against an already-captured snapshot.</summary>
        /// <param name="theme">The snapshot to read. May be <c>null</c>.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> on failure.</param>
        /// <returns><c>false</c> when unassigned, or absent from the snapshot.</returns>
        /// <remarks>
        /// Preferred inside a change handler: resolving against the snapshot the notification carried
        /// cannot race a newer activation the way re-reading the service can.
        /// </remarks>
        public bool TryResolve(ResolvedColorTheme theme, out Color color)
        {
            if (theme == null || !IsAssigned)
            {
                color = Color.clear;
                return false;
            }
            return theme.TryGetColor(_tokenId, out color);
        }

        /// <summary>Resolves this reference, throwing when it cannot.</summary>
        /// <param name="service">The theme service.</param>
        /// <returns>The resolved colour.</returns>
        /// <exception cref="InvalidOperationException">
        /// The reference is unassigned, no theme is active, or the token is absent from it.
        /// </exception>
        /// <remarks>
        /// For code where an unresolvable token is a programming error rather than a data problem.
        /// Anything driven by authored content should prefer <see cref="TryResolve(IColorThemeService, out Color)"/>
        /// — a broken asset should surface as a validation finding, not an exception at runtime.
        /// </remarks>
        public Color ResolveRequired(IColorThemeService service)
        {
            if (!IsAssigned)
                throw new InvalidOperationException("ColorTokenReference is unassigned.");

            var theme = service?.ActiveTheme;
            if (theme == null)
                throw new InvalidOperationException(
                    $"Cannot resolve colour token '{_tokenId}': no colour theme is active. "
                    + "Await RuntimeManager.WaitForInitialization() before resolving.");

            if (!theme.TryGetColor(_tokenId, out Color color))
                throw new InvalidOperationException(
                    $"Colour token '{_tokenId}' is not present in active variant '{theme.VariantId}'.");

            return color;
        }

        /// <inheritdoc/>
        public bool Equals(ColorTokenReference other) =>
            string.Equals(_tokenId, other._tokenId, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ColorTokenReference other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _tokenId?.GetHashCode() ?? 0;

        /// <inheritdoc/>
        public override string ToString() => IsAssigned ? _tokenId : "<unassigned>";
    }
}
