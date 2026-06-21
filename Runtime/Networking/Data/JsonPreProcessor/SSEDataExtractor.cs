using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Extracts only the data field from Server-Sent Events (SSE) format
    /// Ignores event type and other metadata, returns just the JSON content
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SSE Data Extractor", menuName = "Molca/Networking/JsonPreProcessor/SSE Data Extractor", order = 20)]
    public class SSEDataExtractor : JsonPreProcessor
    {
        [Header("SSE Processing")]
        [SerializeField, FormerlySerializedAs("removeSSEPrefixes")] private bool _removeSSEPrefixes = true;
        [SerializeField, FormerlySerializedAs("addBracketsIfNeeded")] private bool _addBracketsIfNeeded = true;
        
        public override string GetDescription()
        {
            return "Extracts only the data field from SSE format, ignoring event type and metadata";
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
            
            LogMessage($"Processing SSE data: {rawData.Substring(0, Mathf.Min(100, rawData.Length))}...");
            
            string processedData = rawData;
            
            // Step 1: Remove SSE prefixes
            if (_removeSSEPrefixes)
            {
                processedData = RemoveSSEPrefixes(processedData);
            }
            
            // Step 2: Extract data field
            processedData = ExtractDataField(processedData);
            
            // Step 3: Add brackets if needed
            if (_addBracketsIfNeeded)
            {
                processedData = AddBracketsIfNeeded(processedData);
            }
            
            LogMessage($"Final processed data: {processedData.Substring(0, Mathf.Min(100, processedData.Length))}...");
            
            return processedData;
        }
        
        /// <summary>
        /// Removes Server-Sent Events prefixes like "event:" and "data:"
        /// </summary>
        private string RemoveSSEPrefixes(string data)
        {
            if (string.IsNullOrEmpty(data))
                return data;
            
            // Remove event: prefix
            data = Regex.Replace(data, @"^event:\s*[^\n]*\n?", "", RegexOptions.Multiline);
            
            // Remove data: prefix
            data = Regex.Replace(data, @"^data:\s*", "", RegexOptions.Multiline);
            
            // Remove any remaining SSE prefixes
            data = Regex.Replace(data, @"^[a-zA-Z_][a-zA-Z0-9_]*:\s*", "", RegexOptions.Multiline);
            
            // Clean up extra whitespace and newlines
            data = data.Trim();
            
            LogMessage($"After SSE prefix removal: {data.Substring(0, Mathf.Min(100, data.Length))}...");
            
            return data;
        }
        
        /// <summary>
        /// Extracts only the data field content from SSE format
        /// </summary>
        private string ExtractDataField(string data)
        {
            if (string.IsNullOrEmpty(data))
                return data;
            
            // Look for "data:" field in the content
            var dataMatch = Regex.Match(data, @"data:\s*(\{.*\})", RegexOptions.Singleline);
            if (dataMatch.Success)
            {
                string extractedData = dataMatch.Groups[1].Value.Trim();
                LogMessage($"Extracted data field: {extractedData.Substring(0, Mathf.Min(100, extractedData.Length))}...");
                return extractedData;
            }
            
            // If no "data:" field found, return the original
            return data;
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
        [ContextMenu("Test SSE Data Extractor")]
        public void TestSSEDataExtractor()
        {
            Debug.Log("=== SSE Data Extractor Test ===");
            
            // Test SSE format
            string sseData = @"event: line-detail-oee
data: {""working_hour_start"":""2025-08-17 07:00:00"",""oee"":""124.20%""}";
            
            Debug.Log($"Testing SSE data: {sseData}");
            string processedSSE = ProcessData(sseData);
            Debug.Log($"Processed SSE result: {processedSSE}");
            
            Debug.Log("=== End Test ===");
        }
    }
}
