using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>How a failing contrast requirement is reported.</summary>
    public enum ColorContrastSeverity
    {
        /// <summary>Reported for information; never fails a build.</summary>
        Info = 0,

        /// <summary>Reported as a warning; does not fail a build by default.</summary>
        Warning = 1,

        /// <summary>Fails validation and, under production policy, the build.</summary>
        Error = 2
    }

    /// <summary>
    /// An authored, checkable claim that one token must be legible on another.
    /// </summary>
    /// <remarks>
    /// This is the piece of information V1 had no place to record. Contrast cannot be inferred from
    /// colour values alone: a low ratio between two tokens is a defect if one is text on the other,
    /// and irrelevant if they never touch. Only the author knows which pairs actually meet on screen,
    /// so the pairing is authored and the ratio is then checked mechanically.
    /// <para/>
    /// Minimum ratios are authored explicitly rather than derived from text size, because the model
    /// does not know the size a token will be rendered at. <see cref="ColorContrast"/> exposes the
    /// standard thresholds to author against.
    /// </remarks>
    [Serializable]
    public class ColorContrastRequirement
    {
        [SerializeField] private string _foregroundTokenId;
        [SerializeField] private string _backgroundTokenId;
        [SerializeField] private float _minimumRatio = ColorContrast.MinimumNormalText;
        [SerializeField] private List<string> _appliesToVariants = new List<string>();
        [SerializeField] private string _underSurfaceTokenId;
        [SerializeField, TextArea(1, 3)] private string _rationale;
        [SerializeField] private ColorContrastSeverity _severity = ColorContrastSeverity.Error;

        /// <summary>The token that must be legible.</summary>
        public string ForegroundTokenId => _foregroundTokenId;

        /// <summary>The token it must be legible on.</summary>
        public string BackgroundTokenId => _backgroundTokenId;

        /// <summary>The minimum acceptable contrast ratio.</summary>
        public float MinimumRatio => _minimumRatio;

        /// <summary>
        /// Variant IDs this requirement applies to. Empty means every selectable variant.
        /// </summary>
        public IReadOnlyList<string> AppliesToVariants => _appliesToVariants;

        /// <summary>
        /// The opaque token beneath <see cref="BackgroundTokenId"/>, needed only when that background
        /// is translucent. Without it a translucent-background requirement is <i>incomplete</i>, not
        /// passing — see <see cref="ColorContrast.RequiresUnderSurface"/>.
        /// </summary>
        public string UnderSurfaceTokenId => _underSurfaceTokenId;

        /// <summary>Why this pair matters — the UI it describes, so a later author can re-judge it.</summary>
        public string Rationale => _rationale;

        /// <summary>How a failure is reported.</summary>
        public ColorContrastSeverity Severity => _severity;

        /// <summary>Creates a requirement. Intended for authoring tools, importers and tests.</summary>
        /// <param name="foregroundTokenId">The token that must be legible.</param>
        /// <param name="backgroundTokenId">The token it sits on.</param>
        /// <param name="minimumRatio">The minimum acceptable ratio.</param>
        /// <param name="severity">How a failure is reported.</param>
        /// <param name="underSurfaceTokenId">Required when the background is translucent.</param>
        /// <param name="rationale">Why this pair matters.</param>
        public ColorContrastRequirement(string foregroundTokenId, string backgroundTokenId,
            float minimumRatio = ColorContrast.MinimumNormalText,
            ColorContrastSeverity severity = ColorContrastSeverity.Error,
            string underSurfaceTokenId = null, string rationale = null)
        {
            _foregroundTokenId = foregroundTokenId;
            _backgroundTokenId = backgroundTokenId;
            _minimumRatio = minimumRatio;
            _severity = severity;
            _underSurfaceTokenId = underSurfaceTokenId;
            _rationale = rationale;
        }

        /// <summary>Whether this requirement applies to the given variant.</summary>
        /// <param name="variantId">The variant being evaluated.</param>
        public bool AppliesTo(string variantId)
        {
            if (_appliesToVariants == null || _appliesToVariants.Count == 0) return true;

            foreach (var id in _appliesToVariants)
            {
                if (string.Equals(id, variantId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Validates this requirement's shape.</summary>
        /// <param name="error">The first problem found, or <c>null</c> when valid.</param>
        /// <returns><c>true</c> when the requirement is well-formed.</returns>
        /// <remarks>
        /// Whether the referenced tokens exist is a set-level check performed by
        /// <see cref="ColorThemeSet"/>, which has the contract to compare against.
        /// </remarks>
        public bool Validate(out string error)
        {
            if (!ColorTokenId.Validate(_foregroundTokenId, out string foregroundError))
            {
                error = $"Contrast requirement foreground is invalid: {foregroundError}";
                return false;
            }

            if (!ColorTokenId.Validate(_backgroundTokenId, out string backgroundError))
            {
                error = $"Contrast requirement background is invalid: {backgroundError}";
                return false;
            }

            if (string.Equals(_foregroundTokenId, _backgroundTokenId, StringComparison.Ordinal))
            {
                error = $"Contrast requirement pairs '{_foregroundTokenId}' with itself, which is always 1:1.";
                return false;
            }

            // 1 is the ratio of a colour against itself and 21 is black on white; anything outside
            // is unsatisfiable and would fail forever.
            if (_minimumRatio < 1f || _minimumRatio > 21f)
            {
                error = $"Minimum ratio {_minimumRatio} is outside the achievable range [1, 21].";
                return false;
            }

            if (!string.IsNullOrEmpty(_underSurfaceTokenId)
                && !ColorTokenId.Validate(_underSurfaceTokenId, out string underError))
            {
                error = $"Contrast requirement under-surface is invalid: {underError}";
                return false;
            }

            error = null;
            return true;
        }
    }
}
