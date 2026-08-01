using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Shared editor helpers for changing a provider's Ref Id.
    /// </summary>
    /// <remarks>
    /// <para>This used to offer a blanket <c>oldId → newId</c> rewrite of every serialized <c>refId</c>
    /// string in the loaded scenes. Three things were wrong with that. It matched any string property named
    /// <c>refId</c>, not only real reference fields. It could not know which of two duplicate providers a
    /// given reference had meant, so on the very case it existed to fix it pointed references at the wrong
    /// object. And it silently rewrote data in scenes the user was not looking at.</para>
    ///
    /// <para>What replaces it is information, not automation: before an id changes, the user sees exactly
    /// which reference sites will stop resolving, and decides. Re-pointing those sites is then a per-site
    /// choice, made where the intended target is actually known.</para>
    /// </remarks>
    internal static class RefIdEditorUtility
    {
        /// <summary>
        /// Asks the user to confirm a Ref Id change, listing the reference sites it will break.
        /// </summary>
        /// <param name="inbound">
        /// Sites currently pointing at <paramref name="oldId"/>, from <see cref="FindInboundSites"/>. Passed
        /// in rather than looked up here so the caller resolves them once, before the id changes.
        /// </param>
        /// <param name="oldId">The provider's current Ref Id. Empty means nothing can reference it yet.</param>
        /// <param name="displayName">Human-readable name of the provider whose id is changing.</param>
        /// <returns>True when the change should proceed.</returns>
        internal static bool ConfirmIdChange(
            IReadOnlyList<ReferenceSiteRecord> inbound, string oldId, string displayName)
        {
            if (string.IsNullOrEmpty(oldId) || inbound == null || inbound.Count == 0)
                return true;

            var listed = inbound.Take(10).Select(site => $"  • {site.Describe()}");
            var overflow = inbound.Count > 10 ? $"\n  … +{inbound.Count - 10} more" : string.Empty;

            var message =
                $"Changing the Ref Id of \"{displayName}\" will break {inbound.Count} reference(s) that "
                + $"point at \"{oldId}\":\n\n"
                + string.Join("\n", listed) + overflow + "\n\n"
                + "These are not rewritten automatically: when an id is shared or duplicated there is no way "
                + "to know which target each reference meant, and guessing points them at the wrong object. "
                + "Proceed, then re-assign each listed reference to its intended target.";

            return EditorUtility.DisplayDialog("Change Ref Id?", message, "Change Anyway", "Cancel");
        }

        /// <summary>
        /// Logs the reference sites left unresolved by an id change, so the follow-up work is recorded.
        /// </summary>
        /// <param name="brokenSites">The sites that pointed at the old id.</param>
        /// <param name="oldId">The previous Ref Id.</param>
        /// <param name="newId">The newly assigned Ref Id.</param>
        /// <param name="displayName">Human-readable name of the provider whose id changed.</param>
        internal static void ReportBrokenInboundReferences(
            IReadOnlyList<ReferenceSiteRecord> brokenSites, string oldId, string newId, string displayName)
        {
            if (brokenSites == null || brokenSites.Count == 0)
                return;

            var lines = brokenSites.Select(site => $"  • {site.Describe()}  (stored \"{site.StoredRefId}\")");
            Debug.LogWarning(
                $"[RefId] \"{displayName}\" changed from \"{oldId}\" to \"{newId}\". "
                + $"{brokenSites.Count} reference(s) still point at the old id and no longer resolve:\n"
                + string.Join("\n", lines)
                + $"\nRe-assign each to the intended target, or set them back to \"{newId}\" if this object "
                + "really was what they meant.");
        }

        /// <summary>
        /// Reference sites whose stored Ref Id is <paramref name="refId"/>, from the shared audit.
        /// </summary>
        /// <param name="refId">The Ref Id to search for.</param>
        /// <returns>Matching sites; empty when none, or when no audit could be produced.</returns>
        internal static IReadOnlyList<ReferenceSiteRecord> FindInboundSites(string refId)
        {
            if (string.IsNullOrEmpty(refId))
                return System.Array.Empty<ReferenceSiteRecord>();

            // A user-initiated id change is worth a full audit: the whole point is to know what breaks
            // across the project, not just in whatever scene happens to be open. Synchronous because the
            // caller is a GUI button that must have the answer before it can put up its confirmation
            // dialog — so the scan gets a progress bar rather than looking like a hang.
            try
            {
                var snapshot = ReferenceAuditService.GetOrRun(
                    progress: (phase, fraction) => EditorUtility.DisplayProgressBar(
                        "Checking inbound references", phase, Mathf.Clamp01(fraction)));

                return snapshot.Sites
                    .Where(site => string.Equals(site.StoredRefId, refId, System.StringComparison.Ordinal))
                    .ToList();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
