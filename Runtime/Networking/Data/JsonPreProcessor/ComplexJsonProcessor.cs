using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Processes complex nested JSON structures and adds brackets if needed
    /// Handles malformed JSON and converts it to valid format
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "Complex JSON Processor", menuName = "Molca/Networking/JsonPreProcessor/Complex JSON Processor", order = 20)]
    public class ComplexJsonProcessor : JsonPreProcessor
    {
        [Header("JSON Processing")]
        [SerializeField, FormerlySerializedAs("addBracketsIfNeeded")] private bool _addBracketsIfNeeded = true;
        [SerializeField, FormerlySerializedAs("fixCommonIssues")] private bool _fixCommonIssues = true;
        [SerializeField, FormerlySerializedAs("validateOutput")] private bool _validateOutput = true;
        
        public override string GetDescription()
        {
            return "Processes complex JSON structures, adds brackets if needed, and fixes common formatting issues";
        }
        
        public override bool CanHandle(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return false;
            
            // Can handle any text that might be JSON-related
            string trimmed = rawData.Trim();
            
            // If it's already valid JSON, we can handle it
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}")) return true;
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) return true;
            
            // If it looks like JSON content without braces, we can handle it
            if (trimmed.Contains(":") && !trimmed.StartsWith("event:") && !trimmed.StartsWith("data:")) return true;
            
            return false;
        }
        
        public override string ProcessData(string rawData)
        {
            if (string.IsNullOrEmpty(rawData))
            {
                LogWarning("Raw data is null or empty");
                return "{}";
            }
            
            LogMessage($"Processing complex JSON: {rawData.Substring(0, Mathf.Min(100, rawData.Length))}...");
            
            string processedData = rawData;

            // Step 1: Add wrapping braces for brace-less object bodies.
            if (_addBracketsIfNeeded)
            {
                processedData = AddBracketsIfNeeded(processedData);
            }

            // Step 2: Tolerant normalize via Json.NET instead of regex surgery. The old
            // regex "repair" could corrupt valid payloads (e.g. apostrophes in string
            // values, quote rebalancing). Json.NET already accepts single quotes,
            // unquoted property names, and trailing commas; re-serializing yields clean
            // JSON. If it can't parse, the input is returned UNCHANGED — never mangled.
            if (_fixCommonIssues || _validateOutput)
            {
                processedData = TryNormalizeJson(processedData);
            }

            LogMessage($"Final processed data: {processedData.Substring(0, Mathf.Min(100, processedData.Length))}...");

            return processedData;
        }

        /// <summary>
        /// Parses with a tolerant <see cref="JsonTextReader"/> and re-serializes to clean
        /// JSON. Returns the input unchanged if it cannot be parsed, so a valid payload is
        /// never corrupted by a failed "repair".
        /// </summary>
        private string TryNormalizeJson(string data)
        {
            if (string.IsNullOrEmpty(data))
                return "{}";

            try
            {
                using var reader = new JsonTextReader(new StringReader(data));
                var token = JToken.ReadFrom(reader);
                return token.ToString(Formatting.None);
            }
            catch (Exception e)
            {
                LogWarning($"Could not parse JSON; leaving payload unchanged: {e.Message}");
                return data;
            }
        }
        
        /// <summary>
        /// Adds curly braces if the content doesn't have them
        /// </summary>
        private string AddBracketsIfNeeded(string data)
        {
            if (string.IsNullOrEmpty(data))
                return "{}";
            
            data = data.Trim();
            
            // If it already starts and ends with braces, return as-is
            if (data.StartsWith("{") && data.EndsWith("}"))
            {
                return data;
            }
            
            // If it's just a JSON object without braces, add them
            if (!data.StartsWith("{") && !data.StartsWith("[") && !data.StartsWith("\""))
            {
                data = "{" + data + "}";
                LogMessage($"Added brackets: {data}");
            }
            
            return data;
        }
        
        /// <summary>
        /// Test method to verify the pre-processor is working correctly
        /// </summary>
        [ContextMenu("Test Complex JSON Processor")]
        public void TestComplexJsonProcessor()
        {
            Debug.Log("=== Complex JSON Processor Test ===");
            
            // Test 1: JSON without braces
            string complexData1 = @"""CLUSTER LOW VOLUME"": {""historical_performance"": {""MA.001.ADM | INSULATOR , INTAKE MANIFOLD NO.1 (FG)"": {""target"": 84884, ""total_ok"": 63213}}}";
            Debug.Log($"Test 1 - JSON without braces:");
            Debug.Log($"Input: {complexData1}");
            Debug.Log($"Output: {ProcessData(complexData1)}");
            
            // Test 2: Malformed JSON
            string complexData2 = @"{'name': 'test', 'value': 42, 'nested': {inner: 'data'}}";
            Debug.Log($"Test 2 - Malformed JSON:");
            Debug.Log($"Input: {complexData2}");
            Debug.Log($"Output: {ProcessData(complexData2)}");
            
            // Test 3: Already valid JSON
            string complexData3 = @"{""name"": ""test"", ""value"": 42}";
            Debug.Log($"Test 3 - Already valid JSON:");
            Debug.Log($"Input: {complexData3}");
            Debug.Log($"Output: {ProcessData(complexData3)}");
            
            Debug.Log("=== End Test ===");
        }
    }
}
