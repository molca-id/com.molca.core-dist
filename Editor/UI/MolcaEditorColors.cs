using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI
{
    /// <summary>
    /// C# mirror of the shared editor design tokens for IMGUI surfaces and GraphView nodes.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/</c>.
    /// USS windows resolve colors from <c>MolcaEditorTokens.uss</c> via <c>var(--molca-*)</c>; IMGUI
    /// (<c>OnGUI</c>/<c>OnInspectorGUI</c>) and GraphView code cannot, so they read the same palette
    /// here instead of hardcoding hex. Values mirror the tokens one-for-one (dark + light skin) so the
    /// two paths stay in sync — when a token changes, change it in both places. See
    /// <c>Documentation~/EDITOR_DESIGN_LANGUAGE.md</c>.
    /// </remarks>
    public static class MolcaEditorColors
    {
        private static bool Pro => EditorGUIUtility.isProSkin;

        private static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        /// <summary>Main background (<c>--molca-bg</c>).</summary>
        public static Color Bg => Pro ? C(56, 56, 56) : C(194, 194, 194);

        /// <summary>Panel / rail-adjacent surface (<c>--molca-panel</c>).</summary>
        public static Color Panel => Pro ? C(48, 48, 48) : C(200, 200, 200);

        /// <summary>Card base (<c>--molca-card</c>).</summary>
        public static Color Card => Pro ? C(47, 47, 47) : C(207, 207, 207);

        /// <summary>Card header band (<c>--molca-card-header</c>).</summary>
        public static Color CardHeader => Pro ? C(58, 58, 58) : C(196, 196, 196);

        /// <summary>Text field / readonly box (<c>--molca-input</c>).</summary>
        public static Color Input => Pro ? C(43, 43, 43) : C(222, 222, 222);

        /// <summary>Strong border (<c>--molca-border</c>).</summary>
        public static Color Border => Pro ? C(31, 31, 31) : C(150, 150, 150);

        /// <summary>Card/header outline (<c>--molca-border-soft</c>).</summary>
        public static Color BorderSoft => Pro ? C(37, 37, 37) : C(168, 168, 168);

        /// <summary>Body text (<c>--molca-text</c>).</summary>
        public static Color Text => Pro ? C(194, 194, 194) : C(38, 38, 38);

        /// <summary>Titles (<c>--molca-heading</c>).</summary>
        public static Color Heading => Pro ? C(220, 220, 220) : C(20, 20, 20);

        /// <summary>Helper text (<c>--molca-muted</c>).</summary>
        public static Color Muted => Pro ? C(138, 138, 138) : C(102, 102, 102);

        /// <summary>Field labels (<c>--molca-label</c>).</summary>
        public static Color Label => Pro ? C(182, 182, 182) : C(56, 56, 56);

        /// <summary>Primary action / active accent (<c>--molca-primary</c>).</summary>
        public static Color Primary => C(59, 103, 150);

        /// <summary>Selected row fill (<c>--molca-row-selected</c>).</summary>
        public static Color RowSelected => Pro ? C(59, 94, 128) : C(140, 170, 205);

        /// <summary>Read-only URL/link text (<c>--molca-link</c>).</summary>
        public static Color Link => Pro ? C(91, 155, 213) : C(38, 96, 158);

        /// <summary>Molca selection accent (<c>--molca-accent</c>).</summary>
        public static Color Accent => C(198, 242, 58);

        /// <summary>Status: OK / healthy (<c>--molca-status-green</c>).</summary>
        public static Color StatusOk => C(87, 200, 74);

        /// <summary>Status: idle / neutral (<c>--molca-status-grey</c>).</summary>
        public static Color StatusIdle => C(107, 107, 107);

        /// <summary>Status: warning (<c>--molca-status-warn</c>).</summary>
        public static Color StatusWarn => C(216, 178, 74);

        /// <summary>Status: error (<c>--molca-status-error</c>).</summary>
        public static Color StatusError => C(224, 112, 58);
    }
}
