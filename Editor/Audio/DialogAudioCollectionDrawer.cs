using UnityEngine;
using UnityEditor;
using UnityEngine.AddressableAssets;
using UnityEditor.AddressableAssets;
using Molca.Audio;
using Molca.Settings;
using Molca.Localization;
using System.Linq;
using System.Collections.Generic;

namespace Molca.Editor
{
    [CustomEditor(typeof(DialogAudioCollection))]
    public class DialogAudioCollectionDrawer : UnityEditor.Editor
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
            var collection = (DialogAudioCollection)target;
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
                    EditorGUILayout.HelpBox("No entries found. Click 'Add Entry' to create your first dialog entry.", MessageType.Info);
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
                
                var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
                if (localizationModule == null)
                {
                    EditorGUILayout.HelpBox("LocalizationModule not found in GlobalSettings!", MessageType.Error);
                }
                else
                {
                    EditorGUILayout.LabelField("Available Languages:", string.Join(", ", localizationModule.LanguageCode));
                    
                    if (GUILayout.Button("Validate All Entries", GUILayout.Width(150)))
                    {
                        ValidateCollection(collection);
                    }
                }

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
                    if (GUILayout.Button("Import from Language Folders", GUILayout.Width(200)))
                    {
                        ImportFromLanguageFolders(collection);
                    }
                    EditorGUILayout.HelpBox("Select a folder containing subfolders named by language codes (e.g., 'en', 'ja', 'id'). Audio files in each subfolder will be imported as entries and added to Addressables.", MessageType.Info);
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
                var allIds = collection.GetAllDialogIds();
                EditorGUILayout.LabelField("Total Dialog IDs:", allIds.Length.ToString());
                if (allIds.Length > 0)
                {
                    EditorGUILayout.LabelField("Dialog IDs:", string.Join(", ", allIds));
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void AddNewEntry(DialogAudioCollection collection)
        {
            var newEntry = new LocalizedAudioEntry
            {
                id = $"dialog_{System.Guid.NewGuid().ToString().Substring(0, 8)}",
                description = "New dialog entry"
            };
            
            newEntry.Initialize();
            
            var entriesProp = serializedObject.FindProperty("_entries");
            entriesProp.arraySize++;
            var newEntryProp = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
            
            // Set the new entry properties
            newEntryProp.FindPropertyRelative("id").stringValue = newEntry.id;
            newEntryProp.FindPropertyRelative("description").stringValue = newEntry.description;
            
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(collection);
        }

        private void ImportFromLanguageFolders(DialogAudioCollection collection)
        {
            var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null)
            {
                EditorUtility.DisplayDialog("Import Error", "LocalizationModule not found in GlobalSettings!", "OK");
                return;
            }

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
                groupName = "DialogAudio";
            }

            var group = addressableSettings.FindGroup(groupName);
            if (group == null)
            {
                // Create group with default template
                group = addressableSettings.CreateGroup(groupName, false, false, false, addressableSettings.DefaultGroup.Schemas);
            }

            // Select the root folder
            string rootPath = EditorUtility.OpenFolderPanel("Select Language Folders Root", "Assets", "");
            if (string.IsNullOrEmpty(rootPath))
                return;

            // Convert to relative path if it's within the project
            string relativePath = rootPath;
            if (rootPath.StartsWith(Application.dataPath))
            {
                relativePath = "Assets" + rootPath.Substring(Application.dataPath.Length);
            }

            var languageFolders = new Dictionary<string, string>();
            var availableLanguages = localizationModule.LanguageCode;

            // Find language folders
            foreach (var languageCode in availableLanguages)
            {
                string languageFolderPath = System.IO.Path.Combine(rootPath, languageCode);
                if (System.IO.Directory.Exists(languageFolderPath))
                {
                    languageFolders[languageCode] = languageFolderPath;
                }
            }

