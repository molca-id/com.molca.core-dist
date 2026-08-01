using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;
using Molca.ContentPackage.Release;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// The Content workspace: Packages, Release, Verify, Publish (plan §10.1).
    /// </summary>
    /// <remarks>
    /// Every number this view shows comes from a Phase 4 service — <see cref="ContentValidation"/>,
    /// <see cref="ContentBuildGraph"/>, <see cref="ContentReleaseCandidate"/>. It computes nothing
    /// itself, and where a service cannot answer, it shows nothing and says why. That rule is the
    /// point: the surface this replaces inferred bundle ownership from filename prefixes and reported
    /// download sizes smaller than what a player actually fetched, and nobody re-checks a plausible
    /// number.
    ///
    /// It also never writes the settings asset directly. Structural edits go through
    /// <see cref="ContentPackageEditingService"/>, the same path the inspector, the MCP tools, and
    /// remediation use.
    /// </remarks>
    internal sealed class ContentWorkspaceView : VisualElement
    {
        private readonly VisualElement _tabStrip = new VisualElement();
        private readonly VisualElement _body = new VisualElement();
        private readonly Label _status = new Label();

        private ContentPackageSettings _settings;
        private ContentPackageEditingService _editing;
        private CancellationTokenSource _publishCancellation;
        private string _activeTab = "packages";

        private static readonly string[] Tabs = { "packages", "release", "verify", "publish" };

        /// <summary>Builds the workspace view and selects the last tab.</summary>
        public ContentWorkspaceView()
        {
            style.flexGrow = 1;
            style.paddingLeft = 10;
            style.paddingRight = 10;
            style.paddingTop = 8;

            _tabStrip.style.flexDirection = FlexDirection.Row;
            _tabStrip.style.marginBottom = 8;
            Add(_tabStrip);

            _status.style.marginBottom = 6;
            _status.style.whiteSpace = WhiteSpace.Normal;
            Add(_status);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.Add(_body);
            Add(scroll);

            ResolveSettings();
            BuildTabStrip();
            Rebuild();

            RegisterCallback<DetachFromPanelEvent>(_ => CancelPublish());
        }

        // ── Shell ────────────────────────────────────────────────────────────

        private void ResolveSettings()
        {
            var found = AssetDatabase.FindAssets("t:ContentPackageSettings")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ContentPackageSettings>)
                .Where(asset => asset != null)
                .ToList();

            _settings = found.Count == 1 ? found[0] : found.FirstOrDefault();
            _editing = _settings != null ? new ContentPackageEditingService(_settings) : null;
        }

        private void BuildTabStrip()
        {
            _tabStrip.Clear();
            foreach (string tab in Tabs)
            {
                string id = tab;
                var button = new Button(() => { _activeTab = id; BuildTabStrip(); Rebuild(); })
                {
                    text = char.ToUpperInvariant(id[0]) + id.Substring(1),
                };
                button.style.marginRight = 2;
                if (id == _activeTab)
                {
                    button.style.unityFontStyleAndWeight = FontStyle.Bold;
                    button.style.borderBottomWidth = 2;
                    button.style.borderBottomColor = MolcaEditorColors.StatusOk;
                }
                _tabStrip.Add(button);
            }
        }

        private void Rebuild()
        {
            _body.Clear();
            UpdateStatus();

            if (_settings == null)
            {
                _body.Add(Help(
                    "No ContentPackageSettings asset was found.\n\n" +
                    "Create one under Assets/ — not inside a package. Package assets are replaced on " +
                    "upgrade, and this asset holds the trusted release signing keys.",
                    MessageType.Warning));
                return;
            }

            string readOnly = _editing.ReadOnlyReason();
            if (readOnly != null) _body.Add(Help(readOnly, MessageType.Warning));

            switch (_activeTab)
            {
                case "release": BuildRelease(); break;
                case "verify": BuildVerify(); break;
                case "publish": BuildPublish(); break;
                default: BuildPackages(); break;
            }
        }

        private void UpdateStatus()
        {
            if (ContentWorkspaceSession.Busy)
            {
                _status.text = ContentWorkspaceSession.BusyProgress >= 0f
                    ? $"⏳ {ContentWorkspaceSession.BusyStatus}  ({ContentWorkspaceSession.BusyProgress:P0})"
                    : $"⏳ {ContentWorkspaceSession.BusyStatus}";
                _status.style.color = MolcaEditorColors.StatusWarn;
                return;
            }

            if (!string.IsNullOrEmpty(ContentWorkspaceSession.LastPublishSummary))
            {
                _status.text = ContentWorkspaceSession.LastPublishSummary;
                _status.style.color = MolcaEditorColors.Muted;
                return;
            }

            _status.text = "";
        }

        // ── Packages ─────────────────────────────────────────────────────────

        private void BuildPackages()
        {
            var report = ContentValidation.ValidateSettings(_settings.packageConfigs);
            var graph = ContentWorkspaceSession.LastGraph;

            _body.Add(Header($"{_settings.packageConfigs.Count} package(s)"));

            if (graph == null)
            {
                // Said rather than implied. The surface this replaces guessed sizes from filenames,
                // and a wrong number that looks right is worse than an absent one.
                _body.Add(Help(
                    "Download sizes and bundle ownership come from an Addressables build layout. " +
                    "Run a clean build on the Verify tab to see them.",
                    MessageType.None));
            }

            foreach (var config in _settings.packageConfigs.Where(entry => entry != null))
            {
                var card = Card();
                var row = Row();

                var issues = report.Issues.Where(issue => issue.PackageId == config.packageId).ToList();
                row.Add(Dot(issues.Any(i => i.Severity == ContentIssueSeverity.Error) ? MolcaEditorColors.StatusError
                    : issues.Any(i => i.Severity == ContentIssueSeverity.Warning) ? MolcaEditorColors.StatusWarn
                    : MolcaEditorColors.StatusOk));

                var title = new Label(string.IsNullOrEmpty(config.displayName)
                    ? config.packageId
                    : $"{config.displayName}  ·  {config.packageId}");
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.flexGrow = 1;
                row.Add(title);

                var tags = new List<string>();
                if (config.isRequired) tags.Add("required");
                if (!config.isVisible) tags.Add("hidden");
                if (tags.Count > 0) row.Add(Muted(string.Join(" · ", tags)));
                card.Add(row);

                var node = graph?.Packages.FirstOrDefault(entry => entry.PackageId == config.packageId);
                if (node != null)
                {
                    card.Add(Muted(
                        $"{node.DirectBundles.Count} direct · {node.DependencyBundles.Count} dependency bundle(s) · " +
                        $"{SizeFormatter.Format(node.DownloadSizeBytes)} · {node.ResolvedAssetCount} asset(s)"));
                }
                else if (graph != null)
                {
                    // The graph exists and this package is not in it: a real finding, not a blank.
                    card.Add(Warn("This package resolved to no bundles in the last build."));
                }

                card.Add(Muted($"Labels: {string.Join(", ", config.addressableLabels ?? Array.Empty<string>())}"));
                if (config.dependencies is { Length: > 0 })
                    card.Add(Muted($"Depends on: {string.Join(", ", config.dependencies.Select(d => d?.packageId))}"));

                foreach (var issue in issues)
                    card.Add(IssueLine(issue));

                _body.Add(card);
            }

            if (_editing.ReadOnlyReason() == null)
            {
                var add = new Button(() =>
                {
                    var result = _editing.AddPackage();
                    if (!result.Changed) Debug.LogWarning($"[ContentPackage] {result.Message}");
                    AssetDatabase.SaveAssets();
                    ContentWorkspaceSession.InvalidateBuild();
                    Rebuild();
                }) { text = "Add Package" };
                add.style.marginTop = 6;
                _body.Add(add);
            }

            _body.Add(Muted("Edit package fields in the ContentPackageSettings inspector."));
        }

        // ── Release ──────────────────────────────────────────────────────────

        private void BuildRelease()
        {
            var project = MolcaProjectSettings.Instance;
            string platform = ReleasePlatform.Normalize(
                PlatformOf(EditorUserBuildSettings.activeBuildTarget));

            var identity = Card();
            identity.Add(Header("Identity"));
            identity.Add(Field("Project", string.IsNullOrEmpty(project?.ProjectName)
                ? "(not connected)"
                : $"{project.ProjectName}  ·  {project.ProjectCode}"));
            identity.Add(Field("Platform", string.IsNullOrEmpty(platform)
                ? $"{EditorUserBuildSettings.activeBuildTarget} is not a publishable content platform"
                : platform));

            identity.Add(Field("Channel", project?.ContentChannel ?? "stable"));
            identity.Add(Muted(
                "The channel is set on MolcaProjectSettings so every developer building this commit ships " +
                "against the same content. Requesting a non-stable channel needs the project.build.channel.select " +
                "capability, and the server enforces that when a build token is minted."));
            _body.Add(identity);

            if (project == null || string.IsNullOrEmpty(project.ProjectId))
                _body.Add(Help(
                    "This repository is not connected to a Molca project. Connect it in Settings before publishing.",
                    MessageType.Warning));

            var authoring = Card();
            authoring.Add(Header("Next release"));

            var version = new TextField("Content Version") { value = ContentWorkspaceSession.ContentVersion };
            version.RegisterValueChangedCallback(evt =>
            {
                ContentWorkspaceSession.ContentVersion = evt.newValue;
                UpdateVersionWarning(authoring, evt.newValue);
            });
            authoring.Add(version);

            var minApp = new TextField("Min App Version") { value = ContentWorkspaceSession.MinAppVersion };
            minApp.RegisterValueChangedCallback(evt => ContentWorkspaceSession.MinAppVersion = evt.newValue);
            authoring.Add(minApp);

            var maxApp = new TextField("Max App Version") { value = ContentWorkspaceSession.MaxAppVersion };
            maxApp.RegisterValueChangedCallback(evt => ContentWorkspaceSession.MaxAppVersion = evt.newValue);
            authoring.Add(maxApp);
            authoring.Add(Muted(
                "Leave Max empty unless this content is known to break on a newer app version. " +
                "An empty maximum means it keeps working on every future release."));

            var changelog = new TextField("Changelog") { value = ContentWorkspaceSession.Changelog, multiline = true };
            changelog.style.minHeight = 60;
            changelog.RegisterValueChangedCallback(evt => ContentWorkspaceSession.Changelog = evt.newValue);
            authoring.Add(changelog);

            _body.Add(authoring);
            UpdateVersionWarning(authoring, ContentWorkspaceSession.ContentVersion);

            var protocol = Card();
            protocol.Add(Header("Release protocol"));
            protocol.Add(Field("Enabled", _settings.EnableReleaseProtocol ? "yes" : "no"));
            protocol.Add(Field("Content service", _settings.ContentServiceId));
            protocol.Add(Field("Trusted keys", _settings.TrustedReleaseKeys.Count == 0
                ? "none — every release would be refused as untrusted"
                : string.Join(", ", _settings.TrustedReleaseKeys.Select(key => key.KeyId))));
            _body.Add(protocol);
        }

        private static void UpdateVersionWarning(VisualElement host, string version)
        {
            const string name = "version-warning";
            host.Q<VisualElement>(name)?.RemoveFromHierarchy();
            if (ReleaseCompatibility.TryParse(version, out _)) return;

            var warning = Warn($"'{version}' is not SemVer. The server rejects a non-SemVer content version.");
            warning.name = name;
            host.Add(warning);
        }

        // ── Verify ───────────────────────────────────────────────────────────

        private void BuildVerify()
        {
            var configReport = ContentValidation.ValidateSettings(_settings.packageConfigs);

            var summary = Card();
            summary.Add(Header("Configuration"));
            summary.Add(new Label($"{configReport.ErrorCount} error(s), {configReport.WarningCount} warning(s)."));
            foreach (var issue in configReport.Issues) summary.Add(IssueLine(issue));
            if (configReport.Issues.Count == 0) summary.Add(Muted("No configuration findings."));
            _body.Add(summary);

            var build = Card();
            build.Add(Header("Build"));

            if (ContentWorkspaceSession.LastGraph == null)
            {
                build.Add(Muted("No clean build in this session. Content findings need one."));
            }
            else
            {
                build.Add(Field("Staged", ContentWorkspaceSession.StagedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                build.Add(Field("Directory", ContentWorkspaceSession.StagingDirectory));

                var orphans = ContentWorkspaceSession.LastGraph.OrphanBundles.ToList();
                if (orphans.Count > 0)
                    build.Add(Warn($"{orphans.Count} bundle(s) belong to no package and would ship unreferenced."));

                var report = ContentWorkspaceSession.LastReport;
                if (report != null)
                {
                    build.Add(new Label($"{report.ErrorCount} error(s), {report.WarningCount} warning(s) against the build."));
                    foreach (var issue in report.Issues) build.Add(IssueLine(issue));
                }
            }

            var run = new Button(RunCleanBuild) { text = "Build Clean and Verify" };
            run.SetEnabled(!ContentWorkspaceSession.Busy);
            run.style.marginTop = 6;
            build.Add(run);
            build.Add(Muted(
                "Builds Addressables into a fresh staging directory with the build layout enabled, then " +
                "resolves every package's bundles from that layout."));
            _body.Add(build);
        }

        private void RunCleanBuild()
        {
            string platform = ReleasePlatform.Normalize(PlatformOf(EditorUserBuildSettings.activeBuildTarget));
            if (string.IsNullOrEmpty(platform))
            {
                EditorUtility.DisplayDialog("Build",
                    $"{EditorUserBuildSettings.activeBuildTarget} is not a publishable content platform.", "OK");
                return;
            }

            ContentWorkspaceSession.Busy = true;
            ContentWorkspaceSession.BusyStatus = "Building content";
            ContentWorkspaceSession.BusyProgress = -1f;
            UpdateStatus();

            try
            {
                var staging = ContentReleaseStaging.BuildClean(platform);
                if (!staging.Success)
                {
                    ContentWorkspaceSession.InvalidateBuild();
                    EditorUtility.DisplayDialog("Build failed", staging.Error, "OK");
                    return;
                }

                var labels = _settings.packageConfigs
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
                    _settings.packageConfigs, graph,
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
                Rebuild();
            }
        }

        // ── Publish ──────────────────────────────────────────────────────────

        private void BuildPublish()
        {
            var blockers = PublishBlockers().ToList();

            var card = Card();
            card.Add(Header("Publish"));

            if (blockers.Count > 0)
            {
                foreach (string blocker in blockers) card.Add(Warn(blocker));
                card.Add(Muted("Resolve these first. Nothing is uploaded until they are clear."));
                _body.Add(card);
                return;
            }

            card.Add(Field("Version", ContentWorkspaceSession.ContentVersion));
            card.Add(Field("Objects", ContentWorkspaceSession.LastGraph.Bundles.Count.ToString()));
            card.Add(Muted(
                "Uploads go straight to storage using short-lived presigned URLs. No storage credential " +
                "ever reaches this project, and no Molca credential is attached to a storage request."));

            var row = Row();
            var publish = new Button(() => RunPublish(promote: false)) { text = "Publish Draft" };
            var promote = new Button(() => RunPublish(promote: true)) { text = "Publish and Promote" };
            publish.SetEnabled(!ContentWorkspaceSession.Busy);
            promote.SetEnabled(!ContentWorkspaceSession.Busy);
            row.Add(publish);
            row.Add(promote);

            if (ContentWorkspaceSession.Busy)
            {
                var cancel = new Button(CancelPublish) { text = "Cancel" };
                row.Add(cancel);
            }
            card.Add(row);

            card.Add(Muted(
                "Publish Draft uploads and verifies without making the release active — the safe default. " +
                "Promote makes it the release players resolve."));
            _body.Add(card);
        }

        private IEnumerable<string> PublishBlockers()
        {
            var project = MolcaProjectSettings.Instance;
            if (project == null || string.IsNullOrEmpty(project.ProjectId))
                yield return "This repository is not connected to a Molca project.";

            if (string.IsNullOrWhiteSpace(DevEntitlementStoreToken()))
                yield return "No developer entitlement. Sign in from the Molca licence window.";

            if (ContentWorkspaceSession.LastGraph == null)
                yield return "No clean build in this session. Run Build Clean and Verify first.";

            if (!ReleaseCompatibility.TryParse(ContentWorkspaceSession.ContentVersion, out _))
                yield return $"Content version '{ContentWorkspaceSession.ContentVersion}' is not SemVer.";

            var report = ContentWorkspaceSession.LastReport;
            if (report != null && !report.CanPublish)
                yield return $"Verification found {report.ErrorCount} blocking error(s).";
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
                    _settings.packageConfigs,
                    ContentWorkspaceSession.StagingDirectory,
                    channel: MolcaProjectSettings.Instance?.ContentChannel ?? "stable",
                    platform: ReleasePlatform.Normalize(PlatformOf(EditorUserBuildSettings.activeBuildTarget)),
                    contentVersion: ContentWorkspaceSession.ContentVersion,
                    minAppVersion: ContentWorkspaceSession.MinAppVersion,
                    maxAppVersion: ContentWorkspaceSession.MaxAppVersion,
                    changelog: ContentWorkspaceSession.Changelog);
            }
            catch (Exception exception)
            {
                // Candidate derivation refuses a staging directory that disagrees with the layout,
                // which means one of them is stale. Publishing either would produce a release that
                // cannot be downloaded.
                EditorUtility.DisplayDialog("Cannot build a release candidate", exception.Message, "OK");
                return;
            }

            _publishCancellation = new CancellationTokenSource();
            ContentWorkspaceSession.Busy = true;
            ContentWorkspaceSession.LastPublishSummary = "";
            Rebuild();

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
                    UpdateStatus();
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
                Rebuild();
            }
        }

        private void CancelPublish()
        {
            try { _publishCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        // Named rather than inlined so the two credential reads are obvious in a diff instead of
        // buried in the publish path. LoadEffective also picks up the CI environment variable, so a
        // headless publish works without an interactive sign-in.
        private static string DevEntitlementStoreToken() =>
            Molca.Editor.Licensing.DevEntitlementStore.LoadEffective();

        private static string DevLicenseConfigBaseUrl() =>
            Molca.Editor.Licensing.DevLicenseConfig.ServerBaseUrl;

        private static RuntimePlatform PlatformOf(BuildTarget target) => target switch
        {
            BuildTarget.StandaloneWindows64 or BuildTarget.StandaloneWindows => RuntimePlatform.WindowsPlayer,
            BuildTarget.StandaloneOSX => RuntimePlatform.OSXPlayer,
            BuildTarget.StandaloneLinux64 => RuntimePlatform.LinuxPlayer,
            BuildTarget.Android => RuntimePlatform.Android,
            BuildTarget.iOS => RuntimePlatform.IPhonePlayer,
            BuildTarget.WebGL => RuntimePlatform.WebGLPlayer,
            _ => RuntimePlatform.LinuxEditor,
        };

        // ── Small UI helpers ─────────────────────────────────────────────────

        private static VisualElement Card()
        {
            var card = new VisualElement();
            card.style.marginBottom = 8;
            card.style.paddingLeft = 8;
            card.style.paddingRight = 8;
            card.style.paddingTop = 6;
            card.style.paddingBottom = 6;
            card.style.borderLeftWidth = 2;
            card.style.borderLeftColor = MolcaEditorColors.Muted;
            return card;
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        private static Label Header(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 4;
            return label;
        }

        private static VisualElement Field(string name, string value)
        {
            var row = Row();
            var key = new Label(name) { style = { width = 130, color = MolcaEditorColors.Muted } };
            var val = new Label(value ?? "") { style = { flexGrow = 1, whiteSpace = WhiteSpace.Normal } };
            row.Add(key);
            row.Add(val);
            return row;
        }

        private static Label Muted(string text)
        {
            var label = new Label(text) { style = { whiteSpace = WhiteSpace.Normal } };
            label.style.color = MolcaEditorColors.Muted;
            label.style.fontSize = 11;
            return label;
        }

        private static Label Warn(string text)
        {
            var label = new Label(text) { style = { whiteSpace = WhiteSpace.Normal } };
            label.style.color = MolcaEditorColors.StatusWarn;
            return label;
        }

        private static VisualElement Dot(Color color)
        {
            var dot = new Label("●");
            dot.style.color = color;
            dot.style.width = 16;
            return dot;
        }

        private static Label IssueLine(ContentIssue issue)
        {
            string marker = issue.Severity == ContentIssueSeverity.Error ? "✕"
                : issue.Severity == ContentIssueSeverity.Warning ? "!" : "·";
            var label = new Label($"{marker} [{issue.Code}] {issue.Message}")
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 11 },
            };
            label.style.color = issue.Severity == ContentIssueSeverity.Error ? MolcaEditorColors.StatusError
                : issue.Severity == ContentIssueSeverity.Warning ? MolcaEditorColors.StatusWarn
                : MolcaEditorColors.Muted;
            return label;
        }

        private static VisualElement Help(string text, MessageType type)
        {
            var box = new HelpBox(text, type switch
            {
                MessageType.Error => HelpBoxMessageType.Error,
                MessageType.Warning => HelpBoxMessageType.Warning,
                _ => HelpBoxMessageType.Info,
            });
            box.style.marginBottom = 6;
            return box;
        }
    }
}
