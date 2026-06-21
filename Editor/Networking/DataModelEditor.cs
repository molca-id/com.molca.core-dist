using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Molca.Networking.Data;
using Molca.Editor.UI;

namespace Molca.Editor
{
    [CustomEditor(typeof(DataModel))]
    public class DataModelEditor : UnityEditor.Editor
    {
        private SerializedProperty _modelIdProperty;
        private SerializedProperty _modelNameProperty;
        private SerializedProperty _fieldsProperty;
        private DataModel _dataModel;
        private List<string> _duplicateKeys = new List<string>();

        private void OnEnable()
        {
            _modelIdProperty = serializedObject.FindProperty("_modelId");
            _modelNameProperty = serializedObject.FindProperty("_modelName");
            _fieldsProperty = serializedObject.FindProperty("_fields");
            _dataModel = (DataModel)target;
            ValidateFields();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Model ID
            EditorGUILayout.PropertyField(_modelIdProperty, new GUIContent("Model ID", "Unique identifier for this data model"));

            // Model Name
            EditorGUILayout.PropertyField(_modelNameProperty, new GUIContent("Model Name", "Human-readable name for this data model"));

            EditorGUILayout.Space();

            // Fields section
            EditorGUILayout.LabelField("Data Fields", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Define the structure of your data model. Each field must have a unique key.", MessageType.Info);

            // Show duplicate key warnings if any
            if (_duplicateKeys.Count > 0)
            {
                EditorGUILayout.HelpBox($"Duplicate keys detected: {string.Join(", ", _duplicateKeys)}", MessageType.Error);
            }

            // Fields list
            if (_fieldsProperty.arraySize > 0)
            {
                DrawFieldsList();
            }
            else
            {
                EditorGUILayout.HelpBox("No fields defined. Add fields to define your data structure.", MessageType.Info);
            }

            EditorGUILayout.Space();

            // Add field button
            if (GUILayout.Button("Add Field"))
            {
                AddNewField();
            }

            // Apply changes
            if (serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
                ValidateFields();
            }
        }

        private void DrawFieldsList()
        {
            EditorGUILayout.BeginVertical("box");

            // Draw header row first to establish column positions
            var headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var availableWidth = EditorGUIUtility.currentViewWidth - 90; // Account for margins
            
            // Calculate column widths to fill available space
            var keyWidth = Mathf.Max(120, availableWidth * 0.5f);   // 30% of available width
            var typeWidth = Mathf.Max(80, availableWidth * 0.25f);   // 20% of available width
            var arrayWidth = 40f;  // 20% of available width
            var modelWidth = Mathf.Max(40, availableWidth * 0.25f);  // 30% of available width
            var removeWidth = 20; // Fixed width for remove button
            
            // Calculate positions step by step with no gaps
            var currentX = headerRect.x;
            
            // Key header
            var keyHeaderRect = new Rect(currentX, headerRect.y, keyWidth, headerRect.height);
            EditorGUI.LabelField(keyHeaderRect, "Key");
            currentX += keyWidth;
            
            // Type header
            var typeHeaderRect = new Rect(currentX, headerRect.y, typeWidth, headerRect.height);
            EditorGUI.LabelField(typeHeaderRect, "Type");
            currentX += typeWidth;
            
            // Array header
            var arrayHeaderRect = new Rect(currentX, headerRect.y, arrayWidth, headerRect.height);
            EditorGUI.LabelField(arrayHeaderRect, "Array");
            currentX += arrayWidth;
            
            // Model header
            var modelHeaderRect = new Rect(currentX, headerRect.y, modelWidth, headerRect.height);
            EditorGUI.LabelField(modelHeaderRect, "Model");
            currentX += modelWidth;

            // Draw separator line aligned with headers
            EditorGUILayout.Space(2);
            var separatorRect = EditorGUILayout.GetControlRect(false, 1);
            var totalHeaderWidth = currentX - headerRect.x;
            EditorGUI.DrawRect(new Rect(headerRect.x, separatorRect.y, totalHeaderWidth, 1), MolcaEditorColors.BorderSoft);
            EditorGUILayout.Space(2);

            for (int i = 0; i < _fieldsProperty.arraySize; i++)
            {
                var fieldProperty = _fieldsProperty.GetArrayElementAtIndex(i);
                var keyProperty = fieldProperty.FindPropertyRelative("key");
                var typeProperty = fieldProperty.FindPropertyRelative("type");
                var modelProperty = fieldProperty.FindPropertyRelative("model");
                var arrayProperty = fieldProperty.FindPropertyRelative("isArray");

                var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);

                // Key field - positioned exactly under Key header
                var keyRect = new Rect(keyHeaderRect.x, rowRect.y, keyHeaderRect.width, rowRect.height);
                EditorGUI.PropertyField(keyRect, keyProperty, GUIContent.none);

                // Type dropdown - positioned exactly under Type header
                var typeRect = new Rect(typeHeaderRect.x, rowRect.y, typeHeaderRect.width, rowRect.height);
                EditorGUI.PropertyField(typeRect, typeProperty, GUIContent.none);

                // Array toggle - positioned exactly under Array header
                var arrayRect = new Rect(arrayHeaderRect.x, rowRect.y, arrayHeaderRect.width, rowRect.height);
                var arrayToggleRect = new Rect(arrayHeaderRect.x + 20, rowRect.y, 20, arrayRect.height);
                EditorGUI.PropertyField(arrayToggleRect, arrayProperty, GUIContent.none);

                // Model field - positioned exactly under Model header
                var currentType = (DataType)typeProperty.enumValueIndex;
                if (currentType == DataType.Model)
                {
                    var modelRect = new Rect(modelHeaderRect.x, rowRect.y, modelHeaderRect.width, rowRect.height);
                    EditorGUI.PropertyField(modelRect, modelProperty, GUIContent.none);
                }
                else
                {
                    // Draw disabled field to maintain alignment
                    var modelRect = new Rect(modelHeaderRect.x, rowRect.y, modelHeaderRect.width, rowRect.height);
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUI.TextField(modelRect, "");
                    EditorGUI.EndDisabledGroup();
                }

                // Remove button - positioned to the right of Model column
                var removeRect = new Rect(modelHeaderRect.x + modelHeaderRect.width, rowRect.y, removeWidth, rowRect.height);
                if (GUI.Button(removeRect, "×"))
                {
                    RemoveField(i);
                    break; // Exit loop since array size changed
                }

                // Add spacing between fields
                if (i < _fieldsProperty.arraySize - 1)
                {
                    EditorGUILayout.Space(2);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void AddNewField()
        {
            Undo.RecordObject(_dataModel, "Add Data Field");
            
            var newField = new DataField("NewField", DataType.String);

            // Add to the serialized property
            _fieldsProperty.arraySize++;
            var newFieldProperty = _fieldsProperty.GetArrayElementAtIndex(_fieldsProperty.arraySize - 1);
            newFieldProperty.FindPropertyRelative("key").stringValue = newField.key;
            newFieldProperty.FindPropertyRelative("type").enumValueIndex = (int)newField.type;
            newFieldProperty.FindPropertyRelative("model").objectReferenceValue = newField.model;
            newFieldProperty.FindPropertyRelative("isArray").boolValue = newField.isArray;

            EditorUtility.SetDirty(_dataModel);
            ValidateFields();
        }

        private void RemoveField(int index)
        {
            Undo.RecordObject(_dataModel, "Remove Data Field");
            _fieldsProperty.DeleteArrayElementAtIndex(index);
            EditorUtility.SetDirty(_dataModel);
            ValidateFields();
        }

        private void ValidateFields()
        {
            _duplicateKeys.Clear();
            
            if (_dataModel.Fields == null) return;

            var keys = _dataModel.Fields.Select(f => f.key).ToList();
            var duplicateGroups = keys.GroupBy(k => k).Where(g => g.Count() > 1);
            
            foreach (var group in duplicateGroups)
            {
                _duplicateKeys.Add(group.Key);
            }
        }
    }
}
