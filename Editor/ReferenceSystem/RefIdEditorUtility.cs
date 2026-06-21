using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Shared editor utilities for RefId operations.
    /// Extracted so both <see cref="ReferenceManagerSettingsEditor"/> and <see cref="RefIdDrawer"/>
    /// can redirect <see cref="Molca.ReferenceSystem.SceneObjectReference"/> fields after an ID change.
    /// </summary>
    internal static class RefIdEditorUtility
    {
        /// <summary>
        /// Shows a confirmation dialog listing the old→new ID remapping, then redirects
        /// <c>SceneObjectReference.refId</c> fields in loaded scenes if the user confirms.
        /// Matches the UX of the full project-scan redirect flow.
        /// </summary>
        /// <param name="displayName">Human-readable name of the object whose ID changed (used in the dialog).</param>
        internal static void OfferAndApplyRedirectInLoadedScenes(string oldId, string newId, string displayName)
        {
            string body =
                $"RefId was regenerated for \"{displayName}\":\n\n" +
                $"  {oldId}  →  {newId}\n\n" +
                "Do you want to update SceneObjectReference fields in loaded scenes that point to the old ID?\n\n" +
                "Redirect  — fields are updated to the new ID (they will resolve this object).\n" +
                "Keep      — fields keep the old ID (they will no longer resolve this object).";

            bool redirect = EditorUtility.DisplayDialog(
                "Update SceneObjectReferences?",
                body,
                "Redirect to New ID",
                "Keep Old ID");

            if (!redirect)
                return;

            var log = new List<string>();
            int redirected = RedirectInLoadedScenes(oldId, newId, log);
            if (redirected > 0)
                Debug.Log($"[RefId] Redirected SceneObjectReference on {redirected} object(s):\n" + string.Join("\n", log));
            else
                Debug.Log("[RefId] No SceneObjectReference fields found matching the old ID in loaded scenes.");
        }

        /// <summary>
        /// Redirects all <c>SceneObjectReference.refId</c> string fields in loaded scenes
        /// that match <paramref name="oldId"/> to <paramref name="newId"/>.
        /// </summary>
        /// <returns>Number of MonoBehaviours that had at least one field updated.</returns>
        internal static int RedirectInLoadedScenes(string oldId, string newId, List<string> log = null)
        {
            var oldToNew = new Dictionary<string, string>(System.StringComparer.Ordinal) { { oldId, newId } };
            int redirected = 0;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || !scene.IsValid())
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (RedirectSceneObjectRefsInObject(mb, oldToNew, log))
                            redirected++;
                    }
                }
            }

            return redirected;
        }

        /// <summary>
        /// Iterates all serialized properties on <paramref name="mb"/> and updates any string property
        /// named <c>refId</c> whose value matches a key in <paramref name="oldToNew"/>.
        /// </summary>
        /// <returns>True if at least one field was updated.</returns>
        internal static bool RedirectSceneObjectRefsInObject(
            MonoBehaviour mb,
            Dictionary<string, string> oldToNew,
            List<string> log)
        {
            var so = new SerializedObject(mb);
            bool changed = false;
            var iter = so.GetIterator();
            while (iter.NextVisible(true))
            {
                if (iter.propertyType != SerializedPropertyType.String || iter.name != "refId")
                    continue;
                var oldId = iter.stringValue;
                if (string.IsNullOrEmpty(oldId) || !oldToNew.TryGetValue(oldId, out var newId))
                    continue;
                iter.stringValue = newId;
                changed = true;
                log?.Add($"  {mb.name}/{iter.propertyPath}: {oldId} → {newId}");
            }
            if (changed)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(mb);
            }
            return changed;
        }
    }
}
