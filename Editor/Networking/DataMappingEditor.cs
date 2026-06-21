using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Molca.Networking.Data;
using Molca.Editor.UI;

namespace Molca.Editor
{
    [CustomEditor(typeof(DataMapping))]
    public class DataMappingEditor : UnityEditor.Editor
    {
        private SerializedProperty _modelProperty;
        private SerializedProperty _mappingIdProperty;
        private SerializedProperty _mappingNameProperty;
        private SerializedProperty _fieldsProperty;
        private DataMapping _dataMapping;
        private DataModel _previousModel;

        // JSON Path Discovery
        private string _sampleJson = "";
        private List<string> _availableJsonPaths = new List<string>();
        private Vector2 _jsonScrollPos;
        private bool _showJsonDiscovery = false;

        private void OnEnable()
        {
            _modelProperty = serializedObject.FindProperty("_model");
            _mappingIdProperty = serializedObject.FindProperty("_mappingId");
            _mappingNameProperty = serializedObject.FindProperty("_mappingName");
            _fieldsProperty = serializedObject.FindProperty("_fields");
            _dataMapping = (DataMapping)target;
            _previousModel = _dataMapping.Model;
            
            // Check if we need to generate fields on enable
            if (_dataMapping.Model != null && _fieldsProperty.arraySize == 0)
            {
                GenerateMappingFields();
            }
            
            // Always refresh fields when editor is enabled to pick up any DataModel changes
            if (_dataMapping.Model != null)
            {
                RefreshMappingFields();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Check if the DataModel has changed and refresh fields if needed
            if (_previousModel != _dataMapping.Model)
            {
                RefreshMappingFields();
                _previousModel = _dataMapping.Model;
            }

            // Mapping ID
            EditorGUILayout.PropertyField(_mappingIdProperty, new GUIContent("Mapping ID", "Unique identifier for this data mapping"));

            // Mapping Name
            EditorGUILayout.PropertyField(_mappingNameProperty, new GUIContent("Mapping Name", "Human-readable name for this data mapping"));

            EditorGUILayout.Space();

            // Model assignment
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_modelProperty, new GUIContent("Data Model", "Assign a DataModel to automatically generate mapping fields"));
            
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                UpdateMappingFields();
            }

            EditorGUILayout.Space();

            // JSON Path Discovery Section
            DrawJsonPathDiscovery();

            EditorGUILayout.Space();

            // Display mapping fields section
            if (_dataMapping.Model != null)
            {
                DrawMappingFields();
            }
            else
            {
                EditorGUILayout.HelpBox("Please assign a DataModel to generate mapping fields automatically.", MessageType.Info);
            }

