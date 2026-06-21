using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System;
using System.Linq;
using Molca.Editor.Hub;
using Molca.Editor.Icons;
using Molca.Editor.UI;

namespace Molca.Editor
{
    static class MolcaSettingsProvider
    {
        private const string SETTINGS_PATH = "Project/Molca";
        private const float SlimLabelWidth = 96f;
        private const float SlimProviderHeight = 255f;
        private static SerializedObject _settings;
        private static SerializedObject _editorSettings;
        private static GUIStyle _headerLabelStyle;
        private static GUIStyle _boxStyle;
        private static int _selectedModuleTab;
        private static int _buildAndVersionTab;
        private static GUIStyle _titleStyle;
        private static GUIStyle _versionPillStyle;
        private static GUIStyle _mutedLabelStyle;
        private static GUIStyle _primaryButtonStyle;
        private static GUIStyle _subtitleStyle;
        private static readonly Dictionary<UnityEngine.Object, UnityEditor.Editor> _editorCache = new Dictionary<UnityEngine.Object, UnityEditor.Editor>();
        private static UnityEditor.Editor _buildSettingsEditor;
        private static UnityEditor.Editor _versionSettingsEditor;
        private static UnityEditor.Editor _notificationSettingsEditor;
        private static UnityEditor.Editor _mcpSettingsEditor;
        private static bool _revealMcpToken;
        private static bool _mcpBuilderHooked;
        private static UnityEditor.Editor _assistantSettingsEditor;
        private static string _assistantKeyDraft;

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
                keywords = new HashSet<string>(new[] { "Molca", "Hub", "Open Molca Hub", "Project", "Identity", "Company", "Version", "Logo", "Global", "Settings", "Build", "Runtime", "Notifications", "Webhook", "Discord", "Area Picker", "Editor", "MCP", "Model Context Protocol", "Bridge", "Token" })
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
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(15, 15, 15, 15),
                    margin = new RectOffset(5, 5, 5, 5)
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
            DrawSlimIdentityCard(new Rect(content.x, content.y + 44f, width, 94f));

            var buttonRect = new Rect(content.x, content.y + 156f, width, 34f);
            if (DrawPrimaryHubButton(buttonRect, "Open Molca Hub  ↗"))
                MolcaHubWindow.Open();

