using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Molca.UI.Tokens;
using Molca.Editor.UI.Figma;

namespace Molca.Editor.UI.Build
{
    /// <summary>How the generated root canvas renders — VR world-space, or a flat screen canvas.</summary>
    public enum UguiCanvasMode
    {
        /// <summary>Screen-space overlay — standard non-VR UI drawn on top of everything.</summary>
        Overlay,
        /// <summary>Screen-space camera — non-VR UI rendered by a specific camera (assign it after build).</summary>
        Camera,
        /// <summary>World-space — VR/diegetic UI scaled to <c>worldScale</c> metres (the default).</summary>
        World
    }

    /// <summary>
    /// Applies the VR rules a Figma frame can't carry, mechanically from spec inputs + catalog policy
    /// (Sprint 59.3): the root becomes a <b>world-space</b> <see cref="Canvas"/> scaled so its width equals
    /// <c>worldScale</c> metres; a <see cref="GraphicRaycaster"/> is attached — the catalog-declared type
    /// (e.g. XRI's <c>TrackedDeviceGraphicRaycaster</c>) when set, else the built-in one; interactive rects
    /// are grown to at least <c>minHitCm</c>; and list containers get a nested canvas to isolate their
    /// dynamic redraws from the static panel batch.
    /// </summary>
    /// <remarks>
    /// Core stays XR-Interaction-Toolkit-agnostic: the raycaster type is resolved by name from catalog
    /// policy via reflection, never referenced directly.
    /// </remarks>
    public static class UguiVrPass
    {
        /// <summary>Design canvas width (px) used when the root rect has no authored size yet.</summary>
        public const float DefaultDesignWidthPx = 1000f;

        public static void Apply(GameObject root, UiIntentSpec spec, MolcaUiTokenRegistry catalog,
            IReadOnlyList<UguiMaterializer.NodeBinding> bindings, List<string> notes,
            UguiCanvasMode canvasMode = UguiCanvasMode.World)
        {
            if (root == null || spec == null) return;

            var rt = root.GetComponent<RectTransform>();
            if (rt == null) rt = root.AddComponent<RectTransform>();

            var canvas = root.GetComponent<Canvas>();
            if (canvas == null) canvas = root.AddComponent<Canvas>();
            var scaler = root.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = root.AddComponent<CanvasScaler>();

            float widthPx = rt.rect.width > 0f ? rt.rect.width
                          : (rt.sizeDelta.x > 0f ? rt.sizeDelta.x : DefaultDesignWidthPx);
            float heightPx = rt.rect.height > 0f ? rt.rect.height
                           : (rt.sizeDelta.y > 0f ? rt.sizeDelta.y : DefaultDesignWidthPx);
            if (rt.sizeDelta.x <= 0f) rt.sizeDelta = new Vector2(DefaultDesignWidthPx, rt.sizeDelta.y);

            float minHitPx = 0f;
            if (canvasMode == UguiCanvasMode.World)
            {
                // Diegetic UI: keep design px as the rect size; scale the canvas so its width = worldScale m.
                canvas.renderMode = RenderMode.WorldSpace;
                float worldScale = spec.worldScale > 0f ? spec.worldScale : 0.5f;
                float metresPerPx = worldScale / widthPx;
                rt.localScale = new Vector3(metresPerPx, metresPerPx, metresPerPx);
                // Comfortable VR hit targets (px = metres / metresPerPx).
                minHitPx = (spec.minHitCm / 100f) / metresPerPx;
            }
            else
            {
                // Flat screen UI: a normal screen-space canvas that scales with the design resolution.
                canvas.renderMode = canvasMode == UguiCanvasMode.Camera
                    ? RenderMode.ScreenSpaceCamera   // caller assigns canvas.worldCamera after build
                    : RenderMode.ScreenSpaceOverlay;
                rt.localScale = Vector3.one;
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(widthPx, heightPx);
                // minHitCm is a VR-physical input; in screen space hit sizing is left to the design px.
            }

            AttachRaycaster(root, catalog, notes);

            if (bindings != null)
                foreach (var binding in bindings)
                {
                    if (minHitPx > 0f && binding.Node.type == "button")
                        GrowToMin(binding.Go, minHitPx);

                    // Isolate each list's dynamic content into its own batch (no overrideSorting needed).
                    if (binding.Node.type == "list" && binding.Go.GetComponent<Canvas>() == null)
                        binding.Go.AddComponent<Canvas>();
                }
        }

        private static void GrowToMin(GameObject go, float minPx)
        {
            var rt = go != null ? go.GetComponent<RectTransform>() : null;
            if (rt == null || minPx <= 0f) return;
            var size = rt.sizeDelta;
            rt.sizeDelta = new Vector2(Mathf.Max(size.x, minPx), Mathf.Max(size.y, minPx));
        }

        private static void AttachRaycaster(GameObject root, MolcaUiTokenRegistry catalog, List<string> notes)
        {
            string typeName = catalog != null ? catalog.VrRaycasterTypeName : null;
            if (!string.IsNullOrEmpty(typeName))
            {
                var type = Type.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                {
                    if (root.GetComponent(type) == null) root.AddComponent(type);
                    return;
                }
                notes?.Add($"VR raycaster type '{typeName}' not found; using the built-in GraphicRaycaster.");
            }
            if (root.GetComponent<GraphicRaycaster>() == null) root.AddComponent<GraphicRaycaster>();
        }
    }
}
