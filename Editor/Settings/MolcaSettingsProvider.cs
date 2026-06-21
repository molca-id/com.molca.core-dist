using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Molca.Editor.Hub;
using Molca.Editor.Icons;
using Molca.Editor.UI;

namespace Molca.Editor
{
    static class MolcaSettingsProvider
    {
        private const string SETTINGS_PATH = "Project/Molca";
        private const float SlimLabelWidth = 110f;
        private const float SlimProviderHeight = 320f;
        private static SerializedObject _settings;
        private static SerializedObject _editorSettings;
        private static GUIStyle _headerLabelStyle;
        private static GUIStyle _titleStyle;
        private static GUIStyle _versionPillStyle;
        private static GUIStyle _mutedLabelStyle;
        private static GUIStyle _primaryButtonStyle;
        private static GUIStyle _subtitleStyle;

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider(SETTINGS_PATH, SettingsScope.Project)
            {
                label = "Molca",
                guiHandler = (searchContext) =>
                {
                    if (_settings == null || _settings.targetObject == null)
                    {
                        var projectSettings = MolcaProjectSettings.Instance;
                        _settings = new SerializedObject(projectSettings);
                    }

                    if (_editorSettings == null || _editorSettings.targetObject == null)
                    {
                        var editorSettings = MolcaEditorSettings.Instance;
                        _editorSettings = new SerializedObject(editorSettings);
                    }

                    InitializeStyles();

                    _settings.Update();
                    _editorSettings.Update();

                    DrawSlimSettingsProvider();

                    _settings.ApplyModifiedProperties();
                    // The editor settings live in ProjectSettings/ (outside the AssetDatabase),
                    // so changes must be written explicitly instead of relying on SaveAssets.
                    if (_editorSettings.ApplyModifiedProperties())
                        MolcaEditorSettings.Instance.Save();
                },
                keywords = new HashSet<string>(new[] { "Molca", "Hub", "Open Molca Hub", "Project", "Identity", "Company", "Project Name", "Repository", "Documentation", "URL", "Version", "Logo", "Settings" })
            };

            return provider;
        }

