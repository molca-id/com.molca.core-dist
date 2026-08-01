using System;
using UnityEngine;
using UnityEngine.UI;
using Molca.ColorID;
using Molca.Localization;

namespace Molca.UI.Tokens
{
    /// <summary>The kind of UI concern a <see cref="MolcaUiToken"/> names.</summary>
    /// <remarks>
    /// The category decides which of the token's fields are meaningful and which apply path the
    /// resolver takes — a <see cref="Color"/> token drives a <c>ColorID</c>, a <see cref="Text"/> token a
    /// <c>LocalizedText</c> style preset, a <see cref="Surface"/> token an <c>Image</c> sprite + PPU, a
    /// <see cref="Control"/> token a prefab to instantiate, and a <see cref="Spacing"/> token a scalar.
    /// </remarks>
    public enum MolcaUiTokenCategory
    {
        /// <summary>A background/surface: sprite + image type + a PPU reference (see <see cref="MolcaUiToken.ReferencePixels"/>).</summary>
        Surface,
        /// <summary>
        /// A palette color, named as a canonical colour token (never a raw <see cref="Color"/>).
        /// </summary>
        /// <remarks>
        /// New authoring uses <see cref="MolcaUiToken.ColorToken"/>, a
        /// <see cref="Molca.ColorID.ColorTokenReference"/> into the project's Color Theme Set. The legacy
        /// swatch + step pair remains deserializable and resolvable for the compatibility window.
        /// </remarks>
        Color,
        /// <summary>A typography preset, referencing a <see cref="LocalizedTextStyleInfo"/>.</summary>
        Text,
        /// <summary>A reusable control, referencing a prefab (optionally a variant) to instantiate.</summary>
        Control,
        /// <summary>A layout scalar (gap/padding), in UI units.</summary>
        Spacing
    }

    /// <summary>
    /// A single named design token. Tokens <b>name</b> the framework's existing styling mechanisms
    /// (<c>ColorID</c> swatches, <see cref="LocalizedTextStyleInfo"/> presets, sprites, reusable prefabs)
    /// — they never store raw appearance, so re-theming continues to flow through those systems.
    /// </summary>
    /// <remarks>
    /// Deliberately a <i>flat</i> serializable record (a category discriminator + per-category fields)
    /// rather than a polymorphic hierarchy: it serializes cleanly inside a catalog asset without
    /// <c>[SerializeReference]</c> and the resolver simply switches on <see cref="Category"/>. Core defines
    /// the shape; concrete values are authored in an SDK/project catalog (see
    /// <see cref="MolcaUiTokenCatalog"/>), per the Core-vs-SDK layer model.
    /// </remarks>
    [Serializable]
    public class MolcaUiToken
    {
        [Tooltip("Token id in 'category/name' form, e.g. 'color/primary', 'surface/panel-bg'.")]
        [SerializeField] private string _id;
        [SerializeField] private MolcaUiTokenCategory _category;

        [Header("Color (category = Color)")]
        [Tooltip("Canonical colour token from the project's Color Theme Set. Preferred; the legacy "
               + "swatch + colour ID below are only read when this is unassigned.")]
        [SerializeField] private ColorTokenReference _colorToken;

        // Legacy V1 pair. Kept serialized so existing catalogs keep resolving, and read only when
        // _colorToken is unassigned. No initializers: a default of "Default"/"Primary" would give every
        // canonically-authored token a legacy pair too, which makes "which one did the author mean"
        // unanswerable and hides an unmigrated entry from the audit.
        [Tooltip("Legacy V1 swatch name. Superseded by the canonical colour token above.")]
        [SerializeField] private string _swatchName;
        [Tooltip("Legacy V1 colour ID. Superseded by the canonical colour token above.")]
        [SerializeField] private string _colorId;

        [Header("Text (category = Text)")]
        [SerializeField] private LocalizedTextStyleInfo _styleInfo;

        [Header("Surface (category = Surface)")]
        [SerializeField] private Sprite _sprite;
        [SerializeField] private Image.Type _imageType = Image.Type.Sliced;
        [Tooltip("PPU-rule numerator: pixelsPerUnitMultiplier = ReferencePixels / min(rectWidth, rectHeight), "
               + "so a 9-sliced corner radius stays visually constant across rect sizes.")]
        [SerializeField] private float _referencePixels = 176f;

        [Header("Control (category = Control)")]
        [SerializeField] private GameObject _prefab;

        [Header("Spacing (category = Spacing)")]
        [SerializeField] private float _value;

        /// <summary>The token id in <c>category/name</c> form (e.g. <c>color/primary</c>).</summary>
        public string Id => _id;
        /// <summary>Which concern this token names; decides the resolver's apply path.</summary>
        public MolcaUiTokenCategory Category => _category;

        /// <summary>
        /// Color token: the canonical colour token this names, e.g. <c>action/primary/fill</c>.
        /// </summary>
        /// <remarks>
        /// The authoring API. A catalog entry carrying this resolves through the Color Theme Set, which
        /// means it participates in variant switching, contrast validation and the reference audit — none
        /// of which the legacy pair can offer, because a V1 <c>(swatch, colorId)</c> had no declared usage
        /// and no guarantee of existing in every variant.
        /// </remarks>
        public ColorTokenReference ColorToken => _colorToken;

