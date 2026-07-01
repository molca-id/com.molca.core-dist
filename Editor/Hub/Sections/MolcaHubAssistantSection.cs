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

            if (settings.UsesBaseUrl)
                configCard.Body.Add(BoundRow(so, "baseUrl", "Base URL"));

            // Known-weak local models (Gemma 3n e2b/e4b, ≤2B tags) run fine for read-only chat but drop or
            // malform tool calls, so multi-step authoring is unreliable. Non-blocking — just flag it.
            if (settings.IsWeakToolModel)
            {
                var weakWarning = new Label(
                    $"⚠ '{settings.Model}' is a small local model with weak tool-calling. Read-only questions work, but multi-step authoring (editing sequences, Figma import, etc.) may be unreliable. For dependable tool use try a 7B+ tool-tuned model such as qwen2.5:7b.");
                weakWarning.AddToClassList("molca-hub-muted");
                weakWarning.style.whiteSpace = WhiteSpace.Normal;
                configCard.Body.Add(weakWarning);
            }

            // Essentials — the handful most users touch. Everything else lives under Advanced (Sprint 71.1),
            // collapsed by default, so the card isn't a wall of knobs.
            configCard.Body.Add(BoundRow(so, "maxTokens", "Max Tokens"));
            configCard.Body.Add(BoundRow(so, "streamResponses", "Stream Responses"));

            // Advanced: grouped, collapsible, and remembered per user via the view-data key. Includes the
            // tool-use, compaction, resilience, research sub-agent (Sprint 56), and cost-override knobs that
            // were previously either buried in a flat list or not surfaced in the Hub at all.
            var advanced = new Foldout { text = "Advanced settings", value = false, viewDataKey = "molca-assistant-advanced" };
            advanced.AddToClassList("molca-hub-assistant-advanced");
            configCard.Body.Add(advanced);

            AddGroupHeading(advanced, "Tool Use");
            advanced.Add(BoundRow(so, "maxToolRounds", "Max Tool Rounds"));
            advanced.Add(BoundRow(so, "toolExposure", "Tool Exposure"));
            advanced.Add(BoundRow(so, "toolCallTransport", "Tool Call Transport"));

            AddGroupHeading(advanced, "Context & Compaction");
            advanced.Add(BoundRow(so, "autoCompact", "Auto Compact"));
            advanced.Add(BoundRow(so, "autoCompactThreshold", "Auto Compact Threshold"));
            advanced.Add(BoundRow(so, "compactToolResultsFirst", "Compact Tool Results First"));
            advanced.Add(BoundRow(so, "keepRecentToolResultTurns", "Keep Recent Tool-Result Turns"));
            advanced.Add(BoundRow(so, "proactiveRetrieval", "Proactive Retrieval"));
            advanced.Add(BoundRow(so, "retrievalTokenBudget", "Retrieval Token Budget"));

            // Resilience knobs (Sprint 68): transport retry cap, unproductive-loop breaker, per-result size cap.
            AddGroupHeading(advanced, "Resilience");
            advanced.Add(BoundRow(so, "retryMaxAttempts", "Retry Max Attempts"));
            advanced.Add(BoundRow(so, "loopBreakThreshold", "Loop-Break Threshold"));
            advanced.Add(BoundRow(so, "maxToolResultChars", "Max Tool-Result Chars"));

            // Research sub-agents (Sprint 56): read-only research swarm caps — now surfaced in the Hub.
            AddGroupHeading(advanced, "Research Sub-Agents");
            advanced.Add(BoundRow(so, "maxSubAgentsPerTurn", "Max Sub-Agents / Turn"));
            advanced.Add(BoundRow(so, "subAgentConcurrency", "Sub-Agent Concurrency"));
            advanced.Add(BoundRow(so, "subAgentMaxRounds", "Sub-Agent Max Rounds"));
            advanced.Add(BoundRow(so, "subAgentMaxTokens", "Sub-Agent Max Tokens"));

            // Cost: per-model price overrides for the session cost estimate (a list — full-width field).
            AddGroupHeading(advanced, "Cost");
            advanced.Add(BoundListField(so, "modelPriceOverrides", "Model Price Overrides"));

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

            // Sprint 65: the assistant runs on the editor loop, so it stays usable while Play mode is paused.
            var playPause = new Label("The assistant stays usable while Unity Play mode is paused — ask about the frozen scene and it answers and runs read tools without needing you to unpause.");
            playPause.AddToClassList("molca-hub-muted");
            configCard.Body.Add(playPause);

            AddUsageCard(settings);
        }

        /// <summary>
        /// A read-only, disk-backed usage panel (Sprint 53): lists recent sessions (title · tokens · estimated
        /// cost) from <see cref="AssistantSessionLibrary"/> metadata with a totals row — no live controller
        /// needed, so it fits the settings-only Hub section. Cost is estimated at the current model's pricing.
        /// </summary>
        private void AddUsageCard(AssistantSettings settings)
        {
            var sessions = AssistantSessionLibrary.ListSessions();
            var card = new MolcaSectionCard("Usage", $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}",
                MolcaStatusKind.Idle, "Estimated");

            var refresh = new Button(Rebuild) { text = "Refresh", tooltip = "Re-read session usage from disk." };
            refresh.AddToClassList("molca-hub-mini-button");
            card.AddHeaderAction(refresh);
            Add(card);

            if (sessions.Count == 0)
            {
                var empty = new Label("No saved sessions yet.");
                empty.AddToClassList("molca-hub-muted");
                card.Body.Add(empty);
                return;
            }

            card.Body.Add(UsageRow("Session", "Tokens (in / out)", "Est. cost", header: true));

            long totalIn = 0, totalOut = 0;
            double totalCost = 0;
            foreach (var s in sessions)
            {
                totalIn += s.InputTokens;
                totalOut += s.OutputTokens;
                var cost = AssistantCostTable.EstimateCost(settings.Model, s.InputTokens, s.OutputTokens, settings.ModelPriceOverrides);
                totalCost += cost;
                card.Body.Add(UsageRow(
                    string.IsNullOrWhiteSpace(s.Title) ? "(untitled)" : s.Title,
                    $"{s.InputTokens:n0} / {s.OutputTokens:n0}",
                    AssistantCostTable.FormatCost(cost)));
            }

            card.Body.Add(UsageRow("Total", $"{totalIn:n0} / {totalOut:n0}", AssistantCostTable.FormatCost(totalCost), header: true));

            var note = new Label("Estimated from stored token counts at the current model's pricing; actual billing may differ.");
            note.AddToClassList("molca-hub-muted");
            card.Body.Add(note);
        }

        private static VisualElement UsageRow(string title, string tokens, string cost, bool header = false)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");

            var titleLabel = new Label(title) { style = { flexGrow = 1, flexShrink = 1 } };
            var tokensLabel = new Label(tokens) { style = { flexGrow = 0, width = 150 } };
            var costLabel = new Label(cost) { style = { flexGrow = 0, width = 80 } };
            if (header)
            {
                titleLabel.AddToClassList("molca-hub-bv-option-heading");
                tokensLabel.AddToClassList("molca-hub-bv-option-heading");
                costLabel.AddToClassList("molca-hub-bv-option-heading");
            }

            row.Add(titleLabel);
            row.Add(tokensLabel);
            row.Add(costLabel);
            return row;
        }

        private VisualElement BuildKeyRow(AssistantSettings settings)
        {
            var container = new VisualElement();
            var provider = settings.Provider;

            var isLocal = provider == LlmProviderKind.Local;

            var heading = new Label(isLocal ? "API Key (optional)" : "API Key");
            heading.AddToClassList("molca-hub-bv-option-heading");
            container.Add(heading);

            if (isLocal)
            {
                var localNote = new Label("Local runtimes like Ollama don't require a key — leave this blank. Set one only for a secured or remote endpoint.");
                localNote.AddToClassList("molca-hub-muted");
                container.Add(localNote);
            }

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

        /// <summary>Adds a small group subheading inside the Advanced foldout.</summary>
        private static void AddGroupHeading(VisualElement parent, string text)
        {
            var heading = new Label(text);
            heading.AddToClassList("molca-hub-bv-option-heading");
            parent.Add(heading);
        }

        /// <summary>
        /// A full-width bound <see cref="PropertyField"/> for a non-scalar property (e.g. a list), which the
        /// label+control <see cref="BoundRow"/> layout doesn't suit. Keeps the property's own foldout/label.
        /// </summary>
        private static VisualElement BoundListField(SerializedObject so, string propertyName, string label)
        {
            var property = so.FindProperty(propertyName);
            var field = new PropertyField(property, label);
            field.BindProperty(property);
            return field;
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
