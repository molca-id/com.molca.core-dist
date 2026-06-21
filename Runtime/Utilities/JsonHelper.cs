using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Text;

namespace Molca.Utils
{ 
    /// <summary>
    /// Helper class for JSON operations using Newtonsoft.Json
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Deserializes JSON string to specified type
        /// </summary>
        public static T FromJson<T>(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to deserialize JSON: {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// Serializes object to JSON string
        /// </summary>
        public static string ToJson(object obj, bool prettyPrint = false)
        {
            try
            {
                return JsonConvert.SerializeObject(obj, prettyPrint ? Formatting.Indented : Formatting.None);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to serialize object: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Attempts to get a value from JSON string by key
        /// </summary>
        public static bool TryGetValue<T>(string json, string key, out T value)
        {
            value = default;
            if (!IsValidJson(json)) return false;

            try
            {
                var jObject = JObject.Parse(json);
                if (jObject.TryGetValue(key, out JToken token))
                {
                    value = token.ToObject<T>();
                    return true;
                }
            }
            catch (JsonException ex)
            {
                Debug.LogError($"JSON parsing error: {ex.Message}");
            }
            
            return false;
        }

        /// <summary>
        /// Gets a value from JSON string by key
        /// </summary>
        public static T GetValue<T>(string json, string key)
        {
            if (!IsValidJson(json)) return default;

            try
            {
                var jObject = JObject.Parse(json);
                return jObject.Value<T>(key);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"JSON parsing error: {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// Extracts a JSON block by name from a JSON string
        /// </summary>
        public static string ExtractBlock(string json, string blockName)
        {
            if (!IsValidJson(json)) return null;

            try
            {
                var jObject = JObject.Parse(json);
                if (jObject.TryGetValue(blockName, out JToken block))
                {
                    return block.ToString(Formatting.None);
                }
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to extract block '{blockName}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Checks if a string is valid JSON
        /// </summary>
        public static bool IsValidJson(string strInput)
        {
            if (string.IsNullOrWhiteSpace(strInput)) return false;
            
            try
            {
                JToken.Parse(strInput);
                return true;
            }
            catch (JsonReaderException)
            {
                return false;
            }
        }

        /// <summary>
        /// Merges multiple JSON objects into one
        /// </summary>
        public static string MergeJsonObjects(params string[] jsonStrings)
        {
            try
            {
                var result = new JObject();
                foreach (var json in jsonStrings)
                {
                    if (IsValidJson(json))
                    {
                        var jObject = JObject.Parse(json);
                        result.Merge(jObject, new JsonMergeSettings 
                        { 
                            MergeArrayHandling = MergeArrayHandling.Union 
                        });
                    }
                }
                return result.ToString(Formatting.None);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to merge JSON objects: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Updates a value in a JSON string
        /// </summary>
        public static string UpdateValue(string json, string key, object newValue)
        {
            if (!IsValidJson(json)) return json;

            try
            {
                var jObject = JObject.Parse(json);
                jObject[key] = JToken.FromObject(newValue);
                return jObject.ToString(Formatting.None);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to update JSON value: {ex.Message}");
                return json;
            }
        }
    }
}