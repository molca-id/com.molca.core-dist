using UnityEngine;
using UnityEditor;
using Molca.Settings;
using Molca.Audio;
using System.Linq;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(AudioReference))]
    public class AudioReferenceDrawer : PropertyDrawer
    {
        const float toggleWidth = 20f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            // Define label and control rects
            var labelRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, position.height);
            var controlRect = new Rect(labelRect.xMax, position.y, position.width - EditorGUIUtility.labelWidth, position.height);
            EditorGUI.LabelField(labelRect, label);

            var collectionNameProp = property.FindPropertyRelative("_collectionName");
            var audioIdProp = property.FindPropertyRelative("_audioId");
            var audioTypeProp = property.FindPropertyRelative("_audioType");
            var enabledProp = property.FindPropertyRelative("_enabled");

            // Define rects for toggle and dropdown
            var toggleRect = new Rect(controlRect.x, controlRect.y, toggleWidth, controlRect.height);
            var dropdownRect = new Rect(toggleRect.xMax, controlRect.y, controlRect.width - toggleWidth, controlRect.height);

            // Draw UI
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);
            EditorGUI.BeginDisabledGroup(!enabledProp.boolValue);

            DrawAudioDropdown(dropdownRect, collectionNameProp, audioIdProp, audioTypeProp);

            EditorGUI.EndDisabledGroup();
            EditorGUI.EndProperty();
        }

        private void DrawAudioDropdown(Rect position, SerializedProperty collectionNameProp, SerializedProperty audioIdProp, SerializedProperty audioTypeProp)
        {
            var audioModule = GlobalSettings.GetModule<AudioModule>();
            if (audioModule == null)
            {
                EditorGUI.LabelField(position, "No audio module found");
                return;
            }

            // Build display options with type/collection/id grouping
            var displayOptions = new System.Collections.Generic.List<string>();
            var audioDataMap = new System.Collections.Generic.List<(AudioLibrary.AudioType audioType, string collectionName, string audioId)>();

            var audioTypes = new[] { AudioLibrary.AudioType.Music, AudioLibrary.AudioType.SFX, AudioLibrary.AudioType.Voice };
            
            foreach (var audioType in audioTypes)
            {
                var library = GetLibraryForType(audioType);
                if (library == null) continue;

                var collections = library.GetCollections();
                foreach (var collection in collections)
                {
                    var audioIds = collection.GetAllAudioIds();
                    foreach (var audioId in audioIds)
                    {
                        displayOptions.Add($"{audioType}/{collection.CollectionName}/{audioId}");
                        audioDataMap.Add((audioType, collection.CollectionName, audioId));
                    }
                }
            }

            if (displayOptions.Count == 0)
            {
                EditorGUI.LabelField(position, "No audio available");
                return;
            }

            // Find current index
            AudioLibrary.AudioType currentAudioType = (AudioLibrary.AudioType)audioTypeProp.enumValueIndex;
            string currentCollectionName = collectionNameProp.stringValue;
            string currentAudioId = audioIdProp.stringValue;
            int currentIndex = -1;
            
            for (int i = 0; i < audioDataMap.Count; i++)
            {
                if (audioDataMap[i].audioType == currentAudioType && 
                    audioDataMap[i].collectionName == currentCollectionName && 
                    audioDataMap[i].audioId == currentAudioId)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex == -1 && audioDataMap.Count > 0)
            {
                currentIndex = 0;
                audioTypeProp.enumValueIndex = (int)audioDataMap[0].audioType;
                collectionNameProp.stringValue = audioDataMap[0].collectionName;
                audioIdProp.stringValue = audioDataMap[0].audioId;
            }

            // Draw dropdown
            int newIndex = EditorGUI.Popup(position, currentIndex, displayOptions.ToArray());
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < audioDataMap.Count)
            {
                audioTypeProp.enumValueIndex = (int)audioDataMap[newIndex].audioType;
                collectionNameProp.stringValue = audioDataMap[newIndex].collectionName;
                audioIdProp.stringValue = audioDataMap[newIndex].audioId;
                audioTypeProp.serializedObject.ApplyModifiedProperties();
            }
        }

        private AudioLibrary GetLibraryForType(AudioLibrary.AudioType type)
        {
            var audioModule = GlobalSettings.GetModule<AudioModule>();
            if (audioModule == null) return null;

            return type switch
            {
                AudioLibrary.AudioType.Music => audioModule.MusicLibrary,
                AudioLibrary.AudioType.SFX => audioModule.SFXLibrary,
                AudioLibrary.AudioType.Voice => audioModule.VoiceLibrary,
                _ => null,
            };
        }
    }
}