using UnityEngine;
using UnityEditor;
using Molca.Audio;
using Molca.Settings;
using System.Linq;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(DialogAudioReference))]
    public class DialogAudioReferenceDrawer : PropertyDrawer
    {
        const float toggleWidth = 20f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            
            // Manually define the label and control rects to ensure consistent alignment.
            var labelRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, position.height);
            var controlRect = new Rect(labelRect.xMax, position.y, position.width - EditorGUIUtility.labelWidth, position.height);
            EditorGUI.LabelField(labelRect, label);

            var collectionNameProp = property.FindPropertyRelative("_collectionName");
            var dialogIdProp = property.FindPropertyRelative("_dialogId");
            var enabledProp = property.FindPropertyRelative("_enabled");

            // Define rects for toggle and dropdown
            var toggleRect = new Rect(controlRect.x, controlRect.y, toggleWidth, controlRect.height);
            var dropdownRect = new Rect(toggleRect.xMax, controlRect.y, controlRect.width - toggleWidth, controlRect.height);

            // --- Draw UI ---
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);
            EditorGUI.BeginDisabledGroup(!enabledProp.boolValue);

            DrawDialogDropdown(dropdownRect, collectionNameProp, dialogIdProp);

            EditorGUI.EndDisabledGroup();
            EditorGUI.EndProperty();
        }

        private void DrawDialogDropdown(Rect position, SerializedProperty collectionNameProp, SerializedProperty dialogIdProp)
        {
            var audioModule = GlobalSettings.GetModule<AudioModule>();
            var voiceLibrary = audioModule?.VoiceLibrary;
            var collections = voiceLibrary != null 
                ? voiceLibrary.GetCollections().OfType<DialogAudioCollection>().ToList() 
                : new System.Collections.Generic.List<DialogAudioCollection>();

            // Build display options with collection/id grouping
            var displayOptions = new System.Collections.Generic.List<string>();
            var dialogDataMap = new System.Collections.Generic.List<(string collectionName, string dialogId)>();

            foreach (var collection in collections)
            {
                if (collection == null) continue;

                var dialogIds = collection.GetAllDialogIds();
                foreach (var dialogId in dialogIds)
                {
                    displayOptions.Add($"{collection.CollectionName}/{dialogId}");
                    dialogDataMap.Add((collection.CollectionName, dialogId));
                }
            }

            if (displayOptions.Count == 0)
            {
                EditorGUI.LabelField(position, "No dialog audio available");
                return;
            }

            // Find current index
            string currentCollectionName = collectionNameProp.stringValue;
            string currentDialogId = dialogIdProp.stringValue;
            int currentIndex = -1;

            for (int i = 0; i < dialogDataMap.Count; i++)
            {
                if (dialogDataMap[i].collectionName == currentCollectionName && 
                    dialogDataMap[i].dialogId == currentDialogId)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex == -1 && dialogDataMap.Count > 0)
            {
                currentIndex = 0;
                collectionNameProp.stringValue = dialogDataMap[0].collectionName;
                dialogIdProp.stringValue = dialogDataMap[0].dialogId;
            }

            // Draw dropdown
            int newIndex = EditorGUI.Popup(position, currentIndex, displayOptions.ToArray());
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < dialogDataMap.Count)
            {
                collectionNameProp.stringValue = dialogDataMap[newIndex].collectionName;
                dialogIdProp.stringValue = dialogDataMap[newIndex].dialogId;
                collectionNameProp.serializedObject.ApplyModifiedProperties();
            }
        }
    }
}