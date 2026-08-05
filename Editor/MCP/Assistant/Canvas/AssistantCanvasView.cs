using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Molca.Editor.Automation;
using Molca.Editor.Remediation;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// One typed artifact extracted from an assistant turn (Sprint 92.3): a fenced block whose info string
    /// names a canvas kind (<c>mermaid</c>, <c>molca-workflow</c>, <c>molca-run</c>, <c>molca-findings</c>).
    /// Identity is the kind plus a stable body hash, so a re-scan after a domain reload (or session switch)
    /// rebinds to the same tab (Sprint 92.4) — the transcript is the source of truth; the canvas never
    /// carries state of its own.
    /// </summary>
    public sealed class AssistantArtifact
    {
        /// <summary>The fence info string (lower-case), e.g. <c>molca-findings</c>.</summary>
        public string Kind { get; }

        /// <summary>The fence body (raw payload — mermaid source or artifact JSON).</summary>
        public string Body { get; }

        /// <summary>Stable id: kind + FNV-1a hash of the body.</summary>
        public string Id { get; }

        /// <summary>Short human tab title derived from the payload.</summary>
        public string Title { get; }

        internal AssistantArtifact(string kind, string body)
        {
            Kind = (kind ?? string.Empty).ToLowerInvariant();
            Body = body ?? string.Empty;
            Id = Kind + ":" + Fnv1a(Body).ToString("x8");
            Title = DeriveTitle(Kind, Body);
        }

        /// <summary>The canvas kinds recognized by the transcript scan (the closed artifact vocabulary).</summary>
        internal static readonly string[] CanvasKinds = { "mermaid", "molca-workflow", "molca-run", "molca-findings" };

        /// <summary>Extracts all canvas artifacts from one turn's markdown text, in order of appearance.</summary>
        internal static List<AssistantArtifact> ExtractAll(string text)
        {
            var result = new List<AssistantArtifact>();
            if (string.IsNullOrEmpty(text)) return result;

            var lines = text.Split('\n');
            string openKind = null;
            StringBuilder body = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    if (openKind == null)
                    {
                        var info = trimmed.Substring(3).Trim().ToLowerInvariant();
                        if (Array.IndexOf(CanvasKinds, info) >= 0)
                        {
                            openKind = info;
                            body = new StringBuilder();
                        }
                        else
                        {
                            openKind = string.Empty; // an ordinary fence — swallow until it closes
                        }
                    }
                    else
                    {
                        if (openKind.Length > 0 && body != null)
                            result.Add(new AssistantArtifact(openKind, body.ToString().TrimEnd('\n')));
                        openKind = null;
                        body = null;
                    }
                    continue;
                }
                if (openKind != null && openKind.Length > 0) body?.Append(line).Append('\n');
            }
            return result;
        }

        private static string DeriveTitle(string kind, string body)
        {
            switch (kind)
            {
                case "mermaid":
                    return "Diagram";
                case "molca-findings":
                {
                    var json = TryParse(body);
                    var title = json?["title"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(title)) return title;
                    var count = (json?["findings"] as JArray)?.Count ?? 0;
                    return count > 0 ? $"Findings ({count})" : "Findings";
                }
                case "molca-workflow":
                    return TryParse(body)?["displayName"]?.ToString() is string name && !string.IsNullOrWhiteSpace(name)
                        ? name : "Workflow";
                case "molca-run":
                    return "Run";
                default:
                    return kind;
            }
        }

        internal static JObject TryParse(string body)
        {
            try { return JObject.Parse(body); }
            catch { return null; }
        }

        private static uint Fnv1a(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in text) { hash ^= c; hash *= 16777619u; }
                return hash;
            }
        }
    }

    /// <summary>
    /// The assistant canvas (Sprint 92): the split-view pane beside the chat that hosts interactive artifact
    /// panels. The transcript stays the conversation of record; the canvas is where artifacts are shown and
    /// operated. Content derives entirely from a transcript scan (<see cref="SyncFromTranscript"/>), so a
    /// domain reload or session switch rebinds by re-scanning — no live object state to lose (Sprint 92.4).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Assistant/Canvas/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Hosted by <see cref="AssistantChatView"/> as the second pane of its <see cref="TwoPaneSplitView"/>.
    /// Panel factories are looked up per artifact kind; unknown kinds fall back to a source view.
    /// </remarks>
    public sealed class AssistantCanvasView : VisualElement
    {
        private static bool _renderersRegistered;

        private readonly VisualElement _tabStrip;
        private readonly ScrollView _content;
        private readonly List<AssistantArtifact> _artifacts = new List<AssistantArtifact>();

        // Built panels, keyed by artifact id. A rebuild (tab switch, or any transcript change) must reuse
        // the existing panel: the proposal panel holds the user's per-step criticality edits and the run
        // panel holds its live poll binding, and both would be silently discarded by rebuilding from the
        // artifact payload. Pruned alongside the artifact list.
        private readonly Dictionary<string, VisualElement> _panelCache = new Dictionary<string, VisualElement>(StringComparer.Ordinal);

        // Dismissed artifact ids for the current session. The canvas is otherwise derived state, so a
        // re-scan would immediately resurrect a closed tab; this is the one piece of user intent it must
        // remember. Persisted per session so a domain reload doesn't undo the tidying, and reopening from the
        // transcript card clears the dismissal.
        private readonly HashSet<string> _dismissed = new HashSet<string>(StringComparer.Ordinal);
        private const string DismissedPrefKey = "Assistant.Canvas.Dismissed";

        private string _activeId;
        private int _scannedTurnCount = -1;
        private string _scannedSessionId;

        // Retained so a restore can re-derive the artifact list without waiting for the next controller event.
        private AssistantChatController _lastController;

        /// <summary>Raised by an inline transcript card asking the host view to open an artifact in the canvas.</summary>
        public static event Action<AssistantArtifact> OpenRequested;

        /// <summary>
        /// Raised by a canvas panel asking the host view to prefill the composer with a message — the
        /// remediation loop's re-entry into the conversation (Sprint 94.3). Mutation never happens from a
        /// panel directly; the assistant proposes and the normal action policy applies.
        /// </summary>
        public static event Action<string> ComposerPrefillRequested;

        /// <summary>Asks the hosting chat view to put <paramref name="text"/> into the composer.</summary>
        internal static void RequestComposerPrefill(string text) => ComposerPrefillRequested?.Invoke(text);

        /// <summary>Creates the canvas pane (empty until <see cref="SyncFromTranscript"/> finds artifacts).</summary>
        public AssistantCanvasView()
        {
            AddToClassList("chat-canvas");
            style.flexGrow = 1;
            style.minWidth = 0;

            var header = new VisualElement();
            header.AddToClassList("chat-canvas__header");
            Add(header);

            _tabStrip = new VisualElement();
            _tabStrip.AddToClassList("chat-canvas__tabs");
            header.Add(_tabStrip);

            _content = new ScrollView();
            _content.AddToClassList("chat-canvas__content");
            Add(_content);
        }

        /// <summary>Number of artifacts currently derived from the transcript.</summary>
        public int ArtifactCount => _artifacts.Count;

        /// <summary>
        /// Registers the inline transcript cards for the <c>molca-*</c> artifact kinds with the shared
        /// Markdown renderer (Sprint 92.3). Mermaid keeps its native inline diagram. Idempotent; called by
        /// the hosting chat view.
        /// </summary>
        public static void EnsureFenceRenderersRegistered()
        {
            if (_renderersRegistered) return;
            _renderersRegistered = true;

            foreach (var kind in new[] { "molca-workflow", "molca-run", "molca-findings" })
            {
                var captured = kind;
                MolcaMarkdown.RegisterFenceRenderer(captured, body => BuildInlineCard(new AssistantArtifact(captured, body)));
            }
        }

        /// <summary>The compact inline representation of an artifact in the transcript: kind, title, open affordance.</summary>
        private static VisualElement BuildInlineCard(AssistantArtifact artifact)
        {
            var card = new VisualElement();
            card.AddToClassList("chat-artifact-card");

            var icon = new Label(IconFor(artifact.Kind));
            icon.AddToClassList("chat-artifact-card__icon");
            card.Add(icon);

            var title = new Label(artifact.Title);
            title.AddToClassList("chat-artifact-card__title");
            card.Add(title);

            var open = new Button(() => OpenRequested?.Invoke(artifact)) { text = "Open in canvas ↗" };
            open.AddToClassList("chat-artifact-card__open");
            card.Add(open);
            return card;
        }

        /// <summary>
        /// Whether <paramref name="commandId"/> is an Action the automation policy has not allowlisted — the
        /// reason a freshly saved workflow is refused under every profile, including UnattendedCi, whose
        /// allowlist is exact.
        /// </summary>
        private static bool NeedsAllowlisting(string commandId)
        {
            var kernel = MolcaAutomationKernel.Instance;
            if (!kernel.TryGetCommand(commandId, out var command) || command.Kind != MolcaCommandKind.Action)
                return false;
            return kernel.Policy is MolcaAutomationPolicy policy && !policy.IsAllowlisted(commandId);
        }

        /// <summary>Whether <paramref name="commandId"/> is an Action command the policy currently allowlists.</summary>
        private static bool IsAuthorizedAction(string commandId)
        {
            var kernel = MolcaAutomationKernel.Instance;
            return kernel.TryGetCommand(commandId, out var command)
                   && command.Kind == MolcaCommandKind.Action
                   && kernel.Policy is MolcaAutomationPolicy policy
                   && policy.IsAllowlisted(commandId);
        }

        /// <summary>
        /// The authorization control for one action command: <b>Authorize</b> when it is not allowlisted,
        /// <b>Revoke</b> when it is. Granting is explicit and confirmed — nothing in the save or run path may
        /// allowlist on the user's behalf, because that would let a model widen its own permissions. Revoking
        /// is offered wherever granting is, and deliberately without a confirmation dialog: it narrows
        /// permissions, is the safe direction, and is undone by the same button. Making revocation harder to
        /// reach than authorization would be the wrong bias.
        /// </summary>
        private static Button BuildAuthorizationButton(string commandId, Action onDone)
        {
            if (IsAuthorizedAction(commandId))
            {
                var revoke = new Button(() =>
                {
                    MolcaAutomationPolicySettings.GetOrCreateSettings().SetActionAllowed(commandId, false);
                    MolcaAutomationKernel.Instance.Rebuild();
                    onDone?.Invoke();
                })
                {
                    text = "Revoke authorization",
                    tooltip = $"Removes '{commandId}' from the automation action allowlist; runs are refused again."
                };
                revoke.AddToClassList("chat-mini-button");
                return revoke;
            }

            var button = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Authorize workflow",
                        $"Add '{commandId}' to the automation action allowlist?\n\n"
                        + "It will then be permitted to run as an action under the active policy profile. "
                        + "You can revoke this here, or in Hub → Automation → Permissions.",
                        "Add to allowlist", "Cancel"))
                    return;
                MolcaAutomationPolicySettings.GetOrCreateSettings().SetActionAllowed(commandId, true);
                MolcaAutomationKernel.Instance.Rebuild();
                onDone?.Invoke();
            })
            {
                text = "Authorize this workflow…",
                tooltip = "Adds this workflow's command id to the automation action allowlist, after you confirm."
            };
            button.AddToClassList("chat-mini-button");
            return button;
        }

        private static string IconFor(string kind) => kind switch
        {
            "mermaid" => "◆",
            "molca-workflow" => "▶",
            "molca-run" => "◐",
            "molca-findings" => "⚠",
            _ => "▣"
        };

        /// <summary>
        /// Re-derives the artifact set from the controller's committed turns. Cheap when nothing changed
        /// (keyed on session id + turn count). Returns true when a new artifact appeared — the host uses
        /// that to auto-open the pane.
        /// </summary>
        public bool SyncFromTranscript(AssistantChatController controller)
        {
            if (controller == null) return false;
            _lastController = controller;
            var transcript = controller.Transcript;
            var sessionId = controller.CurrentSessionId ?? string.Empty;
            if (transcript.Count == _scannedTurnCount && sessionId == _scannedSessionId) return false;
            // Dismissals belong to the session they were made in; switching sessions loads that one's set.
            if (sessionId != _scannedSessionId) LoadDismissed(sessionId);
            _scannedTurnCount = transcript.Count;
            _scannedSessionId = sessionId;

            var previousCount = _artifacts.Count;
            var previousActive = _activeId;
            _artifacts.Clear();
            var seen = new HashSet<string>();
            foreach (var turn in transcript)
            {
                if (turn.Kind != ChatTurnKind.Assistant) continue;
                foreach (var artifact in AssistantArtifact.ExtractAll(turn.Text))
                    if (seen.Add(artifact.Id) && !_dismissed.Contains(artifact.Id))
                        _artifacts.Add(artifact);
            }

            // Keep the active tab when it survived the re-scan; otherwise favor the newest artifact.
            _activeId = null;
            if (previousActive != null)
                foreach (var a in _artifacts)
                    if (a.Id == previousActive) { _activeId = previousActive; break; }
            if (_activeId == null && _artifacts.Count > 0)
                _activeId = _artifacts[_artifacts.Count - 1].Id;

            PrunePanelCache();
            Rebuild();
            return _artifacts.Count > previousCount;
        }

        /// <summary>
        /// Selects (or adds, if the transcript scan already knows it) the given artifact's tab. Reopening a
        /// previously closed artifact clears its dismissal — the transcript card is how a closed tab comes
        /// back.
        /// </summary>
        public void ShowArtifact(AssistantArtifact artifact)
        {
            if (artifact == null) return;
            if (_dismissed.Remove(artifact.Id)) SaveDismissed();
            var known = false;
            foreach (var a in _artifacts)
                if (a.Id == artifact.Id) { known = true; break; }
            if (!known) _artifacts.Add(artifact);
            _activeId = artifact.Id;
            Rebuild();
        }

        /// <summary>
        /// Closes an artifact's tab. This hides a view — it never cancels work: a workflow run keeps going,
        /// and the artifact is still in the transcript, so its inline card reopens it.
        /// </summary>
        /// <param name="artifactId">The artifact id to dismiss.</param>
        public void DismissArtifact(string artifactId)
        {
            if (string.IsNullOrEmpty(artifactId)) return;
            if (!_dismissed.Add(artifactId)) return;
            SaveDismissed();

            _artifacts.RemoveAll(a => a.Id == artifactId);
            if (_panelCache.TryGetValue(artifactId, out var panel))
            {
                panel?.RemoveFromHierarchy();
                _panelCache.Remove(artifactId);
            }
            if (_activeId == artifactId)
                _activeId = _artifacts.Count > 0 ? _artifacts[_artifacts.Count - 1].Id : null;
            Rebuild();
        }

        /// <summary>
        /// Reopens every artifact closed in this session by re-scanning the transcript — the dismissals were
        /// the only reason they were absent, so clearing them and re-deriving is the whole operation.
        /// </summary>
        public void RestoreDismissed()
        {
            if (_dismissed.Count == 0) return;
            _dismissed.Clear();
            SaveDismissed();
            _scannedTurnCount = -1; // invalidate the scan cache so the re-sync actually re-reads
            if (_lastController != null) SyncFromTranscript(_lastController);
            else Rebuild();
        }

        private string DismissedPrefKeyForSession =>
            DismissedPrefKey + "." + (string.IsNullOrEmpty(_scannedSessionId) ? "current" : _scannedSessionId);

        private void LoadDismissed(string sessionId)
        {
            _dismissed.Clear();
            var key = DismissedPrefKey + "." + (string.IsNullOrEmpty(sessionId) ? "current" : sessionId);
            var stored = MolcaEditorPrefs.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(stored)) return;
            foreach (var id in stored.Split('\n'))
                if (!string.IsNullOrWhiteSpace(id)) _dismissed.Add(id);
        }

        private void SaveDismissed() =>
            MolcaEditorPrefs.SetString(DismissedPrefKeyForSession, string.Join("\n", _dismissed));

        private void Rebuild()
        {
            _tabStrip.Clear();
            _content.Clear();

            if (_artifacts.Count == 0)
            {
                var empty = new Label(_dismissed.Count > 0
                    ? "All artifacts in this chat are closed. Reopen one from its card in the conversation, or restore them all."
                    : "No artifacts yet. Diagrams, workflows, runs, and findings the assistant produces appear here.");
                empty.AddToClassList("chat-canvas__empty");
                _content.Add(empty);
                if (_dismissed.Count > 0) _tabStrip.Add(BuildRestoreButton());
                return;
            }

            AssistantArtifact active = null;
            foreach (var artifact in _artifacts)
            {
                var captured = artifact;

                // A container, not a Button: Button derives from TextElement and paints its own text, so a
                // child added inside one overlaps that text instead of sitting beside it. The label and the
                // close affordance are siblings here, and the row selects on click unless the close button
                // was the target (same guard the transcript's disclosure rows use).
                var tab = new VisualElement { tooltip = artifact.Kind };
                tab.AddToClassList("chat-canvas__tab");
                tab.RegisterCallback<ClickEvent>(evt =>
                {
                    if (evt.target is Button) return;
                    _activeId = captured.Id;
                    Rebuild();
                });

                var label = new Label($"{IconFor(artifact.Kind)} {artifact.Title}");
                label.AddToClassList("chat-canvas__tab-label");
                tab.Add(label);

                var close = new Button(() => DismissArtifact(captured.Id)) { text = "×" };
                close.AddToClassList("chat-canvas__tab-close");
                close.tooltip = "Close this artifact (the run is not cancelled; reopen it from the conversation)";
                tab.Add(close);

                if (artifact.Id == _activeId)
                {
                    tab.AddToClassList("chat-canvas__tab--active");
                    active = artifact;
                }
                _tabStrip.Add(tab);
            }
            if (_dismissed.Count > 0) _tabStrip.Add(BuildRestoreButton());
            active ??= _artifacts[_artifacts.Count - 1];

            if (!_panelCache.TryGetValue(active.Id, out var panel) || panel == null)
            {
                panel = BuildPanel(active);
                _panelCache[active.Id] = panel;
            }
            _content.Add(panel);
        }

        /// <summary>A "+N closed" chip restoring every artifact dismissed in this session.</summary>
        private Button BuildRestoreButton()
        {
            var restore = new Button(RestoreDismissed)
            {
                text = $"+{_dismissed.Count} closed",
                tooltip = "Reopen the artifacts closed in this chat."
            };
            restore.AddToClassList("chat-canvas__tab");
            restore.AddToClassList("chat-canvas__tab--restore");
            return restore;
        }

        /// <summary>Drops cached panels whose artifacts left the transcript (session switch, compaction).</summary>
        private void PrunePanelCache()
        {
            if (_panelCache.Count == 0) return;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in _artifacts) live.Add(artifact.Id);

            var stale = new List<string>();
            foreach (var id in _panelCache.Keys)
                if (!live.Contains(id)) stale.Add(id);
            foreach (var id in stale)
            {
                _panelCache[id]?.RemoveFromHierarchy();
                _panelCache.Remove(id);
            }
        }

        /// <summary>Builds the full canvas panel for one artifact kind.</summary>
        private static VisualElement BuildPanel(AssistantArtifact artifact)
        {
            switch (artifact.Kind)
            {
                case "mermaid":
                    return MolcaMermaid.Create(artifact.Body);
                case "molca-findings":
                    return BuildFindingsPanel(artifact);
                case "molca-workflow":
                    return new WorkflowProposalPanel(artifact);
                case "molca-run":
                    var runPayload = AssistantArtifact.TryParse(artifact.Body);
                    var runId = runPayload?["runId"]?.ToString();
                    // commandId is optional but valuable: a run that has left the store (reload, eviction)
                    // cannot be looked up, and without the command id the panel has nothing actionable to
                    // offer — not even the authorization step that is usually the reason it never ran.
                    var runCommandId = runPayload?["commandId"]?.ToString();
                    return string.IsNullOrWhiteSpace(runId)
                        ? BuildSourcePanel(artifact, "The run payload names no runId.")
                        : new RunBindingPanel(runId, runCommandId);
                default:
                    return BuildSourcePanel(artifact, null);
            }
        }

        /// <summary>
        /// The interactive workflow proposal (Sprint 94.1): validation state and kernel-aggregated facets
        /// up top, one row per step (command, kind/mode badges, criticality toggle), then Save / Save &amp;
        /// Run / Revise-in-chat actions. The panel edits a local copy of the composition — the transcript
        /// artifact stays the immutable record; saving/running uses the edited copy through the store and
        /// the kernel (policy, confirmation, audit all apply).
        /// </summary>
        private sealed class WorkflowProposalPanel : VisualElement
        {
            private readonly MolcaComposedWorkflow _workflow;
            private VisualElement _body;

            public WorkflowProposalPanel(AssistantArtifact artifact)
            {
                AddToClassList("chat-proposal");
                _workflow = MolcaComposedWorkflow.Parse(artifact.Body);
                Rebuild();
            }

            private void Rebuild()
            {
                Clear();
                if (_workflow == null)
                {
                    Add(new Label("The workflow payload is not a valid composition object."));
                    return;
                }

                var kernel = MolcaAutomationKernel.Instance;
                var validation = MolcaComposedWorkflowCompiler.Validate(_workflow, kernel.Registry);
                var facets = validation.Facets;

                var title = new Label(string.IsNullOrWhiteSpace(_workflow.DisplayName) ? _workflow.Id : _workflow.DisplayName);
                title.AddToClassList("chat-proposal__title");
                Add(title);
                if (!string.IsNullOrWhiteSpace(_workflow.Description))
                {
                    var description = new Label(_workflow.Description);
                    description.AddToClassList("chat-canvas__note");
                    Add(description);
                }

                // Facet summary — what running this composition means, in one line.
                var facetLine = new Label(
                    $"{facets.Kind} · mode {facets.Mode} · revert {facets.Reversibility}" +
                    (facets.RequiresConfirmation ? " · confirms before running" : string.Empty));
                facetLine.AddToClassList("chat-proposal__facets");
                facetLine.tooltip = "Claims: " + string.Join(", ", facets.ResourceClaims);
                Add(facetLine);

                foreach (var issue in validation.Issues)
                {
                    var row = new Label($"✗ {issue.Message}");
                    row.AddToClassList("chat-proposal__issue");
                    Add(row);
                }

                // Authorization is separate from validity: a valid Action workflow is still refused until its
                // command id is allowlisted (exactly so under UnattendedCi). Show the state either way —
                // authorized or not — so the panel is also where the grant can be taken back.
                if (validation.IsValid && facets.Kind == MolcaCommandKind.Action && !string.IsNullOrWhiteSpace(_workflow.Id)
                    && kernel.Policy is MolcaAutomationPolicy policy)
                {
                    var authorized = policy.IsAllowlisted(_workflow.Id);
                    var line = new Label(authorized
                        ? $"Authorized: '{_workflow.Id}' is in the automation action allowlist and may run under the active profile."
                        : $"Not yet authorized: '{_workflow.Id}' is an action and is not in the automation action "
                          + "allowlist, so a run is refused under every profile — raising the profile does not help, "
                          + "because the CI profile's allowlist is exact.");
                    line.AddToClassList("chat-proposal__issue");
                    line.AddToClassList(authorized ? "chat-proposal__issue--ok" : "chat-proposal__issue--warn");
                    line.style.whiteSpace = WhiteSpace.Normal;
                    Add(line);
                    Add(BuildAuthorizationButton(_workflow.Id, onDone: Rebuild));
                }

                _body = new VisualElement();
                Add(_body);
                for (var i = 0; i < _workflow.Steps.Count; i++)
                    _body.Add(BuildStepRow(i, kernel));

                var actions = new VisualElement();
                actions.AddToClassList("chat-proposal__actions");
                Add(actions);

                var save = new Button(() => SaveWorkflow(runAfterSave: false)) { text = "Save" };
                save.AddToClassList("chat-mini-button");
                save.SetEnabled(validation.IsValid);
                actions.Add(save);

                var run = new Button(() => SaveWorkflow(runAfterSave: true)) { text = "Save & Run" };
                run.AddToClassList("chat-mini-button");
                run.SetEnabled(validation.IsValid);
                actions.Add(run);

                var revise = new Button(() => RequestComposerPrefill(
                    $"Revise the '{_workflow.Id}' workflow proposal: ")) { text = "Revise in chat" };
                revise.AddToClassList("chat-mini-button");
                actions.Add(revise);
            }

            private VisualElement BuildStepRow(int index, MolcaAutomationKernel kernel)
            {
                var step = _workflow.Steps[index];
                var row = new VisualElement();
                row.AddToClassList("chat-proposal__step");

                var known = kernel.TryGetCommand(step.CommandId, out var command);
                var label = new Label($"{index + 1}. {(known ? command.DisplayName : step.CommandId)}");
                label.AddToClassList("chat-proposal__step-label");
                label.tooltip = known
                    ? $"{step.CommandId} · {command.Kind} · mode {command.Mode} · revert {command.Reversibility}" +
                      (step.Args != null ? $"\nargs: {step.Args.ToString(Newtonsoft.Json.Formatting.None)}" : string.Empty)
                    : $"{step.CommandId} — not registered";
                if (!known) label.AddToClassList("chat-proposal__step-label--unknown");
                row.Add(label);

                if (known && command.Kind == MolcaCommandKind.Action)
                {
                    var badge = new Label(command.Reversibility == MolcaCommandReversibility.None ? "action · irreversible" : "action");
                    badge.AddToClassList("chat-proposal__badge");
                    if (command.Reversibility == MolcaCommandReversibility.None)
                        badge.AddToClassList("chat-proposal__badge--irreversible");
                    row.Add(badge);
                }

                var critical = new Toggle("critical") { value = step.Critical };
                critical.AddToClassList("chat-proposal__critical");
                critical.tooltip = "A critical step's failure halts the workflow; a non-critical failure is recorded and the run continues.";
                critical.RegisterValueChangedCallback(evt => step.Critical = evt.newValue);
                row.Add(critical);
                return row;
            }

            private void SaveWorkflow(bool runAfterSave)
            {
                var kernel = MolcaAutomationKernel.Instance;
                var validation = MolcaComposedWorkflowCompiler.Validate(_workflow, kernel.Registry);
                if (!validation.IsValid) { Rebuild(); return; }

                if (!MolcaComposedWorkflowStore.Save(_workflow, validation.Facets, out var error))
                {
                    EditorUtility.DisplayDialog("Save workflow", error, "OK");
                    return;
                }
                AssetDatabase.Refresh();
                kernel.Rebuild();

                if (!runAfterSave) { Rebuild(); return; }

                // One confirmation for the whole composition — that is exactly why aggregation escalates
                // RequiresConfirmation for any irreversible member (Sprint 93.2).
                var confirmed = !validation.Facets.RequiresConfirmation
                    || EditorUtility.DisplayDialog("Run workflow",
                        $"'{_workflow.Id}' contains an irreversible or confirmation-requiring step. Run it?",
                        "Run", "Cancel");
                if (!confirmed) return;

                var runId = Guid.NewGuid().ToString();
                StartDetached(kernel, _workflow.Id, runId, confirmed);

                Clear();
                Add(new RunBindingPanel(runId, _workflow.Id));
            }

            /// <summary>Fire-and-poll: the panel binds to the run id; the run owns its exceptions.</summary>
            private static async void StartDetached(MolcaAutomationKernel kernel, string commandId, string runId, bool confirmed)
            {
                try
                {
                    await kernel.InvokeAsync(commandId, new JObject(), MolcaTransport.Assistant,
                        isConfirmed: confirmed, runId: runId);
                }
                catch (OperationCanceledException) { /* cancelled from the run panel — not an error */ }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        /// <summary>
        /// The live run binding (Sprint 94.2): polls the kernel run store by run id (the journal-backed
        /// source of truth — never live workflow objects, per the 93.6 contract), rendering status,
        /// progress, and, once terminal, the per-step outcomes. Offers Cancel while active and
        /// "Diagnose &amp; fix" on failure, which re-enters the conversation with the failed steps'
        /// diagnostic codes (Sprint 94.3) — the assistant proposes fixes; policy applies as always.
        /// A reload mid-run surfaces as Interrupted, reported truthfully.
        /// </summary>
        private sealed class RunBindingPanel : VisualElement
        {
            private readonly string _runId;
            private readonly string _commandId;
            private IVisualElementScheduledItem _poll;
            private MolcaCommandStatus? _renderedStatus;
            private string _renderedProgress;

            public RunBindingPanel(string runId, string commandId = null)
            {
                _runId = runId;
                _commandId = commandId;
                AddToClassList("chat-run");
                // The panel is cached and re-parented on every canvas rebuild, so the poller is created once
                // and resumed/paused across attach cycles — scheduling a new item per attach would leave a
                // paused item behind on the element's scheduler each time.
                RegisterCallback<AttachToPanelEvent>(_ =>
                {
                    Render();
                    if (_poll == null) _poll = schedule.Execute(Render).Every(500);
                    else _poll.Resume();
                });
                RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause());
            }

            private void Render()
            {
                var kernel = MolcaAutomationKernel.InstanceOrNull ?? MolcaAutomationKernel.Instance;
                if (!kernel.TryGetRun(_runId, out var handle) || handle == null)
                {
                    RenderMissing();
                    return;
                }

                var progressText = handle.Progress is { } p ? $"{p.Message}|{p.StepIndex}" : null;
                if (_renderedStatus == handle.Status && _renderedProgress == progressText) return;
                _renderedStatus = handle.Status;
                _renderedProgress = progressText;

                Clear();
                var title = new Label($"Run {handle.CommandId}");
                title.AddToClassList("chat-proposal__title");
                Add(title);

                var status = new Label(StatusLine(handle.Status));
                status.AddToClassList("chat-run__status");
                status.AddToClassList("chat-run__status--" + handle.Status.ToString().ToLowerInvariant());
                Add(status);

                if (!handle.IsTerminal)
                {
                    if (handle.Progress is { } progress)
                    {
                        var bar = new ProgressBar
                        {
                            value = progress.IsIndeterminate ? 0f : progress.Fraction * 100f,
                            title = progress.StepCount > 0
                                ? $"{progress.StepIndex + 1}/{progress.StepCount} · {progress.Message}"
                                : progress.Message
                        };
                        Add(bar);
                    }
                    var cancel = new Button(() => kernel.Cancel(_runId)) { text = "Cancel" };
                    cancel.AddToClassList("chat-mini-button");
                    Add(cancel);
                    return;
                }

                _poll?.Pause();
                if (handle.Result?.Data?["steps"] is JArray steps)
                    foreach (var token in steps)
                    {
                        if (token is not JObject step) continue;
                        var passed = step["passed"]?.Type == JTokenType.Boolean && (bool)step["passed"];
                        var row = new Label($"{(passed ? "✓" : "✗")} {step["id"]}  ·  {step["description"]}");
                        row.AddToClassList(passed ? "chat-run__step--pass" : "chat-run__step--fail");
                        Add(row);
                    }

                // The reason, always. A refusal or a block happens before any step runs, so without this the
                // panel showed a bare "Refused by policy" with nothing saying which gate rejected it or how
                // to satisfy it.
                if (handle.Result?.Diagnostics != null)
                    foreach (var diagnostic in handle.Result.Diagnostics)
                    {
                        var row = new Label($"[{diagnostic.Code}] {diagnostic.Message}");
                        row.AddToClassList("chat-run__diagnostic");
                        row.style.whiteSpace = WhiteSpace.Normal;
                        Add(row);
                    }

                // A not-allowlisted action is the one refusal the user can fix from here, so offer the
                // explicit consent step rather than making them hunt through settings.
                if (handle.Status == MolcaCommandStatus.Refused && NeedsAllowlisting(handle.CommandId))
                    Add(BuildAuthorizationButton(handle.CommandId, onDone: () =>
                    {
                        _renderedStatus = null; // force a re-render of the (now authorized) state
                        Render();
                    }));

                if (handle.Status == MolcaCommandStatus.Failed)
                {
                    var diagnose = new Button(() => RequestComposerPrefill(BuildDiagnosePrompt(handle)))
                    {
                        text = "Diagnose & fix",
                        tooltip = "Hands the failed steps' diagnostic codes to the assistant to map through the fix registry (dry-run first)."
                    };
                    diagnose.AddToClassList("chat-mini-button");
                    Add(diagnose);
                }
            }

            private void RenderMissing()
            {
                if (_renderedStatus != null) return; // keep the last real render if the store evicted the run
                Clear();
                Add(new Label($"Run {_runId} is not in this session's run store — the editor may have reloaded mid-run (Interrupted) or the entry was evicted. Ask for molca_workflow_status to check the journal.")
                {
                    style = { whiteSpace = WhiteSpace.Normal }
                });

                // The commonest reason a run vanished without a trace is that it was refused before it ever
                // entered the store. If the artifact named its command, offer the fix rather than a dead end.
                if (!string.IsNullOrWhiteSpace(_commandId) && NeedsAllowlisting(_commandId))
                {
                    var note = new Label($"'{_commandId}' is also not in the action allowlist, so a run would be refused under every profile.");
                    note.AddToClassList("chat-run__diagnostic");
                    note.style.whiteSpace = WhiteSpace.Normal;
                    Add(note);
                    Add(BuildAuthorizationButton(_commandId, onDone: () => { _renderedStatus = null; Render(); }));
                }
                _poll?.Pause();
            }

            private static string StatusLine(MolcaCommandStatus status) => status switch
            {
                MolcaCommandStatus.Succeeded => "✓ Succeeded",
                MolcaCommandStatus.Failed => "✗ Failed",
                MolcaCommandStatus.Cancelled => "⊘ Cancelled",
                MolcaCommandStatus.Refused => "✗ Refused by policy",
                MolcaCommandStatus.NeedsConfirmation => "… Needs confirmation",
                MolcaCommandStatus.Interrupted => "⊘ Interrupted (editor reloaded mid-run; not resumed)",
                MolcaCommandStatus.Blocked => "… Blocked on resources",
                _ => "◐ " + status
            };

            /// <summary>The remediation re-entry prompt: failed steps and their stable diagnostic codes.</summary>
            private static string BuildDiagnosePrompt(MolcaRunHandle handle)
            {
                var sb = new StringBuilder();
                sb.Append($"The workflow run '{handle.CommandId}' failed. ");
                if (handle.Result?.Data?["steps"] is JArray steps)
                {
                    foreach (var token in steps)
                    {
                        if (token is not JObject step) continue;
                        if (step["passed"]?.Type == JTokenType.Boolean && (bool)step["passed"]) continue;
                        sb.Append($"Step '{step["id"]}' failed with: ");
                        if (step["diagnostics"] is JArray diagnostics)
                            foreach (var d in diagnostics)
                                sb.Append($"[{d?["code"]}] {d?["message"]} ");
                    }
                }
                sb.Append("Map these finding codes through the fix registry (molca_remediation_plan first, dry-run), "
                        + "propose the remediation, and after applying re-run the failed check.");
                return sb.ToString();
            }
        }

        private static VisualElement BuildSourcePanel(AssistantArtifact artifact, string note)
        {
            var panel = new VisualElement();
            if (!string.IsNullOrEmpty(note))
            {
                var noteLabel = new Label(note);
                noteLabel.AddToClassList("chat-canvas__note");
                panel.Add(noteLabel);
            }
            MolcaMarkdown.Render(panel, "```json\n" + artifact.Body + "\n```");
            return panel;
        }

        /// <summary>
        /// The findings panel (Sprint 92.5): one row per finding (severity glyph, code, path, message) with
        /// the registered fix surfaced read-only — fix id + facets; Apply arrives with the workflow runner
        /// (Sprint 94.3), always through the remediation pass, never from this panel's render path.
        /// </summary>
        private static VisualElement BuildFindingsPanel(AssistantArtifact artifact)
        {
            var panel = new VisualElement();
            panel.AddToClassList("chat-findings");

            var json = AssistantArtifact.TryParse(artifact.Body);
            var findings = json?["findings"] as JArray;
            if (findings == null || findings.Count == 0)
            {
                panel.Add(new Label("The findings payload is empty or malformed.") { });
                MolcaMarkdown.Render(panel, "```json\n" + artifact.Body + "\n```");
                return panel;
            }

            foreach (var token in findings)
            {
                if (token is not JObject finding) continue;
                var code = finding["code"]?.ToString() ?? string.Empty;
                var severity = (finding["severity"]?.ToString() ?? "info").ToLowerInvariant();
                var path = finding["path"]?.ToString();
                var message = finding["message"]?.ToString() ?? string.Empty;

                var row = new VisualElement();
                row.AddToClassList("chat-findings__row");
                row.AddToClassList("chat-findings__row--" + severity);

                var glyph = new Label(severity == "error" ? "✗" : severity == "warning" ? "⚠" : "ℹ");
                glyph.AddToClassList("chat-findings__glyph");
                row.Add(glyph);

                var body = new VisualElement();
                body.AddToClassList("chat-findings__body");
                row.Add(body);

                var head = new Label(string.IsNullOrEmpty(path) ? code : $"{code}  ·  {path}");
                head.AddToClassList("chat-findings__code");
                body.Add(head);

                if (!string.IsNullOrEmpty(message))
                {
                    var msg = new Label(message);
                    msg.AddToClassList("chat-findings__message");
                    body.Add(msg);
                }

                // Fix surfacing: which registered fix would remediate this code, and its facets. Applying
                // is never done from this render path — the button re-enters the conversation so the
                // assistant runs the remediation pass under the normal action policy (Sprint 94.3).
                var fixes = MolcaFixRegistry.FixesFor(code);
                if (fixes != null && fixes.Count > 0)
                {
                    foreach (var fix in fixes)
                    {
                        var facets = $"{(fix.IsDeterministic ? "deterministic" : "needs input")}, " +
                                     $"{(fix.IsDestructive ? "destructive" : "non-destructive")}, {fix.Reversibility}";
                        var fixLabel = new Label($"🔧 {fix.Id} — {fix.Description}  ({facets})");
                        fixLabel.AddToClassList("chat-findings__fix");
                        body.Add(fixLabel);
                    }

                    var capturedCode = code;
                    var capturedPath = path;
                    var ask = new Button(() => RequestComposerPrefill(
                        $"Remediate finding '{capturedCode}'" +
                        (string.IsNullOrEmpty(capturedPath) ? "" : $" at {capturedPath}") +
                        " — plan it with molca_remediation_plan (dry-run) first, then apply the fix and re-check."))
                    {
                        text = "Ask assistant to fix",
                        tooltip = "Prefills a remediation request in the chat; the fix runs through the remediation pass and action policy."
                    };
                    ask.AddToClassList("chat-mini-button");
                    body.Add(ask);
                }

                panel.Add(row);
            }
            return panel;
        }
    }
}
