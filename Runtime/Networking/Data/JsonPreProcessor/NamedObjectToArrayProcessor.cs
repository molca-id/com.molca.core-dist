using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Serialization;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Converts named objects to arrays, making object names into fields
    /// Useful for APIs that return data with dynamic keys instead of arrays
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "Named Object to Array Processor", menuName = "Molca/Networking/JsonPreProcessor/Named Object to Array Processor", order = 20)]
    public class NamedObjectToArrayProcessor : JsonPreProcessor
    {
        [Header("Conversion Settings")]
        [SerializeField, FormerlySerializedAs("nameFieldName")] private string _nameFieldName = "name";
        [SerializeField, FormerlySerializedAs("maxDepth")] private int _maxDepth = 5;
        
        public override string GetDescription()
        {
            return "Converts named objects to arrays, making object names into fields for easier consumption";
        }
        
        public override bool CanHandle(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return false;
            
            string trimmed = rawData.Trim();
            
            // Must be valid JSON object
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return false;
            
            try
            {
                JObject obj = JObject.Parse(trimmed);
                return HasNamedObjects(obj);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Check if the JSON object contains named objects that should be converted
        /// </summary>
        private bool HasNamedObjects(JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                // Check if this looks like a named object (contains spaces, |, or ,)
                if (property.Name.Contains(" ") || property.Name.Contains("|") || property.Name.Contains(","))
                {
                    return true;
                }
                
                // Recursively check nested objects
                if (property.Value is JObject nestedObj && HasNamedObjects(nestedObj))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        public override string ProcessData(string rawData)
        {
            if (string.IsNullOrEmpty(rawData))
            {
                LogWarning("Raw data is null or empty");
                return "{}";
            }
            
            LogMessage($"Converting named objects to arrays: {rawData.Substring(0, Mathf.Min(100, rawData.Length))}...");
            
            try
            {
                JObject jsonObj = JObject.Parse(rawData);
                JToken result = ProcessToken(jsonObj, 0);
                
                string resultString = result.ToString(Formatting.None);
                LogMessage($"Conversion result: {resultString.Substring(0, Mathf.Min(100, resultString.Length))}...");
                
                return resultString;
            }
            catch (System.Exception e)
            {
                LogError($"Error during conversion: {e.Message}");
                return rawData; // Return original data if conversion fails
            }
        }
        
        /// <summary>
        /// Recursively processes JSON tokens to convert named objects to arrays
        /// </summary>
        private JToken ProcessToken(JToken token, int depth)
        {
            if (depth > _maxDepth)
            {
                LogWarning($"Maximum depth {_maxDepth} reached, stopping recursion");
                return token;
            }
            
            if (token is JObject obj)
            {
                return ProcessObject(obj, depth);
            }
            else if (token is JArray arr)
            {
                return ProcessArray(arr, depth);
            }
            else
            {
                return token; // Primitive value, return as-is
            }
        }
        
        /// <summary>
        /// Processes a JSON object, converting it to array if it contains named objects
        /// </summary>
        private JToken ProcessObject(JObject obj, int depth)
        {
            LogMessage($"Processing object at depth {depth}");
            
            // First, recursively process all nested objects
            var processedObj = new JObject();
            foreach (var property in obj.Properties())
            {
                processedObj[property.Name] = ProcessToken(property.Value, depth + 1);
            }
            
            // Check if this object should be converted to an array
            if (ShouldConvertToArray(processedObj))
            {
                LogMessage($"Converting object to array at depth {depth}");
                return ConvertObjectToArray(processedObj);
            }
            
            return processedObj;
        }
        
        /// <summary>
        /// Processes a JSON array
        /// </summary>
        private JToken ProcessArray(JArray arr, int depth)
        {
            LogMessage($"Processing array at depth {depth}");
            
            var processedArray = new JArray();
            foreach (var item in arr)
            {
                processedArray.Add(ProcessToken(item, depth + 1));
            }
            
            return processedArray;
        }
        
        /// <summary>
        /// Determines if an object should be converted to an array
        /// </summary>
        private bool ShouldConvertToArray(JObject obj)
        {
            // Check if all properties are objects and have names that suggest they're named objects
            foreach (var property in obj.Properties())
            {
                // If the property name contains spaces, |, or , and the value is an object,
                // this suggests it's a named object that should be converted
                if ((property.Name.Contains(" ") || property.Name.Contains("|") || property.Name.Contains(",")) 
                    && property.Value is JObject)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Converts an object with named properties to an array
        /// </summary>
        private JArray ConvertObjectToArray(JObject obj)
        {
            var array = new JArray();
            
            foreach (var property in obj.Properties())
            {
                if (property.Value is JObject valueObj)
                {
                    // Create a new object with the name field and all original properties
                    var arrayItem = new JObject();
                    arrayItem[_nameFieldName] = property.Name;
                    
                    // Copy all properties from the original object
                    foreach (var valueProp in valueObj.Properties())
                    {
                        arrayItem[valueProp.Name] = valueProp.Value;
                    }
                    
                    array.Add(arrayItem);
                }
                else
                {
                    // If it's not an object, create a simple object with name and value
                    var arrayItem = new JObject();
                    arrayItem[_nameFieldName] = property.Name;
                    arrayItem["value"] = property.Value;
                    array.Add(arrayItem);
                }
            }
            
            return array;
        }
        
        /// <summary>
        /// Test method to verify the processor is working correctly
        /// </summary>
        [ContextMenu("Test Named Object to Array")]
        public void TestNamedObjectToArray()
        {
            Debug.Log("=== Named Object to Array Test ===");
            
            string testData = @"{
                ""CLUSTER LOW VOLUME"": {
                    ""historical_performance"": {
                        ""MA.001.ADM | INSULATOR"": {
                            ""target"": 84884,
                            ""achievement"": 74.47
                        },
                        ""MA.001.DEN | BRACKET"": {
                            ""target"": 98646,
                            ""achievement"": 78.86
                        }
                    }
                },
                ""CLUSTER HIGH VOLUME"": {
                    ""historical_performance"": {
                        ""MA.002.ADM | INSULATOR"": {
                            ""target"": 75327,
                            ""achievement"": 65.47
                        },
                        ""MA.002.DEN | BRACKET"": {
                            ""target"": 34246,
                            ""achievement"": 77.86
                        }
                    }
                }
            }";
            
            Debug.Log($"Test data: {testData}");
            
            // Enable logging for testing
            bool originalLogging = logProcessingSteps;
            logProcessingSteps = true;
            
            string result = ProcessData(testData);
            
            // Restore original logging
            logProcessingSteps = originalLogging;
            
            Debug.Log($"Conversion result: {result}");
            
            // Pretty print for easier reading
            try
            {
                JToken parsed = JToken.Parse(result);
                string prettyResult = parsed.ToString(Formatting.Indented);
                Debug.Log($"Pretty formatted result:\n{prettyResult}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Could not pretty print result: {e.Message}");
            }
            
            Debug.Log("=== End Test ===");
        }
    }
}
