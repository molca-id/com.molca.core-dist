using System;
using UnityEngine;
using UnityEngine.UI;
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
        /// <summary>A palette color, named as a <c>ColorID</c> swatch + step (never a raw <see cref="Color"/>).</summary>
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
        [SerializeField] private string _swatchName = "Default";
        [SerializeField] private string _colorId = "Primary";

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

        /// <summary>Color token: the <c>ColorID</c> swatch name (e.g. <c>Default</c>, <c>Gray</c>).</summary>
        public string SwatchName => _swatchName;
        /// <summary>Color token: the <c>ColorID</c> step within the swatch (e.g. <c>Primary</c>, <c>60</c>).</summary>
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

        /// <summary>Builds a <see cref="MolcaUiTokenCategory.Color"/> token (a <c>ColorID</c> swatch + step).</summary>
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
