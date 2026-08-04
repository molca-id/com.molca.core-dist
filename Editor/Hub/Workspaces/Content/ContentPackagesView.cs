using System;
using System.Linq;
using UnityEngine.UIElements;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>Every package in one list: identity, findings, and what the last build gave it.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>packages</c> rail node.
    /// <para>
    /// The cross-package view, kept beside the per-package forms rather than replaced by them: "which of
    /// these needs attention, and which is carrying the download size" is a question about the set, and
    /// answering it by opening eleven rail rows is not answering it. Rows open the form rather than
    /// editing inline — a row that edits is a form pretending to be a list.
    /// </para>
    /// </remarks>
    internal sealed class ContentPackagesView : VisualElement
    {
        /// <summary>Builds the list.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="navigate">Opens a rail node, used by the row's Edit action.</param>
        public ContentPackagesView(ContentWorkspaceContext context, Action<string> navigate)
        {
            var report = context.Report;
            var graph = ContentWorkspaceSession.LastGraph;

            var header = new MolcaWorkspaceHeader("Packages",
                $"{context.Settings.packageConfigs.Count} defined");
            Add(header);

            if (graph == null)
            {
                Add(ContentWorkspaceUi.Help(
                    "Download sizes and bundle ownership come from an Addressables build layout. " +
                    "Run a clean build on Verify to see them."));
            }

            var status = report.ErrorCount > 0 ? MolcaStatusKind.Error
                : report.WarningCount > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            var list = new VisualElement();
            list.AddToClassList("molca-list");

            var group = new MolcaListGroup(
                "Project packages",
                $"{context.Settings.packageConfigs.Count} package(s)",
                status,
                report.ErrorCount > 0 ? $"{report.ErrorCount} errors"
                    : report.WarningCount > 0 ? $"{report.WarningCount} warnings"
                    : "Valid");

            foreach (var config in context.Settings.packageConfigs.Where(entry => entry != null))
            {
                string id = config.packageId ?? "";
                string displayName = string.IsNullOrEmpty(config.displayName) ? id : config.displayName;
                if (string.IsNullOrEmpty(displayName)) displayName = "(unnamed package)";

                var row = new MolcaListRow(displayName, id);

                var rowStatus = ContentWorkspaceUi.StatusOf(report, id);
                row.AddMetadata(new MolcaStatusBadge(rowStatus, ContentWorkspaceUi.StatusText(rowStatus)));

                // In Play mode the question changes from "is this authored correctly" to "did it
                // install", so the live badge sits beside the validation one rather than replacing it.
                var live = context.Runtime?.StateOf(id);
                if (live != null)
                {
                    row.AddMetadata(new MolcaStatusBadge(
                        live.HasError ? MolcaStatusKind.Error
                            : live.IsInstalled ? MolcaStatusKind.Ok
                            : MolcaStatusKind.Idle,
                        ContentPackageDetailView.RuntimeLabel(live)));
                }

                var tags = new System.Collections.Generic.List<string>();
                if (config.isRequired) tags.Add("required");
                if (!config.isVisible) tags.Add("hidden");
                if (tags.Count > 0)
                {
                    var tagLabel = new Label(string.Join(" · ", tags));
                    tagLabel.AddToClassList("molca-list-row__meta");
                    row.AddMetadata(tagLabel);
                }

                var node = graph?.Packages.FirstOrDefault(entry => entry.PackageId == id);
                if (node != null)
                {
                    var value = new Label(
                        $"{node.DirectBundles.Count} direct · {node.DependencyBundles.Count} dependency · " +
                        $"{SizeFormatter.Format(node.DownloadSizeBytes)} · {node.ResolvedAssetCount} asset(s)");
                    value.AddToClassList("molca-list-row__value");
                    row.AddMetadata(value);
                }
                else if (graph != null)
                {
                    row.AddMetadata(new MolcaStatusBadge(MolcaStatusKind.Warning, "No bundles"));
                }

                string captured = id;
                row.AddAction(MolcaButtons.Mini("Edit",
                    () => navigate?.Invoke(ContentWorkspaceNodes.ForPackage(captured))));

                row.AddDetail(MolcaFields.ReadOnly("Addressable labels",
                    string.Join(", ", config.addressableLabels ?? Array.Empty<string>())));

                if (config.dependencies is { Length: > 0 })
                {
                    row.AddDetail(MolcaFields.ReadOnly("Dependencies",
                        string.Join(", ", config.dependencies
                            .Where(dependency => dependency != null)
                            .Select(dependency => dependency.packageId))));
                }

                row.AddDetail(MolcaFields.ReadOnly("Version", config.metadata?.version));

                if (node != null)
                {
                    row.AddDetail(MolcaFields.ReadOnly("Build ownership",
                        $"{node.DirectBundles.Count} direct bundles · {node.DependencyBundles.Count} dependencies"));
                }
                else if (graph != null)
                {
                    row.AddDetail(ContentWorkspaceUi.Warn(
                        "This package resolved to no bundles in the last build."));
                }

                foreach (var issue in ContentWorkspaceUi.IssuesFor(report, id))
                    row.AddDetail(ContentWorkspaceUi.IssueLine(issue));

                group.Body.Add(row);
            }

            if (context.Settings.packageConfigs.Count == 0)
            {
                group.Body.Add(MolcaFields.Note(
                    "No packages defined. Add one from the rail to start."));
            }

            list.Add(group);
            Add(list);

            // Release-wide findings name no package, so they appear nowhere in the rows above.
            var releaseWide = report.Issues.Where(issue => string.IsNullOrEmpty(issue.PackageId)).ToList();
            if (releaseWide.Count > 0)
            {
                var card = ContentWorkspaceUi.Card("Release-wide findings", $"{releaseWide.Count} finding(s)");
                foreach (var issue in releaseWide) card.Body.Add(ContentWorkspaceUi.IssueLine(issue));
                Add(card);
            }
        }
    }
}
