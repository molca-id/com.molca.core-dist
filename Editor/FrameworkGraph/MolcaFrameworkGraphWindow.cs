using System.Collections.Generic;
using Molca.Editor.UI;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Read-only editor window that maps how the loaded project is wired — subsystems, services, scene
    /// references, and sequences — over the same <see cref="FrameworkGraphSnapshot"/> the
    /// <c>molca_framework_graph</c> MCP tool returns. Lives in Core, so every project that consumes the
    /// Molca framework gets it. Strictly an introspection surface: it never mutates serialized data;
    /// drill-in to authoring is delegated to the existing Sequence Graph window (Sprint 22.5).
    /// </summary>
    public sealed class MolcaFrameworkGraphWindow : EditorWindow
    {
        private FrameworkGraphView _graphView;
        private IMGUIContainer _details;
        private Label _banner;
        private string _search = string.Empty;

        private FrameworkGraphNode _selected;
        private FrameworkGraphSnapshot _lastSnapshot;

        // Persisted node positions keyed by node id (22.9): seeded from disk on open, written on drag.
        private Dictionary<string, Vector2> _positions = new();

        // Layer visibility. "Sequences" controls both the controller and its step nodes.
        private readonly HashSet<FrameworkNodeCategory> _visible = new()
        {
            FrameworkNodeCategory.Subsystem, FrameworkNodeCategory.Service,
            FrameworkNodeCategory.Reference, FrameworkNodeCategory.Sequence,
            FrameworkNodeCategory.Step, FrameworkNodeCategory.Config, FrameworkNodeCategory.Fork,
        };

        /// <summary>Opens (or focuses) the Framework Graph window.</summary>
        [MenuItem("Molca/Diagnostics/Framework Graph", priority = 70)]
        public static void Open()
        {
            var window = GetWindow<MolcaFrameworkGraphWindow>();
            window.titleContent = new GUIContent("Framework Graph");
            window.minSize = new Vector2(560, 360);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Framework Graph");

            // Load the shared design tokens onto the root, then layer this window's chrome USS so its
            // rules resolve the inherited `--molca-*` custom properties (Sprint 27.5).
            MolcaEditorUi.Apply(rootVisualElement);
            var chrome = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.molca.core/Editor/FrameworkGraph/MolcaFrameworkGraphWindow.uss");
            if (chrome != null && !rootVisualElement.styleSheets.Contains(chrome))
                rootVisualElement.styleSheets.Add(chrome);

            rootVisualElement.Add(BuildToolbar());

            _banner = new Label { name = "unavailable-banner" };
            _banner.AddToClassList("molca-fg-banner");
            _banner.style.display = DisplayStyle.None;
            rootVisualElement.Add(_banner);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1;

            _graphView = new FrameworkGraphView { name = "framework-graph" };
            _graphView.style.flexGrow = 1;
            _graphView.NodeClicked += OnNodeClicked;
            _graphView.NodesMoved += OnNodesMoved;
            row.Add(_graphView);

            row.Add(BuildDetailsPanel());
            rootVisualElement.Add(row);

            _positions = FrameworkGraphLayoutStore.Load();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Keep the graph current as the scene changes in Edit mode (incremental refresh, 22.9).
            EditorApplication.hierarchyChanged += Rebuild;
            Rebuild();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged -= Rebuild;
            if (_graphView != null)
            {
                _graphView.NodeClicked -= OnNodeClicked;
                _graphView.NodesMoved -= OnNodesMoved;
                PersistPositions();
            }
        }

        // Capture and persist the current node layout whenever the user drags nodes (22.9).
        private void OnNodesMoved() => PersistPositions();

        private void PersistPositions()
        {
            if (_graphView == null) return;
            // Merge into the stored map so nodes not currently shown (filtered out) keep their saved spot.
            foreach (var kv in _graphView.GetPositions())
                _positions[kv.Key] = kv.Value;
            FrameworkGraphLayoutStore.Save(_positions);
        }

        private Toolbar BuildToolbar()
        {
            var toolbar = new Toolbar();

            AddLayerToggle(toolbar, "Subsystems", FrameworkNodeCategory.Subsystem);
            AddLayerToggle(toolbar, "Services", FrameworkNodeCategory.Service);
            AddLayerToggle(toolbar, "References", FrameworkNodeCategory.Reference);
            // One toggle drives both the controller and step layers.
            AddLayerToggle(toolbar, "Sequences", FrameworkNodeCategory.Sequence, FrameworkNodeCategory.Step);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            toolbar.Add(spacer);

            var searchField = new ToolbarSearchField();
            searchField.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue;
                RebuildView();
            });
            toolbar.Add(searchField);

            var refresh = new ToolbarButton(Rebuild) { text = "Refresh" };
            toolbar.Add(refresh);

            return toolbar;
        }

        private void AddLayerToggle(Toolbar toolbar, string label, params FrameworkNodeCategory[] categories)
        {
            var toggle = new ToolbarToggle { text = label, value = true };
            toggle.RegisterValueChangedCallback(evt =>
            {
                foreach (var c in categories)
                {
                    if (evt.newValue) _visible.Add(c);
                    else _visible.Remove(c);
                }
                RebuildView();
            });
            toolbar.Add(toggle);
        }

        private VisualElement BuildDetailsPanel()
        {
            var panel = new VisualElement { name = "details-panel" };
            panel.AddToClassList("molca-fg-details-panel");

            _details = new IMGUIContainer(DrawDetails);
            _details.style.flexGrow = 1;
            panel.Add(_details);
            return panel;
        }

        private void DrawDetails()
        {
            if (_selected == null)
            {
                EditorGUILayout.HelpBox("Click a node to see its details.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(_selected.Label, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_selected.Subtitle))
                EditorGUILayout.LabelField(_selected.Subtitle, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Category", _selected.Category.ToString());
            if (_selected.Severity != FrameworkGraphSeverity.None)
                EditorGUILayout.LabelField("Severity", _selected.Severity.ToString());
            if (_selected.RuntimeOnly)
                EditorGUILayout.LabelField("Runtime-only", "yes");

            if (_selected.Properties.Count > 0)
            {
                EditorGUILayout.Space();
                foreach (var kv in _selected.Properties)
                    EditorGUILayout.LabelField(kv.Key, kv.Value);
            }

            // Drill-in to the dedicated authoring surface (Sprint 22.5) — never edit here.
            if (_selected.Category == FrameworkNodeCategory.Sequence ||
                _selected.Category == FrameworkNodeCategory.Step)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Open in Sequence Graph"))
                    EditorApplication.ExecuteMenuItem("Molca/Sequence/Sequence Graph");
            }
        }

        private void OnNodeClicked(FrameworkGraphNode node)
        {
            _selected = node;
            _details?.MarkDirtyRepaint();
        }

        /// <summary>Rebuilds the snapshot from the project, then redraws the canvas.</summary>
        private void Rebuild()
        {
            _lastSnapshot = FrameworkGraphBuilder.Build();
            RebuildView();
        }

        /// <summary>Redraws the canvas from the last snapshot (filter/search change — no rescan).</summary>
        private void RebuildView()
        {
            if (_graphView == null || _lastSnapshot == null) return;
            _graphView.Rebuild(_lastSnapshot, _visible, _search, _positions);

            if (_lastSnapshot.UnavailableReasons.Count > 0)
            {
                _banner.text = string.Join("\n", _lastSnapshot.UnavailableReasons);
                _banner.style.display = DisplayStyle.Flex;
            }
            else
            {
                _banner.style.display = DisplayStyle.None;
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
                Rebuild();
        }
    }
}