            if (languageFolders.Count == 0)
            {
                EditorUtility.DisplayDialog("Import Error", 
                    $"No language folders found in '{rootPath}'. Expected folders: {string.Join(", ", availableLanguages)}", 
                    "OK");
                return;
            }

            // Get all audio files from the first language folder to determine entry names
            var firstLanguagePath = languageFolders.Values.First();
            var audioFiles = System.IO.Directory.GetFiles(firstLanguagePath, "*.wav")
                .Concat(System.IO.Directory.GetFiles(firstLanguagePath, "*.mp3"))
                .Concat(System.IO.Directory.GetFiles(firstLanguagePath, "*.ogg"))
                .Concat(System.IO.Directory.GetFiles(firstLanguagePath, "*.aiff"))
                .ToArray();

            if (audioFiles.Length == 0)
            {
                EditorUtility.DisplayDialog("Import Error", 
                    $"No audio files found in '{firstLanguagePath}'. Supported formats: .wav, .mp3, .ogg, .aiff", 
                    "OK");
                return;
            }

            // Confirm import
            var message = $"Found {audioFiles.Length} audio files across {languageFolders.Count} languages:\n\n" +
                         $"Languages: {string.Join(", ", languageFolders.Keys)}\n" +
                         $"Files per language: {audioFiles.Length}\n\n" +
                         $"This will create {audioFiles.Length} new entries and add them to Addressables group '{groupName}'. Continue?";
            
            if (!EditorUtility.DisplayDialog("Confirm Import", message, "Import", "Cancel"))
                return;

            // Import entries
            var entriesProp = serializedObject.FindProperty("_entries");
            int importedCount = 0;
            int totalFiles = audioFiles.Length;

            for (int fileIndex = 0; fileIndex < audioFiles.Length; fileIndex++)
            {
                var audioFile = audioFiles[fileIndex];
                string fileName = System.IO.Path.GetFileNameWithoutExtension(audioFile);
                string entryId = fileName.ToLower().Replace(" ", "_").Replace("-", "_");

                // Update progress
                float progress = (float)fileIndex / totalFiles;
                string progressTitle = $"Importing Audio Files ({fileIndex + 1}/{totalFiles})";
                string progressInfo = $"Processing: {fileName}";
                
                if (EditorUtility.DisplayCancelableProgressBar(progressTitle, progressInfo, progress))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Import Cancelled", "Audio import was cancelled by the user.", "OK");
                    return;
                }

                // Check if entry already exists
                bool entryExists = false;
                for (int i = 0; i < entriesProp.arraySize; i++)
                {
                    var existingEntry = entriesProp.GetArrayElementAtIndex(i);
                    var existingId = existingEntry.FindPropertyRelative("id").stringValue;
                    if (existingId == entryId)
                    {
                        entryExists = true;
                        break;
                    }
                }

                if (entryExists)
                {
                    Debug.LogWarning($"Entry '{entryId}' already exists, skipping import.");
                    continue;
                }

                // Create new entry
                entriesProp.arraySize++;
                var newEntryProp = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
                
                // Set entry properties
                newEntryProp.FindPropertyRelative("id").stringValue = entryId;
                newEntryProp.FindPropertyRelative("description").stringValue = $"Imported: {fileName}";
                
                // Initialize the language clips array for all available languages
                var languageClipsProp = newEntryProp.FindPropertyRelative("_languageClips");
                languageClipsProp.ClearArray();
                languageClipsProp.arraySize = availableLanguages.Length;
                
                for (int i = 0; i < availableLanguages.Length; i++)
                {
                    var langClipProp = languageClipsProp.GetArrayElementAtIndex(i);
                    langClipProp.FindPropertyRelative("languageCode").stringValue = availableLanguages[i];
                }

