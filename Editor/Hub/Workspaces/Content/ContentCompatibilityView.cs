using UnityEditor;
using UnityEngine.UIElements;
using Molca.ContentPackage.Release;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>Who this release is for: project, platform, channel, version, and app range.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>compatibility</c> node.
    /// <para>
    /// <b>Two kinds of value live here and the page says which is which.</b> Identity is read from the
    /// project binding and the active build target — nothing on this page can change them. The
    /// next-release fields are per-user drafts held in <see cref="ContentWorkspaceSession"/>, not the
    /// settings asset, so two developers on the same commit can be mid-way through different releases.
    /// The channel is the deliberate exception: it lives on <c>MolcaProjectSettings</c> precisely so
    /// everyone building this commit ships against the same content, which is why it is shown here and
    /// changed there.
    /// </para>
    /// </remarks>
    internal sealed class ContentCompatibilityView : VisualElement
    {
        /// <summary>Builds the page.</summary>
        /// <param name="context">The workspace context.</param>
        public ContentCompatibilityView(ContentWorkspaceContext context)
        {
            Add(new MolcaWorkspaceHeader("Compatibility", "Who the next release is for"));

            BuildIdentity();
            BuildNextRelease();
        }

        private void BuildIdentity()
        {
            var project = MolcaProjectSettings.Instance;
            string platform = ReleasePlatform.Normalize(
                ContentPlatform.Of(EditorUserBuildSettings.activeBuildTarget));

            var card = ContentWorkspaceUi.Card("Identity", "Read from the project binding and build target");

            card.Body.Add(MolcaFields.ReadOnly("Project", string.IsNullOrEmpty(project?.ProjectName)
                ? "(not connected)"
                : $"{project.ProjectName}  ·  {project.ProjectCode}"));

            card.Body.Add(MolcaFields.ReadOnly("Platform", string.IsNullOrEmpty(platform)
                ? $"{EditorUserBuildSettings.activeBuildTarget} is not a publishable content platform"
                : platform));

            card.Body.Add(MolcaFields.ReadOnly("Channel", project?.ContentChannel ?? "stable"));
            card.Body.Add(MolcaFields.Note(
                "The channel is set on MolcaProjectSettings so every developer building this commit ships " +
                "against the same content. Requesting a non-stable channel needs the " +
                "project.build.channel.select capability, and the server enforces that when a build token " +
                "is minted."));

            Add(card);

            if (project == null || string.IsNullOrEmpty(project.ProjectId))
            {
                Add(ContentWorkspaceUi.Help(
                    "This repository is not connected to a Molca project. Connect it in Settings before publishing.",
                    HelpBoxMessageType.Warning));
            }
        }

        private void BuildNextRelease()
        {
            var card = ContentWorkspaceUi.Card("Next release", "Your draft, not the project's");

            var warning = new VisualElement();

            card.Body.Add(MolcaFields.EditText(
                "Content Version",
                ContentWorkspaceSession.ContentVersion,
                value =>
                {
                    ContentWorkspaceSession.ContentVersion = value;
                    RenderVersionWarning(warning, value);
                },
                "Semantic version for the release as a whole.",
                placeholder: "1.0.0"));

            card.Body.Add(warning);
            RenderVersionWarning(warning, ContentWorkspaceSession.ContentVersion);

            card.Body.Add(MolcaFields.EditText(
                "Min App Version",
                ContentWorkspaceSession.MinAppVersion,
                value => ContentWorkspaceSession.MinAppVersion = value,
                "Lowest app version this release may activate on. Empty means no lower bound."));

            card.Body.Add(MolcaFields.EditText(
                "Max App Version",
                ContentWorkspaceSession.MaxAppVersion,
                value => ContentWorkspaceSession.MaxAppVersion = value,
                "Highest app version this release may activate on. Empty means no upper bound."));

            card.Body.Add(MolcaFields.Note(
                "Leave Max empty unless this content is known to break on a newer app version. " +
                "An empty maximum means it keeps working on every future release."));

            card.Body.Add(MolcaFields.EditTextArea(
                "Changelog",
                ContentWorkspaceSession.Changelog,
                value => ContentWorkspaceSession.Changelog = value));

            Add(card);
        }

        /// <summary>
        /// Shows or clears the non-semantic version warning in place.
        /// </summary>
        /// <remarks>
        /// Rendered into its own slot rather than by rebuilding the page, because the field raising it is
        /// the field the author is typing in — rebuilding would destroy the control mid-edit.
        /// </remarks>
        private static void RenderVersionWarning(VisualElement host, string version)
        {
            host.Clear();
            if (ReleaseCompatibility.TryParse(version, out _)) return;

            host.Add(ContentWorkspaceUi.Warn(
                $"'{version}' is not SemVer. The server rejects a non-SemVer content version."));
        }
    }
}
