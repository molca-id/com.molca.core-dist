#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ColorID;
using Molca.Editor.Upgrade;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEditor;
// ColorField and the other editor-only controls live here, not in UnityEngine.UIElements.
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// The Themes workspace: one window over the colour contract, its health, its usage and its migration
    /// (revamp plan §11.1).
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Hub/</c>.
    /// <b>Registration:</b> contributed as a Hub workspace by <see cref="ColorThemeHubWorkspaceProvider"/>.
    /// <para/>
    /// <b>The view holds no logic of its own.</b> Every number comes from
    /// <see cref="ColorThemeWorkspaceModel"/> and every mutation goes through the existing services —
    /// <see cref="ColorThemeEditSession"/> for values, the transaction planner for anything structural, the
    /// USS generator, the interchange. The plan states that as a requirement; the practical consequence is
    /// that this file can be read as "what is shown", with no risk that a rule lives only here and disagrees
    /// with the CLI.
    /// <para/>
    /// <b>Shape: authoring first, report second.</b> The four destinations are Tokens and Palette (where you
    /// change things), then Health and Manage (where you find out what you changed). Tokens is a list of
    /// expandable rows rather than a token-by-variant grid: a grid compares variants well and edits a token
    /// badly, because a token's kind, description, aliases, contrast and usage have nowhere to go except a
    /// tooltip. The grid survives as <i>Compare</i>, read-only, which is the job it is good at.
    /// <para/>
    /// <b>Two refresh paths, not one.</b> A value edit re-resolves the asset in memory
    /// (<see cref="ColorThemeWorkspaceModel.WithRefreshedValues"/>) and repaints only what derives from a
    /// value. Only an explicit Refresh, or a structural transaction, re-scans the project. Rebuilding
    /// everything after every edit — which is what this workspace used to do — meant a walk over closed
    /// scenes per frame of a colour-picker drag, and a rebuild destroys the field being dragged.
    /// </remarks>
    public sealed class ColorThemeWorkspaceView : VisualElement
    {
        private const string TokensMode = "tokens";
        private const string PaletteMode = "palette";
        private const string HealthMode = "health";
        private const string ManageMode = "manage";

        /// <summary>Height one rail row occupies: its 29px minimum plus the button's default margins.</summary>
        private const float RailRowHeight = 33f;

        /// <summary>Rail destinations, for the short-dock height check.</summary>
        private const int RailRowCount = 4;

        /// <summary>Left-rail width, per the editor design language.</summary>
        private const int RailWidth = 188;

        /// <summary>The rail's own vertical padding, from <c>.molca-hub-rail</c>.</summary>
        private const float RailPadding = 16f;

        /// <summary>Rows rendered in a list before the rest are summarised.</summary>
        /// <remarks>
        /// A cap rather than a scroll: building a thousand rows costs real time in UI Toolkit and nobody
        /// reads the four hundredth. The overflow line states how many were withheld, so the cap never reads
        /// as "that was all of them".
        /// </remarks>
        private const int MaxRows = 120;

        private readonly MolcaNavRail _rail = new MolcaNavRail("Search");
        private readonly VisualElement _content = new VisualElement();
        private readonly MolcaWorkspaceHeader _header = new MolcaWorkspaceHeader("Themes");
        private readonly TwoPaneSplitView _split =
            new TwoPaneSplitView(0, RailWidth, TwoPaneSplitViewOrientation.Horizontal);
        private readonly VisualElement _preview = new VisualElement();

        /// <summary>
        /// Closures that repaint one value-derived element each, run after a live edit.
        /// </summary>
        /// <remarks>
        /// The alternative — re-rendering the surface — destroys the control the author is dragging, and the
        /// colour picker is still holding a callback into it. Each builder that paints a swatch, a hex label
        /// or a contrast badge registers how to repaint just that piece, so a live edit touches no control.
        /// Cleared on every surface render, because the elements they close over stop existing.
        /// </remarks>
        private readonly List<Action> _liveRefreshers = new List<Action>();

        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);

        private ColorThemeEditSession _session;
        private ColorThemeWorkspaceModel _model;
        private string _tokenFilter = string.Empty;
        private string _paletteFilter = string.Empty;
        private ColorTokenKind? _kindFilter = ColorTokenKind.Semantic;
        private bool _problemsOnly;
        private bool _compare;
        private bool _previewVisible = true;
        private string _activeVariantId;
        private bool? _stacked;
        private PopupField<string> _variantPicker;

        /// <summary>Creates the workspace.</summary>
        public ColorThemeWorkspaceView()
        {
            // A hostable view carries its own design language rather than inheriting the Hub's: the editor
            // design language allows this same element to be hosted standalone, and Apply is idempotent.
            MolcaEditorUi.Apply(this);
            AddToClassList("molca-workspace");
            AddToClassList("molca-workspace--railed");

            BuildHeader();
            Add(_header);

            _split.AddToClassList("molca-workspace-split");
            Add(_split);

            _rail.SetRoots(new[]
            {
                Leaf(TokensMode, "Tokens"),
                Leaf(PaletteMode, "Palette"),
                Leaf(HealthMode, "Health"),
                Leaf(ManageMode, "Manage"),
            });
            _rail.NodeSelected += _ => RenderContent();
            _split.Add(_rail);

            // Vertical only. The fixed-width tables carry their own sideways scroll (see TableScroll), so the
            // page itself always knows how wide it is — which is what lets a card fill the window and a
            // wrapping note wrap, and what keeps a short dock from spending its last pixels of height on a
            // horizontal scrollbar the visible panel does not need.
            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { minHeight = 0 } };

            // The pane class belongs on the scroll view, because the scroll view is the pane. On the element
            // *inside* the scroll it makes that element a flex child which wants to fill the viewport and,
            // with the default flex-shrink of 1, one the viewport can also squeeze — and the squeeze lands on
            // the cards and rows below it, which have no min-height and so overlap their own text.
            scroll.AddToClassList("molca-workspace-split__content");
            scroll.Add(_content);
            _split.Add(scroll);

            _preview.AddToClassList("molca-theme-preview");
            Add(_preview);

            _session = new ColorThemeEditSession(() => _model?.ThemeSet, this);
            _session.Changed += OnValueEdited;

            _split.RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(evt.newRect.height));

            // An edit is in memory until the debounce fires, so a view that goes away first has to commit.
            RegisterCallback<DetachFromPanelEvent>(_ => _session?.Flush());

            Rebuild();
            _rail.SelectNodeById(TokensMode);
        }

        /// <summary>A flat rail row: a leaf with no children, whose content this view renders itself.</summary>
        private static MolcaNavRailNode Leaf(string id, string label) =>
            new MolcaNavRailNode(id, label, () => null);

        #region Shell

        private void BuildHeader()
        {
            _variantPicker = new PopupField<string>(new List<string> { "—" }, 0)
            {
                tooltip = "The variant the preview resolves, and the column Compare highlights."
            };
            _variantPicker.AddToClassList("molca-theme-variant-picker");
            _variantPicker.RegisterValueChangedCallback(evt =>
            {
                _activeVariantId = evt.newValue;
                RenderPreview();
                if (_compare) RenderContent();
            });
            _header.Actions.Add(_variantPicker);

            var preview = MolcaButtons.Toolbar("Preview", () =>
            {
                _previewVisible = !_previewVisible;
                RenderPreview();
            });
            preview.tooltip = "Show or hide the live preview strip.";
            _header.AddAction(preview);

            _header.AddAction(MolcaButtons.Toolbar("Refresh", Rebuild));
        }

        /// <summary>Chooses between the rail-beside-content and rail-above-content layouts.</summary>
        /// <remarks>
        /// Height, not width: the Hub is commonly docked as a short strip, and the vertical axis is what the
        /// rail and the content compete for. A rail row has a minimum height and does not shrink below it, and
        /// the rail is not a scroll view — so in a pane too short for its rows, the rows that do not fit
        /// simply leave the box and the last destinations become unreachable with nothing on screen saying so.
        /// <para/>
        /// Measured against the split's height rather than the workspace's, and against what this rail
        /// actually needs rather than a fixed number. Both matter: the header and the preview strip take
        /// height the rail never sees, and a destination added later changes the answer. A hard-coded
        /// threshold would flip a four-row rail in a pane it fits in fine.
        /// <para/>
        /// Guarded on the previous state because a geometry change fires on every resize frame and re-styling
        /// the rail invalidates layout, which would fire it again. Flipping cannot change the split's own
        /// height — that comes from the parent — so there is no oscillation to worry about.
        /// </remarks>
        private void ApplyResponsiveLayout(float splitHeight)
        {
            bool stacked = splitHeight > 0f && splitHeight < RailRowCount * RailRowHeight + RailPadding;
            if (_stacked == stacked) return;

            _stacked = stacked;
            _split.EnableInClassList("molca-workspace-split--stacked", stacked);

            // The rail is a draggable pane now, so "stacked" is the split's own orientation rather than a
            // flex-direction override — otherwise the CSS and the splitter would disagree about which axis
            // the drag handle belongs on.
            _split.orientation = stacked
                ? TwoPaneSplitViewOrientation.Vertical
                : TwoPaneSplitViewOrientation.Horizontal;
        }

        /// <summary>Re-reads every service and redraws. Runs a full project scan.</summary>
        private void Rebuild()
        {
            // A pending value edit must reach disk before a scan reads the asset, or the scan reports the
            // state before the edit and the window contradicts itself.
            _session?.Flush();

            // A full audit walks the project, so the window says what it is doing rather than appearing to
            // hang. Cleared in the finally so a failed audit does not leave a stale "scanning" message.
            _header.SetSummary("Scanning project…");
            try
            {
                _model = ColorThemeWorkspaceModel.Build();
            }
            catch (Exception exception)
            {
                _model = null;
                Debug.LogError($"[ColorTheme] The Themes workspace could not read project state: {exception}");
            }
            finally
            {
                _header.SetSummary(DescribeHealth());
            }

            SyncVariantPicker();
            RenderContent();
            RenderPreview();
        }

        /// <summary>
        /// Re-resolves values without re-scanning, then repaints only what a value can change.
        /// </summary>
        private void OnValueEdited()
        {
            if (_model == null) return;

            _model = _model.WithRefreshedValues();
            _header.SetSummary(DescribeHealth());

            foreach (var refresh in _liveRefreshers) refresh();
            RenderPreview();
        }

        private void SyncVariantPicker()
        {
            var variants = _model == null || _model.VariantIds.Count == 0
                ? new List<string> { "—" }
                : _model.VariantIds.ToList();

            if (_activeVariantId == null || !variants.Contains(_activeVariantId))
                _activeVariantId = _model?.DefaultVariantId ?? variants[0];

            _variantPicker.choices = variants;
            _variantPicker.SetValueWithoutNotify(_activeVariantId);
            _variantPicker.SetEnabled(variants.Count > 1);
        }

        private string DescribeHealth()
        {
            if (_model == null) return "Could not read project state — see the Console.";

            string stale = _model.ValuesAreNewerThanScan ? " · edited since the last scan" : "";

            switch (_model.Health)
            {
                case MolcaStatusForTheme.NotInstalled: return "No Color Theme Set installed.";
                case MolcaStatusForTheme.Incomplete:
                    return $"Coverage incomplete — {_model.Audit.SkippedInputs.Count} input(s) skipped{stale}.";
                case MolcaStatusForTheme.Errors:
                    return $"{_model.Audit.Findings.Count} finding(s), including build-blocking errors{stale}.";
                case MolcaStatusForTheme.Warnings:
                    return $"{_model.Audit.Findings.Count} finding(s), none blocking{stale}.";
                default:
                    return $"Clean — {_model.Tokens.Count} tokens across "
                           + $"{_model.VariantIds.Count} variants{stale}.";
            }
        }

        private static MolcaStatusKind StatusDot(MolcaStatusForTheme health)
        {
            switch (health)
            {
                case MolcaStatusForTheme.Clean: return MolcaStatusKind.Ok;
                case MolcaStatusForTheme.Warnings: return MolcaStatusKind.Warning;
                case MolcaStatusForTheme.Errors: return MolcaStatusKind.Error;
                case MolcaStatusForTheme.Incomplete: return MolcaStatusKind.Warning;
                default: return MolcaStatusKind.Idle;
            }
        }

        private void RenderContent()
        {
            _content.Clear();
            _liveRefreshers.Clear();

            if (_model == null)
            {
                _content.Add(Note("The workspace could not read project state. See the Console."));
                return;
            }

            if (!_model.IsInstalled && _rail.SelectedNode?.Id != ManageMode)
            {
                RenderNotInstalled();
                return;
            }

            switch (_rail.SelectedNode?.Id)
            {
                case PaletteMode: RenderPalette(); break;
                case HealthMode: RenderHealth(); break;
                case ManageMode: RenderManage(); break;
                default: RenderTokens(); break;
            }
        }

        private void RenderNotInstalled()
        {
            var card = new MolcaSectionCard("V2 is not installed", "This project still resolves colour "
                + "through V1 ColorModule palettes.", MolcaStatusKind.Idle);

            card.Body.Add(Note("Installing writes the canonical vocabulary to an asset and registers a "
                               + "ColorThemeSettings module. Existing content keeps working: every legacy "
                               + "pair resolves through the alias map."));
            card.Body.Add(Row(
                MolcaButtons.Primary("Create vocabulary asset", ColorThemeSetBootstrap.CreateOrUpdate),
                MolcaButtons.Mini("Install settings (V1 → V2)", () =>
                {
                    ColorThemeInstaller.Install();
                    Rebuild();
                })));

            _content.Add(card);
        }

        #endregion

        #region Preview

        /// <summary>
        /// Paints the persistent preview strip for the active variant.
        /// </summary>
        /// <remarks>
        /// A strip rather than a destination, because the point of a preview is to be visible while you edit
        /// — a preview you have to navigate to is a report. It shows a <i>composition</i> (a surface carrying
        /// text, a border and an accent) and not only swatches, because a contrast problem is invisible in
        /// two separate squares and obvious in one drawn on the other.
        /// <para/>
        /// Rebuilt wholesale on each edit. It is a handful of elements and holds no control an author can be
        /// mid-gesture on, so it is the one part of the window that does not need in-place refresh.
        /// </remarks>
        private void RenderPreview()
        {
            _preview.Clear();
            _preview.style.display = _previewVisible && _model != null && _model.IsInstalled
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            if (!_previewVisible || _model == null || !_model.IsInstalled) return;

            string variantId = _activeVariantId;
            if (variantId == null) return;

            var caption = new Label($"Preview — {variantId}");
            caption.AddToClassList("molca-theme-preview__caption");
            _preview.Add(caption);

            var body = new VisualElement();
            body.AddToClassList("molca-theme-preview__body");
            _preview.Add(body);

            body.Add(BuildComposition(variantId));
            body.Add(BuildSwatchWall(variantId));
        }

        /// <summary>One representative composition: text and an accent drawn on a real surface.</summary>
        private VisualElement BuildComposition(string variantId)
        {
            var surface = FirstTokenFor(ColorTokenUsage.Surface, variantId);
            var text = FirstTokenFor(ColorTokenUsage.Text, variantId);
            var border = FirstTokenFor(ColorTokenUsage.Border, variantId);
            var accent = FirstTokenFor(ColorTokenUsage.Status, variantId)
                         ?? FirstTokenFor(ColorTokenUsage.Focus, variantId);

            var panel = new VisualElement();
            panel.AddToClassList("molca-theme-preview__panel");

            if (surface != null) panel.style.backgroundColor = new StyleColor(ColorOf(surface, variantId));
            if (border != null)
            {
                var edge = new StyleColor(ColorOf(border, variantId));
                panel.style.borderTopColor = edge;
                panel.style.borderBottomColor = edge;
                panel.style.borderLeftColor = edge;
                panel.style.borderRightColor = edge;
            }

            var sample = new Label("The quick brown fox");
            sample.AddToClassList("molca-theme-preview__text");
            if (text != null) sample.style.color = new StyleColor(ColorOf(text, variantId));
            panel.Add(sample);

            if (accent != null)
            {
                Color accentColor = ColorOf(accent, variantId);
                var chip = new Label("Accent");
                chip.AddToClassList("molca-theme-preview__chip");
                chip.style.backgroundColor = new StyleColor(accentColor);

                // Black or white by luminance rather than by a token: this label exists to prove the accent
                // is legible against *something*, and picking a theme token here would make the chip a second
                // contrast claim the theme has not declared.
                chip.style.color = new StyleColor(Luminance(accentColor) > 0.5f ? Color.black : Color.white);
                panel.Add(chip);
            }

            var used = new[] { surface, text, border, accent }.Where(id => id != null).ToArray();
            var legend = new Label(used.Length == 0
                ? "No token declares a Surface, Text, Border or Status usage, so there is nothing to compose."
                : "Composed from " + string.Join(" · ", used));
            legend.AddToClassList("molca-theme-preview__legend");

            var column = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            column.Add(panel);
            column.Add(legend);
            return column;
        }

        /// <summary>Every semantic token as a tile, so a palette edit is visible in bulk.</summary>
        private VisualElement BuildSwatchWall(string variantId)
        {
            var wall = new VisualElement();
            wall.AddToClassList("molca-theme-preview__wall");

            foreach (var row in _model.Tokens.Where(t => t.Definition.Kind == ColorTokenKind.Semantic))
            {
                bool resolved = row.Values.TryGetValue(variantId, out Color color);
                var tile = Swatch(resolved ? color : Color.magenta, 13, 13);
                tile.tooltip = resolved
                    ? $"{row.Definition.Id} — {ToHex(color)}"
                    : $"{row.Definition.Id} — unresolved in '{variantId}'";
                wall.Add(tile);
            }

            return wall;
        }

        private string FirstTokenFor(ColorTokenUsage usage, string variantId) =>
            _model.Tokens.FirstOrDefault(t =>
                    t.Definition.Kind == ColorTokenKind.Semantic
                    && !t.Definition.Deprecated
                    && t.Definition.Usage.HasFlag(usage)
                    && t.Values.ContainsKey(variantId))
                ?.Definition.Id;

        private Color ColorOf(string tokenId, string variantId)
        {
            var row = _model.Tokens.FirstOrDefault(t => t.Definition.Id == tokenId);
            return row != null && row.Values.TryGetValue(variantId, out Color color) ? color : Color.magenta;
        }

        /// <summary>Perceptual-ish luminance, enough to choose between black and white text.</summary>
        private static float Luminance(Color color) =>
            0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;

        #endregion

        #region Tokens

        private void RenderTokens()
        {
            var card = new MolcaSectionCard("Tokens",
                "The roles content binds to. Expand a row to author its value in every variant.");

            var search = new MolcaSearchField("Search id, description or alias");
            search.OnSearchChanged += value =>
            {
                _tokenFilter = value ?? string.Empty;
                RenderContent();
            };
            card.Body.Add(search);

            card.Body.Add(BuildTokenFilters());

            if (CanEdit)
            {
                card.Body.Add(Row(
                    MolcaButtons.Mini("Add token…", PromptAddToken),
                    MolcaButtons.Mini("Rename token…", PromptRenameToken),
                    MolcaButtons.Mini("Map legacy pair…", PromptAddAlias)));
            }
            else if (_model.IsInstalled)
            {
                card.Body.Add(Note(ColorThemeAssetWriteAccess.DescribeRefusal(_model.ThemeSetPath)
                                   ?? "This theme set is read-only."));
            }

            var rows = FilterTokens().ToList();
            card.SetSubtitle($"{rows.Count} of {_model.Tokens.Count} tokens");

            if (_compare) card.Body.Add(BuildCompareTable(rows));
            else card.Body.Add(BuildTokenList(rows));

            if (rows.Count == 0) card.Body.Add(Note("No token matches this filter."));

            _content.Add(card);
        }

        private VisualElement BuildTokenFilters()
        {
            var bar = new MolcaWorkspaceToolbar();

            var kinds = new List<string> { "Semantic", "Primitive", "All kinds" };
            int selected = _kindFilter == ColorTokenKind.Semantic ? 0
                : _kindFilter == ColorTokenKind.Primitive ? 1 : 2;

            var kind = new PopupField<string>(kinds, selected);
            kind.RegisterValueChangedCallback(evt =>
            {
                _kindFilter = evt.newValue == "Semantic" ? ColorTokenKind.Semantic
                    : evt.newValue == "Primitive" ? ColorTokenKind.Primitive
                    : (ColorTokenKind?)null;
                RenderContent();
            });
            bar.Content.Add(kind);

            var problems = new Toggle("Needs attention only") { value = _problemsOnly };
            problems.tooltip = "Tokens missing from a variant, failing a contrast requirement, or deprecated.";
            problems.RegisterValueChangedCallback(evt =>
            {
                _problemsOnly = evt.newValue;
                RenderContent();
            });
            bar.Content.Add(problems);

            var compare = MolcaButtons.Mini(_compare ? "Editing view" : "Compare variants", () =>
            {
                _compare = !_compare;
                RenderContent();
            });
            compare.tooltip = _compare
                ? "Back to the editable list."
                : "A read-only token-by-variant grid, for comparing across variants.";
            bar.AddAction(compare);

            return bar;
        }

        private IEnumerable<ColorThemeTokenRow> FilterTokens()
        {
            foreach (var row in _model.Tokens)
            {
                if (_kindFilter.HasValue && row.Definition.Kind != _kindFilter.Value) continue;
                if (_problemsOnly && !NeedsAttention(row)) continue;

                if (_tokenFilter.Length > 0)
                {
                    bool matches =
                        Contains(row.Definition.Id, _tokenFilter)
                        || Contains(row.Definition.Description, _tokenFilter)
                        || row.Sources.Values.Any(alias => Contains(alias, _tokenFilter));
                    if (!matches) continue;
                }

                yield return row;
            }
        }

        private bool NeedsAttention(ColorThemeTokenRow row) =>
            row.Definition.Deprecated
            || !row.IsComplete(_model.VariantIds)
            || FailingContrastFor(row.Definition.Id).Any();

        private VisualElement BuildTokenList(IReadOnlyList<ColorThemeTokenRow> rows)
        {
            var list = new VisualElement();
            list.AddToClassList("molca-list");

            foreach (var row in rows.Take(MaxRows)) list.Add(BuildTokenRow(row));

            if (rows.Count > MaxRows)
                list.Add(Note($"… and {rows.Count - MaxRows} more. Narrow the filter to reach them."));

            return list;
        }

        private VisualElement BuildTokenRow(ColorThemeTokenRow row)
        {
            var line = new MolcaListRow(row.Definition.Id, DescribeDefinition(row.Definition));

            var strip = new VisualElement();
            strip.AddToClassList("molca-theme-strip");
            PaintStrip(strip, row.Definition.Id);
            line.AddMetadata(strip);

            var contrast = new MolcaStatusBadge();
            PaintContrastBadge(contrast, row);
            line.AddMetadata(contrast);

            var uses = Cell(DescribeUsage(row), 74);
            uses.AddToClassList("molca-list-row__meta");
            uses.tooltip = _model.ValuesAreNewerThanScan
                ? "References found by the last project scan. Values have been edited since, so this count "
                  + "predates those edits — Refresh to re-scan."
                : "References found by the last project scan.";
            line.AddMetadata(uses);

            // Registered once per row, not per element, so a live edit repaints both indicators together and
            // they can never disagree about which model they were painted from.
            _liveRefreshers.Add(() =>
            {
                var current = TokenById(row.Definition.Id);
                if (current == null) return;
                PaintStrip(strip, row.Definition.Id);
                PaintContrastBadge(contrast, current);
            });

            line.AddDetail(BuildTokenDetail(row));
            if (_expanded.Contains(row.Definition.Id)) line.SetExpanded(true);

            // The component owns the disclosure control, so expansion state is read back rather than driven:
            // this records what the author opened so a re-render restores it.
            line.RegisterCallback<ClickEvent>(_ => RecordExpansion(row.Definition.Id, line));

            return line;
        }

        private void RecordExpansion(string tokenId, MolcaListRow line)
        {
            if (line.Expanded) _expanded.Add(tokenId);
            else _expanded.Remove(tokenId);
        }

        private VisualElement BuildTokenDetail(ColorThemeTokenRow row)
        {
            var detail = new VisualElement();

            if (!string.IsNullOrEmpty(row.Definition.Description))
                detail.Add(Note(row.Definition.Description));

            if (row.Definition.Deprecated)
            {
                var replacement = string.IsNullOrEmpty(row.Definition.ReplacementId)
                    ? "No replacement is declared, which is itself a finding."
                    : $"Use {row.Definition.ReplacementId} instead.";
                detail.Add(Warn($"Deprecated. {replacement}"));
            }

            foreach (string variantId in _model.VariantIds)
                detail.Add(BuildVariantEditor(row, variantId));

            var failing = FailingContrastFor(row.Definition.Id).ToList();
            foreach (var contrast in failing)
            {
                detail.Add(Warn($"{contrast.Requirement.ForegroundTokenId} on "
                                + $"{contrast.Requirement.BackgroundTokenId} needs "
                                + $"{contrast.Requirement.MinimumRatio:F1}:1 and does not reach it in "
                                + string.Join(", ", contrast.FailingVariants)));
            }

            var dependents = DependentsOf(row.Definition.Id);
            if (dependents.Count > 0)
            {
                detail.Add(DetailRow("Aliased by",
                    $"{dependents.Count} token(s): {string.Join(", ", dependents.Take(8))}"
                    + (dependents.Count > 8 ? ", …" : "")));
            }

            return detail;
        }

        /// <summary>
        /// One variant's authored value, edited through the control that matches how it was authored.
        /// </summary>
        /// <remarks>
        /// The mode selector is the part that was missing: the workspace could turn an alias into a literal
        /// (<c>Detach</c>) but never the other way, so the one lever that makes a theme cheap to change —
        /// point many semantic tokens at one primitive, edit the primitive once — could only be dismantled
        /// from here, never built.
        /// </remarks>
        private VisualElement BuildVariantEditor(ColorThemeTokenRow row, string variantId)
        {
            var line = Row();
            line.AddToClassList("molca-theme-editor");

            var label = Cell(variantId, 96, bold: true);
            label.tooltip = variantId == _model.DefaultVariantId ? "The default variant." : variantId;
            line.Add(label);

            row.Expressions.TryGetValue(variantId, out var expression);
            var kind = expression?.ExpressionKind;

            var modes = new List<string> { "Literal", "Alias", "Alias × alpha" };
            int mode = kind == ColorExpression.Kind.Alias ? 1
                : kind == ColorExpression.Kind.AliasWithAlpha ? 2
                : 0;

            if (expression == null)
            {
                // Named rather than left blank: a blank reads as "no opinion", and a required token missing
                // from a variant is the most consequential state in this window.
                line.Add(Warn($"missing in {variantId}"));

                if (CanEdit)
                {
                    var set = MolcaButtons.Mini("Set literal", () =>
                    {
                        Fallback(row, variantId, out Color seed);
                        WriteExpression(row, variantId, ColorExpression.FromLiteral(seed));
                    });
                    set.tooltip = $"Give '{variantId}' a value for this token.";
                    line.Add(set);
                }

                return line;
            }

            var picker = new PopupField<string>(modes, mode) { tooltip = "How this variant supplies the value." };
            picker.style.width = 110;
            picker.style.flexShrink = 0;
            picker.SetEnabled(CanEdit);
            picker.RegisterValueChangedCallback(evt => ChangeMode(row, variantId, evt.newValue, expression));
            line.Add(picker);

            switch (kind)
            {
                case ColorExpression.Kind.Alias:
                case ColorExpression.Kind.AliasWithAlpha:
                    line.Add(BuildAliasEditor(row, variantId, expression));
                    break;
                default:
                    line.Add(BuildLiteralEditor(row, variantId));
                    break;
            }

            return line;
        }

        private VisualElement BuildLiteralEditor(ColorThemeTokenRow row, string variantId)
        {
            row.Values.TryGetValue(variantId, out Color color);

            var field = new ColorField { value = color, showAlpha = true };
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.SetEnabled(CanEdit);

            // No rebuild here. This fires on every frame the picker moves; the session writes and the view
            // repaints its derived parts, but this field survives the gesture that is driving it.
            field.RegisterValueChangedCallback(evt =>
                WriteExpression(row, variantId, ColorExpression.FromLiteral(evt.newValue), rerender: false));

            return field;
        }

        private VisualElement BuildAliasEditor(ColorThemeTokenRow row, string variantId,
            ColorExpression expression)
        {
            var host = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1, minWidth = 0 } };

            var primitives = PrimitiveIds(row.Definition.Id);
            string target = expression.AliasTokenId;

            if (string.IsNullOrEmpty(target))
            {
                // An alias expression with no target is malformed data, not an authoring state. Saying so
                // beats handing a null to a popup, which would throw while drawing the row.
                host.Add(Warn("Aliased, but no target token is recorded — the asset is malformed here."));
                return host;
            }

            // The current target is offered even when it is not a primitive, because the row has to be able
            // to show what the asset actually says before it can help change it.
            if (!primitives.Contains(target)) primitives.Insert(0, target);

            var swatch = Swatch(row.Values.TryGetValue(variantId, out Color resolved) ? resolved : Color.magenta);
            host.Add(swatch);

            var picker = new PopupField<string>(primitives, Mathf.Max(0, primitives.IndexOf(target)))
            {
                tooltip = "The primitive this variant points at."
            };
            picker.style.flexGrow = 1;
            picker.style.flexShrink = 1;
            picker.style.minWidth = 0;
            picker.SetEnabled(CanEdit);
            picker.RegisterValueChangedCallback(evt => WriteExpression(row, variantId,
                expression.ExpressionKind == ColorExpression.Kind.AliasWithAlpha
                    ? ColorExpression.FromAliasWithAlpha(evt.newValue, expression.AlphaMultiplier)
                    : ColorExpression.FromAlias(evt.newValue)));
            host.Add(picker);

            if (expression.ExpressionKind == ColorExpression.Kind.AliasWithAlpha)
            {
                var alpha = new FloatField { value = expression.AlphaMultiplier, isDelayed = true };
                alpha.style.width = 56;
                alpha.style.flexShrink = 0;
                alpha.tooltip = "Alpha multiplier applied to the aliased colour.";
                alpha.SetEnabled(CanEdit);
                alpha.RegisterValueChangedCallback(evt => WriteExpression(row, variantId,
                    ColorExpression.FromAliasWithAlpha(picker.value, Mathf.Clamp01(evt.newValue))));
                host.Add(alpha);
            }

            var hex = new Label(row.Values.TryGetValue(variantId, out Color value) ? ToHex(value) : "unresolved");
            hex.AddToClassList("molca-theme-editor__hex");
            host.Add(hex);

            _liveRefreshers.Add(() =>
            {
                var current = TokenById(row.Definition.Id);
                if (current == null) return;

                bool has = current.Values.TryGetValue(variantId, out Color now);
                swatch.style.backgroundColor = new StyleColor(has ? now : Color.magenta);
                swatch.tooltip = has ? ToHex(now) : "unresolved";
                hex.text = has ? ToHex(now) : "unresolved";
            });

            return host;
        }

        /// <summary>Converts one variant's value between literal, alias, and alias-with-alpha.</summary>
        private void ChangeMode(ColorThemeTokenRow row, string variantId, string mode,
            ColorExpression current)
        {
            if (!CanEdit) return;

            bool wasAlias = current.ExpressionKind != ColorExpression.Kind.Literal;
            row.Values.TryGetValue(variantId, out Color resolved);

            if (mode == "Literal")
            {
                // Confirmed, because this is the one conversion that discards information: after it, a change
                // to the primitive no longer reaches this token, and nothing on screen would say so.
                if (wasAlias && !EditorUtility.DisplayDialog("Detach from alias",
                        $"'{row.Definition.Id}' aliases '{current.AliasTokenId}' in '{variantId}'.\n\n"
                        + $"Replacing it with the literal {ToHex(resolved)} means a later change to that "
                        + "primitive will no longer reach this token.",
                        "Detach", "Cancel"))
                {
                    RenderContent();
                    return;
                }

                WriteExpression(row, variantId, ColorExpression.FromLiteral(resolved));
                return;
            }

            // Converting *into* an alias keeps the colour it already had by choosing the closest primitive, so
            // switching mode is not also an unintended recolour the author has to undo.
            string target = wasAlias ? current.AliasTokenId : ClosestPrimitive(variantId, resolved);
            if (target == null)
            {
                EditorUtility.DisplayDialog("Alias a token",
                    "This theme set declares no primitive tokens to alias. Add one on the Palette "
                    + "destination first.", "Close");
                RenderContent();
                return;
            }

            float alphaMultiplier = current.ExpressionKind == ColorExpression.Kind.AliasWithAlpha
                ? current.AlphaMultiplier
                : 1f;

            WriteExpression(row, variantId, mode == "Alias × alpha"
                ? ColorExpression.FromAliasWithAlpha(target, alphaMultiplier)
                : ColorExpression.FromAlias(target));
        }

        /// <summary>Writes through the session, then re-renders unless a live control is driving the edit.</summary>
        private void WriteExpression(ColorThemeTokenRow row, string variantId, ColorExpression expression,
            bool rerender = true)
        {
            if (!CanEdit) return;
            if (!_session.Write(variantId, row.Definition.Id, expression,
                    $"Set colour token '{row.Definition.Id}'"))
            {
                return;
            }

            // A mode change swaps which control belongs on the row, so that one has to re-render. A picker
            // drag must not: re-rendering would destroy the field mid-gesture.
            if (rerender) RenderContent();
        }

        private List<string> PrimitiveIds(string exclude) =>
            _model.Tokens
                .Where(t => t.Definition.Kind == ColorTokenKind.Primitive && t.Definition.Id != exclude)
                .Select(t => t.Definition.Id)
                .ToList();

        private string ClosestPrimitive(string variantId, Color color)
        {
            string best = null;
            float bestDistance = float.MaxValue;

            foreach (var candidate in _model.Tokens)
            {
                if (candidate.Definition.Kind != ColorTokenKind.Primitive) continue;
                if (!candidate.Values.TryGetValue(variantId, out Color value)) continue;

                float distance = (value.r - color.r) * (value.r - color.r)
                                 + (value.g - color.g) * (value.g - color.g)
                                 + (value.b - color.b) * (value.b - color.b)
                                 + (value.a - color.a) * (value.a - color.a);

                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = candidate.Definition.Id;
            }

            return best;
        }

        private void Fallback(ColorThemeTokenRow row, string variantId, out Color seed)
        {
            // Seeded from whatever the token already resolves to elsewhere, so filling a hole in one variant
            // starts from the theme rather than from grey.
            foreach (string other in _model.VariantIds)
            {
                if (other != variantId && row.Values.TryGetValue(other, out seed)) return;
            }
            seed = Color.grey;
        }

        /// <summary>Whether the installed theme set is one this project may write.</summary>
        /// <remarks>
        /// A set inside an immutable installed package is displayed but not editable: the edit would be
        /// discarded at the next package resolve, and a control that silently loses the author's work is
        /// worse than one that is visibly disabled.
        /// </remarks>
        private bool CanEdit =>
            _model != null && _model.IsInstalled
            && ColorThemeAssetWriteAccess.CanWrite(_model.ThemeSetPath);

        private ColorThemeTokenRow TokenById(string tokenId) =>
            _model?.Tokens.FirstOrDefault(t => t.Definition.Id == tokenId);

        private IReadOnlyList<string> DependentsOf(string tokenId) =>
            _model.AliasDependents.TryGetValue(tokenId, out var dependents)
                ? dependents
                : Array.Empty<string>();

        private IEnumerable<ColorThemeContrastRow> FailingContrastFor(string tokenId) =>
            _model.Contrast.Where(c =>
                !c.Passes
                && (c.Requirement.ForegroundTokenId == tokenId || c.Requirement.BackgroundTokenId == tokenId));

        private string DescribeDefinition(ColorTokenDefinition definition)
        {
            var parts = new List<string> { definition.Kind.ToString(), definition.Usage.ToString() };
            if (definition.Required) parts.Add("required");
            if (definition.Deprecated) parts.Add("deprecated");
            return string.Join(" · ", parts);
        }

        private string DescribeUsage(ColorThemeTokenRow row) =>
            _model.ValuesAreNewerThanScan
                ? $"{row.UsageCount} uses*"
                : $"{row.UsageCount} uses";

        private void PaintStrip(VisualElement strip, string tokenId)
        {
            strip.Clear();
            var row = TokenById(tokenId);
            if (row == null) return;

            foreach (string variantId in _model.VariantIds)
            {
                bool resolved = row.Values.TryGetValue(variantId, out Color color);
                var swatch = Swatch(resolved ? color : Color.magenta, 15, 15);
                swatch.tooltip = resolved
                    ? $"{variantId} — {ToHex(color)}"
                        + (row.Sources.TryGetValue(variantId, out string alias) ? $" via {alias}" : "")
                    : $"{variantId} — missing";
                if (!resolved) swatch.AddToClassList("molca-theme-swatch--missing");
                strip.Add(swatch);
            }
        }

        private void PaintContrastBadge(MolcaStatusBadge badge, ColorThemeTokenRow row)
        {
            var failing = FailingContrastFor(row.Definition.Id).ToList();
            if (failing.Count > 0)
            {
                badge.SetStatus(MolcaStatusKind.Error, $"{failing.Count} contrast");
                badge.tooltip = string.Join("\n", failing.Select(c =>
                    $"{c.Requirement.ForegroundTokenId} on {c.Requirement.BackgroundTokenId} "
                    + $"needs {c.Requirement.MinimumRatio:F1}:1"));
                return;
            }

            var missing = row.MissingIn(_model.VariantIds).ToList();
            if (missing.Count > 0)
            {
                badge.SetStatus(MolcaStatusKind.Warning, "incomplete");
                badge.tooltip = "Missing in " + string.Join(", ", missing);
                return;
            }

            badge.SetStatus(MolcaStatusKind.None);
        }

        #endregion

        #region Compare

        /// <summary>Column widths, shared by the header and every row so they cannot drift apart.</summary>
        private const float TokenColumnWidth = 260f;
        private const float VariantColumnWidth = 150f;
        private const float UsesColumnWidth = 56f;

        /// <summary>
        /// The token-by-variant grid, read-only.
        /// </summary>
        /// <remarks>
        /// Kept because comparing one token's value across variants is a real task a list does badly, and
        /// dropped as an editing surface because it does that badly: fixed columns leave a token's kind,
        /// description, aliases and contrast nowhere to go, and a colour picker in a 150px cell is how an
        /// alias gets flattened by accident. Editing lives in the list; this compares.
        /// </remarks>
        private VisualElement BuildCompareTable(IReadOnlyList<ColorThemeTokenRow> rows)
        {
            var table = TableScroll();

            var header = Row();
            header.style.marginTop = 6;
            header.style.marginBottom = 2;
            header.Add(Cell("Token", TokenColumnWidth, bold: true));
            foreach (string variantId in _model.VariantIds)
            {
                var cell = Cell(variantId, VariantColumnWidth, bold: true);
                if (variantId == _activeVariantId) cell.AddToClassList("molca-theme-column--active");
                header.Add(cell);
            }
            header.Add(Cell("Uses", UsesColumnWidth, bold: true));
            table.Add(header);

            foreach (var row in rows.Take(MaxRows)) table.Add(BuildCompareRow(row));

            if (rows.Count > MaxRows)
                table.Add(Note($"… and {rows.Count - MaxRows} more. Narrow the filter to reach them."));

            return table;
        }

        private VisualElement BuildCompareRow(ColorThemeTokenRow row)
        {
            var line = Row();
            line.style.alignItems = Align.Center;

            var id = Cell(row.Definition.Id, TokenColumnWidth);
            id.tooltip = Describe(row);
            line.Add(id);

            foreach (string variantId in _model.VariantIds)
            {
                var cell = new VisualElement();
                cell.style.flexDirection = FlexDirection.Row;
                cell.style.alignItems = Align.Center;
                cell.style.width = VariantColumnWidth;

                // A fixed-width column whose children may want more room has to say so on both sides:
                // flexShrink stops the column collapsing when a sibling is wide, and overflow stops a long
                // alias id painting over the next column instead of being clipped.
                cell.style.flexShrink = 0;
                cell.style.overflow = Overflow.Hidden;
                cell.style.paddingRight = 6;

                if (row.Values.TryGetValue(variantId, out Color color))
                {
                    cell.Add(Swatch(color));

                    // "palette/" is stripped for display only: every alias in this vocabulary targets the
                    // primitive tier, so the prefix is eight characters of noise on every row. The tooltip
                    // keeps the full id.
                    string text = row.Sources.TryGetValue(variantId, out string alias)
                        ? ShortAlias(alias)
                        : ToHex(color);

                    var label = new Label(text)
                    {
                        style =
                        {
                            marginLeft = 4,
                            flexGrow = 1,
                            flexShrink = 1,
                            minWidth = 0,
                            overflow = Overflow.Hidden,
                            textOverflow = TextOverflow.Ellipsis,
                            whiteSpace = WhiteSpace.NoWrap
                        }
                    };
                    label.tooltip = alias == null
                        ? ToHex(color)
                        : $"aliases {alias} — resolves to {ToHex(color)}";
                    cell.Add(label);
                }
                else
                {
                    var missing = new Label("missing");
                    missing.AddToClassList("molca-text--warn");
                    cell.Add(missing);
                }

                line.Add(cell);
            }

            line.Add(Cell(row.UsageCount.ToString(), UsesColumnWidth));
            return line;
        }

        private string Describe(ColorThemeTokenRow row)
        {
            var parts = new List<string> { DescribeDefinition(row.Definition) };

            if (!string.IsNullOrEmpty(row.Definition.Description)) parts.Add(row.Definition.Description);

            foreach (var source in row.Sources) parts.Add($"{source.Key}: aliases {source.Value}");

            var missing = row.MissingIn(_model.VariantIds).ToList();
            if (missing.Count > 0) parts.Add("missing in " + string.Join(", ", missing));

            return string.Join("\n", parts);
        }

        #endregion

        #region Palette

        /// <summary>
        /// The primitive tier, with each primitive's blast radius on the resting row.
        /// </summary>
        /// <remarks>
        /// Its own destination because it is the highest-leverage edit in the whole workspace and used to be
        /// the least reachable — primitives were hidden behind a "semantic only" toggle on the token matrix
        /// and looked like more rows. Most semantic tokens alias a primitive, so editing one primitive is how
        /// a theme is actually re-coloured; the dependent count is what makes that leverage visible before
        /// the edit rather than after it.
        /// </remarks>
        private void RenderPalette()
        {
            var primitives = _model.Tokens
                .Where(t => t.Definition.Kind == ColorTokenKind.Primitive)
                .Where(t => _paletteFilter.Length == 0 || Contains(t.Definition.Id, _paletteFilter))
                .ToList();

            var card = new MolcaSectionCard("Palette",
                $"{primitives.Count} primitive(s). Editing one moves every token that aliases it.");

            var search = new MolcaSearchField("Search primitives");
            search.OnSearchChanged += value =>
            {
                _paletteFilter = value ?? string.Empty;
                RenderContent();
            };
            card.Body.Add(search);

            if (primitives.Count == 0)
            {
                card.Body.Add(Note(_paletteFilter.Length > 0
                    ? "No primitive matches this filter."
                    : "This theme set declares no primitives. A semantic token can still carry a literal, "
                      + "but nothing can be re-coloured in one edit until a primitive tier exists."));
                _content.Add(card);
                return;
            }

            var list = new VisualElement();
            list.AddToClassList("molca-list");

            foreach (var row in primitives.Take(MaxRows)) list.Add(BuildPaletteRow(row));

            if (primitives.Count > MaxRows)
                list.Add(Note($"… and {primitives.Count - MaxRows} more. Narrow the filter to reach them."));

            card.Body.Add(list);
            _content.Add(card);
        }

        private VisualElement BuildPaletteRow(ColorThemeTokenRow row)
        {
            var dependents = DependentsOf(row.Definition.Id);
            var line = new MolcaListRow(row.Definition.Id,
                dependents.Count == 1 ? "1 token aliases this" : $"{dependents.Count} tokens alias this");

            var strip = new VisualElement();
            strip.AddToClassList("molca-theme-strip");
            PaintStrip(strip, row.Definition.Id);
            line.AddMetadata(strip);

            _liveRefreshers.Add(() => PaintStrip(strip, row.Definition.Id));

            var detail = new VisualElement();

            if (!string.IsNullOrEmpty(row.Definition.Description)) detail.Add(Note(row.Definition.Description));

            foreach (string variantId in _model.VariantIds)
                detail.Add(BuildVariantEditor(row, variantId));

            if (dependents.Count == 0)
            {
                detail.Add(Note("No token aliases this primitive, so editing it changes nothing on its own."));
            }
            else
            {
                var jump = new VisualElement();
                jump.AddToClassList("molca-list-nested");
                foreach (string dependent in dependents.Take(12))
                {
                    string target = dependent;
                    var link = MolcaButtons.Mini(target, () => RevealToken(target));
                    link.tooltip = $"Show {target} in Tokens.";
                    jump.Add(link);
                }
                if (dependents.Count > 12) jump.Add(Note($"… and {dependents.Count - 12} more."));

                detail.Add(DetailRow("Aliased by", string.Empty));
                detail.Add(jump);
            }

            line.AddDetail(detail);
            if (_expanded.Contains(row.Definition.Id)) line.SetExpanded(true);
            line.RegisterCallback<ClickEvent>(_ => RecordExpansion(row.Definition.Id, line));

            return line;
        }

        /// <summary>Navigates to one token in the Tokens list, filtered down to it and expanded.</summary>
        private void RevealToken(string tokenId)
        {
            _tokenFilter = tokenId;
            _kindFilter = null;
            _problemsOnly = false;
            _compare = false;
            _expanded.Clear();
            _expanded.Add(tokenId);
            _rail.SelectNodeById(TokensMode);

            // Select() only raises its event when the key changes, so a reveal from within Tokens has to
            // render for itself.
            RenderContent();
        }

        #endregion

        #region Health

        private void RenderHealth()
        {
            var health = new MolcaSectionCard("Active theme", _model.ThemeSetPath,
                StatusDot(_model.Health), DescribeHealth());

            health.Body.Add(DetailRow("Variants", string.Join(", ", _model.VariantIds)));
            health.Body.Add(DetailRow("Default variant", _model.DefaultVariantId ?? "(none)"));
            health.Body.Add(DetailRow("Tokens", $"{_model.Tokens.Count} declared, "
                                                + $"{_model.IncompleteTokens.Count()} incomplete"));
            health.Body.Add(DetailRow("Legacy aliases", _model.ThemeSet.LegacyAliases.Count.ToString()));
            _content.Add(health);

            foreach (string problem in _model.Problems) _content.Add(Warn(problem));

            if (_model.ValuesAreNewerThanScan)
            {
                _content.Add(Note("Values have been edited since the last project scan, so the findings and "
                                  + "usage counts below describe the state before those edits. Refresh to "
                                  + "re-scan."));
            }

            if (_model.Audit.Findings.Count > 0)
            {
                var findings = new MolcaSectionCard("Findings",
                    $"{_model.Audit.Findings.Count} in total, most severe first.",
                    _model.Audit.HasErrors ? MolcaStatusKind.Error : MolcaStatusKind.Warning);

                foreach (var finding in _model.Audit.Findings.Take(25))
                    findings.Body.Add(Note(finding.ToString()));

                if (_model.Audit.Findings.Count > 25)
                    findings.Body.Add(Note($"… and {_model.Audit.Findings.Count - 25} more."));

                _content.Add(findings);
            }

            RenderContrast();
            RenderUsage();

            var upgrade = new MolcaSectionCard("1.x to 2.x readiness",
                "The unified upgrade report finds every remaining legacy subsystem artefact.");
            upgrade.Body.Add(Row(MolcaButtons.Primary("Run upgrade report", MolcaUpgradeMenu.Report)));
            _content.Add(upgrade);
        }

        private void RenderContrast()
        {
            int failing = _model.Contrast.Count(c => !c.Passes);

            var card = new MolcaSectionCard("Contrast requirements",
                $"{_model.Contrast.Count} declared, {failing} not met",
                failing == 0 ? MolcaStatusKind.Ok : MolcaStatusKind.Warning);

            if (_model.Contrast.Count == 0)
            {
                card.Body.Add(Note("No contrast requirements are declared, so nothing here is checked. A "
                                   + "requirement names a foreground, a background and a threshold."));
                _content.Add(card);
                return;
            }

            const float pairWidth = 320f;
            const float ratioWidth = 110f;

            var table = TableScroll();
            card.Body.Add(table);

            var header = Row();
            header.Add(Cell("Pair", pairWidth, bold: true));
            foreach (string variantId in _model.VariantIds)
                header.Add(Cell(variantId, ratioWidth, bold: true));
            header.Add(Cell("Needs", 80, bold: true));
            header.Add(Cell("Severity", 90, bold: true));
            table.Add(header);

            foreach (var row in _model.Contrast)
            {
                var line = Row();
                line.Add(Cell($"{row.Requirement.ForegroundTokenId} on "
                              + $"{row.Requirement.BackgroundTokenId}", pairWidth));

                foreach (string variantId in _model.VariantIds)
                {
                    string text = row.Ratios.TryGetValue(variantId, out float ratio)
                        ? $"{ratio:F2}:1"
                        : "—";

                    var cell = Cell(text, ratioWidth);
                    if (row.FailingVariants.Contains(variantId))
                        cell.AddToClassList("molca-text--error");
                    line.Add(cell);
                }

                line.Add(Cell($"{row.Requirement.MinimumRatio:F1}:1", 80));
                line.Add(Cell(row.Requirement.Severity.ToString(), 90));

                if (!string.IsNullOrEmpty(row.Requirement.Rationale)) line.tooltip = row.Requirement.Rationale;
                table.Add(line);
            }

            card.Body.Add(Note("A dash means the pair could not be measured — usually a translucent "
                               + "background with no under-surface named. That is reported as incomplete "
                               + "rather than guessed, because a guessed ratio is worse than none."));

            _content.Add(card);
        }

        private void RenderUsage()
        {
            var sites = _model.Audit.UsageSites;

            var card = new MolcaSectionCard("Inbound references",
                $"{sites.Count} site(s) across the scanned inputs");

            foreach (var group in sites.GroupBy(s => s.Kind).OrderByDescending(g => g.Count()))
                card.Body.Add(DetailRow(group.Key.ToString(), group.Count().ToString()));

            if (_model.Audit.SkippedInputs.Count > 0)
            {
                card.Body.Add(Note("Some declared inputs were skipped, so this list is not exhaustive:"));
                foreach (var skipped in _model.Audit.SkippedInputs)
                    card.Body.Add(Note($"  {skipped.Key}: {skipped.Value}"));
            }

            card.Body.Add(Note("The scan reads asset text, so it cannot see a legacy pair carried as a "
                               + "prefab-instance override. Confirm against a migration preview before "
                               + "treating a zero as proof."));

            var busiest = _model.Tokens
                .Where(t => t.UsageCount > 0)
                .OrderByDescending(t => t.UsageCount)
                .Take(20)
                .ToList();

            if (busiest.Count > 0)
            {
                card.Body.Add(Divider());
                card.Body.Add(Heading("Most referenced"));
                foreach (var row in busiest)
                    card.Body.Add(DetailRow(row.Definition.Id, row.UsageCount.ToString()));
            }

            _content.Add(card);
        }

        #endregion

        #region Manage

        private void RenderManage()
        {
            var aliases = new MolcaSectionCard("Legacy aliases",
                _model.IsInstalled
                    ? $"{_model.ThemeSet.LegacyAliases.Count} mapped pairs"
                    : "No theme set installed.");

            if (_model.IsInstalled)
            {
                foreach (var alias in _model.ThemeSet.LegacyAliases.Take(30))
                {
                    if (alias == null) continue;
                    aliases.Body.Add(DetailRow(alias.Key.ToString(), alias.CanonicalTokenId));
                }

                if (_model.ThemeSet.LegacyAliases.Count > 30)
                    aliases.Body.Add(Note($"… and {_model.ThemeSet.LegacyAliases.Count - 30} more."));

                if (CanEdit) aliases.Body.Add(Row(MolcaButtons.Mini("Map legacy pair…", PromptAddAlias)));
            }
            _content.Add(aliases);

            var content = new MolcaSectionCard("Content migration",
                "Find and repair serialized 1.x content from one report.");
            content.Body.Add(Note("The unified report owns migration order and exposes deterministic "
                                  + "repairs through the Remediation workspace."));
            content.Body.Add(Row(MolcaButtons.Primary("Run upgrade report", MolcaUpgradeMenu.Report)));
            _content.Add(content);

            var generation = new MolcaSectionCard("Runtime UI Toolkit",
                "One generated stylesheet per variant, plus a manifest.");
            generation.Body.Add(Row(MolcaButtons.Primary("Generate stylesheets", () =>
            {
                // Generated from the asset on disk, so a pending value edit has to land first.
                _session.Flush();
                var result = ColorThemeUssGenerator.Generate(_model.ThemeSet);
                foreach (string message in result.Messages) Debug.Log($"[ColorTheme] {message}");
                Rebuild();
            })));
            _content.Add(generation);

            var interchange = new MolcaSectionCard("Interchange",
                "Export and import the theme as DTCG-shaped JSON.");
            interchange.Body.Add(Note("Import previews first: the plan resolves a candidate in memory and "
                                      + "reports contrast regressions, so a round trip cannot silently "
                                      + "degrade accessibility."));
            interchange.Body.Add(Note("Molca ▸ ColorID ▸ Export / Preview Import / Import."));
            _content.Add(interchange);

            var reports = new MolcaSectionCard("Reports");
            reports.Body.Add(Row(
                MolcaButtons.Mini("Upgrade readiness", MolcaUpgradeMenu.Report),
                MolcaButtons.Mini("Contrast", ColorThemeSetBootstrap.ReportContrast)));
            _content.Add(reports);
        }

        #endregion

        #region Structural authoring

        /// <summary>
        /// Runs a planned transaction: preview to the Console, confirm, apply, rebuild.
        /// </summary>
        /// <remarks>
        /// The preview is written to the Console rather than into a dialog because a plan over a real project
        /// can list hundreds of sites, and a truncated preview in a modal is how somebody approves a change
        /// they did not read. The dialog carries the counts and points at the Console.
        /// </remarks>
        private void RunTransaction(string title, ColorThemeTransactionPlan plan)
        {
            Debug.Log($"[ColorTheme] {title}\n{plan.ToPreview()}");

            if (!plan.IsValid)
            {
                EditorUtility.DisplayDialog(title,
                    "The plan is not applicable:\n\n" + string.Join("\n", plan.Errors), "Close");
                return;
            }

            string blocked = plan.BlockedChangeCount == 0
                ? ""
                : $"\n\n{plan.BlockedChangeCount} site(s) are reported but cannot be rewritten — they are "
                  + "package-owned. That is why a rename keeps a compatibility alias.";

            if (!EditorUtility.DisplayDialog(title,
                    $"{plan.WritableChangeCount} change(s) will be applied.{blocked}"
                    + "\n\nThe full plan is in the Console.", "Apply", "Cancel"))
            {
                return;
            }

            var result = ColorThemeTransactionExecutor.Apply(plan);
            Debug.Log($"[ColorTheme] {result}");

            if (!result.Applied)
                EditorUtility.DisplayDialog(title, result.RejectionReason, "Close");

            Rebuild();
        }

        /// <summary>Plans a transaction against a state that includes any pending value edit.</summary>
        private ColorThemeAuditSnapshot PlanningAudit()
        {
            _session.Flush();
            return _model.Audit;
        }

        private void PromptAddToken()
        {
            ColorThemeTokenPrompt.Show("Add token", "Canonical ID, e.g. surface/raised", null,
                (id, _) =>
                {
                    var plan = ColorThemeTransactionPlanner.PlanAddToken(PlanningAudit(), id, Color.grey,
                        ColorTokenKind.Semantic, ColorTokenUsage.Surface, required: true);
                    RunTransaction("Add colour token", plan);
                });
        }

        private void PromptRenameToken()
        {
            ColorThemeTokenPrompt.Show("Rename token", "Current ID", "New ID",
                (from, to) =>
                {
                    // Keeps a compatibility alias by default: shipped content and installed packages that
                    // name the old ID are reported by the plan but cannot be rewritten, so dropping the alias
                    // would break exactly the references the plan just said it could not touch.
                    var plan = ColorThemeTransactionPlanner.PlanRenameToken(PlanningAudit(), from, to);
                    RunTransaction("Rename colour token", plan);
                });
        }

        private void PromptAddAlias()
        {
            ColorThemeTokenPrompt.Show("Map a legacy pair", "Legacy pair, e.g. Default.Text",
                "Canonical token ID",
                (pair, tokenId) =>
                {
                    if (!TryParseLegacyPair(pair, out string swatch, out string colorId))
                    {
                        EditorUtility.DisplayDialog("Map a legacy pair",
                            $"'{pair}' is not a legacy pair. Use Swatch.ColorId, e.g. Default.Text.",
                            "Close");
                        return;
                    }

                    var plan = ColorThemeTransactionPlanner.PlanAddLegacyAlias(PlanningAudit(), swatch,
                        colorId, tokenId, "Added from the Themes workspace.");
                    RunTransaction("Map a legacy pair", plan);
                });
        }

        #endregion

        #region Small builders

        private static bool TryParseLegacyPair(string pair, out string swatch, out string colorId)
        {
            swatch = null;
            colorId = null;
            if (string.IsNullOrWhiteSpace(pair)) return false;

            int separator = pair.IndexOf('.');
            if (separator <= 0 || separator >= pair.Length - 1) return false;

            swatch = pair.Substring(0, separator).Trim();
            colorId = pair.Substring(separator + 1).Trim();
            return swatch.Length > 0 && colorId.Length > 0;
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A sideways-scrolling host for one fixed-width table. Rows are added to it directly.</summary>
        /// <remarks>
        /// The compare grid and the contrast table are fixed-width by necessity — columns that reflow cannot
        /// be compared — so something has to scroll sideways. Scoping that to the table rather than to the
        /// whole workspace is what keeps the rest of the page width-aware: a page that can grow sideways has
        /// no width to lay out against, so cards shrink-wrap their content instead of filling the window and
        /// a wrapping note never wraps.
        /// <para/>
        /// The height is left to the content and the outer vertical scroll, so a long table is one scroll,
        /// not a scroll inside a scroll.
        /// </remarks>
        private static ScrollView TableScroll()
        {
            var scroll = new ScrollView(ScrollViewMode.Horizontal);

            // Horizontal mode lays the content container out as a row; the rows have to stack.
            scroll.contentContainer.style.flexDirection = FlexDirection.Column;

            // A vertical wheel over the table belongs to the page, not to a table that cannot scroll
            // vertically — otherwise the gesture is swallowed and the workspace appears stuck.
            scroll.nestedInteractionKind = ScrollView.NestedInteractionKind.ForwardScrolling;
            return scroll;
        }

        /// <summary>A table line. Keeps its natural height; a short window scrolls rather than compressing it.</summary>
        /// <remarks>
        /// <c>flexShrink = 0</c> mirrors what <see cref="Cell"/> does on the horizontal axis, and for the same
        /// reason: a row has no min-height of its own, so a parent short of room would otherwise shrink it
        /// below the height of the swatch and colour field inside it, and consecutive rows print over each
        /// other.
        /// </remarks>
        private static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexShrink = 0;
            foreach (var child in children) row.Add(child);
            return row;
        }

        /// <summary>
        /// A fixed-width table cell that clips rather than overflowing into its neighbour.
        /// </summary>
        /// <remarks>
        /// <c>flexShrink = 0</c> is the load-bearing part. A row is a flex container, so by default every
        /// child is allowed to shrink below its stated width when the row runs out of room — which is how a
        /// column ends up narrower than its header and the text spills across the next one.
        /// </remarks>
        private static Label Cell(string text, float width, bool bold = false)
        {
            var label = new Label(text)
            {
                style =
                {
                    width = width,
                    flexShrink = 0,
                    overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis,
                    whiteSpace = WhiteSpace.NoWrap,
                    paddingRight = 6
                }
            };
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.tooltip = text;
            return label;
        }

        /// <summary>Drops the redundant <c>palette/</c> prefix for display.</summary>
        private static string ShortAlias(string alias) =>
            alias != null && alias.StartsWith("palette/", StringComparison.Ordinal)
                ? alias.Substring("palette/".Length)
                : alias;

        private static VisualElement Swatch(Color color, float width = 16, float height = 16)
        {
            var swatch = new VisualElement();
            swatch.AddToClassList("molca-theme-swatch");
            swatch.style.width = width;
            swatch.style.height = height;
            swatch.style.backgroundColor = new StyleColor(color);
            swatch.tooltip = ToHex(color);
            return swatch;
        }

        private static VisualElement DetailRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-list-detail-row");

            var key = new Label(label);
            key.AddToClassList("molca-list-detail-row__label");
            row.Add(key);

            var text = new Label(value);
            text.AddToClassList("molca-list-detail-row__value");
            row.Add(text);
            return row;
        }

        private static Label Heading(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-list-detail-heading");
            return label;
        }

        private static VisualElement Divider()
        {
            var divider = new VisualElement();
            divider.AddToClassList("molca-divider");
            return divider;
        }

        private static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-list-note");
            return label;
        }

        private static Label Warn(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-text--warn");
            return label;
        }

        // Qualified: this file's namespace is Molca.ColorID.Editor, so an unqualified ColorUtility binds to
        // the deprecated Molca.ColorID.ColorUtility rather than Unity's.
        private static string ToHex(Color color) =>
            "#" + UnityEngine.ColorUtility.ToHtmlStringRGBA(color);

        #endregion
    }
}
#endif
