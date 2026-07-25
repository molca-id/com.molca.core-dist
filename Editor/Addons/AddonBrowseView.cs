using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.UI.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// The Add-ons &gt; Browse view: search and install entitled, Core-compatible add-on packs from the catalog.
    /// Cards reflect install state (not installed / installed / update available).
    /// </summary>
    internal sealed class AddonBrowseView : AddonViewBase
    {
        private AddonCatalogResponse _catalog;

        /// <summary>Per-pack version chosen in the card dropdown; absent means "use the newest".</summary>
        private readonly Dictionary<string, string> _selected = new Dictionary<string, string>();

        internal AddonBrowseView() : base(
            "Browse add-ons",
            "Signed capability packs entitled to this license and compatible with this Core. Installed at edit time; runtime packs require a player rebuild.",
            showImportAction: false)
        {
        }

        protected override async Awaitable FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            var result = await AddonCatalogCache.GetAsync(Client, Channel, forceRefresh, cancellationToken);
            _catalog = result.Success ? result.Value : null;
            if (!result.Success) Status.text = result.Error;
            else UpdateChannelSelector(_catalog.maxChannel);
        }

        protected override void RenderCards(string filter)
        {
            List.Clear();
            if (_catalog == null) return; // FetchAsync already set the error/sign-in status.

            var packs = _catalog.packs ?? Array.Empty<AddonCatalogPackage>();
            var ledger = InstalledAddonsAsset.FindExisting();
            int shown = 0;

            foreach (var pack in packs.OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase))
            {
                if (!Matches(pack, filter)) continue;
                List.Add(BuildBrowseCard(pack, ledger?.Find(pack.id)));
                shown++;
            }

            if (shown == 0)
            {
                Status.text = string.IsNullOrEmpty(filter)
                    ? $"Licensed as {_catalog.licenseeId}. No compatible add-ons are currently entitled."
                    : "No add-ons match your search.";
            }
            else
            {
                Status.text = string.IsNullOrEmpty(filter)
                    ? $"Licensed as {_catalog.licenseeId}. {shown} add-on{(shown == 1 ? string.Empty : "s")} available."
                    : $"{shown} add-on{(shown == 1 ? string.Empty : "s")} match your search.";
            }
        }

        private VisualElement BuildBrowseCard(AddonCatalogPackage pack, InstalledAddonRecord installed)
        {
            // The server returns every compatible version newest-first. The card acts on the newest by
            // default and exposes the rest, so pinning or rolling back does not require a support ticket.
            AddonCatalogVersion latest = pack.Latest;
            AddonCatalogVersion selected = _selected.TryGetValue(pack.id, out string pinned)
                ? pack.versions.FirstOrDefault(v => v.version == pinned) ?? latest
                : latest;

            var model = new AddonCardModel
            {
                Id = pack.id,
                Name = pack.name,
                Publisher = pack.publisher,
                Description = pack.description,
                HasRuntime = selected?.hasRuntime ?? false,
                Icon = installed != null ? LoadPackageIcon(pack.id) : null,
                VersionText = selected != null ? $"v{selected.version}" : "no compatible version",
            };

            bool updateAvailable = false;
            if (installed != null)
            {
                if (latest != null && installed.version != latest.version)
                {
                    updateAvailable = true;
                    model.Status = MolcaStatusKind.Warning;
                    model.StatusText = "Update available";
                    model.VersionText = $"v{installed.version} → {latest.version}";
                }
                else
                {
                    model.Status = MolcaStatusKind.Ok;
                    model.StatusText = "Installed";
                    model.VersionText = $"v{installed.version}";
                }
            }

            if (selected != null)
            {
                model.Details.Add(("Compatibility", $"Core {selected.coreVersionRange}  ·  runtime {selected.runtime}"));
                if (!string.IsNullOrEmpty(selected.unityVersion))
                    model.Details.Add(("Unity", selected.unityVersion));
                model.Details.Add(("Channel", selected.channel));
                model.Details.Add(("Published", FormatDate(selected.publishedAt)));
                model.Details.Add(("Download size", FormatBytes(selected.sizeBytes)));
                model.Details.Add(("SHA-256", selected.sha256));
                if (!string.IsNullOrWhiteSpace(selected.releaseNotes))
                    model.Details.Add(("Release notes", Truncate(selected.releaseNotes.Replace("\n", " "), 400)));
            }

            var buttons = new List<Button>();
            bool sameAsInstalled = installed != null && selected != null && installed.version == selected.version;
            if (selected != null && !sameAsInstalled)
            {
                string label = installed == null ? "Install"
                    : AddonSemVer.Compare(selected.version, installed.version) < 0 ? "Downgrade" : "Update";
                buttons.Add(MolcaButtons.Mini(label, () => BeginInstall(pack, selected)));
            }

            VisualElement card = BuildCard(model, buttons.ToArray());
            if (pack.versions != null && pack.versions.Length > 1) AddVersionPicker(card, pack, selected);
            return card;
        }

        /// <summary>Adds a version dropdown to a card that has more than one compatible version.</summary>
        private void AddVersionPicker(VisualElement card, AddonCatalogPackage pack, AddonCatalogVersion selected)
        {
            var choices = pack.versions.Select(v => v.version).ToList();
            var dropdown = new DropdownField("Version", choices, Math.Max(0, choices.IndexOf(selected?.version)));
            dropdown.AddToClassList("molca-addons-version-picker");
            dropdown.RegisterValueChangedCallback(changed =>
            {
                _selected[pack.id] = changed.newValue;
                RenderCards(Search.Value);
            });
            card.Q<VisualElement>(className: "molca-addons-card")?.Add(dropdown);
        }

        private static string FormatDate(string isoUtc) =>
            DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd")
                : "unknown";

        private static bool Matches(AddonCatalogPackage pack, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return Contains(pack.name, filter) || Contains(pack.id, filter)
                || Contains(pack.publisher, filter) || Contains(pack.description, filter);
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
