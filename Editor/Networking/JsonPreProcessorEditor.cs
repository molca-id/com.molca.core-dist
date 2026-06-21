using UnityEngine;
using UnityEditor;
using Molca.Networking.Data;

namespace Molca.Networking.Data.Editor
{
    /// <summary>
    /// Custom editor for JsonPreProcessor assets
    /// Provides testing interface for data processing
    /// </summary>
    [CustomEditor(typeof(JsonPreProcessor), true)]
    public class JsonPreProcessorEditor : UnityEditor.Editor
    {
        private string testData = "";
        private string processedResult = "";
        private Vector2 testDataScroll;
        private Vector2 resultScroll;
        private bool showTestSection = true;
        private bool showSampleData = true;
        
        public override void OnInspectorGUI()
        {
            var processor = (JsonPreProcessor)target;
            
            // Draw default inspector
            DrawDefaultInspector();
            
            EditorGUILayout.Space(10);
            
            // Processor Info
            DrawProcessorInfo(processor);
            
            EditorGUILayout.Space(10);
            
            // Test Section
            DrawTestSection(processor);
            
            EditorGUILayout.Space(10);
            
            // Sample Data Section
            DrawSampleDataSection(processor);
        }
        
        private void DrawProcessorInfo(JsonPreProcessor processor)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Processor Information", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Type:", GUILayout.Width(100));
            EditorGUILayout.LabelField(processor.GetType().Name, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Description:", GUILayout.Width(100));
            EditorGUILayout.LabelField(processor.GetDescription(), EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawTestSection(JsonPreProcessor processor)
        {
            showTestSection = EditorGUILayout.Foldout(showTestSection, "Data Processing Test", true);
            
            if (showTestSection)
            {
                EditorGUILayout.BeginVertical("box");
                
                EditorGUILayout.LabelField("Test Data Input", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Enter test data to see how it gets processed by this JsonPreProcessor.", MessageType.Info);
                
                // Test data input
                EditorGUILayout.LabelField("Test Data (JSON/SSE/Text):");
                testDataScroll = EditorGUILayout.BeginScrollView(testDataScroll, GUILayout.Height(120));
                testData = EditorGUILayout.TextArea(testData, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                
                EditorGUILayout.Space(5);
                
                // Test buttons
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Test Processing", GUILayout.Height(30)))
                {
                    TestDataProcessing(processor);
                }
                
                if (GUILayout.Button("Clear Results", GUILayout.Height(30)))
                {
                    processedResult = "";
                    GUI.changed = true;
                }
                
                EditorGUILayout.EndHorizontal();
                
                // Can Handle Check
                if (!string.IsNullOrEmpty(testData))
                {
                    bool canHandle = processor.CanHandle(testData);
                    EditorGUILayout.Space(5);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Can Handle This Data:");
                    if (canHandle)
                    {
                        EditorGUILayout.LabelField("✓ Yes", EditorStyles.boldLabel);
                        GUI.color = Color.green;
                    }
                    else
                    {
                        EditorGUILayout.LabelField("✗ No", EditorStyles.boldLabel);
                        GUI.color = Color.red;
                    }
                    GUI.color = Color.white;
                    EditorGUILayout.EndHorizontal();
                }
                
                // Results display
                if (!string.IsNullOrEmpty(processedResult))
                {
                    EditorGUILayout.Space(10);
                    EditorGUILayout.LabelField("Processing Result:", EditorStyles.boldLabel);
                    
                    resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.Height(150));
                    EditorGUILayout.TextArea(processedResult, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                    
                    // Copy button
                    if (GUILayout.Button("Copy Result to Clipboard"))
                    {
                        EditorGUIUtility.systemCopyBuffer = processedResult;
                        Debug.Log("Result copied to clipboard!");
                    }
                }
                
                EditorGUILayout.EndVertical();
            }
        }
        
        private void DrawSampleDataSection(JsonPreProcessor processor)
        {
            showSampleData = EditorGUILayout.Foldout(showSampleData, "Sample Data", true);
            
            if (showSampleData)
            {
                EditorGUILayout.BeginVertical("box");
                
                EditorGUILayout.LabelField("Quick Test Data", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Click any button below to load sample data for testing.", MessageType.Info);
                
                // Sample data buttons based on processor type
                if (processor is SSEToJsonConverter)
                {
                    DrawSSESampleData();
                }
                else if (processor is NamedObjectToArrayProcessor)
                {
                    DrawNamedObjectSampleData();
                }
                else if (processor is ComplexJsonProcessor)
                {
                    DrawComplexJsonSampleData();
                }
                else if (processor is PassThroughProcessor)
                {
                    DrawPassThroughSampleData();
                }
                else if (processor is CompositeProcessor)
                {
                    DrawCompositeSampleData();
                }
                else
                {
                    DrawGenericSampleData();
                }
                
                EditorGUILayout.EndVertical();
            }
        }
        
        private void DrawSSESampleData()
        {
            EditorGUILayout.LabelField("SSE Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Basic SSE"))
            {
                testData = @"event: line-detail-oee
data: {""working_hour_start"":""2025-08-17 07:00:00"",""oee"":""124.20%""}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("SSE with Metadata"))
            {
                testData = @"event: performance-update
data: {""target"": 84884, ""achievement"": 74.47}
timestamp: 1234567890
priority: high";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawNamedObjectSampleData()
        {
            EditorGUILayout.LabelField("Named Object Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Simple Named Objects"))
            {
                testData = @"{
    ""CLUSTER LOW VOLUME"": {
        ""historical_performance"": {
            ""MA.001.ADM | INSULATOR , INTAKE MANIFOLD NO.1 (FG)"": {
                ""target"": 84884,
                ""total_ok"": 63213,
                ""achievement"": 74.47
            },
            ""MA.001.DEN | BRACKET COMPRESSOR MOUNTING 6592 (FG)"": {
                ""target"": 98646,
                ""total_ok"": 77793,
                ""achievement"": 78.86
            }
        }
    }
}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("Complex Named Objects"))
            {
                testData = @"{
    ""CLUSTER MATIC AHM"": {
        ""historical_performance"": {
            ""MA.001.AHM | COVER L SIDE K2SA (SFG)"": {
                ""target"": 465225,
                ""total_ok"": 407022,
                ""achievement"": 87.49
            }
        }
    }
}";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawComplexJsonSampleData()
        {
            EditorGUILayout.LabelField("Complex JSON Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Missing Brackets"))
            {
                testData = @"""CLUSTER LOW VOLUME"": {""historical_performance"": {""target"": 84884}}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("Malformed JSON"))
            {
                testData = @"{'name': 'test', 'value': 42, nested: {inner: 'data'}}";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawPassThroughSampleData()
        {
            EditorGUILayout.LabelField("Valid JSON Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Simple Object"))
            {
                testData = @"{""name"": ""test"", ""value"": 42}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("Complex Object"))
            {
                testData = @"{""nested"": {""deep"": {""value"": 123}}, ""array"": [1, 2, 3]}";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawCompositeSampleData()
        {
            EditorGUILayout.LabelField("Composite Processor Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("SSE Data"))
            {
                testData = @"event: line-detail-oee
data: {""working_hour_start"":""2025-08-17 07:00:00"",""oee"":""124.20%""}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("Complex Data"))
            {
                testData = @"""CLUSTER LOW VOLUME"": {""historical_performance"": {""target"": 84884}}";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawGenericSampleData()
        {
            EditorGUILayout.LabelField("Generic Sample Data:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("JSON Object"))
            {
                testData = @"{""name"": ""test"", ""value"": 42}";
                GUI.changed = true;
            }
            
            if (GUILayout.Button("JSON Array"))
            {
                testData = @"[""item1"", ""item2"", ""item3""]";
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void TestDataProcessing(JsonPreProcessor processor)
        {
            if (string.IsNullOrEmpty(testData))
            {
                EditorUtility.DisplayDialog("Test Data Required", "Please enter some test data first.", "OK");
                return;
            }
            
            try
            {
                // Check if processor can handle this data
                bool canHandle = processor.CanHandle(testData);
                
                // Process the data
                string result = processor.ProcessData(testData);
                
                // Format the result for display
                processedResult = $"=== PROCESSING RESULT ===\n";
                processedResult += $"Processor: {processor.GetType().Name}\n";
                processedResult += $"Description: {processor.GetDescription()}\n";
                processedResult += $"Can Handle: {(canHandle ? "YES" : "NO")}\n\n";
                processedResult += $"Original Data Length: {testData.Length} characters\n";
                processedResult += $"Processed Data Length: {result.Length} characters\n\n";
                
                processedResult += $"=== PROCESSED DATA ===\n{result}";
                
                GUI.changed = true;
                
                Debug.Log($"[JsonPreProcessorEditor] Test processing completed for {processor.name}");
                Debug.Log($"[JsonPreProcessorEditor] Can Handle: {canHandle}");
                Debug.Log($"[JsonPreProcessorEditor] Result: {result}");
            }
            catch (System.Exception e)
            {
                processedResult = $"ERROR during processing:\n{e.Message}\n\nStack Trace:\n{e.StackTrace}";
                GUI.changed = true;
                
                Debug.LogError($"[JsonPreProcessorEditor] Error testing data processing: {e.Message}");
            }
        }
    }
}