        /// <summary>Whether this token names a canonical colour token.</summary>
        public bool HasCanonicalColorToken => _colorToken.IsAssigned;

        /// <summary>Whether this token still carries only the legacy V1 pair.</summary>
        /// <remarks>
        /// What the migration audit counts. <c>true</c> means the entry has not been migrated yet; it
        /// still resolves, through the theme set's alias map when a theme set is installed.
        /// </remarks>
        public bool HasLegacyColorPair =>
            !_colorToken.IsAssigned
            && !string.IsNullOrEmpty(_swatchName) && !string.IsNullOrEmpty(_colorId);

        /// <summary>Legacy color token: the V1 <c>ColorID</c> swatch name (e.g. <c>Default</c>).</summary>
        /// <remarks>Read only when <see cref="ColorToken"/> is unassigned.</remarks>
        public string SwatchName => _swatchName;

        /// <summary>Legacy color token: the V1 step within the swatch (e.g. <c>Primary</c>, <c>60</c>).</summary>
        /// <remarks>Read only when <see cref="ColorToken"/> is unassigned.</remarks>
        public string ColorId => _colorId;

        /// <summary>Text token: the typography preset applied to a <see cref="LocalizedText"/>.</summary>
        public LocalizedTextStyleInfo StyleInfo => _styleInfo;

        /// <summary>Surface token: the (typically 9-sliced) background sprite.</summary>
        public Sprite Sprite => _sprite;
        /// <summary>Surface token: the <see cref="Image.Type"/> to set on the target image.</summary>
        public Image.Type ImageType => _imageType;
        /// <summary>Surface token: the PPU-rule numerator (see field tooltip).</summary>
        public float ReferencePixels => _referencePixels;

        /// <summary>Control token: the prefab (or prefab variant) to instantiate.</summary>
        public GameObject Prefab => _prefab;

        /// <summary>Spacing token: the layout scalar in UI units.</summary>
        public float Value => _value;

        private MolcaUiToken(string id, MolcaUiTokenCategory category)
        {
            _id = id;
            _category = category;
        }

        /// <summary>
        /// Builds a <see cref="MolcaUiTokenCategory.Color"/> token naming a canonical colour token.
        /// </summary>
        /// <param name="id">The catalog token id, in <c>category/name</c> form.</param>
        /// <param name="canonicalTokenId">The Color Theme Set token id, e.g. <c>text/primary</c>.</param>
        public static MolcaUiToken NewColorToken(string id, string canonicalTokenId) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Color)
            { _colorToken = new ColorTokenReference(canonicalTokenId) };

        /// <summary>
        /// Builds a <see cref="MolcaUiTokenCategory.Color"/> token from a legacy V1 swatch + step.
        /// </summary>
        /// <param name="id">The catalog token id, in <c>category/name</c> form.</param>
        /// <param name="swatchName">The V1 swatch name.</param>
        /// <param name="colorId">The V1 colour ID within that swatch.</param>
        /// <remarks>
        /// Retained for the compatibility window and for fixtures that exercise the legacy resolution
        /// path. New authoring uses <see cref="NewColorToken"/>.
        /// <para/>
        /// Deprecated so that "new authoring creates no legacy swatch/colour pairs" is enforced by the
        /// compiler rather than by convention: a legacy pair resolves only through the alias map, which is
        /// itself scheduled for removal, so a token authored this way acquires a dependency on something
        /// with an end date.
        /// </remarks>
        [Obsolete("A catalog colour token authored as a legacy swatch/colorId pair depends on the legacy "
                  + "alias map, which is scheduled for removal in Core 2.0.0. Use "
                  + "NewColorToken(id, canonicalTokenId). Existing pairs keep resolving; run "
                  + "MolcaUiTokenCatalogMigration to convert them.")]
        public static MolcaUiToken NewColor(string id, string swatchName, string colorId) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Color) { _swatchName = swatchName, _colorId = colorId };

        /// <summary>Builds a <see cref="MolcaUiTokenCategory.Text"/> token (a typography style preset).</summary>
        public static MolcaUiToken NewText(string id, LocalizedTextStyleInfo styleInfo) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Text) { _styleInfo = styleInfo };

        /// <summary>Builds a <see cref="MolcaUiTokenCategory.Surface"/> token (sprite + image type + PPU reference).</summary>
        public static MolcaUiToken NewSurface(string id, Sprite sprite, Image.Type imageType, float referencePixels) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Surface)
            { _sprite = sprite, _imageType = imageType, _referencePixels = referencePixels };

        /// <summary>Builds a <see cref="MolcaUiTokenCategory.Control"/> token (a reusable prefab).</summary>
        public static MolcaUiToken NewControl(string id, GameObject prefab) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Control) { _prefab = prefab };

        /// <summary>Builds a <see cref="MolcaUiTokenCategory.Spacing"/> token (a layout scalar).</summary>
        public static MolcaUiToken NewSpacing(string id, float value) =>
            new MolcaUiToken(id, MolcaUiTokenCategory.Spacing) { _value = value };
    }
}
