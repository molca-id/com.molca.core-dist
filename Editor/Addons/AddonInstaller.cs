using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Downloads, hash-verifies, safely extracts, validates, and transactionally activates signed UPM add-ons.
    /// Existing packages are never overwritten unless the ownership ledger says this manager installed them.
    /// </summary>
    internal sealed class AddonInstaller
    {
        private static readonly Regex PackageIdPattern =
            new Regex("^[a-z0-9][a-z0-9._-]{1,127}$", RegexOptions.CultureInvariant);

        private readonly IAddonActivator _activator;

        internal AddonInstaller(IAddonActivator activator = null) =>
            _activator = activator ?? new DomainReloadAddonActivator();

        /// <summary>Installs or updates a previously verified manifest.</summary>
        internal async Awaitable<AddonInstallResult> InstallAsync(
            VerifiedAddonManifest verified, CancellationToken cancellationToken = default)
        {
            if (verified?.Manifest == null) return AddonInstallResult.Fail("A verified manifest is required.");
            AddonManifest manifest = verified.Manifest;
            if (!PackageIdPattern.IsMatch(manifest.id ?? string.Empty))
                return AddonInstallResult.Fail("Manifest package id is invalid.");

            string projectRoot = ProjectRoot();
            string workRoot = Path.Combine(projectRoot, "Library", "Molca", "Addons");
            string downloadDirectory = Path.Combine(workRoot, "Downloads");
            string stagingDirectory = Path.Combine(workRoot, "Staging", manifest.id + "-" + Guid.NewGuid().ToString("N"));
            string archivePath = Path.Combine(downloadDirectory, manifest.id + "-" + manifest.version + "-" + Guid.NewGuid().ToString("N") + ".tgz");
            Directory.CreateDirectory(downloadDirectory);

            Uri downloadUri = new Uri(manifest.downloadUrl);
            try
            {
                var download = await DownloadAsync(downloadUri, archivePath, cancellationToken);
                if (!download.Success)
                {
                    AddonAuditLog.Record("install", "failed", manifest.id, manifest.version,
                        manifest.sha256, downloadUri.Host, download.Error);
                    return AddonInstallResult.Fail(download.Error);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(archivePath);
                if (!fileInfo.Exists || fileInfo.Length != manifest.sizeBytes)
                    return VerificationFailure(manifest, downloadUri.Host,
                        $"Artifact size mismatch (expected {manifest.sizeBytes}, received {(fileInfo.Exists ? fileInfo.Length : 0)})." );

                string actualHash = Sha256(archivePath);
                if (!string.Equals(actualHash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                    return VerificationFailure(manifest, downloadUri.Host,
                        $"Artifact SHA-256 mismatch (expected {manifest.sha256}, received {actualHash}).");

                AddonTarGzExtractor.Extract(archivePath, stagingDirectory, cancellationToken);
                if (!AddonPackageValidator.TryValidate(stagingDirectory, manifest, out string packageError))
                    return VerificationFailure(manifest, downloadUri.Host, packageError);

                return CommitInstall(stagingDirectory, verified, downloadUri.Host);
            }
            catch (OperationCanceledException)
            {
                AddonAuditLog.Record("install", "canceled", manifest.id, manifest.version,
                    manifest.sha256, downloadUri.Host);
                throw;
            }
            catch (Exception exception)
            {
                AddonAuditLog.Record("install", "failed", manifest.id, manifest.version,
                    manifest.sha256, downloadUri.Host, exception.Message);
                return AddonInstallResult.Fail($"Installation failed: {exception.Message}");
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }

        /// <summary>Installs an administrator-exported tarball using an independently signed offline manifest.</summary>
        internal AddonInstallResult InstallOffline(VerifiedAddonManifest verified, string archivePath)
        {
            if (verified?.Manifest == null || !verified.Manifest.offline)
                return AddonInstallResult.Fail("A verified offline manifest is required.");
            AddonManifest manifest = verified.Manifest;
            if (!PackageIdPattern.IsMatch(manifest.id ?? string.Empty) || !File.Exists(archivePath))
                return AddonInstallResult.Fail("Offline artifact or package id is invalid.");

            string stagingDirectory = Path.Combine(ProjectRoot(), "Library", "Molca", "Addons", "Staging",
                manifest.id + "-offline-" + Guid.NewGuid().ToString("N"));
            try
            {
                var fileInfo = new FileInfo(archivePath);
                if (fileInfo.Length != manifest.sizeBytes)
                    return VerificationFailure(manifest, "offline", $"Artifact size mismatch (expected {manifest.sizeBytes}, received {fileInfo.Length}).");
                string actualHash = Sha256(archivePath);
                if (!string.Equals(actualHash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                    return VerificationFailure(manifest, "offline", $"Artifact SHA-256 mismatch (expected {manifest.sha256}, received {actualHash}).");
                AddonTarGzExtractor.Extract(archivePath, stagingDirectory, CancellationToken.None);
                if (!AddonPackageValidator.TryValidate(stagingDirectory, manifest, out string packageError))
                    return VerificationFailure(manifest, "offline", packageError);
                return CommitInstall(stagingDirectory, verified, "offline");
            }
            catch (Exception exception)
            {
                AddonAuditLog.Record("install", "failed", manifest.id, manifest.version,
                    manifest.sha256, "offline", exception.Message);
                return AddonInstallResult.Fail($"Offline installation failed: {exception.Message}");
            }
            finally { TryDeleteDirectory(stagingDirectory); }
        }

        /// <summary>
        /// Removes a manager-owned embedded package by moving it to a recoverable Library trash directory.
        /// Packages absent from the ownership ledger are always refused.
        /// </summary>
        internal AddonInstallResult Remove(string id)
        {
            var state = InstalledAddonsAsset.FindExisting();
            InstalledAddonRecord record = state?.Find(id);
            if (record == null) return AddonInstallResult.Fail("The Add-on Manager does not own this package; removal was refused.");

            string target = PackagePath(id);
            if (!Directory.Exists(target)) return AddonInstallResult.Fail("The managed package directory is already missing.");
            if (!TryReadPackageIdentity(target, out string packageId, out _, out string packageError) || packageId != id)
                return AddonInstallResult.Fail($"Managed package identity check failed: {packageError ?? packageId}");

            string recovery = UniqueRecoveryPath(id, "Removed");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(recovery) ?? ProjectRoot());
                Directory.Move(target, recovery);
                state.Remove(id);
                AddonAuditLog.Record("remove", "executed", id, record.version, record.sha256, record.sourceHost);
                AddonTelemetry.Record("remove", id, record.version);
                _activator.Activate();
                return AddonInstallResult.Ok($"Removed {id} {record.version}.", recovery);
            }
            catch (Exception exception)
            {
                if (!Directory.Exists(target) && Directory.Exists(recovery))
                {
                    try { Directory.Move(recovery, target); }
                    catch { /* Recovery path is returned in the error and remains intact. */ }
                }
                state.Upsert(record);
                AddonAuditLog.Record("remove", "failed", id, record.version, record.sha256, record.sourceHost, exception.Message);
                return AddonInstallResult.Fail($"Removal failed: {exception.Message}");
            }
        }

        private AddonInstallResult CommitInstall(string stagingDirectory, VerifiedAddonManifest verified, string sourceHost)
        {
            AddonManifest manifest = verified.Manifest;
            string target = PackagePath(manifest.id);
            var state = InstalledAddonsAsset.GetOrCreate();
            InstalledAddonRecord previous = state.Find(manifest.id);
            string backup = null;
            bool installedNewDirectory = false;
            string action = previous == null ? "install" : "update";

            if (Directory.Exists(target))
            {
                if (previous == null)
                    return AddonInstallResult.Fail($"Packages/{manifest.id} already exists and is not owned by the Add-on Manager.");
                if (!TryReadPackageIdentity(target, out string existingId, out _, out string existingError) || existingId != manifest.id)
                    return AddonInstallResult.Fail($"Existing managed package identity check failed: {existingError ?? existingId}");
                backup = UniqueRecoveryPath(manifest.id, "Updated");
            }

            try
            {
                if (backup != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup) ?? ProjectRoot());
                    Directory.Move(target, backup);
                }
                Directory.Move(stagingDirectory, target);
                installedNewDirectory = true;

                state.Upsert(new InstalledAddonRecord
                {
                    id = manifest.id,
                    name = manifest.name,
                    version = manifest.version,
                    sha256 = manifest.sha256.ToLowerInvariant(),
                    publisher = manifest.publisher,
                    sourceHost = sourceHost,
                    signingKeyId = verified.KeyId,
                    installedAtUtc = DateTime.UtcNow.ToString("o"),
                    hasRuntime = manifest.hasRuntime,
                    contentHash = ComputeSourceHash(target),
                });
                AddonAuditLog.Record(action, "executed", manifest.id, manifest.version,
                    manifest.sha256, sourceHost);
                if (!verified.Manifest.offline) AddonTelemetry.Record(action, manifest.id, manifest.version);
                _activator.Activate();
                return AddonInstallResult.Ok(
                    $"{(action == "install" ? "Installed" : "Updated")} {manifest.id} {manifest.version}.", backup);
            }
            catch (Exception exception)
            {
                if (installedNewDirectory) TryDeleteDirectory(target);
                if (backup != null && Directory.Exists(backup) && !Directory.Exists(target))
                {
                    try { Directory.Move(backup, target); }
                    catch { /* Keep the recoverable backup when automatic rollback cannot move it. */ }
                }
                if (previous == null) state.Remove(manifest.id);
                else state.Upsert(previous);
                AddonAuditLog.Record(action, "failed", manifest.id, manifest.version,
                    manifest.sha256, sourceHost, exception.Message);
                return AddonInstallResult.Fail($"Could not activate add-on: {exception.Message}");
            }
        }

        private static async Awaitable<AddonOperationResult<bool>> DownloadAsync(
            Uri uri, string destination, CancellationToken cancellationToken)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return AddonOperationResult<bool>.Fail("Download URL is not on the pinned HTTPS host allowlist.");

            using var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbGET);
            request.downloadHandler = new DownloadHandlerFile(destination) { removeFileOnAbort = true };
            request.timeout = 120;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((long)request.downloadedBytes > AddonDistributionConfig.MaxDownloadBytes)
                {
                    request.Abort();
                    return AddonOperationResult<bool>.Fail("Download exceeded the maximum add-on size policy.");
                }
                await Awaitable.NextFrameAsync(cancellationToken);
            }
            return request.result == UnityWebRequest.Result.Success
                ? AddonOperationResult<bool>.Ok(true)
                : AddonOperationResult<bool>.Fail($"Download failed (HTTP {request.responseCode}): {request.error}");
        }

        private static AddonInstallResult VerificationFailure(AddonManifest manifest, string host, string error)
        {
            AddonAuditLog.Record("verify", "failed", manifest.id, manifest.version, manifest.sha256, host, error);
            return AddonInstallResult.Fail(error);
        }

        private static string Sha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>
        /// Deterministic SHA-256 over a package's <c>.cs</c> files (each contributing its package-relative
        /// path then its bytes, files ordered by full path). Only <c>.cs</c> is hashed: it is the code that
        /// runs with full Editor privileges, and the content Unity never rewrites — Unity reformats
        /// <c>.asmdef</c>/<c>.json</c> and generates <c>.meta</c> on import, so hashing those would produce
        /// false drift positives. Used to record install-time content and re-check it on load.
        /// </summary>
        internal static string ComputeSourceHash(string packageDir)
        {
            if (!Directory.Exists(packageDir)) return string.Empty;
            // All entries share the packageDir prefix, so ordering by full path is identical to ordering by
            // relative path — deterministic across machines without a separate projection.
            string[] files = Directory.GetFiles(packageDir, "*.cs", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);

            using var sha = SHA256.Create();
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
                byte[] pathBytes = Encoding.UTF8.GetBytes(relative + "\n");
                sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
                byte[] content = File.ReadAllBytes(file);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool TryReadPackageIdentity(string directory, out string id, out string version, out string error)
        {
            id = null;
            version = null;
            error = null;
            string path = Path.Combine(directory, "package.json");
            if (!File.Exists(path)) { error = "Extracted add-on has no package.json at its root."; return false; }
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                id = (string)json["name"];
                version = (string)json["version"];
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                { error = "package.json must contain name and version."; return false; }
                return true;
            }
            catch (Exception exception)
            { error = $"Could not parse package.json: {exception.Message}"; return false; }
        }

        private static string PackagePath(string id) => Path.Combine(ProjectRoot(), "Packages", id);

        private static string UniqueRecoveryPath(string id, string action) =>
            Path.Combine(ProjectRoot(), "Library", "Molca", "Addons", "Recovery",
                $"{id}-{action}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the Unity project root.");

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* Best-effort cleanup under Library only. */ }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { /* Best-effort cleanup of manager-owned staging/rollback directories only. */ }
        }
    }
}