            GUI.Label(
                new Rect(content.x, content.y + 198f, width, 34f),
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

            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = SlimLabelWidth;
            EditorGUI.PropertyField(new Rect(rect.x + 12f, rect.y + 40f, rect.width - 24f, EditorGUIUtility.singleLineHeight),
                _settings.FindProperty("companyName"), new GUIContent("Company Name"));
            EditorGUI.PropertyField(new Rect(rect.x + 12f, rect.y + 66f, rect.width - 24f, EditorGUIUtility.singleLineHeight),
                _settings.FindProperty("projectName"), new GUIContent("Project Name"));
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

        private static void DrawProjectSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.PropertyField(_settings.FindProperty("companyName"), new GUIContent("Company Name"));
            EditorGUILayout.PropertyField(_settings.FindProperty("projectName"), new GUIContent("Project Name"));

            // Project Logo with thumbnail
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_settings.FindProperty("projectLogo"), new GUIContent("Project Logo"));
            var logoProperty = _settings.FindProperty("projectLogo");
            if (logoProperty.objectReferenceValue != null)
            {
                var sprite = logoProperty.objectReferenceValue as Sprite;
                if (sprite != null && sprite.texture != null)
                {
                    float previewHeight = 64f;
                    float aspect = (float)sprite.texture.width / sprite.texture.height;
                    float previewWidth = previewHeight * aspect;
                    var rect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                    GUI.DrawTexture(rect, sprite.texture, ScaleMode.ScaleToFit);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            EditorGUILayout.PropertyField(_settings.FindProperty("projectId"), new GUIContent("Project ID / Code"));

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_editorSettings.FindProperty("repositoryUrl"), new GUIContent("Repository URL"));
            var repoUrl = _editorSettings.FindProperty("repositoryUrl").stringValue;
            if (!string.IsNullOrEmpty(repoUrl))
            {
                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    Application.OpenURL(repoUrl);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_editorSettings.FindProperty("documentationUrl"), new GUIContent("Documentation URL"));
            var docUrl = _editorSettings.FindProperty("documentationUrl").stringValue;
            if (!string.IsNullOrEmpty(docUrl))
            {
                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    Application.OpenURL(docUrl);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private static void DrawBuildAndVersionSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            _buildAndVersionTab = GUILayout.Toolbar(_buildAndVersionTab, new[] { "Version", "Build" });
            EditorGUILayout.Space(8);

            if (_buildAndVersionTab == 0)
            {
                var versionSettingsProperty = _editorSettings.FindProperty("versionSettings");
                EditorGUILayout.PropertyField(versionSettingsProperty, new GUIContent("Version Settings"));

                if (versionSettingsProperty.objectReferenceValue != null)
                {
                    if (_versionSettingsEditor == null || _versionSettingsEditor.target != versionSettingsProperty.objectReferenceValue)
                    {
                        if (_versionSettingsEditor != null) UnityEngine.Object.DestroyImmediate(_versionSettingsEditor);
                        _versionSettingsEditor = UnityEditor.Editor.CreateEditor(versionSettingsProperty.objectReferenceValue);
                    }
                    _versionSettingsEditor.OnInspectorGUI();
                }
                else
                {
                    if (_versionSettingsEditor != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_versionSettingsEditor);
                        _versionSettingsEditor = null;
                    }
                }
            }
            else
            {
                var buildSettingsProperty = _editorSettings.FindProperty("buildSettings");
                EditorGUILayout.PropertyField(buildSettingsProperty, new GUIContent("Build Settings"));

                if (buildSettingsProperty.objectReferenceValue != null)
                {
                    if (_buildSettingsEditor == null || _buildSettingsEditor.target != buildSettingsProperty.objectReferenceValue)
                    {
                        if (_buildSettingsEditor != null) UnityEngine.Object.DestroyImmediate(_buildSettingsEditor);
                        _buildSettingsEditor = UnityEditor.Editor.CreateEditor(buildSettingsProperty.objectReferenceValue);
                    }
                    _buildSettingsEditor.OnInspectorGUI();
                }
                else
                {
                    if (_buildSettingsEditor != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_buildSettingsEditor);
                        _buildSettingsEditor = null;
                    }
                }
            }

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Sync to Player Settings", GUILayout.Height(30)))
            {
                SyncPlayerSettingsFromMolca();
                MolcaEditorPrefs.SetString("Molca.LastSyncTime", DateTime.Now.ToString("g"));
            }

