using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Addons
{
    [Serializable]
    internal sealed class PendingAddonSelection
    {
        public string id;
        public string version;
    }

    [Serializable]
    internal sealed class PendingAddonTransaction
    {
        public string transactionId;
        public string rootId;
        public string rootVersion;
        public string channel;
        public string status;
        public string error;
        public int attempts;
        public PendingAddonSelection[] selected;
    }

    /// <summary>
    /// Resumes a confirmed add-on plan after Unity Package Manager triggers a domain reload while resolving
    /// an external prerequisite. The record contains only package identities and versions, never credentials,
    /// entitlement tokens, signed URLs, or local source paths.
    /// </summary>
    [InitializeOnLoad]
    internal static class AddonTransactionResume
    {
        private const int MaxAttempts = 12;
        private static bool _resuming;

        static AddonTransactionResume() => EditorApplication.delayCall += ResumeIfPending;

        internal static void Save(AddonInstallPlan plan, string channel)
        {
            var pending = new PendingAddonTransaction
            {
                transactionId = Guid.NewGuid().ToString("N"),
                rootId = plan.RootId,
                rootVersion = plan.RootVersion,
                channel = channel,
                status = "pending",
                selected = plan.Ordered.Select(entry => new PendingAddonSelection
                {
                    id = entry.Package.id,
                    version = entry.Version.version,
                }).ToArray(),
            };
            Write(pending);
        }

        internal static void Complete()
        {
            try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Add-ons] Could not clear completed transaction: {exception.Message}");
            }
        }

        internal static void Fail(string error)
        {
            PendingAddonTransaction pending = Read();
            if (pending == null) return;
            pending.status = "failed";
            pending.error = error;
            Write(pending);
        }

        private static async void ResumeIfPending()
        {
            if (_resuming) return;
            PendingAddonTransaction pending = Read();
            if (pending == null || pending.status != "pending") return;
            if (++pending.attempts > MaxAttempts)
            {
                pending.status = "failed";
                pending.error = "Automatic resume exceeded the retry limit.";
                Write(pending);
                Debug.LogError("[Molca Add-ons] Pending dependency transaction requires manual review at " +
                               PendingPath);
                return;
            }
            Write(pending);
            _resuming = true;
            try
            {
                var client = new AddonCatalogClient();
                var catalogResult = await client.GetCatalogAsync(
                    pending.channel, CancellationToken.None);
                if (!catalogResult.Success) throw new InvalidOperationException(catalogResult.Error);
                AddonCatalogPackage root = catalogResult.Value.packs?.FirstOrDefault(
                    pack => pack.id == pending.rootId);
                AddonCatalogVersion version = root?.versions?.FirstOrDefault(
                    item => item.version == pending.rootVersion);
                if (root == null || version == null)
                    throw new InvalidOperationException("Confirmed root is no longer visible.");
                if (!AddonDependencyResolver.TryResolve(catalogResult.Value, root.id, version.version,
                    InstalledAddonsAsset.FindExisting(), out AddonInstallPlan plan, out string resolveError))
                    throw new InvalidOperationException(resolveError);
                string[] actual = plan.Ordered.Select(
                    entry => $"{entry.Package.id}@{entry.Version.version}").ToArray();
                string[] confirmed = (pending.selected ?? Array.Empty<PendingAddonSelection>())
                    .Select(item => $"{item.id}@{item.version}").ToArray();
                if (!actual.SequenceEqual(confirmed))
                    throw new InvalidOperationException(
                        "The dependency plan changed after confirmation; review it again.");

                var manifests = new System.Collections.Generic.List<VerifiedAddonManifest>();
                foreach (AddonInstallPlanEntry entry in plan.Ordered)
                {
                    var manifest = await client.GetManifestAsync(
                        entry.Package.id, entry.Version.version, CancellationToken.None);
                    if (!manifest.Success) throw new InvalidOperationException(manifest.Error);
                    if (!AddonDependencyResolver.ManifestMatches(
                        entry.Version, manifest.Value.Manifest, out string metadataError))
                        throw new InvalidOperationException(metadataError);
                    manifests.Add(manifest.Value);
                }
                var prerequisites = await ExternalPrerequisiteResolver.EnsureAsync(
                    plan.ExternalPrerequisites, CancellationToken.None);
                if (!prerequisites.Success)
                    throw new InvalidOperationException(prerequisites.Error);
                AddonInstallResult result = await new AddonInstaller().InstallGraphAsync(
                    plan, manifests, CancellationToken.None);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                Complete();
                Debug.Log($"[Molca Add-ons] Resumed and completed {pending.rootId} {pending.rootVersion}.");
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
                Debug.LogError($"[Molca Add-ons] Pending dependency transaction stopped: {exception.Message}. " +
                               $"Review {PendingPath}.");
            }
            finally { _resuming = false; }
        }

        private static PendingAddonTransaction Read()
        {
            try
            {
                return File.Exists(PendingPath)
                    ? JsonUtility.FromJson<PendingAddonTransaction>(File.ReadAllText(PendingPath))
                    : null;
            }
            catch { return null; }
        }

        private static void Write(PendingAddonTransaction pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath) ?? ProjectRoot);
            File.WriteAllText(PendingPath, JsonUtility.ToJson(pending, true));
        }

        private static string PendingPath => Path.Combine(
            ProjectRoot, "Library", "Molca", "Addons", "Transactions", "pending.json");

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the Unity project root.");
    }
}
