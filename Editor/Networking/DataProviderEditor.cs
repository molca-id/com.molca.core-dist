using UnityEngine;
using UnityEditor;
using Molca.Networking.Data;

namespace Molca.Networking.Data.Editor
{
    /// <summary>
    /// Custom editor for DataProvider assets
    /// Provides configuration validation and provider status
    /// </summary>
    [CustomEditor(typeof(DataProvider), true)]
    public class DataProviderEditor : UnityEditor.Editor
    {
        private string validationDetails = "";
        private Vector2 validationScroll;
        private bool showValidationDetails = false;
        
        public override void OnInspectorGUI()
        {
            var provider = (DataProvider)target;
            
            // Draw default inspector but handle chunking fields specially
            DrawDefaultInspectorWithChunkingControl(provider);
            
            EditorGUILayout.Space(10);
            
            // Provider Status
            DrawProviderStatus(provider);
        }
        
        /// <summary>
        /// Draws the default inspector with conditional chunking field visibility
        /// </summary>
        private void DrawDefaultInspectorWithChunkingControl(DataProvider provider)
        {
            // Get the serialized object to access private fields
            var serializedObject = new SerializedObject(provider);
            var iterator = serializedObject.GetIterator();
            
            // Draw the first property (usually the script field)
            if (iterator.NextVisible(true))
            {
                EditorGUILayout.PropertyField(iterator, true);
            }
            
            // Draw remaining properties with chunking control
            while (iterator.NextVisible(false))
            {
                var property = serializedObject.FindProperty(iterator.name);
                if (property != null)
                {
                    // Check if this is a chunking-related field
                    bool isChunkingField = IsChunkingField(property.name);
                    
                    // Only draw chunking fields if chunking is enabled
                    if (!isChunkingField || IsChunkingEnabled(provider))
                    {
                        EditorGUILayout.PropertyField(property, true);
                    }
                }
            }
            
            // Apply any changes
            serializedObject.ApplyModifiedProperties();
        }
        
        /// <summary>
        /// Checks if a property is related to chunking
        /// </summary>
        private bool IsChunkingField(string propertyName)
        {
            return propertyName == "chunkSize" || propertyName == "chunkDelayMs";
        }
        
        /// <summary>
        /// Checks if chunking is enabled on the provider
        /// </summary>
        private bool IsChunkingEnabled(DataProvider provider)
        {
            // Use reflection to access the private enableChunking field
            var field = typeof(DataProvider).GetField("enableChunking", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (field != null)
            {
                return (bool)field.GetValue(provider);
            }
            
            return false;
        }
        
        private void DrawProviderStatus(DataProvider provider)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Provider Status", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Active:", GUILayout.Width(100));
            if (Application.isPlaying)
            {
                if (provider.IsActive)
                {
                    EditorGUILayout.LabelField("✓ Active", EditorStyles.boldLabel);
                    GUI.color = Color.green;
                }
                else
                {
                    EditorGUILayout.LabelField("✗ Inactive", EditorStyles.boldLabel);
                    GUI.color = Color.red;
                }
                GUI.color = Color.white;
            }
            else
            {
                EditorGUILayout.LabelField("Not in Play Mode", EditorStyles.helpBox);
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Configuration:", GUILayout.Width(100));
            
            // Validate configuration and show details
            bool configValid = ValidateConfigurationWithDetails(provider);
            
            if (configValid)
            {
                EditorGUILayout.LabelField("✓ Valid", EditorStyles.boldLabel);
                GUI.color = Color.green;
            }
            else
            {
                EditorGUILayout.LabelField("✗ Invalid", EditorStyles.boldLabel);
                GUI.color = Color.red;
            }
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();
            
            // Show validation details if there are issues
            if (!configValid && !string.IsNullOrEmpty(validationDetails))
            {
                EditorGUILayout.Space(5);
                showValidationDetails = EditorGUILayout.Foldout(showValidationDetails, "Configuration Issues", true);
                
                if (showValidationDetails)
                {
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.HelpBox("The following configuration issues were found:", MessageType.Warning);
                    
                    validationScroll = EditorGUILayout.BeginScrollView(validationScroll, GUILayout.Height(80));
                    EditorGUILayout.TextArea(validationDetails, EditorStyles.helpBox);
                    EditorGUILayout.EndScrollView();
                    
                    EditorGUILayout.EndVertical();
                }
            }
            
            EditorGUILayout.EndVertical();
        }
        
        private bool ValidateConfigurationWithDetails(DataProvider provider)
        {
            validationDetails = "";
            bool isValid = true;
            
            // Check DataMapping
            if (provider.Mapping == null)
            {
                validationDetails += "• DataMapping is not assigned\n";
                isValid = false;
            }
            else if (provider.Mapping.Model == null)
            {
                validationDetails += "• DataMapping.Model is not assigned\n";
                isValid = false;
            }
            
            // Check JsonPreProcessor (optional, but warn if missing)
            if (provider.JsonPreProcessor == null)
            {
                validationDetails += "• JsonPreProcessor is not assigned (optional but recommended)\n";
                // Don't fail validation for this, just warn
            }
            
            // Check ProviderId
            if (string.IsNullOrEmpty(provider.ProviderId))
            {
                validationDetails += "• ProviderId is empty or null\n";
                isValid = false;
            }
            
            // Check ProviderName
            if (string.IsNullOrEmpty(provider.ProviderName))
            {
                validationDetails += "• ProviderName is empty or null\n";
                isValid = false;
            }
            
            // If everything is valid, show success message
            if (isValid)
            {
                validationDetails = "✓ All configuration checks passed successfully!";
            }
            
            return isValid;
        }
    }
}
