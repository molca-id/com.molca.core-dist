using System.Collections.Generic;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// The token-referential, Unity-internal-free description of a UI a Figma frame maps to — the
    /// semantic layer between "pixels in Figma" and "uGUI prefab in Unity" (Sprint 58). Every visual
    /// choice is a Molca token id (Sprint 57); there are no anchors, sizeDeltas, PPU values, sprite GUIDs,
    /// or hex colors. The materializer (Sprint 59) consumes this to build the actual prefab.
    /// </summary>
    /// <remarks>
    /// Plain POCO for Newtonsoft (de)serialization. The VR-physical inputs (<see cref="worldScale"/>,
    /// <see cref="minHitCm"/>) are <b>supplied by the caller</b>, never inferred from Figma — the frame
    /// carries no physical size.
    /// </remarks>
    public sealed class UiIntentSpec
    {
        /// <summary>The Figma frame this was derived from (name or node id), for traceability.</summary>
        public string sourceFrame;

        /// <summary>World-space width of the panel in metres (caller-supplied VR input).</summary>
        public float worldScale;

        /// <summary>Minimum comfortable hit-target size in centimetres (caller-supplied VR input).</summary>
        public float minHitCm;

        /// <summary>The catalog this spec's tokens are validated against (name/id).</summary>
        public string catalogId;

        /// <summary>The root node of the UI tree.</summary>
        public UiIntentNode root;
    }

    /// <summary>One node in a <see cref="UiIntentSpec"/> tree. All appearance is by token reference.</summary>
    public sealed class UiIntentNode
    {
        /// <summary>Structural kind: <c>panel</c>, <c>group</c>, <c>text</c>, <c>button</c>, <c>list</c>, <c>image</c>.</summary>
        public string type;

        /// <summary>The primary token id for this node (e.g. <c>surface/panel-bg</c>, <c>control/button</c>). Optional for pure groups.</summary>
        public string token;

        /// <summary>Optional color token override (a <c>color/*</c> id), e.g. for a button's tint or text color.</summary>
        public string color;

        /// <summary>Optional text-style token override (a <c>text/*</c> id) for text/button labels.</summary>
        public string text;

        /// <summary>Localization key for text content (text/button nodes).</summary>
        public string locKey;

        /// <summary>Child layout: <c>vertical</c>, <c>horizontal</c>, or <c>none</c> (explicit/absolute).</summary>
        public string layout;

        /// <summary>Gap between children in UI units (layout vertical/horizontal).</summary>
        public float gap;

        /// <summary>Padding as <c>[left, top, right, bottom]</c> in UI units; null/empty means none.</summary>
        public float[] padding;

        /// <summary>Relative size intent: <c>stretch</c>, <c>hug</c>, or <c>fixed</c> (advisory for the materializer).</summary>
        public string sizeHint;

        /// <summary>For a <c>list</c> node, the data/collection this repeats over (a hint for the materializer).</summary>
        public string bind;

        /// <summary>Child nodes, in order.</summary>
        public List<UiIntentNode> children;
    }
}
