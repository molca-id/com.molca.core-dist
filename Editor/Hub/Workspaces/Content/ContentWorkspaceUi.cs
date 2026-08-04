using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;
using Molca.ContentPackage.Editor;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>Rail node ids for the Content workspace.</summary>
    /// <remarks>
    /// Constants rather than literals because three things key on them: the rail's selection, the
    /// persisted <see cref="ContentWorkspaceSession.SelectedNode"/>, and the deep links other surfaces
    /// use to send a reader here. A typo in any one of them fails by quietly showing the packages list.
    /// </remarks>
    internal static class ContentWorkspaceNodes
    {
        /// <summary>The cross-package list.</summary>
        public const string Packages = "packages";

        /// <summary>Prefix of a single package's detail node; the suffix is its id.</summary>
        public const string PackagePrefix = "pkg:";

        /// <summary>The add-package command leaf.</summary>
        public const string AddPackage = "add-package";

        /// <summary>Release identity and compatibility.</summary>
        public const string Compatibility = "compatibility";

        /// <summary>Release protocol and trusted signing keys.</summary>
        public const string Protocol = "protocol";

        /// <summary>Remote catalog, cache budget, and download behaviour.</summary>
        public const string Delivery = "delivery";

        /// <summary>Build and validate.</summary>
        public const string Verify = "verify";

        /// <summary>Sign and promote.</summary>
        public const string Publish = "publish";

        /// <summary>The node id for one package.</summary>
        /// <param name="packageId">The package.</param>
        /// <returns>Its node id.</returns>
        public static string ForPackage(string packageId) => PackagePrefix + packageId;

        /// <summary>The package a node id names, or null when it names something else.</summary>
        /// <param name="nodeId">The node id.</param>
        /// <returns>The package id, or null.</returns>
        public static string PackageOf(string nodeId) =>
            nodeId != null && nodeId.StartsWith(PackagePrefix, System.StringComparison.Ordinal)
                ? nodeId.Substring(PackagePrefix.Length)
                : null;
    }

    /// <summary>The Content workspace's shared read-only vocabulary.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>. Editable
    /// controls come from <see cref="MolcaFields"/>; these are the pieces that only display.
    /// </remarks>
    internal static class ContentWorkspaceUi
    {
        /// <summary>A page section.</summary>
        /// <param name="title">Section title.</param>
        /// <param name="subtitle">Optional secondary line.</param>
        /// <param name="status">Optional status shown in the header band.</param>
        /// <param name="statusText">Text beside the status dot.</param>
        /// <returns>The card; add content to its <see cref="MolcaSectionCard.Body"/>.</returns>
        public static MolcaSectionCard Card(
            string title,
            string subtitle = null,
            MolcaStatusKind status = MolcaStatusKind.None,
            string statusText = null) =>
            new MolcaSectionCard(title, subtitle, status, statusText);

        /// <summary>One validation finding, coloured by severity and prefixed by a non-colour marker.</summary>
        /// <param name="issue">The finding.</param>
        /// <returns>The line.</returns>
        public static Label IssueLine(ContentIssue issue)
        {
            string marker = issue.Severity == ContentIssueSeverity.Error ? "✕"
                : issue.Severity == ContentIssueSeverity.Warning ? "!" : "·";

            var label = new Label($"{marker} [{issue.Code}] {issue.Message}")
            {
                style = { whiteSpace = WhiteSpace.Normal },
            };
            label.AddToClassList(issue.Severity == ContentIssueSeverity.Error ? "molca-text--error"
                : issue.Severity == ContentIssueSeverity.Warning ? "molca-text--warn"
                : "molca-muted");
            return label;
        }

        /// <summary>A warning line.</summary>
        /// <param name="text">The warning.</param>
        /// <returns>The line.</returns>
        public static Label Warn(string text)
        {
            var label = new Label(text) { style = { whiteSpace = WhiteSpace.Normal } };
            label.AddToClassList("molca-text--warn");
            return label;
        }

        /// <summary>A framed message.</summary>
        /// <param name="text">The message.</param>
        /// <param name="type">Severity.</param>
        /// <returns>The help box.</returns>
        public static VisualElement Help(string text, HelpBoxMessageType type = HelpBoxMessageType.Info)
        {
            var box = new HelpBox(text, type);
            box.style.marginBottom = 6;
            return box;
        }

        /// <summary>The worst severity among a package's findings.</summary>
        /// <param name="report">The validation report.</param>
        /// <param name="packageId">The package.</param>
        /// <returns>Error, Warning, or Ok.</returns>
        public static MolcaStatusKind StatusOf(ContentValidationReport report, string packageId)
        {
            var issues = IssuesFor(report, packageId);
            return issues.Any(issue => issue.Severity == ContentIssueSeverity.Error) ? MolcaStatusKind.Error
                : issues.Any(issue => issue.Severity == ContentIssueSeverity.Warning) ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;
        }

        /// <summary>The findings naming one package.</summary>
        /// <param name="report">The validation report.</param>
        /// <param name="packageId">The package.</param>
        /// <returns>Its findings, most severe first.</returns>
        public static List<ContentIssue> IssuesFor(ContentValidationReport report, string packageId) =>
            report?.Issues.Where(issue => issue.PackageId == packageId).ToList()
            ?? new List<ContentIssue>();

        /// <summary>What a status badge says for a package.</summary>
        /// <param name="status">The package's worst severity.</param>
        /// <returns>The badge text.</returns>
        public static string StatusText(MolcaStatusKind status) => status switch
        {
            MolcaStatusKind.Error => "Invalid",
            MolcaStatusKind.Warning => "Review",
            _ => "Valid",
        };
    }
}
