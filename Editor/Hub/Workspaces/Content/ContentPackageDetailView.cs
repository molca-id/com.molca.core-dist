using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>One package's authoring form: identity, content, dependencies, metadata, flags.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for a <c>pkg:</c> rail node.
    /// <para>
    /// Every control commits through <see cref="ContentWorkspaceContext.ApplyPackageEdit"/>, so the edit
    /// takes the same path — same validation, same Undo grouping, same refusals — as the MCP tools and
    /// the remediation fixes. The form holds no rule of its own; where it looks like it is deciding
    /// something (which labels exist, whether an id is free) it is reading Addressables or asking the
    /// service.
    /// </para>
    /// <para>
    /// <b>The id is not a text field.</b> A package id is a key: dependencies reference it by string, and
    /// an installed package on a player's device is keyed on it. Renaming through a field bound to the
    /// setter would rewrite it per keystroke, so it is a prompt and a single named operation that also
    /// retargets whatever depended on the old name.
    /// </para>
    /// </remarks>
    internal sealed class ContentPackageDetailView : VisualElement
    {
        private readonly ContentWorkspaceContext _context;
        private readonly ContentPackageSettings.PackageConfig _config;
        private readonly Action<string> _navigate;

        /// <summary>Builds the form for one package.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="config">The package to edit.</param>
        /// <param name="navigate">Selects another rail node, used after a rename or a delete.</param>
        public ContentPackageDetailView(
            ContentWorkspaceContext context,
            ContentPackageSettings.PackageConfig config,
            Action<string> navigate)
        {
            _context = context;
            _config = config;
            _navigate = navigate;

            BuildHeader();
            BuildFindings();
            BuildRuntime();
            BuildIdentity();
            BuildContent();
            BuildDependencies();
            BuildMetadata();
            BuildFlags();
        }

        private string PackageId => _config.packageId ?? "";

        private bool Editable => !_context.IsReadOnly;

        // ── Sections ─────────────────────────────────────────────────────────

        private void BuildHeader()
        {
            var header = new MolcaWorkspaceHeader(
                string.IsNullOrEmpty(_config.displayName) ? PackageId : _config.displayName,
                PackageId);

            var status = ContentWorkspaceUi.StatusOf(_context.Report, PackageId);
            header.Actions.Add(new MolcaStatusBadge(status, ContentWorkspaceUi.StatusText(status)));

            if (Editable)
            {
                var delete = MolcaButtons.Toolbar("Delete", Delete);
                delete.tooltip = "Removes this package definition. Built bundles and Addressables labels are untouched.";
                header.AddAction(delete);
            }

            Add(header);
        }

        private void BuildFindings()
        {
            var issues = ContentWorkspaceUi.IssuesFor(_context.Report, PackageId);
            if (issues.Count == 0) return;

            var card = ContentWorkspaceUi.Card(
                "Findings",
                $"{issues.Count} finding(s) name this package",
                ContentWorkspaceUi.StatusOf(_context.Report, PackageId));

            foreach (var issue in issues) card.Body.Add(ContentWorkspaceUi.IssueLine(issue));
            Add(card);
        }

        /// <summary>
        /// What the running player has actually done with this package.
        /// </summary>
        /// <remarks>
        /// Shown above the form rather than below it: when a download is failing, that is the thing the
        /// reader opened this page for, and it should not be under four cards of authoring fields.
        /// Absent entirely outside Play mode — an empty "not installed" panel in the Editor says
        /// nothing true.
        /// </remarks>
        private void BuildRuntime()
        {
            var state = _context.Runtime?.StateOf(PackageId);
            if (state == null) return;

            var status = state.HasError ? MolcaStatusKind.Error
                : state.IsInstalled ? MolcaStatusKind.Ok
                : MolcaStatusKind.Idle;

            var card = ContentWorkspaceUi.Card("Runtime", "In the running player", status, RuntimeLabel(state));

            if (state.IsInstalled && !string.IsNullOrEmpty(state.installedVersion))
                card.Body.Add(MolcaFields.ReadOnly("Installed version", state.installedVersion));

            if (state.IsDownloading)
            {
                card.Body.Add(MolcaFields.ReadOnly("Downloading",
                    $"{state.downloadProgress:P0}  ·  {SizeFormatter.Format(state.downloadedBytes)} of " +
                    SizeFormatter.Format(state.totalBytes)));
            }

            if (state.HasError && !string.IsNullOrEmpty(state.errorMessage))
                card.Body.Add(ContentWorkspaceUi.Help(state.errorMessage, HelpBoxMessageType.Error));

            Add(card);
        }

        /// <summary>The badge text for one live package state.</summary>
        internal static string RuntimeLabel(PackageState state) => state.status switch
        {
            PackageStatus.Installed => "Installed",
            PackageStatus.UpdateAvailable => "Update available",
            PackageStatus.Downloading => "Downloading",
            PackageStatus.Failed => "Failed",
            _ => "Available",
        };

        private void BuildIdentity()
        {
            var card = ContentWorkspaceUi.Card("Identity");

            var idRow = MolcaFields.Row(
                "Package ID",
                IdControl(),
                "The key everything else references. Lowercase letters, digits, dot, dash and underscore.");
            card.Body.Add(idRow);

            card.Body.Add(MolcaFields.EditText(
                "Display Name",
                _config.displayName,
                value => Apply(_context.Editing.SetDisplayName(PackageId, value)),
                "What players see in the content manager.",
                placeholder: "Winter Hall"));

            card.Body.Add(MolcaFields.EditTextArea(
                "Description",
                _config.metadata?.description,
                value => Apply(_context.Editing.SetDescription(PackageId, value))));

            SetEditable(card.Body);
            Add(card);
        }

        /// <summary>The id read-out plus its rename action.</summary>
        private VisualElement IdControl()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var value = new Label(string.IsNullOrEmpty(PackageId) ? "(none)" : PackageId);
            value.style.flexGrow = 1;
            if (string.IsNullOrEmpty(PackageId)) value.AddToClassList("molca-muted");
            row.Add(value);

            if (Editable) row.Add(MolcaButtons.Mini("Rename…", Rename));
            return row;
        }

        private void BuildContent()
        {
            var labels = _config.addressableLabels ?? Array.Empty<string>();
            var node = ContentWorkspaceSession.LastGraph?.Packages
                .FirstOrDefault(entry => entry.PackageId == PackageId);

            var card = ContentWorkspaceUi.Card(
                "Content",
                $"{labels.Length} Addressables label(s)");

            if (Editable)
            {
                card.AddHeaderAction(MolcaButtons.Mini("Pick labels…",
                    () => ContentLabelPicker.ShowLabels(_context, PackageId)));
                card.AddHeaderAction(MolcaButtons.Mini("Pick groups…",
                    () => ContentLabelPicker.ShowGroups(_context, PackageId)));
            }

            var known = ContentLabelPicker.KnownLabels();

            if (labels.Length == 0)
            {
                card.Body.Add(ContentWorkspaceUi.Warn(
                    "No labels selected, so this package ships nothing."));
            }
            else
            {
                foreach (string label in labels)
                {
                    string captured = label;
                    bool valid = known == null || known.Contains(label);

                    var row = new MolcaListRow(
                        string.IsNullOrWhiteSpace(label) ? "(empty label)" : label);
                    row.AddMetadata(new MolcaStatusBadge(
                        valid ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                        valid ? "In catalog" : "Not in catalog"));

                    if (Editable)
                    {
                        row.AddAction(MolcaButtons.Mini("Remove",
                            () => Apply(_context.Editing.RemoveLabel(PackageId, captured))));
                    }

                    card.Body.Add(row);
                }
            }

            if (known == null)
            {
                card.Body.Add(ContentWorkspaceUi.Help(
                    "Addressables is not configured in this project, so labels cannot be checked or picked. " +
                    "Open Window > Asset Management > Addressables > Groups to set it up.",
                    HelpBoxMessageType.Warning));
            }

            if (known != null && labels.Length > 0) BuildScanPreview(card.Body, labels);

            // Build ownership is stated only when a build produced it. The surface this descends from
            // guessed sizes from filenames, and a wrong number that looks right is worse than none.
            if (node != null)
            {
                card.Body.Add(MolcaFields.ReadOnly("Bundles",
                    $"{node.DirectBundles.Count} direct · {node.DependencyBundles.Count} dependency"));
                card.Body.Add(MolcaFields.ReadOnly("Download", SizeFormatter.Format(node.DownloadSizeBytes)));
                card.Body.Add(MolcaFields.ReadOnly("Assets", node.ResolvedAssetCount.ToString()));
            }
            else if (ContentWorkspaceSession.LastGraph != null)
            {
                card.Body.Add(ContentWorkspaceUi.Warn(
                    "This package resolved to no bundles in the last build."));
            }
            else
            {
                card.Body.Add(MolcaFields.Note(
                    "Download size and bundle ownership come from an Addressables build layout. " +
                    "Run a clean build on Verify to see them."));
            }

            Add(card);
        }

        /// <summary>
        /// What these labels match in the project today, on demand.
        /// </summary>
        /// <remarks>
        /// Deliberately a button rather than something that runs on open. The scan walks every
        /// Addressables entry and the full dependency graph of everything it matches, which on a real
        /// project is seconds — acceptable when asked for, not acceptable every time a rail row is
        /// clicked.
        /// <para/>
        /// The wording carries the caveat because the number invites a comparison it cannot win:
        /// these are source bytes, and a build packs and compresses them. It answers "did my labels
        /// catch what I meant", not "how big is the download".
        /// </remarks>
        private void BuildScanPreview(VisualElement body, string[] labels)
        {
            var slot = new VisualElement();

            void Render()
            {
                slot.Clear();
                var scan = ContentScanPreview.Cached(PackageId);

                if (scan == null)
                {
                    slot.Add(MolcaFields.Note(
                        "Scan to see how many assets these labels match before building."));
                    return;
                }

                var result = scan.Value;
                slot.Add(MolcaFields.ReadOnly("Matches",
                    $"{result.AssetCount} asset(s)  ·  {SizeFormatter.Format(result.SourceBytes)} of source"));

                foreach (var (group, entries) in result.Groups)
                    slot.Add(MolcaFields.ReadOnly(group, $"{entries} entr{(entries == 1 ? "y" : "ies")}"));

                if (result.AssetCount == 0)
                {
                    slot.Add(ContentWorkspaceUi.Warn(
                        "These labels match nothing. Check the labels exist and the assets are marked Addressable."));
                }

                slot.Add(MolcaFields.Note(
                    "Source files and everything they reference. A build packs and compresses them, so " +
                    "the real download is smaller — Verify reports that one."));
            }

            body.Add(MolcaFields.Actions(MolcaButtons.Mini("Scan assets", () =>
            {
                ContentScanPreview.Scan(PackageId, labels);
                Render();
            })));

            body.Add(slot);
            Render();
        }

        private void BuildDependencies()
        {
            var dependencies = (_config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                .Where(dependency => dependency != null)
                .ToList();

            var card = ContentWorkspaceUi.Card("Dependencies", $"{dependencies.Count} package(s)");

            var candidates = _context.Settings.packageConfigs
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.packageId))
                .Select(entry => entry.packageId)
                .Where(id => !string.Equals(id, PackageId, StringComparison.Ordinal))
                .Where(id => !dependencies.Any(dependency =>
                    string.Equals(dependency.packageId, id, StringComparison.Ordinal)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (Editable)
            {
                var add = MolcaButtons.Mini("Add dependency…", () => ShowDependencyMenu(candidates));
                add.SetEnabled(candidates.Count > 0);
                add.tooltip = candidates.Count > 0
                    ? "Depend on another package in this project."
                    : "Every other package is already a dependency.";
                card.AddHeaderAction(add);
            }

            if (dependencies.Count == 0)
            {
                card.Body.Add(MolcaFields.Note("Installs on its own."));
            }
            else
            {
                foreach (var dependency in dependencies)
                {
                    string target = dependency.packageId ?? "";
                    var resolved = _context.Settings.GetPackageConfig(target);

                    var row = new MolcaListRow(
                        string.IsNullOrEmpty(target) ? "(nothing)" : target,
                        resolved?.displayName);

                    row.AddMetadata(resolved == null
                        ? new MolcaStatusBadge(MolcaStatusKind.Error, "Not defined")
                        : new MolcaStatusBadge(
                            resolved.isRequired ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                            resolved.isRequired ? "Required" : "Optional"));

                    if (Editable)
                    {
                        string captured = target;
                        row.AddAction(MolcaButtons.Mini("Remove",
                            () => Apply(_context.Editing.RemoveDependency(PackageId, captured))));
                    }

                    card.Body.Add(row);
                }
            }

            Add(card);
        }

        private void BuildMetadata()
        {
            var card = ContentWorkspaceUi.Card("Metadata");

            card.Body.Add(MolcaFields.EditText(
                "Version",
                _config.metadata?.version,
                value => Apply(_context.Editing.SetVersion(PackageId, value)),
                "Semantic version. Update detection compares this against the release.",
                placeholder: "1.0.0"));

            card.Body.Add(MolcaFields.EditText(
                "Author",
                _config.metadata?.author,
                value => Apply(_context.Editing.SetAuthor(PackageId, value))));

            card.Body.Add(MolcaFields.EditStringList(
                "Tags",
                _config.metadata?.tags ?? Array.Empty<string>(),
                values => Apply(_context.Editing.SetTags(PackageId, values)),
                "tag",
                "No tags."));

            SetEditable(card.Body);
            Add(card);
        }

        private void BuildFlags()
        {
            var card = ContentWorkspaceUi.Card("Availability");

            card.Body.Add(MolcaFields.EditToggle(
                "Visible",
                _config.isVisible,
                value => Apply(_context.Editing.SetVisible(PackageId, value)),
                "Whether players see this package in the content manager. Hiding never changes whether " +
                "it installs, resolves, or is validated."));

            card.Body.Add(MolcaFields.EditToggle(
                "Required",
                _config.isRequired,
                value => Apply(_context.Editing.SetRequired(PackageId, value)),
                "Required packages install on startup and cannot be uninstalled. Their dependencies must " +
                "be required too, or publishing is blocked."));

            SetEditable(card.Body);
            Add(card);
        }

        // ── Actions ──────────────────────────────────────────────────────────

        private void ShowDependencyMenu(List<string> candidates)
        {
            var menu = new GenericMenu();
            foreach (string candidate in candidates)
            {
                string captured = candidate;
                var config = _context.Settings.GetPackageConfig(candidate);
                string label = string.IsNullOrEmpty(config?.displayName)
                    ? candidate
                    : $"{config.displayName}  ({candidate})";

                menu.AddItem(new GUIContent(label), false,
                    () => Apply(_context.Editing.AddDependency(PackageId, captured)));
            }

            if (candidates.Count == 0)
                menu.AddDisabledItem(new GUIContent("No other packages available"));

            menu.ShowAsContext();
        }

        /// <summary>
        /// Prompts for a new id and applies the rename, following the package to its new row.
        /// </summary>
        /// <remarks>
        /// The selection has to move because the rail keys rows on the id: after the rename the old node
        /// does not exist, and leaving the selection on it would drop the reader back to the package list
        /// immediately after a successful edit.
        /// </remarks>
        private void Rename()
        {
            string next = MolcaValuePrompt.ForValue(
                "Rename package",
                "The id every dependency, install record and cached download refers to. Packages that " +
                "depend on this one are retargeted; content already installed under the old id is " +
                "orphaned on devices that have it.",
                "Package ID",
                PackageId,
                "Rename",
                ValidateId);

            if (string.IsNullOrEmpty(next) || next == PackageId) return;

            var result = _context.Editing.RenamePackage(PackageId, next);
            if (!result.Changed)
            {
                Debug.LogWarning($"[ContentPackage] {result.Message}");
                EditorUtility.DisplayDialog("Cannot rename", result.Message, "OK");
                return;
            }

            Debug.Log($"[ContentPackage] {result.Message}");
            AssetDatabase.SaveAssets();
            ContentWorkspaceSession.InvalidateBuild();
            _navigate?.Invoke(ContentWorkspaceNodes.ForPackage(next));
        }

        /// <summary>
        /// Why a candidate id cannot be used, or null.
        /// </summary>
        /// <remarks>
        /// Two rules, from two owners: the shape comes from
        /// <see cref="Molca.ContentPackage.Editor.ContentValidation.IsValidPackageId"/>, so the prompt
        /// and the finding cannot disagree, and uniqueness is checked here because it is a fact about
        /// this project rather than about the id.
        /// </remarks>
        private string ValidateId(string candidate)
        {
            if (!Molca.ContentPackage.Editor.ContentValidation.IsValidPackageId(candidate, out string error))
                return error;

            string trimmed = candidate.Trim();
            if (string.Equals(trimmed, PackageId, StringComparison.Ordinal)) return null;

            return _context.Settings.packageConfigs.Any(entry =>
                entry != null && string.Equals(entry.packageId, trimmed, StringComparison.Ordinal))
                ? $"A package with id '{trimmed}' already exists."
                : null;
        }

        private void Delete()
        {
            string name = string.IsNullOrEmpty(_config.displayName) ? PackageId : _config.displayName;
            if (!EditorUtility.DisplayDialog("Delete package",
                    $"Remove '{name}' from this project's content definitions?\n\n" +
                    "Built bundles and Addressables labels are not touched.",
                    "Delete", "Cancel"))
                return;

            var result = _context.Editing.RemovePackage(PackageId);
            if (!result.Changed)
            {
                Debug.LogWarning($"[ContentPackage] {result.Message}");
                return;
            }

            // Logged rather than swallowed: the message names any package left depending on this one,
            // which is now a blocking finding on a package the author did not open.
            Debug.Log($"[ContentPackage] {result.Message}");
            AssetDatabase.SaveAssets();
            ContentWorkspaceSession.InvalidateBuild();
            _navigate?.Invoke(ContentWorkspaceNodes.Packages);
        }

        private void Apply(Molca.ContentPackage.Editor.ContentEditResult result) =>
            _context.ApplyPackageEdit(result);

        /// <summary>Disables a card body wholesale when the asset cannot be written.</summary>
        /// <remarks>
        /// The read-only reason is already stated once at the top of the workspace. Leaving the controls
        /// live under it would let an author type into fields whose every commit is refused into the
        /// console — which reads as the editor being broken rather than as the asset being off-limits.
        /// </remarks>
        private void SetEditable(VisualElement body)
        {
            if (Editable) return;
            body.SetEnabled(false);
        }
    }
}
