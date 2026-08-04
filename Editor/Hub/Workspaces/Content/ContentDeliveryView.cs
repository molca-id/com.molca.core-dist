using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>How content reaches a device: download behaviour, cache budget, and the legacy catalog.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>delivery</c> node.
    /// <para>
    /// These settings had no Hub page at all: they existed only in the collapsed bottom section of the
    /// <c>ContentPackageSettings</c> inspector, which is also the surface this workspace was supposed to
    /// replace. A project could therefore be fully authored here and still need the Inspector to change
    /// its cache budget.
    /// </para>
    /// <para>
    /// <b>Edits here keep the staged build.</b> None of these values feed the Addressables build graph,
    /// so throwing away a clean build because someone changed the retry count would cost fifteen minutes
    /// to protect nothing — which is why the context has a separate apply path for them.
    /// </para>
    /// </remarks>
    internal sealed class ContentDeliveryView : VisualElement
    {
        private readonly ContentWorkspaceContext _context;

        /// <summary>Builds the page.</summary>
        /// <param name="context">The workspace context.</param>
        public ContentDeliveryView(ContentWorkspaceContext context)
        {
            _context = context;

            Add(new MolcaWorkspaceHeader("Delivery", "Downloads, cache, and the legacy catalog"));

            BuildDownloads();
            BuildVersioning();
            BuildCloudStatus();
            BuildLegacyRemote();
            BuildLegacyBuild();
            BuildTools();
        }

        private void BuildDownloads()
        {
            var settings = _context.Settings;
            var card = ContentWorkspaceUi.Card("Downloads & storage");

            card.Body.Add(MolcaFields.EditInt(
                "Concurrent downloads",
                settings.MaxConcurrentDownloads,
                value => _context.ApplySettingsEdit(_context.Editing.SetMaxConcurrentDownloads(value)),
                1, 16,
                "How many packages install at once."));

            card.Body.Add(MolcaFields.EditInt(
                "Retry attempts",
                settings.MaxRetryAttempts,
                value => _context.ApplySettingsEdit(_context.Editing.SetMaxRetryAttempts(value)),
                0, 10,
                "How many times a failed download is retried before it is reported as failed."));

            card.Body.Add(MolcaFields.EditByteSize(
                "Cache budget",
                settings.MaxCacheBytes,
                value => _context.ApplySettingsEdit(_context.Editing.SetMaxCacheBytes(value)),
                "Soft cap on total cached package bytes. When exceeded, least-recently-used " +
                "non-required packages are evicted to fit. 0 means no eviction."));

            card.Body.Add(MolcaFields.Note(settings.MaxCacheBytes == 0
                ? "Unlimited: nothing is ever evicted, and the cache grows to whatever the content needs."
                : $"About {SizeFormatter.Format(settings.MaxCacheBytes)}."));

            card.Body.Add(MolcaFields.EditToggle(
                "Verbose logging",
                settings.EnableVerboseLogging,
                value => _context.ApplySettingsEdit(_context.Editing.SetVerboseLogging(value))));

            Disable(card.Body);
            Add(card);
        }

        private void BuildVersioning()
        {
            var settings = _context.Settings;
            var card = ContentWorkspaceUi.Card("Content versioning");

            card.Body.Add(MolcaFields.EditToggle(
                "Enabled",
                settings.EnableContentVersioning,
                value => _context.ApplySettingsEdit(_context.Editing.SetContentVersioning(value)),
                "Changes how the deployed packages.json is parsed, not just what the app does with it. " +
                "On, it must be a ContentVersionIndex; off, a flat RemotePackageManifest."));

            card.Body.Add(MolcaFields.EditText(
                "App version",
                settings.AppVersion,
                value => _context.ApplySettingsEdit(_context.Editing.SetAppVersion(value)),
                "Used to filter compatible content versions. Empty falls back to Application.version.",
                placeholder: "Application.version"));

            if (!settings.EnableContentVersioning)
            {
                card.Body.Add(MolcaFields.Note(
                    "Versioning is off, so the app version is not consulted and version switching is a no-op."));
            }

            Disable(card.Body);
            Add(card);
        }

        private void BuildLegacyRemote()
        {
            var settings = _context.Settings;
            var card = ContentWorkspaceUi.Card(
                "Remote content (legacy)",
                settings.EnableReleaseProtocol ? "Superseded by the release protocol" : null,
                settings.EnableReleaseProtocol ? MolcaStatusKind.Idle : MolcaStatusKind.None,
                settings.EnableReleaseProtocol ? "Unused" : null);

            if (settings.EnableReleaseProtocol)
            {
                card.Body.Add(MolcaFields.Note(
                    "The release protocol is on, so content is resolved through it and these URLs are " +
                    "not consulted. They are kept because turning the protocol off falls back to them."));
            }

            card.Body.Add(MolcaFields.EditText(
                "Catalog URL",
                settings.RemoteCatalogUrl,
                value => _context.ApplySettingsEdit(_context.Editing.SetRemoteCatalogUrl(value)),
                placeholder: "https://cdn.example.com/catalog.json"));

            card.Body.Add(MolcaFields.EditText(
                "Manifest URL",
                settings.RemotePackagesManifestUrl,
                value => _context.ApplySettingsEdit(_context.Editing.SetRemotePackagesManifestUrl(value)),
                "The packages.json deployed alongside the Addressables catalog. Empty disables remote " +
                "manifest fetching.",
                placeholder: "https://cdn.example.com/packages.json"));

            card.Body.Add(MolcaFields.EditToggle(
                "Check for catalog updates",
                settings.CheckForCatalogUpdates,
                value => _context.ApplySettingsEdit(_context.Editing.SetCheckForCatalogUpdates(value))));

            Disable(card.Body);
            Add(card);
        }

        /// <summary>
        /// Whether the running player can reach the content host, and what it last saw there.
        /// </summary>
        /// <remarks>
        /// Only meaningful while playing, so it is absent otherwise rather than shown as "Unknown".
        /// The distinction that matters is <c>NotConfigured</c> versus <c>Unreachable</c>: the first is
        /// a project that never set a manifest URL, the second is one that did and got nothing back.
        /// </remarks>
        private void BuildCloudStatus()
        {
            var status = _context.Runtime?.CloudStatus;
            if (status == null) return;

            var kind = status.State switch
            {
                CloudConnectionState.Connected => MolcaStatusKind.Ok,
                CloudConnectionState.Unreachable => MolcaStatusKind.Error,
                CloudConnectionState.NotConfigured => MolcaStatusKind.Warning,
                _ => MolcaStatusKind.Idle,
            };

            var card = ContentWorkspaceUi.Card(
                "Cloud status", "From the running player", kind, status.State.ToString());

            card.Body.Add(MolcaFields.ReadOnly("Last sync", status.LastSyncTime.HasValue
                ? status.LastSyncTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : null));

            if (!string.IsNullOrEmpty(status.ManifestGeneratedAt))
                card.Body.Add(MolcaFields.ReadOnly("Manifest built", status.ManifestGeneratedAt));

            if (status.RemotePackageCount > 0)
                card.Body.Add(MolcaFields.ReadOnly("Remote packages", status.RemotePackageCount.ToString()));

            if (status.State == CloudConnectionState.Unreachable && !string.IsNullOrEmpty(status.ErrorMessage))
                card.Body.Add(ContentWorkspaceUi.Help(status.ErrorMessage, HelpBoxMessageType.Warning));

            Add(card);
        }

        /// <summary>
        /// The schema-v1 build: Addressables to a configured path, plus a manifest beside it.
        /// </summary>
        /// <remarks>
        /// Beside the legacy remote URLs because it is the other half of that path. Ship → Verify is
        /// the release-protocol build and produces something entirely different; keeping the two on one
        /// page is how an author ends up running the superseded one because it had the nearer button.
        /// </remarks>
        private void BuildLegacyBuild()
        {
            var config = ContentLegacyBuild.LoadConfig();
            var card = ContentWorkspaceUi.Card(
                "Legacy build",
                config == null ? "No build config selected" : AssetDatabase.GetAssetPath(config));

            var picker = new ObjectField { objectType = typeof(ContentPackageBuildConfig), value = config };
            picker.RegisterValueChangedCallback(evt =>
            {
                ContentLegacyBuild.SaveConfig(evt.newValue as ContentPackageBuildConfig);
                _context.Reload();
            });
            card.Body.Add(MolcaFields.Row("Build config", picker,
                "Names the local output path and the remote load URL a legacy build uses."));

            if (config == null)
            {
                card.Body.Add(MolcaFields.Actions(MolcaButtons.Mini("Create…", () =>
                {
                    if (ContentLegacyBuild.CreateConfig() != null) _context.Reload();
                })));
                card.Body.Add(MolcaFields.Note(
                    "A project on the release protocol does not need one. This path exists for projects " +
                    "that still upload a catalog themselves."));
                Add(card);
                return;
            }

            var serialized = new SerializedObject(config);
            var local = new PropertyField(serialized.FindProperty("localBuildPath"), "Local output");
            var remote = new PropertyField(serialized.FindProperty("remoteLoadURL"), "Remote load URL");
            local.Bind(serialized);
            remote.Bind(serialized);
            card.Body.Add(local);
            card.Body.Add(remote);

            string target = EditorUserBuildSettings.activeBuildTarget.ToString();
            string resolved = config.ResolvedLocalBuildPath(target);
            card.Body.Add(MolcaFields.ReadOnly("Resolves to", resolved));

            string mismatch = ContentLegacyBuild.DescribeProfileMismatch(
                AddressableAssetSettingsDefaultObject.Settings, config);
            if (mismatch != null)
            {
                card.Body.Add(ContentWorkspaceUi.Help(
                    "The active Addressables profile disagrees with this config:\n\n" + mismatch +
                    "\n\nFix these in the Addressables Profiles window — nothing here writes to the " +
                    "shared profile asset.",
                    HelpBoxMessageType.Warning));
            }

            var summary = new VisualElement();

            var full = MolcaButtons.Mini("Build player content",
                () => RunLegacyBuild(config, summary, fullBuild: true));
            var update = MolcaButtons.Mini("Build content update",
                () => RunLegacyBuild(config, summary, fullBuild: false));
            update.SetEnabled(ContentLegacyBuild.CanBuildUpdate());
            update.tooltip = ContentLegacyBuild.CanBuildUpdate()
                ? "Incremental rebuild of changed groups only."
                : "Needs a previous full build's content state file.";

            card.Body.Add(MolcaFields.Actions(full, update));
            card.Body.Add(summary);

            if (Directory.Exists(resolved))
            {
                card.Body.Add(MolcaFields.Actions(MolcaButtons.Mini("Verify output",
                    () => RenderVerify(summary, config, resolved))));
            }
            else
            {
                card.Body.Add(MolcaFields.Note($"No build found at {resolved}."));
            }

            Add(card);
        }

        private void RunLegacyBuild(
            ContentPackageBuildConfig config, VisualElement summary, bool fullBuild)
        {
            string message = ContentLegacyBuild.Run(_context.Settings, config, fullBuild);
            summary.Clear();
            summary.Add(MolcaFields.Note(message));
        }

        private void RenderVerify(
            VisualElement summary, ContentPackageBuildConfig config, string buildPath)
        {
            summary.Clear();
            var rows = ContentLegacyBuild.Verify(_context.Settings, buildPath);

            if (rows.Count == 0)
            {
                summary.Add(MolcaFields.Note("No packages to verify."));
                return;
            }

            foreach (var row in rows)
            {
                var listRow = new MolcaListRow(row.PackageId);
                listRow.AddMetadata(new MolcaStatusBadge(
                    row.Ok ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                    row.Ok
                        ? $"{row.Bundles} bundle(s) · {SizeFormatter.Format(row.Bytes)}"
                        : row.Error ?? "no bundles found"));
                summary.Add(listRow);
            }
        }

        /// <summary>
        /// Moving a package definition in and out of a file.
        /// </summary>
        /// <remarks>
        /// "Reset settings to defaults" did not come across from the inspector. It called
        /// <c>ContentPackageSettings.ResetToDefaults</c>, which returns immediately when the paired
        /// <c>SettingState</c> is null — and it always is outside Play mode, where the button lived. So
        /// the button did nothing, every time it was pressed. Porting a no-op would have carried the
        /// impression that it worked; the delivery fields above are each individually editable, which
        /// is what someone reaching for it actually wanted.
        /// </remarks>
        private void BuildTools()
        {
            var card = ContentWorkspaceUi.Card("Tools");

            var import = MolcaButtons.Mini("Import manifest…", () =>
            {
                string imported = ContentManifestIo.Import(_context);
                if (imported != null) _context.Reload();
            });
            import.tooltip = "Defines a package from a JSON manifest, through the same write path as the form.";
            import.SetEnabled(!_context.IsReadOnly);

            var export = MolcaButtons.Mini("Export settings…", () => ContentManifestIo.Export(_context.Settings));
            export.tooltip = "Writes the whole settings asset out as JSON.";

            card.Body.Add(MolcaFields.Actions(import, export));
            Add(card);
        }

        private void Disable(VisualElement body)
        {
            if (!_context.IsReadOnly) return;
            body.SetEnabled(false);
        }
    }
}
