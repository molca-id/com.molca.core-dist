using UnityEngine;
using UnityEditor;
using Molca.Audio;

namespace Molca.Editor
{
    [CustomEditor(typeof(AudioLibrary))]
    public class AudioLibraryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            AudioLibrary audioLibrary = (AudioLibrary)target;
            
            // Draw the default inspector
            DrawDefaultInspector();
            
            // Show validation warning if needed
            if (audioLibrary.HasVoiceValidationError)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Voice Audio Libraries should only contain Dialog Audio Collections for proper localized audio support. " +
                    "Regular Audio Collections in Voice libraries will not support language switching.",
                    MessageType.Warning
                );
                
                // Add a button to manually fix the issue
                if (GUILayout.Button("Remove Non-Dialog Collections"))
                {
                    if (EditorUtility.DisplayDialog("Remove Non-Dialog Collections", 
                        "This will permanently remove all non-Dialog Audio Collections from this Voice library. Are you sure?", 
                        "Yes, Remove", "Cancel"))
                    {
                        Undo.RecordObject(audioLibrary, "Remove Non-Dialog Collections");
                        audioLibrary.RemoveNonDialogCollections();
                        EditorUtility.SetDirty(audioLibrary);
                    }
                }
            }
            
            // Show info box for Voice libraries to guide users
            if (audioLibrary.Type == AudioLibrary.AudioType.Voice)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Voice libraries are designed for localized dialog audio. Use Dialog Audio Collections to support multiple languages (English and Indonesian).",
                    MessageType.Info
                );
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Collection Management", EditorStyles.boldLabel);

            if (GUILayout.Button("Create New Collection"))
            {
                CreateNewCollection(audioLibrary);
            }
        }

        private void CreateNewCollection(AudioLibrary library)
        {
            // Determine collection type based on library type
            bool isVoiceLibrary = library.Type == AudioLibrary.AudioType.Voice;
            string collectionTypeName = isVoiceLibrary ? "Dialog Audio Collection" : "Audio Collection";
            string defaultFileName = isVoiceLibrary ? "NewDialogAudioCollection" : "NewAudioCollection";

            string path = EditorUtility.SaveFilePanelInProject(
                $"Create New {collectionTypeName}",
                defaultFileName,
                "asset",
                $"Please enter a name for the new {collectionTypeName.ToLower()}"
            );

            if (!string.IsNullOrEmpty(path))
            {
                ScriptableObject collection;
                
                if (isVoiceLibrary)
                {
                    collection = CreateInstance<DialogAudioCollection>();
                }
                else
                {
                    collection = CreateInstance<AudioCollection>();
                }

                AssetDatabase.CreateAsset(collection, path);
                AssetDatabase.SaveAssets();

                Undo.RecordObject(library, $"Add {collectionTypeName}");
                if (collection is IAudioCollection audioCollection)
                {
                    library.AddCollection(audioCollection);
                }
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssets();
            }
        }
    }
} 