using System;
using System.IO;
using System.Text;
using Molca.Editor.Licensing;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Load-time guardrail for manager-installed add-ons. On editor load it warns when a valid Molca
    /// developer license is missing (add-ons are ungated but require a licensed Core) and when an installed
    /// add-on's <c>.cs</c> source has drifted from the signed content it was installed from.
    /// </summary>
    /// <remarks>
    /// Advisory only: it never blocks the editor or unloads code. Installed packages are already compiled
    /// into the domain, and licensing here is access-control + evidence, not DRM (a developer with source can
    /// change client-side enforcement). Enforcement of "no access when unlicensed" is applied where it is
    /// actionable — the Hub Add-ons management surface (see <see cref="AddonViewBase"/>) — not by trying
    /// to claw back already-compiled code.
    /// </remarks>
    [InitializeOnLoad]
    internal static class AddonIntegrityCheck
    {
        static AddonIntegrityCheck() => EditorApplication.delayCall += Run;

        private static void Run()
        {
            InstalledAddonsAsset ledger = InstalledAddonsAsset.FindExisting();
            if (ledger == null || ledger.Addons.Count == 0) return;

            WarnIfUnlicensed(ledger);
            WarnOnSourceDrift(ledger);
        }

        private static void WarnIfUnlicensed(InstalledAddonsAsset ledger)
        {
            DevLicenseStatus status = DevEntitlementVerifier.Evaluate(
                DevEntitlementStore.LoadEffective(), SystemInfo.deviceUniqueIdentifier, out _);
            if (status == DevLicenseStatus.Valid) return;

            var installed = new StringBuilder();
            foreach (InstalledAddonRecord record in ledger.Addons)
                installed.Append("\n  • ").Append(record.name).Append(' ').Append(record.version);

            Debug.LogWarning(
                $"[Molca Add-ons] Your Molca developer license is {Describe(status)}. Installed add-ons " +
                "require a valid license, and add-on management in the Hub is unavailable until you sign in " +
                "(Molca > License > Developer Sign-In)." + installed);
        }

        private static void WarnOnSourceDrift(InstalledAddonsAsset ledger)
        {
            string packagesRoot = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? ".", "Packages");

            foreach (InstalledAddonRecord record in ledger.Addons)
            {
                string dir = Path.Combine(packagesRoot, record.id);
                if (!Directory.Exists(dir))
                {
                    Debug.LogWarning($"[Molca Add-ons] '{record.name} {record.version}' is recorded as " +
                                     $"installed but its files are missing from Packages/{record.id}.");
                    continue;
                }
                // Records written before contentHash existed can't be checked; skip rather than false-warn.
                if (string.IsNullOrEmpty(record.contentHash)) continue;
                if (!string.Equals(AddonInstaller.ComputeSourceHash(dir), record.contentHash, StringComparison.Ordinal))
                    Debug.LogWarning($"[Molca Add-ons] '{record.name} {record.version}' source in " +
                                     $"Packages/{record.id} has been modified since install — it no longer " +
                                     "matches the signed content it was installed from.");
            }
        }

        private static string Describe(DevLicenseStatus status) => status switch
        {
            DevLicenseStatus.Missing => "not signed in",
            DevLicenseStatus.Expired => "expired",
            DevLicenseStatus.WrongMachine => "issued for a different machine",
            _ => "invalid",
        };
    }
}
