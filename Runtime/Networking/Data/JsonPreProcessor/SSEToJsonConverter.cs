using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Converts Server-Sent Events (SSE) format to valid JSON
    /// Preserves event type, data, and all other metadata fields
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SSE to JSON Converter", menuName = "Molca/Networking/JsonPreProcessor/SSE to JSON Converter", order = 20)]
    public class SSEToJsonConverter : JsonPreProcessor
    {
        [Header("SSE to JSON Conversion")]
        [SerializeField, FormerlySerializedAs("autoDetectTypes")] private bool _autoDetectTypes = true;
        
        public override string GetDescription()
        {
            return "Converts SSE format to valid JSON while preserving all fields (event, data, metadata)";
        }
        
        public override bool CanHandle(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return false;
            return rawData.StartsWith("event:") || rawData.StartsWith("data:");
        }
        
        public override string ProcessData(string rawData)
        {
            if (string.IsNullOrEmpty(rawData))
            {
                LogWarning("Raw data is null or empty");
                return "{}";
            }
            
            LogMessage($"Converting SSE to JSON: {rawData.Substring(0, Math.Min(100, rawData.Length))}...");
            
            string jsonResult = ConvertSSEToJson(rawData);
            
            LogMessage($"SSE to JSON result: {jsonResult.Substring(0, Math.Min(100, jsonResult.Length))}...");
            
            return jsonResult;
        }
        
        /// <summary>
        /// Converts SSE format to valid JSON while preserving event and data fields
        /// </summary>
        private string ConvertSSEToJson(string sseData)
        {
            if (string.IsNullOrEmpty(sseData))
                return "{}";
            
            var result = new System.Text.StringBuilder();
            result.Append("{");
            
            bool hasFields = false;
            
            // Split by lines and process each field
            string[] lines = sseData.Split('\n');
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;
                
                // Check if line has a field separator
                int colonIndex = trimmedLine.IndexOf(':');
                if (colonIndex > 0)
                {
                    string fieldName = trimmedLine.Substring(0, colonIndex).Trim();
                    string fieldValue = trimmedLine.Substring(colonIndex + 1).Trim();
                    
                    if (hasFields) result.Append(",");
                    hasFields = true;
                    
                    // Handle different field types
                    if (fieldName == "event")
                    {
                        // Event field is always a string
                        result.Append($"\"{fieldName}\":\"{fieldValue}\"");
                    }
                    else if (fieldName == "data")
                    {
                        // Data field might be JSON or plain text
                        if (fieldValue.StartsWith("{") || fieldValue.StartsWith("["))
                        {
                            // It's already JSON, just add it
                            result.Append($"\"{fieldName}\":{fieldValue}");
                        }
                        else
                        {
                            // It's plain text, wrap it in quotes
                            result.Append($"\"{fieldName}\":\"{fieldValue}\"");
                        }
                    }
                    else
                    {
                        // Other fields - try to detect if they're JSON or plain text
                        if (_autoDetectTypes && (fieldValue.StartsWith("{") || fieldValue.StartsWith("[") || 
                            fieldValue.StartsWith("\"") || IsNumeric(fieldValue)))
                        {
                            // It's JSON, numeric, or quoted string
                            result.Append($"\"{fieldName}\":{fieldValue}");
                        }
                        else
                        {
                            // It's plain text, wrap it in quotes
                            result.Append($"\"{fieldName}\":\"{fieldValue}\"");
                        }
                    }
                }
            }
            
            result.Append("}");
            
            return result.ToString();
        }
        
        /// <summary>
        /// Checks if a string represents a numeric value
        /// </summary>
        private bool IsNumeric(string value)
        {
            return double.TryParse(value, out _);
        }
        
        /// <summary>
        /// Test method specifically for SSE to JSON conversion
        /// </summary>
        [ContextMenu("Test SSE to JSON")]
        public void TestSSEToJson()
        {
            Debug.Log("=== SSE to JSON Conversion Test ===");
            
            // Test 1: Standard SSE with JSON data
            string sse1 = @"event: line-detail-oee
data: {""working_hour_start"":""2025-08-17 07:00:00"",""oee"":""124.20%""}";
            Debug.Log($"Test 1 - Standard SSE:");
            Debug.Log($"Input: {sse1}");
            Debug.Log($"Output: {ProcessData(sse1)}");
            
            // Test 2: SSE with plain text data
            string sse2 = @"event: status-update
data: machine_running
timestamp: 1234567890";
            Debug.Log($"Test 2 - SSE with plain text:");
            Debug.Log($"Input: {sse2}");
            Debug.Log($"Output: {ProcessData(sse2)}");
            
            // Test 3: SSE with mixed data types
            string sse3 = @"event: performance-data
data: {""target"": 84884, ""achievement"": 74.47}
priority: high
count: 42";
            Debug.Log($"Test 3 - SSE with mixed types:");
            Debug.Log($"Input: {sse3}");
            Debug.Log($"Output: {ProcessData(sse3)}");
            
            Debug.Log("=== End SSE to JSON Test ===");
        }
    }
}
