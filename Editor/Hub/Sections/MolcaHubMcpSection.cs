using System.IO;
using System.Linq;
using Molca.Editor.UI.Components;
using Molca.Editor.KnowledgeGraph;
using Molca.Editor.Mcp;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// MCP section for the Molca Hub Settings workspace: bridge, auth, proxy, and tool providers.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the MCP rail section is active.
    /// Reuses the existing bridge/auth/proxy/registry services (<see cref="McpServerController"/>,
    /// <see cref="McpAuth"/>, <see cref="McpProxyBuilder"/>, <see cref="McpSettings"/>): the bridge status and
    /// Start/Stop live in the card header, the token stays in env-backed <see cref="McpAuth"/> (never on the SO),
    /// and tool providers are listed once with status + tool count merged into each row.
    /// </remarks>
    internal sealed class MolcaHubMcpSection : VisualElement
    {
        /// <summary>Number of action tools shown per allowlist page (matches the legacy editor).</summary>
        private const int AllowlistPageSize = 12;

        private readonly SerializedObject _editorSettings;

        private bool _revealToken;
        private int _allowlistPage;
        private int _readOnlyPage;

        internal MolcaHubMcpSection()
        {
            AddToClassList("molca-hub-mcp-section");
            _editorSettings = new SerializedObject(MolcaEditorSettings.Instance);
            Build();
        }

        private void Build()
        {
            Clear();

            var intro = new Label("Exposes Molca tooling to MCP clients (Claude Code, Cursor) over an authenticated loopback channel. The TypeScript proxy connects to this bridge.");
            intro.AddToClassList("molca-hub-mcp-intro");
            Add(intro);

            var settingsProperty = _editorSettings.FindProperty("mcpSettings");
            var settings = settingsProperty.objectReferenceValue as McpSettings;

            if (settings == null)
            {
                var card = new MolcaSectionCard("MCP Bridge", "Not configured", MolcaStatusKind.Idle, "No asset");
                var message = new Label("No MCP Settings asset assigned. Create one to enable the bridge and configure tool providers.");
                message.AddToClassList("molca-hub-muted");
                card.Body.Add(message);

                var create = new Button(() =>
                {
                    var created = McpSettings.GetOrCreateSettings();
                    settingsProperty.objectReferenceValue = created;
                    _editorSettings.ApplyModifiedProperties();
                    MolcaEditorSettings.Instance.Save();
                    Build();
                })
                { text = "Create MCP Settings" };
                create.AddToClassList("molca-hub-action-full");
                create.AddToClassList("molca-hub-action-full--primary");
                card.Body.Add(create);

                Add(card);
                return;
            }

            Add(BuildBridgeCard(settings));
            Add(BuildAuthCard());
            Add(BuildProxyCard());
            Add(BuildToolProvidersCard(settings));
            Add(BuildReadOnlyToolsCard(settings));
            Add(BuildAllowlistCard(settings));
            Add(BuildKnowledgeGraphCard());
        }

        // -------------------------------------------------------------------
        // Bridge
        // -------------------------------------------------------------------

        private VisualElement BuildBridgeCard(McpSettings settings)
        {
            var card = new MolcaSectionCard("Bridge");

            var statusDot = new VisualElement();
            statusDot.AddToClassList("molca-hub-status-dot");
            card.AddHeaderAction(statusDot);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-hub-section-card__status-label");
            card.AddHeaderAction(statusLabel);

            var startStop = new Button(() =>
            {
                // Drive the persisted Enabled flag (not Start/Stop directly) so the button, the checkbox,
                // and the listener never disagree, and a manual start survives the next domain reload.
                settings.Enabled = !McpServerController.IsRunning;
                EditorUtility.SetDirty(settings);
                McpServerController.Restart();
            })
            { text = "Start" };
            startStop.AddToClassList("molca-hub-mini-button");
            card.AddHeaderAction(startStop);

            var restart = new Button(() => McpServerController.Restart()) { text = "Restart" };
            restart.AddToClassList("molca-hub-mini-button");
            card.AddHeaderAction(restart);

            var settingsSO = new SerializedObject(settings);

            var enableRow = MakeFieldRow("Enable Bridge", out var enableControl);
            var enableToggle = new Toggle();
            enableToggle.BindProperty(settingsSO.FindProperty("enabled"));
            enableToggle.RegisterValueChangedCallback(_ =>
            {
                settingsSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                McpServerController.Restart();
            });
            enableControl.Add(enableToggle);
            card.Body.Add(enableRow);

            var portRow = MakeFieldRow("Port", out var portControl);
            var portField = new IntegerField();
            portField.AddToClassList("molca-hub-field-control");
            portField.BindProperty(settingsSO.FindProperty("port"));
            portField.RegisterValueChangedCallback(_ =>
            {
                settingsSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                McpServerController.Restart();
            });
            portControl.Add(portField);
            card.Body.Add(portRow);

            // Keep the header status live as the listener starts/stops (Restart is async across a reload).
            void RefreshStatus()
            {
                bool running = McpServerController.IsRunning;
                statusDot.RemoveFromClassList("molca-hub-status-dot--ok");
                statusDot.RemoveFromClassList("molca-hub-status-dot--idle");
                statusDot.AddToClassList(running ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
                statusLabel.text = running ? $"Running on 127.0.0.1:{McpServerController.Port}" : "Stopped";
                startStop.text = running ? "Stop" : "Start";
            }
            RefreshStatus();
            card.schedule.Execute(RefreshStatus).Every(500);

            return card;
        }

        // -------------------------------------------------------------------
        // Auth
        // -------------------------------------------------------------------

        private VisualElement BuildAuthCard()
        {
            var card = new MolcaSectionCard("Authentication");

            var tokenRow = new VisualElement();
            tokenRow.AddToClassList("molca-hub-mcp-token-row");
            card.Body.Add(tokenRow);

            var tokenBox = new Label();
            tokenBox.AddToClassList("molca-hub-mcp-token");
            tokenRow.Add(tokenBox);

            void RefreshToken()
            {
                var token = McpAuth.Token;
                tokenBox.text = _revealToken ? token : new string('•', Mathf.Min(token.Length, 28));
            }

            var reveal = new Button { text = "Reveal", tooltip = "Show or hide the token." };
            reveal.AddToClassList("molca-hub-mini-button");
            reveal.clicked += () =>
            {
                _revealToken = !_revealToken;
                reveal.text = _revealToken ? "Hide" : "Reveal";
                RefreshToken();
            };
            tokenRow.Add(reveal);

            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = McpAuth.Token) { text = "Copy", tooltip = "Copy the token to the clipboard." };
            copy.AddToClassList("molca-hub-mini-button");
            tokenRow.Add(copy);

            var regenerate = new Button(() =>
            {
                if (EditorUtility.DisplayDialog("Regenerate MCP Token",
                    "Regenerating invalidates the current token. Any connected MCP client must be reconfigured with the new token.",
                    "Regenerate", "Cancel"))
                {
                    McpAuth.Regenerate();
                    RefreshToken();
                }
            })
            { text = "Regenerate", tooltip = "Replace the token with a new one." };
            regenerate.AddToClassList("molca-hub-mini-button");
            tokenRow.Add(regenerate);

            var note = new Label("Pass this token to the MCP server via the MOLCA_MCP_TOKEN env var (see the molca-mcp README).");
            note.AddToClassList("molca-hub-muted");
            card.Body.Add(note);

            RefreshToken();
            return card;
        }

        // -------------------------------------------------------------------
        // Proxy
        // -------------------------------------------------------------------

        private VisualElement BuildProxyCard()
        {
            var card = new MolcaSectionCard("TypeScript Proxy");

            var headerStatusDot = new VisualElement();
            headerStatusDot.AddToClassList("molca-hub-status-dot");
            card.AddHeaderAction(headerStatusDot);

            var headerStatus = new Label();
            headerStatus.AddToClassList("molca-hub-section-card__status-label");
            card.AddHeaderAction(headerStatus);

            var build = new Button(() => McpProxyBuilder.Build());
            build.AddToClassList("molca-hub-action-full");
            card.Body.Add(build);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-hub-bv-footer__note");
            card.Body.Add(statusLabel);

            // Build log: streams npm install/build output. Hidden until there is something to show.
            var logScroll = new ScrollView();
            logScroll.AddToClassList("molca-hub-mcp-log");
            var logLabel = new Label { enableRichText = false };
            logLabel.AddToClassList("molca-hub-mcp-log__text");
            logLabel.selection.isSelectable = true;
            logScroll.Add(logLabel);
            card.Body.Add(logScroll);

            void Refresh()
            {
                bool built = McpProxyBuilder.IsBuilt;
                bool building = McpProxyBuilder.IsBuilding;

                headerStatusDot.RemoveFromClassList("molca-hub-status-dot--ok");
                headerStatusDot.RemoveFromClassList("molca-hub-status-dot--idle");
                headerStatusDot.AddToClassList(built ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
                headerStatus.text = built ? "dist/index.js present" : "Not built";

                build.SetEnabled(!building);
                build.text = built ? "Rebuild Proxy (npm install + build)" : "Set Up Proxy (npm install + build)";
                statusLabel.text = building ? McpProxyBuilder.Status
                    : built ? "dist/index.js present." : "Proxy not built yet.";

                var log = McpProxyBuilder.LogText;
                bool hasLog = !string.IsNullOrEmpty(log);
                logScroll.style.display = hasLog ? DisplayStyle.Flex : DisplayStyle.None;
                if (hasLog && logLabel.text != log)
                    logLabel.text = log;
            }
            Refresh();
            card.schedule.Execute(Refresh).Every(500);

            return card;
        }

        // -------------------------------------------------------------------
        // Tool Providers
        // -------------------------------------------------------------------

        private VisualElement BuildToolProvidersCard(McpSettings settings)
        {
            var card = new MolcaSectionCard("Tool Providers");

            var settingsSO = new SerializedObject(settings);
            var providersProperty = settingsSO.FindProperty("providers");

            var add = new Button(() =>
            {
                providersProperty.InsertArrayElementAtIndex(providersProperty.arraySize);
                providersProperty.GetArrayElementAtIndex(providersProperty.arraySize - 1).objectReferenceValue = null;
                settingsSO.ApplyModifiedProperties();
                Build();
            })
            { text = "+", tooltip = "Add a tool provider slot." };
            add.AddToClassList("molca-hub-rt-context__ping");
            card.AddHeaderAction(add);

            if (providersProperty.arraySize == 0)
            {
                var empty = new Label("No tool providers configured.");
                empty.AddToClassList("molca-hub-muted");
                card.Body.Add(empty);
                return card;
            }

            for (int i = 0; i < providersProperty.arraySize; i++)
                card.Body.Add(BuildProviderRow(settingsSO, providersProperty, i));

            return card;
        }

        private VisualElement BuildProviderRow(SerializedObject settingsSO, SerializedProperty providersProperty, int index)
        {
            var element = providersProperty.GetArrayElementAtIndex(index);
            var provider = element.objectReferenceValue as McpToolProvider;

            var row = new VisualElement();
            row.AddToClassList("molca-hub-provider-row");

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-status-dot");
            dot.AddToClassList(provider == null ? "molca-hub-status-dot--error" : StatusDotClass(provider.GetStatus()));
            row.Add(dot);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-provider-row__stack");
            row.Add(stack);

            if (provider == null)
            {
                var missing = new Label($"(empty slot {index})");
                missing.AddToClassList("molca-hub-provider-row__name");
                stack.Add(missing);

                var assign = new ObjectField { objectType = typeof(McpToolProvider), allowSceneObjects = false };
                assign.AddToClassList("molca-hub-provider-row__assign");
                assign.RegisterValueChangedCallback(evt =>
                {
                    element.objectReferenceValue = evt.newValue;
                    settingsSO.ApplyModifiedProperties();
                    EditorUtility.SetDirty(settingsSO.targetObject);
                    Build();
                });
                row.Add(assign);
            }
            else
            {
                var name = new Label($"{provider.DisplayName}  ({provider.Namespace})");
                name.AddToClassList("molca-hub-provider-row__name");
                stack.Add(name);

                int toolCount = provider.GetTools()?.Count() ?? 0;
                var status = new Label($"{provider.GetStatusMessage()} · {toolCount} tools registered");
                status.AddToClassList("molca-hub-provider-row__status");
                stack.Add(status);

                var inspect = new Button(() => EditorGUIUtility.PingObject(provider)) { text = "Inspect", tooltip = "Locate this provider asset." };
                inspect.AddToClassList("molca-hub-mini-button");
                row.Add(inspect);
            }

            var remove = new Button(() =>
            {
                // DeleteArrayElementAtIndex only nulls a non-null object reference on the first call, so
                // clear it first to guarantee the element is actually removed in one action.
                element.objectReferenceValue = null;
                providersProperty.DeleteArrayElementAtIndex(index);
                settingsSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(settingsSO.targetObject);
                Build();
            })
            { text = "−", tooltip = "Remove this provider slot." };
            remove.AddToClassList("molca-hub-rt-context__ping");
            row.Add(remove);

            return row;
        }

        // -------------------------------------------------------------------
        // Knowledge graph (graphify)
        // -------------------------------------------------------------------

        private VisualElement BuildKnowledgeGraphCard()
        {
            var card = new MolcaSectionCard("Knowledge Graph");

            var headerDot = new VisualElement();
            headerDot.AddToClassList("molca-hub-status-dot");
            card.AddHeaderAction(headerDot);

            var headerStatus = new Label();
            headerStatus.AddToClassList("molca-hub-section-card__status-label");
            card.AddHeaderAction(headerStatus);

            var corpus = new Label($"Corpus: {GraphifyCli.CorpusDir}");
            corpus.AddToClassList("molca-hub-muted");
            card.Body.Add(corpus);

            var actions = new VisualElement();
            actions.AddToClassList("molca-hub-bv-actions");
            card.Body.Add(actions);

            var buildButton = new Button(() => GraphifyBuild.Run(full: false));
            buildButton.AddToClassList("molca-hub-bv-action");
            actions.Add(buildButton);

            var fullRebuild = new Button(() => GraphifyBuild.Run(full: true)) { text = "Full Rebuild", tooltip = "Discard the cache and rebuild the whole graph." };
            fullRebuild.AddToClassList("molca-hub-bv-action");
            actions.Add(fullRebuild);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-hub-bv-footer__note");
            card.Body.Add(statusLabel);

            var help = new Label("Builds a graphify knowledge graph of the project (code + assets + docs) so the assistant can answer project-wide questions via molca_kg_query. Requires the graphify CLI on PATH; indexing incurs LLM cost.");
            help.AddToClassList("molca-hub-muted");
            card.Body.Add(help);

            void Refresh()
            {
                bool built = GraphifyCli.GraphExists;
                bool building = GraphifyBuild.IsBuilding;

                headerDot.RemoveFromClassList("molca-hub-status-dot--ok");
                headerDot.RemoveFromClassList("molca-hub-status-dot--idle");
                headerDot.AddToClassList(built ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
                headerStatus.text = built
                    ? $"Built {File.GetLastWriteTime(GraphifyCli.GraphJsonPath):yyyy-MM-dd HH:mm}"
                    : "Not built";

                buildButton.SetEnabled(!building);
                buildButton.text = built ? "Update Graph" : "Build Graph";
                fullRebuild.SetEnabled(built && !building);

                statusLabel.text = GraphifyBuild.Status ?? string.Empty;
                statusLabel.style.display = string.IsNullOrEmpty(GraphifyBuild.Status) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            Refresh();
            card.schedule.Execute(Refresh).Every(500);

            return card;
        }

        // -------------------------------------------------------------------
        // Read-only tools (transparency: always exposed, no gating)
        // -------------------------------------------------------------------

        private VisualElement BuildReadOnlyToolsCard(McpSettings settings)
        {
            var card = new MolcaSectionCard("Read-Only Tools");

            var registry = settings.BuildRegistry();
            var readTools = registry.Tools.Where(t => t.Kind != McpToolKind.Action).ToList();

            if (readTools.Count == 0)
            {
                var none = new Label("No read-only tools are exposed by the configured providers.");
                none.AddToClassList("molca-hub-muted");
                card.Body.Add(none);
                return card;
            }

            var note = new Label($"{readTools.Count} read-only tools — always available to connected clients (no mutation, no confirmation).");
            note.AddToClassList("molca-hub-muted");
            card.Body.Add(note);

            var viewport = new VisualElement();
            viewport.AddToClassList("molca-hub-mcp-allowlist");
            viewport.AddToClassList("molca-hub-mcp-allowlist--readonly");
            var grid = new VisualElement();
            grid.AddToClassList("molca-hub-mcp-allowlist__grid");
            viewport.Add(grid);
            var nav = new VisualElement();
            nav.AddToClassList("molca-hub-mcp-allowlist-nav");

            void RebuildPage()
            {
                int pageCount = Mathf.Max(1, Mathf.CeilToInt(readTools.Count / (float)AllowlistPageSize));
                _readOnlyPage = Mathf.Clamp(_readOnlyPage, 0, pageCount - 1);
                int start = _readOnlyPage * AllowlistPageSize;
                int end = Mathf.Min(start + AllowlistPageSize, readTools.Count);

                grid.Clear();
                for (int i = start; i < end; i++)
                    grid.Add(BuildReadOnlyCell(readTools[i]));

                nav.Clear();
                if (pageCount > 1)
                {
                    var prev = new Button(() => { _readOnlyPage--; RebuildPage(); }) { text = "◄ Prev" };
                    prev.AddToClassList("molca-hub-mini-button");
                    prev.SetEnabled(_readOnlyPage > 0);
                    nav.Add(prev);

                    var pageLabel = new Label($"Page {_readOnlyPage + 1} / {pageCount}   (tools {start + 1}–{end} of {readTools.Count})");
                    pageLabel.AddToClassList("molca-hub-mcp-allowlist-nav__label");
                    nav.Add(pageLabel);

                    var next = new Button(() => { _readOnlyPage++; RebuildPage(); }) { text = "Next ►" };
                    next.AddToClassList("molca-hub-mini-button");
                    next.SetEnabled(_readOnlyPage < pageCount - 1);
                    nav.Add(next);
                }
            }

            card.Body.Add(viewport);
            card.Body.Add(nav);
            RebuildPage();

            return card;
        }

        private static VisualElement BuildReadOnlyCell(McpToolDefinition tool)
        {
            var cell = new VisualElement();
            cell.AddToClassList("molca-hub-mcp-allowlist__cell");

            var name = new Label(tool.Name) { tooltip = tool.Description };
            name.AddToClassList("molca-hub-allow-name");
            cell.Add(name);

            var badges = new VisualElement();
            badges.AddToClassList("molca-hub-allow-badges");
            if (tool.Mode == McpToolMode.Play)
                badges.Add(MakeBadge("play", "molca-hub-allow-badge--play", "Only runs in Play mode."));
            cell.Add(badges);

            return cell;
        }

        // -------------------------------------------------------------------
        // Action-tool allowlist
        // -------------------------------------------------------------------

        private VisualElement BuildAllowlistCard(McpSettings settings)
        {
            var card = new MolcaSectionCard("Action Tools");

            var registry = settings.BuildRegistry();
            var actionTools = registry.Tools.Where(t => t.Kind == McpToolKind.Action).ToList();

            if (actionTools.Count == 0)
            {
                var none = new Label("No action (mutating) tools are exposed by the configured providers.");
                none.AddToClassList("molca-hub-muted");
                card.Body.Add(none);
                return card;
            }

            var settingsSO = new SerializedObject(settings);
            var listProp = settingsSO.FindProperty("actionToolAllowlist");

            var warning = new VisualElement();
            warning.AddToClassList("molca-hub-bv-warning");
            var warnIcon = new Label("⚠");
            warnIcon.AddToClassList("molca-hub-bv-warning__icon");
            warning.Add(warnIcon);
            var warnText = new Label("Action tools mutate the project. Only ticked tools may run, and each run still requires explicit confirmation.");
            warnText.AddToClassList("molca-hub-bv-warning__text");
            warning.Add(warnText);
            card.Body.Add(warning);

            var countLabel = new Label();
            countLabel.AddToClassList("molca-hub-muted");
            void RefreshCount() => countLabel.text = $"{CountAllowed(listProp, actionTools)} / {actionTools.Count} allowed";

            // Fixed-height viewport holds the page height; the inner wrap grid sits at its natural row
            // height (Yoga stretches wrap lines if min-height is on the wrapping element itself).
            var viewport = new VisualElement();
            viewport.AddToClassList("molca-hub-mcp-allowlist");
            viewport.AddToClassList("molca-hub-mcp-allowlist--action");
            var grid = new VisualElement();
            grid.AddToClassList("molca-hub-mcp-allowlist__grid");
            viewport.Add(grid);
            var nav = new VisualElement();
            nav.AddToClassList("molca-hub-mcp-allowlist-nav");

            // Rebuilds the current page's two-column grid + page nav in place (no full section rebuild),
            // so toggling, paging, and All/None keep the user on the same card.
            void RebuildPage()
            {
                int pageCount = Mathf.Max(1, Mathf.CeilToInt(actionTools.Count / (float)AllowlistPageSize));
                _allowlistPage = Mathf.Clamp(_allowlistPage, 0, pageCount - 1);
                int start = _allowlistPage * AllowlistPageSize;
                int end = Mathf.Min(start + AllowlistPageSize, actionTools.Count);

                grid.Clear();
                for (int i = start; i < end; i++)
                    grid.Add(BuildAllowlistCell(settingsSO, listProp, actionTools[i], RefreshCount));

                nav.Clear();
                if (pageCount > 1)
                {
                    var prev = new Button(() => { _allowlistPage--; RebuildPage(); }) { text = "◄ Prev" };
                    prev.AddToClassList("molca-hub-mini-button");
                    prev.SetEnabled(_allowlistPage > 0);
                    nav.Add(prev);

                    var pageLabel = new Label($"Page {_allowlistPage + 1} / {pageCount}   (tools {start + 1}–{end} of {actionTools.Count})");
                    pageLabel.AddToClassList("molca-hub-mcp-allowlist-nav__label");
                    nav.Add(pageLabel);

                    var next = new Button(() => { _allowlistPage++; RebuildPage(); }) { text = "Next ►" };
                    next.AddToClassList("molca-hub-mini-button");
                    next.SetEnabled(_allowlistPage < pageCount - 1);
                    nav.Add(next);
                }
            }

            var all = new Button(() =>
            {
                foreach (var t in actionTools) SetAllowed(settingsSO, listProp, t.Name, true);
                RefreshCount();
                RebuildPage();
            })
            { text = "All", tooltip = "Allow every action tool." };
            all.AddToClassList("molca-hub-mini-button");
            card.AddHeaderAction(all);

            var none2 = new Button(() =>
            {
                for (int i = listProp.arraySize - 1; i >= 0; i--) listProp.DeleteArrayElementAtIndex(i);
                settingsSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                RefreshCount();
                RebuildPage();
            })
            { text = "None", tooltip = "Disallow every action tool." };
            none2.AddToClassList("molca-hub-mini-button");
            card.AddHeaderAction(none2);

            card.Body.Add(countLabel);
            RefreshCount();
            card.Body.Add(viewport);
            card.Body.Add(nav);
            RebuildPage();

            return card;
        }

        private static VisualElement BuildAllowlistCell(
            SerializedObject settingsSO, SerializedProperty listProp, McpToolDefinition tool, System.Action onChanged)
        {
            var cell = new VisualElement();
            cell.AddToClassList("molca-hub-mcp-allowlist__cell");

            var name = new Label(tool.Name) { tooltip = tool.Description };
            name.AddToClassList("molca-hub-allow-name");
            cell.Add(name);

            // Every tool here is an action (the list is filtered to Action kind), so the kind badge would be
            // noise — only the capability badges that vary across tools are shown.
            var badges = new VisualElement();
            badges.AddToClassList("molca-hub-allow-badges");
            if (tool.Reversibility != McpToolReversibility.Irreversible)
                badges.Add(MakeBadge("undoable", "molca-hub-allow-badge--undo", "This action can be reverted."));
            if (tool.Mode == McpToolMode.Play)
                badges.Add(MakeBadge("play", "molca-hub-allow-badge--play", "Only runs in Play mode."));
            cell.Add(badges);

            var toggle = new Toggle { value = IndexInStringList(listProp, tool.Name) >= 0 };
            toggle.AddToClassList("molca-hub-allow-toggle");
            var captured = tool.Name;
            toggle.RegisterValueChangedCallback(evt =>
            {
                SetAllowed(settingsSO, listProp, captured, evt.newValue);
                onChanged?.Invoke();
            });
            cell.Add(toggle);

            return cell;
        }

        private static Label MakeBadge(string text, string variantClass, string tooltip)
        {
            var badge = new Label(text) { tooltip = tooltip };
            badge.AddToClassList("molca-hub-allow-badge");
            badge.AddToClassList(variantClass);
            return badge;
        }

        private static int CountAllowed(SerializedProperty listProp, System.Collections.Generic.List<McpToolDefinition> tools)
        {
            int count = 0;
            foreach (var t in tools)
                if (IndexInStringList(listProp, t.Name) >= 0) count++;
            return count;
        }

        private static int IndexInStringList(SerializedProperty listProp, string value)
        {
            for (int i = 0; i < listProp.arraySize; i++)
                if (listProp.GetArrayElementAtIndex(i).stringValue == value) return i;
            return -1;
        }

        private static void SetAllowed(SerializedObject settingsSO, SerializedProperty listProp, string toolName, bool allowed)
        {
            int index = IndexInStringList(listProp, toolName);
            if (allowed && index < 0)
            {
                listProp.InsertArrayElementAtIndex(listProp.arraySize);
                listProp.GetArrayElementAtIndex(listProp.arraySize - 1).stringValue = toolName;
            }
            else if (!allowed && index >= 0)
            {
                listProp.DeleteArrayElementAtIndex(index);
            }
            else
            {
                return;
            }

            settingsSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(settingsSO.targetObject);
        }

        private static string StatusDotClass(McpProviderStatus status)
        {
            return status switch
            {
                McpProviderStatus.Configured => "molca-hub-status-dot--ok",
                McpProviderStatus.Misconfigured => "molca-hub-status-dot--error",
                _ => "molca-hub-status-dot--idle",
            };
        }

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
