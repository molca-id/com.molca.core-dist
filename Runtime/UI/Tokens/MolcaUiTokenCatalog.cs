using System.Collections.Generic;
using UnityEngine;

namespace Molca.UI.Tokens
{
    /// <summary>
    /// The default concrete <see cref="MolcaUiTokenRegistry"/>: a serialized list of tokens authored as an
    /// asset. An SDK or project creates one of these (Create > Molca > UI > UI Token Catalog) — typically
    /// seeded by mining its real UI prefabs — to bind <c>color/*</c> tokens to <c>ColorID</c> swatches,
    /// <c>text/*</c> to style presets, <c>surface/*</c> to sprites, and <c>control/*</c> to reusable prefabs.
    /// </summary>
    /// <remarks>
    /// Core ships this container but no token <i>values</i>; the asset (with its values) lives in the
    /// SDK/project layer. Forks that need custom resolution can instead subclass
    /// <see cref="MolcaUiTokenRegistry"/> directly.
    /// </remarks>
    [CreateAssetMenu(fileName = "UI Token Catalog", menuName = "Molca/UI/UI Token Catalog", order = 60)]
    public class MolcaUiTokenCatalog : MolcaUiTokenRegistry
    {
        [SerializeField] private List<MolcaUiToken> _tokens = new List<MolcaUiToken>();

        [Tooltip("Assembly-qualified GraphicRaycaster type for world-space roots (e.g. XRI's "
               + "TrackedDeviceGraphicRaycaster). Empty → the built-in GraphicRaycaster.")]
        [SerializeField] private string _vrRaycasterTypeName;

        /// <inheritdoc />
        public override IReadOnlyList<MolcaUiToken> AllTokens => _tokens;

        /// <inheritdoc />
        public override string VrRaycasterTypeName =>
            string.IsNullOrWhiteSpace(_vrRaycasterTypeName) ? null : _vrRaycasterTypeName;

#if UNITY_EDITOR
        /// <summary>
        /// Replaces the catalog's tokens. Editor-only authoring entry point (used by the token miner and
        /// tests) — populating a catalog is a design-time operation, never a runtime write.
        /// </summary>
        public void EditorSetTokens(IEnumerable<MolcaUiToken> tokens)
        {
            _tokens = tokens != null ? new List<MolcaUiToken>(tokens) : new List<MolcaUiToken>();
        }
#endif
    }
}
