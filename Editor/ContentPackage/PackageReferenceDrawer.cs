using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Property drawer for <see cref="PackageReference"/>.
    /// Renders a dropdown populated from all <see cref="ContentPackageSettings"/> assets
    /// in the project, with a validity dot and a select-helper button.
    /// </summary>
    [CustomPropertyDrawer(typeof(PackageReference))]
    public class PackageReferenceDrawer : PropertyDrawer
    {
        private const float SelectButtonWidth = 22f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var idProp          = property.FindPropertyRelative("_packageId");
            var displayNameProp = property.FindPropertyRelative("_cachedDisplayName");

            var settings   = FindContentPackageSettings();
            string current = idProp.stringValue;

            // Resolve display info for the current value.
            bool   idValid      = false;
            string buttonText   = "None";

            if (!string.IsNullOrEmpty(current))
            {
                var cfg = settings?.GetPackageConfig(current);
                idValid     = cfg != null;
                buttonText  = idValid
                    ? $"{(string.IsNullOrEmpty(cfg.displayName) ? current : cfg.displayName)}  ({current})"
                    : $"Missing: {current}";
            }

            // Layout: [dot] [label] [popup button] [clear button]
            Rect prefixRect = EditorGUI.PrefixLabel(position, label);
            const float dotWidth   = 14f;
            const float clearWidth = SelectButtonWidth;

            Rect dotRect    = new Rect(prefixRect.x,                               prefixRect.y, dotWidth,                            prefixRect.height);
            Rect buttonRect = new Rect(prefixRect.x + dotWidth + 2f,               prefixRect.y, prefixRect.width - dotWidth - clearWidth - 4f, prefixRect.height);
            Rect clearRect  = new Rect(prefixRect.xMax - clearWidth,               prefixRect.y, clearWidth,                          prefixRect.height);

            // Validity dot
            var prev = GUI.color;
            GUI.color = string.IsNullOrEmpty(current) ? new Color(0.55f, 0.55f, 0.55f)
                      : idValid                        ? new Color(0.1f,  0.8f,  0.1f)
                      :                                  new Color(1f,    0.25f, 0.25f);
            EditorGUI.LabelField(dotRect, "●");
            GUI.color = prev;

            // Popup button
            if (GUI.Button(buttonRect, new GUIContent(buttonText, current), EditorStyles.popup))
                ShowPickerMenu(property.serializedObject.targetObjects, property.propertyPath, settings, current);

            // Clear button
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(current)))
            {
                if (GUI.Button(clearRect, new GUIContent("✕", "Clear"), EditorStyles.miniButton))
                    ApplySelection(property.serializedObject.targetObjects, property.propertyPath, null, null);
            }

            EditorGUI.EndProperty();
        }

        // ── Picker ───────────────────────────────────────────────────────────

        private static void ShowPickerMenu(Object[] targets, string propPath,
            ContentPackageSettings settings, string currentId)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("None"), string.IsNullOrEmpty(currentId),
                () => ApplySelection(targets, propPath, null, null));
            menu.AddSeparator("");

            if (settings == null || settings.packageConfigs.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No packages defined in ContentPackageSettings"));
                menu.ShowAsContext();
                return;
            }

            // Group into visible / hidden.
            var visible = settings.packageConfigs.Where(c => c.isVisible  && !string.IsNullOrEmpty(c.packageId)).OrderBy(c => c.packageId);
            var hidden  = settings.packageConfigs.Where(c => !c.isVisible && !string.IsNullOrEmpty(c.packageId)).OrderBy(c => c.packageId);

            foreach (var cfg in visible)
            {
                string id      = cfg.packageId;
                string display = string.IsNullOrEmpty(cfg.displayName) ? id : $"{cfg.displayName}  ({id})";
                menu.AddItem(new GUIContent(display), id == currentId,
                    () => ApplySelection(targets, propPath, id, cfg.displayName));
            }

            if (hidden.Any())
            {
                menu.AddSeparator("Hidden/");
                foreach (var cfg in hidden)
                {
                    string id      = cfg.packageId;
                    string display = string.IsNullOrEmpty(cfg.displayName) ? id : $"{cfg.displayName}  ({id})";
                    menu.AddItem(new GUIContent($"Hidden/{display}"), id == currentId,
                        () => ApplySelection(targets, propPath, id, cfg.displayName));
                }
            }

            menu.ShowAsContext();
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        private static void ApplySelection(Object[] targets, string propPath, string packageId, string displayName)
        {
            if (targets == null || targets.Length == 0) return;

            var so   = new SerializedObject(targets);
            var prop = so.FindProperty(propPath);
            if (prop == null) return;

            prop.FindPropertyRelative("_packageId").stringValue          = packageId ?? "";
            prop.FindPropertyRelative("_cachedDisplayName").stringValue  = displayName ?? "";

            so.ApplyModifiedProperties();
        }

        // ── Settings lookup ──────────────────────────────────────────────────

        private static ContentPackageSettings _cachedSettings;

        private static ContentPackageSettings FindContentPackageSettings()
        {
            if (_cachedSettings != null) return _cachedSettings;

            var guids = AssetDatabase.FindAssets($"t:{nameof(ContentPackageSettings)}");
            if (guids.Length == 0) return null;

            _cachedSettings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
            return _cachedSettings;
        }
    }
}
