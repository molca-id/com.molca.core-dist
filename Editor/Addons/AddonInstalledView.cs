using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Molca.Editor.UI.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// The Add-ons &gt; Installed view: manage add-ons installed into this project — updates, removal, source
    /// integrity, and signed offline-bundle import. Cross-references the catalog to surface available updates
    /// and orphaned installs (present locally but no longer entitled/compatible).
    /// </summary>
    internal sealed class AddonInstalledView : AddonViewBase
    {
        // Catalog is best-effort here: the ledger still renders (with no update info) if it can't be fetched.
        private AddonCatalogResponse _catalog;

        internal AddonInstalledView() : base(
            "Installed add-ons",
            "Add-ons installed into this project. Update, remove, check integrity, or import a signed offline bundle.",
            showImportAction: true)
        {
        }

        protected override async Awaitable FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            var result = await AddonCatalogCache.GetAsync(Client, Channel, forceRefresh, cancellationToken);
            _catalog = result.Success ? result.Value : null;
            if (result.Success) UpdateChannelSelector(_catalog.maxChannel);
        }

        protected override void RenderCards(string filter)
        {
            List.Clear();
            var ledger = InstalledAddonsAsset.FindExisting();
            IReadOnlyList<InstalledAddonRecord> records =
                ledger != null ? ledger.Addons : Array.Empty<InstalledAddonRecord>();

            if (records.Count == 0)
            {
                Status.text = "No add-ons are installed. Use Browse to install entitled packs, or import a signed bundle.";
                return;
            }

            int shown = 0;
            foreach (var record in records.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            {
                if (!Matches(record, filter)) continue;
                List.Add(BuildInstalledCard(record));
                shown++;
            }

            Status.text = shown == 0
                ? "No installed add-ons match your search."
                : $"{shown} installed add-on{(shown == 1 ? string.Empty : "s")}.";
        }

        private VisualElement BuildInstalledCard(InstalledAddonRecord record)
        {
            AddonCatalogPackage pack = _catalog?.packs?.FirstOrDefault(p => p.id == record.id);
            AddonCatalogVersion latest = pack?.Latest;

            string packageDir = Path.GetFullPath($"Packages/{record.id}");
            bool missing = !Directory.Exists(packageDir);
            bool drifted = !missing && !string.IsNullOrEmpty(record.contentHash)
                && !string.Equals(AddonInstaller.ComputeSourceHash(packageDir), record.contentHash, StringComparison.Ordinal);
            bool updateAvailable = latest != null && latest.version != record.version;

            var model = new AddonCardModel
            {
                Id = record.id,
                Name = record.name,
                Publisher = record.publisher,
                Description = pack?.description,
                HasRuntime = record.hasRuntime,
                Icon = LoadPackageIcon(record.id),
                VersionText = updateAvailable ? $"v{record.version} → {latest.version}" : $"v{record.version}",
            };

            if (missing)
            {
                model.Status = MolcaStatusKind.Error;
                model.StatusText = "Files missing";
            }
            else if (drifted)
            {
                model.Status = MolcaStatusKind.Error;
                model.StatusText = "Modified since install";
            }
            else if (updateAvailable)
            {
                model.Status = MolcaStatusKind.Warning;
                model.StatusText = "Update available";
            }
            else if (pack == null)
            {
                model.Status = MolcaStatusKind.Idle;
                model.StatusText = "Not in catalog";
            }
            else
            {
                model.Status = MolcaStatusKind.Ok;
                model.StatusText = "Up to date";
            }

            model.Details.Add(("Source", record.sourceHost));
            model.Details.Add(("Signing key", record.signingKeyId));
            model.Details.Add(("SHA-256", record.sha256));
            model.Details.Add(("Installed", record.installedAtUtc));
            model.Details.Add(("Installed as", record.requested ? "Requested root" :
                $"Dependency of {string.Join(", ", record.requiredBy ?? new List<string>())}"));
            if (record.dependencies?.Count > 0)
                model.Details.Add(("Dependencies", string.Join(", ",
                    record.dependencies.Select(item => $"{item.id} {item.resolvedVersion}"))));
            if (latest != null)
            {
                model.Details.Add(("Compatibility", $"Core {latest.coreVersionRange}  ·  runtime {latest.runtime}"));
                if (updateAvailable && !string.IsNullOrWhiteSpace(latest.releaseNotes))
                    model.Details.Add(($"What's new in {latest.version}", Truncate(latest.releaseNotes.Replace("\n", " "), 400)));
            }
            model.Details.Add(("Integrity", missing ? "files missing"
                : drifted ? "source modified since install"
                : string.IsNullOrEmpty(record.contentHash) ? "not tracked (installed before integrity checks)"
                : "matches signed content"));

            var buttons = new List<Button>();
            bool canManage = _catalog?.canManagePolicy == true;
            if (canManage && updateAvailable && pack != null)
                buttons.Add(MolcaButtons.Mini("Update", () => BeginInstall(_catalog, pack, latest)));
            if (canManage) buttons.Add(MolcaButtons.Mini("Remove", () => Remove(record)));

            return BuildCard(model, buttons.ToArray());
        }

        private static bool Matches(InstalledAddonRecord record, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return Contains(record.name, filter) || Contains(record.id, filter) || Contains(record.publisher, filter);
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