            // Apply changes
            if (serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawMappingFields()
        {
            if (_fieldsProperty.arraySize == 0)
            {
                EditorGUILayout.HelpBox("No mapping fields available. Assign a DataModel to generate fields automatically.", MessageType.Warning);
                return;
            }

            EditorGUILayout.BeginVertical("box");

            // Draw header row first to establish column positions
            var headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var availableWidth = EditorGUIUtility.currentViewWidth - 30; // Account for margins
            
            // Calculate column widths to fill available space
            var fromWidth = availableWidth * 0.4f;   // 40% of available width
            var toWidth = availableWidth * 0.4f;     // 40% of available width
            var nestedWidth = availableWidth * 0.2f; // 20% of available width
            
            // Calculate positions step by step with no gaps
            var currentX = headerRect.x;
            
            // From header
            var fromHeaderRect = new Rect(currentX, headerRect.y, fromWidth, headerRect.height);
            EditorGUI.LabelField(fromHeaderRect, "From");
            currentX += fromWidth;
            
            // To header
            var toHeaderRect = new Rect(currentX, headerRect.y, toWidth, headerRect.height);
            EditorGUI.LabelField(toHeaderRect, "To");
            currentX += toWidth;
            
            // Nested Mapping header
            var nestedHeaderRect = new Rect(currentX, headerRect.y, nestedWidth, headerRect.height);
            EditorGUI.LabelField(nestedHeaderRect, "Mapping");

            // Draw separator line aligned with headers
            EditorGUILayout.Space(2);
            var separatorRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(new Rect(headerRect.x, separatorRect.y, availableWidth, 1), MolcaEditorColors.BorderSoft);
            EditorGUILayout.Space(2);

            // Draw mapping fields
            for (int i = 0; i < _fieldsProperty.arraySize; i++)
            {
                var fieldProperty = _fieldsProperty.GetArrayElementAtIndex(i);
                var fromProperty = fieldProperty.FindPropertyRelative("from");
                var toProperty = fieldProperty.FindPropertyRelative("to");
                var nestedMappingProperty = fieldProperty.FindPropertyRelative("nestedMapping");
                
                var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);

                // From field - positioned exactly under From header
                var fromRect = new Rect(fromHeaderRect.x, rowRect.y, fromHeaderRect.width, rowRect.height);

                // Enhanced "from" field with JSON path dropdown
                DrawEnhancedFromField(fromRect, fromProperty);

                // To field - positioned exactly under To header (readonly)
                var toRect = new Rect(toHeaderRect.x, rowRect.y, toHeaderRect.width, rowRect.height);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.TextField(toRect, toProperty.stringValue);
                EditorGUI.EndDisabledGroup();

                // Check if this field maps to a Model type and show nested mapping
                var dataField = GetDataFieldForMapping(i);
                if (dataField != null && dataField.type == DataType.Model && dataField.model != null)
                {
                    // Show nested mapping field inline
                    var nestedRect = new Rect(nestedHeaderRect.x, rowRect.y, nestedHeaderRect.width, rowRect.height);
                    EditorGUI.PropertyField(nestedRect, nestedMappingProperty, GUIContent.none);
                }
                else
                {
                    // Draw disabled field to maintain alignment
                    var nestedRect = new Rect(nestedHeaderRect.x, rowRect.y, nestedHeaderRect.width, rowRect.height);
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUI.TextField(nestedRect, "");
                    EditorGUI.EndDisabledGroup();
                }

                // Add spacing between fields
                if (i < _fieldsProperty.arraySize - 1)
                {
                    EditorGUILayout.Space(2);
                }
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawJsonPathDiscovery()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("JSON Path Discovery", EditorStyles.boldLabel);

            // Toggle for showing/hiding the discovery section
            _showJsonDiscovery = EditorGUILayout.Foldout(_showJsonDiscovery, "Sample JSON Analysis");

            if (_showJsonDiscovery)
            {
                // Sample JSON input
                EditorGUILayout.LabelField("Paste Sample JSON:", EditorStyles.miniBoldLabel);
                _sampleJson = EditorGUILayout.TextArea(_sampleJson, GUILayout.Height(100));

                // Analyze button
                if (GUILayout.Button("Analyze JSON", GUILayout.Width(100)))
                {
                    AnalyzeSampleJson();
                }

                // Show discovered paths
                if (_availableJsonPaths.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Available JSON Paths:", EditorStyles.miniBoldLabel);

                    _jsonScrollPos = EditorGUILayout.BeginScrollView(_jsonScrollPos, GUILayout.Height(120));

                    foreach (string path in _availableJsonPaths)
                    {
                        EditorGUILayout.BeginHorizontal();

                        EditorGUILayout.LabelField(path, GUILayout.ExpandWidth(true));

                        if (GUILayout.Button("Copy", GUILayout.Width(50)))
                        {
                            EditorGUIUtility.systemCopyBuffer = path;
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.EndScrollView();

                    if (GUILayout.Button("Clear Paths"))
                    {
                        _availableJsonPaths.Clear();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void AnalyzeSampleJson()
        {
            if (string.IsNullOrEmpty(_sampleJson))
            {
                Debug.LogWarning("[DataMappingEditor] Please enter sample JSON to analyze");
                return;
            }

            try
            {
                _availableJsonPaths = DataMapping.ExtractJsonPaths(_sampleJson);

                if (_availableJsonPaths.Count > 0)
                {
                    Debug.Log($"[DataMappingEditor] Found {_availableJsonPaths.Count} JSON paths from sample data");
                    // Repaint to show the discovered paths
                    Repaint();
                }
                else
                {
                    Debug.LogWarning("[DataMappingEditor] No JSON paths found in the sample data");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DataMappingEditor] Failed to analyze JSON: {e.Message}");
                _availableJsonPaths.Clear();
                Repaint();
            }
        }

        private void DrawEnhancedFromField(Rect rect, SerializedProperty fromProperty)
        {
            // If we have available JSON paths, show a dropdown
            if (_availableJsonPaths.Count > 0)
            {
                int currentIndex = _availableJsonPaths.IndexOf(fromProperty.stringValue);
                if (currentIndex == -1) currentIndex = 0; // Default to first item if not found

                EditorGUI.BeginChangeCheck();
                int selectedIndex = EditorGUI.Popup(rect, currentIndex, _availableJsonPaths.ToArray());

                if (EditorGUI.EndChangeCheck())
                {
                    // Use selected JSON path
                    fromProperty.stringValue = _availableJsonPaths[selectedIndex];
                }
            }
            else
            {
                // No JSON paths available, show regular text field
                fromProperty.stringValue = EditorGUI.TextField(rect, fromProperty.stringValue);
            }
        }

        private DataField GetDataFieldForMapping(int mappingIndex)
        {
            if (_dataMapping.Model?.Fields == null || mappingIndex >= _dataMapping.Model.Fields.Count)
                return null;
            
            return _dataMapping.Model.Fields[mappingIndex];
        }
        
        private void UpdateMappingFields()
        {
            if (_dataMapping.Model == null)
            {
                ClearMappingFields();
                return;
            }

            // Check if we need to update the fields
            if (_previousModel != _dataMapping.Model)
            {
                GenerateMappingFields();
                _previousModel = _dataMapping.Model;
            }
        }

        private void GenerateMappingFields()
        {
            if (_dataMapping.Model?.Fields == null)
            {
                ClearMappingFields();
                return;
            }

            var newFields = new List<MappingField>();
            var existingFields = _dataMapping.Fields?.ToList() ?? new List<MappingField>();
            
            foreach (var dataField in _dataMapping.Model.Fields)
            {
                // Try to find an existing field with the same "to" value to preserve customization
                var existingField = existingFields.FirstOrDefault(f => f.to == dataField.key);
                
                var mappingField = new MappingField(dataField.key, dataField.key);
                
                // If we found an existing field with the same target, preserve the source mapping and nested mapping
                if (existingField != null && existingField.to == dataField.key)
                {
                    mappingField.from = existingField.from;
                    mappingField.nestedMapping = existingField.nestedMapping; // Preserve nested mapping
                }
                
                newFields.Add(mappingField);
            }

            // Use the internal method to update fields
            Undo.RecordObject(_dataMapping, "Generate Mapping Fields");
            _dataMapping.SetFields(newFields);
            EditorUtility.SetDirty(_dataMapping);
            
            // Force the serialized object to update
            serializedObject.Update();
            
            // Repaint the inspector to show the new fields
            Repaint();
        }

        private void ClearMappingFields()
        {
            Undo.RecordObject(_dataMapping, "Clear Mapping Fields");
            _dataMapping.SetFields(new List<MappingField>());
            EditorUtility.SetDirty(_dataMapping);
            
            // Force the serialized object to update
            serializedObject.Update();
            
            // Repaint the inspector to show the cleared fields
            Repaint();
        }

        private void RefreshMappingFields()
        {
            if (_dataMapping.Model?.Fields == null) return;

            // Get existing customizations including nested mappings
            var existingCustomizations = new Dictionary<string, (string from, DataMapping nestedMapping)>();
            if (_dataMapping.Fields != null)
            {
                foreach (var field in _dataMapping.Fields)
                {
                    existingCustomizations[field.to] = (field.from, field.nestedMapping);
                }
            }

            // Generate new fields while preserving customizations
            var newFields = new List<MappingField>();
            foreach (var dataField in _dataMapping.Model.Fields)
            {
                var mappingField = new MappingField(dataField.key, dataField.key);
                
                // Preserve existing customization if available
                if (existingCustomizations.ContainsKey(dataField.key))
                {
                    var customization = existingCustomizations[dataField.key];
                    mappingField.from = customization.from;
                    mappingField.nestedMapping = customization.nestedMapping; // Preserve nested mapping
                }
                
                newFields.Add(mappingField);
            }

            // Update the fields
            Undo.RecordObject(_dataMapping, "Refresh Mapping Fields");
            _dataMapping.SetFields(newFields);
            EditorUtility.SetDirty(_dataMapping);
            
            // Force the serialized object to update
            serializedObject.Update();
            
            // Repaint the inspector to show the updated fields
            Repaint();
        }
    }
}

