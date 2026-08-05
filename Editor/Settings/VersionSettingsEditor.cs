using UnityEngine;
using UnityEditor;
using Molca.Settings;

namespace Molca.Editor
{
    /// <summary>
    /// A summary of the version settings asset, and the way to the surface that authors it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Settings/</c>.
    /// <b>Registration:</b> <c>[CustomEditor(typeof(VersionSettings))]</c>.
    /// </para>
    /// <para>
    /// <b>This used to be an authoring surface</b> — version fields, bump buttons, a release section, an
    /// advanced panel and a history list, all of it a second implementation of what the Hub's
    /// Build &amp; Version section already does. The two had already drifted: this one warned about an
    /// invalid version and the Hub did not, the Hub reimplemented the SemVer bump rules in raw
    /// serialized-property arithmetic instead of calling the model, and only this one could clear the
    /// history. Two surfaces that can disagree about the project's version is one more than a project
    /// can have, and the Hub is where the rest of the settings moved.
    /// </para>
    /// <para>
    /// What is left follows the slim-page rule already applied to <c>ContentPackageSettings</c> and
    /// <c>Project Settings &gt; Molca</c>: say what this asset is, say whether it is healthy, and open
    /// the place it is edited. The health line stays because an asset selected in the Project window is
    /// often selected <em>because</em> something is wrong with it.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(VersionSettings))]
    public class VersionSettingsEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var settings = (VersionSettings)target;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Version", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Full", settings.GetFullVersionString());
                EditorGUILayout.TextField("Semantic", settings.GetSemanticVersion());
                EditorGUILayout.TextField("PlayerSettings", PlayerSettings.bundleVersion);
            }

            EditorGUILayout.Space(6);
            if (!settings.IsValidVersion())
            {
                EditorGUILayout.HelpBox(
                    "Version is invalid: Major, Minor and Patch must be zero or greater, and Build must " +
                    "be at least 1. Builds abort until this is fixed.",
                    MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The build number advances and a changelog entry is written after a build succeeds.",
                    MessageType.None);
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Open in Molca Hub", GUILayout.Height(24)))
                Hub.MolcaHubWindow.OpenSettingsSection(Hub.MolcaHubSection.BuildVersion);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "Version fields, bumps, releases and history are authored there.",
                EditorStyles.centeredGreyMiniLabel);
        }
    }
}
