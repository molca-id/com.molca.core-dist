using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Molca.Localization;
using Molca.UI.Tokens;
using ColorIDComponent = Molca.ColorID.ColorID;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Applies a <see cref="MolcaUiToken"/> to a GameObject at edit time by writing the concrete framework
    /// components it names — a color token drives a <see cref="ColorID"/>, a text token a
    /// <see cref="LocalizedText"/> style preset, a surface token an <see cref="Image"/>'s sprite/type/PPU.
    /// The materializer (Sprint 59) and the <see cref="MolcaStyleApplier"/> inspector both route through
    /// here, so token application is defined in exactly one place.
    /// </summary>
    /// <remarks>
    /// Edit-time only. The styling is baked into real components (indistinguishable from hand-authoring),
    /// so nothing here runs at play time. Mutations are registered with <see cref="Undo"/> so an apply is
    /// revertible. Control/spacing tokens are not "applied" to an object: control tokens are instantiated
    /// by the materializer, spacing tokens are layout scalars consumed by the layout pass.
    /// </remarks>
    public static class MolcaUiTokenResolver
    {
        /// <summary>
        /// Resolves <paramref name="tokenId"/> in <paramref name="catalog"/> and applies it to
        /// <paramref name="target"/>. Returns false with <paramref name="error"/> set on any problem
        /// (unknown token, wrong category for in-place application, missing prerequisite component).
        /// </summary>
        public static bool TryApply(MolcaUiTokenRegistry catalog, string tokenId, GameObject target, out string error)
        {
            error = null;
            if (catalog == null) { error = "No token catalog assigned."; return false; }
            if (target == null) { error = "No target GameObject."; return false; }
            if (!catalog.TryResolve(tokenId, out var token))
            {
                error = $"Token '{tokenId}' is not in catalog '{catalog.name}'.";
                return false;
            }

            switch (token.Category)
            {
                case MolcaUiTokenCategory.Color: return ApplyColor(token, target, out error);
                case MolcaUiTokenCategory.Text: return ApplyText(token, target, out error);
                case MolcaUiTokenCategory.Surface: return ApplySurface(token, target, out error);
                case MolcaUiTokenCategory.Spacing:
                    error = "Spacing tokens are layout scalars, not applied to a GameObject.";
                    return false;
                case MolcaUiTokenCategory.Control:
                    error = "Control tokens are instantiated (by the materializer), not applied in place.";
                    return false;
                default:
                    error = $"Unhandled token category '{token.Category}'.";
                    return false;
            }
        }

        private static bool ApplyColor(MolcaUiToken token, GameObject target, out string error)
        {
            error = null;
            var colorId = target.GetComponent<ColorIDComponent>();
            if (colorId == null) colorId = Undo.AddComponent<ColorIDComponent>(target);
            else Undo.RecordObject(colorId, "Apply Color Token");

            colorId.SetColor(token.SwatchName, token.ColorId);
            // Auto-detect the graphic(s) on this object and apply the swatch+step to them.
            colorId.Refresh();
            EditorUtility.SetDirty(colorId);
            return true;
        }

        private static bool ApplyText(MolcaUiToken token, GameObject target, out string error)
        {
            error = null;
            if (token.StyleInfo == null)
            {
                error = "Text token has no style preset (LocalizedTextStyleInfo) assigned.";
                return false;
            }

            // LocalizedText carries [RequireComponent(typeof(TextMeshProUGUI))], so adding it auto-adds the
            // text component when absent — no direct TMP dependency needed here. A text token styles the
            // text element; it does not author its content.
            var localizedText = target.GetComponent<LocalizedText>();
            if (localizedText == null) localizedText = Undo.AddComponent<LocalizedText>(target);
            if (localizedText == null)
            {
                error = "Could not add LocalizedText to the target.";
                return false;
            }

            // styleInfo is a protected serialized field — set it through SerializedObject, then apply.
            var so = new SerializedObject(localizedText);
            var prop = so.FindProperty("styleInfo");
            if (prop == null)
            {
                error = "LocalizedText has no serialized 'styleInfo' field.";
                return false;
            }
            prop.objectReferenceValue = token.StyleInfo;
            so.ApplyModifiedProperties();
            localizedText.ApplyStyle();
            EditorUtility.SetDirty(localizedText);
            return true;
        }

        private static bool ApplySurface(MolcaUiToken token, GameObject target, out string error)
        {
            error = null;
            var image = target.GetComponent<Image>();
            if (image == null) image = Undo.AddComponent<Image>(target);
            else Undo.RecordObject(image, "Apply Surface Token");

            image.sprite = token.Sprite;
            image.type = token.ImageType;

            // PPU rule: pixelsPerUnitMultiplier = ReferencePixels / min(rectWidth, rectHeight), so a
            // 9-sliced corner radius stays visually constant across sizes. Skipped if the rect has no size.
            var rt = target.GetComponent<RectTransform>();
            if (token.ReferencePixels > 0f && rt != null)
            {
                var size = rt.rect.size;
                float min = Mathf.Min(Mathf.Abs(size.x), Mathf.Abs(size.y));
                if (min > 0f)
                    image.pixelsPerUnitMultiplier = token.ReferencePixels / min;
            }

            EditorUtility.SetDirty(image);
            return true;
        }
    }
}
