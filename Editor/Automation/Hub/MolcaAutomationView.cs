using System.Linq;
using Molca.Editor.UI.Components;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Molca.Editor.Automation.Hub
{
    /// <summary>
    /// The Molca Hub <b>Automation</b> workspace content (§12), laid out as a left navigation rail
    /// (<see cref="MolcaRail"/>) with a detail pane: Overview, Workflows, Permissions, History, and
    /// Capabilities. Only the selected section renders, so the tab stays compact as sections grow. Styled
    /// with the shared Molca editor card/field vocabulary. Editor-only; main thread.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Automation/Hub/</c>. Rebuilt on demand each time the tab
    /// is selected; Refresh rebuilds the kernel registry and re-renders the current section.
    /// </remarks>
    public sealed class MolcaAutomationView : VisualElement
    {
        private readonly MolcaRail _rail = new MolcaRail();
        // ScrollView so a tall section scrolls instead of flex-compressing its rows (which overlapped text).
        private readonly ScrollView _detail = new ScrollView { style = { flexGrow = 1, minHeight = 0, paddingLeft = 12 } };

        /// <summary>Builds the Automation workspace view.</summary>
        public MolcaAutomationView()
        {
            style.flexGrow = 1;
            style.paddingLeft = 12;
            style.paddingRight = 12;
            style.paddingTop = 10;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 10 } };
            var title = new Label("Automation");
            title.AddToClassList("molca-hub-title");
            title.style.flexGrow = 1;
            header.Add(title);
            header.Add(MolcaButtons.Mini("Refresh", Refresh));
            Add(header);

            var body = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1, minHeight = 0 } };
            _rail.AddItem("overview", "Overview");
            _rail.AddItem("workflows", "Workflows");
            _rail.AddItem("permissions", "Permissions");
            _rail.AddItem("history", "History");
            _rail.AddItem("capabilities", "Capabilities");
            _rail.OnSelected += _ => ShowSelected();
            body.Add(_rail);
            body.Add(_detail);
            Add(body);

            MolcaAutomationKernel.Instance.Rebuild();
            _rail.Select("overview");
        }

        private void Refresh()
        {
            MolcaAutomationKernel.Instance.Rebuild();
            ShowSelected();
        }

        private void ShowSelected()
        {
            _detail.Clear();
            switch (_rail.SelectedKey)
            {
                case "workflows": _detail.Add(BuildWorkflows()); break;
                case "permissions": _detail.Add(BuildPermissions()); break;
                case "history": _detail.Add(BuildHistory()); break;
                case "capabilities": _detail.Add(BuildCapabilities()); break;
                default: _detail.Add(BuildOverview()); break;
            }
        }

        // ---- Overview ---------------------------------------------------------------------------

        private VisualElement BuildOverview()
        {
            var card = Card("Overview", "Automation kernel status", out _);
            var body = CardBody(card);
            var kernel = MolcaAutomationKernel.Instance;
            var status = kernel.StatusJson();
            var coord = status["coordinator"];

            body.Add(Field("Commands", status["commandCount"]?.ToString() ?? "0"));
            body.Add(Field("Active profile", status["activeProfile"]?.ToString() ?? "?"));
            body.Add(Field("Active runs", status["activeRunCount"]?.ToString() ?? "0"));
            body.Add(Field("Coordinator", $"readers {coord?["activeReaders"]} · writer {coord?["hasActiveWriter"]}"));
            body.Add(Field("Registry", kernel.Registry.HasErrors ? $"{kernel.Registry.Errors.Count} error(s)" : "OK"));

            if (kernel.Registry.HasErrors)
                foreach (var error in kernel.Registry.Errors)
                {
                    var line = new Label("• " + error);
                    line.AddToClassList("molca-md--error");
                    line.style.whiteSpace = WhiteSpace.Normal;
                    body.Add(line);
                }
            return card;
        }

        // ---- Workflows --------------------------------------------------------------------------

        private VisualElement BuildWorkflows()
        {
            var card = Card("Workflows", "Run a composed workflow and read its evidence", out _);
            var body = CardBody(card);
            var workflows = MolcaAutomationKernel.Instance.Capabilities().Where(c => c.Category == "workflow").ToList();

            if (workflows.Count == 0)
            {
                body.Add(Muted("No workflows registered."));
                return card;
            }

            foreach (var workflow in workflows)
            {
                var row = new VisualElement { style = { marginBottom = 10 } };
                var headerRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                headerRow.Add(new Label(workflow.DisplayName) { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold, flexGrow = 1 } });
                var status = Muted("idle");
                status.style.marginRight = 6;
                headerRow.Add(status);
                var preview = MolcaButtons.Mini("Preview", null);
                preview.style.marginRight = 4;
                headerRow.Add(preview);
                var run = MolcaButtons.Mini("Run", null);
                headerRow.Add(run);
                row.Add(headerRow);

                if (!string.IsNullOrEmpty(workflow.Description))
                {
                    var desc = Muted(workflow.Description);
                    desc.style.whiteSpace = WhiteSpace.Normal;
                    row.Add(desc);
                }

                var result = new VisualElement { style = { marginTop = 3 } };
                row.Add(result);

                var command = workflow;
                preview.clicked += () => ShowPlan(command, result);
                run.clicked += () => RunWorkflow(command, status, result, run);
                body.Add(row);
            }
            return card;
        }

        private async void RunWorkflow(MolcaCommandDefinition command, Label status, VisualElement result, Button run)
        {
            // async void as a UI event entry point only (async-contract rule 2); body wrapped so exceptions
            // cannot escape into Unity's synchronization context.
            run.SetEnabled(false);
            result.Clear();
            status.text = "running…";
            try
            {
                var res = await MolcaAutomationKernel.Instance.InvokeAsync(
                    command.Id, new JObject(), MolcaTransport.Hub,
                    progress: p => status.text = string.IsNullOrEmpty(p.StepName) ? p.Message : $"{p.StepName} ({p.StepIndex + 1}/{p.StepCount})");

                status.text = res.Success ? "succeeded" : MolcaCommandResult.WireStatusName(res.Status.ToString());
                RenderWorkflowResult(res, result);
            }
            catch (System.Exception ex)
            {
                status.text = "error";
                var line = new Label(ex.Message);
                line.AddToClassList("molca-md--error");
                line.style.whiteSpace = WhiteSpace.Normal;
                result.Add(line);
            }
            finally { run.SetEnabled(true); }
        }

        /// <summary>
        /// Renders a pre-execution plan preview inline: whether the workflow would run now under the active
        /// profile and play state, and — when it wouldn't — the blockers, so the user sees why before
        /// clicking Run (e.g. "requires Play mode", "the Observe profile permits read-only commands only").
        /// </summary>
        private static void ShowPlan(MolcaCommandDefinition command, VisualElement into)
        {
            into.Clear();
            var plan = MolcaAutomationKernel.Instance.PreviewPlan(command.Id, new JObject(), MolcaTransport.Hub);

            var wouldRun = plan.Value<bool>("wouldRun");
            var needsConfirm = plan.Value<bool>("needsConfirmationToRun");

            var headline = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 16 } };
            headline.Add(Dot(wouldRun ? "molca-status-dot--ok" : "molca-status-dot--warn"));
            headline.Add(new Label(wouldRun
                ? (needsConfirm ? "Would run — confirmation required" : "Would run now")
                : "Blocked — see below"));
            into.Add(headline);

            into.Add(Muted($"{plan.Value<string>("kind")} · {plan.Value<string>("mode")} · "
                + $"revert: {plan.Value<string>("reversibility")} · retry: {plan.Value<string>("retryClassification")}"));

            if (plan["blockers"] is JArray blockers)
                foreach (var b in blockers)
                {
                    var line = new Label($"• {b}") { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
                    line.style.color = new UnityEngine.Color(0.90f, 0.72f, 0.20f);
                    into.Add(line);
                }
        }

        private static void RenderWorkflowResult(MolcaCommandResult result, VisualElement into)
        {
            if (result.Data?["steps"] is JArray steps)
                foreach (var step in steps)
                {
                    var passed = step.Value<bool>("passed");
                    var stepRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 16 } };
                    stepRow.Add(Dot(passed ? "molca-status-dot--ok" : "molca-status-dot--error"));
                    stepRow.Add(new Label(step.Value<string>("id")));
                    into.Add(stepRow);
                }

            if (result.Diagnostics.Count > 0)
            {
                var errors = result.Diagnostics.Count(d => d.Severity == MolcaDiagnosticSeverity.Error);
                var warnings = result.Diagnostics.Count(d => d.Severity == MolcaDiagnosticSeverity.Warning);
                into.Add(Muted($"{errors} error(s), {warnings} warning(s)"));

                // Surface the actual messages — for a refusal the reason (e.g. "requires Play mode")
                // lives here, so counts alone leave the user guessing why a run stopped.
                foreach (var d in result.Diagnostics.Take(8))
                    into.Add(DiagnosticLine(d));
                if (result.Diagnostics.Count > 8)
                    into.Add(Muted($"…and {result.Diagnostics.Count - 8} more."));
            }
            else if (result.Status == MolcaCommandStatus.Refused)
            {
                // Defensive: a refusal should always carry a diagnostic, but never render blank.
                into.Add(Muted("Refused with no diagnostic — check the run history for details."));
            }
        }

        /// <summary>A wrapped, severity-coloured line for one diagnostic: "code — message".</summary>
        private static Label DiagnosticLine(MolcaDiagnostic d)
        {
            var text = string.IsNullOrEmpty(d.Code) ? d.Message : $"{d.Code} — {d.Message}";
            var label = new Label(text) { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
            if (d.Severity == MolcaDiagnosticSeverity.Error)
                label.AddToClassList("molca-md--error");
            else if (d.Severity == MolcaDiagnosticSeverity.Warning)
                label.style.color = new UnityEngine.Color(0.90f, 0.72f, 0.20f); // amber — warning/refusal reason
            else
                label.AddToClassList("molca-hub-muted");
            return label;
        }

        // ---- Permissions ------------------------------------------------------------------------

        private static string ProfileBlurb(MolcaAutomationProfile profile)
        {
            switch (profile)
            {
                case MolcaAutomationProfile.Observe:
                    return "Read-only commands only. Every action is refused — the safe default.";
                case MolcaAutomationProfile.Develop:
                    return "Allowlisted undoable/snapshot actions run; irreversible actions require confirmation.";
                case MolcaAutomationProfile.Release:
                    return "Allowlisted validation/build/deploy actions; irreversible actions require confirmation.";
                case MolcaAutomationProfile.UnattendedCi:
                    return "Exact allowlist, no confirmation prompts — for headless CI runs.";
                default:
                    return string.Empty;
            }
        }

        private VisualElement BuildPermissions()
        {
            var card = Card("Permissions", "Standing policy for CLI, CI, and agent callers", out _);
            var body = CardBody(card);
            var settings = MolcaAutomationPolicySettings.GetOrCreateSettings();

            // What this authoring is for — the common point of confusion.
            var banner = Muted("Running a workflow yourself in the Hub is already consent. These settings "
                + "govern what non-interactive callers — the Unity CLI, CI, and AI agents — may run without a "
                + "human present. Irreversible actions still confirm interactively.");
            banner.style.whiteSpace = WhiteSpace.Normal;
            banner.style.marginBottom = 8;
            body.Add(banner);

            // --- Profile chooser: one selectable row per profile, with its blurb ---
            body.Add(new Label("Profile") { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold, marginBottom = 4 } });
            foreach (MolcaAutomationProfile p in System.Enum.GetValues(typeof(MolcaAutomationProfile)))
            {
                var profile = p;
                var active = settings.ActiveProfile == profile;
                var row = new VisualElement { style = { flexShrink = 0, flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, paddingTop = 6, paddingBottom = 7, paddingLeft = 6, borderLeftWidth = 2, marginBottom = 3 } };
                row.style.borderLeftColor = active ? new UnityEngine.Color(0.70f, 0.94f, 0f) : new UnityEngine.Color(0, 0, 0, 0);
                if (active) row.style.backgroundColor = new UnityEngine.Color(1f, 1f, 1f, 0.04f);

                var marker = new Label(active ? "●" : "○") { style = { width = 16, paddingTop = 1, color = active ? new UnityEngine.Color(0.70f, 0.94f, 0f) : new UnityEngine.Color(0.5f, 0.5f, 0.5f) } };
                row.Add(marker);
                var stack = new VisualElement { style = { flexGrow = 1 } };
                stack.Add(new Label(profile.ToString()) { style = { unityFontStyleAndWeight = active ? UnityEngine.FontStyle.Bold : UnityEngine.FontStyle.Normal, marginBottom = 2 } });
                var blurb = Muted(ProfileBlurb(profile));
                blurb.style.whiteSpace = WhiteSpace.Normal;
                blurb.style.fontSize = 10;
                stack.Add(blurb);
                row.Add(stack);

                if (!active)
                    row.AddManipulator(new Clickable(() => { settings.SetActiveProfile(profile); Refresh(); }));
                body.Add(row);
            }

            // --- Action allowlist ---
            body.Add(Divider());
            var allowlisted = settings.ActionAllowlist.ToList();

            if (settings.ActiveProfile == MolcaAutomationProfile.Observe)
            {
                var note = Muted($"The action allowlist is inactive under Observe (all actions are refused). "
                    + $"Switch to Develop or higher to enable it. {allowlisted.Count} action(s) are allowlisted for those profiles.");
                note.style.whiteSpace = WhiteSpace.Normal;
                body.Add(note);
            }
            else
            {
                var countLabel = new Label($"Action allowlist ({allowlisted.Count} allowed)") { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold } };
                body.Add(countLabel);
                body.Add(Muted("Which mutating actions those callers may run under this profile. Filter, then toggle — or use the bulk buttons."));

                var search = new TextField();
                search.AddToClassList("molca-search");
                body.Add(search);

                var tools = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
                var scroll = new ScrollView { style = { maxHeight = 320 } };
                body.Add(tools);
                body.Add(scroll);

                void RefreshCount() => countLabel.text = $"Action allowlist ({settings.ActionAllowlist.Count} allowed)";

                void Render()
                {
                    var filter = (search.value ?? string.Empty).Trim();
                    var filtered = MolcaAutomationKernel.Instance.Capabilities()
                        .Where(c => c.Kind == MolcaCommandKind.Action)
                        .Where(c => string.IsNullOrEmpty(filter) || c.Id.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(c => settings.ActionAllowlist.Contains(c.Id)) // allowlisted first
                        .ThenBy(c => c.Id, System.StringComparer.Ordinal)
                        .ToList();

                    void Bulk(bool allowed)
                    {
                        settings.SetActionsAllowed(filtered.Select(c => c.Id), allowed);
                        MolcaAutomationKernel.Instance.Rebuild();
                        RefreshCount();
                        Render();
                    }

                    // Context-aware bulk buttons: without a filter these act on everything ("all");
                    // with a filter they act on just the shown subset.
                    tools.Clear();
                    var scoped = string.IsNullOrEmpty(filter);
                    var allow = MolcaButtons.Mini(scoped ? "Allow all" : $"Allow {filtered.Count} shown", () => Bulk(true));
                    allow.style.marginRight = 4;
                    tools.Add(allow);
                    tools.Add(MolcaButtons.Mini(scoped ? "Allow none" : $"Deny {filtered.Count} shown", () => Bulk(false)));

                    scroll.Clear();
                    foreach (var command in filtered)
                    {
                        var id = command.Id;
                        var toggleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 20 } };
                        var toggle = new Toggle { value = settings.ActionAllowlist.Contains(id) };
                        toggle.RegisterValueChangedCallback(evt =>
                        {
                            settings.SetActionAllowed(id, evt.newValue);
                            MolcaAutomationKernel.Instance.Rebuild();
                            RefreshCount();
                        });
                        toggleRow.Add(toggle);
                        toggleRow.Add(new Label(id) { style = { flexGrow = 1 } });
                        var meta = command.Reversibility == MolcaCommandReversibility.None ? "irreversible" : command.Reversibility.ToString();
                        toggleRow.Add(Muted(meta));
                        scroll.Add(toggleRow);
                    }
                }

                search.RegisterValueChangedCallback(_ => Render());
                Render();
            }

            body.Add(Divider());
            body.Add(MolcaButtons.Mini("Reset to safe defaults", () => { settings.ResetToDefaults(); Refresh(); }));
            return card;
        }

        private static VisualElement Divider()
        {
            var d = new VisualElement { style = { marginTop = 8, marginBottom = 8 } };
            d.AddToClassList("molca-divider");
            return d;
        }

        // ---- History ----------------------------------------------------------------------------

        private VisualElement BuildHistory()
        {
            var card = Card("History", null, out var count);
            var body = CardBody(card);
            var runs = MolcaAutomationKernel.Instance.RunStore.History();

            var interrupted = runs.Count(r => r.Status == MolcaCommandStatus.Interrupted);
            count.text = interrupted > 0 ? $"{runs.Count} run(s) · {interrupted} interrupted" : $"{runs.Count} run(s)";

            if (runs.Count == 0)
            {
                body.Add(Muted("No runs recorded yet. History persists across domain reloads."));
                return card;
            }

            var export = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
            export.Add(MolcaButtons.Mini("Export…", () => ExportHistory(runs)));
            body.Add(export);

            var scroll = new ScrollView { style = { maxHeight = 320 } };
            foreach (var run in runs)
            {
                var entry = new VisualElement();
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 18 } };
                row.Add(Dot(HistoryDotClass(run.Status)));
                row.Add(new Label(run.CommandId) { style = { flexGrow = 1 } });
                var when = run.OrderingTimeUtc.ToLocalTime().ToString("HH:mm:ss");
                row.Add(Muted($"{MolcaCommandResult.WireStatusName(run.Status.ToString())} · {run.Transport} · {when}"));
                entry.Add(row);

                // Click a row to expand its audit detail (diagnostics, verification, revert) from the
                // persisted result envelope — no log scraping to see why a run ended as it did (§8, §12).
                var detail = new VisualElement { style = { marginLeft = 14, marginBottom = 4, display = DisplayStyle.None } };
                bool built = false;
                row.RegisterCallback<ClickEvent>(_ =>
                {
                    if (!built) { RenderRunAudit(run, detail); built = true; }
                    detail.style.display = detail.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None;
                });
                entry.Add(detail);
                scroll.Add(entry);
            }
            body.Add(scroll);
            return card;
        }

        /// <summary>Renders one run's audit trail from its persisted result: verification, diagnostics, revert.</summary>
        private static void RenderRunAudit(MolcaPersistedRun run, VisualElement into)
        {
            var result = run.ResultJson;
            if (result == null)
            {
                into.Add(Muted(run.Status == MolcaCommandStatus.Interrupted
                    ? "Interrupted before an outcome was recorded — its effect, if any, is of unknown state."
                    : "No result was recorded for this run."));
                return;
            }

            if (result["verification"] is JObject v && v.Value<bool>("performed"))
            {
                into.Add(Muted($"Verification: {(v.Value<bool>("passed") ? "passed" : "FAILED")}"));
                if (v["evidence"] is JArray ev)
                    foreach (var e in ev.Take(8)) into.Add(Muted($"  ✓ {e}"));
            }

            if (result["diagnostics"] is JArray diags && diags.Count > 0)
                foreach (var d in diags.Take(8))
                    into.Add(DiagnosticLine(new MolcaDiagnostic(
                        d.Value<string>("code"), d.Value<string>("message"),
                        System.Enum.TryParse(d.Value<string>("severity"), out MolcaDiagnosticSeverity sev) ? sev : MolcaDiagnosticSeverity.Info)));

            if (result["revert"] is JObject rev && !string.IsNullOrEmpty(rev.Value<string>("id")))
            {
                var revRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 2 } };
                revRow.Add(Muted($"Revert available ({rev.Value<string>("kind")})"));
                var runId = run.RunId;
                var undo = MolcaButtons.Mini("Revert", () => RevertRun(runId, into));
                undo.style.marginLeft = 6;
                revRow.Add(undo);
                into.Add(revRow);
            }
        }

        /// <summary>Executes a stored revert for a run and reports the outcome inline.</summary>
        private static async void RevertRun(string runId, VisualElement into)
        {
            // async void as a UI event entry point only (async-contract rule 2).
            try
            {
                var result = await MolcaAutomationKernel.Instance.RevertAsync(runId, MolcaTransport.Hub);
                var line = new Label(result.Success ? "Reverted." : $"Revert failed: {result.Diagnostics.FirstOrDefault()?.Message}")
                { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
                if (!result.Success) line.AddToClassList("molca-md--error");
                into.Add(line);
            }
            catch (System.Exception ex)
            {
                var line = new Label(ex.Message) { style = { whiteSpace = WhiteSpace.Normal } };
                line.AddToClassList("molca-md--error");
                into.Add(line);
            }
        }

        private static string HistoryDotClass(MolcaCommandStatus status)
        {
            switch (status)
            {
                case MolcaCommandStatus.Succeeded: return "molca-status-dot--ok";
                case MolcaCommandStatus.Failed: return "molca-status-dot--error";
                case MolcaCommandStatus.Cancelled:
                case MolcaCommandStatus.Refused:
                case MolcaCommandStatus.Blocked:
                case MolcaCommandStatus.Interrupted: return "molca-status-dot--warn";
                default: return "molca-status-dot--idle";
            }
        }

        private static void ExportHistory(System.Collections.Generic.IReadOnlyList<MolcaPersistedRun> runs)
        {
            var path = UnityEditor.EditorUtility.SaveFilePanel("Export automation run history", "", "molca-automation-runs.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            var array = new JArray(runs.Select(r => r.ToJson()));
            System.IO.File.WriteAllText(path, array.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        // ---- Capabilities -----------------------------------------------------------------------

        private VisualElement BuildCapabilities()
        {
            var card = Card("Capabilities", null, out var count);
            var body = CardBody(card);

            var search = new TextField();
            search.AddToClassList("molca-search");
            body.Add(search);

            // Rows flow into the outer detail ScrollView (no nested scroll).
            var list = new VisualElement();
            body.Add(list);

            void Render()
            {
                list.Clear();
                var filter = (search.value ?? string.Empty).Trim();
                var commands = MolcaAutomationKernel.Instance.Capabilities()
                    .Where(c => string.IsNullOrEmpty(filter) ||
                                c.Id.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                c.Category.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                count.text = $"{commands.Count} command(s)";
                foreach (var command in commands)
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 22 } };
                    row.Add(Dot(command.Kind == MolcaCommandKind.Action ? "molca-status-dot--warn" : "molca-status-dot--ok"));
                    row.Add(new Label(command.Id) { style = { flexGrow = 1 } });
                    var meta = $"{command.Category} · {command.Kind} · {command.Mode}";
                    if (command.RequiresConfirmation) meta += " · confirm";
                    row.Add(Muted(meta));
                    list.Add(row);
                }
            }

            search.RegisterValueChangedCallback(_ => Render());
            Render();
            return card;
        }

        // ---- Shared helpers ---------------------------------------------------------------------

        private VisualElement Card(string title, string subtitle, out Label subtitleLabel)
        {
            var card = new VisualElement();
            card.AddToClassList("molca-card");
            var head = new VisualElement();
            head.AddToClassList("molca-card__header");
            var stack = new VisualElement();
            stack.AddToClassList("molca-card__title-stack");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("molca-card__title");
            stack.Add(titleLabel);
            subtitleLabel = new Label(subtitle ?? string.Empty);
            subtitleLabel.AddToClassList("molca-card__subtitle");
            stack.Add(subtitleLabel);
            head.Add(stack);
            card.Add(head);
            return card;
        }

        private static VisualElement CardBody(VisualElement card)
        {
            var body = new VisualElement();
            body.AddToClassList("molca-card__body");
            card.Add(body);
            return body;
        }

        private static VisualElement Field(string label, string value)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 18, marginBottom = 2 } };
            var l = new Label(label);
            l.AddToClassList("molca-field-label");
            row.Add(l);
            var v = new Label(value);
            v.AddToClassList("molca-field-control");
            row.Add(v);
            return row;
        }

        private static Label Muted(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-muted");
            return label;
        }

        private static VisualElement Dot(string modifierClass)
        {
            var dot = new VisualElement();
            dot.AddToClassList("molca-status-dot");
            dot.AddToClassList(modifierClass);
            dot.style.marginRight = 6;
            return dot;
        }
    }
}
