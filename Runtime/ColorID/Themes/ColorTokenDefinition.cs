using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>Whether a token is a raw palette ingredient or a semantic application role.</summary>
    public enum ColorTokenKind
    {
        /// <summary>
        /// A palette ingredient such as <c>palette/neutral/900</c>. Primitives are building blocks
        /// for semantic tokens and are hidden from the default component picker.
        /// </summary>
        Primitive = 0,

        /// <summary>
        /// An application role such as <c>text/primary</c> or <c>action/primary/fill</c>. Semantic
        /// tokens are the authoring API components bind to.
        /// </summary>
        Semantic = 1
    }

    /// <summary>
    /// What a token is intended to colour. Drives picker grouping and, critically, which
    /// accessibility rules can be checked against it.
    /// </summary>
    /// <remarks>
    /// Flags rather than a single value because one token legitimately serves more than one purpose
    /// (a border colour that is also used for a focus ring). This metadata is the missing
    /// information that made V1 contrast checking impossible: without knowing whether a colour is a
    /// surface or a foreground, a raw ratio against the background cannot be judged. The shipped
    /// Light palette produces ratios of 1.14:1 for <c>Success</c> and 1.16:1 for <c>Warning</c>
    /// against its background — which is a defect if those are text and completely fine if they are
    /// fills, and V1 had no way to tell.
    /// </remarks>
    [Flags]
    public enum ColorTokenUsage
    {
        /// <summary>No declared usage. Treated as unreviewed rather than as "anything".</summary>
        None = 0,

        /// <summary>A background or panel fill.</summary>
        Surface = 1 << 0,

        /// <summary>Foreground text.</summary>
        Text = 1 << 1,

        /// <summary>Icon or glyph fill.</summary>
        Icon = 1 << 2,

        /// <summary>A border, divider or outline.</summary>
        Border = 1 << 3,

        /// <summary>A focus indicator.</summary>
        Focus = 1 << 4,

        /// <summary>A status fill or status foreground (info/success/warning/error).</summary>
        Status = 1 << 5,

        /// <summary>A data-visualisation series colour.</summary>
        DataVisualization = 1 << 6,

        /// <summary>Development-only or diagnostic colour, excluded from accessibility gates.</summary>
        Debug = 1 << 7,

        /// <summary>Explicitly declared as general-purpose. Distinct from <see cref="None"/>.</summary>
        Any = 1 << 8
    }

    /// <summary>
    /// The contract entry for one colour token: its identity and metadata, independent of any
    /// variant's value for it.
    /// </summary>
    /// <remarks>
    /// Definitions belong to the <see cref="ColorThemeSet"/>, not to a variant. That is the central
    /// design inversion of the revamp: V1 gave every theme its own independent list, so a key could
    /// exist in Dark and silently not exist in Light, and switching theme turned it magenta. Here a
    /// variant supplies <i>values for the shared contract</i> and cannot add or omit a required
    /// token.
    /// </remarks>
    [Serializable]
    public class ColorTokenDefinition
    {
        [SerializeField] private string _id;
        [SerializeField] private string _displayName;
        [SerializeField, TextArea(1, 4)] private string _description;
        [SerializeField] private ColorTokenKind _kind = ColorTokenKind.Semantic;
        [SerializeField] private ColorTokenUsage _usage = ColorTokenUsage.None;
        [SerializeField] private bool _required = true;
        [SerializeField] private bool _deprecated;
        [SerializeField] private string _replacementId;
        [SerializeField] private List<string> _tags = new List<string>();

        /// <summary>The canonical token ID. Identity — see <see cref="ColorTokenId"/>.</summary>
        public string Id => _id;

        /// <summary>Author-facing label. Presentation only; may change freely.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>What this token is for, and when to choose it over a neighbour.</summary>
        public string Description => _description;

        /// <summary>Whether this is a palette primitive or a semantic role.</summary>
        public ColorTokenKind Kind => _kind;

        /// <summary>What this token is intended to colour.</summary>
        public ColorTokenUsage Usage => _usage;

        /// <summary>
        /// Whether every selectable variant must resolve this token. A non-required token may be
        /// absent from some variants, and resolution reports it as missing rather than failing
        /// activation.
        /// </summary>
        public bool Required => _required;

        /// <summary>Whether authoring should steer away from this token.</summary>
        public bool Deprecated => _deprecated;

        /// <summary>
        /// The token to use instead of this deprecated one, or <c>null</c>. A deprecated token
        /// without a replacement is a validation finding, not a valid state.
        /// </summary>
        public string ReplacementId => _replacementId;

        /// <summary>Free-form tags for searching and filtering in the Hub.</summary>
        public IReadOnlyList<string> Tags => _tags;

        /// <summary>Creates a definition. Intended for authoring tools, importers and tests.</summary>
        /// <param name="id">The canonical token ID.</param>
        /// <param name="kind">Primitive or semantic.</param>
        /// <param name="usage">What the token colours.</param>
        /// <param name="required">Whether every selectable variant must resolve it.</param>
        /// <param name="displayName">Optional author-facing label.</param>
        /// <param name="description">Optional guidance.</param>
        public ColorTokenDefinition(string id, ColorTokenKind kind = ColorTokenKind.Semantic,
            ColorTokenUsage usage = ColorTokenUsage.None, bool required = true,
            string displayName = null, string description = null)
        {
            _id = id;
            _kind = kind;
            _usage = usage;
            _required = required;
            _displayName = displayName;
            _description = description;
        }

        /// <summary>Validates this definition in isolation.</summary>
        /// <param name="error">The first problem found, or <c>null</c> when valid.</param>
        /// <returns><c>true</c> when the definition is well-formed.</returns>
        public bool Validate(out string error)
        {
            if (!ColorTokenId.Validate(_id, out string idError))
            {
                error = $"Invalid token ID: {idError}";
                return false;
            }

            if (_deprecated && !string.IsNullOrEmpty(_replacementId)
                            && !ColorTokenId.Validate(_replacementId, out _))
            {
                error = $"Token '{_id}' names replacement '{_replacementId}', which is not a canonical token ID.";
                return false;
            }

            if (_deprecated && string.IsNullOrEmpty(_replacementId))
            {
                error = $"Token '{_id}' is deprecated but names no replacement, so migration has nowhere to go.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