            string lastSyncTime = MolcaEditorPrefs.GetString("Molca.LastSyncTime", "Never");
            EditorGUILayout.LabelField($"Last Sync: {lastSyncTime}", EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private static void DrawNotificationsSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            var notificationSettingsProperty = _editorSettings.FindProperty("notificationSettings");

            if (notificationSettingsProperty.objectReferenceValue != null)
            {
                EditorGUILayout.BeginHorizontal();
                var newRef = EditorGUILayout.ObjectField("Notification Settings", notificationSettingsProperty.objectReferenceValue, typeof(Molca.Settings.NotificationSettings), false);
                if (newRef != notificationSettingsProperty.objectReferenceValue)
                {
                    notificationSettingsProperty.objectReferenceValue = newRef;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                }
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    Selection.activeObject = notificationSettingsProperty.objectReferenceValue;
                    EditorGUIUtility.PingObject(notificationSettingsProperty.objectReferenceValue);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Show custom editor content
                if (_notificationSettingsEditor == null || _notificationSettingsEditor.target != notificationSettingsProperty.objectReferenceValue)
                {
                    if (_notificationSettingsEditor != null) UnityEngine.Object.DestroyImmediate(_notificationSettingsEditor);
                    _notificationSettingsEditor = UnityEditor.Editor.CreateEditor(notificationSettingsProperty.objectReferenceValue);
                }
                _notificationSettingsEditor.OnInspectorGUI();
            }
            else
            {
                if (_notificationSettingsEditor != null)
                {
                    UnityEngine.Object.DestroyImmediate(_notificationSettingsEditor);
                    _notificationSettingsEditor = null;
                }

                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox(
                    "No Notification Settings asset assigned.\n\n" +
                    "Assign an existing asset or create a new one to configure build notifications, light baking notifications, " +
                    "and custom notification providers for Discord, Slack, or other services.",
                    MessageType.Info);

                var assignedRef = EditorGUILayout.ObjectField("Notification Settings", notificationSettingsProperty.objectReferenceValue, typeof(Molca.Settings.NotificationSettings), false);
                if (assignedRef != null)
                {
                    notificationSettingsProperty.objectReferenceValue = assignedRef;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                }

                if (GUILayout.Button("Create Notification Settings", GUILayout.Height(30)))
                {
                    var newSettings = Molca.Settings.NotificationSettings.GetOrCreateSettings();
                    notificationSettingsProperty.objectReferenceValue = newSettings;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                    EditorUtility.DisplayDialog("Notification Settings Created",
                        "NotificationSettings asset has been created and assigned. " +
                        "You can now add notification providers to configure build and baking notifications.", "OK");
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawRuntimeSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.PropertyField(_settings.FindProperty("runtimeManager"), true);

            EditorGUILayout.Space(5);

            var bootstrapExtensionsProperty = _settings.FindProperty("bootstrapExtensions");
            EditorGUILayout.PropertyField(bootstrapExtensionsProperty, new GUIContent("Bootstrap Extensions", "Optional extensions invoked during bootstrap, after RuntimeManager instantiation and before GlobalSettings initialization."), true);

            EditorGUILayout.Space(5);

            var globalSettingsProperty = _settings.FindProperty("globalSettings");
            EditorGUILayout.PropertyField(globalSettingsProperty, new GUIContent("Global Settings"));

            if (globalSettingsProperty.objectReferenceValue != null)
            {
                var globalSettingsSO = new SerializedObject(globalSettingsProperty.objectReferenceValue);
                globalSettingsSO.Update();

                var modulesProperty = globalSettingsSO.FindProperty("modules");

                var currentModules = new HashSet<UnityEngine.Object>();
                var moduleNames = new List<string>();
                if (modulesProperty.isArray)
                {
                    for (int i = 0; i < modulesProperty.arraySize; i++)
                    {
                        var module = modulesProperty.GetArrayElementAtIndex(i).objectReferenceValue;
                        moduleNames.Add(module != null ? module.name : $"Element {i}");
                        if (module != null)
                        {
                            currentModules.Add(module);
                        }
                    }
                }
                var editorsToRemove = _editorCache.Keys.Where(k => !currentModules.Contains(k)).ToList();
                foreach (var key in editorsToRemove)
                {
                    UnityEngine.Object.DestroyImmediate(_editorCache[key]);
                    _editorCache.Remove(key);
                }

                EditorGUILayout.Space();

                if (modulesProperty.isArray && modulesProperty.arraySize > 0)
                {
                    _selectedModuleTab = GUILayout.SelectionGrid(_selectedModuleTab, moduleNames.ToArray(), 4);
                    EditorGUILayout.Space();

                    if (_selectedModuleTab >= modulesProperty.arraySize)
                    {
                        _selectedModuleTab = modulesProperty.arraySize - 1;
                    }

                    if (_selectedModuleTab >= 0)
                    {
                        var selectedModule = modulesProperty.GetArrayElementAtIndex(_selectedModuleTab).objectReferenceValue;
                        if (selectedModule != null)
                        {
                            if (!_editorCache.TryGetValue(selectedModule, out var editor) || editor == null)
                            {
                                editor = UnityEditor.Editor.CreateEditor(selectedModule);
                                _editorCache[selectedModule] = editor;
                            }
                            editor.OnInspectorGUI();
                        }
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(modulesProperty, true);
                }

                globalSettingsSO.ApplyModifiedProperties();
            }
            else
            {
                foreach (var editor in _editorCache.Values)
                {
                    UnityEngine.Object.DestroyImmediate(editor);
                }
                _editorCache.Clear();
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawEditorSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Area Picker", _headerLabelStyle);
            var areaPickerTooltip = new GUIContent("?", "Lists GameObjects whose position is inside the proximity sphere around the click point. Works for any GameObject. Optionally filter by component type.");
            GUILayout.Button(areaPickerTooltip, EditorStyles.miniButton, GUILayout.Width(20), GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();

            bool enabled = AreaPicker.GetEnabled();
            bool newEnabled = EditorGUILayout.Toggle(new GUIContent("Enable Area Picker"), enabled);
            if (newEnabled != enabled)
                AreaPicker.SetEnabled(newEnabled);

            float radius = AreaPicker.GetProximityRadius();
            float newRadius = EditorGUILayout.FloatField(new GUIContent("Proximity Radius", "World-space radius of the sphere around the click point. GameObjects whose transform.position is inside this sphere are listed."), radius);
            if (newRadius != radius)
                AreaPicker.SetProximityRadius(newRadius);

            int modifierValue = (int)AreaPicker.GetModifier();
            var modifierOptions = new[] { new GUIContent("Alt"), new GUIContent("Control"), new GUIContent("Shift") };
            var modifierValues = new[] { (int)EventModifiers.Alt, (int)EventModifiers.Control, (int)EventModifiers.Shift };
            int newModifierValue = EditorGUILayout.IntPopup(new GUIContent("Modifier Key", "Hold this key and click in the Scene view to open the Area Picker menu."), modifierValue, modifierOptions, modifierValues);
            if (newModifierValue != modifierValue)
                AreaPicker.SetModifier((EventModifiers)newModifierValue);

            string filterType = AreaPicker.GetFilterTypeName();
            string newFilterType = EditorGUILayout.TextField(new GUIContent("Filter by component type", "Optional. Only list GameObjects that have this component (e.g. UnityEngine.MeshRenderer, MyNamespace.MyScript). Leave empty for all."), filterType);
            if (newFilterType != filterType)
                AreaPicker.SetFilterTypeName(newFilterType);

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);
            DrawNotificationsSection();
        }

        private static void DrawMcpSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.LabelField("Molca MCP Bridge", _headerLabelStyle);
            EditorGUILayout.HelpBox(
                "Exposes Molca tooling to MCP clients (Claude Code, Cursor) over an authenticated " +
                "loopback channel. The TypeScript proxy (shipped in the package; build it below) connects to this bridge.",
                MessageType.None);

            var mcpSettingsProperty = _editorSettings.FindProperty("mcpSettings");
            var settings = mcpSettingsProperty.objectReferenceValue as Mcp.McpSettings;

            EditorGUILayout.BeginHorizontal();
            var newRef = EditorGUILayout.ObjectField("MCP Settings", settings, typeof(Mcp.McpSettings), false);
            if (newRef != mcpSettingsProperty.objectReferenceValue)
            {
                mcpSettingsProperty.objectReferenceValue = newRef;
                _editorSettings.ApplyModifiedProperties();
                MolcaEditorSettings.Instance.Save();
                settings = newRef as Mcp.McpSettings;
            }
            if (settings != null && GUILayout.Button("Select", GUILayout.Width(60)))
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }
            EditorGUILayout.EndHorizontal();

            if (settings == null)
            {
                if (_mcpSettingsEditor != null)
                {
                    UnityEngine.Object.DestroyImmediate(_mcpSettingsEditor);
                    _mcpSettingsEditor = null;
                }

                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox(
                    "No MCP Settings asset assigned. Create one to enable the bridge and configure " +
                    "tool providers.", MessageType.Info);

                if (GUILayout.Button("Create MCP Settings", GUILayout.Height(30)))
                {
                    var created = Mcp.McpSettings.GetOrCreateSettings();
                    mcpSettingsProperty.objectReferenceValue = created;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                }

                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(8);

            // Connection: enable + port. Changes restart the listener.
            var settingsSO = new SerializedObject(settings);
            settingsSO.Update();

            bool wasEnabled = settings.Enabled;
            int wasPort = settings.Port;

            EditorGUILayout.PropertyField(settingsSO.FindProperty("enabled"),
                new GUIContent("Enable Bridge", "Start the loopback listener when the editor loads."));
            EditorGUILayout.PropertyField(settingsSO.FindProperty("port"),
                new GUIContent("Port", "Loopback TCP port (127.0.0.1 only)."));

            if (settingsSO.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
                if (settings.Enabled != wasEnabled || settings.Port != wasPort)
                    Mcp.McpServerController.Restart();
            }

            // Server status line.
            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            bool running = Mcp.McpServerController.IsRunning;
            var statusColor = running ? MolcaEditorColors.StatusOk : MolcaEditorColors.StatusIdle;
            var statusStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = statusColor } };
            EditorGUILayout.LabelField(running ? $"● Running on 127.0.0.1:{Mcp.McpServerController.Port}" : "○ Stopped", statusStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(running ? "Stop" : "Start", GUILayout.Width(70)))
            {
                // Drive the persisted Enabled flag rather than calling Start()/Stop() directly, so the
                // button, the "Enable Bridge" checkbox, and the listener never disagree — and a manual
                // start survives the next domain reload (Restart → StartIfEnabled respects the flag).
                settings.Enabled = !running;
                EditorUtility.SetDirty(settings);
                settingsSO.Update();
                Mcp.McpServerController.Restart();
            }
            if (GUILayout.Button("Restart", GUILayout.Width(70)))
                Mcp.McpServerController.Restart();
            EditorGUILayout.EndHorizontal();

            // Auth token: a secret, stored in project-scoped EditorPrefs (never on the asset).
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Auth Token", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            string token = Mcp.McpAuth.Token;
            string shown = _revealMcpToken ? token : new string('•', Mathf.Min(token.Length, 32));
            EditorGUILayout.SelectableLabel(shown, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            _revealMcpToken = GUILayout.Toggle(_revealMcpToken, _revealMcpToken ? "Hide" : "Reveal", EditorStyles.miniButton, GUILayout.Width(60));
            if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(50)))
                EditorGUIUtility.systemCopyBuffer = token;
            if (GUILayout.Button("Regenerate", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                if (EditorUtility.DisplayDialog("Regenerate MCP Token",
                    "Regenerating invalidates the current token. Any connected MCP client must be " +
                    "reconfigured with the new token.", "Regenerate", "Cancel"))
                {
                    Mcp.McpAuth.Regenerate();
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                "Pass this token to the MCP server via the MOLCA_MCP_TOKEN env var (see the molca-mcp README).",
                EditorStyles.wordWrappedMiniLabel);

            // TypeScript proxy build.
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("TypeScript Proxy", EditorStyles.miniBoldLabel);

            // Repaint the settings window live while the build streams output.
            if (!_mcpBuilderHooked)
            {
                Mcp.McpProxyBuilder.Changed += () => UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                _mcpBuilderHooked = true;
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(Mcp.McpProxyBuilder.IsBuilding))
            {
                if (GUILayout.Button(Mcp.McpProxyBuilder.IsBuilt ? "Rebuild Proxy (npm install + build)" : "Set Up Proxy (npm install + build)", GUILayout.Height(26)))
                    Mcp.McpProxyBuilder.Build();
            }
            EditorGUILayout.EndHorizontal();

            string buildStatus = Mcp.McpProxyBuilder.IsBuilding
                ? Mcp.McpProxyBuilder.Status
                : Mcp.McpProxyBuilder.IsBuilt ? "dist/index.js present." : "Proxy not built yet.";
            EditorGUILayout.LabelField(buildStatus, EditorStyles.centeredGreyMiniLabel);

            var buildLog = Mcp.McpProxyBuilder.LogText;
            if (!string.IsNullOrEmpty(buildLog))
            {
                EditorGUILayout.SelectableLabel(buildLog, EditorStyles.helpBox,
                    GUILayout.Height(110), GUILayout.ExpandWidth(true));
            }

            // Embedded provider list + status dots.
            EditorGUILayout.Space(10);
            if (_mcpSettingsEditor == null || _mcpSettingsEditor.target != settings)
            {
                if (_mcpSettingsEditor != null) UnityEngine.Object.DestroyImmediate(_mcpSettingsEditor);
                _mcpSettingsEditor = UnityEditor.Editor.CreateEditor(settings);
            }
            _mcpSettingsEditor.OnInspectorGUI();

            EditorGUILayout.EndVertical();
        }

        private static void DrawAssistantSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.LabelField("In-Editor Assistant", _headerLabelStyle);
            EditorGUILayout.HelpBox(
                "A chat assistant inside the editor that answers questions using the same read-only MCP " +
                "tools, for team members without an IDE. No setup beyond an API key.", MessageType.None);

            var assistantProperty = _editorSettings.FindProperty("assistantSettings");
            var settings = assistantProperty.objectReferenceValue as Mcp.Assistant.AssistantSettings;

            EditorGUILayout.BeginHorizontal();
            var newRef = EditorGUILayout.ObjectField("Assistant Settings", settings, typeof(Mcp.Assistant.AssistantSettings), false);
            if (newRef != assistantProperty.objectReferenceValue)
            {
                assistantProperty.objectReferenceValue = newRef;
                _editorSettings.ApplyModifiedProperties();
                MolcaEditorSettings.Instance.Save();
                settings = newRef as Mcp.Assistant.AssistantSettings;
            }
            EditorGUILayout.EndHorizontal();

            if (settings == null)
            {
                if (_assistantSettingsEditor != null)
                {
                    UnityEngine.Object.DestroyImmediate(_assistantSettingsEditor);
                    _assistantSettingsEditor = null;
                }
                EditorGUILayout.Space(8);
                if (GUILayout.Button("Create Assistant Settings", GUILayout.Height(30)))
                {
                    var created = Mcp.Assistant.AssistantSettings.GetOrCreateSettings();
                    assistantProperty.objectReferenceValue = created;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                }
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(6);
            var so = new SerializedObject(settings);
            so.Update();
            EditorGUILayout.PropertyField(so.FindProperty("enabled"), new GUIContent("Enable Assistant"));
            EditorGUILayout.PropertyField(so.FindProperty("provider"), new GUIContent("Provider"));
            EditorGUILayout.PropertyField(so.FindProperty("model"), new GUIContent("Model", "Leave empty for the provider default."));

            // OpenAI-compatible base URL (also used for DeepSeek and other compatible vendors).
            if (so.FindProperty("provider").enumValueIndex == (int)Mcp.Assistant.LlmProviderKind.OpenAI)
            {
                EditorGUILayout.PropertyField(so.FindProperty("baseUrl"),
                    new GUIContent("Base URL", "Leave empty for OpenAI. For DeepSeek use https://api.deepseek.com (model: deepseek-chat)."));
                EditorGUILayout.LabelField("OpenAI-compatible. DeepSeek: base https://api.deepseek.com, model deepseek-chat.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.PropertyField(so.FindProperty("maxTokens"), new GUIContent("Max Tokens"));
            EditorGUILayout.PropertyField(so.FindProperty("maxToolRounds"),
                new GUIContent("Max Tool Rounds", "Maximum model→tool→model rounds per turn (clamped 1–100). Multi-step authoring needs more headroom than read-only queries."));
            EditorGUILayout.PropertyField(so.FindProperty("streamResponses"),
                new GUIContent("Stream Responses", "Stream assistant text incrementally (SSE) where the provider supports it."));
            so.ApplyModifiedProperties();

            // API key — a secret; stored in project-scoped EditorPrefs / env var, never on the asset.
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("API Key", EditorStyles.miniBoldLabel);
            var provider = settings.Provider;
            if (Mcp.Assistant.AssistantApiAuth.IsFromEnv(provider))
            {
                EditorGUILayout.HelpBox($"Using the {Mcp.Assistant.AssistantApiAuth.EnvVarFor(provider)} environment variable.", MessageType.Info);
            }
            else
            {
                _assistantKeyDraft ??= string.Empty;
                EditorGUILayout.BeginHorizontal();
                _assistantKeyDraft = EditorGUILayout.PasswordField(
                    new GUIContent("Key", "Stored in project-scoped EditorPrefs, never committed."), _assistantKeyDraft);
                if (GUILayout.Button("Save", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Mcp.Assistant.AssistantApiAuth.SetKey(provider, _assistantKeyDraft);
                    _assistantKeyDraft = string.Empty;
                    GUI.FocusControl(null);
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Mcp.Assistant.AssistantApiAuth.SetKey(provider, string.Empty);
                    _assistantKeyDraft = string.Empty;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(
                    Mcp.Assistant.AssistantApiAuth.HasKey(provider) ? "A key is stored for this provider." : "No key stored.",
                    EditorStyles.miniLabel);
            }

            // Status dot + privacy note.
            EditorGUILayout.Space(6);
            var status = settings.GetStatus(out var statusMessage);
            var color = status == Mcp.Assistant.AssistantConfigStatus.Configured ? MolcaEditorColors.StatusOk
                      : status == Mcp.Assistant.AssistantConfigStatus.Misconfigured ? MolcaEditorColors.StatusWarn
                      : MolcaEditorColors.StatusIdle;
            var iconStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } };
            EditorGUILayout.LabelField($"{(status == Mcp.Assistant.AssistantConfigStatus.Disabled ? "○" : "●")} {statusMessage}", iconStyle);

            EditorGUILayout.HelpBox(
                "Privacy: questions and the read-only tool results they pull (subsystem names, Doctor " +
                "findings, build info, etc.) are sent to the configured LLM provider.", MessageType.Warning);

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Open Assistant Chat", GUILayout.Height(28)))
                Mcp.Assistant.AssistantChatWindow.Open();

            EditorGUILayout.EndVertical();
        }

        private static void SyncPlayerSettingsFromMolca()
        {
            var projectSettings = MolcaProjectSettings.Instance;
            var editorSettings = MolcaEditorSettings.Instance;

            if (projectSettings == null)
            {
                Debug.LogError("MolcaProjectSettings asset not found!");
                return;
            }

            if (editorSettings == null)
            {
                Debug.LogError("MolcaEditorSettings asset not found!");
                return;
            }

            if (editorSettings.BuildSettings != null)
            {
                PlayerSettings.companyName = projectSettings.CompanyName;
                PlayerSettings.productName = projectSettings.ProjectName;
            }

            if (editorSettings.VersionSettings != null)
            {
                PlayerSettings.bundleVersion = editorSettings.VersionSettings.GetBundleVersionString();
            }

            Debug.Log("PlayerSettings synced from Molca Settings.");
            EditorUtility.DisplayDialog("Sync Complete", "PlayerSettings have been successfully updated from your Molca Settings.", "OK");
        }
    }
}
