using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Molca.App.UI.ContentPackage;

namespace Molca.App.Editor.ContentPackage
{
    /// <summary>
    /// Fills in the shipped ContentPackage prefab's missing rows and buttons.
    /// </summary>
    /// <remarks>
    /// Five documented features — download size, tags, changelog, Update All, and Free Up Space —
    /// existed in <see cref="ContentPackageManagerUI"/> and were never wired in the prefab, so they
    /// silently did nothing at runtime. Every access in the script is guarded with
    /// <c>if (field != null)</c>, which is what let the gap survive: an unassigned field is not an
    /// error, it is an absent feature.
    ///
    /// This builds them by <em>cloning</em> the prefab's own existing rows and buttons rather than
    /// constructing new ones from scratch. A clone inherits this project's fonts, colours, spacing,
    /// and layout components; a hand-built row inherits whatever the author happened to type, and
    /// drifts the moment the design changes.
    ///
    /// Idempotent by construction: a field that is already assigned is left alone, so running it on
    /// a prefab a designer has since adjusted changes nothing.
    /// </remarks>
    public static class ContentPackageManagerPrefabBuilder
    {
        private const string InputFieldPrefabName = "InputField (TMP)";

        [MenuItem("Molca/Content/Rebuild Content Package Manager Prefab")]
        public static void Rebuild()
        {
            string summary = Run();
            EditorUtility.DisplayDialog("Content Package Manager prefab", summary, "OK");
        }

