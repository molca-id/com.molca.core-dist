using System;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Molca.Editor.About
{
    /// <summary>
    /// Applies (or hands over) a Core upgrade, according to how Core is installed in this project.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>.
    /// Two hard rules, both about not lying to the developer:
    /// <list type="bullet">
    /// <item>An install is only ever mutated through <see cref="UnityEditor.PackageManager.Client"/>, and
    /// only after an explicit confirmation. This code never edits <c>Packages/manifest.json</c> or files on
    /// disk itself — when a git dependency is repointed it is Unity that rewrites the manifest entry, the
    /// same way its own <c>Manage ▸ Update</c> does.</item>
    /// <item>Any install the Package Manager does not own, and any spec it could not apply faithfully, gets
    /// the exact instruction on the clipboard instead of a button that pretends to work. Which case applies
    /// is decided by <see cref="FrameworkUpdateEvaluator.ResolvePath"/>, not here.</item>
    /// </list>
    /// Adding a package triggers a package resolve and domain reload, so the caller must not assume it
    /// survives the await. Editor-only; main thread.
    /// </remarks>
    internal static class FrameworkUpgrader
    {
        private const int PollTimeoutSeconds = 300;

        /// <summary>
        /// Asks the Package Manager to move this project to <paramref name="upgradeSpec"/>.
        /// </summary>
        /// <param name="upgradeSpec">
        /// The spec published by the feed — a registry spec (<c>com.molca.core@1.15.0</c>) or a git ref
        /// (<c>https://…/molca-core.git#v1.15.0</c>). <c>Client.Add</c> takes both; a git URL is how the
        /// Package Manager repoints a git dependency, and Unity writes the manifest entry itself.
        /// </param>
        /// <param name="cancellationToken">Stops polling; the Package Manager request itself cannot be cancelled.</param>
        /// <returns><c>null</c> on success, otherwise a message fit to show in the panel.</returns>
        /// <remarks>
        /// Resolving a git URL shells out to the developer's <c>git</c> and needs credentials for a private
        /// repository, exactly as the Package Manager window does. Both failures come back through
        /// <see cref="UnityEditor.PackageManager.Requests.Request.Error"/> and are surfaced verbatim rather
        /// than reinterpreted here.
        /// </remarks>
        internal static async Awaitable<string> AddAsync(string upgradeSpec, CancellationToken cancellationToken = default)
        {
            string spec = FrameworkUpgradeSpec.Normalize(upgradeSpec);
            if (spec.Length == 0)
                return "No upgrade instruction was published for this release.";

            AddRequest request;
            try { request = Client.Add(spec); }
            catch (Exception exception) { return $"The Package Manager refused the request: {exception.Message}"; }

            var deadline = DateTime.UtcNow.AddSeconds(PollTimeoutSeconds);
            while (request.Status == StatusCode.InProgress)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline)
                    return "The Package Manager did not finish resolving in time. Check the Package Manager window.";
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            return request.Status == StatusCode.Success
                ? null
                : request.Error?.message ?? "The Package Manager could not add that version.";
        }

        /// <summary>
        /// Confirms the upgrade with the developer before anything is changed.
        /// </summary>
        /// <param name="target">The release being installed.</param>
        /// <returns><c>true</c> when the developer accepted.</returns>
        /// <remarks>
        /// Upgrading Core repoints every assembly in the project and forces a domain reload, so it is not a
        /// silent action even though it is reversible. Upgrade notes go in the dialog, not just the panel:
        /// this is the last moment they will be read.
        /// </remarks>
        internal static bool Confirm(FrameworkReleaseDto target)
        {
            if (target == null) return false;

            string notes = string.IsNullOrWhiteSpace(target.upgradeNotes)
                ? string.Empty
                : $"\n\nUpgrade notes:\n{target.upgradeNotes}";
            return EditorUtility.DisplayDialog(
                $"Update Molca Core to {target.version}?",
                $"This repoints com.molca.core to {target.version} and triggers a package resolve and domain " +
                $"reload.{notes}\n\nCommit or stash your work first if the reload would be disruptive.",
                $"Update to {target.version}", "Cancel");
        }

        /// <summary>
        /// Copies the upgrade instruction for a project that owns its own copy of Core.
        /// </summary>
        /// <param name="path">The resolved upgrade path, which decides what is useful to copy.</param>
        /// <param name="target">The release being targeted.</param>
        /// <returns>A short confirmation of what landed on the clipboard.</returns>
        internal static string CopyInstruction(FrameworkUpgradePath path, FrameworkReleaseDto target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.upgradeSpec))
                return "No upgrade instruction was published for this release.";

            string spec = FrameworkUpgradeSpec.Normalize(target.upgradeSpec);
            string payload = path switch
            {
                // A git install that could not be applied from here is upgraded by repointing the manifest
                // entry, so the useful thing to paste is the dependency line itself, not the bare spec.
                FrameworkUpgradePath.Manifest =>
                    $"\"com.molca.core\": \"{FrameworkUpgradeSpec.StripName(spec)}\"",
                _ => spec,
            };

            EditorGUIUtility.systemCopyBuffer = payload;
            return path == FrameworkUpgradePath.Manifest
                ? "Copied the manifest dependency line. Paste it into Packages/manifest.json and let Unity resolve."
                : $"Copied `{payload}`.";
        }
    }
}
