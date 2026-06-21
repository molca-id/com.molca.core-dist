using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;

namespace Molca.Editor
{
    /// <summary>
    /// Toolbar (UI Toolkit, Phase 1), runtime filters, empty-state message, and keyboard shortcuts.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        // --- UI Toolkit toolbar (Phase 1) ---
        private Toolbar _toolbar;
        private ObjectField _controllerField;
        private ToolbarButton _problemsButton;
        private ToolbarToggle _autoRefreshToggle;
        private ToolbarToggle _detailsToggle;

        /// <summary>Builds the native UI Toolkit toolbar hosted above the (still-IMGUI) body.</summary>
        private VisualElement BuildToolbar()
        {
            _toolbar = new Toolbar();

            var label = new Label("Controller:");
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.marginLeft = 4;
            label.style.marginRight = 4;
            _toolbar.Add(label);

            _controllerField = new ObjectField
            {
                objectType = typeof(SequenceController),
                allowSceneObjects = true,
            };
            _controllerField.style.minWidth = 150;
            _controllerField.style.maxWidth = 320;
            _controllerField.style.flexShrink = 1;
            _controllerField.RegisterValueChangedCallback(evt =>
            {
                var controller = evt.newValue as SequenceController;
                if (controller != _selectedController)
                    SelectController(controller);
            });
            _toolbar.Add(_controllerField);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            _toolbar.Add(spacer);

            // Validation summary + "select all problems" (edit mode only; findings are meaningless at runtime).
            _problemsButton = new ToolbarButton(SelectAllProblems) { text = "No Problems" };
            _problemsButton.style.width = 110;
            _toolbar.Add(_problemsButton);

            _autoRefreshToggle = new ToolbarToggle { text = "Auto", tooltip = "Auto-refresh while in play mode" };
            _autoRefreshToggle.RegisterValueChangedCallback(evt =>
            {
                _autoRefresh = evt.newValue;
                MolcaEditorPrefs.SetBool(Key("AutoRefresh"), _autoRefresh);
            });
            _toolbar.Add(_autoRefreshToggle);

            _detailsToggle = new ToolbarToggle { text = "Details", tooltip = "Show the details panel" };
            _detailsToggle.RegisterValueChangedCallback(evt =>
            {
                _showDetailedInfo = evt.newValue;
                MolcaEditorPrefs.SetBool(Key("ShowDetails"), _showDetailedInfo);
                Repaint();
            });
            _toolbar.Add(_detailsToggle);

            var help = new ToolbarButton(ShowShortcutsDialog) { text = "?", tooltip = "Keyboard shortcuts & search operators" };
            help.style.width = 24;
            _toolbar.Add(help);

            var refresh = new ToolbarButton(ForceRefresh) { tooltip = "Refresh hierarchy and caches" };
            refresh.Add(new Image { image = EditorGUIUtility.IconContent("d_Refresh").image });
            _toolbar.Add(refresh);

            return _toolbar;
        }

        /// <summary>
        /// Syncs the event-driven toolbar to current state. Called from <see cref="Repaint"/>, which
        /// every edit/selection/play-mode transition already funnels through.
        /// </summary>
        private void RefreshToolbarState()
        {
            if (_toolbar == null) return;

            _controllerField.SetValueWithoutNotify(_selectedController);

            bool editMode = !Application.isPlaying && _selectedController != null;
            _problemsButton.style.display = editMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (editMode)
            {
                EnsureValidation();
                _problemsButton.text = _findingCount == 0
                    ? "No Problems"
                    : $"{_findingCount} Problem{(_findingCount == 1 ? "" : "s")}";
                _problemsButton.SetEnabled(_findingCount > 0);
            }

            _autoRefreshToggle.SetEnabled(Application.isPlaying);
            _autoRefreshToggle.SetValueWithoutNotify(_autoRefresh);
            _detailsToggle.SetValueWithoutNotify(_showDetailedInfo);
        }

        /// <summary>Forces a re-scan (edit mode) and repaint — the refresh toolbar action.</summary>
        private void ForceRefresh()
        {
            if (!Application.isPlaying)
            {
                _hierarchyDirty = true;
                RefreshEditorHierarchy();
            }
            ClearCaches();
            Repaint();
        }

        private static void ShowShortcutsDialog()
        {
            EditorUtility.DisplayDialog("Keyboard Shortcuts",
                "Ctrl/Cmd+Click - Add or remove step from selection\n\n" +
                "Shift+Click - Range select (anchor = last clicked)\n\n" +
                "Ctrl/Cmd+D - Duplicate selected step(s) (edit mode only)\n\n" +
                "Delete - Delete selected step(s) (edit mode only)\n\n" +
                "F - Focus/frame primary selected step in hierarchy\n\n" +
                "Ctrl/Cmd+F - Focus search field\n\n" +
                "Esc - Clear search filter; pressed again, clear selection\n\n" +
                "Search operators (combine freely, AND):\n" +
                "  ref:<id>       match Ref Id\n" +
                "  type:<name>    match step type\n" +
                "  aux:<name>     match auxiliary type\n" +
                "  status:<state> match runtime status (active/completed/inactive)\n" +
                "  plain text     match step name or type\n" +
                "  quote values with spaces, e.g. type:\"My Step\"", "OK");
        }

    }
}
