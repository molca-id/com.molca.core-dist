#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ColorID;
using Molca.Editor.Upgrade;
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
    /// the transaction planner, the content migration, the USS generator, the interchange. The plan states
    /// that as a requirement; the practical consequence is that this file can be read as "what is shown",
    /// with no risk that a rule lives only here and disagrees with the CLI.
    /// <para/>
    /// <b>Rebuild, do not patch.</b> Any action that changes state rebuilds the whole model rather than
    /// updating the affected panel. Colour state is cross-cutting — one token edit moves the matrix, the
    /// contrast table, the usage counts and the health summary at once — and partial refresh is how two
    /// panels of the same window come to disagree.
    /// </remarks>
    public sealed class ColorThemeWorkspaceView : VisualElement
    {
        private const string OverviewMode = "overview";
        private const string TokensMode = "tokens";
        private const string AccessibilityMode = "accessibility";
        private const string UsageMode = "usage";
        private const string PreviewMode = "preview";
        private const string MigrationMode = "migration";
        private const string IntegrationsMode = "integrations";

        private readonly MolcaRail _rail = new MolcaRail();
        private readonly VisualElement _content = new VisualElement();
        private readonly MolcaWorkspaceHeader _header = new MolcaWorkspaceHeader("Themes");

        private ColorThemeWorkspaceModel _model;
        private string _tokenFilter = string.Empty;
        private bool _semanticOnly = true;
        private string _previewVariantId;

        /// <summary>Creates the workspace.</summary>
        public ColorThemeWorkspaceView()
        {
            AddToClassList("molca-workspace");
            _header.AddAction(MolcaButtons.Toolbar("Refresh", Rebuild));
            Add(_header);

            var split = new VisualElement();
            split.AddToClassList("molca-workspace-split");
            Add(split);

            _rail.AddItem(OverviewMode, "Overview");
            _rail.AddItem(TokensMode, "Tokens");
            _rail.AddItem(AccessibilityMode, "Accessibility");
            _rail.AddItem(UsageMode, "Usage");
            _rail.AddItem(PreviewMode, "Preview");
            _rail.AddItem(MigrationMode, "Migration");
            _rail.AddItem(IntegrationsMode, "Integrations");
            _rail.OnSelected += _ => RenderContent();
            split.Add(_rail);

            // Both axes: the matrix is a fixed-width table, so a narrow Hub window must be able to scroll
            // sideways to it rather than squeezing the columns into each other.
            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal) { style = { flexGrow = 1 } };
            _content.AddToClassList("molca-workspace-split__content");
            scroll.Add(_content);
            split.Add(scroll);

            Rebuild();
            _rail.Select(OverviewMode);
        }

        /// <summary>Re-reads every service and redraws.</summary>
        private void Rebuild()
        {
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

            if (_model != null && _previewVariantId == null)
                _previewVariantId = _model.DefaultVariantId ?? _model.VariantIds.FirstOrDefault();

            RenderContent();
        }

        private string DescribeHealth()
        {
            if (_model == null) return "Could not read project state — see the Console.";

            switch (_model.Health)
            {
                case MolcaStatusForTheme.NotInstalled: return "No Color Theme Set installed.";
                case MolcaStatusForTheme.Incomplete:
                    return $"Coverage incomplete — {_model.Audit.SkippedInputs.Count} input(s) skipped.";
                case MolcaStatusForTheme.Errors:
                    return $"{_model.Audit.Findings.Count} finding(s), including build-blocking errors.";
                case MolcaStatusForTheme.Warnings:
                    return $"{_model.Audit.Findings.Count} finding(s), none blocking.";
                default:
                    return $"Clean — {_model.Tokens.Count} tokens across {_model.VariantIds.Count} variants.";
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

            if (_model == null)
            {
                _content.Add(Note("The workspace could not read project state. See the Console."));
                return;
            }

            if (!_model.IsInstalled && _rail.SelectedKey != MigrationMode)
            {
                RenderNotInstalled();
                return;
            }

            switch (_rail.SelectedKey)
            {
                case TokensMode: RenderTokens(); break;
                case AccessibilityMode: RenderAccessibility(); break;
                case UsageMode: RenderUsage(); break;
                case PreviewMode: RenderPreview(); break;
                case MigrationMode: RenderMigration(); break;
                case IntegrationsMode: RenderIntegrations(); break;
                default: RenderOverview(); break;
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

        #region Overview

        private void RenderOverview()
        {
            var health = new MolcaSectionCard("Active theme", _model.ThemeSetPath,
                StatusDot(_model.Health), DescribeHealth());

            health.Body.Add(KeyValue("Variants", string.Join(", ", _model.VariantIds)));
            health.Body.Add(KeyValue("Default variant", _model.DefaultVariantId ?? "(none)"));
            health.Body.Add(KeyValue("Tokens", $"{_model.Tokens.Count} declared, "
                                               + $"{_model.IncompleteTokens.Count()} incomplete"));
            health.Body.Add(KeyValue("Legacy aliases", _model.ThemeSet.LegacyAliases.Count.ToString()));
            _content.Add(health);

            foreach (string problem in _model.Problems) _content.Add(Note(problem));

            var upgrade = new MolcaSectionCard("1.x to 2.x readiness",
                "The unified upgrade report finds every remaining legacy subsystem artefact.");
            upgrade.Body.Add(Row(MolcaButtons.Primary("Run upgrade report", MolcaUpgradeMenu.Report)));
            _content.Add(upgrade);

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
        }

        #endregion

        #region Tokens

        private void RenderTokens()
        {
            var card = new MolcaSectionCard("Variant matrix",
                "Token rows, variant columns. Primitives are hidden by default.");

            var search = new MolcaSearchField("Search id, description or alias");
            search.OnSearchChanged += value =>
            {
                _tokenFilter = value ?? string.Empty;
                RenderContent();
            };
            card.Body.Add(search);

            var toggle = new Toggle("Semantic tokens only") { value = _semanticOnly };
            toggle.RegisterValueChangedCallback(evt =>
            {
                _semanticOnly = evt.newValue;
                RenderContent();
            });
            card.Body.Add(toggle);

            if (CanEdit)
            {
                // Identity changes only. Values are edited in the matrix directly, because they cannot
                // repoint a reference; a rename can, which is why it is a previewed transaction.
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

            card.Body.Add(MatrixHeader());
            foreach (var row in rows) card.Body.Add(MatrixRow(row));

            if (rows.Count == 0) card.Body.Add(Note("No token matches this filter."));

            _content.Add(card);
        }

        private IEnumerable<ColorThemeTokenRow> FilterTokens()
        {
            foreach (var row in _model.Tokens)
            {
                if (_semanticOnly && row.Definition.Kind != ColorTokenKind.Semantic) continue;

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

        /// <summary>Whether the installed theme set is one this project may write.</summary>
        /// <remarks>
        /// A set inside an immutable installed package is displayed but not editable: the edit would be
        /// discarded at the next package resolve, and a control that silently loses the author's work is
        /// worse than one that is visibly disabled.
        /// </remarks>
        private bool CanEdit =>
            _model != null && _model.IsInstalled
            && ColorThemeAssetWriteAccess.CanWrite(_model.ThemeSetPath);

        /// <summary>Writes a literal value, then rebuilds.</summary>
        /// <remarks>
        /// Rebuilds rather than patching the one cell, because a value change moves the contrast table and
        /// the health summary too — this is exactly the cross-cutting case the single-model design exists
        /// for. It costs a full audit per edit; acceptable because a colour edit is a deliberate act, and
        /// the alternative is a window whose panels disagree.
        /// </remarks>
        private void SetLiteral(ColorThemeTokenRow row, string variantId, Color color)
        {
            if (!CanEdit) return;

            Undo.RecordObject(_model.ThemeSet, "Set colour token value");
            if (!ColorThemeSetEditing.SetTokenValue(_model.ThemeSet, variantId, row.Definition.Id,
                    ColorExpression.FromLiteral(color)))
            {
                Debug.LogWarning($"[ColorTheme] Variant '{variantId}' does not exist, so "
                                 + $"'{row.Definition.Id}' was not set.");
                return;
            }

            _model.ThemeSet.InvalidateIndexes();
            EditorUtility.SetDirty(_model.ThemeSet);
            AssetDatabase.SaveAssets();
            Rebuild();
        }

        private void DetachToLiteral(ColorThemeTokenRow row, string variantId, Color resolved)
        {
            if (!EditorUtility.DisplayDialog("Detach from alias",
                    $"'{row.Definition.Id}' aliases '{row.Sources[variantId]}' in '{variantId}'.\n\n"
                    + $"Replacing it with the literal {ToHex(resolved)} means a later change to that "
                    + "primitive will no longer reach this token.",
                    "Detach", "Cancel"))
            {
                return;
            }

            SetLiteral(row, variantId, resolved);
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Column widths, shared by the header and every row so they cannot drift apart.</summary>
        private const float TokenColumnWidth = 260f;
        private const float VariantColumnWidth = 200f;
        private const float UsesColumnWidth = 56f;

        private VisualElement MatrixHeader()
        {
            var header = Row();
            header.style.marginTop = 6;
            header.style.marginBottom = 2;

            header.Add(Cell("Token", TokenColumnWidth, bold: true));
            foreach (string variantId in _model.VariantIds)
                header.Add(Cell(variantId, VariantColumnWidth, bold: true));
            header.Add(Cell("Uses", UsesColumnWidth, bold: true));
            return header;
        }

        private VisualElement MatrixRow(ColorThemeTokenRow row)
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
                    if (row.Sources.TryGetValue(variantId, out string alias))
                    {
                        // An alias is shown, not offered as a colour field. A picker here would write a
                        // literal on the first drag and silently sever the link to the primitive — turning
                        // "change the palette in one edit" into a search-and-replace, invisibly. Detaching
                        // is available, but only as something the author asks for by name.
                        cell.Add(Swatch(color));

                        // "palette/" is stripped for display only: every alias in this vocabulary targets
                        // the primitive tier, so the prefix is eight characters of noise on every row and
                        // it is what pushed the column into its neighbour. The tooltip keeps the full id.
                        var label = new Label(ShortAlias(alias))
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
                        label.tooltip = $"aliases {alias} — resolves to {ToHex(color)}";
                        cell.Add(label);

                        if (CanEdit)
                        {
                            var detach = MolcaButtons.Mini("↧", () => DetachToLiteral(row, variantId, color));
                            detach.tooltip = $"Replace the alias to '{alias}' with the literal "
                                             + $"{ToHex(color)} in '{variantId}'.";
                            detach.style.flexShrink = 0;
                            detach.style.width = 22;
                            detach.style.marginLeft = 2;
                            detach.style.marginRight = 0;
                            cell.Add(detach);
                        }
                    }
                    else
                    {
                        var field = new ColorField { value = color, showAlpha = true };
                        field.style.flexGrow = 1;
                        field.style.flexShrink = 1;
                        field.style.minWidth = 0;
                        field.style.marginLeft = 0;
                        field.SetEnabled(CanEdit);
                        field.RegisterValueChangedCallback(evt =>
                            SetLiteral(row, variantId, evt.newValue));
                        cell.Add(field);
                    }
                }
                else
                {
                    // Named rather than left blank: a blank cell reads as "no opinion", and a required
                    // token missing from a variant is the single most consequential state in this table.
                    var missing = new Label("missing");
                    missing.style.color = new StyleColor(new Color(0.9f, 0.5f, 0.2f));
                    cell.Add(missing);

                    if (CanEdit)
                    {
                        var add = MolcaButtons.Mini("Set", () => SetLiteral(row, variantId, Color.grey));
                        add.tooltip = $"Give '{variantId}' a value for this token.";
                        cell.Add(add);
                    }
                }

                line.Add(cell);
            }

            line.Add(Cell(row.UsageCount.ToString(), UsesColumnWidth));
            return line;
        }

        private string Describe(ColorThemeTokenRow row)
        {
            var parts = new List<string>
            {
                $"{row.Definition.Kind} · {row.Definition.Usage}"
                + (row.Definition.Required ? " · required" : "")
            };

            if (!string.IsNullOrEmpty(row.Definition.Description)) parts.Add(row.Definition.Description);

            foreach (var source in row.Sources) parts.Add($"{source.Key}: aliases {source.Value}");

            var missing = row.MissingIn(_model.VariantIds).ToList();
            if (missing.Count > 0) parts.Add("missing in " + string.Join(", ", missing));

            return string.Join("\n", parts);
        }

        #endregion

        #region Structural authoring

        /// <summary>
        /// Runs a planned transaction: preview to the Console, confirm, apply, rebuild.
        /// </summary>
        /// <remarks>
        /// The preview is written to the Console rather than into a dialog because a plan over a real
        /// project can list hundreds of sites, and a truncated preview in a modal is how somebody approves
        /// a change they did not read. The dialog carries the counts and points at the Console.
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

        private void PromptAddToken()
        {
            ColorThemeTokenPrompt.Show("Add token", "Canonical ID, e.g. surface/raised", null,
                (id, _) =>
                {
                    var plan = ColorThemeTransactionPlanner.PlanAddToken(_model.Audit, id, Color.grey,
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
                    // name the old ID are reported by the plan but cannot be rewritten, so dropping the
                    // alias would break exactly the references the plan just said it could not touch.
                    var plan = ColorThemeTransactionPlanner.PlanRenameToken(_model.Audit, from, to);
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

                    var plan = ColorThemeTransactionPlanner.PlanAddLegacyAlias(_model.Audit, swatch,
                        colorId, tokenId, "Added from the Themes workspace.");
                    RunTransaction("Map a legacy pair", plan);
                });
        }

        #endregion

        #region Accessibility

        private void RenderAccessibility()
        {
            int failing = _model.Contrast.Count(c => !c.Passes);

            var card = new MolcaSectionCard("Contrast requirements",
                $"{_model.Contrast.Count} declared, {failing} not met",
                failing == 0 ? MolcaStatusKind.Ok : MolcaStatusKind.Warning);

            if (_model.Contrast.Count == 0)
            {
                card.Body.Add(Note("No contrast requirements are declared, so nothing here is checked. A "
                                   + "requirement names a foreground, a background and a threshold."));
            }

            const float pairWidth = 320f;
            const float ratioWidth = 110f;

            var header = Row();
            header.Add(Cell("Pair", pairWidth, bold: true));
            foreach (string variantId in _model.VariantIds)
                header.Add(Cell(variantId, ratioWidth, bold: true));
            header.Add(Cell("Needs", 80, bold: true));
            header.Add(Cell("Severity", 90, bold: true));
            card.Body.Add(header);

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
                        cell.style.color = new StyleColor(new Color(0.9f, 0.4f, 0.3f));
                    line.Add(cell);
                }

                line.Add(Cell($"{row.Requirement.MinimumRatio:F1}:1", 80));
                line.Add(Cell(row.Requirement.Severity.ToString(), 90));

                if (!string.IsNullOrEmpty(row.Requirement.Rationale)) line.tooltip = row.Requirement.Rationale;
                card.Body.Add(line);
            }

            card.Body.Add(Note("A dash means the pair could not be measured — usually a translucent "
                               + "background with no under-surface named. That is reported as incomplete "
                               + "rather than guessed, because a guessed ratio is worse than none."));

            _content.Add(card);
        }

        #endregion

        #region Usage

        private void RenderUsage()
        {
            var sites = _model.Audit.UsageSites;

            var card = new MolcaSectionCard("Inbound references",
                $"{sites.Count} site(s) across the scanned inputs");

            foreach (var group in sites.GroupBy(s => s.Kind).OrderByDescending(g => g.Count()))
            {
                card.Body.Add(KeyValue(group.Key.ToString(), group.Count().ToString()));
            }

            if (_model.Audit.SkippedInputs.Count > 0)
            {
                card.Body.Add(Note("Some declared inputs were skipped, so this list is not exhaustive:"));
                foreach (var skipped in _model.Audit.SkippedInputs)
                    card.Body.Add(Note($"  {skipped.Key}: {skipped.Value}"));
            }

            card.Body.Add(Note("The scan reads asset text, so it cannot see a legacy pair carried as a "
                               + "prefab-instance override. Confirm against a migration preview before "
                               + "treating a zero as proof."));
            _content.Add(card);

            var busiest = new MolcaSectionCard("Most referenced tokens");
            foreach (var row in _model.Tokens.OrderByDescending(t => t.UsageCount).Take(20))
            {
                if (row.UsageCount == 0) break;
                busiest.Body.Add(KeyValue(row.Definition.Id, row.UsageCount.ToString()));
            }
            _content.Add(busiest);
        }

        #endregion

        #region Preview

        private void RenderPreview()
        {
            var card = new MolcaSectionCard("Preview",
                "Every semantic token as it resolves in the selected variant.");

            var picker = new PopupField<string>("Variant", _model.VariantIds.ToList(),
                Mathf.Max(0, _model.VariantIds.ToList().IndexOf(_previewVariantId)));
            picker.RegisterValueChangedCallback(evt =>
            {
                _previewVariantId = evt.newValue;
                RenderContent();
            });
            card.Body.Add(picker);

            foreach (var group in _model.Tokens
                         .Where(t => t.Definition.Kind == ColorTokenKind.Semantic)
                         .GroupBy(t => Family(t.Definition.Id)))
            {
                var family = new Label(group.Key)
                {
                    style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 }
                };
                card.Body.Add(family);

                foreach (var row in group)
                {
                    var line = Row();
                    line.style.alignItems = Align.Center;

                    if (_previewVariantId != null && row.Values.TryGetValue(_previewVariantId, out Color color))
                    {
                        line.Add(Swatch(color, 44, 20));
                        line.Add(Cell(row.Definition.Id, TokenColumnWidth));
                        line.Add(Cell(ToHex(color), 110));
                    }
                    else
                    {
                        line.Add(Swatch(Color.magenta, 44, 20));
                        line.Add(Cell(row.Definition.Id, TokenColumnWidth));
                        line.Add(Cell("unresolved", 110));
                    }

                    card.Body.Add(line);
                }
            }

            _content.Add(card);
        }

        private static string Family(string tokenId)
        {
            int slash = tokenId.IndexOf('/');
            return slash < 0 ? tokenId : tokenId.Substring(0, slash);
        }

        #endregion

        #region Migration

        private void RenderMigration()
        {
            var aliases = new MolcaSectionCard("Legacy aliases",
                _model.IsInstalled
                    ? $"{_model.ThemeSet.LegacyAliases.Count} mapped pairs"
                    : "No theme set installed.");

            foreach (var alias in _model.ThemeSet.LegacyAliases.Take(30))
            {
                if (alias == null) continue;
                aliases.Body.Add(KeyValue(alias.Key.ToString(), alias.CanonicalTokenId));
            }
            _content.Add(aliases);

            var content = new MolcaSectionCard("Content migration",
                "Find and repair serialized 1.x content from one report.");

            content.Body.Add(Note("The unified report owns migration order and exposes deterministic "
                                  + "repairs through the Remediation workspace."));
            content.Body.Add(Row(MolcaButtons.Primary("Run upgrade report", MolcaUpgradeMenu.Report)));
            _content.Add(content);
        }

        #endregion

        #region Integrations

        private void RenderIntegrations()
        {
            var generation = new MolcaSectionCard("Runtime UI Toolkit",
                "One generated stylesheet per variant, plus a manifest.");
            generation.Body.Add(Row(MolcaButtons.Primary("Generate stylesheets", () =>
            {
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

        private static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
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
            var swatch = new VisualElement
            {
                style =
                {
                    width = width,
                    height = height,
                    flexShrink = 0,
                    marginRight = 2,
                    backgroundColor = new StyleColor(color),
                    borderTopWidth = 1, borderBottomWidth = 1,
                    borderLeftWidth = 1, borderRightWidth = 1
                }
            };

            // A border in a colour neither light nor dark, so a swatch stays visible against either editor
            // skin and at any alpha. Without it a fully transparent token is an invisible gap.
            var edge = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            swatch.style.borderTopColor = edge;
            swatch.style.borderBottomColor = edge;
            swatch.style.borderLeftColor = edge;
            swatch.style.borderRightColor = edge;
            swatch.tooltip = ToHex(color);
            return swatch;
        }

        private static VisualElement KeyValue(string key, string value)
        {
            var row = Row();
            row.Add(Cell(key, 220));
            row.Add(new Label(value));
            return row;
        }

        private static Label Note(string text)
        {
            var label = new Label(text)
            {
                style = { whiteSpace = WhiteSpace.Normal, marginTop = 4, marginBottom = 2, opacity = 0.85f }
            };
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
