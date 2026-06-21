using System;
using Molca.Editor.UI;
using Molca.Sequence;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Graph
{
    /// <summary>
    /// A <see cref="GraphView"/> node that represents a single <see cref="Step"/>. Carries one
    /// single-capacity input port (the step that runs before it) and one multi-capacity output port
    /// (the step(s) that run next), matching the controller's execution-order flow model.
    /// </summary>
    /// <remarks>
    /// The node holds a direct reference to its <see cref="Step"/> for selection reconciliation
    /// and play-mode status updates. It never mutates the step itself — all editing routes through
    /// <see cref="StepEditingService"/> from the owning view (Sprint 8.3). Status coloring is applied
    /// via USS classes so it survives repaints without per-frame work.
    /// </remarks>
    public sealed class StepNode : Node
    {
        /// <summary>USS class applied while the step's runtime status is <see cref="StepStatus.Active"/>.</summary>
        public const string ActiveClass = "step-active";

        /// <summary>USS class applied while the step's runtime status is <see cref="StepStatus.Completed"/>.</summary>
        public const string CompletedClass = "step-completed";

        /// <summary>USS class applied to the controller's current step (Sprint 8.5).</summary>
        public const string CurrentClass = "step-current";

        // Title-bar tints for play-mode status, drawn from the shared editor design tokens
        // (MolcaEditorColors) so node coloring matches the rest of the editor and tracks the skin.
        // Properties (not cached fields) so a skin change is picked up. The USS classes above remain
        // for optional theming.
        private static Color ActiveTint => MolcaEditorColors.Primary;
        private static Color CompletedTint => MolcaEditorColors.StatusOk;
        private static readonly StyleColor NoTint = new StyleColor(StyleKeyword.Null);

        // Amber outline marking the controller's current step during play (Sprint 8.5).
        private static Color CurrentBorder => MolcaEditorColors.StatusWarn;

        private static Color ErrorBadgeColor => MolcaEditorColors.StatusError;
        private static Color WarningBadgeColor => MolcaEditorColors.StatusWarn;

        private VisualElement _playControls;
        private Label _validationBadge;
        private Action _onBadgeClick;

        /// <summary>The step this node represents.</summary>
        public Step Step { get; }

        /// <summary>Incoming edge anchor — the step that runs immediately before this one (single capacity).</summary>
        public Port InputPort { get; }

        /// <summary>Outgoing edge anchor — the next step(s): next sibling and/or first child, or parallel fan-out (multi capacity).</summary>
        public Port OutputPort { get; }

        private readonly Label _typeLabel;

        /// <param name="step">The step to wrap; must not be <c>null</c>.</param>
        public StepNode(Step step)
        {
            Step = step;
            // userData lets selection/lookup code recover the step from any VisualElement.
            userData = step;

            // Single parent in: capacity Single. Many children out: capacity Multi.
            // Port orientation is vertical so the graph reads top-down like the hierarchy.
            InputPort = InstantiatePort(Orientation.Vertical, Direction.Input, Port.Capacity.Single, typeof(bool));
            InputPort.portName = string.Empty;
            inputContainer.Add(InputPort);

            OutputPort = InstantiatePort(Orientation.Vertical, Direction.Output, Port.Capacity.Multi, typeof(bool));
            OutputPort.portName = string.Empty;
            outputContainer.Add(OutputPort);

            _typeLabel = new Label(step.GetType().Name) { name = "step-type-label" };
            _typeLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _typeLabel.style.opacity = 0.7f;
            _typeLabel.style.marginLeft = 6;
            _typeLabel.style.marginRight = 6;
            mainContainer.Add(_typeLabel);

            ApplyCategoryVisual(step);
            BuildPlayControls();

            RefreshTitle();
            RefreshExpandedState();
            RefreshPorts();
        }

        // Distinct visual for control-flow step types (Sprint 8.6): a coloured category chip in the
        // header. Plain steps get no chip. Edge colouring (parallel/branch) is applied by the view.
        private void ApplyCategoryVisual(Step step)
        {
            string text;
            Color color;
            switch (step)
            {
                case ParallelStep: text = "∥ Parallel"; color = MolcaEditorColors.Primary; break;
                case BranchingStep: text = "⌥ Branch"; color = MolcaEditorColors.StatusWarn; break;
                case ConditionalStep: text = "? Condition"; color = MolcaEditorColors.StatusWarn; break;
                default: return;
            }

            var chip = new Label(text) { name = "category-chip" };
            chip.style.color = Color.white;
            chip.style.backgroundColor = color;
            chip.style.fontSize = 10;
            chip.style.paddingLeft = 5;
            chip.style.paddingRight = 5;
            chip.style.paddingTop = 1;
            chip.style.paddingBottom = 1;
            chip.style.marginRight = 6;
            chip.style.borderTopLeftRadius = chip.style.borderTopRightRadius = 3;
            chip.style.borderBottomLeftRadius = chip.style.borderBottomRightRadius = 3;
            chip.style.alignSelf = Align.Center;
            titleContainer.Add(chip);
        }

        // Play-mode runtime controls. These call Step directly (runtime control, not structural
        // editing) — the same pattern the visualizer's runtime panel uses. Hidden in edit mode.
        private void BuildPlayControls()
        {
            _playControls = new VisualElement { name = "play-controls" };
            _playControls.style.flexDirection = FlexDirection.Row;
            _playControls.style.marginLeft = 4;
            _playControls.style.marginRight = 4;
            _playControls.style.marginBottom = 4;

            var activate = new Button(() => { if (Step != null) Step.SetStatus(StepStatus.Active); }) { text = "Activate" };
            var complete = new Button(() => { if (Step != null) Step.Complete(); }) { text = "Complete" };
            activate.style.flexGrow = 1;
            complete.style.flexGrow = 1;
            _playControls.Add(activate);
            _playControls.Add(complete);

            _playControls.style.display = DisplayStyle.None;
            mainContainer.Add(_playControls);
        }

        /// <summary>Shows or hides the play-mode runtime controls (Activate/Complete).</summary>
        public void SetPlayMode(bool playing)
        {
            if (_playControls != null)
                _playControls.style.display = playing ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Shows or hides a validation badge in the header (Sprint 8.7). When findings exist the badge
        /// displays an error/warning glyph, carries <paramref name="tooltip"/>, and invokes
        /// <paramref name="onClick"/> (the window's click-to-fix menu) when clicked.
        /// </summary>
        public void SetValidation(bool hasError, bool hasWarning, string tooltip, Action onClick)
        {
            _onBadgeClick = onClick;

            if (!hasError && !hasWarning)
            {
                if (_validationBadge != null) _validationBadge.style.display = DisplayStyle.None;
                return;
            }

            if (_validationBadge == null)
            {
                _validationBadge = new Label { name = "validation-badge" };
                _validationBadge.style.color = Color.white;
                _validationBadge.style.fontSize = 11;
                _validationBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
                _validationBadge.style.paddingLeft = 5;
                _validationBadge.style.paddingRight = 5;
                _validationBadge.style.marginLeft = 4;
                _validationBadge.style.alignSelf = Align.Center;
                _validationBadge.style.borderTopLeftRadius = _validationBadge.style.borderTopRightRadius = 3;
                _validationBadge.style.borderBottomLeftRadius = _validationBadge.style.borderBottomRightRadius = 3;
                // Click anywhere on the badge opens the fix/inspect menu.
                _validationBadge.RegisterCallback<MouseDownEvent>(_ => _onBadgeClick?.Invoke());
                titleContainer.Add(_validationBadge);
            }

            _validationBadge.text = hasError ? "⛔ !" : "⚠ !";
            _validationBadge.style.backgroundColor = hasError ? ErrorBadgeColor : WarningBadgeColor;
            _validationBadge.tooltip = tooltip;
            _validationBadge.style.display = DisplayStyle.Flex;
        }

        /// <summary>Marks (or clears) this node as the controller's current step with an outline (Sprint 8.5).</summary>
        public void SetCurrent(bool isCurrent)
        {
            if (isCurrent)
            {
                AddToClassList(CurrentClass);
                style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 2f;
                var color = new StyleColor(CurrentBorder);
                style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = color;
            }
            else
            {
                RemoveFromClassList(CurrentClass);
                style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 0f;
            }
        }

        /// <summary>Re-reads the step's name into the node title (call after a rename).</summary>
        public void RefreshTitle()
        {
            if (Step == null) return;
            title = Step.name;
            _typeLabel.text = Step.GetType().Name;
        }

        /// <summary>
        /// Applies the current runtime status as a USS class for play-mode coloring.
        /// Safe to call every status change; clears prior status classes first.
        /// </summary>
        public void RefreshStatus()
        {
            RemoveFromClassList(ActiveClass);
            RemoveFromClassList(CompletedClass);

            var titleBar = titleContainer;
            if (Step == null)
            {
                if (titleBar != null) titleBar.style.backgroundColor = NoTint;
                return;
            }

            switch (Step.CurrentStatus)
            {
                case StepStatus.Active:
                    AddToClassList(ActiveClass);
                    if (titleBar != null) titleBar.style.backgroundColor = ActiveTint;
                    break;
                case StepStatus.Completed:
                    AddToClassList(CompletedClass);
                    if (titleBar != null) titleBar.style.backgroundColor = CompletedTint;
                    break;
                default:
                    if (titleBar != null) titleBar.style.backgroundColor = NoTint;
                    break;
            }
        }
    }
}