        /// <summary>Wires every unassigned field it knows how to build. Returns what it did.</summary>
        /// <remarks>
        /// The prefab is <em>located</em>, never hardcoded. It used to ship inside this package and now
        /// belongs to the project, which may keep it anywhere — and the package cannot name a path under
        /// <c>Assets/</c>, because that path exists only in the development repository. Searching for the
        /// component is the one identifier that survives both the move and any project's own layout.
        /// </remarks>
        public static string Run()
        {
            var prefabPath = ResolveManagerPrefabPath(out var ambiguity);
            if (prefabPath == null) return ambiguity;

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return $"Could not open {prefabPath}.";

            var added = new List<string>();
            try
            {
                var ui = root.GetComponent<ContentPackageManagerUI>();
                if (ui == null) return "The prefab has no ContentPackageManagerUI component.";

                var detailPanel = Field<GameObject>(ui, "_detailPanel");
                var sizeLabel = Field<TextMeshProUGUI>(ui, "_detailSize");
                var installButton = Field<Component>(ui, "_installButton");

                // Every clone needs a template that already exists on this prefab. Without one there
                // is nothing to inherit styling from, and guessing is what this method exists to avoid.
                if (sizeLabel == null || installButton == null)
                    return "Cannot build: the prefab is missing the Size row or the Install button to clone from.";

                AddValueRow(ui, "_detailDownloadSize", "Download", sizeLabel, added);
                AddValueRow(ui, "_detailTags", "Tags", sizeLabel, added);
                AddChangelogRow(ui, sizeLabel, added);

                AddButton(ui, "_updateAllButton", "_updateAllButtonLabel", "Update All", installButton, added);
                AddButton(ui, "_freeUpSpaceButton", "_freeUpSpaceButtonLabel", "Free Up Space", installButton, added);

                AddReleaseProgressRow(ui, added);
                AddListToolbar(ui, installButton, added);

                if (added.Count > 0)
                {
                    EditorUtility.SetDirty(ui);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    AssetDatabase.SaveAssets();
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return added.Count == 0
                ? "Every field this tool builds is already wired. Nothing changed."
                : "Wired: " + string.Join(", ", added);
        }

        /// <summary>Finds the project's ContentPackage manager prefab by the component it carries.</summary>
        /// <param name="problem">Set to a message for the user when the path could not be resolved.</param>
        /// <returns>The asset path, or <c>null</c> with <paramref name="problem"/> explaining why.</returns>
        /// <remarks>
        /// <para>Identified by <see cref="ContentPackageManagerUI"/> rather than by name or path: a project
        /// is free to rename or relocate its own prefab, and a script reference is the thing that cannot
        /// be renamed without also breaking the prefab.</para>
        /// <para>Several matches are reported rather than guessed. This tool overwrites the prefab it
        /// picks, so choosing the wrong one silently edits content the author did not mean to touch.</para>
        /// </remarks>
        private static string ResolveManagerPrefabPath(out string problem)
        {
            var matches = AssetDatabase.FindAssets("t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => AssetDatabase.LoadAssetAtPath<GameObject>(path)
                                   ?.GetComponent<ContentPackageManagerUI>() != null)
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .ToArray();

            if (matches.Length == 0)
            {
                problem = "No prefab in this project carries a ContentPackageManagerUI component. "
                          + "Import the Content Package UI before running this.";
                return null;
            }

            if (matches.Length > 1)
            {
                problem = "Several prefabs carry a ContentPackageManagerUI component, and this tool "
                          + "overwrites the one it picks. Resolve the ambiguity first:\n  "
                          + string.Join("\n  ", matches);
                return null;
            }

            problem = null;
            return matches[0];
        }

        /// <summary>Finds the input-field prefab the search box is cloned from.</summary>
        /// <returns>The prefab, or <c>null</c> when the project has none — in which case the search box
        /// is skipped rather than built from scratch.</returns>
        /// <remarks>
        /// Located by name, because an input field carries only stock Unity components and there is no
        /// script to identify it by. A miss is not an error: the point of cloning is to inherit the
        /// project's styling, and with nothing to inherit from, building one anyway would invent a look.
        /// </remarks>
        private static GameObject ResolveInputFieldPrefab()
        {
            return AssetDatabase.FindAssets($"\"{InputFieldPrefabName}\" t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => System.IO.Path.GetFileNameWithoutExtension(path) == InputFieldPrefabName)
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(prefab => prefab != null && prefab.GetComponent<TMP_InputField>() != null);
        }

        /// <summary>Clones the Size row to make another label-and-value row, and assigns its value field.</summary>
        private static void AddValueRow(
            ContentPackageManagerUI ui, string fieldName, string label,
            TextMeshProUGUI template, List<string> added)
        {
            if (Field<TextMeshProUGUI>(ui, fieldName) != null) return;

            var row = CloneRow(template, label, out var value);
            if (row == null) return;

            row.name = label + " Row";
            SetField(ui, fieldName, value);
            added.Add(fieldName);
        }

        /// <summary>
        /// Clones a row for the changelog and records the row object itself.
        /// </summary>
        /// <remarks>
        /// The changelog needs both the label and its containing row, because the script hides the
        /// whole row when a release carries no notes. Wiring only the label would leave an empty
        /// "Changelog:" heading on every package that has none.
        /// </remarks>
        private static void AddChangelogRow(
            ContentPackageManagerUI ui, TextMeshProUGUI template, List<string> added)
        {
            bool needsLabel = Field<TextMeshProUGUI>(ui, "_detailChangelog") == null;
            bool needsRow = Field<GameObject>(ui, "_detailChangelogRow") == null;
            if (!needsLabel && !needsRow) return;

            var row = CloneRow(template, "Changelog", out var value);
            if (row == null) return;

            row.name = "Changelog Row";
            if (value != null)
            {
                // Release notes are prose, not a single value.
                value.enableWordWrapping = true;
                value.overflowMode = TextOverflowModes.Overflow;
            }

            if (needsLabel) { SetField(ui, "_detailChangelog", value); added.Add("_detailChangelog"); }
            if (needsRow) { SetField(ui, "_detailChangelogRow", row); added.Add("_detailChangelogRow"); }
        }

        /// <summary>
        /// Duplicates the row containing <paramref name="template"/>, retitles its label, and hands
        /// back the value label inside the copy.
        /// </summary>
        private static GameObject CloneRow(TextMeshProUGUI template, string label, out TextMeshProUGUI value)
        {
            value = null;

            var rowTransform = template.transform.parent;
            if (rowTransform == null) return null;

            var clone = Object.Instantiate(rowTransform.gameObject, rowTransform.parent);
            clone.transform.SetSiblingIndex(rowTransform.GetSiblingIndex() + 1);

            var labels = clone.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
            if (labels.Length == 0) return clone;

            // Convention in this prefab: the first label in a row is the caption, the last is the
            // value. A one-label row means caption and value are the same object.
            labels[0].text = label;
            value = labels[labels.Length - 1];
            value.text = "";
            clone.SetActive(true);
            return clone;
        }

        /// <summary>
        /// Clones the detail panel's progress row into the header for release-level progress.
        /// </summary>
        /// <remarks>
        /// The header, not the detail panel. Adopting a release is app-wide work; a bar that lives
        /// beside one package's details would disappear the moment the user tapped a different row,
        /// which during a multi-gigabyte download reads as "it stopped."
        ///
        /// Clones the existing progress row rather than assembling a slider, so the fill colour,
        /// handle, and track match the one already in the panel. Two progress bars that look
        /// different in the same window are read as two different kinds of thing.
        /// </remarks>
        private static void AddReleaseProgressRow(ContentPackageManagerUI ui, List<string> added)
        {
            bool needsRow = Field<GameObject>(ui, "_releaseProgressRow") == null;
            bool needsLabel = Field<TextMeshProUGUI>(ui, "_releaseProgressLabel") == null;
            bool needsSlider = Field<Slider>(ui, "_releaseProgressSlider") == null;
            if (!needsRow && !needsLabel && !needsSlider) return;

            var template = Field<GameObject>(ui, "_progressRow");
            var header = Field<TextMeshProUGUI>(ui, "_titleLabel")?.transform.parent;
            if (template == null || header == null) return;

            var clone = Object.Instantiate(template, header);
            clone.name = "Release Progress Row";
            clone.SetActive(false); // Nothing is activating when the panel opens.

            // Sits directly under the title, above the status line: it describes the whole panel's
            // state, and burying it under the per-package rows would put it out of the reading order.
            var title = Field<TextMeshProUGUI>(ui, "_titleLabel");
            clone.transform.SetSiblingIndex(title.transform.GetSiblingIndex() + 1);

            var labels = clone.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
            if (labels.Length > 0) labels[0].text = "Content";

            var value = labels.Length > 1 ? labels[labels.Length - 1] : null;
            if (value != null) value.text = "";

            var slider = clone.GetComponentInChildren<Slider>(includeInactive: true);
            if (slider != null) slider.value = 0f;

            if (needsRow) { SetField(ui, "_releaseProgressRow", clone); added.Add("_releaseProgressRow"); }
            if (needsLabel && value != null)
            {
                SetField(ui, "_releaseProgressLabel", value);
                added.Add("_releaseProgressLabel");
            }
            if (needsSlider && slider != null)
            {
                SetField(ui, "_releaseProgressSlider", slider);
                added.Add("_releaseProgressSlider");
            }
        }

        /// <summary>
        /// Builds the search / filter / sort toolbar above the package list.
        /// </summary>
        /// <remarks>
        /// Filter and sort are cycling buttons rather than dropdowns. Two reasons, and the second is
        /// the deciding one: a cloned button inherits the panel's existing styling for free, and this
        /// SDK ships to head-mounted displays, where a dropdown's popup list is a floating panel that
        /// has to be raycast against at arm's length. A button that advances through four labels
        /// needs one press and no popup.
        ///
        /// The toolbar root is assembled here rather than cloned because the prefab has no horizontal
        /// row to copy — but its children are all clones or project prefabs, so nothing about the
        /// controls themselves is invented.
        /// </remarks>
        private static void AddListToolbar(
            ContentPackageManagerUI ui, Component buttonTemplate, List<string> added)
        {
            var toolbar = Field<GameObject>(ui, "_listToolbar");
            if (toolbar == null)
            {
                var panel = Field<TextMeshProUGUI>(ui, "_emptyLabel")?.transform.parent;
                if (panel == null) return;

                toolbar = new GameObject("List Toolbar", typeof(RectTransform));
                toolbar.transform.SetParent(panel, worldPositionStays: false);
                toolbar.transform.SetSiblingIndex(0);

                var layout = toolbar.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                layout.spacing = 8f;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
                layout.childControlWidth = true;
                layout.childControlHeight = true;

                var element = toolbar.AddComponent<UnityEngine.UI.LayoutElement>();
                element.minHeight = 40f;
                element.flexibleHeight = 0f;

                SetField(ui, "_listToolbar", toolbar);
                added.Add("_listToolbar");
            }

            AddSearchField(ui, toolbar.transform, added);

            // The object name and the caption differ on purpose: the caption is the *current* filter
            // and changes as the user cycles it, so naming the object after it would leave a
            // hierarchy that says "Updates Button" for the control that selects any filter.
            AddToolbarButton(ui, "_filterButton", "_filterButtonLabel",
                "Filter Button", "All", buttonTemplate, toolbar.transform, added);
            AddToolbarButton(ui, "_sortButton", "_sortButtonLabel",
                "Sort Button", "Name", buttonTemplate, toolbar.transform, added);
        }

        /// <summary>Instantiates the project's own input field prefab as the search box.</summary>
        /// <remarks>
        /// Instantiated as a linked prefab instance, not a copy. This is the project's input field,
        /// used elsewhere in the same SDK; if its caret colour or padding changes, the search box
        /// should change with it rather than keep whatever it looked like the day this ran.
        /// </remarks>
        private static void AddSearchField(ContentPackageManagerUI ui, Transform parent, List<string> added)
        {
            if (Field<TMP_InputField>(ui, "_searchField") != null) return;

            var asset = ResolveInputFieldPrefab();
            if (asset == null) return;

            var instance = PrefabUtility.InstantiatePrefab(asset, parent) as GameObject;
            if (instance == null) return;

            instance.name = "Search Field";
            instance.SetActive(true);

            var field = instance.GetComponent<TMP_InputField>();
            if (field == null) { Object.DestroyImmediate(instance); return; }

            field.text = "";
            if (field.placeholder is TextMeshProUGUI placeholder) placeholder.text = "Search…";

            // The search box takes the slack; the two buttons stay their natural width.
            var element = instance.GetComponent<UnityEngine.UI.LayoutElement>()
                ?? instance.AddComponent<UnityEngine.UI.LayoutElement>();
            element.flexibleWidth = 1f;
            element.minWidth = 120f;

            SetField(ui, "_searchField", field);
            added.Add("_searchField");
        }

        /// <summary>Clones the Install button into the toolbar as a cycling filter or sort control.</summary>
        private static void AddToolbarButton(
            ContentPackageManagerUI ui, string buttonField, string labelField, string objectName,
            string caption, Component template, Transform parent, List<string> added)
        {
            if (Field<Component>(ui, buttonField) != null) return;
            if (template == null) return;

            var clone = Object.Instantiate(template.gameObject, parent);
            clone.name = objectName;
            clone.SetActive(true);

            var cloned = clone.GetComponent(template.GetType());
            if (cloned == null) { Object.DestroyImmediate(clone); return; }

            var button = clone.GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();

            SetField(ui, buttonField, cloned);
            added.Add(buttonField);

            var label = clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
            if (label != null && Field<TextMeshProUGUI>(ui, labelField) == null)
            {
                label.text = caption;
                SetField(ui, labelField, label);
                added.Add(labelField);
            }
        }

        /// <summary>Clones the Install button, retitles it, and assigns the button and its label.</summary>
        private static void AddButton(
            ContentPackageManagerUI ui, string buttonField, string labelField, string caption,
            Component template, List<string> added)
        {
            if (Field<Component>(ui, buttonField) != null) return;

            var clone = Object.Instantiate(template.gameObject, template.transform.parent);
            clone.name = caption + " Button";
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            clone.SetActive(true);

            var cloned = clone.GetComponent(template.GetType());
            if (cloned == null) { Object.DestroyImmediate(clone); return; }

            // A cloned button inherits the template's serialized onClick list. The script adds every
            // listener itself in Start, so leaving a persisted one would fire the *template's* action
            // -- an "Update All" button that also installs the selected package.
            var button = clone.GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();

            SetField(ui, buttonField, cloned);
            added.Add(buttonField);

            var label = clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
            if (label != null && Field<TextMeshProUGUI>(ui, labelField) == null)
            {
                label.text = caption;
                SetField(ui, labelField, label);
                added.Add(labelField);
            }
        }

        // ── Reflection helpers ───────────────────────────────────────────────
        //
        // The fields are private and serialized, which is correct for the component: nothing outside
        // it should be reassigning its own references at runtime. A build-time tool is the one
        // legitimate exception, and reflection keeps that exception here rather than widening the
        // component's surface for everyone.

        private static FieldInfo Info(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static T Field<T>(object target, string name) where T : class =>
            Info(target, name)?.GetValue(target) as T;

        private static void SetField(object target, string name, Object value)
        {
            var info = Info(target, name);
            if (info != null && (value == null || info.FieldType.IsInstanceOfType(value)))
                info.SetValue(target, value);
        }
    }
}
