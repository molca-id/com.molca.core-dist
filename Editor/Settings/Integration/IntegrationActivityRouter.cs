using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Single fan-out point that pushes build and release activity to every opted-in
    /// <see cref="IntegrationProvider"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/</c>.
    /// Registration: implements Unity's <see cref="IPostprocessBuildWithReport"/> (Unity scans <i>types</i>,
    /// not asset instances) for builds, and an <see cref="InitializeOnLoadMethodAttribute"/> static hook
    /// subscribes once to <see cref="ReleaseTool.ReleaseCreated"/> for releases.
    /// <para>
    /// Replaces the per-provider reporters from Sprint 28: the payload (version, duration, size, composed
    /// changelog notes, triggered-by) is assembled <b>once</b> here and handed to each provider's
    /// <see cref="IntegrationProvider.PushBuildActivityAsync"/> / <see cref="IntegrationProvider.PushReleaseActivityAsync"/>.
    /// Pushes are fire-and-forget editor HTTP; per-provider failures are logged, never thrown into the build
    /// or release flow. A shared editor-session throttle prevents rapid successive builds from spamming.
    /// </para>
    /// </remarks>
    public sealed class IntegrationActivityRouter : IPostprocessBuildWithReport
    {
        /// <inheritdoc/>
        public int callbackOrder => 0;

        // Editor-session throttle shared across builds (mirrors the Sprint 28 per-reporter guard).
        private static DateTime _lastBuildPushUtc = DateTime.MinValue;
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

        [InitializeOnLoadMethod]
        private static void SubscribeToReleases()
        {
            // Idempotent across domain reloads: unsubscribe before subscribing so we never double-handle.
            ReleaseTool.ReleaseCreated -= OnReleaseCreated;
            ReleaseTool.ReleaseCreated += OnReleaseCreated;
        }

        /// <inheritdoc/>
        public void OnPostprocessBuild(BuildReport report)
        {
            var targets = ProvidersFor(p => p.ShouldPushOnBuild);
            if (targets.Count == 0) return;

            if (DateTime.UtcNow - _lastBuildPushUtc < MinInterval)
            {
                Debug.Log("[Integration] Skipping build push (throttled).");
                return;
            }
            _lastBuildPushUtc = DateTime.UtcNow;

            var activity = BuildActivityFrom(report);
            foreach (var provider in targets)
                _ = PushBuildAsync(provider, activity);
        }

        private static void OnReleaseCreated(ReleaseTool.ReleaseEventArgs args)
        {
            var targets = ProvidersFor(p => p.ShouldPushOnRelease);
            if (targets.Count == 0) return;

            var activity = ReleaseActivityFrom(args);
            foreach (var provider in targets)
                _ = PushReleaseAsync(provider, activity);
        }

        // ---- Fan-out (each push isolated so one provider can't break the build/release) ----

        private static async Awaitable PushBuildAsync(IntegrationProvider provider, BuildActivity activity)
        {
            try
            {
                await provider.PushBuildActivityAsync(activity);
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Integration] {provider.DisplayName} build push error: {e.Message}");
            }
        }

        private static async Awaitable PushReleaseAsync(IntegrationProvider provider, ReleaseActivity activity)
        {
            try
            {
                await provider.PushReleaseActivityAsync(activity);
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Integration] {provider.DisplayName} release push error: {e.Message}");
            }
        }

        // ---- Payload assembly (done once for all providers) ----

        private static BuildActivity BuildActivityFrom(BuildReport report)
        {
            var summary = report.summary;
            bool succeeded = summary.result == BuildResult.Succeeded;
            return new BuildActivity(
                ProjectName, Version, summary.platform.ToString(), succeeded, summary.result.ToString(),
                summary.totalTime, summary.totalSize, (int)summary.totalErrors, TriggeredBy);
        }

        private static ReleaseActivity ReleaseActivityFrom(ReleaseTool.ReleaseEventArgs args)
        {
            var notes = ReadChangelogNotes(args) ?? args.Notes;
            return new ReleaseActivity(ProjectName, args.Version, TriggeredBy, notes);
        }

        // Prefer the composed changelog entry (raw notes + git commits) over the raw notes alone.
        private static string ReadChangelogNotes(ReleaseTool.ReleaseEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.ChangelogPath))
                return null;

            try
            {
                var entries = new ChangelogWriter(args.ChangelogPath, includeGitCommits: false).Read();
                var entry = entries.LastOrDefault(e => e.version == args.Version && e.changeType == "release");
                return string.IsNullOrWhiteSpace(entry?.notes) ? null : entry.notes;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Integration] Could not read changelog for release notes: {e.Message}");
                return null;
            }
        }

        private static List<IntegrationProvider> ProvidersFor(Func<IntegrationProvider, bool> predicate)
        {
            var settings = IntegrationSettings.FindSettings();
            if (settings == null) return new List<IntegrationProvider>();
            return settings.Providers.Where(predicate).ToList();
        }

        private static string ProjectName
        {
            get
            {
                var projectSettings = MolcaProjectSettings.Instance;
                return projectSettings != null ? projectSettings.ProjectName : "Molca Project";
            }
        }

        private static string Version
        {
            get
            {
                var editorSettings = MolcaEditorSettings.Instance;
                return editorSettings?.VersionSettings != null
                    ? editorSettings.VersionSettings.GetFullVersionString()
                    : "Unknown";
            }
        }

        private static string TriggeredBy
        {
            get
            {
                var editorUserName = CloudProjectSettings.userName;
                return string.IsNullOrWhiteSpace(editorUserName) ? Environment.UserName : editorUserName;
            }
        }
    }
}
