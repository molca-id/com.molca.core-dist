using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Resolves reviewed external Unity packages before any Molca package is activated. External packages
    /// remain project-owned and are never removed automatically with a Molca add-on.
    /// </summary>
    internal static class ExternalPrerequisiteResolver
    {
        internal static async Awaitable<AddonOperationResult<bool>> EnsureAsync(
            IReadOnlyList<ExternalAddonPrerequisite> prerequisites,
            CancellationToken cancellationToken)
        {
            if (prerequisites == null || prerequisites.Count == 0)
                return AddonOperationResult<bool>.Ok(true);

            ListRequest list = Client.List(true, false);
            while (!list.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }
            if (list.Status != StatusCode.Success)
                return AddonOperationResult<bool>.Fail(
                    $"Could not inspect Unity packages: {list.Error?.message}");
            var installed = list.Result.ToDictionary(
                package => package.name, package => package, StringComparer.Ordinal);

            foreach (ExternalAddonPrerequisite prerequisite in prerequisites)
            {
                if (installed.TryGetValue(prerequisite.packageId, out var resolved))
                {
                    if (prerequisite.source == "git" &&
                        !string.Equals(resolved.git?.revision, prerequisite.resolvedCommit,
                            StringComparison.OrdinalIgnoreCase))
                        return AddonOperationResult<bool>.Fail(
                            $"external_prerequisite_version_conflict: {prerequisite.packageId} " +
                            "is installed from another commit.");
                    continue;
                }
                if (prerequisite.source is "builtin" or "local")
                    return AddonOperationResult<bool>.Fail(
                        $"external_prerequisite_missing: {prerequisite.packageId} must already exist.");

                string addSpec;
                if (prerequisite.source == "git")
                {
                    if (!Uri.TryCreate(prerequisite.spec?.Split('#')[0], UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
                        string.IsNullOrWhiteSpace(prerequisite.resolvedCommit))
                        return AddonOperationResult<bool>.Fail(
                            $"External Git prerequisite '{prerequisite.packageId}' is not safely pinned.");
                    // Install the administrator-reviewed immutable commit, never the authored branch/tag
                    // fragment. Verification below is defense in depth after Package Manager resolves it.
                    addSpec = prerequisite.spec.Split('#')[0] + "#" + prerequisite.resolvedCommit;
                }
                else if (prerequisite.source == "registry")
                {
                    addSpec = $"{prerequisite.packageId}@{prerequisite.spec}";
                }
                else
                {
                    return AddonOperationResult<bool>.Fail(
                        $"Unsupported external prerequisite source '{prerequisite.source}'.");
                }

                AddRequest add = Client.Add(addSpec);
                while (!add.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync(cancellationToken);
                }
                if (add.Status != StatusCode.Success || add.Result?.name != prerequisite.packageId)
                    return AddonOperationResult<bool>.Fail(
                        $"external_prerequisite_missing: {prerequisite.packageId}: {add.Error?.message}");
                if (prerequisite.source == "git" &&
                    !string.Equals(add.Result.git?.revision, prerequisite.resolvedCommit,
                        StringComparison.OrdinalIgnoreCase))
                    return AddonOperationResult<bool>.Fail(
                        $"external_prerequisite_version_conflict: {prerequisite.packageId} " +
                        "resolved to an unexpected commit.");
                AddonAuditLog.Record("external_prerequisite", "resolved", prerequisite.packageId,
                    add.Result.version, prerequisite.resolvedCommit, prerequisite.publisher);
                installed[prerequisite.packageId] = add.Result;
            }
            return AddonOperationResult<bool>.Ok(true);
        }
    }
}
