using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The navigation and clipboard actions the References detail panel offers: select, ping, open the
    /// owning scene or prefab, and copy a diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>Opening a closed scene is <b>explicit and confirmed</b>, never a side effect of looking at a
    /// finding. The audit itself is forbidden from disturbing the user's scene setup (see
    /// <see cref="ReferenceSceneScanSession"/>); navigation is the one place a scene may be opened, and only
    /// because the user asked for it by name and agreed to the prompt.</para>
    ///
    /// <para>Every action degrades honestly. A locator whose scene is closed cannot be resolved, so the
    /// action says which scene to open instead of silently doing nothing — the failure mode of the earlier
    /// tooling, where a Select button on an unresolvable target simply did not respond.</para>
    /// </remarks>
    internal static class ReferenceHubNavigation
    {
        /// <summary>
        /// Selects and pings the object a row's locator points at, or explains why it cannot.
        /// </summary>
        /// <param name="locator">The address to resolve.</param>
        /// <param name="what">Human-readable description used in the failure message, e.g. "owner".</param>
        /// <returns>True when an object was selected.</returns>
        internal static bool SelectAndPing(ReferenceObjectLocator locator, string what)
        {
            var target = locator.TryResolve();
            if (target == null)
            {
                Debug.LogWarning(
                    $"[ReferenceSystem] The {what} at {locator} is not loaded, so it cannot be selected. "
                    + (IsScene(locator.AssetPath)
                        ? $"Open '{locator.AssetPath}' first — use Open Scene on this row."
                        : "Its asset may have been deleted since the audit ran."));
                return false;
            }

            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
            return true;
        }

        /// <summary>True when the row names a scene asset that is not currently open.</summary>
        /// <param name="assetPath">The row's asset path.</param>
        internal static bool CanOpenScene(string assetPath) =>
            IsScene(assetPath) && !IsSceneOpen(assetPath);

        /// <summary>True when the row names a prefab asset.</summary>
        /// <param name="assetPath">The row's asset path.</param>
        internal static bool CanOpenPrefab(string assetPath) =>
            !string.IsNullOrEmpty(assetPath)
            && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Opens a scene after confirming, additively so the user's current setup is not replaced.
        /// </summary>
        /// <param name="assetPath">Scene asset path.</param>
        /// <returns>True when the scene was opened.</returns>
        /// <remarks>
        /// Additive rather than single: the reference this scene owns is very often about an object in a scene
        /// the user already has open, and replacing that setup to look at one finding would destroy the
        /// context they are working in. Unsaved work is still protected by Unity's own prompt.
        /// </remarks>
        internal static bool OpenScene(string assetPath)
        {
            if (!IsScene(assetPath))
                return false;

            if (IsSceneOpen(assetPath))
            {
                var open = SceneManager.GetSceneByPath(assetPath);
                Debug.Log($"[ReferenceSystem] Scene '{open.name}' is already open.");
                return true;
            }

            if (!EditorUtility.DisplayDialog(
                    "Open Scene?",
                    $"Open '{assetPath}' additively so this reference can be inspected?\n\n"
                    + "Your currently open scenes stay open.",
                    "Open", "Cancel"))
                return false;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            try
            {
                EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceSystem] Could not open scene '{assetPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>Opens a prefab asset in Prefab Mode.</summary>
        /// <param name="assetPath">Prefab asset path.</param>
        /// <returns>True when the prefab was opened.</returns>
        internal static bool OpenPrefab(string assetPath)
        {
            if (!CanOpenPrefab(assetPath))
                return false;

            var prefab = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[ReferenceSystem] Prefab '{assetPath}' no longer exists.");
                return false;
            }

            return AssetDatabase.OpenAsset(prefab);
        }

        /// <summary>Reveals and selects the row's asset in the Project window.</summary>
        /// <param name="assetPath">The asset to reveal.</param>
        /// <returns>True when the asset was found.</returns>
        internal static bool RevealAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return false;

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return true;
        }

        /// <summary>
        /// Builds the copyable diagnostic for a row: everything needed to reproduce or report the finding
        /// without the workspace open.
        /// </summary>
        /// <param name="row">The selected row.</param>
        /// <param name="snapshot">The snapshot the row came from, for candidate details.</param>
        internal static string BuildDiagnostic(ReferenceHubRow row, ReferenceAuditSnapshot snapshot)
        {
            if (row == null)
                return string.Empty;

            var text = new StringBuilder();
            text.AppendLine(string.IsNullOrEmpty(row.Code)
                ? $"[Molca References] {row.Kind}: {row.Title}"
                : $"[Molca References] {row.Code} {row.Title}");

            if (!string.IsNullOrEmpty(row.Summary))
                text.AppendLine(row.Summary);

            Append(text, "Asset", row.AssetPath);
            Append(text, "Owner", row.Owner);
            Append(text, "Property", row.PropertyPath);
            Append(text, "Stored target", row.StoredTarget);
            Append(text, "Scope", row.Scope);
            Append(text, "Source", row.SourceKind);
            Append(text, "Expected type", row.ExpectedType);
            Append(text, "Resolution", row.ResolutionState);
            Append(text, "Repair", row.Repair.ToString());
            if (row.Kind == ReferenceHubRowKind.Provider)
                Append(text, "Inbound", $"{row.InboundCount} resolved, {row.ClaimingCount} claiming the id");

            if (row.CandidateProviderKeys.Count > 0 && snapshot != null)
            {
                text.AppendLine("Candidates:");
                foreach (var candidate in row.CandidateProviderKeys.Select(snapshot.FindProvider).Where(p => p != null))
                    text.AppendLine($"  • {candidate}");
            }

            if (snapshot != null)
            {
                text.AppendLine(
                    $"Audit revision {snapshot.Revision}, {snapshot.State}, coverage {snapshot.Coverage.DescribeGaps()}");
            }

            return text.ToString();
        }

        /// <summary>Copies text to the system clipboard.</summary>
        /// <param name="text">The text to copy. Empty is ignored.</param>
        internal static void Copy(string text)
        {
            if (!string.IsNullOrEmpty(text))
                EditorGUIUtility.systemCopyBuffer = text;
        }

        private static void Append(StringBuilder text, string label, string value)
        {
            if (!string.IsNullOrEmpty(value))
                text.AppendLine($"  {label}: {value}");
        }

        private static bool IsScene(string assetPath) =>
            !string.IsNullOrEmpty(assetPath)
            && assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);

        private static bool IsSceneOpen(string assetPath)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (string.Equals(SceneManager.GetSceneAt(i).path, assetPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
