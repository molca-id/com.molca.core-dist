using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEditor;
using UnityEditor.AddressableAssets;
using System.Linq;
using System.Collections.Generic;
using Molca.Audio;

namespace Molca.Editor
{
    [CustomEditor(typeof(AudioCollection))]
    public class AudioCollectionEditor : UnityEditor.Editor
    {
        private bool showBasicInfo = true;
        private bool showEntries = true;
        private bool showValidation = true;
        private bool showTools = true;
        private HashSet<int> selectedEntries = new HashSet<int>();
        
        // Pagination settings
        private const int ENTRIES_PER_PAGE = 10;
        private int currentPage = 0;
        private int totalPages = 0;

        public override void OnInspectorGUI()
        {
            var collection = (AudioCollection)target;
            serializedObject.Update();

            EditorGUILayout.Space(5);

            // Basic Information Section
            showBasicInfo = EditorGUILayout.Foldout(showBasicInfo, "Basic Information", true);
            if (showBasicInfo)
            {
                EditorGUI.indentLevel++;
                
                var collectionNameProp = serializedObject.FindProperty("_collectionName");
                var descriptionProp = serializedObject.FindProperty("_description");
                var addressableGroupNameProp = serializedObject.FindProperty("_addressableGroupName");

                EditorGUILayout.PropertyField(collectionNameProp, new GUIContent("Collection Name"));
                EditorGUILayout.PropertyField(descriptionProp, new GUIContent("Description"));
                EditorGUILayout.PropertyField(addressableGroupNameProp, new GUIContent("Addressable Group"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            // Entries Section
            showEntries = EditorGUILayout.Foldout(showEntries, $"Entries ({collection.GetEntries()?.Count ?? 0})", true);
            if (showEntries)
            {
                EditorGUI.indentLevel++;
                
                var entriesProp = serializedObject.FindProperty("_entries");
                
                // Entry management buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Add Entry", GUILayout.Width(100)))
                {
                    AddNewEntry(collection);
                }
                if (GUILayout.Button("Clear All", GUILayout.Width(100)))
                {
                    if (EditorUtility.DisplayDialog("Clear All Entries", 
                        "Are you sure you want to clear all entries? This action cannot be undone.", 
                        "Clear All", "Cancel"))
                    {
                        EditorUtility.DisplayProgressBar("Clearing Entries", "Clearing all entries...", 0f);
                        
                        // Clear entries with progress
                        int totalEntries = entriesProp.arraySize;
                        for (int i = 0; i < totalEntries; i++)
                        {
                            float progress = (float)i / totalEntries;
                            string progressInfo = $"Clearing entry {i + 1} of {totalEntries}";
                            
                            if (EditorUtility.DisplayCancelableProgressBar("Clearing Entries", progressInfo, progress))
                            {
                                EditorUtility.ClearProgressBar();
                                EditorUtility.DisplayDialog("Clear Cancelled", "Entry clearing was cancelled by the user.", "OK");
                                return;
                            }
                        }
                        
                        EditorUtility.ClearProgressBar();
                        entriesProp.ClearArray();
                        selectedEntries.Clear();
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Batch selection controls
                if (entriesProp.arraySize > 0)
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Select All", GUILayout.Width(100)))
                    {
                        selectedEntries.Clear();
                        for (int i = 0; i < entriesProp.arraySize; i++)
                        {
                            selectedEntries.Add(i);
                        }
                    }
                    if (GUILayout.Button("Select Page", GUILayout.Width(100)))
                    {
                        int startIndex = currentPage * ENTRIES_PER_PAGE;
                        int endIndex = Mathf.Min(startIndex + ENTRIES_PER_PAGE, entriesProp.arraySize);
                        for (int i = startIndex; i < endIndex; i++)
                        {
                            selectedEntries.Add(i);
                        }
                    }
                    if (GUILayout.Button("Clear Selection", GUILayout.Width(100)))
                    {
                        selectedEntries.Clear();
                    }
                    if (selectedEntries.Count > 0)
                    {
                        if (GUILayout.Button($"Delete Selected ({selectedEntries.Count})", GUILayout.Width(150)))
                        {
                            DeleteSelectedEntries(collection, entriesProp);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(5);

                // Calculate pagination
                totalPages = Mathf.Max(1, Mathf.CeilToInt((float)entriesProp.arraySize / ENTRIES_PER_PAGE));
                currentPage = Mathf.Clamp(currentPage, 0, totalPages - 1);
                
                // Display entries
                if (entriesProp.arraySize == 0)
                {
                    EditorGUILayout.HelpBox("No entries found. Click 'Add Entry' to create your first audio entry or use 'Import from Folder' to add multiple entries.", MessageType.Info);
                }
                else
                {
                    // Pagination info
                    EditorGUILayout.LabelField($"Showing entries {currentPage * ENTRIES_PER_PAGE + 1} - {Mathf.Min((currentPage + 1) * ENTRIES_PER_PAGE, entriesProp.arraySize)} of {entriesProp.arraySize}", EditorStyles.miniLabel);
                    
                    // Pagination controls
                    EditorGUILayout.BeginHorizontal();
                    GUI.enabled = currentPage > 0;
                    if (GUILayout.Button("← Previous", GUILayout.Width(80)))
                    {
                        currentPage--;
                    }
                    GUI.enabled = true;
                    
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"Page {currentPage + 1} of {totalPages}", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    
                    GUI.enabled = currentPage < totalPages - 1;
                    if (GUILayout.Button("Next →", GUILayout.Width(80)))
                    {
                        currentPage++;
                    }
                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();
                    
                    EditorGUILayout.Space(5);
                    
                    // Display entries for current page
                    int startIndex = currentPage * ENTRIES_PER_PAGE;
                    int endIndex = Mathf.Min(startIndex + ENTRIES_PER_PAGE, entriesProp.arraySize);
                    
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        var entryProp = entriesProp.GetArrayElementAtIndex(i);
                        var idProp = entryProp.FindPropertyRelative("id");
                        
                        EditorGUILayout.BeginVertical("box");
                        
                        // Entry header with selection checkbox and remove button
                        EditorGUILayout.BeginHorizontal();
                        
                        // Selection checkbox
                        bool isSelected = selectedEntries.Contains(i);
                        bool newSelection = EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
                        if (newSelection != isSelected)
                        {
                            if (newSelection)
                                selectedEntries.Add(i);
                            else
                                selectedEntries.Remove(i);
                        }
                        
                        var entryLabel = string.IsNullOrEmpty(idProp.stringValue) ? $"Entry {i + 1}" : idProp.stringValue;
                        EditorGUILayout.LabelField(entryLabel, EditorStyles.boldLabel);
                        
                        if (GUILayout.Button("Remove", GUILayout.Width(60)))
                        {
                            if (EditorUtility.DisplayDialog("Remove Entry", 
                                $"Are you sure you want to remove entry '{entryLabel}'?", 
                                "Remove", "Cancel"))
                            {
                                entriesProp.DeleteArrayElementAtIndex(i);
                                selectedEntries.Remove(i);
                                
                                // Adjust current page if we deleted the last entry on the current page
                                if (i >= entriesProp.arraySize && currentPage > 0)
                                {
                                    currentPage--;
                                }
                                break;
                            }
                        }
                        EditorGUILayout.EndHorizontal();

                        // Entry content
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(entryProp, GUIContent.none, true);
                        EditorGUI.indentLevel--;
                        
                        EditorGUILayout.EndVertical();
                        EditorGUILayout.Space(2);
                    }
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            // Validation Section
            showValidation = EditorGUILayout.Foldout(showValidation, "Validation", true);
            if (showValidation)
            {
                EditorGUI.indentLevel++;
                
                if (GUILayout.Button("Validate All Entries", GUILayout.Width(150)))
                {
                    ValidateCollection(collection);
                }
                
                EditorGUILayout.HelpBox("Validation checks that all entries have valid audio clip references and are properly configured.", MessageType.Info);

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            // Tools Section
            showTools = EditorGUILayout.Foldout(showTools, "Tools", true);
            if (showTools)
            {
                EditorGUI.indentLevel++;
                
                // Import section
                EditorGUILayout.LabelField("Import Audio", EditorStyles.boldLabel);
                
                if (string.IsNullOrEmpty(collection.AddressableGroupName))
                {
                    EditorGUILayout.HelpBox("Please specify an Addressable Group Name in the Basic Information section before importing audio clips.", MessageType.Warning);
                }
                else
                {
                    if (GUILayout.Button("Import from Folder", GUILayout.Width(200)))
                    {
                        ImportFromFolder(collection);
                    }
                    EditorGUILayout.HelpBox("Select a folder containing audio files. All audio files in the folder will be imported as entries and added to Addressables.", MessageType.Info);
                }
                
                EditorGUILayout.Space(10);
                
                // Addressable Group Management
                EditorGUILayout.LabelField("Addressable Group Management", EditorStyles.boldLabel);
                
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings != null && !string.IsNullOrEmpty(collection.AddressableGroupName))
                {
                    var group = settings.FindGroup(collection.AddressableGroupName);
                    
                    if (group != null)
                    {
                        if (GUILayout.Button("Open Addressable Group"))
                        {
                            Selection.activeObject = group;
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"Group '{collection.AddressableGroupName}' doesn't exist yet. It will be created when you import audio clips.", MessageType.Info);
                    }
                }
                
                EditorGUILayout.Space(10);
                
                // Show collection info
                EditorGUILayout.LabelField("Collection Info", EditorStyles.boldLabel);
                var allIds = collection.GetAllAudioIds();
                EditorGUILayout.LabelField("Total Audio IDs:", allIds.Length.ToString());
                if (allIds.Length > 0)
                {
                    EditorGUILayout.LabelField("Audio IDs:", string.Join(", ", allIds));
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void AddNewEntry(AudioCollection collection)
        {
            var newEntry = new AudioCollection.AudioEntry
            {
                id = $"audio_{System.Guid.NewGuid().ToString().Substring(0, 8)}",
                description = "New audio entry"
            };
            
            var entriesProp = serializedObject.FindProperty("_entries");
            entriesProp.arraySize++;
            var newEntryProp = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
            
            // Set the new entry properties
            newEntryProp.FindPropertyRelative("id").stringValue = newEntry.id;
            newEntryProp.FindPropertyRelative("description").stringValue = newEntry.description;
            
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(collection);
        }

        private void ImportFromFolder(AudioCollection collection)
        {
            // Check if Addressables settings are available
            var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings == null)
            {
                EditorUtility.DisplayDialog("Import Error", "Addressables settings not found. Please initialize Addressables first.", "OK");
                return;
            }

            // Get or create the addressable group for this collection
            string groupName = collection.AddressableGroupName;
            if (string.IsNullOrEmpty(groupName))
            {
                groupName = "AudioCollection";
            }

            var group = addressableSettings.FindGroup(groupName);
            if (group == null)
            {
                // Create group with default template
                group = addressableSettings.CreateGroup(groupName, false, false, false, addressableSettings.DefaultGroup.Schemas);
            }

            // Select the folder
            string folder = EditorUtility.OpenFolderPanel("Select Audio Folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder))
                return;

            // Convert absolute path to relative (Assets/...)
            string relPath = folder;
            if (relPath.StartsWith(Application.dataPath))
                relPath = "Assets" + relPath.Substring(Application.dataPath.Length);

            // Find all audio clips in the folder
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { relPath });
            var existingIds = collection.GetEntries().Select(e => e.id).ToHashSet();

            if (guids.Length == 0)
            {
                EditorUtility.DisplayDialog("Import Error", 
                    $"No audio files found in '{relPath}'. Supported formats: .wav, .mp3, .ogg, .aiff", 
                    "OK");
                return;
            }

            // Confirm import
            var message = $"Found {guids.Length} audio files in '{relPath}'.\n\n" +
                         $"This will create {guids.Length} new entries and add them to Addressables group '{groupName}'. Continue?";
            
            if (!EditorUtility.DisplayDialog("Confirm Import", message, "Import", "Cancel"))
                return;

            // Import entries with progress
            var entriesProp = serializedObject.FindProperty("_entries");
            int added = 0;
            int totalFiles = guids.Length;

            EditorUtility.DisplayProgressBar("Importing Audio Files", "Starting import...", 0f);

            for (int i = 0; i < guids.Length; i++)
            {
                var guid = guids[i];
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                
                // Update progress
                float progress = (float)i / totalFiles;
                string progressInfo = $"Processing: {clip?.name ?? "Unknown"}";
                
                if (EditorUtility.DisplayCancelableProgressBar("Importing Audio Files", progressInfo, progress))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Import Cancelled", "Audio import was cancelled by the user.", "OK");
                    return;
                }

                if (clip != null && !existingIds.Contains(clip.name))
                {
                    // Add to Addressables in the collection's group
                    var entry = addressableSettings.CreateOrMoveEntry(guid, group);
                    if (entry != null)
                    {
                        // Set the address to be the collection name + clip name for better organization
                        entry.address = $"{collection.CollectionName}/{clip.name}";
                        
                        // Create AssetReference from the entry
                        var clipReference = new AssetReferenceT<AudioClip>(guid);

                        Undo.RecordObject(collection, "Add Audio Entry");
                        collection.AddEntry(clip.name, clipReference);
                        added++;
                    }
                }
            }

            EditorUtility.ClearProgressBar();

            if (added > 0)
            {
                EditorUtility.SetDirty(collection);
                AssetDatabase.SaveAssets();
                EditorUtility.DisplayDialog("Import Complete", 
                    $"Successfully imported {added} new audio entries to collection '{collection.CollectionName}' and added them to Addressables group '{groupName}'.", 
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Import Complete", "No new audio clips found to add as entries.", "OK");
            }
        }

        private void DeleteSelectedEntries(AudioCollection collection, SerializedProperty entriesProp)
        {
            if (selectedEntries.Count == 0) return;

            var selectedEntryNames = new List<string>();
            var orderedIndices = selectedEntries.OrderByDescending(x => x).ToList();
            
            foreach (int index in orderedIndices)
            {
                if (index < entriesProp.arraySize)
                {
                    var entryProp = entriesProp.GetArrayElementAtIndex(index);
                    var idProp = entryProp.FindPropertyRelative("id");
                    var entryName = string.IsNullOrEmpty(idProp.stringValue) ? $"Entry {index + 1}" : idProp.stringValue;
                    selectedEntryNames.Add(entryName);
                }
            }

            var message = $"Are you sure you want to delete {selectedEntries.Count} selected entries?\n\n" +
                         string.Join("\n", selectedEntryNames);
            
            if (EditorUtility.DisplayDialog("Delete Selected Entries", message, "Delete", "Cancel"))
            {
                EditorUtility.DisplayProgressBar("Deleting Entries", "Preparing to delete entries...", 0f);
                
                // Delete from highest index to lowest to avoid index shifting issues
                for (int i = 0; i < orderedIndices.Count; i++)
                {
                    int index = orderedIndices[i];
                    float progress = (float)i / orderedIndices.Count;
                    string progressInfo = $"Deleting entry {i + 1} of {orderedIndices.Count}";
                    
                    if (EditorUtility.DisplayCancelableProgressBar("Deleting Entries", progressInfo, progress))
                    {
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("Delete Cancelled", "Entry deletion was cancelled by the user.", "OK");
                        return;
                    }
                    
                    if (index < entriesProp.arraySize)
                    {
                        entriesProp.DeleteArrayElementAtIndex(index);
                    }
                }
                
                EditorUtility.ClearProgressBar();
                selectedEntries.Clear();
                
                // Adjust current page if we deleted all entries from the current page
                int newTotalPages = Mathf.Max(1, Mathf.CeilToInt((float)entriesProp.arraySize / ENTRIES_PER_PAGE));
                if (currentPage >= newTotalPages && currentPage > 0)
                {
                    currentPage = newTotalPages - 1;
                }
                
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(collection);
            }
        }

        private void ValidateCollection(AudioCollection collection)
        {
            var entries = collection.GetEntries();
            if (entries == null || entries.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Result", "No entries to validate.", "OK");
                return;
            }

            var invalidEntries = new List<string>();
            int totalEntries = entries.Count;

            EditorUtility.DisplayProgressBar("Validating Collection", "Starting validation...", 0f);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                float progress = (float)i / totalEntries;
                string progressInfo = $"Checking '{entry.id}'";
                
                if (EditorUtility.DisplayCancelableProgressBar("Validating Collection", progressInfo, progress))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Validation Cancelled", "Collection validation was cancelled by the user.", "OK");
                    return;
                }

                if (string.IsNullOrEmpty(entry.id))
                {
                    invalidEntries.Add($"Entry with no ID");
                    continue;
                }

                if (entry.clipReference == null || !entry.clipReference.RuntimeKeyIsValid())
                {
                    invalidEntries.Add($"'{entry.id}' has invalid or missing audio clip reference");
                }
            }

            EditorUtility.ClearProgressBar();

            if (invalidEntries.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Result", "All entries are valid! All audio clips are properly referenced.", "OK");
            }
            else
            {
                var message = "Validation found issues:\n\n" + string.Join("\n", invalidEntries);
                EditorUtility.DisplayDialog("Validation Result", message, "OK");
            }
        }
    }
} 