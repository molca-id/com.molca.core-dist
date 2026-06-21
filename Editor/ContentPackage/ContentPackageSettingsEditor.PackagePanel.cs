using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Left package list + right package detail form.
    /// </summary>
    public partial class ContentPackageSettingsEditor
    {
        // ── Left panel ───────────────────────────────────────────────────────

        private void DrawLeftPanel(ContentPackageSettings settings)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(LeftPanelWidth));

            // Search
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var newFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField);
            if (newFilter != _searchFilter)
            {
                _searchFilter      = newFilter;
                _searchFilterLower = newFilter.ToLowerInvariant();
            }
            if (!string.IsNullOrEmpty(_searchFilter) && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                _searchFilter      = "";
                _searchFilterLower = "";
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Package cards
            var filtered = string.IsNullOrEmpty(_searchFilterLower)
                ? settings.packageConfigs
                : settings.packageConfigs.FindAll(p =>
                    (p.packageId  ?? "").IndexOf(_searchFilterLower, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (p.displayName ?? "").IndexOf(_searchFilterLower, StringComparison.OrdinalIgnoreCase) >= 0);

            if (filtered.Count == 0 && settings.packageConfigs.Count == 0)
            {
                EditorGUILayout.LabelField("No packages yet.", _mutedStyle);
            }
            else if (filtered.Count == 0)
            {
                EditorGUILayout.LabelField("No matches.", _mutedStyle);
            }
            else
            {
                foreach (var cfg in filtered)
                    DrawPackageCard(cfg);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ New Package", GUILayout.Width(LeftPanelWidth - 4)))
                AddNewPackage(settings);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawPackageCard(ContentPackageSettings.PackageConfig cfg)
        {
            bool isSelected = cfg.packageId == _selectedPackageId;

            // Tint selected card
            var prevBg = GUI.backgroundColor;
            if (isSelected) { var sel = MolcaEditorColors.RowSelected; sel.a = 0.35f; GUI.backgroundColor = sel; }

            EditorGUILayout.BeginVertical(_cardStyle);
            GUI.backgroundColor = prevBg;

            EditorGUILayout.BeginHorizontal();

            // Config health dot (left of name)
            var health = GetConfigHealth(cfg);
            var prevColor = GUI.color;
            GUI.color = health == ConfigHealth.Ok      ? MolcaEditorColors.StatusOk
                      : health == ConfigHealth.Warning ? MolcaEditorColors.StatusWarn
                      :                                  MolcaEditorColors.StatusError;
            GUILayout.Label("●", GUILayout.Width(14));
            GUI.color = prevColor;

            // Name + ID
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(cfg.displayName) ? cfg.packageId : cfg.displayName,
                EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(cfg.displayName) && cfg.displayName != cfg.packageId)
                EditorGUILayout.LabelField(cfg.packageId, _mutedStyle);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // Runtime status badge (play mode)
            if (_runtimeStates.TryGetValue(cfg.packageId, out var rs))
            {
                var (badge, badgeStyle) = GetStatusBadge(rs.status);
                EditorGUILayout.LabelField(badge, badgeStyle);
            }
            else if (!cfg.isVisible)
            {
                EditorGUILayout.LabelField("hidden", _mutedStyle);
            }

            EditorGUILayout.EndVertical();

            // Whole card is clickable
            var cardRect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && cardRect.Contains(Event.current.mousePosition))
            {
                var s = target as ContentPackageSettings;
                _selectedPackageId    = cfg.packageId;
                _selectedPackageIndex = s.packageConfigs.IndexOf(cfg);
                Event.current.Use();
                Repaint();
            }
        }

        // ── Right panel ──────────────────────────────────────────────────────

        private void DrawRightPanel(ContentPackageSettings settings)
        {
            // Width is computed by OnInspectorGUI; constraining it here prevents the detail form from
            // overflowing when hosted inside the Hub's IMGUIContainer (see ContentPackageSettingsEditor.cs).
            EditorGUILayout.BeginVertical(GUILayout.Width(_rightPanelWidth), GUILayout.MaxWidth(_rightPanelWidth));

            // No selection at all
            if (_selectedPackageId == null && _selectedPackageIndex < 0)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Select a package to edit.", _mutedStyle);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            // Prefer ID lookup; fall back to index when ID was cleared mid-edit
            int idx = string.IsNullOrEmpty(_selectedPackageId)
                ? _selectedPackageIndex
                : settings.packageConfigs.FindIndex(p => p.packageId == _selectedPackageId);

            if (idx < 0 || idx >= settings.packageConfigs.Count)
            {
                _selectedPackageId    = null;
                _selectedPackageIndex = -1;
                EditorGUILayout.EndVertical();
                return;
            }

            var configsProp = serializedObject.FindProperty("packageConfigs");
            if (idx >= configsProp.arraySize)
            {
                // SerializedObject is still one frame behind; wait for the next repaint.
                EditorGUILayout.EndVertical();
                return;
            }
            var configProp = configsProp.GetArrayElementAtIndex(idx);
            var cfg         = settings.packageConfigs[idx];

            // Detail header with delete button
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(cfg.displayName) ? cfg.packageId : cfg.displayName,
                _sectionLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete", GUILayout.Width(60)))
            {
                if (EditorUtility.DisplayDialog("Delete Package",
                    $"Remove '{cfg.displayName ?? cfg.packageId}' from configuration?", "Delete", "Cancel"))
                {
                    configsProp.DeleteArrayElementAtIndex(idx);
                    _selectedPackageId    = null;
                    _selectedPackageIndex = -1;
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            DrawDetailIdentity(configProp, cfg);
            EditorGUILayout.Space(4);
            DrawDetailAddressablesKeys(configProp, cfg);
            EditorGUILayout.Space(4);
            DrawDetailDependencies(configProp, cfg);
            EditorGUILayout.Space(4);
            DrawDetailMetadata(configProp, cfg);
            EditorGUILayout.Space(4);
            DrawDetailFlags(configProp);

            // Runtime status (play mode)
            if (_runtimeStates.TryGetValue(cfg.packageId, out var runtimeState))
            {
                EditorGUILayout.Space(6);
                DrawDetailRuntimeStatus(runtimeState);
            }

            EditorGUILayout.EndVertical();
        }

        // ── Detail sections ──────────────────────────────────────────────────

        private void DrawDetailIdentity(SerializedProperty configProp, ContentPackageSettings.PackageConfig cfg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);

            var idProp = configProp.FindPropertyRelative("packageId");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(idProp, new GUIContent("Package ID"));
            if (EditorGUI.EndChangeCheck())
                _selectedPackageId = idProp.stringValue;

            EditorGUILayout.PropertyField(
                configProp.FindPropertyRelative("displayName"),
                new GUIContent("Display Name"));

            // description uses [TextArea(2,4)] attribute — PropertyField renders it as a text area.
            EditorGUILayout.PropertyField(
                configProp.FindPropertyRelative("metadata").FindPropertyRelative("description"),
                new GUIContent("Description"));

            EditorGUILayout.EndVertical();
        }

        private void DrawDetailDependencies(SerializedProperty configProp, ContentPackageSettings.PackageConfig cfg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var depsProp = configProp.FindPropertyRelative("dependencies");
            int count = depsProp.arraySize;

            EditorGUILayout.LabelField($"Dependencies  ({count})", EditorStyles.boldLabel);

            var settings = target as ContentPackageSettings;

            for (int i = 0; i < count; i++)
            {
                var dep      = depsProp.GetArrayElementAtIndex(i);
                var idProp   = dep.FindPropertyRelative("packageId");
                string depId = idProp.stringValue;

                EditorGUILayout.BeginHorizontal();

                // Package ID picker button
                var btnLabel = string.IsNullOrEmpty(depId) ? "— pick package —" : depId;
                if (GUILayout.Button(btnLabel, EditorStyles.popup))
                    ShowDependencyPickerMenu(cfg.packageId, i, settings);

                if (GUILayout.Button("−", GUILayout.Width(22)))
                {
                    depsProp.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Dependency"))
            {
                depsProp.InsertArrayElementAtIndex(count);
                var newDep = depsProp.GetArrayElementAtIndex(count);
                newDep.FindPropertyRelative("packageId").stringValue  = "";
            }

            EditorGUILayout.EndVertical();
        }

        private void ShowDependencyPickerMenu(string ownerPackageId, int depIndex, ContentPackageSettings settings)
        {
            var menu = new GenericMenu();
            var candidates = settings.packageConfigs
                .Where(p => p.packageId != ownerPackageId && !string.IsNullOrEmpty(p.packageId))
                .OrderBy(p => p.packageId);

            foreach (var candidate in candidates)
            {
                string captured = candidate.packageId;
                string label    = string.IsNullOrEmpty(candidate.displayName)
                    ? captured
                    : $"{candidate.displayName}  ({captured})";

                menu.AddItem(new GUIContent(label), false,
                    () => SetDependencyPackageId(ownerPackageId, depIndex, captured));
            }

            if (!menu.GetItemCount().Equals(0))
                menu.AddSeparator("");

            // Allow typing a manual ID not in this settings file
            menu.AddItem(new GUIContent("Enter ID manually…"), false,
                () => SetDependencyPackageId(ownerPackageId, depIndex, ""));

            if (settings.packageConfigs.Count(p => p.packageId != ownerPackageId) == 0)
                menu.AddDisabledItem(new GUIContent("No other packages defined"));

            menu.ShowAsContext();
        }

        // GenericMenu callback — re-resolve SerializedProperty by index.
        private void SetDependencyPackageId(string ownerPackageId, int depIndex, string newId)
        {
            serializedObject.Update();

            var s   = target as ContentPackageSettings;
            int idx = s.packageConfigs.FindIndex(p => p.packageId == ownerPackageId);
            if (idx < 0) return;

            var idProp = serializedObject
                .FindProperty("packageConfigs")
                .GetArrayElementAtIndex(idx)
                .FindPropertyRelative("dependencies")
                .GetArrayElementAtIndex(depIndex)
                .FindPropertyRelative("packageId");

            idProp.stringValue = newId;
            serializedObject.ApplyModifiedProperties();
            Repaint();
        }

        private void DrawDetailMetadata(SerializedProperty configProp, ContentPackageSettings.PackageConfig cfg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Metadata", EditorStyles.boldLabel);

            var meta = configProp.FindPropertyRelative("metadata");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(meta.FindPropertyRelative("version"), new GUIContent("Version"), GUILayout.ExpandWidth(true));
            EditorGUILayout.PropertyField(meta.FindPropertyRelative("author"), new GUIContent("Author"), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            // Tags — comma-separated string field for usability
            var tagsProp = meta.FindPropertyRelative("tags");
            var tagsJoined = string.Join(", ", Enumerable.Range(0, tagsProp.arraySize)
                .Select(i => tagsProp.GetArrayElementAtIndex(i).stringValue));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Tags", GUILayout.Width(40));
            var newTags = EditorGUILayout.TextField(tagsJoined);
            EditorGUILayout.EndHorizontal();

            if (newTags != tagsJoined)
            {
                var split = newTags.Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToArray();
                tagsProp.arraySize = split.Length;
                for (int i = 0; i < split.Length; i++)
                    tagsProp.GetArrayElementAtIndex(i).stringValue = split[i];
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDetailFlags(SerializedProperty configProp)
        {
            EditorGUILayout.BeginHorizontal();
            DrawToggleCompact(configProp.FindPropertyRelative("isVisible"), "Visible");
            DrawToggleCompact(configProp.FindPropertyRelative("isRequired"), "Required");
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDetailRuntimeStatus(PackageState rs)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            var (badge, style) = GetStatusBadge(rs.status);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Status", GUILayout.Width(60));
            EditorGUILayout.LabelField(badge, style);
            EditorGUILayout.EndHorizontal();

            if (rs.IsInstalled && !string.IsNullOrEmpty(rs.installedVersion))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Version", GUILayout.Width(60));
                EditorGUILayout.LabelField(rs.installedVersion, _mutedStyle);
                EditorGUILayout.EndHorizontal();
            }

            if (rs.IsDownloading)
            {
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(false, 14),
                    rs.downloadProgress,
                    $"{rs.downloadProgress:P0}  ({SizeFormatter.Format(rs.downloadedBytes)} / {SizeFormatter.Format(rs.totalBytes)})");
            }

            if (rs.HasError && !string.IsNullOrEmpty(rs.errorMessage))
                EditorGUILayout.HelpBox(rs.errorMessage, MessageType.Error);

            EditorGUILayout.EndVertical();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void DrawToggleCompact(SerializedProperty prop, string label)
        {
            prop.boolValue = EditorGUILayout.ToggleLeft(label, prop.boolValue, GUILayout.Width(100));
        }

        private void AddNewPackage(ContentPackageSettings settings)
        {
            // Generate a unique placeholder ID
            string baseId = "new-package";
            string id     = baseId;
            int    n      = 1;
            while (settings.GetPackageConfig(id) != null)
                id = $"{baseId}-{n++}";

            // Use SerializedProperty so the array is immediately in sync within this repaint frame.
            var configsProp = serializedObject.FindProperty("packageConfigs");
            int newIdx      = configsProp.arraySize;
            configsProp.InsertArrayElementAtIndex(newIdx);

            var p = configsProp.GetArrayElementAtIndex(newIdx);
            p.FindPropertyRelative("packageId").stringValue      = id;
            p.FindPropertyRelative("displayName").stringValue    = "";
            p.FindPropertyRelative("isVisible").boolValue        = true;
            p.FindPropertyRelative("isRequired").boolValue       = false;
            p.FindPropertyRelative("addressableLabels").arraySize = 0;
            p.FindPropertyRelative("dependencies").arraySize     = 0;

            var meta = p.FindPropertyRelative("metadata");
            meta.FindPropertyRelative("version").stringValue       = "1.0.0";
            meta.FindPropertyRelative("description").stringValue   = "";
            meta.FindPropertyRelative("author").stringValue        = "";
            meta.FindPropertyRelative("tags").arraySize            = 0;

            serializedObject.ApplyModifiedProperties();
            _selectedPackageId    = id;
            _selectedPackageIndex = newIdx;
            Repaint();
        }

        private enum ConfigHealth { Ok, Warning, Error }

        private static ConfigHealth GetConfigHealth(ContentPackageSettings.PackageConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.packageId)) return ConfigHealth.Error;
            if (cfg.addressableLabels == null || cfg.addressableLabels.Length == 0) return ConfigHealth.Warning;
            if (string.IsNullOrEmpty(cfg.displayName)) return ConfigHealth.Warning;
            return ConfigHealth.Ok;
        }

        private (string badge, GUIStyle style) GetStatusBadge(PackageStatus status)
        {
            return status switch
            {
                PackageStatus.Installed       => ("Installed",        _successStyle),
                PackageStatus.UpdateAvailable => ("Update Available", _warningStyle),
                PackageStatus.Downloading     => ("Downloading",      _warningStyle),
                PackageStatus.Failed          => ("Failed",           _errorStyle),
                _                             => ("Available",        _mutedStyle)
            };
        }
    }
}
