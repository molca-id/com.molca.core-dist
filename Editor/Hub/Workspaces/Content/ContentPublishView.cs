using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Molca.ContentPackage.Editor;
using Molca.ContentPackage.Release;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>Sign a verified build and, optionally, make it the release players resolve.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>publish</c> node.
    /// <para>
    /// <b>Blockers are listed, not disabled-with-a-tooltip.</b> Every reason publishing cannot proceed is
    /// a sentence naming what to do about it, because the alternative — a greyed button — is the state
    /// this page will be in most of the time, and a greyed button explains nothing.
    /// </para>
    /// <para>
    /// The cancellation source is static because the operation outlives the view. A Hub tab switch
    /// evicts and rebuilds workspace content, and an upload holding an instance field would be
    /// unreachable the moment that happened — leaving a running publish with no way to stop it.
    /// </para>
    /// </remarks>
    internal sealed class ContentPublishView : VisualElement
    {
        private static CancellationTokenSource _publishCancellation;

        private readonly ContentWorkspaceContext _context;
        private readonly Action _refreshHeader;
        private readonly Action _rebuild;

        /// <summary>Builds the page.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="refreshHeader">Updates the workspace header while a publish runs.</param>
        /// <param name="rebuild">Re-renders the workspace when the publish settles.</param>
        public ContentPublishView(ContentWorkspaceContext context, Action refreshHeader, Action rebuild)
        {
            _context = context;
            _refreshHeader = refreshHeader;
            _rebuild = rebuild;

            Add(new MolcaWorkspaceHeader("Publish", "Sign and promote verified content"));
            Build();
        }

        /// <summary>Cancels a publish still running when the workspace goes away.</summary>
        public static void CancelRunning()
        {
            try { _publishCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        private void Build()
        {
            var blockers = PublishBlockers();

            if (blockers.Count > 0)
            {
                var blocked = ContentWorkspaceUi.Card(
                    "Not ready", $"{blockers.Count} blocker(s)", MolcaStatusKind.Warning, "Blocked");

                foreach (string blocker in blockers) blocked.Body.Add(ContentWorkspaceUi.Warn(blocker));
                blocked.Body.Add(MolcaFields.Note("Nothing is uploaded until these are clear."));
                Add(blocked);
                return;
            }

            var card = ContentWorkspaceUi.Card("Ready to publish", null, MolcaStatusKind.Ok, "Verified");

            card.Body.Add(MolcaFields.ReadOnly("Version", ContentWorkspaceSession.ContentVersion));
            card.Body.Add(MolcaFields.ReadOnly("Objects",
                ContentWorkspaceSession.LastGraph.Bundles.Count.ToString()));
            card.Body.Add(MolcaFields.Note(
                "Uploads go straight to storage using short-lived presigned URLs. No storage credential " +
                "ever reaches this project, and no Molca credential is attached to a storage request."));

            var actions = MolcaFields.Actions(
                MolcaButtons.Primary("Publish Draft", () => RunPublish(promote: false)),
                MolcaButtons.Mini("Publish and Promote", () => RunPublish(promote: true)));

            if (ContentWorkspaceSession.Busy)
            {
                actions.SetEnabled(false);
                card.Body.Add(actions);
                card.Body.Add(MolcaFields.Actions(MolcaButtons.Mini("Cancel", CancelRunning)));
            }
            else
            {
                card.Body.Add(actions);
            }

            card.Body.Add(MolcaFields.Note(
                "Publish Draft uploads and verifies without making the release active — the safe default. " +
                "Promote makes it the release players resolve."));

            Add(card);

            if (!string.IsNullOrEmpty(ContentWorkspaceSession.LastPublishSummary))
            {
                var last = ContentWorkspaceUi.Card("Last attempt");
                last.Body.Add(MolcaFields.Note(ContentWorkspaceSession.LastPublishSummary));
                Add(last);
            }
        }

        private List<string> PublishBlockers()
        {
            var blockers = new List<string>();
            var project = MolcaProjectSettings.Instance;

            if (project == null || string.IsNullOrEmpty(project.ProjectId))
                blockers.Add("This repository is not connected to a Molca project. Connect it in Settings.");

            if (string.IsNullOrWhiteSpace(DevEntitlementStoreToken()))
                blockers.Add("No developer entitlement. Sign in from the Molca licence window.");

            if (ContentWorkspaceSession.LastGraph == null)
                blockers.Add("No clean build in this session. Run Build Clean and Verify first.");

            if (!ReleaseCompatibility.TryParse(ContentWorkspaceSession.ContentVersion, out _))
            {
                blockers.Add(
                    $"Content version '{ContentWorkspaceSession.ContentVersion}' is not SemVer. " +
                    "Set it on Compatibility.");
            }

            var report = ContentWorkspaceSession.LastReport;
            if (report != null && !report.CanPublish)
                blockers.Add($"Verification found {report.ErrorCount} blocking error(s). See Verify.");

            return blockers;
        }

        private async void RunPublish(bool promote)
        {
            if (promote && !EditorUtility.DisplayDialog("Promote release",
                    $"Make content version {ContentWorkspaceSession.ContentVersion} the active release for this " +
                    "project, channel, and platform?\n\nPlayers resolve it on their next launch.",
                    "Publish and Promote", "Cancel"))
                return;

            ContentReleaseCandidate candidate;
            try
            {
                candidate = ContentReleaseCandidate.FromBuild(
                    ContentWorkspaceSession.LastGraph,
                    _context.Settings.packageConfigs,
                    ContentWorkspaceSession.StagingDirectory,
                    channel: MolcaProjectSettings.Instance?.ContentChannel ?? "stable",
                    platform: ReleasePlatform.Normalize(
                        ContentPlatform.Of(EditorUserBuildSettings.activeBuildTarget)),
                    contentVersion: ContentWorkspaceSession.ContentVersion,
                    minAppVersion: ContentWorkspaceSession.MinAppVersion,
                    maxAppVersion: ContentWorkspaceSession.MaxAppVersion,
                    changelog: ContentWorkspaceSession.Changelog);
            }
            catch (Exception exception)
            {
                // Candidate derivation refuses a staging directory that disagrees with the layout, which
                // means one of them is stale. Publishing either would produce a release that cannot be
                // downloaded.
                EditorUtility.DisplayDialog("Cannot build a release candidate", exception.Message, "OK");
                return;
            }

            _publishCancellation = new CancellationTokenSource();
            ContentWorkspaceSession.Busy = true;
            ContentWorkspaceSession.LastPublishSummary = "";
            _rebuild?.Invoke();

            try
            {
                var client = new ContentAuthoringClient(
                    DevLicenseConfigBaseUrl(),
                    MolcaProjectSettings.Instance.ProjectId,
                    DevEntitlementStoreToken);

                var progress = new Progress<ContentAuthoringClient.PublishProgress>(update =>
                {
                    ContentWorkspaceSession.BusyStatus = update.Total > 0
                        ? $"{update.Stage} ({update.Completed}/{update.Total})"
                        : update.Stage;
                    ContentWorkspaceSession.BusyProgress = update.Fraction;
                    _refreshHeader?.Invoke();
                });

                var result = await client.PublishAsync(
                    candidate, promote, progress, _publishCancellation.Token);

                ContentWorkspaceSession.LastPublishSummary = result.Success
                    ? $"✓ Release {result.ReleaseId} {(result.Promoted ? "published and promoted" : "published as a draft")}."
                    : result.Cancelled
                        ? "Publish cancelled. Nothing was promoted."
                        : $"✕ Publish failed ({result.Reason}): {result.Message}";

                if (result.Success && !string.IsNullOrEmpty(result.ReleaseId))
                    Debug.Log($"[ContentPackage] {ContentWorkspaceSession.LastPublishSummary}");
                else if (!result.Cancelled)
                    Debug.LogError($"[ContentPackage] {ContentWorkspaceSession.LastPublishSummary}");
            }
            catch (Exception exception)
            {
                ContentWorkspaceSession.LastPublishSummary = $"✕ Publish failed: {exception.Message}";
                Debug.LogError($"[ContentPackage] Publish failed: {exception}");
            }
            finally
            {
                _publishCancellation?.Dispose();
                _publishCancellation = null;
                ContentWorkspaceSession.Busy = false;
                ContentWorkspaceSession.BusyStatus = "";
                ContentWorkspaceSession.BusyProgress = -1f;
                _rebuild?.Invoke();
            }
        }

        // Named rather than inlined so the two credential reads are obvious in a diff instead of buried
        // in the publish path. LoadEffective also picks up the CI environment variable, so a headless
        // publish works without an interactive sign-in.
        private static string DevEntitlementStoreToken() =>
            Molca.Editor.Licensing.DevEntitlementStore.LoadEffective();

        private static string DevLicenseConfigBaseUrl() =>
            Molca.Editor.Licensing.DevLicenseConfig.ServerBaseUrl;
    }
}