                // Add audio clips for each language
                foreach (var languagePair in languageFolders)
                {
                    string languageCode = languagePair.Key;
                    string languagePath = languagePair.Value;
                    
                    // Find corresponding audio file in this language folder
                    string[] extensions = { ".wav", ".mp3", ".ogg", ".aiff" };
                    string audioClipPath = null;
                    
                    foreach (var ext in extensions)
                    {
                        string testPath = System.IO.Path.Combine(languagePath, fileName + ext);
                        if (System.IO.File.Exists(testPath))
                        {
                            audioClipPath = testPath;
                            break;
                        }
                    }

                    if (audioClipPath != null)
                    {
                        // Convert to relative path
                        string relativeAudioPath = audioClipPath;
                        if (audioClipPath.StartsWith(Application.dataPath))
                        {
                            relativeAudioPath = "Assets" + audioClipPath.Substring(Application.dataPath.Length);
                        }

                        // Get the GUID for the audio clip
                        var guid = AssetDatabase.AssetPathToGUID(relativeAudioPath);
                        if (!string.IsNullOrEmpty(guid))
                        {
                            // Add to Addressables in the collection's group
                            var entry = addressableSettings.CreateOrMoveEntry(guid, group);
                            if (entry != null)
                            {
                                // Set the address to be the collection name + language + clip name for better organization
                                entry.address = $"{collection.CollectionName}/{languageCode}/{fileName}";
                                
                                // Create AssetReference from the entry
                                var clipReference = new AssetReferenceT<AudioClip>(guid);
                                
                                // Set the clip reference for this language
                                for (int i = 0; i < languageClipsProp.arraySize; i++)
                                {
                                    var langClip = languageClipsProp.GetArrayElementAtIndex(i);
                                    var langCodeProp = langClip.FindPropertyRelative("languageCode").stringValue;
                                    if (langCodeProp == languageCode)
                                    {
                                        var clipRefProp = langClip.FindPropertyRelative("clipReference");
                                        clipRefProp.FindPropertyRelative("m_AssetGUID").stringValue = guid;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                importedCount++;
            }

            // Clear progress bar
            EditorUtility.ClearProgressBar();

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(collection);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Import Complete", 
                $"Successfully imported {importedCount} entries from language folders and added them to Addressables group '{groupName}'.", 
                "OK");
        }

        private void DeleteSelectedEntries(DialogAudioCollection collection, SerializedProperty entriesProp)
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

        private void ValidateCollection(DialogAudioCollection collection)
        {
            var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
            if (localizationModule == null)
            {
                EditorUtility.DisplayDialog("Validation Error", "LocalizationModule not found in GlobalSettings!", "OK");
                return;
            }

            var entries = collection.GetEntries();
            if (entries == null || entries.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Result", "No entries to validate.", "OK");
                return;
            }

            var missingClips = new System.Collections.Generic.List<string>();
            var languageCodes = localizationModule.LanguageCode;
            int totalEntries = entries.Count;
            int totalChecks = totalEntries * languageCodes.Length;

            EditorUtility.DisplayProgressBar("Validating Collection", "Starting validation...", 0f);

            int currentCheck = 0;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.id))
                {
                    missingClips.Add($"Entry with no ID");
                    currentCheck += languageCodes.Length;
                    continue;
                }

                foreach (var languageCode in languageCodes)
                {
                    float progress = (float)currentCheck / totalChecks;
                    string progressInfo = $"Checking '{entry.id}' for '{languageCode}'";
                    
                    if (EditorUtility.DisplayCancelableProgressBar("Validating Collection", progressInfo, progress))
                    {
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("Validation Cancelled", "Collection validation was cancelled by the user.", "OK");
                        return;
                    }

                    if (!entry.HasClipForLanguage(languageCode))
                    {
                        missingClips.Add($"'{entry.id}' missing clip for '{languageCode}'");
                    }
                    
                    currentCheck++;
                }
            }

            EditorUtility.ClearProgressBar();

            if (missingClips.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Result", "All entries are valid! All required audio clips are present.", "OK");
            }
            else
            {
                var message = "Validation found issues:\n\n" + string.Join("\n", missingClips);
                EditorUtility.DisplayDialog("Validation Result", message, "OK");
            }
        }
    }
} 