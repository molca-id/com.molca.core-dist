using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Molca.ContentPackage.Editor;
using Molca.ContentPackage.Release;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>Build clean, resolve every package against the layout, and validate the result.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>verify</c> node.
    /// <para>
    /// The only page that produces a build graph, which is why several other pages say "run a clean
    /// build on Verify" instead of estimating. A finding that needs the graph — a package that resolved
    /// to nothing, a bundle belonging to no package — cannot be reported without one, and reporting it
    /// anyway from label counts is exactly how the predecessor surface came to state download sizes
    /// smaller than what a player fetched.
    /// </para>
    /// </remarks>
    internal sealed class ContentVerifyView : VisualElement
    {
        private readonly ContentWorkspaceContext _context;
        private readonly Action _refreshHeader;
        private readonly Action _rebuild;

        /// <summary>Builds the page.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="refreshHeader">Updates the workspace header while a build runs.</param>
        /// <param name="rebuild">Re-renders the workspace once it finishes.</param>
        public ContentVerifyView(ContentWorkspaceContext context, Action refreshHeader, Action rebuild)
        {
            _context = context;
            _refreshHeader = refreshHeader;
            _rebuild = rebuild;

            Add(new MolcaWorkspaceHeader("Verify", "Build, inspect, and validate"));

            BuildConfiguration();
            BuildBuild();
        }

        private void BuildConfiguration()
        {
            var report = _context.Report;
            var status = report.ErrorCount > 0 ? MolcaStatusKind.Error
                : report.WarningCount > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            var card = ContentWorkspaceUi.Card(
                "Configuration",
                $"{report.ErrorCount} error(s), {report.WarningCount} warning(s)",
                status,
                status == MolcaStatusKind.Ok ? "Valid" : "Needs attention");

            if (report.Issues.Count == 0)
                card.Body.Add(MolcaFields.Note("No configuration findings."));

            foreach (var issue in report.Issues)
                card.Body.Add(ContentWorkspaceUi.IssueLine(issue));

            Add(card);
        }

        private void BuildBuild()
        {
            var graph = ContentWorkspaceSession.LastGraph;
            var card = ContentWorkspaceUi.Card(
                "Build",
                graph == null ? "No clean build in this session" : "Staged",
                graph == null ? MolcaStatusKind.Idle : MolcaStatusKind.Ok,
                graph == null ? "Not built" : "Built");

            if (graph == null)
            {
                card.Body.Add(MolcaFields.Note("No clean build in this session. Content findings need one."));
            }
            else
            {
                card.Body.Add(MolcaFields.ReadOnly("Staged",
                    ContentWorkspaceSession.StagedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                card.Body.Add(MolcaFields.ReadOnly("Directory", ContentWorkspaceSession.StagingDirectory));

                var orphans = graph.OrphanBundles.ToList();
                if (orphans.Count > 0)
                {
                    card.Body.Add(ContentWorkspaceUi.Warn(
                        $"{orphans.Count} bundle(s) belong to no package and would ship unreferenced."));
                }

                var buildReport = ContentWorkspaceSession.LastReport;
                if (buildReport != null)
                {
                    card.Body.Add(MolcaFields.Note(
                        $"{buildReport.ErrorCount} error(s), {buildReport.WarningCount} warning(s) against the build."));

                    foreach (var issue in buildReport.Issues)
                        card.Body.Add(ContentWorkspaceUi.IssueLine(issue));
                }
            }

            var run = MolcaButtons.Primary("Build Clean and Verify", RunCleanBuild);
            run.SetEnabled(!ContentWorkspaceSession.Busy);
            run.style.marginTop = 6;
            card.Body.Add(run);

            card.Body.Add(MolcaFields.Note(
                "Builds Addressables into a fresh staging directory with the build layout enabled, then " +
                "resolves every package's bundles from that layout."));

            Add(card);
        }

        /// <summary>
        /// Runs a clean Addressables build and resolves every package against its layout.
        /// </summary>
        /// <remarks>
        /// Synchronous, and the busy flag is set before the first frame it could block: the build runs on
        /// the main thread, so nothing repaints while it does. The flag exists for what happens
        /// afterwards — a re-entrant press queued behind the build, and the Publish page's own guard.
        /// </remarks>
        private void RunCleanBuild()
        {
            string platform = ReleasePlatform.Normalize(
                ContentPlatform.Of(EditorUserBuildSettings.activeBuildTarget));

            if (string.IsNullOrEmpty(platform))
            {
                EditorUtility.DisplayDialog("Build",
                    $"{EditorUserBuildSettings.activeBuildTarget} is not a publishable content platform.", "OK");
                return;
            }

            ContentWorkspaceSession.Busy = true;
            ContentWorkspaceSession.BusyStatus = "Building content";
            ContentWorkspaceSession.BusyProgress = -1f;
            _refreshHeader?.Invoke();

            try
            {
                var staging = ContentReleaseStaging.BuildClean(platform);
                if (!staging.Success)
                {
                    ContentWorkspaceSession.InvalidateBuild();
                    EditorUtility.DisplayDialog("Build failed", staging.Error, "OK");
                    return;
                }

                var labels = _context.Settings.packageConfigs
                    .Where(config => config != null && !string.IsNullOrEmpty(config.packageId))
                    .ToDictionary(
                        config => config.packageId,
                        config => config.addressableLabels ?? Array.Empty<string>(),
                        StringComparer.Ordinal);

                var graph = ContentBuildGraph.Resolve(labels, staging.LayoutPath);

                ContentWorkspaceSession.LastGraph = graph;
                ContentWorkspaceSession.StagingDirectory = staging.Directory;
                ContentWorkspaceSession.StagedAtUtc = DateTime.UtcNow;
                ContentWorkspaceSession.LastReport = ContentValidation.Validate(
                    _context.Settings.packageConfigs, graph,
                    ContentWorkspaceSession.ContentVersion,
                    ContentWorkspaceSession.MinAppVersion,
                    ContentWorkspaceSession.MaxAppVersion);
            }
            catch (Exception exception)
            {
                ContentWorkspaceSession.InvalidateBuild();
                Debug.LogError($"[ContentPackage] Clean build failed: {exception}");
                EditorUtility.DisplayDialog("Build failed", exception.Message, "OK");
            }
            finally
            {
                ContentWorkspaceSession.Busy = false;
                ContentWorkspaceSession.BusyStatus = "";
                _rebuild?.Invoke();
            }
        }
    }
}
