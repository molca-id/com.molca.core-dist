using UnityEngine;
using UnityEditor;
using Molca.Attributes;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(InfoBoxAttribute))]
    public class InfoBoxDrawer : DecoratorDrawer
    {
        private const float PAD = 6f;
        private const float ICON_STRIP_WIDTH = 24f;
        private const float ICON_SIZE = 14f;
        private const float DEFAULT_WIDTH = 300f;

        // [Info, Warning, Error] x [dark, light]
        private static readonly Color[,] TextColors = {
            { new(0.6f, 0.85f, 1.0f), new(0.12f, 0.35f, 0.85f) },
            { new(1.0f, 0.85f, 0.35f), new(0.85f, 0.45f, 0.05f) },
            { new(1.0f, 0.45f, 0.45f), new(0.9f, 0.15f, 0.15f) }
        };
        private static readonly Color[,] BgColors = {
            { new(0.18f, 0.22f, 0.28f), new(0.88f, 0.92f, 1.0f) },
            { new(0.28f, 0.22f, 0.18f), new(1.0f, 0.96f, 0.88f) },
            { new(0.28f, 0.18f, 0.18f), new(1.0f, 0.9f, 0.9f) }
        };
        private static readonly Color[,] StripColors = {
            { new(0.25f, 0.45f, 0.7f, 0.5f), new(0.4f, 0.6f, 1.0f, 0.35f) },
            { new(0.7f, 0.55f, 0.25f, 0.5f), new(1.0f, 0.7f, 0.2f, 0.35f) },
            { new(0.7f, 0.3f, 0.3f, 0.5f), new(1.0f, 0.35f, 0.35f, 0.35f) }
        };
        private static readonly Color[,] BorderColors = {
            { new(0.35f, 0.45f, 0.6f, 0.5f), new(0.5f, 0.6f, 0.85f, 0.4f) },
            { new(0.6f, 0.5f, 0.35f, 0.5f), new(0.85f, 0.65f, 0.2f, 0.4f) },
            { new(0.6f, 0.35f, 0.35f, 0.5f), new(0.85f, 0.4f, 0.4f, 0.4f) }
        };
        private static readonly string[] IconNames = { "console.infoicon", "console.warnicon", "console.erroricon" };

        public override float GetHeight()
        {
            if (attribute is not InfoBoxAttribute infoBox) return 0f;
            var style = GetStyle(infoBox.Type);
            float textWidth = DEFAULT_WIDTH - ICON_STRIP_WIDTH - PAD * 2 - PAD;
            float h = style.CalcHeight(new GUIContent(infoBox.Message), textWidth);
            return Mathf.Max(h + PAD * 2, ICON_STRIP_WIDTH + PAD * 2);
        }

        public override void OnGUI(Rect position)
        {
            if (attribute is not InfoBoxAttribute infoBox) return;
            int t = (int)infoBox.Type;
            if (t < 0 || t > 2) t = 0;
            bool dark = EditorGUIUtility.isProSkin;
            int theme = dark ? 0 : 1;

            // Even 1px border (four lines so no uneven rendering)
            float b = 1f;
            Color border = BorderColors[t, theme];
            EditorGUI.DrawRect(new Rect(position.x, position.y, position.width, b), border);
            EditorGUI.DrawRect(new Rect(position.x, position.yMax - b, position.width, b), border);
            EditorGUI.DrawRect(new Rect(position.x, position.y, b, position.height), border);
            EditorGUI.DrawRect(new Rect(position.xMax - b, position.y, b, position.height), border);

            Rect inner = new Rect(position.x + b, position.y + b, position.width - b * 2, position.height - b * 2);
            EditorGUI.DrawRect(inner, BgColors[t, theme]);

            // Icon strip
            Rect stripRect = new Rect(inner.x, inner.y, ICON_STRIP_WIDTH, inner.height);
            EditorGUI.DrawRect(stripRect, StripColors[t, theme]);
            Rect iconRect = new Rect(
                stripRect.x + (stripRect.width - ICON_SIZE) * 0.5f,
                stripRect.y + (stripRect.height - ICON_SIZE) * 0.5f,
                ICON_SIZE, ICON_SIZE
            );
            GUI.Label(iconRect, EditorGUIUtility.IconContent(IconNames[t]));

            // Message
            float textX = inner.x + ICON_STRIP_WIDTH + PAD;
            Rect msgRect = new Rect(textX, inner.y + PAD, inner.xMax - textX - PAD, inner.height - PAD * 2);
            EditorGUI.LabelField(msgRect, infoBox.Message, GetStyle(infoBox.Type));
        }

        private static GUIStyle GetStyle(InfoBoxType type)
        {
            int t = (int)type;
            if (t < 0 || t > 2) t = 0;
            var style = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
                padding = new RectOffset(0, 0, 0, 0)
            };
            style.normal.textColor = TextColors[t, EditorGUIUtility.isProSkin ? 0 : 1];
            return style;
        }
    }
}
