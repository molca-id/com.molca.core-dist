using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Molca.Editor.UI.Figma;

namespace Molca.Editor.UI.Build
{
    /// <summary>
    /// Applies layout to a materialized tree from the spec's intent (Sprint 59.2): <c>vertical</c>/
    /// <c>horizontal</c> → a layout group with the spec's <c>gap</c>/<c>padding</c> (+ a
    /// <see cref="ContentSizeFitter"/> when the node hugs content); <c>none</c> + <c>sizeHint:stretch</c> →
    /// 0–1 fill anchors; a <c>list</c> container gets a vertical layout group so rows stack. Every value
    /// comes from the spec — no magic numbers.
    /// </summary>
    /// <remarks>
    /// Scrolling rig (ScrollRect + viewport mask) for lists is intentionally left to the human polish pass;
    /// this produces a correct, stacked, deterministic draft.
    /// </remarks>
    public static class UguiLayoutPass
    {
        public static void Apply(IReadOnlyList<UguiMaterializer.NodeBinding> bindings)
        {
            if (bindings == null) return;
            foreach (var binding in bindings)
                ApplyNode(binding.Go, binding.Node);
        }

        private static void ApplyNode(GameObject go, UiIntentNode node)
        {
            if (go == null || node == null) return;

            if (node.layout == "vertical" || node.layout == "horizontal")
            {
                HorizontalOrVerticalLayoutGroup group = node.layout == "horizontal"
                    ? (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>()
                    : go.AddComponent<VerticalLayoutGroup>();

                group.spacing = node.gap;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                ApplyPadding(group, node.padding);

                if (node.sizeHint == "hug")
                {
                    var fitter = go.AddComponent<ContentSizeFitter>();
                    fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }
            }
            else if (node.layout == "none" && node.sizeHint == "stretch")
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }

            // A list container stacks its rows; guard against doubling a layout group already added above.
            if (node.type == "list" && go.GetComponent<HorizontalOrVerticalLayoutGroup>() == null)
            {
                var vertical = go.AddComponent<VerticalLayoutGroup>();
                vertical.spacing = node.gap;
                vertical.childControlWidth = true;
                vertical.childControlHeight = true;
                vertical.childForceExpandHeight = false;
                ApplyPadding(vertical, node.padding);
            }
        }

        // Spec padding is [left, top, right, bottom]; RectOffset is (left, right, top, bottom).
        private static void ApplyPadding(HorizontalOrVerticalLayoutGroup group, float[] padding)
        {
            if (padding == null || padding.Length != 4) return;
            group.padding = new RectOffset(
                (int)padding[0], (int)padding[2], (int)padding[1], (int)padding[3]);
        }
    }
}
