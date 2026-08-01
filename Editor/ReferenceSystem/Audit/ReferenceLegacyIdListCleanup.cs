using System;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>How many authored id-list entries a settings asset still carries, and whether dropping
    /// them is safe yet.</summary>
    public sealed class ReferenceLegacyIdListState
    {
        /// <summary>Entries under the asset-wide bucket.</summary>
        public int AssetEntries { get; }

        /// <summary>Entries across every per-scene bucket.</summary>
        public int SceneEntries { get; }

        /// <summary>Per-scene collections present.</summary>
        public int SceneCollections { get; }

        /// <summary>Total id entries the lists hold.</summary>
        public int TotalEntries => AssetEntries + SceneEntries;

        /// <summary>True when there is anything to remove.</summary>
        public bool HasEntries => TotalEntries > 0;

        /// <summary>
        /// Why removal is not offered yet, or empty when it is.
        /// </summary>
        public string BlockedReason { get; }

        /// <summary>True when the lists can be dropped.</summary>
        public bool CanRemove => HasEntries && string.IsNullOrEmpty(BlockedReason);

        internal ReferenceLegacyIdListState(
            int assetEntries, int sceneEntries, int sceneCollections, string blockedReason)
        {
            AssetEntries = assetEntries;
            SceneEntries = sceneEntries;
            SceneCollections = sceneCollections;
            BlockedReason = blockedReason ?? string.Empty;
        }

        /// <summary>One-line summary for the Hub.</summary>
        public string Describe()
        {
            if (!HasEntries)
                return "No legacy cached id lists remain.";

            var summary = $"{TotalEntries} cached id entr{(TotalEntries == 1 ? "y" : "ies")} "
                + $"({AssetEntries} asset, {SceneEntries} across {SceneCollections} scene collection"
                + $"{(SceneCollections == 1 ? "" : "s")})";

            return string.IsNullOrEmpty(BlockedReason) ? summary : $"{summary} — {BlockedReason}";
        }
    }

    /// <summary>
    /// Removes the authored id lists on <see cref="ReferenceManagerSettings"/>, which the audit index
    /// replaced as the operational record of what exists.
    /// </summary>
    /// <remarks>
    /// <para>The lists were the original "index": a hand-maintained snapshot of every id, written by a
    /// scan and read by validation. That made them a second source of truth able to disagree with the
    /// assets they described, and they routinely did — an id deleted from a scene stayed in the list
    /// forever, so validation reported providers that no longer existed.</para>
    ///
    /// <para><b>Removal is gated on a healthy audit.</b> Dropping the lists while the real index cannot
    /// answer would leave the project with neither, and the cleanup would look like the cause of whatever
    /// broke next. It is also a deliberate, reported action rather than a migration that runs on load:
    /// this writes to a committed asset, and nothing should rewrite committed data because a window
    /// opened.</para>
    /// </remarks>
    public static class ReferenceLegacyIdListCleanup
    {
        /// <summary>Serialized field holding the asset-wide id buckets.</summary>
        private const string AssetKnownIdsField = "assetKnownIds";

        /// <summary>Serialized field holding the per-scene id buckets.</summary>
        private const string SceneKnownIdsField = "sceneKnownIds";

        /// <summary>
        /// Inspects the settings asset and the latest audit to decide whether cleanup is offerable.
        /// </summary>
        /// <param name="snapshot">
        /// The most recent audit. Null, stale or unhealthy blocks removal — the new index has to be
        /// working before the old one is discarded.
        /// </param>
        /// <returns>The state; never null.</returns>
        public static ReferenceLegacyIdListState Inspect(ReferenceAuditSnapshot snapshot)
        {
            var settings = ReferenceAuditService.FindSettings();
            if (settings == null)
                return new ReferenceLegacyIdListState(0, 0, 0, "no ReferenceManagerSettings asset was found");

            int assetEntries = 0;
            int sceneEntries = 0;
            int sceneCollections = 0;

            using (var serialized = new SerializedObject(settings))
            {
                assetEntries = CountEntries(serialized.FindProperty(AssetKnownIdsField));

                var scenes = serialized.FindProperty(SceneKnownIdsField);
                if (scenes != null && scenes.isArray)
                {
                    sceneCollections = scenes.arraySize;
                    for (int i = 0; i < scenes.arraySize; i++)
                        sceneEntries += CountEntries(scenes.GetArrayElementAtIndex(i).FindPropertyRelative("types"));
                }
            }

            return new ReferenceLegacyIdListState(
                assetEntries, sceneEntries, sceneCollections, BlockedReason(snapshot));
        }

        /// <summary>Why removal must wait, or empty when it may proceed.</summary>
        private static string BlockedReason(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null)
                return "run an audit first, so the new index is known to work before the old one is discarded";

            if (!snapshot.Coverage.IsComplete)
                return "coverage is incomplete, so the audit cannot yet stand in for these lists";

            if (snapshot.Errors.Count > 0)
                return "resolve the reference errors first — removing the old lists while the index reports "
                    + "errors would confuse cause and effect";

            return string.Empty;
        }

        /// <summary>Total id strings across a list of <c>ReferenceTypeData</c> entries.</summary>
        private static int CountEntries(SerializedProperty typeList)
        {
            if (typeList == null || !typeList.isArray)
                return 0;

            int total = 0;
            for (int i = 0; i < typeList.arraySize; i++)
            {
                var ids = typeList.GetArrayElementAtIndex(i).FindPropertyRelative("ids");
                if (ids != null && ids.isArray)
                    total += ids.arraySize;
            }

            return total;
        }

        /// <summary>
        /// Clears both id buckets on the settings asset, under a single undoable operation.
        /// </summary>
        /// <param name="snapshot">The audit that justifies the removal.</param>
        /// <returns>How many id entries were removed, or zero when the cleanup was refused.</returns>
        /// <remarks>
        /// Written through <see cref="SerializedObject"/> so it participates in Undo and is saved as an
        /// ordinary asset edit. The gate is re-checked here rather than trusted from the caller: the UI
        /// that offered the action may have been drawn before the last audit changed its mind.
        /// </remarks>
        public static int Remove(ReferenceAuditSnapshot snapshot)
        {
            var state = Inspect(snapshot);
            if (!state.CanRemove)
            {
                Debug.LogWarning(
                    "[ReferenceLegacyIdLists] Refusing to remove the cached id lists: "
                    + (string.IsNullOrEmpty(state.BlockedReason) ? "there is nothing to remove" : state.BlockedReason));
                return 0;
            }

            var settings = ReferenceAuditService.FindSettings();
            if (settings == null)
                return 0;

            using (var serialized = new SerializedObject(settings))
            {
                Undo.RecordObject(settings, "Remove legacy cached reference id lists");

                serialized.FindProperty(AssetKnownIdsField)?.ClearArray();
                serialized.FindProperty(SceneKnownIdsField)?.ClearArray();
                serialized.ApplyModifiedProperties();
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);

            Debug.Log(
                $"[ReferenceLegacyIdLists] Removed {state.TotalEntries} cached id entr"
                + $"{(state.TotalEntries == 1 ? "y" : "ies")} from {AssetDatabase.GetAssetPath(settings)}. "
                + "The audit index is now the only record of what exists.");

            return state.TotalEntries;
        }
    }
}
