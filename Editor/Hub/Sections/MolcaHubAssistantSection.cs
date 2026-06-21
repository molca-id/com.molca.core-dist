using Molca.Editor.UI.Components;
using Molca.Editor.Mcp.Assistant;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Assistant settings section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Assistant rail section is active.
    /// Configures the in-editor chat assistant (provider, model, generation knobs, API key) through the
    /// existing <see cref="AssistantSettings"/> SO and <see cref="AssistantApiAuth"/>. The API key stays in
    /// project-scoped EditorPrefs / env var, never on the asset. The chat itself is a separate Hub workspace.
    /// </remarks>
    internal sealed class MolcaHubAssistantSection : VisualElement
    {
        private readonly SerializedObject _editorSettings;
        private string _keyDraft = string.Empty;
        private LlmProviderKind _providerAtBuild;

        internal MolcaHubAssistantSection()
        {
            AddToClassList("molca-hub-assistant-section");
            _editorSettings = new SerializedObject(MolcaEditorSettings.Instance);
            Rebuild();
        }

        private void Rebuild()
        {
            Clear();

            var assistantProperty = _editorSettings.FindProperty("assistantSettings");
            var settings = assistantProperty.objectReferenceValue as AssistantSettings;

            if (settings == null)
            {
                var card = new MolcaSectionCard("Assistant", "Not configured", MolcaStatusKind.Idle, "No asset");
                var message = new Label("A chat assistant inside the editor using the same read-only MCP tools. Create its settings to configure a provider and API key.");
                message.AddToClassList("molca-hub-muted");
                card.Body.Add(message);

                var create = new Button(() =>
                {
                    var created = AssistantSettings.GetOrCreateSettings();
                    assistantProperty.objectReferenceValue = created;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                    Rebuild();
                })
                { text = "Create Assistant Settings" };
                create.AddToClassList("molca-hub-action-full");
                create.AddToClassList("molca-hub-action-full--primary");
                card.Body.Add(create);
                Add(card);
                return;
            }

            var status = settings.GetStatus(out var statusMessage);
            var configCard = new MolcaSectionCard("Assistant", settings.name,
                status == AssistantConfigStatus.Configured ? MolcaStatusKind.Ok
                    : status == AssistantConfigStatus.Misconfigured ? MolcaStatusKind.Error
                    : MolcaStatusKind.Idle,
                statusMessage);
            Add(configCard);

            var so = new SerializedObject(settings);
            _providerAtBuild = settings.Provider;

            configCard.Body.Add(BoundRow(so, "enabled", "Enable Assistant"));
            configCard.Body.Add(BoundRow(so, "provider", "Provider", out var providerField));
            configCard.Body.Add(BoundRow(so, "model", "Model"));

            if (settings.Provider == LlmProviderKind.OpenAI)
                configCard.Body.Add(BoundRow(so, "baseUrl", "Base URL"));

            configCard.Body.Add(BoundRow(so, "maxTokens", "Max Tokens"));
            configCard.Body.Add(BoundRow(so, "maxToolRounds", "Max Tool Rounds"));
            configCard.Body.Add(BoundRow(so, "streamResponses", "Stream Responses"));
            configCard.Body.Add(BoundRow(so, "autoCompact", "Auto Compact"));
            configCard.Body.Add(BoundRow(so, "autoCompactThreshold", "Auto Compact Threshold"));
            configCard.Body.Add(BoundRow(so, "compactToolResultsFirst", "Compact Tool Results First"));
            configCard.Body.Add(BoundRow(so, "keepRecentToolResultTurns", "Keep Recent Tool-Result Turns"));
            configCard.Body.Add(BoundRow(so, "proactiveRetrieval", "Proactive Retrieval"));
            configCard.Body.Add(BoundRow(so, "retrievalTokenBudget", "Retrieval Token Budget"));

            // Provider change flips the Base-URL row and the key env-var, so the card is rebuilt — but only
            // on a real change. PropertyField fires SerializedPropertyChangeEvent on every bind too; without
            // this guard the rebuild would re-bind and re-fire every frame (the bridge-toggle flicker bug).
            providerField.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                so.ApplyModifiedProperties();
                if (settings.Provider == _providerAtBuild) return;
                EditorUtility.SetDirty(settings);
                Rebuild();
            });

            configCard.Body.Add(BuildKeyRow(settings));

            var privacy = new Label("Project data (questions, tool results) leaves the editor to the configured LLM provider.");
            privacy.AddToClassList("molca-hub-muted");
            configCard.Body.Add(privacy);
        }

        private VisualElement BuildKeyRow(AssistantSettings settings)
        {
            var container = new VisualElement();
            var provider = settings.Provider;

            var heading = new Label("API Key");
            heading.AddToClassList("molca-hub-bv-option-heading");
            container.Add(heading);

            if (AssistantApiAuth.IsFromEnv(provider))
            {
                var envNote = new Label($"Using the {AssistantApiAuth.EnvVarFor(provider)} environment variable.");
                envNote.AddToClassList("molca-hub-muted");
                container.Add(envNote);
                return container;
            }

            var row = new VisualElement();
            row.AddToClassList("molca-hub-mcp-token-row");
            container.Add(row);

            var keyField = new TextField { isPasswordField = true, value = _keyDraft };
            keyField.AddToClassList("molca-hub-field-control");
            keyField.RegisterValueChangedCallback(evt => _keyDraft = evt.newValue);
            row.Add(keyField);

            var save = new Button(() =>
            {
                AssistantApiAuth.SetKey(provider, _keyDraft);
                _keyDraft = string.Empty;
                Rebuild();
            })
            { text = "Save", tooltip = "Store the key in project-scoped EditorPrefs (never committed)." };
            save.AddToClassList("molca-hub-mini-button");
            row.Add(save);

            var clear = new Button(() =>
            {
                AssistantApiAuth.SetKey(provider, string.Empty);
                _keyDraft = string.Empty;
                Rebuild();
            })
            { text = "Clear", tooltip = "Remove the stored key." };
            clear.AddToClassList("molca-hub-mini-button");
            row.Add(clear);

            var state = new Label(AssistantApiAuth.HasKey(provider) ? "A key is stored for this provider." : "No key stored.");
            state.AddToClassList("molca-hub-muted");
            container.Add(state);

            return container;
        }

        private static VisualElement BoundRow(SerializedObject so, string propertyName, string label) =>
            BoundRow(so, propertyName, label, out _);

        private static VisualElement BoundRow(SerializedObject so, string propertyName, string label, out PropertyField field)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");

            var fieldLabel = new Label(label);
            fieldLabel.AddToClassList("molca-hub-field-label");
            row.Add(fieldLabel);

            var property = so.FindProperty(propertyName);
            field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);
            row.Add(field);

            return row;
        }
    }
}
