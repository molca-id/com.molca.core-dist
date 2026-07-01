using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="MolcaEditorSettings"/> that guards against a common trap: the live
    /// settings are persisted to <c>ProjectSettings/MolcaEditorSettings.asset</c> (outside the AssetDatabase)
    /// and resolved via <see cref="MolcaEditorSettings.Instance"/>, but a legacy/migration-source asset may
    /// still exist inside the (immutable) package or under <c>Assets/</c>.
    /// </summary>
    /// <remarks>
    /// Editing that AssetDatabase-backed asset has no effect — <see cref="MolcaEditorSettings.Instance"/>
    /// migrates its values only once, when the ProjectSettings file does not yet exist, and never re-reads
    /// it afterward. When the inspected target is such an asset, this editor renders a warning + a redirect
    /// to <b>Project Settings → Molca</b> and shows the fields read-only so the stale copy cannot be mistaken
    /// for the authoritative one. The settings provider draws its own <see cref="SerializedObject"/> bound to
    /// the live instance and does not use this editor, so it is unaffected.
    /// </remarks>
    [CustomEditor(typeof(MolcaEditorSettings))]
    internal sealed class MolcaEditorSettingsEditor : UnityEditor.Editor
    {
        private const string SettingsPath = "Project/Molca";

        public override void OnInspectorGUI()
        {
            // The authoritative instance lives outside the AssetDatabase (persisted via Save() to
            // ProjectSettings/). Anything the AssetDatabase can resolve is therefore a stale copy.
            bool isStaleAsset = AssetDatabase.Contains(target);

            if (isStaleAsset)
            {
                EditorGUILayout.HelpBox(
                    "This is a stale MolcaEditorSettings asset. The live settings are stored in " +
                    "ProjectSettings/MolcaEditorSettings.asset and edits made here are ignored. " +
                    "Configure Molca editor settings via Project Settings → Molca instead.",
                    MessageType.Warning);

                if (GUILayout.Button("Open Project Settings → Molca"))
                    SettingsService.OpenProjectSettings(SettingsPath);

                EditorGUILayout.Space();

                using (new EditorGUI.DisabledScope(true))
                    DrawDefaultInspector();

                return;
            }

            DrawDefaultInspector();
        }
    }
}
