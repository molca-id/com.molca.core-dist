using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;
using Molca.Editor.UI;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Renders the assistant transcript into a <see cref="ScrollView"/> (Sprint 25.2): flat, full-width,
    /// role-labeled rows with inline markdown, clickable file links, collapsible tool chains, raw-result
    /// foldouts, and per-message Copy/Retry/Edit/Undo affordances. Extracted from the window so the
    /// renderer is self-contained — it depends only on the controller (for transcript data and busy state)
    /// and a few callbacks back to the window for row actions. Cosmetic styling lives in the matching USS.
    /// </summary>
    public sealed class AssistantTranscriptView
    {
        private readonly ScrollView _scroll;
        private readonly AssistantChatController _controller;
        private readonly Action _onRetry;
        private readonly Action<string> _onEdit;
        private readonly Action _onRefresh;
        private readonly Action<string> _onNotify;
        private readonly Action _onContinue;

        // Streaming incremental-render state: the in-flight "live" row and the committed-turn count it was
        // built against. While a turn streams, only this row changes, so we swap it in place rather than
        // clearing and re-parsing every committed row on each token delta (Sprint 25.3).
        private VisualElement _liveRow;
        private int _committedCount = -1;
        private ChatTurnKind? _lastCommittedKind;

        /// <summary>Creates a transcript view bound to a scroll view and the row-action callbacks.</summary>
        /// <param name="scroll">The scroll view that hosts the rendered rows.</param>
        /// <param name="controller">Source of transcript turns, busy state, and streaming text.</param>
        /// <param name="onRetry">Invoked when the user clicks Retry on the last user turn.</param>
        /// <param name="onEdit">Invoked with a turn's text when the user clicks Edit.</param>
        /// <param name="onRefresh">Re-renders the window after an Undo mutates revert state.</param>
        /// <param name="onNotify">Shows a transient editor notification (Undo outcomes).</param>
        /// <param name="onContinue">Resumes a turn that hit the tool-round cap (Sprint 25.8).</param>
        public AssistantTranscriptView(ScrollView scroll, AssistantChatController controller,
            Action onRetry, Action<string> onEdit, Action onRefresh, Action<string> onNotify, Action onContinue)
        {
            _scroll = scroll;
            _controller = controller;
            _onRetry = onRetry;
            _onEdit = onEdit;
            _onRefresh = onRefresh;
            _onNotify = onNotify;
            _onContinue = onContinue;
        }

        /// <summary>
        /// Refreshes the transcript. While a turn is streaming and no new committed turn has arrived, only
        /// the in-flight live row is swapped (the committed rows above it are left untouched); otherwise
        /// every row is rebuilt from the current transcript. Always scrolls to the latest.
        /// </summary>
        public void Render()
        {
            if (_scroll == null) return;

            var count = _controller.Transcript.Count;
            var content = _scroll.contentContainer;

            // Streaming fast-path: the committed turns are unchanged, so rebuild just the live row.
            if (_controller.IsBusy && _liveRow != null && count == _committedCount)
            {
                var index = content.IndexOf(_liveRow);
                if (index >= 0)
                {
                    var replacement = BuildLiveRow(continuation: _lastCommittedKind == ChatTurnKind.Assistant);
                    content.Insert(index, replacement);
                    _liveRow.RemoveFromHierarchy();
                    _liveRow = replacement;
                    _scroll.scrollOffset = new Vector2(0, float.MaxValue);
                    return;
                }
            }

            _scroll.Clear();
            var prevKind = (ChatTurnKind?)null;
            var transcript = _controller.Transcript;
            for (var i = 0; i < transcript.Count; i++)
            {
                var turn = transcript[i];

                // While a prompt is pending, its question is shown in the docked prompt bar, so skip the
                // inline copy to avoid showing the same question twice. Once answered it renders normally
                // as a record (question + chosen answer).
                if (turn.Kind == ChatTurnKind.Prompt && _controller.IsAwaitingUser && i == transcript.Count - 1)
                    continue;

                // Inline, a Prompt turn is just a record of the question and the chosen answer; its
                // interactive controls live in the docked prompt bar (above the composer).
                var row = BuildRow(turn, continuation: prevKind == turn.Kind);
                _scroll.Add(row);
                prevKind = turn.Kind;
            }

            _committedCount = count;
            _lastCommittedKind = prevKind;

            // Suppress the "Thinking…" live row while paused for a user decision — the prompt controls
            // are the live element in that state.
            if (_controller.IsBusy && !_controller.IsAwaitingUser)
            {
                _liveRow = BuildLiveRow(continuation: prevKind == ChatTurnKind.Assistant);
                _scroll.Add(_liveRow);
            }
            else
            {
                _liveRow = null;
            }
            _scroll.scrollOffset = new Vector2(0, float.MaxValue);
        }

        /// <summary>Builds one flat, full-width, role-labeled row.</summary>
        private VisualElement BuildRow(ChatTurn turn, bool continuation) => BuildRowElement(turn, turn.Text, isLive: false, continuation);

        private VisualElement BuildLiveRow(bool continuation)
        {
            // Render the in-flight row as a plain (unformatted) label: streaming text changes on every token
            // delta, so skipping the markdown parse here avoids re-parsing partial text dozens of times per
            // turn. The fully formatted version appears once the assistant turn commits to the transcript.
            var text = string.IsNullOrEmpty(_controller.StreamingText) ? "Thinking…" : _controller.StreamingText;
            var liveTurn = new ChatTurn(ChatTurnKind.Assistant, text);
            return BuildRowElement(liveTurn, text, isLive: true, continuation);
        }

        private VisualElement BuildRowElement(ChatTurn turn, string text, bool isLive, bool continuation)
        {
            var suffix = KindSuffix(turn.Kind);
            var row = new VisualElement();
            row.AddToClassList("chat-row");
            row.AddToClassList("chat-row--" + suffix);
            // Continuation rows of the same role hug the previous row (no top padding, no gap) so the
            // chain reads as one block instead of repeating the role header.
            if (continuation) row.AddToClassList("chat-row--continuation");

            // Header: role label (omitted on continuation rows) + per-message actions.
            var headerRow = new VisualElement();
            headerRow.AddToClassList("chat-row__header");
            if (continuation) headerRow.AddToClassList("chat-row__header--continuation");
            // Work turns omit the redundant "Worked" title — the foldout label ("Worked through N
            // steps.") already carries it — but keep the header row for the right-aligned Undo button.
            if (!continuation && turn.Kind != ChatTurnKind.Work)
            {
                var title = new Label(TitleFor(turn));
                title.AddToClassList("chat-row__title");
                title.AddToClassList("chat-row__title--" + suffix);
                headerRow.Add(title);
            }
            else
            {
                // Spacer keeps the per-message action buttons right-aligned without a role label.
                var spacer = new VisualElement();
                spacer.AddToClassList("chat-row__spacer");
                headerRow.Add(spacer);
            }

            if (!isLive && !string.IsNullOrWhiteSpace(text))
                headerRow.Add(CreateMiniButton("Copy", () => EditorGUIUtility.systemCopyBuffer = AssistantTranscriptFormatter.RedactSecrets(text)));
            if (!isLive && turn.Kind == ChatTurnKind.User && !_controller.IsBusy)
            {
                headerRow.Add(CreateMiniButton("Retry", () => _onRetry?.Invoke()));
                headerRow.Add(CreateMiniButton("Edit", () => _onEdit?.Invoke(text)));
            }
            if (!isLive && (turn.Kind == ChatTurnKind.Tool || turn.Kind == ChatTurnKind.Work))
                AddUndoButtonIfAny(headerRow, turn);
            if (!isLive && turn.Kind == ChatTurnKind.Error && turn.CanContinue && !_controller.IsBusy)
                headerRow.Add(CreateMiniButton("Continue", () => _onContinue?.Invoke()));
            // A retrieval notice (Sprint 47) offers Pin to promote its transient context to a persistent pin.
            if (!isLive && turn.Kind == ChatTurnKind.Notice && turn.CanPin && !string.IsNullOrWhiteSpace(turn.Detail))
                headerRow.Add(CreateMiniButton("Pin", () => _controller.PinRetrievedContext(turn.Detail)));
            // A plan-completed notice (Sprint 48) offers a single "Undo task" reverting the whole bracket.
            if (!isLive && turn.Kind == ChatTurnKind.Notice && turn.CanUndoTask && !_controller.IsBusy)
                headerRow.Add(CreateMiniButton("Undo task", () => _controller.UndoApprovedPlan()));
            // A structured plan turn (Sprint 52) with a persisted undo bracket offers an "Undo task" that
            // survives a domain reload (it reverts the bracket stored on the turn).
            if (!isLive && turn.Kind == ChatTurnKind.Plan && HasPlanUndo(turn) && !_controller.IsBusy)
                headerRow.Add(CreateMiniButton("Undo task", () =>
                {
                    _controller.UndoApprovedPlan(turn);
                    _onRefresh?.Invoke();
                }));

            // Work turns drop the separate header row entirely and host their Copy/Undo buttons on the
            // foldout's own toggle line, so the whole step collapses to a single line.
            if (!isLive && turn.Kind == ChatTurnKind.Work)
            {
                RenderWorkSummary(row, turn, headerRow);
                return row;
            }

            // An answered action-confirmation collapses to a single muted outcome line (no "Assistant asks"
            // header): the following Work row already lists exactly what ran, and the audit log keeps the
            // full record. Genuine molca_ask_user prompts (IsConfirmation == false) keep their full form.
            if (!isLive && turn.Kind == ChatTurnKind.Prompt && turn.IsConfirmation && !string.IsNullOrEmpty(turn.PromptAnswer))
            {
                var (glyph, modifier, label) = ClassifyConfirmation(turn.PromptAnswer);
                var outcome = new Label($"{glyph} {label} · {FirstLine(text)}");
                outcome.AddToClassList("chat-confirm-outcome");
                if (modifier != null) outcome.AddToClassList(modifier);
                row.Add(outcome);
                return row;
            }
            row.Add(headerRow);

            if (isLive)
            {
                var live = new Label(text);
                live.AddToClassList("chat-live-text");
                row.Add(live);
                return row;
            }

            // A structured plan turn (Sprint 52) renders an ordered checklist with live per-step status,
            // editable inline (reorder/delete/retext) until the plan is approved.
            if (turn.Kind == ChatTurnKind.Plan)
            {
                RenderPlan(row, turn);
                return row;
            }

            // Tool turns with more than one call collapse their per-call list behind a foldout so a long
            // chain (often 10+ edits) doesn't flood the transcript; a one-line summary stays visible.
            if (turn.Kind == ChatTurnKind.Tool && turn.ToolSummaries != null && turn.ToolSummaries.Count > 1)
            {
                RenderToolChain(row, turn.ToolSummaries);
            }
            else if (turn.Kind == ChatTurnKind.Prompt && HasMultipleLines(text))
            {
                // A confirmation question (e.g. "Run 18 actions?" + the full action list) can be very long.
                // Collapse its body behind a disclosure headed by the first line, so it stays a one-line
                // summary. Defaults to collapsed — the live question is also surfaced in the docked prompt
                // bar while pending, and a transcript refresh (e.g. adding context) keeps it collapsed.
                var firstLine = FirstLine(text);
                var content = AddDisclosure(row, firstLine, startExpanded: false);
                RenderFormattedText(content, text, turn.Kind);
            }
            else if (turn.Kind == ChatTurnKind.Notice && !string.IsNullOrWhiteSpace(turn.Detail))
            {
                // A compaction Notice carries the generated summary in Detail (Sprint 46): show the one-line
                // notice and tuck the summary behind a collapsed "View summary" disclosure.
                RenderFormattedText(row, text, turn.Kind);
                var content = AddDisclosure(row, "View summary", startExpanded: false);
                RenderFormattedText(content, turn.Detail, turn.Kind);
            }
            else
            {
                RenderFormattedText(row, text, turn.Kind);
            }

            // An answered prompt shows the chosen answer beneath the question.
            if (turn.Kind == ChatTurnKind.Prompt && !string.IsNullOrEmpty(turn.PromptAnswer))
            {
                var answer = new Label("→ " + turn.PromptAnswer);
                answer.AddToClassList("chat-prompt-answer");
                row.Add(answer);
            }

            if (turn.ToolSummaries != null && turn.ToolSummaries.Count > 0)
                AddRawToolPayloads(row, turn.ToolSummaries);
            return row;
        }

        /// <summary>
        /// Builds a custom collapsible disclosure (a clickable ▶/▼ header + a content block toggled
        /// beneath it) and appends both to <paramref name="parent"/>. Used in place of a Unity
        /// <see cref="Foldout"/>, whose default toggle paints a pale header background and focus outline
        /// that can't be reliably overridden. Returns the content element for the caller to populate.
        /// </summary>
        /// <param name="parent">Row to append the header and content to.</param>
        /// <param name="headerText">Label shown next to the disclosure arrow.</param>
        /// <param name="startExpanded">Whether the content is visible initially.</param>
        /// <param name="headerActions">Optional buttons (e.g. Copy/Undo) hosted on the header line, right-aligned.</param>
        private VisualElement AddDisclosure(VisualElement parent, string headerText, bool startExpanded, VisualElement headerActions = null)
        {
            var header = new VisualElement();
            header.AddToClassList("chat-work__header");

            var arrow = new Label(startExpanded ? "▼" : "▶");
            arrow.AddToClassList("chat-work__arrow");
            header.Add(arrow);

            var label = new Label(headerText);
            label.AddToClassList("chat-work__label");
            header.Add(label);

            // Host the row's action buttons on the header line (instead of a separate row above it) so the
            // entry occupies a single line when collapsed. A carried-over spacer keeps them right-aligned.
            if (headerActions != null)
                foreach (var child in new List<VisualElement>(headerActions.Children()))
                    header.Add(child);

            var content = new VisualElement();
            content.AddToClassList("chat-work__content");
            content.style.display = startExpanded ? DisplayStyle.Flex : DisplayStyle.None;

            header.RegisterCallback<ClickEvent>(evt =>
            {
                // Ignore clicks on the hosted buttons so they don't also toggle the disclosure.
                if (evt.target is Button || (evt.target as VisualElement)?.GetFirstAncestorOfType<Button>() != null)
                    return;
                var expanded = content.style.display == DisplayStyle.None;
                content.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                arrow.text = expanded ? "▼" : "▶";
            });

            parent.Add(header);
            parent.Add(content);
            return content;
        }

        private void RenderWorkSummary(VisualElement parent, ChatTurn turn, VisualElement headerActions = null)
        {
            var headerText = string.IsNullOrWhiteSpace(turn.Text) ? "Worked through steps." : turn.Text;
            var content = AddDisclosure(parent, headerText, startExpanded: false, headerActions);

            if (turn.WorkItems != null)
            {
                foreach (var item in turn.WorkItems)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    RenderFormattedText(content, item, ChatTurnKind.Assistant);
                }
            }

            if (turn.ToolSummaries != null && turn.ToolSummaries.Count > 0)
            {
                RenderToolChain(content, turn.ToolSummaries);
                // Nest the raw payloads inside the same content so the whole step is one collapsible entry.
                AddRawToolPayloads(content, turn.ToolSummaries);
            }
        }

        /// <summary>True when a structured plan turn carries a persisted whole-task undo bracket (Sprint 52).</summary>
        private static bool HasPlanUndo(ChatTurn turn)
            => !string.IsNullOrEmpty(turn.PlanUndoFileId) || turn.PlanUndoGroup >= 0;

        /// <summary>
        /// Renders a structured plan (Sprint 52) as an ordered checklist with a per-step status glyph. While
        /// the plan is awaiting approval it is editable inline — each step can be moved, deleted, or retexted,
        /// and the revised list is what gets approved and executed.
        /// </summary>
        private void RenderPlan(VisualElement parent, ChatTurn turn)
        {
            var steps = turn.PlanSteps;
            var list = new VisualElement();
            list.AddToClassList("chat-plan");
            parent.Add(list);

            if (steps == null || steps.Count == 0)
            {
                list.Add(new Label("No steps proposed.") { });
                return;
            }

            // Editable only before approval and only while the approval prompt is actually pending — once the
            // user has Approved/Edited/Cancelled the list is a read-only record.
            var editable = !turn.PlanApproved && _controller.IsAwaitingUser;

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null) continue;
                var index = i;

                var stepRow = new VisualElement();
                stepRow.AddToClassList("chat-plan__step");

                var glyph = new Label(PlanGlyph(step.Status));
                glyph.AddToClassList("chat-plan__glyph");
                glyph.AddToClassList("chat-plan__glyph--" + step.Status.ToString().ToLowerInvariant());
                stepRow.Add(glyph);

                if (editable)
                {
                    var field = new TextField { value = step.Summary };
                    field.AddToClassList("chat-plan__field");
                    field.RegisterValueChangedCallback(evt => step.Summary = evt.newValue);
                    stepRow.Add(field);

                    var up = CreateMiniButton("▲", () => { if (index > 0) { (steps[index - 1], steps[index]) = (steps[index], steps[index - 1]); _onRefresh?.Invoke(); } });
                    up.SetEnabled(index > 0);
                    stepRow.Add(up);
                    var down = CreateMiniButton("▼", () => { if (index < steps.Count - 1) { (steps[index + 1], steps[index]) = (steps[index], steps[index + 1]); _onRefresh?.Invoke(); } });
                    down.SetEnabled(index < steps.Count - 1);
                    stepRow.Add(down);
                    stepRow.Add(CreateMiniButton("✕", () => { steps.RemoveAt(index); _onRefresh?.Invoke(); }));
                }
                else
                {
                    var summary = new Label(step.Summary);
                    summary.AddToClassList("chat-plan__summary");
                    if (step.Status == PlanStepStatus.Skipped || step.Status == PlanStepStatus.Failed)
                        summary.AddToClassList("chat-plan__summary--inactive");
                    stepRow.Add(summary);
                }

                list.Add(stepRow);
            }
        }

        /// <summary>The status glyph shown beside a plan step (Sprint 52).</summary>
        private static string PlanGlyph(PlanStepStatus status) => status switch
        {
            PlanStepStatus.Running => "◐",
            PlanStepStatus.Done => "✓",
            PlanStepStatus.Failed => "✗",
            PlanStepStatus.Skipped => "⊘",
            _ => "○"
        };

        private void RenderToolChain(VisualElement parent, IReadOnlyList<ChatToolSummary> summaries)
        {
            var failed = 0;
            foreach (var s in summaries) if (s?.IsError == true) failed++;

            var summaryText = failed == 0
                ? $"Completed {summaries.Count} tool calls."
                : $"Ran {summaries.Count} tool calls: {failed} failed, {summaries.Count - failed} completed.";

            var foldout = new Foldout { text = summaryText, value = failed > 0 };
            foldout.style.fontSize = 11;
            foreach (var s in summaries)
            {
                if (s == null) continue;
                var rowLabel = new Label($"• {(string.IsNullOrWhiteSpace(s.Name) ? "tool" : s.Name)}: {(s.IsError ? "failed" : "completed")}");
                rowLabel.AddToClassList(s.IsError ? "chat-body--error" : "chat-body--tool");
                foldout.Add(rowLabel);
            }
            parent.Add(foldout);
        }

        /// <summary>
        /// Adds a single Undo button to a Tool row that contains one or more revertible actions. Reverting
        /// walks "back to this point": the oldest action in the row plus any newer reversible changes are
        /// undone — FileSnapshot actions via <see cref="McpUndoStack.UndoTo"/> (the earliest entry id covers
        /// the rest by LIFO) and UnityUndo actions via <see cref="Undo.RevertAllDownToGroup"/> (the smallest
        /// group index covers the rest). The button greys out once nothing in the row is revertible.
        /// </summary>
        private void AddUndoButtonIfAny(VisualElement headerRow, ChatTurn turn)
        {
            if (turn.ToolSummaries == null || turn.ToolSummaries.Count == 0) return;

            // Earliest FileSnapshot entry id (first in execution order) and smallest UnityUndo group.
            string earliestFileId = null;
            var minGroup = int.MaxValue;
            var actionCount = 0;
            foreach (var s in turn.ToolSummaries)
            {
                if (s == null || s.Kind != nameof(McpToolKind.Action)) continue;
                actionCount++;
                if (earliestFileId == null && !string.IsNullOrEmpty(s.UndoEntryId)) earliestFileId = s.UndoEntryId;
                if (s.UndoGroup >= 0 && s.UndoGroup < minGroup) minGroup = s.UndoGroup;
            }
            var hasFile = earliestFileId != null;
            var hasGroup = minGroup != int.MaxValue;
            if (!hasFile && !hasGroup) return;

            bool Available() =>
                (hasFile && McpUndoStack.Contains(earliestFileId)) ||
                (hasGroup && Undo.GetCurrentGroup() >= minGroup);

            var prompt = actionCount > 1
                ? $"Revert these {actionCount} actions and any newer reversible changes?"
                : "Revert this action and any newer reversible changes?";

            var button = CreateMiniButton("Undo", () =>
            {
                if (!Available())
                {
                    _onNotify?.Invoke("This change is no longer revertible.");
                    _onRefresh?.Invoke();
                    return;
                }
                if (!EditorUtility.DisplayDialog("Undo MCP Action", prompt, "Undo", "Cancel"))
                    return;

                string message = null;
                if (hasGroup && Undo.GetCurrentGroup() >= minGroup)
                {
                    Undo.RevertAllDownToGroup(minGroup);
                    message = actionCount > 1 ? $"Reverted {actionCount} actions." : "Reverted action.";
                }
                if (hasFile && McpUndoStack.Contains(earliestFileId))
                    message = McpUndoStack.UndoTo(earliestFileId);

                _onNotify?.Invoke(message ?? "Reverted.");
                _onRefresh?.Invoke();
            });
            button.SetEnabled(Available());
            button.tooltip = Available()
                ? (actionCount > 1
                    ? "Revert these actions (and any newer reversible changes)"
                    : "Revert this action (and any newer reversible changes)")
                : "No longer revertible";
            headerRow.Add(button);
        }

        private static Button CreateMiniButton(string label, Action action)
        {
            var button = new Button(action) { text = label };
            button.AddToClassList("chat-mini-button");
            return button;
        }

        /// <summary>True if <paramref name="text"/> spans more than one (non-trailing) line.</summary>
        private static bool HasMultipleLines(string text)
            => !string.IsNullOrEmpty(text) && text.TrimEnd().IndexOf('\n') >= 0;

        /// <summary>
        /// Maps an answered confirmation to its outcome glyph, USS modifier, and label. Affirmative answers
        /// ("Run"/"Run all"/"Approve") read as Approved; stop answers ("Cancel"/"Abort") as Declined; the
        /// other multi-choice answers (Sprint 52 failure UX — Retry/Skip/Undo task, and Edit) render
        /// neutrally with the chosen answer, since they are a choice rather than an approve/decline.
        /// </summary>
        private static (string glyph, string modifier, string label) ClassifyConfirmation(string answer)
        {
            if (answer.StartsWith("Run", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("Approve", StringComparison.OrdinalIgnoreCase))
                return ("✓", "chat-confirm-outcome--ok", "Approved");

            if (answer.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("Abort", StringComparison.OrdinalIgnoreCase))
                return ("✕", "chat-confirm-outcome--declined", "Declined");

            // Retry / Skip / Undo task / Edit — surface the actual choice, no approve/decline coloring.
            return ("•", null, answer);
        }

        /// <summary>The first line of <paramref name="text"/>, trimmed (used as a disclosure header).</summary>
        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var nl = text.IndexOf('\n');
            return (nl >= 0 ? text.Substring(0, nl) : text).TrimEnd('\r', ' ', '\t');
        }

        private static string TitleFor(ChatTurn turn) => turn.Kind switch
        {
            ChatTurnKind.User => "You",
            ChatTurnKind.Tool => ToolTitleFor(turn),
            ChatTurnKind.Error => "Error",
            ChatTurnKind.Prompt => "Assistant asks",
            ChatTurnKind.Work => "Worked",
            ChatTurnKind.Notice => "Context",
            ChatTurnKind.Plan => "Plan",
            _ => "Assistant"
        };

        private static string ToolTitleFor(ChatTurn turn)
        {
            var count = turn.ToolSummaries?.Count ?? 0;
            if (count > 1) return $"Tools ({count})";
            return turn.ToolSummary != null ? $"Tool: {turn.ToolSummary.Name}" : "Tool";
        }

        /// <summary>The USS modifier suffix (<c>user</c>/<c>assistant</c>/<c>tool</c>/<c>error</c>) for a turn kind.</summary>
        private static string KindSuffix(ChatTurnKind kind) => kind switch
        {
            ChatTurnKind.User => "user",
            ChatTurnKind.Tool => "tool",
            ChatTurnKind.Error => "error",
            ChatTurnKind.Prompt => "prompt",
            ChatTurnKind.Work => "tool",
            ChatTurnKind.Notice => "tool",
            ChatTurnKind.Plan => "plan",
            _ => "assistant"
        };

        private void RenderFormattedText(VisualElement parent, string text, ChatTurnKind turnKind)
        {
            var blocks = AssistantTranscriptFormatter.Parse(text);
            if (blocks.Count == 0)
            {
                parent.Add(CreateInlineBlock(text, turnKind));
                return;
            }

            foreach (var block in blocks)
            {
                if (block.Kind == AssistantTextBlockKind.Bullet || block.Kind == AssistantTextBlockKind.Numbered)
                {
                    parent.Add(CreateListRow(block, turnKind));
                    continue;
                }

                if (block.Kind == AssistantTextBlockKind.Code)
                {
                    parent.Add(CreateCodeBlock(block.Text));
                    continue;
                }

                if (block.Kind == AssistantTextBlockKind.Task)
                {
                    parent.Add(CreateTaskRow(block, turnKind));
                    continue;
                }

                if (block.Kind == AssistantTextBlockKind.Table)
                {
                    parent.Add(CreateTable(block, turnKind));
                    continue;
                }

                if (block.Kind == AssistantTextBlockKind.Rule)
                {
                    var rule = new VisualElement();
                    rule.AddToClassList("chat-rule");
                    parent.Add(rule);
                    continue;
                }

                if (block.Kind == AssistantTextBlockKind.Quote)
                {
                    var quote = CreateInlineBlock(block.RawText, turnKind);
                    quote.AddToClassList("chat-quote");
                    parent.Add(quote);
                    continue;
                }

                var element = CreateInlineBlock(block.RawText, turnKind);
                ApplyBlockStyle(element, block.Kind);
                parent.Add(element);
            }
        }

        private VisualElement CreateListRow(AssistantTextBlock block, ChatTurnKind turnKind)
        {
            var row = new VisualElement();
            row.AddToClassList("chat-list-row");
            var marker = new Label(block.Kind == AssistantTextBlockKind.Bullet ? "•" : $"{block.Number}.");
            marker.AddToClassList("chat-list-marker");
            ApplyTurnTextStyle(marker, turnKind);

            var text = CreateInlineBlock(block.RawText, turnKind);
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;

            row.Add(marker);
            row.Add(text);
            return row;
        }

        /// <summary>
        /// Builds the inline content of a block (Sprint 24.2 / 25.3). A block that is a single non-link
        /// run — the overwhelmingly common case of plain prose — renders as one wrapping <see cref="Label"/>
        /// instead of one label per word, which previously produced hundreds of elements per long answer.
        /// Mixed-style or link-bearing blocks fall back to a wrapping container of per-run elements (links
        /// must stay individually clickable, and atomic labels only break between runs).
        /// </summary>
        private VisualElement CreateInlineBlock(string rawText, ChatTurnKind turnKind)
        {
            var spans = AssistantTranscriptFormatter.ParseInline(rawText);

            // Fast path: a single non-interactive run is one Label with white-space:normal, which wraps on its own.
            if (spans.Count == 1 && spans[0].Kind != AssistantInlineKind.Link
                && spans[0].Kind != AssistantInlineKind.Context && spans[0].Kind != AssistantInlineKind.Url)
            {
                var s = spans[0];
                return MakeSpanLabel(s.Text, turnKind,
                    isCode: s.Kind == AssistantInlineKind.Code,
                    bold: s.Kind == AssistantInlineKind.Bold,
                    italic: s.Kind == AssistantInlineKind.Italic);
            }

            var container = new VisualElement();
            container.AddToClassList("chat-inline-container");

            foreach (var span in spans)
            {
                switch (span.Kind)
                {
                    case AssistantInlineKind.Link:
                        container.Add(MakeLinkLabel(span));
                        break;
                    case AssistantInlineKind.Context:
                        container.Add(MakeContextLabel(span));
                        break;
                    case AssistantInlineKind.Url:
                        container.Add(MakeUrlLabel(span));
                        break;
                    case AssistantInlineKind.Code:
                        container.Add(MakeSpanLabel(span.Text, turnKind, isCode: true, bold: false, italic: false));
                        break;
                    case AssistantInlineKind.Bold:
                        AddWords(container, span.Text, turnKind, bold: true, italic: false);
                        break;
                    case AssistantInlineKind.Italic:
                        AddWords(container, span.Text, turnKind, bold: false, italic: true);
                        break;
                    default:
                        AddWords(container, span.Text, turnKind, bold: false, italic: false);
                        break;
                }
            }
            return container;
        }

        private void AddWords(VisualElement container, string text, ChatTurnKind turnKind, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Split on spaces, keeping a trailing space on each word so wrapping looks natural.
            var words = text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var word = i < words.Length - 1 ? words[i] + " " : words[i];
                if (word.Length == 0) { container.Add(MakeSpanLabel(" ", turnKind, false, bold, italic)); continue; }
                container.Add(MakeSpanLabel(word, turnKind, false, bold, italic));
            }
        }

        private Label MakeSpanLabel(string text, ChatTurnKind turnKind, bool isCode, bool bold, bool italic)
        {
            var label = new Label(text);
            label.AddToClassList("chat-span");
            ApplyTurnTextStyle(label, turnKind);
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (italic) label.style.unityFontStyleAndWeight = bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
            if (isCode) label.AddToClassList("chat-inline-code");
            return label;
        }

        private Label MakeLinkLabel(AssistantInlineSpan span)
        {
            // Hover color is handled by the .chat-link:hover USS rule.
            var label = new Label(span.Text)
            {
                tooltip = span.LinkLine > 0 ? $"Open {span.LinkPath}:{span.LinkLine}" : $"Open {span.LinkPath}"
            };
            label.AddToClassList("chat-link");
            label.RegisterCallback<ClickEvent>(_ => OpenLink(span.LinkPath, span.LinkLine));
            return label;
        }

        private Label MakeContextLabel(AssistantInlineSpan span)
        {
            var label = new Label(span.Text)
            {
                tooltip = "Pin this context for the next assistant turn"
            };
            label.AddToClassList("chat-link");
            label.AddToClassList("chat-context-link");
            label.RegisterCallback<ClickEvent>(_ => PinContext(span.ContextUri));
            return label;
        }

        private Label MakeUrlLabel(AssistantInlineSpan span)
        {
            // span.LinkPath carries the http/https target; opening is an explicit click only (locked
            // decision c). The parser already rejected non-web schemes, so this is always a safe URL.
            var label = new Label(span.Text) { tooltip = $"Open {span.LinkPath}" };
            label.AddToClassList("chat-link");
            label.RegisterCallback<ClickEvent>(_ =>
            {
                if (!string.IsNullOrEmpty(span.LinkPath)) Application.OpenURL(span.LinkPath);
            });
            return label;
        }

        private VisualElement CreateTaskRow(AssistantTextBlock block, ChatTurnKind turnKind)
        {
            var row = new VisualElement();
            row.AddToClassList("chat-list-row");
            row.AddToClassList("chat-task");

            var box = new Label(block.Checked ? "☑" : "☐");
            box.AddToClassList("chat-list-marker");
            box.AddToClassList("chat-task__box");
            if (block.Checked) box.AddToClassList("chat-task__box--checked");
            ApplyTurnTextStyle(box, turnKind);

            var text = CreateInlineBlock(block.RawText, turnKind);
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            if (block.Checked) text.AddToClassList("chat-task__text--checked");

            row.Add(box);
            row.Add(text);
            return row;
        }

        /// <summary>Bolds an inline block whether it rendered as a single <see cref="Label"/> or a container.</summary>
        private static void BoldContent(VisualElement element)
        {
            if (element is Label label) { label.style.unityFontStyleAndWeight = FontStyle.Bold; return; }
            foreach (var child in element.Children())
                if (child is Label l) l.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        private VisualElement CreateTable(AssistantTextBlock block, ChatTurnKind turnKind)
        {
            var table = new VisualElement();
            table.AddToClassList("chat-table");
            if (block.TableRows == null) return table;

            for (var r = 0; r < block.TableRows.Count; r++)
            {
                var cells = block.TableRows[r];
                if (cells == null) continue;

                var rowEl = new VisualElement();
                rowEl.AddToClassList("chat-table__row");
                if (r == 0) rowEl.AddToClassList("chat-table__row--header");

                foreach (var cell in cells)
                {
                    var cellEl = CreateInlineBlock(cell ?? string.Empty, turnKind);
                    cellEl.AddToClassList("chat-table__cell");
                    cellEl.style.flexGrow = 1;
                    cellEl.style.flexBasis = 0;
                    if (r == 0) BoldContent(cellEl);
                    rowEl.Add(cellEl);
                }
                table.Add(rowEl);
            }
            return table;
        }

        private void PinContext(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("molca-context://", StringComparison.Ordinal))
                return;

            var action = uri.Substring("molca-context://".Length);
            if (string.Equals(action, "selection-live", StringComparison.OrdinalIgnoreCase))
            {
                _controller.AddContext(AssistantContextItem.ForSelection(true, null, "Selection (live)"));
                return;
            }
            if (string.Equals(action, "selection-snapshot", StringComparison.OrdinalIgnoreCase))
            {
                _controller.AddContext(AssistantContextItem.ForSelection(false, AssistantEditorContext.DescribeSelection(), SelectionLabel()));
                return;
            }
            if (string.Equals(action, "active-scene", StringComparison.OrdinalIgnoreCase))
            {
                _controller.AddContext(AssistantContextItem.ForActiveScene(ActiveSceneLabel()));
                return;
            }
            if (string.Equals(action, "framework-graph", StringComparison.OrdinalIgnoreCase))
            {
                _controller.AddContext(AssistantContextItem.ForFrameworkGraph());
                return;
            }
            if (string.Equals(action, "kg-status", StringComparison.OrdinalIgnoreCase))
            {
                _controller.AddContext(AssistantContextItem.ForKgStatus());
                return;
            }
            if (action.StartsWith("asset/", StringComparison.OrdinalIgnoreCase))
            {
                PinAssetContext(action.Substring("asset/".Length));
                return;
            }

            _onNotify?.Invoke("Unknown assistant context link.");
        }

        private void PinAssetContext(string guid)
        {
            var path = string.IsNullOrWhiteSpace(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                _onNotify?.Invoke("Context asset is no longer available.");
                return;
            }

            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            _controller.AddContext(AssistantContextItem.ForAsset(guid, obj != null ? obj.name : System.IO.Path.GetFileName(path)));
        }

        private static string SelectionLabel()
        {
            var n = Selection.objects?.Length ?? 0;
            return n == 1 ? $"Selection: {Selection.activeObject?.name}" : $"Selection ({n})";
        }

        private static string ActiveSceneLabel()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? $"Scene: {scene.name}" : "Active Scene";
        }

        private static void OpenLink(string path, int line)
        {
            if (string.IsNullOrEmpty(path)) return;
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj != null)
            {
                AssetDatabase.OpenAsset(obj, line > 0 ? line : -1);
                return;
            }

            var filePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", path);
            InternalEditorUtility.OpenFileAtLineExternal(filePath, line > 0 ? line : 0);
        }

        private VisualElement CreateCodeBlock(string text)
        {
            var label = new Label(text);
            label.AddToClassList("chat-code-block");
            return label;
        }

        private static void ApplyTurnTextStyle(Label label, ChatTurnKind turnKind)
        {
            switch (turnKind)
            {
                case ChatTurnKind.Tool:
                    label.AddToClassList("chat-body--tool");
                    break;
                case ChatTurnKind.Error:
                    label.AddToClassList("chat-body--error");
                    break;
            }
        }

        private static void ApplyBlockStyle(VisualElement element, AssistantTextBlockKind kind)
        {
            switch (kind)
            {
                case AssistantTextBlockKind.Heading:
                    element.style.marginTop = 4;
                    element.style.marginBottom = 4;
                    foreach (var child in element.Children())
                        if (child is Label l) l.style.unityFontStyleAndWeight = FontStyle.Bold;
                    break;
                case AssistantTextBlockKind.Warning:
                    foreach (var child in element.Children())
                        if (child is Label l) l.style.color = MolcaEditorColors.StatusWarn;
                    break;
                case AssistantTextBlockKind.Error:
                    foreach (var child in element.Children())
                        if (child is Label l) l.style.color = MolcaEditorColors.StatusError;
                    break;
            }
        }

        private static void AddRawToolPayloads(VisualElement parent, IReadOnlyList<ChatToolSummary> summaries)
        {
            if (!HasToolPayloads(summaries)) return;

            var foldout = new Foldout { text = summaries.Count > 1 ? "Raw results" : "Raw result", value = false };
            foreach (var summary in summaries)
            {
                if (summary == null || string.IsNullOrWhiteSpace(summary.ResultContent)) continue;

                if (summaries.Count > 1)
                    foldout.Add(new Label(summary.Name) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginTop = 4, marginBottom = 1 } });

                var payload = new Label(AssistantTranscriptFormatter.RedactSecrets(summary.ResultContent));
                payload.AddToClassList("chat-raw-payload");
                foldout.Add(payload);
            }
            parent.Add(foldout);
        }

        private static bool HasToolPayloads(IEnumerable<ChatToolSummary> summaries)
        {
            if (summaries == null) return false;
            foreach (var summary in summaries)
                if (summary != null && !string.IsNullOrWhiteSpace(summary.ResultContent))
                    return true;
            return false;
        }
    }
}
