using UnityEngine;

namespace Molca.UI.Tokens
{
    /// <summary>
    /// Marks a UI GameObject as styled by a single design token (e.g. <c>color/primary</c>,
    /// <c>surface/panel-bg</c>, <c>text/title</c>) from a <see cref="MolcaUiTokenRegistry"/>. This is the
    /// hand-authoring counterpart to the Figma materializer: the component only <i>records</i> the token;
    /// an editor action resolves the catalog and writes the concrete <c>ColorID</c>/<c>LocalizedText</c>/
    /// <c>Image</c> values onto this object.
    /// </summary>
    /// <remarks>
    /// Deliberately has no runtime behavior (no <c>Update</c>, no <c>Awake</c> apply) — the styling is baked
    /// at edit time into real components, so there is zero per-frame cost and the object works with the
    /// registry absent at runtime. uGUI has no cascading stylesheet, so this is the closest analogue to a
    /// USS class binding.
    /// </remarks>
    [AddComponentMenu("Molca/UI/Style Applier")]
    [DisallowMultipleComponent]
    public class MolcaStyleApplier : MonoBehaviour
    {
        [Tooltip("Catalog that resolves the token. Usually the project/SDK UI Token Catalog asset.")]
        [SerializeField] private MolcaUiTokenRegistry _catalog;

        [Tooltip("Token id in 'category/name' form, e.g. 'color/primary'.")]
        [SerializeField] private string _token;

        /// <summary>The registry that resolves <see cref="Token"/>.</summary>
        public MolcaUiTokenRegistry Catalog => _catalog;

        /// <summary>The token id this object is styled by, in <c>category/name</c> form.</summary>
        public string Token => _token;
    }
}