        private static void InitializeStyles()
        {
            if (_headerLabelStyle == null)
            {
                _headerLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = MolcaEditorColors.Muted },
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(8, 8, 0, 0)
                };
            }
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = MolcaEditorColors.Heading }
                };
            }
            if (_versionPillStyle == null)
            {
                _versionPillStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 10,
                    normal = { textColor = MolcaEditorColors.Muted },
                    padding = new RectOffset(8, 8, 1, 1),
                    margin = new RectOffset(6, 0, 6, 0)
                };
            }
            if (_mutedLabelStyle == null)
            {
                _mutedLabelStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                {
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = MolcaEditorColors.Muted }
                };
            }
            if (_subtitleStyle == null)
            {
                _subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 10,
                    normal = { textColor = MolcaEditorColors.Muted }
                };
            }
            if (_primaryButtonStyle == null)
            {
                _primaryButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 12,
                    padding = new RectOffset(10, 10, 8, 8)
                };
            }
        }

        private static void DrawSlimSettingsProvider()
        {
            var rect = GUILayoutUtility.GetRect(
                1f,
                SlimProviderHeight,
                GUILayout.Height(SlimProviderHeight),
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(false));

            GUI.BeginGroup(rect);
            var content = new Rect(14f, 14f, Mathf.Max(0f, rect.width - 28f), rect.height - 28f);
            var width = content.width;
            DrawSlimHeader(new Rect(content.x, content.y, width, 30f));
            DrawSlimIdentityCard(new Rect(content.x, content.y + 44f, width, 150f));

            var buttonRect = new Rect(content.x, content.y + 210f, width, 34f);
            if (DrawPrimaryHubButton(buttonRect, "Open Molca Hub  ↗"))
                MolcaHubWindow.Open();

            GUI.Label(
                new Rect(content.x, content.y + 252f, width, 34f),
                "Builds, runtime modules, integrations & MCP live in the Hub window.",
                _mutedLabelStyle);
            GUI.EndGroup();
        }

        private static void DrawSlimHeader(Rect rect)
        {
            var logoRect = new Rect(rect.x, rect.y, 30f, 30f);
            var logo = MolcaEditorIcons.Logo;
            if (logo != null)
                GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(rect.x + 40f, rect.y + 1f, 190f, 18f), "Molca", _titleStyle);
            GUI.Label(new Rect(rect.x + 40f, rect.y + 17f, 190f, 14f), "SDK configuration", _subtitleStyle);
            DrawVersionPill(new Rect(rect.x + rect.width - 70f, rect.y + 5f, 70f, 20f), GetVersionPillText());
        }

        private static void DrawSlimIdentityCard(Rect rect)
        {
            DrawPanel(rect, MolcaEditorColors.Card, MolcaEditorColors.Border);

            var headerRect = new Rect(rect.x, rect.y, rect.width, 28f);
            EditorGUI.DrawRect(headerRect, MolcaEditorColors.CardHeader);
            DrawHorizontalLine(new Rect(rect.x, headerRect.yMax, rect.width, 1f), MolcaEditorColors.Border);
            DrawOutline(headerRect, MolcaEditorColors.BorderSoft);
            GUI.Label(new Rect(headerRect.x + 8f, headerRect.y + 5f, headerRect.width - 16f, 18f), "IDENTITY", _headerLabelStyle);

            var lineH = EditorGUIUtility.singleLineHeight;
            var fieldX = rect.x + 12f;
            var fieldW = rect.width - 24f;
            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = SlimLabelWidth;

            EditorGUI.PropertyField(new Rect(fieldX, rect.y + 40f, fieldW, lineH),
                _settings.FindProperty("companyName"), new GUIContent("Company Name"));
            EditorGUI.PropertyField(new Rect(fieldX, rect.y + 66f, fieldW, lineH),
                _settings.FindProperty("projectName"), new GUIContent("Project Name"));

            // Repository / Documentation URLs live on MolcaEditorSettings (ProjectSettings/, writable
            // even when Core ships as a read-only package). Surfaced here so each consuming project can
            // point the Hub's links at its own repo/docs without hand-editing the asset.
            EditorGUI.PropertyField(new Rect(fieldX, rect.y + 92f, fieldW, lineH),
                _editorSettings.FindProperty("repositoryUrl"), new GUIContent("Repository URL"));
            EditorGUI.PropertyField(new Rect(fieldX, rect.y + 118f, fieldW, lineH),
                _editorSettings.FindProperty("documentationUrl"), new GUIContent("Documentation URL"));

            EditorGUIUtility.labelWidth = oldLabelWidth;
        }

        private static string GetVersionPillText()
        {
            var editorSettings = MolcaEditorSettings.Instance;
            if (editorSettings != null && editorSettings.VersionSettings != null)
                return $"v{editorSettings.VersionSettings.GetFullVersionString()}";

            return "v0.0.0";
        }

        private static void DrawVersionPill(Rect rect, string text)
        {
            DrawPanel(rect, MolcaEditorColors.Input, MolcaEditorColors.Border);
            GUI.Label(rect, text, _versionPillStyle);
        }

        private static void DrawPanel(Rect rect, Color background, Color border)
        {
            EditorGUI.DrawRect(rect, border);
            EditorGUI.DrawRect(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f), background);
        }

        private static bool DrawPrimaryHubButton(Rect rect, string label)
        {
            var evt = Event.current;
            var id = GUIUtility.GetControlID(FocusType.Passive, rect);
            var hover = rect.Contains(evt.mousePosition);
            var pressed = hover && evt.type == EventType.MouseDown && evt.button == 0;

            var fill = pressed
                ? Color.Lerp(MolcaEditorColors.Primary, MolcaEditorColors.Border, 0.2f)
                : MolcaEditorColors.Primary;
            DrawPanel(rect, fill, MolcaEditorColors.Border);
            GUI.Label(rect, label, _primaryButtonStyle);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            if (evt.type == EventType.MouseDown && hover && evt.button == 0)
            {
                GUIUtility.hotControl = id;
                evt.Use();
            }

            if (evt.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
                return hover;
            }

            return false;
        }

        private static void DrawHorizontalLine(Rect rect, Color color)
        {
            EditorGUI.DrawRect(rect, color);
        }

        private static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }
    }
}
