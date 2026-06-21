using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Settings;
using Molca.Settings.Notification;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Editor section for the Molca Hub Settings workspace: Area Picker and Notification Providers.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Editor rail section is active.
    /// Area Picker reads/writes through the existing <see cref="AreaPicker"/> accessors; its card body dims
    /// when disabled. Notification providers are listed once with status merged into each row (the standalone
    /// Provider Status panel is gone), editing the existing <see cref="NotificationSettings"/> serialized array.
    /// </remarks>
    internal sealed class MolcaHubEditorSection : VisualElement
    {
        private readonly SerializedObject _editorSettings;

        private MolcaSectionCard _areaPickerCard;
        private VisualElement _notificationsHost;

        internal MolcaHubEditorSection()
        {
            AddToClassList("molca-hub-editor-section");

            _editorSettings = new SerializedObject(MolcaEditorSettings.Instance);

            BuildAreaPickerCard();

            _notificationsHost = new VisualElement();
            Add(_notificationsHost);
            BuildNotificationsCard();
        }

        // -------------------------------------------------------------------
        // Area Picker
        // -------------------------------------------------------------------

        private void BuildAreaPickerCard()
        {
            _areaPickerCard = new MolcaSectionCard("Area Picker");
            _areaPickerCard.SetHelp(
                "Lists GameObjects whose position is inside the proximity sphere around the Scene-view click point. " +
                "Hold the modifier key and click in the Scene view. Optionally filter by component type.");
            Add(_areaPickerCard);

            var enableToggle = new Toggle { value = AreaPicker.GetEnabled() };
            enableToggle.AddToClassList("molca-hub-card-header-toggle");
            enableToggle.RegisterValueChangedCallback(evt =>
            {
                AreaPicker.SetEnabled(evt.newValue);
                ApplyAreaPickerEnabled(evt.newValue);
            });
            _areaPickerCard.AddHeaderAction(enableToggle);

            _areaPickerCard.Body.Add(BuildRadiusRow());
            _areaPickerCard.Body.Add(BuildModifierRow());
            _areaPickerCard.Body.Add(BuildFilterRow());

            ApplyAreaPickerEnabled(AreaPicker.GetEnabled());
        }

        private void ApplyAreaPickerEnabled(bool enabled)
        {
            _areaPickerCard.SetStatus(enabled ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                enabled ? "Enabled" : "Disabled");
            _areaPickerCard.SetBodyEnabled(enabled);
        }

        private static VisualElement BuildRadiusRow()
        {
            var row = MakeFieldRow("Proximity Radius", out var control);
            var field = new FloatField { value = AreaPicker.GetProximityRadius() };
            field.AddToClassList("molca-hub-field-control");
            field.RegisterValueChangedCallback(evt => AreaPicker.SetProximityRadius(evt.newValue));
            control.Add(field);
            return row;
        }

        private static VisualElement BuildModifierRow()
        {
            var row = MakeFieldRow("Modifier Key", out var control);

            var names = new List<string> { "Alt", "Control", "Shift" };
            var modifiers = new[] { EventModifiers.Alt, EventModifiers.Control, EventModifiers.Shift };
            int current = System.Array.IndexOf(modifiers, AreaPicker.GetModifier());
            if (current < 0) current = 0;

            var dropdown = new DropdownField(names, current);
            dropdown.AddToClassList("molca-hub-field-control");
            dropdown.RegisterValueChangedCallback(evt =>
            {
                int index = names.IndexOf(evt.newValue);
                if (index >= 0) AreaPicker.SetModifier(modifiers[index]);
            });
            control.Add(dropdown);
            return row;
        }

        private static VisualElement BuildFilterRow()
        {
            var row = MakeFieldRow("Filter by component type", out var control);
            var field = new TextField { value = AreaPicker.GetFilterTypeName() };
            field.AddToClassList("molca-hub-field-control");
            field.tooltip = "Optional. Only list GameObjects that have this component (e.g. UnityEngine.MeshRenderer). Leave empty for all.";
            field.RegisterValueChangedCallback(evt => AreaPicker.SetFilterTypeName(evt.newValue));
            control.Add(field);
            return row;
        }

        // -------------------------------------------------------------------
        // Notification Providers
        // -------------------------------------------------------------------

        private void BuildNotificationsCard()
        {
            _notificationsHost.Clear();

            var settingsProperty = _editorSettings.FindProperty("notificationSettings");
            var settings = settingsProperty.objectReferenceValue as NotificationSettings;

            if (settings == null)
            {
                var card = new MolcaSectionCard("Notification Providers", "Not assigned",
                    MolcaStatusKind.Idle, "No asset");
                var message = new Label("No Notification Settings asset assigned. Create one to configure build, light-baking, and custom notification providers.");
                message.AddToClassList("molca-hub-muted");
                card.Body.Add(message);

                var create = new Button(() =>
                {
                    var created = NotificationSettings.GetOrCreateSettings();
                    settingsProperty.objectReferenceValue = created;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                    BuildNotificationsCard();
                })
                { text = "Create Notification Settings" };
                create.AddToClassList("molca-hub-action-full");
                create.AddToClassList("molca-hub-action-full--primary");
                card.Body.Add(create);

                _notificationsHost.Add(card);
                return;
            }

            var settingsSO = new SerializedObject(settings);
            var providersProperty = settingsSO.FindProperty("providers");

            var providersCard = new MolcaSectionCard("Notification Providers", settings.name);

            var select = new Button(() => EditorGUIUtility.PingObject(settings)) { text = "Select", tooltip = "Locate the Notification Settings asset." };
            select.AddToClassList("molca-hub-mini-button");
            providersCard.AddHeaderAction(select);

            var add = new Button(() =>
            {
                providersProperty.InsertArrayElementAtIndex(providersProperty.arraySize);
                providersProperty.GetArrayElementAtIndex(providersProperty.arraySize - 1).objectReferenceValue = null;
                settingsSO.ApplyModifiedProperties();
                BuildNotificationsCard();
            })
            { text = "+", tooltip = "Add a provider slot." };
            add.AddToClassList("molca-hub-rt-context__ping");
            providersCard.AddHeaderAction(add);

            if (providersProperty.arraySize == 0)
            {
                var empty = new Label("No providers configured.");
                empty.AddToClassList("molca-hub-muted");
                providersCard.Body.Add(empty);
            }
            else
            {
                for (int i = 0; i < providersProperty.arraySize; i++)
                    providersCard.Body.Add(BuildProviderRow(settingsSO, providersProperty, i));
            }

            _notificationsHost.Add(providersCard);
        }

        private VisualElement BuildProviderRow(SerializedObject settingsSO, SerializedProperty providersProperty, int index)
        {
            var element = providersProperty.GetArrayElementAtIndex(index);
            var provider = element.objectReferenceValue as NotificationProvider;

            var row = new VisualElement();
            row.AddToClassList("molca-hub-provider-row");

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-status-dot");
            dot.AddToClassList(provider == null ? "molca-hub-status-dot--error"
                : provider.IsEnabled ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
            row.Add(dot);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-provider-row__stack");
            row.Add(stack);

            var name = new Label(provider != null ? provider.DisplayName : $"(empty slot {index})");
            name.AddToClassList("molca-hub-provider-row__name");
            stack.Add(name);

            var status = new Label(provider != null ? provider.GetStatusMessage() : "Assign a NotificationProvider asset");
            status.AddToClassList("molca-hub-provider-row__status");
            stack.Add(status);

            if (provider != null)
            {
                var inspect = new Button(() => EditorGUIUtility.PingObject(provider)) { text = "Inspect", tooltip = "Locate this provider asset to edit its configuration." };
                inspect.AddToClassList("molca-hub-mini-button");
                row.Add(inspect);
            }
            else
            {
                var assign = new ObjectField { objectType = typeof(NotificationProvider), allowSceneObjects = false };
                assign.AddToClassList("molca-hub-provider-row__assign");
                assign.RegisterValueChangedCallback(evt =>
                {
                    element.objectReferenceValue = evt.newValue;
                    settingsSO.ApplyModifiedProperties();
                    BuildNotificationsCard();
                });
                row.Add(assign);
            }

            var remove = new Button(() =>
            {
                // DeleteArrayElementAtIndex only nulls a non-null object reference on the first call, so
                // clear it first to guarantee the element is actually removed in one action.
                element.objectReferenceValue = null;
                providersProperty.DeleteArrayElementAtIndex(index);
                settingsSO.ApplyModifiedProperties();
                BuildNotificationsCard();
            })
            { text = "−", tooltip = "Remove this provider slot." };
            remove.AddToClassList("molca-hub-rt-context__ping");
            row.Add(remove);

            return row;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static VisualElement MakeFieldRow(string label, out VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-bv-field");

            var fieldLabel = new Label(label);
            fieldLabel.AddToClassList("molca-hub-field-label");
            row.Add(fieldLabel);

            control = new VisualElement();
            control.AddToClassList("molca-hub-field-control");
            row.Add(control);

            return row;
        }
    }
}
