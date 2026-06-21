using System.Collections.Generic;
using Molca;
using Molca.Editor.UI.Components;
using Molca.Settings;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Runtime &amp; Global section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Runtime &amp; Global rail section is active.
    /// Modules are presented as a horizontal chip strip above a full-width detail pane (the previous vertical
    /// rail starved the inspector of horizontal space). Runtime Manager and Global Settings are assignable from
    /// the breadcrumb via bound object fields. Module fields are drawn through their serialized inspectors — the
    /// runtime settings APIs and serialized fields are untouched.
    /// </remarks>
    internal sealed class MolcaHubRuntimeSection : VisualElement
    {
        private readonly MolcaHubState _state;
        private readonly MolcaProjectSettings _projectSettings;
        private readonly SerializedObject _projectSO;
        private readonly List<SettingModule> _modules = new List<SettingModule>();

        private GlobalSettings _globalSettings;
        private VisualElement _modulesContainer;
        private VisualElement _chipStrip;
        private VisualElement _moduleDetail;
        private int _selectedModuleIndex = -1;

        internal MolcaHubRuntimeSection(MolcaHubState state)
        {
            _state = state;
            AddToClassList("molca-hub-runtime-section");

            _projectSettings = MolcaProjectSettings.Instance;
            _projectSO = _projectSettings != null ? new SerializedObject(_projectSettings) : null;
            _globalSettings = _projectSettings != null ? _projectSettings.GlobalSettings : null;

            BuildContextHeader();

            _modulesContainer = new VisualElement();
            _modulesContainer.AddToClassList("molca-hub-rt-modules");
            Add(_modulesContainer);

            RebuildModulesArea();
        }

        // -------------------------------------------------------------------
        // Context header (breadcrumb) — Runtime Manager / Global Settings assignable
        // -------------------------------------------------------------------

        private void BuildContextHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("molca-hub-rt-context");
            Add(header);

            if (_projectSO == null)
            {
                var notice = new Label("Molca Project Settings asset is unavailable.");
                notice.AddToClassList("molca-hub-muted");
                header.Add(notice);
                return;
            }

            header.Add(BuildAssignableCrumb(
                "molca-hub-rt-context__marker--runtime",
                "Runtime Manager",
                "runtimeManager",
                typeof(RuntimeManager)));

            var sep = new Label("›");
            sep.AddToClassList("molca-hub-rt-context__sep");
            header.Add(sep);

            header.Add(BuildAssignableCrumb(
                "molca-hub-rt-context__marker--global",
                "Global Settings",
                "globalSettings",
                typeof(GlobalSettings),
                onChanged: RefreshFromGlobalSettings));

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            header.Add(spacer);

            int bootstrapCount = _projectSettings.BootstrapExtensions.Count;
            var bootstrapChip = new Button(() => EditorGUIUtility.PingObject(_projectSettings))
            {
                text = $"Bootstrap Ext ({bootstrapCount})",
                tooltip = "Bootstrap extensions run before any subsystem initializes. Configure them on the Molca Project Settings asset."
            };
            bootstrapChip.AddToClassList("molca-hub-rt-chip");
            header.Add(bootstrapChip);
        }

        private VisualElement BuildAssignableCrumb(string markerClass, string label, string propertyName, System.Type objectType, System.Action onChanged = null)
        {
            var crumb = new VisualElement();
            crumb.AddToClassList("molca-hub-rt-context__crumb");

            var marker = new VisualElement();
            marker.AddToClassList("molca-hub-rt-context__marker");
            marker.AddToClassList(markerClass);
            crumb.Add(marker);

            var caption = new Label($"{label}:");
            caption.AddToClassList("molca-hub-rt-context__label");
            crumb.Add(caption);

            var property = _projectSO.FindProperty(propertyName);
            var field = new ObjectField { objectType = objectType, allowSceneObjects = false };
            field.AddToClassList("molca-hub-rt-context__field");
            field.BindProperty(property);
            field.RegisterValueChangedCallback(_ =>
            {
                _projectSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(_projectSettings);
                onChanged?.Invoke();
            });
            // No separate ping button: the ObjectField already provides its own object-picker / ping affordance.
            crumb.Add(field);

            return crumb;
        }

        private void RefreshFromGlobalSettings()
        {
            _globalSettings = _projectSettings != null ? _projectSettings.GlobalSettings : null;
            _selectedModuleIndex = -1;
            RebuildModulesArea();
        }

        // -------------------------------------------------------------------
        // Modules: horizontal chip strip + full-width detail pane
        // -------------------------------------------------------------------

        private void CollectModules()
        {
            _modules.Clear();
            if (_globalSettings == null || _globalSettings.modules == null) return;
            foreach (var module in _globalSettings.modules)
                _modules.Add(module); // keep null entries so misconfigured slots surface with a grey dot
        }

        private void RebuildModulesArea()
        {
            _modulesContainer.Clear();

            if (_globalSettings == null)
            {
                var card = new MolcaSectionCard(
                    "Global Settings",
                    "Not assigned",
                    MolcaStatusKind.Warning,
                    "Misconfigured");
                var message = new Label("Assign a Global Settings asset above to manage runtime setting modules here.");
                message.AddToClassList("molca-hub-muted");
                card.Body.Add(message);
                _modulesContainer.Add(card);
                return;
            }

            CollectModules();
            if (_selectedModuleIndex < 0)
                _selectedModuleIndex = ResolveSelectedModuleIndex(_state.SelectedRuntimeModule);

            var heading = new VisualElement();
            heading.AddToClassList("molca-hub-rt-modules-heading");
            _modulesContainer.Add(heading);

            var title = new Label("Modules");
            title.AddToClassList("molca-hub-rt-modules-title");
            heading.Add(title);

            var count = new Label(_modules.Count.ToString());
            count.AddToClassList("molca-hub-rt-modules-count");
            heading.Add(count);

            _chipStrip = new VisualElement();
            _chipStrip.AddToClassList("molca-hub-rt-chip-strip");
            _modulesContainer.Add(_chipStrip);

            _moduleDetail = new VisualElement();
            _moduleDetail.AddToClassList("molca-hub-rt-detail");
            _modulesContainer.Add(_moduleDetail);

            RebuildChips();
            RebuildModuleDetail();
        }

        private void RebuildChips()
        {
            _chipStrip.Clear();

            if (_modules.Count == 0)
            {
                var empty = new Label("No modules registered.");
                empty.AddToClassList("molca-hub-muted");
                _chipStrip.Add(empty);
                return;
            }

            for (int i = 0; i < _modules.Count; i++)
                _chipStrip.Add(BuildModuleChip(i, _modules[i]));
        }

        private VisualElement BuildModuleChip(int index, SettingModule module)
        {
            var chip = new Button(() => SelectModule(index));
            chip.AddToClassList("molca-hub-rt-chip-item");
            chip.EnableInClassList("molca-hub-rt-chip-item--selected", index == _selectedModuleIndex);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-status-dot");
            dot.AddToClassList(module != null ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--error");
            chip.Add(dot);

            var name = new Label(module != null ? module.name : $"(missing {index})");
            name.AddToClassList("molca-hub-rt-chip-item__name");
            chip.Add(name);

            return chip;
        }

        private void SelectModule(int index)
        {
            _selectedModuleIndex = index;
            if (index >= 0 && index < _modules.Count && _modules[index] != null)
                _state.SetSelectedRuntimeModule(_modules[index].name);

            RebuildChips();
            RebuildModuleDetail();
        }

        private void RebuildModuleDetail()
        {
            _moduleDetail.Clear();

            if (_selectedModuleIndex < 0 || _selectedModuleIndex >= _modules.Count)
            {
                var empty = new Label("Select a module to edit its settings.");
                empty.AddToClassList("molca-hub-muted");
                _moduleDetail.Add(empty);
                return;
            }

            var module = _modules[_selectedModuleIndex];
            if (module == null)
            {
                var missing = new Label("This module slot is empty. Assign a SettingModule asset on the Global Settings modules list.");
                missing.AddToClassList("molca-hub-muted");
                _moduleDetail.Add(missing);
                return;
            }

            var detailHeader = new VisualElement();
            detailHeader.AddToClassList("molca-hub-rt-detail-header");
            _moduleDetail.Add(detailHeader);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-status-dot");
            dot.AddToClassList("molca-hub-status-dot--ok");
            detailHeader.Add(dot);

            var name = new Label(module.name);
            name.AddToClassList("molca-hub-rt-detail-title");
            detailHeader.Add(name);

            var typeLabel = new Label(module.GetType().Name);
            typeLabel.AddToClassList("molca-hub-rt-detail-type");
            detailHeader.Add(typeLabel);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            detailHeader.Add(spacer);

            var ping = new Button(() => EditorGUIUtility.PingObject(module)) { text = "Ping", tooltip = "Locate this module asset in the Project window." };
            ping.AddToClassList("molca-hub-mini-button");
            detailHeader.Add(ping);

            var body = new VisualElement();
            body.AddToClassList("molca-hub-rt-detail-body");
            _moduleDetail.Add(body);

            // InspectorElement renders the module's serialized fields (honoring [Header] subheaders and any
            // custom editor) through SerializedObject, so edits keep undo/dirtying without duplicating field
            // layout per module type. It now has the full detail width to work in.
            var inspector = new InspectorElement(new SerializedObject(module));
            inspector.AddToClassList("molca-hub-rt-inspector");
            body.Add(inspector);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private int ResolveSelectedModuleIndex(string moduleName)
        {
            if (_modules.Count == 0)
                return -1;

            if (!string.IsNullOrEmpty(moduleName))
            {
                for (int i = 0; i < _modules.Count; i++)
                {
                    if (_modules[i] != null && _modules[i].name == moduleName)
                        return i;
                }
            }

            // Default to the first non-null module so the detail pane is never empty when modules exist.
            for (int i = 0; i < _modules.Count; i++)
                if (_modules[i] != null)
                    return i;

            return 0;
        }
    }
}
