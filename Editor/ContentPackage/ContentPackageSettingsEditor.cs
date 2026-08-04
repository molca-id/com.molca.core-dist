using UnityEngine;
using UnityEditor;
using System.Linq;
using Molca.ContentPackage;
using Molca.Editor.UI;
using ContentValidation = Molca.ContentPackage.Editor.ContentValidation;
using ContentIssueSeverity = Molca.ContentPackage.Editor.ContentIssueSeverity;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// A summary of the content settings asset, and the way to the workspace that authors it.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/ContentPackage/</c>.
    /// <b>Registration:</b> <c>[CustomEditor(typeof(ContentPackageSettings))]</c>.
    /// <para>
    /// <b>This used to be the authoring surface</b> — a two-column package list and detail form, an
    /// Addressables label picker, a collapsible system-settings panel, and a build panel, across five
    /// files. All of it now lives in the Hub's Content workspace, which reaches the asset through
    /// <c>ContentPackageEditingService</c> rather than through an inspector's own
    /// <see cref="SerializedObject"/>. Keeping both would mean two surfaces that can disagree about
    /// what a package is, and only one of them validates.
    /// </para>
    /// <para>
    /// What is left follows the slim-page rule the design language already applies to
    /// <c>Project Settings &gt; Molca</c>: say what this asset is, say whether it is healthy, and open
    /// the place it is edited. The health line is not decoration — an asset selected in the Project
    /// window is often selected <em>because</em> something is wrong with it, and sending the reader to
    /// the Hub to find out what would be a worse answer than one line here.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(ContentPackageSettings))]
    public class ContentPackageSettingsEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var settings = (ContentPackageSettings)target;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Content Packages", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{settings.packageConfigs.Count} package(s) defined.",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            DrawHealth(settings);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Open in Molca Hub", GUILayout.Height(24)))
                Molca.Editor.Hub.MolcaHubWindow.OpenWorkspace("content");

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "Packages, release identity, delivery, and publishing are authored there.",
                EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.Space(8);
            DrawReadOnlyWarning();
        }

        /// <summary>
        /// One line of validation, from the engine every other surface uses.
        /// </summary>
        /// <remarks>
        /// Settings-level only, and it says so. Findings that need a build graph — a package that
        /// resolves to no bundles, a bundle belonging to nothing — cannot be produced without building,
        /// and implying otherwise here is exactly the confidently-wrong reporting the workspace was
        /// rebuilt to remove.
        /// </remarks>
        private static void DrawHealth(ContentPackageSettings settings)
        {
            var report = ContentValidation.ValidateSettings(settings.packageConfigs);

            if (report.ErrorCount == 0 && report.WarningCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No configuration findings. Content is checked against a build on the Hub's Verify page.",
                    MessageType.None);
                return;
            }

            string summary = $"{report.ErrorCount} error(s), {report.WarningCount} warning(s).";
            var worst = report.ErrorCount > 0 ? MessageType.Error : MessageType.Warning;

            var lines = report.Issues
                .Take(6)
                .Select(issue =>
                {
                    string marker = issue.Severity == ContentIssueSeverity.Error ? "✕"
                        : issue.Severity == ContentIssueSeverity.Warning ? "!" : "·";
                    return string.IsNullOrEmpty(issue.PackageId)
                        ? $"{marker} {issue.Message}"
                        : $"{marker} [{issue.PackageId}] {issue.Message}";
                });

            string more = report.Issues.Count > 6 ? $"\n\n…and {report.Issues.Count - 6} more." : "";
            EditorGUILayout.HelpBox($"{summary}\n\n{string.Join("\n\n", lines)}{more}", worst);
        }

        /// <summary>
        /// Warns when this asset lives somewhere an edit would not survive.
        /// </summary>
        /// <remarks>
        /// Shown here as well as in the workspace because the Project window is where someone finds a
        /// second copy of this asset. The rule is the editing service's, read through it rather than
        /// re-derived, so the inspector cannot disagree with what the workspace refuses.
        /// </remarks>
        private void DrawReadOnlyWarning()
        {
            string reason = new Molca.ContentPackage.Editor.ContentPackageEditingService(
                (ContentPackageSettings)target).ReadOnlyReason();

            if (reason != null) EditorGUILayout.HelpBox(reason, MessageType.Warning);
        }
    }
}
