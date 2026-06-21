using UnityEngine;
using UnityEditor;
using Molca.Sequence;

namespace Molca.Editor
{
    [CustomEditor(typeof(SequenceController))]
    public class SequenceControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying) return;

            var controller = target as SequenceController;
            if (controller == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Information", EditorStyles.boldLabel);

            // Current step information
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Current Step:", GUILayout.Width(120));
            if (controller.CurrentStep != null)
            {
                EditorGUILayout.LabelField(controller.CurrentStep.name);
            }
            else
            {
                EditorGUILayout.LabelField("None");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Total Steps:", GUILayout.Width(120));
            var stepsCount = controller.Steps?.Count ?? 0;
            EditorGUILayout.LabelField(stepsCount.ToString());
            EditorGUILayout.EndHorizontal();

            // Timing information
            if (controller.SequenceStartTime != default)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Start Time:", GUILayout.Width(120));
                EditorGUILayout.LabelField(controller.SequenceStartTime.ToString("HH:mm:ss"));
                EditorGUILayout.EndHorizontal();
            }

            if (controller.SequenceFinishTime != default)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Finish Time:", GUILayout.Width(120));
                EditorGUILayout.LabelField(controller.SequenceFinishTime.ToString("HH:mm:ss"));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Duration:", GUILayout.Width(120));
                EditorGUILayout.LabelField(controller.TimeTaken.ToString(@"mm\:ss\.fff"));
                EditorGUILayout.EndHorizontal();
            }

            // Action buttons
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Start Sequence"))
            {
                controller.StartSequence();
            }
            if (GUILayout.Button("Stop Sequence"))
            {
                controller.StopSequence();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Restart Sequence"))
            {
                controller.RestartSequence();
            }
            if (GUILayout.Button("Complete Current Step"))
            {
                controller.CompleteCurrentStep();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
} 