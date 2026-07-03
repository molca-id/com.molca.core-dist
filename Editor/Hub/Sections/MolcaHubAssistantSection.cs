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
        private string _searchKeyDraft = string.Empty;
        private LlmProviderKind _providerAtBuild;
        private WebSearchProviderKind _searchProviderAtBuild;

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
            _searchProviderAtBuild = settings.WebSearchProvider;

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
            // Prompt caching (Sprint 74): mark the stable system+tools prefix cacheable to cut input cost on
            // multi-round turns. Auto = on for cloud, off for Local.
            advanced.Add(BoundRow(so, "promptCaching", "Prompt Caching"));

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

            // Web tools (Sprint 75): outbound egress is OFF by default. When enabled, molca_web_fetch is
            // restricted to the host allowlist; molca_web_search needs a provider + scoped key.
            AddGroupHeading(advanced, "Web");
            advanced.Add(BoundRow(so, "webToolsEnabled", "Enable Web Tools"));
            advanced.Add(BoundListField(so, "webHostAllowlist", "Fetch Host Allowlist"));
            advanced.Add(BoundRow(so, "webSearchProvider", "Search Provider", out var searchProviderField));
            advanced.Add(BoundRow(so, "webSearchMaxResults", "Search Max Results"));
            advanced.Add(BuildWebSearchKeyRow(settings));
            var egressNote = new Label("Off by default: editor network egress is a policy choice. Fetch is limited to the host allowlist; the search key is stored in project-scoped EditorPrefs (never committed).");
            egressNote.AddToClassList("molca-hub-muted");
            egressNote.style.whiteSpace = WhiteSpace.Normal;
            advanced.Add(egressNote);
            // A search-provider change swaps which env var / stored key the key row reflects; rebuild on a real
            // change only (PropertyField re-fires on bind, so guard against the per-frame rebuild loop).
            searchProviderField.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                so.ApplyModifiedProperties();
                if (settings.WebSearchProvider == _searchProviderAtBuild) return;
                EditorUtility.SetDirty(settings);
                Rebuild();
            });

            // Reasoning / extended thinking (Sprint 76): off by default; mapped per vendor and ignored for
            // non-reasoning models and the Local backend. Also selectable from the in-window model picker.
            AddGroupHeading(advanced, "Reasoning");
            advanced.Add(BoundRow(so, "reasoningEffort", "Reasoning Effort"));
            var reasoningNote = new Label("Extended thinking for capable models (Anthropic thinking budget, OpenAI reasoning_effort). Higher = better hard-task answers, more output tokens and latency. Ignored by non-reasoning and Local models.");
            reasoningNote.AddToClassList("molca-hub-muted");
            reasoningNote.style.whiteSpace = WhiteSpace.Normal;
            advanced.Add(reasoningNote);

            // Cross-session project memory (Sprint 77): durable facts under the consumer project, maintained by
            // the assistant via confirmed tools and viewable/deletable here.
            AddGroupHeading(advanced, "Project Memory");
            advanced.Add(BuildMemoryList());

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

        /// <summary>
        /// A scoped-key entry row for the configured web-search provider (Sprint 75), mirroring
        /// <see cref="BuildKeyRow"/>. The key lives in project-scoped EditorPrefs / an env var via
        /// <see cref="AssistantWebAuth"/>, never on the settings asset. Renders nothing actionable when no
        /// search provider is selected.
        /// </summary>
        private VisualElement BuildWebSearchKeyRow(AssistantSettings settings)
        {
            var container = new VisualElement();
            var provider = settings.WebSearchProvider;

            if (provider == WebSearchProviderKind.None)
            {
                var none = new Label("Select a search provider to enable molca_web_search (fetch works without one).");
                none.AddToClassList("molca-hub-muted");
                container.Add(none);
                return container;
            }

            var heading = new Label($"{provider} Search Key");
            heading.AddToClassList("molca-hub-bv-option-heading");
            container.Add(heading);

            if (AssistantWebAuth.IsFromEnv(provider))
            {
                var envNote = new Label($"Using the {AssistantWebAuth.EnvVarFor(provider)} environment variable.");
                envNote.AddToClassList("molca-hub-muted");
                container.Add(envNote);
                return container;
            }

            var row = new VisualElement();
            row.AddToClassList("molca-hub-mcp-token-row");
            container.Add(row);

            var keyField = new TextField { isPasswordField = true, value = _searchKeyDraft };
            keyField.AddToClassList("molca-hub-field-control");
            keyField.RegisterValueChangedCallback(evt => _searchKeyDraft = evt.newValue);
            row.Add(keyField);

            var save = new Button(() =>
            {
                AssistantWebAuth.SetKey(provider, _searchKeyDraft);
                _searchKeyDraft = string.Empty;
                Rebuild();
            })
            { text = "Save", tooltip = "Store the search key in project-scoped EditorPrefs (never committed)." };
            save.AddToClassList("molca-hub-mini-button");
            row.Add(save);

            var clear = new Button(() =>
            {
                AssistantWebAuth.SetKey(provider, string.Empty);
                _searchKeyDraft = string.Empty;
                Rebuild();
            })
            { text = "Clear", tooltip = "Remove the stored search key." };
            clear.AddToClassList("molca-hub-mini-button");
            row.Add(clear);

            var state = new Label(AssistantWebAuth.HasKey(provider) ? "A key is stored for this provider." : "No key stored.");
            state.AddToClassList("molca-hub-muted");
            container.Add(state);

            return container;
        }

        /// <summary>
        /// Builds the read/delete list of cross-session memory entries (Sprint 77): one row per stored fact
        /// (name + description) with a Delete button, or a muted "no entries" line. The store is file-backed
        /// under the consumer project and maintained by the assistant's confirmed memory tools; the Hub only
        /// views and deletes. Rebuilds the card after a delete so the list refreshes.
        /// </summary>
        private VisualElement BuildMemoryList()
        {
            var container = new VisualElement();

            var entries = AssistantMemoryStore.List();
            if (entries.Count == 0)
            {
                var empty = new Label("No memory yet. The assistant saves durable project/user facts here as it learns them.");
                empty.AddToClassList("molca-hub-muted");
                empty.style.whiteSpace = WhiteSpace.Normal;
                container.Add(empty);
                return container;
            }

            foreach (var entry in entries)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;

                var text = new Label(string.IsNullOrWhiteSpace(entry.Description)
                    ? entry.Name
                    : $"{entry.Name} — {entry.Description}")
                {
                    tooltip = entry.Body
                };
                text.style.flexGrow = 1;
                text.style.whiteSpace = WhiteSpace.Normal;
                row.Add(text);

                var name = entry.Name;
                var delete = new Button(() =>
                {
                    AssistantMemoryStore.Delete(name);
                    UnityEditor.AssetDatabase.Refresh();
                    Rebuild();
                })
                { text = "Delete", tooltip = "Remove this memory entry." };
                delete.AddToClassList("molca-hub-mini-button");
                row.Add(delete);

                container.Add(row);
            }

            var note = new Label($"Stored under {AssistantMemoryStore.RelativeRoot} (consumer project, editable by hand). Survives new chats and session deletion.");
            note.AddToClassList("molca-hub-muted");
            note.style.whiteSpace = WhiteSpace.Normal;
            container.Add(note);

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
