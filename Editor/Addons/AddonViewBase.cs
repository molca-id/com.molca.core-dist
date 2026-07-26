using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Molca.Editor.Icons;
using Molca.Editor.Licensing;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Shared base for the Add-ons Hub views (Browse / Installed). Owns the design-language shell (title +
    /// search + status + scrollable card list), the developer-license gate, the add-on acquisition services,
    /// the confirmed install/update/remove/offline-import flows, and the shared card builder. Subclasses only
    /// supply what to fetch (<see cref="FetchAsync"/>) and how to turn it into cards (<see cref="RenderCards"/>).
    /// </summary>
    /// <remarks>
    /// Design language via <see cref="MolcaEditorUi.Apply"/> + the scoped <c>MolcaAddonsView.uss</c>; no inline
    /// colors. Acquisition/verification is unchanged — this is presentation only.
    /// </remarks>
    internal abstract class AddonViewBase : VisualElement
    {
        private const string StyleSheetPath = "Packages/com.molca.core/Editor/Addons/MolcaAddonsView.uss";

        protected readonly AddonCatalogClient Client = new AddonCatalogClient();
        protected readonly AddonInstaller Installer = new AddonInstaller();
        protected readonly ScrollView List;
        protected readonly Label Status;
        protected readonly MolcaSearchField Search;

        private readonly VisualElement _channelSlot;
        private CancellationTokenSource _cancellation;
        private bool _busy;

        /// <summary>Pre-release channel this view requests; the server caps it at the license ceiling.</summary>
        protected string Channel { get; private set; } = AddonChannels.Preferred;

        /// <summary>Row-shaped display model shared by Browse and Installed cards.</summary>
        protected sealed class AddonCardModel
        {
            public string Id;
            public string Name;
            public string Publisher;
            public string Description;
            public string VersionText;
            public bool HasRuntime;
            public MolcaStatusKind Status = MolcaStatusKind.None;
            public string StatusText;
            public Texture2D Icon;
            public readonly List<(string label, string value)> Details = new List<(string, string)>();
        }

        protected AddonViewBase(string title, string description, bool showImportAction)
        {
            AddToClassList("molca-addons-view");
            MolcaEditorUi.Apply(this);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null && !styleSheets.Contains(sheet)) styleSheets.Add(sheet);

            var header = new VisualElement();
            header.AddToClassList("molca-addons-header");

            var titleStack = new VisualElement();
            titleStack.AddToClassList("molca-addons-title-stack");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("molca-addons-title");
            titleStack.Add(titleLabel);
            var descLabel = new Label(description);
            descLabel.AddToClassList("molca-addons-subtitle");
            titleStack.Add(descLabel);
            header.Add(titleStack);

            var actions = new VisualElement();
            actions.AddToClassList("molca-addons-header-actions");
            // Populated after the first fetch: only the server knows how far up the ladder this license
            // reaches, and offering a channel it cannot see would be a dead control.
            _channelSlot = new VisualElement();
            _channelSlot.AddToClassList("molca-addons-channel");
            actions.Add(_channelSlot);
            if (showImportAction)
                actions.Add(MolcaButtons.Toolbar("Import signed bundle…", ImportOffline));
            actions.Add(MolcaButtons.Toolbar("Refresh", () => Refresh(forceRefresh: true)));
            header.Add(actions);
            Add(header);

            Search = new MolcaSearchField("Search add-ons");
            Search.OnSearchChanged += _ => RenderCards(Search.Value);
            Add(Search);

            Status = new Label();
            Status.AddToClassList("molca-addons-status");
            Add(Status);

            List = new ScrollView();
            List.AddToClassList("molca-addons-list");
            Add(List);

            RegisterCallback<DetachFromPanelEvent>(_ => CancelOutstanding());
            // Deferred to AttachToPanel (not the ctor): subclass field initializers run after this base ctor,
            // so fetching here would race an uninitialized subclass. Attach fires once the view is fully built.
            // Switching tabs reuses the shared catalog cache rather than refetching.
            RegisterCallback<AttachToPanelEvent>(_ => Refresh(forceRefresh: false));
        }

        /// <summary>Fetches the data the view needs into subclass-held state. Called on refresh.</summary>
        /// <param name="forceRefresh">True to bypass the shared catalog cache.</param>
        /// <param name="cancellationToken">Cancels the fetch when the view detaches.</param>
        protected abstract Awaitable FetchAsync(bool forceRefresh, CancellationToken cancellationToken);

        /// <summary>Rebuilds the card list from fetched state, applying the current search filter.</summary>
        protected abstract void RenderCards(string filter);

        /// <summary>
        /// Rebuilds the channel selector once a fetch has revealed the license's ceiling. A license
        /// limited to stable gets no control at all rather than a one-option dropdown.
        /// </summary>
        protected void UpdateChannelSelector(string maxChannel)
        {
            _channelSlot.Clear();
            if (AddonChannels.Rank(maxChannel) <= 0) return;

            var choices = new List<string>(AddonChannels.Available(maxChannel));
            string current = choices.Contains(Channel) ? Channel : AddonChannels.Stable;
            var dropdown = new DropdownField(choices, choices.IndexOf(current));
            dropdown.tooltip = "Pre-release channel this license may install from.";
            dropdown.RegisterValueChangedCallback(changed =>
            {
                Channel = changed.newValue;
                AddonChannels.Preferred = changed.newValue;
                Refresh(forceRefresh: true);
            });
            _channelSlot.Add(dropdown);
        }

        /// <summary>Re-applies the license gate, refetches, and re-renders.</summary>
        /// <param name="forceRefresh">True to bypass the shared catalog cache.</param>
        protected async void Refresh(bool forceRefresh = true)
        {
            if (_busy) return;
            List.Clear();

            // Ungated add-ons still require a valid Molca license. When invalid, the management surface is
            // withheld (offline import, in the header, stays available on the Installed view by design).
            if (!IsLicensed(out DevLicenseStatus licenseStatus))
            {
                Status.text = $"Add-on management is unavailable — your Molca developer license is " +
                              $"{Describe(licenseStatus)}. Add-ons require a valid Molca license.";
                List.Add(MolcaButtons.Primary("Open developer sign-in", DevLicenseWindow.Open));
                return;
            }

            _busy = true;
            ResetCancellation();
            Status.text = "Loading…";
            try
            {
                await FetchAsync(forceRefresh, _cancellation.Token);
                RenderCards(Search.Value);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Status.text = $"Could not load add-ons: {exception.Message}";
            }
            finally { _busy = false; }
        }

        // ---- Shared card builder --------------------------------------------------------------------

        /// <summary>Builds one [icon | card] row from a model plus header-action buttons.</summary>
        protected VisualElement BuildCard(AddonCardModel model, params Button[] actions)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-addons-row");

            var icon = new Image { scaleMode = ScaleMode.ScaleToFit, image = model.Icon != null ? model.Icon : GenericIcon };
            icon.AddToClassList("molca-addons-icon");
            row.Add(icon);

            string runtimeText = model.HasRuntime ? "runtime code" : "Editor-only";
            string subtitle = $"{model.Publisher}  ·  {model.VersionText}  ·  {runtimeText}";
            var card = new MolcaSectionCard(model.Name, subtitle, model.Status, model.StatusText);
            card.AddToClassList("molca-addons-card");

            var desc = new Label(string.IsNullOrWhiteSpace(model.Description) ? "No description." : model.Description);
            desc.AddToClassList("molca-addons-description");
            card.Body.Add(desc);

            if (model.Details.Count > 0)
            {
                var fold = new Foldout { text = "Details", value = false };
                fold.AddToClassList("molca-addons-details");
                foreach (var (label, value) in model.Details)
                {
                    var detailRow = new VisualElement();
                    detailRow.AddToClassList("molca-addons-detail-row");
                    var labelEl = new Label(label);
                    labelEl.AddToClassList("molca-addons-detail-label");
                    var valueEl = new Label(value ?? string.Empty);
                    valueEl.AddToClassList("molca-addons-detail-value");
                    detailRow.Add(labelEl);
                    detailRow.Add(valueEl);
                    fold.Add(detailRow);
                }
                card.Body.Add(fold);
            }

            foreach (var action in actions)
                if (action != null) card.AddHeaderAction(action);

            row.Add(card);
            return row;
        }

        /// <summary>Interim generic add-on glyph (reuses the storage family icon until a bespoke one ships).</summary>
        protected static Texture2D GenericIcon => MolcaEditorIcons.Family("storage");

        /// <summary>
        /// Loads an installed add-on's declared <c>package.json</c> <c>"icon"</c> (package-relative), or null
        /// when absent/unresolvable. Only meaningful for installed packages present under <c>Packages/</c>.
        /// </summary>
        protected static Texture2D LoadPackageIcon(string id)
        {
            try
            {
                string manifestPath = Path.GetFullPath($"Packages/{id}/package.json");
                if (!File.Exists(manifestPath)) return null;
                string iconPath = (string)JObject.Parse(File.ReadAllText(manifestPath))["icon"];
                if (string.IsNullOrWhiteSpace(iconPath)) return null;
                return AssetDatabase.LoadAssetAtPath<Texture2D>($"Packages/{id}/{iconPath}");
            }
            catch { return null; }
        }

        /// <summary>Trims free-form publisher text to a length a modal dialog can actually show.</summary>
        protected static string Truncate(string value, int limit) =>
            string.IsNullOrEmpty(value) || value.Length <= limit ? value : value.Substring(0, limit) + "…";

        protected static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "unknown size";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }

        // ---- Shared install / update / remove / import flows ----------------------------------------

        protected async void BeginInstall(
            AddonCatalogResponse catalog, AddonCatalogPackage pack, AddonCatalogVersion version)
        {
            if (_busy) return;
            if (!IsLicensed(out DevLicenseStatus licenseStatus))
            {
                Status.text = $"Cannot install — your Molca developer license is {Describe(licenseStatus)}.";
                Refresh();
                return;
            }
            _busy = true;
            ResetCancellation();
            Status.text = $"Resolving {pack.id} {version.version}…";
            try
            {
                if (!AddonDependencyResolver.TryResolve(catalog, pack.id, version.version,
                    InstalledAddonsAsset.FindExisting(), out AddonInstallPlan plan,
                    out string resolutionError))
                {
                    AddonAuditLog.Record("resolve", "failed", pack.id, version.version,
                        version.sha256, error: resolutionError);
                    EditorUtility.DisplayDialog("Add-on dependency conflict", resolutionError, "OK");
                    Status.text = resolutionError;
                    return;
                }

                bool approvalRequired = plan.Ordered.Any(entry =>
                    !string.Equals(entry.Package.policyStatus, "approved", StringComparison.Ordinal));
                if (approvalRequired && !catalog.canManagePolicy)
                {
                    Status.text = "This add-on closure is not approved for the connected project.";
                    EditorUtility.DisplayDialog("Project approval required",
                        "Ask a project owner or manager to approve this add-on and its dependencies.", "OK");
                    return;
                }

                bool hasRuntime = plan.Ordered.Any(entry => entry.Version.hasRuntime);
                string warning = hasRuntime
                    ? "\n\nThis closure contains runtime code. Rebuild players before distributing them."
                    : "\n\nThis closure is Editor-only and will not enter player builds.";
                string notes = string.IsNullOrWhiteSpace(version.releaseNotes)
                    ? string.Empty
                    : $"\n\nWhat's new:\n{Truncate(version.releaseNotes, 600)}";
                string tree = string.Join("\n", plan.Ordered.Select(entry =>
                    $"{(entry.Requested ? "Requested" : "Required ")}  {entry.Package.id} " +
                    $"{entry.Version.version}" +
                    $"{(entry.AlreadyInstalled ? " (already satisfied)" : string.Empty)}"));
                string external = plan.ExternalPrerequisites.Count == 0 ? string.Empty
                    : "\n\nExternal Unity packages:\n" + string.Join("\n",
                        plan.ExternalPrerequisites.Select(item =>
                            $"{item.packageId} — {item.source}: {item.spec}"));
                string confirmation =
                    $"Install {pack.name} {version.version}?\n\n{tree}{external}\n\n" +
                    $"{plan.ChangedCount} Molca package change" +
                    $"{(plan.ChangedCount == 1 ? string.Empty : "s")}." +
                    (approvalRequired
                        ? "\nThis also approves the complete closure for this project."
                        : string.Empty) +
                    warning + notes + "\n\nDownloaded code runs with full Unity Editor privileges.";
                if (!EditorUtility.DisplayDialog(
                    "Confirm add-on dependency plan", confirmation, "Approve and install", "Cancel"))
                {
                    Status.text = "Installation canceled.";
                    return;
                }

                if (approvalRequired)
                {
                    Status.text = "Approving project add-on closure…";
                    var approval = await Client.ApproveAsync(
                        pack.id, version.version, Channel, _cancellation.Token);
                    if (!approval.Success)
                    {
                        Status.text = approval.Error;
                        EditorUtility.DisplayDialog("Add-on approval failed", approval.Error, "OK");
                        return;
                    }
                }

                AddonTransactionResume.Save(plan, Channel);
                var manifests = new List<VerifiedAddonManifest>();
                foreach (AddonInstallPlanEntry entry in plan.Ordered)
                {
                    Status.text = $"Verifying {entry.Package.id} {entry.Version.version}…";
                    var manifestResult = await Client.GetManifestAsync(
                        entry.Package.id, entry.Version.version, _cancellation.Token);
                    if (!manifestResult.Success)
                    {
                        AddonAuditLog.Record("verify", "failed", entry.Package.id,
                            entry.Version.version, entry.Version.sha256, error: manifestResult.Error);
                        AddonTelemetry.Record("verify_fail", entry.Package.id,
                            entry.Version.version, manifestResult.Error);
                        EditorUtility.DisplayDialog(
                            "Add-on verification failed", manifestResult.Error, "OK");
                        Status.text = manifestResult.Error;
                        AddonTransactionResume.Fail(manifestResult.Error);
                        return;
                    }
                    if (!AddonDependencyResolver.ManifestMatches(
                        entry.Version, manifestResult.Value.Manifest, out string metadataError))
                    {
                        Status.text = metadataError;
                        EditorUtility.DisplayDialog(
                            "Signed dependency mismatch", metadataError, "OK");
                        AddonTransactionResume.Fail(metadataError);
                        return;
                    }
                    manifests.Add(manifestResult.Value);
                }

                Status.text = "Resolving external Unity packages…";
                var prerequisites = await ExternalPrerequisiteResolver.EnsureAsync(
                    plan.ExternalPrerequisites, _cancellation.Token);
                if (!prerequisites.Success)
                {
                    Status.text = prerequisites.Error;
                    EditorUtility.DisplayDialog(
                        "External prerequisite failed", prerequisites.Error, "OK");
                    AddonTransactionResume.Fail(prerequisites.Error);
                    return;
                }

                Status.text = $"Installing {plan.RootId} dependency closure…";
                AddonInstallResult result = await Installer.InstallGraphAsync(
                    plan, manifests, _cancellation.Token);
                if (result.Success)
                {
                    AddonTransactionResume.Complete();
                    AddonCatalogCache.Invalidate();
                }
                else AddonTransactionResume.Fail(result.Message);
                Status.text = result.Message;
                if (!result.Success)
                    EditorUtility.DisplayDialog("Add-on installation failed", result.Message, "OK");
                else if (!string.IsNullOrEmpty(result.RecoveryPath))
                    Debug.Log($"[Molca Add-ons] Recovery retained at: {result.RecoveryPath}");
            }
            catch (OperationCanceledException)
            {
                AddonTransactionResume.Fail("Installation canceled.");
                Status.text = "Installation canceled.";
            }
            catch (Exception exception)
            {
                AddonTransactionResume.Fail(exception.Message);
                Status.text = $"Installation failed: {exception.Message}";
                Debug.LogError($"[Molca Add-ons] Installation failed: {exception}");
            }
            finally { _busy = false; }
        }

        protected void Remove(InstalledAddonRecord installed)
        {
            if (_busy) return;
            if (!IsLicensed(out DevLicenseStatus licenseStatus))
            {
                Status.text = $"Cannot manage add-ons — your Molca developer license is {Describe(licenseStatus)}.";
                Refresh();
                return;
            }
            string runtimeWarning = installed.hasRuntime ? "\n\nThis removes runtime code; rebuild affected players." : string.Empty;
            if (!EditorUtility.DisplayDialog("Remove add-on",
                $"Remove {installed.name} {installed.version} ({installed.id})?\n\n" +
                "The package will be moved into Library/Molca/Addons/Recovery rather than permanently deleted." + runtimeWarning,
                "Remove", "Cancel")) return;

            AddonInstallResult result = Installer.Remove(installed.id);
            if (result.Success) AddonCatalogCache.Invalidate();
            Status.text = result.Message;
            if (!result.Success) EditorUtility.DisplayDialog("Add-on removal failed", result.Message, "OK");
            else if (!string.IsNullOrEmpty(result.RecoveryPath))
                Debug.Log($"[Molca Add-ons] Removed package retained for recovery at: {result.RecoveryPath}");
            // Removal triggers a deferred domain reload that recreates this view; no manual Refresh().
        }

        protected void ImportOffline()
        {
            if (_busy) return;
            string manifestPath = EditorUtility.OpenFilePanel("Select signed Molca manifest", string.Empty, "molca-manifest");
            if (string.IsNullOrEmpty(manifestPath)) return;
            if (!AddonManifestVerifier.TryVerifyOffline(File.ReadAllText(manifestPath), out var verified, out string error))
            {
                EditorUtility.DisplayDialog("Offline manifest rejected", error, "OK");
                return;
            }
            string archivePath = EditorUtility.OpenFilePanel("Select matching UPM tarball", Path.GetDirectoryName(manifestPath), "tgz");
            if (string.IsNullOrEmpty(archivePath)) return;
            AddonManifest manifest = verified.Manifest;
            string warning = manifest.hasRuntime
                ? "\n\nThis pack contains runtime code. Rebuild players before distributing them."
                : "\n\nThis pack is Editor-only and will not enter player builds.";
            if (!EditorUtility.DisplayDialog("Confirm offline signed add-on",
                $"Install {manifest.name} {manifest.version}?\n\nPackage: {manifest.id}\nPublisher: {manifest.publisher}" +
                $"\nArtifact: {archivePath}\nSHA-256: {manifest.sha256}\nSigning key: {verified.KeyId}" + warning +
                "\n\nThe local bytes must exactly match the signed hash. Downloaded code runs with full Editor privileges.",
                "Install", "Cancel")) return;
            AddonInstallResult result = Installer.InstallOffline(verified, archivePath);
            Status.text = result.Message;
            if (!result.Success) EditorUtility.DisplayDialog("Offline installation failed", result.Message, "OK");
            // A successful import triggers a deferred domain reload that recreates this view; no manual Refresh().
        }

        // ---- License gate + cancellation ------------------------------------------------------------

        protected static bool IsLicensed(out DevLicenseStatus status)
        {
            status = DevEntitlementVerifier.Evaluate(
                DevEntitlementStore.LoadEffective(), SystemInfo.deviceUniqueIdentifier, out _);
            return status == DevLicenseStatus.Valid;
        }

        protected static string Describe(DevLicenseStatus status) => status switch
        {
            DevLicenseStatus.Missing => "not signed in",
            DevLicenseStatus.Expired => "expired",
            DevLicenseStatus.WrongMachine => "issued for a different machine",
            _ => "invalid",
        };

        private void ResetCancellation()
        {
            CancelOutstanding();
            _cancellation = new CancellationTokenSource();
        }

        private void CancelOutstanding()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
