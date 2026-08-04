using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Molca.Editor.ReferenceSystem;
using Molca.Editor.ReferenceSystem.Hub;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Inspector for <see cref="ReferenceManagerSettings"/>: reference-system configuration, plus a
    /// read-only audit surface for the project's reference health.
    /// </summary>
    /// <remarks>
    /// <para><b>Scanning never modifies project data.</b> The previous implementation assigned fresh Ref
    /// Ids to providers that had none and reassigned Ref Ids it considered duplicated — during an operation
    /// the user had asked to <i>scan</i> — and then offered to rewrite every serialized <c>refId</c> string
    /// in the loaded scenes from the old value to the new one. That blanket redirect could not know which
    /// of two duplicate providers a given reference had meant, so on a real duplicate it silently pointed
    /// references at the wrong object. Both behaviors are gone: this Inspector reports, and repair is
    /// deliberate and per-site.</para>
    ///
    /// <para>The id buckets serialized on the settings asset are legacy. They are no longer written, and no
    /// longer consulted as authoritative data — the audit reads the objects that actually provide the
    /// references. They remain readable for one compatibility release, with an explicit action to drop
    /// them.</para>
    /// </remarks>
    [CustomEditor(typeof(ReferenceManagerSettings))]
    [InitializeOnLoad]
    public class ReferenceManagerSettingsEditor : UnityEditor.Editor
    {
        private bool _showLegacyBuckets;

        static ReferenceManagerSettingsEditor()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        #region Scene-save validation

        /// <summary>
        /// Validates the provider identities in the scene being saved, when enabled in settings.
        /// </summary>
        /// <remarks>
        /// <b>Validate only.</b> The old hook could auto-fix missing and duplicate Ref Ids on save, which
        /// meant saving a scene could silently change a provider's identity and break every reference
        /// pointing at it — with no preview, no undo grouping, and no record of which references were
        /// affected. Reference identity is not something a save should change behind the user's back.
        /// </remarks>
        private static void OnSceneSaving(Scene scene, string path)
        {
            var settings = GetSettingsSilent();
            if (settings == null || !settings.ValidateRefIdsOnSceneSave)
                return;

            try
            {
                var findings = ReferenceAuditEngine.CheckSceneProviders(scene);
                if (findings.Count == 0)
                    return;

                var sceneName = string.IsNullOrEmpty(scene.path)
                    ? scene.name
                    : System.IO.Path.GetFileNameWithoutExtension(scene.path);

                Debug.LogWarning(
                    $"[ReferenceSystem] Scene '{sceneName}' (pre-save): {findings.Count} provider identity "
                    + "issue(s). Nothing was changed — fix these deliberately so you control which "
                    + $"references move:\n  {string.Join("\n  ", findings.Select(f => f.ToMessage()))}");
            }
            catch (Exception e)
            {
                // A validation defect must never block a save.
                Debug.LogWarning($"[ReferenceSystem] Pre-save reference validation failed: {e.Message}");
            }
        }

        /// <summary>Finds the settings asset without showing dialogs (for the silent scene-save hook).</summary>
        private static ReferenceManagerSettings GetSettingsSilent() => ReferenceAuditService.FindSettings();

        #endregion

        #region Menu Items

        /// <summary>Runs a full read-only project reference audit and reports the result.</summary>
        [MenuItem("Molca/Reference System/Audit Project References", priority = 30)]
        public static void MenuScanProject()
        {
            var settings = GetOrCreateSettings();
            if (settings != null)
                RunAudit(settings, wholeProject: true);
        }

        /// <summary>Runs a read-only audit limited to the currently open scenes, prefabs and assets.</summary>
        [MenuItem("Molca/Reference System/Audit Open Scenes", priority = 31)]
        public static void MenuRefreshActiveScenes()
        {
            var settings = GetOrCreateSettings();
            if (settings != null)
                RunAudit(settings, wholeProject: false);
        }

        /// <summary>Drops the legacy cached id buckets from the settings asset.</summary>
        [MenuItem("Molca/Reference System/Remove Legacy Cached ID Lists", priority = 32)]
        public static void MenuClearReferences()
        {
            var settings = GetOrCreateSettings();
            if (settings == null)
                return;

            if (EditorUtility.DisplayDialog("Remove Legacy Cached ID Lists",
                    "The reference audit reads live providers, so these cached id lists are no longer used. "
                    + "Removing them only shrinks this asset — no reference is affected.",
                    "Remove", "Cancel"))
            {
                ClearLegacyBuckets(settings);
            }
        }

        private static ReferenceManagerSettings GetOrCreateSettings()
        {
            var existing = ReferenceAuditService.FindSettings();
            if (existing != null)
                return existing;

            if (EditorUtility.DisplayDialog("Reference Manager Settings Not Found",
                    "ReferenceManagerSettings asset not found. Would you like to create one?",
                    "Create", "Cancel"))
            {
                var newSettings = CreateInstance<ReferenceManagerSettings>();
                var path = EditorUtility.SaveFilePanelInProject(
                    "Save Reference Manager Settings", "Reference Manager Settings", "asset",
                    "Please enter a file name to save the settings");

                if (!string.IsNullOrEmpty(path))
                {
                    AssetDatabase.CreateAsset(newSettings, path);
                    AssetDatabase.SaveAssets();
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = newSettings;
                    return newSettings;
                }
            }

            Debug.LogWarning("[ReferenceManagerSettings] Settings asset not found. Please create one or add it to GlobalSettings.");
            return null;
        }

        #endregion

        #region Inspector

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var settings = (ReferenceManagerSettings)target;
            serializedObject.Update();

            DrawConfiguration(settings);
            EditorGUILayout.Space();
            DrawAuditSection(settings);
            EditorGUILayout.Space();
            DrawLegacySection(settings);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawConfiguration(ReferenceManagerSettings settings)
        {
            EditorGUILayout.LabelField("Scan Configuration", EditorStyles.boldLabel);

            DrawProperty("prefabScanPaths");

            EditorGUILayout.HelpBox(
                "Prefab Scan Paths gates prefab coverage: an empty list means prefabs are not scanned, and "
                + "the audit reports that as a coverage gap rather than as success. Scene coverage is not "
                + "configured here — a Full audit always opens the project's closed scenes, and is skipped "
                + "when any open scene has unsaved changes, since a read-only audit will neither discard "
                + "nor save your work.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reporting", EditorStyles.boldLabel);
            DrawProperty("validateRefIdsOnSceneSave");
            DrawProperty("showValidationResults");
            DrawProperty("enableDebugLogging");

            if (settings.ValidateRefIdsOnSceneSave)
            {
                EditorGUILayout.HelpBox(
                    "On save, the scene's provider identities are checked for missing and duplicated Ref "
                    + "Ids and reported in the Console. Nothing is modified.",
                    MessageType.None);
            }

            // The two legacy toggles are still serialized (removing a field would silently drop authored
            // data) but no longer do anything, and saying so is better than leaving a switch that lies.
            // Reading the obsolete properties is deliberate: it is the only way to tell whether a project
            // still has them enabled and therefore needs the notice.
#pragma warning disable 618
            if (settings.FixRefIdsOnSceneSave)
            {
                EditorGUILayout.HelpBox(
                    "'Fix Ref Ids On Scene Save' is deprecated and no longer does anything. Reassigning a "
                    + "provider's Ref Id during a save silently broke every reference pointing at it. Fix "
                    + "reported duplicates deliberately instead, so you choose which references move.",
                    MessageType.Warning);
            }

            if (settings.AutoValidateOnScan)
            {
                EditorGUILayout.HelpBox(
                    "'Auto Validate On Scan' is deprecated and no longer does anything: analysis is now "
                    + "part of every scan, so there is nothing to switch off.",
                    MessageType.None);
            }
#pragma warning restore 618
        }

        /// <summary>
        /// Reports the current reference health in one line and hands off to the References workspace.
        /// </summary>
        /// <remarks>
        /// Deliberately <b>not</b> a second management UI. Triage needs filters, a detail panel, candidate
        /// navigation, repair previews and live runtime state, and an Inspector cannot host any of that well;
        /// maintaining a lesser copy of it here would guarantee the two surfaces eventually disagreed about
        /// what a finding means. This is a status line and a door.
        /// </remarks>
        private void DrawAuditSection(ReferenceManagerSettings settings)
        {
            var snapshot = ReferenceAuditService.Current;
            var stale = ReferenceAuditService.IsStale;

            EditorGUILayout.LabelField("Reference Health", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                snapshot.DescribeState()
                + (stale ? $"\nStale: {ReferenceAuditService.StaleReason}. Re-run to refresh." : string.Empty),
                MessageTypeFor(snapshot.State, stale));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open References Workspace", EditorStyles.miniButton))
                ReferenceHubWorkspace.Open();
            using (new EditorGUI.DisabledScope(snapshot.Findings.Count == 0))
            {
                if (GUILayout.Button("Copy Report", EditorStyles.miniButton))
                    EditorGUIUtility.systemCopyBuffer = BuildReport(snapshot);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Findings, filters, candidate navigation, severity policy and previewed repairs live in "
                + "Molca Hub → References.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawLegacySection(ReferenceManagerSettings settings)
        {
            var assetBuckets = GetLegacyAssetBuckets(settings);
            var sceneBuckets = GetLegacySceneBuckets(settings);
            var total = assetBuckets.Count + sceneBuckets.Count;
            if (total == 0)
                return;

            _showLegacyBuckets = EditorGUILayout.Foldout(
                _showLegacyBuckets, $"Legacy cached ID lists ({total} bucket(s))", true, EditorStyles.foldoutHeader);

            if (!_showLegacyBuckets)
                return;

            EditorGUILayout.HelpBox(
                "These lists were maintained by the old scan and are no longer written or trusted. The "
                + "audit reads the objects that actually provide the references, so a stale cache can no "
                + "longer produce a false finding — or false confidence. Safe to remove.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
            {
                DrawProperty("assetKnownIds");
                DrawProperty("sceneKnownIds");
            }

            if (GUILayout.Button("Remove Legacy Cached ID Lists"))
                ClearLegacyBuckets(settings);
        }

        private void DrawProperty(string propertyName)
        {
            var property = serializedObject.FindProperty(propertyName);
            if (property != null)
                EditorGUILayout.PropertyField(property, true);
        }

        private static MessageType MessageTypeFor(ReferenceAuditState state, bool stale)
        {
            if (state == ReferenceAuditState.Errors)
                return MessageType.Error;
            if (stale || state == ReferenceAuditState.Incomplete || state == ReferenceAuditState.Warnings)
                return MessageType.Warning;
            return MessageType.Info;
        }

        #endregion

        #region Audit entry points

        /// <summary>
        /// Runs a read-only project reference audit and reports the result.
        /// </summary>
        /// <param name="settings">The settings asset supplying scan configuration.</param>
        /// <remarks>
        /// Kept under its historical name so existing callers and menu bindings keep working. It no longer
        /// rebuilds any id bucket and no longer writes to a single asset.
        /// </remarks>
        public void ScanProjectReferences(ReferenceManagerSettings settings) => RunAudit(settings, wholeProject: true);

        /// <summary>
        /// Runs a read-only audit limited to the currently open scenes, prefabs and ScriptableObjects.
        /// </summary>
        /// <param name="settings">The settings asset supplying scan configuration.</param>
        public void ScanActiveScenes(ReferenceManagerSettings settings) => RunAudit(settings, wholeProject: false);

        /// <summary>
        /// Removes the legacy cached id buckets from <paramref name="settings"/>.
        /// </summary>
        /// <param name="settings">The settings asset to clean up.</param>
        /// <remarks>
        /// Kept under its historical name. It clears only derived cache data; no reference and no provider
        /// identity is touched.
        /// </remarks>
        public void ClearAllReferences(ReferenceManagerSettings settings) => ClearLegacyBuckets(settings);

        /// <summary>
        /// Runs a read-only audit and reports the result.
        /// </summary>
        /// <remarks>
        /// <c>async void</c> because this is invoked from Inspector button callbacks and menu items — Unity
        /// event-handler entry points — so the body is a try/catch shim per the async contract. Awaiting the
        /// engine rather than blocking keeps the cancellable progress bar live instead of freezing the editor
        /// for the duration of the scan.
        /// </remarks>
        private static async void RunAudit(ReferenceManagerSettings settings, bool wholeProject) // doctor:ignore async-void is intentional: GUI/menu callback wrapped in try/catch
        {
            var scope = ReferenceAuditScope.FromSettings(settings, mayOpenScenes: wholeProject);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var cancellation = new CancellationTokenSource();

            ReferenceAuditSnapshot snapshot;
            try
            {
                snapshot = await ReferenceAuditService.RefreshAsync(
                    scope, (phase, fraction) => ReportProgress(phase, fraction, cancellation), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                EditorUtility.ClearProgressBar();
                Debug.Log("[ReferenceSystem] Audit cancelled; nothing was modified.");
                return;
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[ReferenceSystem] Audit failed: {e}");
                EditorUtility.DisplayDialog(
                    "Reference Audit Failed",
                    $"The audit stopped with an error after {stopwatch.Elapsed.TotalSeconds:F1} s.\n\n"
                    + $"{e.Message}\n\nDetails are in the Console.",
                    "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                cancellation.Dispose();
            }

            var report = BuildReport(snapshot);
            if (snapshot.State == ReferenceAuditState.Errors)
                Debug.LogError(report);
            else if (snapshot.State == ReferenceAuditState.Clean)
                Debug.Log(report);
            else
                Debug.LogWarning(report);

            EditorUtility.DisplayDialog(
                "Reference Audit — Complete", BuildDialogSummary(snapshot), "OK");
        }

        /// <summary>
        /// Draws the cancellable progress bar and cancels the run when the user dismisses it.
        /// </summary>
        private static void ReportProgress(string phase, float fraction, CancellationTokenSource cancellation)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Auditing References", phase, Mathf.Clamp01(fraction)))
                cancellation.Cancel();
        }

        private static string BuildReport(ReferenceAuditSnapshot snapshot)
        {
            var report = new StringBuilder();
            report.AppendLine($"[ReferenceSystem] Audit report — {snapshot.DescribeState()}");
            report.AppendLine($"Duration: {snapshot.Duration.TotalSeconds:F2} s (revision {snapshot.Revision})");

            report.AppendLine("Coverage:");
            foreach (var entry in snapshot.Coverage.Entries)
                report.AppendLine($"  • {entry}");

            if (snapshot.Findings.Count > 0)
            {
                report.AppendLine("Findings:");
                foreach (var finding in snapshot.Findings)
                {
                    report.AppendLine($"  • {finding.ToMessage()}");
                    if (!string.IsNullOrEmpty(finding.AssetPath))
                        report.AppendLine($"      at {finding.AssetPath}");
                }
            }

            return report.ToString();
        }

        private static string BuildDialogSummary(ReferenceAuditSnapshot snapshot)
        {
            var summary = new StringBuilder();
            summary.AppendLine($"State: {snapshot.State}");
            summary.AppendLine($"Completed in {snapshot.Duration.TotalSeconds:F1} s.");
            summary.AppendLine();
            summary.AppendLine($"Providers found: {snapshot.Providers.Count}");
            summary.AppendLine($"Reference sites found: {snapshot.Sites.Count}");
            summary.AppendLine($"Errors: {snapshot.Errors.Count}   Warnings: {snapshot.Warnings.Count}");
            summary.AppendLine();
            summary.AppendLine($"Coverage: {snapshot.Coverage.DescribeGaps()}");

            if (!snapshot.Coverage.IsComplete)
            {
                summary.AppendLine();
                summary.AppendLine(
                    "Coverage is incomplete, so this is not a clean result even with no findings.");
            }

            summary.AppendLine();
            summary.AppendLine("Nothing was modified. The full report is in the Console.");

            const int maxLines = 8;
            if (snapshot.Findings.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Findings (sample):");
                foreach (var finding in snapshot.Findings.Take(maxLines))
                    summary.AppendLine($"  • {finding.CodeString} {finding.Title}");
                if (snapshot.Findings.Count > maxLines)
                    summary.AppendLine($"  … +{snapshot.Findings.Count - maxLines} more");
            }

            return summary.ToString();
        }

        #endregion

        #region Legacy buckets

        private static void ClearLegacyBuckets(ReferenceManagerSettings settings)
        {
            if (settings == null)
                return;

            var serialized = new SerializedObject(settings);
            var cleared = 0;

            foreach (var name in new[] { "assetKnownIds", "sceneKnownIds", "knownIds" })
            {
                var property = serialized.FindProperty(name);
                if (property == null || !property.isArray || property.arraySize == 0)
                    continue;

                cleared += property.arraySize;
                property.ClearArray();
            }

            if (cleared == 0)
            {
                Debug.Log("[ReferenceSystem] No legacy cached ID lists to remove.");
                return;
            }

            serialized.ApplyModifiedProperties();
            Debug.Log($"[ReferenceSystem] Removed {cleared} legacy cached ID bucket(s) from '{settings.name}'.");
        }

        private static List<ReferenceManagerSettings.ReferenceTypeData> GetLegacyAssetBuckets(
            ReferenceManagerSettings settings) =>
            ReadPrivateList<ReferenceManagerSettings.ReferenceTypeData>(settings, "assetKnownIds");

        private static List<ReferenceManagerSettings.SceneKnownIdsCollection> GetLegacySceneBuckets(
            ReferenceManagerSettings settings) =>
            ReadPrivateList<ReferenceManagerSettings.SceneKnownIdsCollection>(settings, "sceneKnownIds");

        // The legacy buckets are private serialized fields with no public accessor for the raw lists.
        // Reflection is confined to this display-and-clear path rather than spread through the editor.
        private static List<T> ReadPrivateList<T>(ReferenceManagerSettings settings, string fieldName)
        {
            if (settings == null)
                return new List<T>();

            var field = typeof(ReferenceManagerSettings).GetField(
                fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as List<T> ?? new List<T>();
        }

        #endregion
    }
}
