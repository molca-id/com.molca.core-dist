using System.Linq;
using UnityEngine;
using UnityEditor;
using Molca.Settings;

namespace Molca.Editor
{
    /// <summary>
    /// A summary of the build settings asset, and the way to the surface that authors it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Settings/</c>.
    /// <b>Registration:</b> <c>[CustomEditor(typeof(BuildSettings))]</c>.
    /// </para>
    /// <para>
    /// <b>This used to be an authoring surface</b> — a reorderable profile list, a per-profile detail
    /// form, and its own Build All button, duplicating the Hub's Build &amp; Version section. Both
    /// copies also carried the same bug when adding a profile (writing a <c>BuildTarget</c> value into
    /// <c>enumValueIndex</c>, which is a popup position, so every new profile started out targeting a
    /// retired console). One surface means one place for that to be wrong, and one place to fix it.
    /// </para>
    /// <para>
    /// What is left says what this asset holds and opens where it is edited, following the same slim-page
    /// rule as <c>ContentPackageSettings</c> and <c>Project Settings &gt; Molca</c>.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(BuildSettings))]
    public class BuildSettingsEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var settings = (BuildSettings)target;
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Build Profiles", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{settings.Profiles.Count} profile(s) defined.", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            DrawTargetSummary(settings, activeTarget);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Open in Molca Hub", GUILayout.Height(24)))
                Hub.MolcaHubWindow.OpenSettingsSection(Hub.MolcaHubSection.BuildVersion);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "Profiles, signing, options and builds are authored there.",
                EditorStyles.centeredGreyMiniLabel);
        }

        /// <summary>
        /// Says which profiles the editor could build right now.
        /// </summary>
        /// <param name="settings">The build settings asset.</param>
        /// <param name="activeTarget">The editor's active build target.</param>
        /// <remarks>
        /// The editor builds one target at a time, so which profiles match the active target is the
        /// single most useful fact about this asset when it is selected — and the reason "Build All"
        /// in the Hub means "every profile for the current target".
        /// </remarks>
        private static void DrawTargetSummary(BuildSettings settings, BuildTarget activeTarget)
        {
            var matching = settings.Profiles
                .Where(p => p != null && p.target == activeTarget && !string.IsNullOrWhiteSpace(p.name))
                .Select(p => p.name)
                .ToList();

            if (matching.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No profile targets the active build target ({activeTarget}). Switch the target, or " +
                    "edit a profile in the Hub.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox(
                $"Buildable for the active target ({activeTarget}): {string.Join(", ", matching)}.",
                MessageType.None);
        }
    }
}
