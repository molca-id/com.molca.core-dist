using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Pass-through processor for data that's already valid JSON
    /// No processing is done, just validation and logging
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "Pass-Through Processor", menuName = "Molca/Networking/JsonPreProcessor/Pass-Through Processor", order = 20)]
    public class PassThroughProcessor : JsonPreProcessor
    {
        [Header("Pass-Through Settings")]
        [SerializeField, FormerlySerializedAs("validateJson")] private bool _validateJson = true;
        [SerializeField, FormerlySerializedAs("logDataSize")] private bool _logDataSize = true;
        
        public override string GetDescription()
        {
            return "Pass-through processor for already valid JSON data (no processing, just validation)";
        }
        
        public override bool CanHandle(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return false;
            
            string trimmed = rawData.Trim();
            
            // Can handle if it looks like valid JSON
            return (trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                   (trimmed.StartsWith("[") && trimmed.EndsWith("]"));
        }
        
        public override string ProcessData(string rawData)
        {
            if (string.IsNullOrEmpty(rawData))
            {
                LogWarning("Raw data is null or empty");
                return "{}";
            }
            
            LogMessage($"Pass-through processing: {rawData.Substring(0, Mathf.Min(100, rawData.Length))}...");
            
            if (_logDataSize)
            {
                LogMessage($"Data size: {rawData.Length} characters");
            }
            
            if (_validateJson)
            {
                if (IsValidJson(rawData))
                {
                    LogMessage("Data is valid JSON, passing through unchanged");
                }
                else
                {
                    LogWarning("Data appears to be invalid JSON, but passing through anyway");
                }
            }
            
            return rawData;
        }
        
        /// <summary>
        /// Simple JSON validation check
        /// </summary>
        private bool IsValidJson(string data)
        {
            if (string.IsNullOrEmpty(data)) return false;
            
            string trimmed = data.Trim();
            
            // Basic structure validation
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                // Count braces to ensure they're balanced
                int openBraces = 0;
                int closeBraces = 0;
                bool inString = false;
                bool escaped = false;
                
                for (int i = 0; i < trimmed.Length; i++)
                {
                    char c = trimmed[i];
                    
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    
                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }
                    
                    if (!inString)
                    {
                        if (c == '{') openBraces++;
                        else if (c == '}') closeBraces++;
                    }
                }
                
                return openBraces == closeBraces;
            }
            
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                // Count brackets to ensure they're balanced
                int openBrackets = 0;
                int closeBrackets = 0;
                bool inString = false;
                bool escaped = false;
                
                for (int i = 0; i < trimmed.Length; i++)
                {
                    char c = trimmed[i];
                    
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    
                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }
                    
                    if (!inString)
                    {
                        if (c == '[') openBrackets++;
                        else if (c == ']') closeBrackets++;
                    }
                }
                
                return openBrackets == closeBrackets;
            }
            
            return false;
        }
        
        /// <summary>
        /// Test method to verify the pass-through processor is working correctly
        /// </summary>
        [ContextMenu("Test Pass-Through Processor")]
        public void TestPassThroughProcessor()
        {
            Debug.Log("=== Pass-Through Processor Test ===");
            
            // Test 1: Valid JSON object
            string validJson1 = @"{""name"": ""test"", ""value"": 42}";
            Debug.Log($"Test 1 - Valid JSON object:");
            Debug.Log($"Input: {validJson1}");
            Debug.Log($"Output: {ProcessData(validJson1)}");
            
            // Test 2: Valid JSON array
            string validJson2 = @"[""item1"", ""item2"", ""item3""]";
            Debug.Log($"Test 2 - Valid JSON array:");
            Debug.Log($"Input: {validJson2}");
            Debug.Log($"Output: {ProcessData(validJson2)}");
            
            // Test 3: Complex nested JSON
            string validJson3 = @"{""nested"": {""deep"": {""value"": 123}}, ""array"": [1, 2, 3]}";
            Debug.Log($"Test 3 - Complex nested JSON:");
            Debug.Log($"Input: {validJson3}");
            Debug.Log($"Output: {ProcessData(validJson3)}");
            
            Debug.Log("=== End Test ===");
        }
    }
}
