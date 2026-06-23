using UnityEditor;
using UnityEngine;
using Molca.UI.Tokens;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Inspector for <see cref="MolcaStyleApplier"/>: the default fields plus an <b>Apply Token</b> button
    /// that resolves the assigned catalog/token through <see cref="MolcaUiTokenResolver"/> and bakes the
    /// concrete components onto the object (one undo group). Warns when the token isn't in the catalog.
    /// </summary>
    [CustomEditor(typeof(MolcaStyleApplier))]
    public class MolcaStyleApplierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var applier = (MolcaStyleApplier)target;
            bool resolvable = applier.Catalog != null && !string.IsNullOrEmpty(applier.Token);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!resolvable))
            {
                if (GUILayout.Button("Apply Token"))
                {
                    Undo.SetCurrentGroupName("Apply UI Token");
                    int group = Undo.GetCurrentGroup();
                    if (MolcaUiTokenResolver.TryApply(applier.Catalog, applier.Token, applier.gameObject, out var error))
                        Debug.Log($"[Molca UI] Applied '{applier.Token}' to '{applier.name}'.", applier);
                    else
                        Debug.LogWarning($"[Molca UI] Could not apply '{applier.Token}': {error}", applier);
                    Undo.CollapseUndoOperations(group);
                }
            }

            if (resolvable && !applier.Catalog.TryResolve(applier.Token, out _))
                EditorGUILayout.HelpBox(
                    $"Token '{applier.Token}' is not in catalog '{applier.Catalog.name}'.",
                    MessageType.Warning);
        }
    }
}
