using System;
using System.Threading;
using Molca.Editor.About;
using Molca.Editor.Addons;
using Molca.Editor.Icons;
using Molca.Editor.Licensing;
using Molca.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// About section for the Molca Hub Settings workspace: what this project is running, whether a newer
    /// Core exists, the license behind it, and where to go next.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the About rail section is active.
    /// This view owns no version or update logic — <see cref="FrameworkVersionInfo"/> resolves the facts,
    /// <see cref="FrameworkUpdateEvaluator"/> decides the verdict, and <see cref="FrameworkUpgrader"/> applies
    /// or hands over the upgrade. Everything here is presentation and wiring.
    /// The update check runs on attach only when the cached answer is stale and the developer has left
    /// <see cref="FrameworkUpdatePreferences.CheckOnOpen"/> on; it never runs in batch mode.
    /// </remarks>
    internal sealed class MolcaHubAboutSection : VisualElement
    {
        private const float CompactLayoutWidth = 560f;
        private const int MaxLinkDisplayLength = 52;

        private readonly FrameworkUpdateClient _client = new FrameworkUpdateClient();

        private MolcaSectionCard _updateCard;
        private VisualElement _updateBody;
        private Label _updateStatus;
        private Button _checkButton;
        private CancellationTokenSource _cancellation;
        private FrameworkUpdateState _state;
        private bool _busy;

        internal MolcaHubAboutSection()
        {
            AddToClassList("molca-hub-about-section");

            BuildHeader();
            BuildUpdateCard();
            BuildVersionsCard();
            BuildLicenseCard();
            BuildLinksCard();

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => CancelOutstanding());
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                // Render whatever is already known first, so switching back to About is instant, then let a
                // stale cache trigger one deferred check.
                RenderUpdate(FromCache());
                schedule.Execute(MaybeCheckOnOpen);
            });
        }

        // -------------------------------------------------------------------
        // Header
        // -------------------------------------------------------------------

        private void BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("molca-hub-about-header");
            Add(header);

            var logo = new VisualElement();
            logo.AddToClassList("molca-hub-about-logo");
            var icon = MolcaEditorIcons.Logo;
            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("molca-hub-about-logo__image");
                logo.Add(image);
            }
            else
            {
                var mark = new Label("m");
                mark.AddToClassList("molca-hub-about-logo__mark");
                logo.Add(mark);
            }
            header.Add(logo);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-about-title-stack");
            header.Add(stack);

            var title = new Label("Molca Framework");
            title.AddToClassList("molca-hub-about-title");
            stack.Add(title);

            var subtitle = new Label($"Core {Describe(FrameworkVersionInfo.CoreVersion)}  ·  " +
                                     $"Unity {FrameworkVersionInfo.UnityVersion}  ·  " +
                                     FrameworkVersionInfo.DescribeSource(FrameworkVersionInfo.CoreSource));
            subtitle.AddToClassList("molca-hub-about-subtitle");
            stack.Add(subtitle);
        }

        // -------------------------------------------------------------------
        // Updates
        // -------------------------------------------------------------------

        private void BuildUpdateCard()
        {
            _updateCard = new MolcaSectionCard("Updates", null, MolcaStatusKind.Idle, "Not checked");
            _checkButton = MolcaButtons.Toolbar("Check now", () => Check(forceRefresh: true));
            _updateCard.AddHeaderAction(_checkButton);
            Add(_updateCard);

            _updateStatus = new Label();
            _updateStatus.AddToClassList("molca-hub-muted");
            _updateCard.Body.Add(_updateStatus);

            _updateBody = new VisualElement();
            _updateBody.AddToClassList("molca-hub-about-update-body");
            _updateCard.Body.Add(_updateBody);

            var options = new VisualElement();
            options.AddToClassList("molca-hub-about-options");
            _updateCard.Body.Add(options);

            options.Add(BuildToggle("Check when this section opens", FrameworkUpdatePreferences.CheckOnOpen,
                value => FrameworkUpdatePreferences.CheckOnOpen = value,
                "When off, updates are only checked by the Check now button."));
            options.Add(BuildToggle("Show available updates in the activity rail",
                FrameworkUpdatePreferences.ShowActivityChip,
                value => FrameworkUpdatePreferences.ShowActivityChip = value,
                "Adds a chip to the Hub's bottom rail while an update is available."));
        }

        private static Toggle BuildToggle(string label, bool value, Action<bool> onChanged, string tooltip)
        {
            var toggle = new Toggle(label) { value = value, tooltip = tooltip };
            toggle.AddToClassList("molca-hub-about-toggle");
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return toggle;
        }

        /// <summary>Evaluates whatever the cache already holds, without touching the network.</summary>
        private FrameworkUpdateState FromCache() => FrameworkUpdateEvaluator.Evaluate(
            FrameworkVersionInfo.CoreVersion, FrameworkVersionInfo.UnityVersion,
            FrameworkVersionInfo.CoreSource, FrameworkUpdateCache.Cached);

        /// <summary>
        /// Checks on open only when it would cost a request the developer would have made anyway: a stale
        /// cache, the preference left on, and an interactive editor. CI must never dial out from a UI build.
        /// </summary>
        private void MaybeCheckOnOpen()
        {
            if (Application.isBatchMode) return;
            if (!FrameworkUpdatePreferences.CheckOnOpen) return;
            if (FrameworkUpdateCache.IsFresh(AddonChannels.Preferred)) return;
            Check(forceRefresh: false);
        }

        private async void Check(bool forceRefresh)
        {
            if (_busy) return;
            _busy = true;
            _checkButton?.SetEnabled(false);
            _updateCard.SetStatus(MolcaStatusKind.Idle, "Checking…");

            ResetCancellation();
            try
            {
                var result = await FrameworkUpdateCache.GetAsync(
                    _client, AddonChannels.Preferred, forceRefresh, _cancellation.Token);

                if (result.Success)
                {
                    Telemetry.MolcaEditorTelemetry.Track("editor.about.update_checked");
                    RenderUpdate(FrameworkUpdateEvaluator.Evaluate(
                        FrameworkVersionInfo.CoreVersion, FrameworkVersionInfo.UnityVersion,
                        FrameworkVersionInfo.CoreSource, result.Value));
                }
                else
                {
                    // Being offline, or not signed in, is a normal state for an editor — it is reported in the
                    // card and nowhere else. No console error, no dialog.
                    RenderUnavailable(result.Error);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                RenderUnavailable(exception.Message);
            }
            finally
            {
                _busy = false;
                _checkButton?.SetEnabled(true);
            }
        }

        private void RenderUnavailable(string message)
        {
            _updateCard.SetStatus(MolcaStatusKind.Idle, "Unavailable");
            _updateStatus.text = message;
            _updateBody.Clear();

            // The one actionable case: the check failed because nobody is signed in.
            if (!string.IsNullOrEmpty(message) && message.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
                _updateBody.Add(MolcaButtons.Toolbar("Open developer sign-in", DevLicenseWindow.Open));
        }

        private void RenderUpdate(FrameworkUpdateState state)
        {
            _state = state;
            _updateBody.Clear();

            var (status, statusText, summary) = Summarize(state);
            _updateCard.SetStatus(status, statusText);
            _updateStatus.text = summary;

            if (state.Eol)
            {
                var eol = new Label($"Core {Describe(state.InstalledVersion)} is past end of support" +
                                    (string.IsNullOrEmpty(state.MinSupportedVersion)
                                        ? "."
                                        : $" — {state.MinSupportedVersion} is the oldest supported release."));
                eol.AddToClassList("molca-hub-about-eol");
                _updateBody.Add(eol);
            }

            if (state.Target != null)
            {
                _updateBody.Add(BuildReleaseDetail(state.Target));
                _updateBody.Add(BuildOfferActions(state));
            }

            if (state.Blocked != null)
            {
                var blocked = new Label(
                    $"Core {state.Blocked.version} is also available but requires Unity " +
                    $"{Describe(state.Blocked.minUnity)} or newer; this editor is {FrameworkVersionInfo.UnityVersion}.");
                blocked.AddToClassList("molca-hub-muted");
                _updateBody.Add(blocked);
                if (!string.IsNullOrEmpty(state.Blocked.changelogUrl))
                    _updateBody.Add(new MolcaLinkRow(state.Blocked.changelogUrl,
                        $"Changelog for {state.Blocked.version}"));
            }
        }

        /// <summary>Maps a verdict to the card's status dot, its caption, and the sentence under it.</summary>
        private (MolcaStatusKind status, string statusText, string summary) Summarize(FrameworkUpdateState state)
        {
            string checkedAt = FrameworkUpdatePreferences.LastCheckedUtc is DateTime stamp
                ? $" Last checked {stamp.ToLocalTime():g}."
                : string.Empty;
            string channel = string.IsNullOrEmpty(state.Channel) ? string.Empty : $" on the {state.Channel} channel";

            return state.Verdict switch
            {
                FrameworkUpdateVerdict.UpToDate => (
                    state.Eol ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                    "Up to date",
                    $"Core {Describe(state.InstalledVersion)} is the newest release{channel}.{checkedAt}"),

                FrameworkUpdateVerdict.UpdateAvailable => (
                    MolcaStatusKind.Warning,
                    $"{state.Target.version} available",
                    $"Core {Describe(state.InstalledVersion)} → {state.Target.version}{channel}.{checkedAt}"),

                FrameworkUpdateVerdict.BlockedByUnity => (
                    MolcaStatusKind.Idle,
                    "Needs a newer Unity",
                    $"Core {state.Blocked.version} requires Unity {Describe(state.Blocked.minUnity)} or newer; " +
                    $"this editor is {FrameworkVersionInfo.UnityVersion}.{checkedAt}"),

                FrameworkUpdateVerdict.InstalledIsNewer => (
                    MolcaStatusKind.Idle,
                    "Ahead of the feed",
                    $"Core {Describe(state.InstalledVersion)} is newer than anything published{channel} — " +
                    $"a local or pre-release build.{checkedAt}"),

                _ => (MolcaStatusKind.Idle, "Not checked",
                    "The newest Core version has not been determined yet."),
            };
        }

        private VisualElement BuildReleaseDetail(FrameworkReleaseDto release)
        {
            var detail = new VisualElement();
            detail.AddToClassList("molca-hub-about-release");

            if (!string.IsNullOrEmpty(release.publishedAt) &&
                DateTime.TryParse(release.publishedAt, out var published))
                detail.Add(Muted($"Published {published.ToLocalTime():d}" +
                                 (string.IsNullOrEmpty(release.minUnity)
                                     ? string.Empty
                                     : $" · requires Unity {release.minUnity}+")));

            if (release.highlights != null)
                foreach (string highlight in release.highlights)
                {
                    if (string.IsNullOrWhiteSpace(highlight)) continue;
                    var line = new Label("•  " + highlight);
                    line.AddToClassList("molca-hub-about-highlight");
                    detail.Add(line);
                }

            if (!string.IsNullOrWhiteSpace(release.upgradeNotes))
            {
                var notes = new Label(release.upgradeNotes);
                notes.AddToClassList("molca-hub-about-notes");
                detail.Add(notes);
            }

            return detail;
        }

        private VisualElement BuildOfferActions(FrameworkUpdateState state)
        {
            var actions = new VisualElement();
            actions.AddToClassList("molca-hub-about-actions");

            // The install source decides the primary affordance. Only an install the Package Manager owns
            // can be mutated from here; every other source is told exactly what to do instead of being
            // given a dead button.
            switch (state.UpgradePath)
            {
                case FrameworkUpgradePath.PackageManager:
                case FrameworkUpgradePath.GitPackageManager:
                    actions.Add(MolcaButtons.Primary($"Update to {state.Target.version}", ApplyUpgrade));
                    actions.Add(MolcaButtons.Toolbar("Open Package Manager",
                        () => EditorApplication.ExecuteMenuItem("Window/Package Manager")));
                    break;

                case FrameworkUpgradePath.Manifest:
                    actions.Add(MolcaButtons.Primary("Copy manifest line", CopyInstruction));
                    break;

                case FrameworkUpgradePath.Embedded:
                    actions.Add(MolcaButtons.Toolbar("Copy upgrade spec", CopyInstruction));
                    break;
            }

            if (!string.IsNullOrEmpty(state.Target.changelogUrl))
                actions.Add(MolcaButtons.Toolbar("Changelog",
                    () => Application.OpenURL(state.Target.changelogUrl)));

            var path = new Label(FrameworkVersionInfo.DescribeUpgradePath(state.UpgradePath));
            path.AddToClassList("molca-hub-muted");

            var wrapper = new VisualElement();
            wrapper.Add(actions);
            wrapper.Add(path);
            return wrapper;
        }

        private void CopyInstruction()
        {
            string message = FrameworkUpgrader.CopyInstruction(_state.UpgradePath, _state.Target);
            _updateStatus.text = message;
        }

        private async void ApplyUpgrade()
        {
            if (_state?.Target == null || _busy) return;
            if (!FrameworkUpgrader.Confirm(_state.Target)) return;

            _busy = true;
            _updateCard.SetStatus(MolcaStatusKind.Idle, "Updating…");
            try
            {
                // Not keyed on this view's cancellation: the package resolve that follows tears the whole
                // domain down, taking the view with it, and abandoning it half-way would be worse than
                // letting it finish.
                string error = await FrameworkUpgrader.AddAsync(_state.Target.upgradeSpec);
                if (error == null)
                {
                    Telemetry.MolcaEditorTelemetry.Track("editor.about.update_applied");
                    FrameworkUpdateCache.Invalidate();
                    _updateCard.SetStatus(MolcaStatusKind.Ok, "Updated");
                    _updateStatus.text = $"Core {_state.Target.version} requested. Unity is resolving packages.";
                }
                else
                {
                    _updateCard.SetStatus(MolcaStatusKind.Error, "Update failed");
                    _updateStatus.text = error;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _updateCard.SetStatus(MolcaStatusKind.Error, "Update failed");
                _updateStatus.text = exception.Message;
            }
            finally { _busy = false; }
        }

        // -------------------------------------------------------------------
        // Versions
        // -------------------------------------------------------------------

        private void BuildVersionsCard()
        {
            var card = new MolcaSectionCard("Versions", "What this project is running");
            card.AddHeaderAction(MolcaButtons.Toolbar("Copy diagnostics", CopyDiagnostics));
            Add(card);

            foreach (var package in FrameworkVersionInfo.MolcaPackages())
                card.Body.Add(BuildInfoRow(package.Name,
                    $"{Describe(package.Version)}  ·  {FrameworkVersionInfo.DescribeSource(package.Source)}",
                    package.PackageId, "molca-hub-about-package-row"));

            if (FrameworkVersionInfo.MolcaPackages().Count == 0)
                card.Body.Add(Muted("No Molca packages could be resolved from the Package Manager."));

            card.Body.Add(BuildDivider());
            card.Body.Add(BuildInfoRow("Unity",
                $"{FrameworkVersionInfo.UnityVersion}  ·  {FrameworkVersionInfo.EditorRuntime}"));
            card.Body.Add(BuildInfoRow("Platform", Application.platform.ToString()));
            card.Body.Add(BuildInfoRow("Add-on catalog schema",
                AddonDistributionConfig.CatalogSchemaVersion.ToString()));
            card.Body.Add(BuildInfoRow("Update feed schema", FrameworkUpdateClient.SchemaVersion.ToString()));
            card.Body.Add(BuildInfoRow("Installed add-ons", InstalledAddonCountText()));
        }

        private void CopyDiagnostics()
        {
            EditorGUIUtility.systemCopyBuffer = FrameworkVersionInfo.DiagnosticsMarkdown(_state);
            Telemetry.MolcaEditorTelemetry.Track("editor.about.diagnostics_copied");
        }

        private static string InstalledAddonCountText()
        {
            // FindExisting, not GetOrCreate: opening About must never create a project asset as a side effect.
            var installed = InstalledAddonsAsset.FindExisting();
            int count = installed?.Addons?.Count ?? 0;
            return count == 1 ? "1 add-on" : $"{count} add-ons";
        }

        // -------------------------------------------------------------------
        // License
        // -------------------------------------------------------------------

        private void BuildLicenseCard()
        {
            var status = DevEntitlementVerifier.Evaluate(
                DevEntitlementStore.LoadEffective(), SystemInfo.deviceUniqueIdentifier, out var payload);

            var card = new MolcaSectionCard("License", "Developer entitlement on this machine",
                status == DevLicenseStatus.Valid ? MolcaStatusKind.Ok : MolcaStatusKind.Warning,
                DescribeLicense(status));
            card.AddHeaderAction(MolcaButtons.Toolbar("Manage", DevLicenseWindow.Open));
            Add(card);

            if (payload != null)
            {
                card.Body.Add(BuildInfoRow("Licensee", Describe(payload.licenseeId)));
                card.Body.Add(BuildInfoRow("Developer", Describe(payload.email)));
                card.Body.Add(BuildInfoRow("Expires", payload.ExpiresAtUtc.ToLocalTime().ToString("g")));
                card.Body.Add(BuildInfoRow("Activated on Core", Describe(payload.coreVersion)));
            }
            else
            {
                card.Body.Add(Muted("No developer entitlement is stored on this machine."));
            }
        }

        private static string DescribeLicense(DevLicenseStatus status) => status switch
        {
            DevLicenseStatus.Valid => "Active",
            DevLicenseStatus.Missing => "Not signed in",
            DevLicenseStatus.Expired => "Expired",
            DevLicenseStatus.WrongMachine => "Other machine",
            _ => "Invalid",
        };

        // -------------------------------------------------------------------
        // Links
        // -------------------------------------------------------------------

        private void BuildLinksCard()
        {
            var card = new MolcaSectionCard("Links");
            Add(card);

            var settings = MolcaEditorSettings.Instance;
            AddLinkRow(card, "Repository", settings != null ? settings.RepositoryUrl : null);
            AddLinkRow(card, "Documentation", settings != null ? settings.DocumentationUrl : null);

            var core = FrameworkVersionInfo.CorePackage();
            AddLinkRow(card, "Changelog", core?.changelogUrl);
            AddLinkRow(card, "Support", "mailto:dev@molca.id");

            card.Body.Add(BuildDivider());
            card.Body.Add(Muted($"Molca Framework · Core {Describe(FrameworkVersionInfo.CoreVersion)} · " +
                                "© Molca. Third-party notices ship with the package."));
        }

        private static void AddLinkRow(MolcaSectionCard card, string label, string url)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-about-link-row");
            row.Add(BuildFieldLabel(label));

            if (string.IsNullOrWhiteSpace(url)) row.Add(Muted("Not configured"));
            else
            {
                var link = new MolcaLinkRow(url, ShortUrl(url));
                link.AddToClassList("molca-hub-about-link");
                row.Add(link);
            }

            card.Body.Add(row);
        }

        // -------------------------------------------------------------------
        // Shared row helpers
        // -------------------------------------------------------------------

        private static VisualElement BuildInfoRow(
            string label,
            string value,
            string tooltip = null,
            string rowClass = null)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            if (!string.IsNullOrEmpty(rowClass)) row.AddToClassList(rowClass);
            row.Add(BuildFieldLabel(label));

            var text = new Label(value) { tooltip = tooltip ?? string.Empty };
            text.AddToClassList("molca-hub-about-value");
            row.Add(text);
            return row;
        }

        private static Label BuildFieldLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-field-label");
            return label;
        }

        private static Label Muted(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-muted");
            return label;
        }

        private static VisualElement BuildDivider()
        {
            var divider = new VisualElement();
            divider.AddToClassList("molca-hub-divider");
            return divider;
        }

        private static string Describe(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

        private static string ShortUrl(string url)
        {
            string display = url
                .Replace("https://", string.Empty)
                .Replace("http://", string.Empty)
                .Replace("mailto:", string.Empty)
                .TrimEnd('/');

            if (display.Length <= MaxLinkDisplayLength) return display;

            const int tailLength = 16;
            int headLength = MaxLinkDisplayLength - tailLength - 1;
            return $"{display.Substring(0, headLength)}…{display.Substring(display.Length - tailLength)}";
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            // UI Toolkit USS has no media queries. Toggle one root class instead so long package ids and
            // links stack cleanly when the Hub is docked into a narrow pane.
            EnableInClassList("molca-hub-about-section--compact", evt.newRect.width < CompactLayoutWidth);
        }

        private void ResetCancellation()
        {
            CancelOutstanding();
            _cancellation = new CancellationTokenSource();
        }

        private void CancelOutstanding()
        {
            if (_cancellation == null) return;
            _cancellation.Cancel();
            _cancellation.Dispose();
            _cancellation = null;
        }
    }
}
