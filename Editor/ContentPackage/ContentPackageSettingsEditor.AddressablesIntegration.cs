using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Addressables-integrated label picker for the package detail form.
    /// Reads real labels from <see cref="AddressableAssetSettingsDefaultObject.Settings"/>,
    /// validates selected labels against the catalog, and can scan matching entries to
    /// calculate asset count and size.
    /// </summary>
    public partial class ContentPackageSettingsEditor
    {
        // ── Label / group cache ──────────────────────────────────────────────

        private List<string> _addressableLabels;
        private bool _addressableLabelsLoaded;

        // Scan results keyed by packageId: (assetCount, totalSourceBytes)
        private readonly Dictionary<string, (int count, long size)> _scanCache
            = new Dictionary<string, (int, long)>();

        private List<string> GetAddressableLabels()
        {
            if (_addressableLabelsLoaded) return _addressableLabels;
            _addressableLabelsLoaded = true;
            _addressableLabels = AddressableAssetSettingsDefaultObject.Settings?.GetLabels()
                                 ?? new List<string>();
            return _addressableLabels;
        }

        private void InvalidateLabelCache()
        {
            _addressableLabelsLoaded = false;
            _addressableLabels = null;
        }

        // ── Main draw method (replaces the simple key list) ──────────────────

        /// <summary>
        /// Draws the Addressables label picker section inside the package detail form.
        /// <paramref name="configProp"/> must be valid for the currently selected package.
        /// </summary>
        private void DrawDetailAddressablesKeys(SerializedProperty configProp,
            ContentPackageSettings.PackageConfig cfg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;

            // ── Section header ───────────────────────────────────────────────
            var keysProp = configProp.FindPropertyRelative("addressableLabels");
            int count    = keysProp.arraySize;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Addressables Labels  ({count})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("↻", GUILayout.Width(22), GUILayout.Height(16)))
            {
                InvalidateLabelCache();
                _scanCache.Remove(cfg.packageId);
            }
            EditorGUILayout.EndHorizontal();

            if (addrSettings == null)
            {
                EditorGUILayout.HelpBox(
                    "Addressables is not configured in this project.\n" +
                    "Open Window > Asset Management > Addressables > Groups to get started.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // ── Label picker button ──────────────────────────────────────────
            var availableLabels = GetAddressableLabels();
            var selectedSet     = BuildSelectedSet(keysProp);

            if (availableLabels.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No labels defined in Addressables. Add labels in the Addressables Groups window first.",
                    MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Pick Labels…"))
                ShowLabelPickerMenu(cfg.packageId, availableLabels, selectedSet);
            if (GUILayout.Button("Pick Groups…"))
                ShowGroupPickerMenu(cfg.packageId, addrSettings, selectedSet);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // ── Selected label list ──────────────────────────────────────────
            if (count == 0)
            {
                EditorGUILayout.LabelField("No labels selected, package has no downloadable content.", _warningStyle);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var label  = keysProp.GetArrayElementAtIndex(i).stringValue;
                    bool valid = availableLabels.Contains(label);

                    EditorGUILayout.BeginHorizontal();

                    // Validity dot
                    var prevColor = GUI.color;
                    GUI.color = valid ? MolcaEditorColors.StatusOk : MolcaEditorColors.StatusError;
                    GUILayout.Label("●", GUILayout.Width(14));
                    GUI.color = prevColor;

                    EditorGUILayout.LabelField(label, valid ? EditorStyles.label : _errorStyle);

                    if (!valid)
                        EditorGUILayout.LabelField("not in catalog", _errorStyle, GUILayout.Width(90));

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("−", GUILayout.Width(22)))
                    {
                        keysProp.DeleteArrayElementAtIndex(i);
                        serializedObject.ApplyModifiedProperties();
                        _scanCache.Remove(cfg.packageId);
                        break;
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            // ── Asset scan / preview ─────────────────────────────────────────
            if (count > 0)
            {
                EditorGUILayout.Space(4);
                DrawScanPreview(cfg, selectedSet, addrSettings);
            }

            EditorGUILayout.EndVertical();
        }

        // ── Group picker ──────────────────────────────────────────────────────

        private void ShowGroupPickerMenu(string packageId, AddressableAssetSettings addrSettings,
            HashSet<string> currentLabels)
        {
            var menu = new GenericMenu();

            var groups = addrSettings.groups
                .Where(g => g != null && !g.IsDefaultGroup())
                .OrderBy(g => g.Name);

            foreach (var group in groups)
            {
                // Derive the label name from the group name (same convention Addressables uses for bundles).
                string derivedLabel = group.Name;
                bool alreadyAdded   = currentLabels.Contains(derivedLabel);
                string capturedId   = packageId;
                string capturedLabel = derivedLabel;
                var capturedGroup   = group;

                var displayName = alreadyAdded ? $"{group.Name} ✓" : group.Name;
                menu.AddItem(new GUIContent(displayName), alreadyAdded,
                    () => ApplyGroupAsLabel(capturedId, capturedLabel, capturedGroup, addrSettings, alreadyAdded));
            }

            if (!menu.GetItemCount().Equals(0))
                menu.ShowAsContext();
            else
                menu.AddDisabledItem(new GUIContent("No non-default groups found"));
        }

        /// <summary>
        /// Ensures a label matching <paramref name="labelName"/> exists in Addressables,
        /// assigns it to every entry in <paramref name="group"/>, then adds it to the
        /// package's <c>addressableLabels</c> list (or removes it if <paramref name="wasSelected"/>).
        /// </summary>
        private void ApplyGroupAsLabel(string packageId, string labelName,
            AddressableAssetGroup group, AddressableAssetSettings addrSettings, bool wasSelected)
        {
            if (wasSelected)
            {
                // Deselect: remove the label from this package only (don't strip it from group entries).
                ToggleLabel(packageId, labelName, wasSelected: true);
                return;
            }

            // Create the label in Addressables if it doesn't exist yet.
            var existingLabels = addrSettings.GetLabels();
            if (!existingLabels.Contains(labelName))
            {
                addrSettings.AddLabel(labelName);
                EditorUtility.SetDirty(addrSettings);
                InvalidateLabelCache();
                Debug.Log($"[ContentPackage] Created Addressables label '{labelName}' for group '{group.Name}'.");
            }

            // Stamp the label onto every entry in the group that doesn't already have it.
            int stamped = 0;
            foreach (var entry in group.entries)
            {
                if (entry == null) continue;
                if (!entry.labels.Contains(labelName))
                {
                    entry.SetLabel(labelName, true, postEvent: false);
                    stamped++;
                }
            }

            if (stamped > 0)
            {
                EditorUtility.SetDirty(addrSettings);
                Debug.Log($"[ContentPackage] Stamped label '{labelName}' onto {stamped} entr{(stamped == 1 ? "y" : "ies")} in group '{group.Name}'.");
            }

            // Add the label to the package config.
            ToggleLabel(packageId, labelName, wasSelected: false);
        }

        // ── Label picker (GenericMenu) ────────────────────────────────────────

        private void ShowLabelPickerMenu(string packageId, List<string> available, HashSet<string> selected)
        {
            var menu = new GenericMenu();

            foreach (var label in available.OrderBy(l => l))
            {
                bool isOn      = selected.Contains(label);
                string captured = label;
                string pkgId   = packageId;

                menu.AddItem(new GUIContent(label), isOn, () => ToggleLabel(pkgId, captured, isOn));
            }

            if (available.Count == 0)
                menu.AddDisabledItem(new GUIContent("No labels defined in Addressables"));

            menu.ShowAsContext();
        }

        // GenericMenu callback fires on next frame — re-resolve SerializedProperty by index.
        private void ToggleLabel(string packageId, string label, bool wasSelected)
        {
            serializedObject.Update();

            var settings    = target as ContentPackageSettings;
            int idx         = settings.packageConfigs.FindIndex(p => p.packageId == packageId);
            if (idx < 0) return;

            var keysProp = serializedObject
                .FindProperty("packageConfigs")
                .GetArrayElementAtIndex(idx)
                .FindPropertyRelative("addressableLabels");

            if (wasSelected)
            {
                for (int i = keysProp.arraySize - 1; i >= 0; i--)
                {
                    if (keysProp.GetArrayElementAtIndex(i).stringValue == label)
                    {
                        keysProp.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }
            else
            {
                int n = keysProp.arraySize;
                keysProp.InsertArrayElementAtIndex(n);
                keysProp.GetArrayElementAtIndex(n).stringValue = label;
            }

            serializedObject.ApplyModifiedProperties();
            _scanCache.Remove(packageId);
            Repaint();
        }

        // ── Scan preview ─────────────────────────────────────────────────────

        private void DrawScanPreview(ContentPackageSettings.PackageConfig cfg,
            HashSet<string> selectedLabels,
            AddressableAssetSettings addrSettings)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Content Preview", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Scan Assets", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                var result = ScanLabelAssets(selectedLabels, addrSettings);
                _scanCache[cfg.packageId] = result;
                Repaint();
            }

            EditorGUILayout.EndHorizontal();

            if (_scanCache.TryGetValue(cfg.packageId, out var cached))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"{cached.count} asset{(cached.count == 1 ? "" : "s")}  ·  {SizeFormatter.Format(cached.size)}  (source files — accurate bundle size written at build time)",
                    _mutedStyle);
                EditorGUILayout.EndHorizontal();

                DrawGroupBreakdown(selectedLabels, addrSettings);
            }
            else
            {
                EditorGUILayout.LabelField("Scan to preview asset count and approximate source size.", _mutedStyle);
            }
        }

        private void DrawGroupBreakdown(HashSet<string> selectedLabels, AddressableAssetSettings addrSettings)
        {
            // Show which Addressables groups contain matching entries — helps the user verify coverage.
            var groupHits = new Dictionary<string, int>();

            foreach (var group in addrSettings.groups)
            {
                if (group == null) continue;
                int hits = group.entries.Count(e => e != null && e.labels.Overlaps(selectedLabels));
                if (hits > 0)
                    groupHits[group.Name] = hits;
            }

            if (groupHits.Count == 0) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foreach (var kvp in groupHits.OrderBy(k => k.Key))
                EditorGUILayout.LabelField($"  {kvp.Key}  ({kvp.Value})", _mutedStyle);
            EditorGUILayout.EndVertical();
        }

        // ── Scan implementation ───────────────────────────────────────────────

        private static (int count, long size) ScanLabelAssets(
            HashSet<string> labels, AddressableAssetSettings addrSettings)
        {
            int  totalCount = 0;
            long totalSize  = 0;

            // Use a set of already-counted asset paths to avoid double-counting
            // entries that share labels across groups.
            var counted = new HashSet<string>();

            foreach (var group in addrSettings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    if (entry == null || !entry.labels.Overlaps(labels)) continue;

                    AccumulateEntry(entry.AssetPath, counted, ref totalCount, ref totalSize);
                }
            }

            return (totalCount, totalSize);
        }

        private static void AccumulateEntry(string assetPath, HashSet<string> counted,
            ref int count, ref long size)
        {
            if (string.IsNullOrEmpty(assetPath) || !counted.Add(assetPath)) return;

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                foreach (var guid in AssetDatabase.FindAssets("", new[] { assetPath }))
                {
                    var child = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(child))
                        AccumulateEntry(child, counted, ref count, ref size);
                }
            }
            else
            {
                var fi = new FileInfo(assetPath);
                if (!fi.Exists) return;
                size += fi.Length;
                count++;

                // Walk all asset dependencies (textures, audio clips, meshes, etc. referenced by this asset)
                // so that indirect references are included in the size estimate.
                foreach (var dep in AssetDatabase.GetDependencies(assetPath, recursive: true))
                {
                    if (dep == assetPath || AssetDatabase.IsValidFolder(dep)) continue;
                    if (!counted.Add(dep)) continue;
                    var depFi = new FileInfo(dep);
                    if (!depFi.Exists) continue;
                    size += depFi.Length;
                    // Dependencies are not counted as top-level assets, only their size is added.
                }
            }
        }

        // ── Utility ──────────────────────────────────────────────────────────

        private static HashSet<string> BuildSelectedSet(SerializedProperty keysProp)
        {
            var set = new HashSet<string>();
            for (int i = 0; i < keysProp.arraySize; i++)
                set.Add(keysProp.GetArrayElementAtIndex(i).stringValue);
            return set;
        }
    }
}
