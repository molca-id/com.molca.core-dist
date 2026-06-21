using UnityEngine;
using UnityEditor;
using Molca.Sequence;
using Molca.Editor.UI;

namespace Molca.Editor
{
    /// <summary>
    /// Editor utility for visualizing step states in the hierarchy
    /// </summary>
    [InitializeOnLoad]
    public static class StepHierarchyUtility
    {
        static StepHierarchyUtility()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyWindowItemOnGUI;
        }

        private static void OnHierarchyWindowItemOnGUI(int instanceID, Rect selectionRect)
        {
            if (!Application.isPlaying) return;

            var go = EditorUtility.EntityIdToObject(instanceID) as GameObject;
            if (go == null) return;

            // Check for Step component first
            var step = go.GetComponent<Step>();
            if (step != null)
            {
                DrawStepStatus(step, selectionRect);
                return;
            }

            // Check for SequenceController component
            var sequenceController = go.GetComponent<SequenceController>();
            if (sequenceController != null)
            {
                DrawSequenceControllerStatus(sequenceController, selectionRect);
                return;
            }
        }

        private static void DrawStepStatus(Step step, Rect selectionRect)
        {
            string label;
            Color color;
            switch (step.CurrentStatus)
            {
                case StepStatus.Active:
                    label = "►";
                    color = MolcaEditorColors.StatusWarn;
                    break;
                case StepStatus.Completed:
                    label = "✓";
                    color = MolcaEditorColors.StatusOk;
                    break;
                default:
                    label = "○";
                    color = MolcaEditorColors.StatusIdle;
                    break;
            }

            var rect = new Rect(selectionRect.xMax - 18, selectionRect.y, 16, selectionRect.height);
            var prevColor = GUI.color;
            GUI.color = color;
            GUI.Label(rect, label, EditorStyles.boldLabel);
            GUI.color = prevColor;
        }

        private static void DrawSequenceControllerStatus(SequenceController sequenceController, Rect selectionRect)
        {
            string label;
            Color color;
            
            switch (sequenceController.CurrentState)
            {
                case SequenceState.Running:
                    label = "▶";
                    color = MolcaEditorColors.StatusOk;
                    break;
                case SequenceState.Paused:
                    label = "⏸";
                    color = MolcaEditorColors.StatusWarn;
                    break;
                case SequenceState.Completed:
                    label = "✓";
                    color = MolcaEditorColors.Link;
                    break;
                case SequenceState.Idle:
                default:
                    label = "⏹";
                    color = MolcaEditorColors.StatusIdle;
                    break;
            }

            var rect = new Rect(selectionRect.xMax - 18, selectionRect.y, 16, selectionRect.height);
            var prevColor = GUI.color;
            GUI.color = color;
            GUI.Label(rect, label, EditorStyles.boldLabel);
            GUI.color = prevColor;
        }
    }
} 