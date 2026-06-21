using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using Molca.Utils;
using System.Linq;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Handles parsing JSON strings through DataMapping configurations to create ImmutableData objects.
    /// This class is responsible for mapping raw JSON data to structured data based on DataMapping rules.
    /// </summary>
    public static class DataMappingParser
    {
        private const int MAX_RECURSION_DEPTH = 5; // Prevent infinite recursion
        
        /// <summary>
        /// Toggle to enable/disable debug logging
        /// </summary>
        public static bool EnableDebugLogging { get; set; } = false;
        
        /// <summary>
        /// Enable debug logging
        /// </summary>
        public static void EnableLogging() => EnableDebugLogging = true;
        
        /// <summary>
        /// Disable debug logging
        /// </summary>
        public static void DisableLogging() => EnableDebugLogging = false;
        
        /// <summary>
        /// Temporarily enable logging for a specific operation
        /// </summary>
        /// <param name="action">The action to perform with logging enabled</param>
        public static void WithLogging(Action action)
        {
            var wasEnabled = EnableDebugLogging;
            EnableDebugLogging = true;
            try
            {
                action();
            }
            finally
            {
                EnableDebugLogging = wasEnabled;
            }
        }
        
        /// <summary>
        /// Temporarily enable logging for a specific operation with return value
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="func">The function to perform with logging enabled</param>
        /// <returns>The result of the function</returns>
        public static T WithLogging<T>(Func<T> func)
        {
            var wasEnabled = EnableDebugLogging;
            EnableDebugLogging = true;
            try
            {
                return func();
            }
            finally
            {
                EnableDebugLogging = wasEnabled;
            }
        }
        
        /// <summary>
        /// Centralized debug logging method
        /// </summary>
        private static void LogMessage(string message, LogType logType = LogType.Log)
        {
            if (!EnableDebugLogging) return;
            
            var prefix = "[DataMappingParser]";
            switch (logType)
            {
                case LogType.Log:
                    Debug.Log($"{prefix} {message}");
                    break;
                case LogType.Warning:
                    Debug.LogWarning($"{prefix} {message}");
                    break;
                case LogType.Error:
                    Debug.LogError($"{prefix} {message}");
                    break;
            }
        }
        
        /// <summary>
        /// Log types for the centralized logging system
        /// </summary>
        private enum LogType
        {
            Log,
            Warning,
            Error
        }
        
        /// <summary>
        /// Parses JSON string through DataMapping to create ImmutableData
        /// </summary>
        /// <param name="json">JSON string to parse</param>
        /// <param name="dataMapping">DataMapping configuration</param>
        /// <returns>ImmutableData object or null if parsing fails</returns>
        public static ImmutableData ParseJsonWithMapping(string json, DataMapping dataMapping)
        {
            return ParseJsonWithMapping(json, dataMapping, 0);
        }
        
        /// <summary>
        /// Internal method with recursion depth tracking
        /// </summary>
        private static ImmutableData ParseJsonWithMapping(string json, DataMapping dataMapping, int depth)
        {
            if (!JsonHelper.IsValidJson(json) || dataMapping?.Model == null)
            {
                LogMessage("Invalid JSON or DataMapping configuration", LogType.Error);
                return ImmutableData.Unknown;
            }
            
            // Prevent infinite recursion
            if (depth >= MAX_RECURSION_DEPTH)
            {
                LogMessage($"Maximum recursion depth ({MAX_RECURSION_DEPTH}) reached. Stopping to prevent stack overflow.", LogType.Warning);
                return ImmutableData.Unknown;
            }

            try
            {
                var jObject = JObject.Parse(json);
                var mappedData = new Dictionary<string, object>();

                foreach (var mappingField in dataMapping.Fields)
                {
                    if (string.IsNullOrEmpty(mappingField.from) || string.IsNullOrEmpty(mappingField.to))
                        continue;

                    // Get the source value from JSON using the "from" field
                    // Support both simple keys and JSON paths (dot notation)
                    JToken sourceToken = GetValueByPath(jObject, mappingField.from);

                    if (sourceToken != null)
                    {
                        var dataField = GetDataFieldByKey(dataMapping.Model, mappingField.to);
                        if (dataField != null)
                        {
                            var mappedValue = MapJsonValue(sourceToken, dataField, mappingField.nestedMapping, depth + 1);
                            if (mappedValue != null)
                            {
                                mappedData[mappingField.to] = mappedValue;
                            }
                        }
                    }
                }

                return new ImmutableData(dataMapping.Model.ModelId, mappedData);
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to parse JSON with mapping: {ex.Message}", LogType.Error);
                return ImmutableData.Unknown;
            }
        }

        /// <summary>
        /// Maps a JSON token to the appropriate type based on DataField configuration
        /// </summary>
        private static object MapJsonValue(JToken token, DataField dataField, DataMapping nestedMapping, int depth)
        {
            if (token == null || dataField == null)
                return null;

            try
            {
                switch (dataField.type)
                {
                    case DataType.String:
                        return token.ToString();

                    case DataType.Int:
                        return token.Value<int>();

                    case DataType.Float:
                        return token.Value<float>();

                    case DataType.Bool:
                        return token.Value<bool>();

                    case DataType.Model:
                        if (dataField.isArray)
                        {
                            return MapJsonArrayToModel(token, dataField.model, nestedMapping, depth);
                        }
                        else
                        {
                            return MapJsonObjectToModel(token, dataField.model, nestedMapping, depth);
                        }

                    default:
                        return token.ToString();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to map value for field '{dataField.key}': {ex.Message}", LogType.Warning);
                return null;
            }
        }

        /// <summary>
        /// Maps a JSON array to a list of model objects
        /// </summary>
        private static List<ImmutableData> MapJsonArrayToModel(JToken token, DataModel model, DataMapping nestedMapping, int depth)
        {
            LogMessage($"[DataMappingParser] Processing array for model: {model?.ModelName}, token type: {token.Type}, depth: {depth}", LogType.Log);
            
            if (token.Type != JTokenType.Array)
            {
                LogMessage($"[DataMappingParser] Expected array for field with model {model?.ModelName}, but got {token.Type}", LogType.Warning);
                return new List<ImmutableData>();
            }

            if (model == null)
            {
                LogMessage($"[DataMappingParser] Cannot map array to null model", LogType.Error);
                return new List<ImmutableData>();
            }

            var result = new List<ImmutableData>();
            int index = 0;
            
            LogMessage($"[DataMappingParser] Array has {token.Count()} items", LogType.Log);
            
            foreach (var item in token)
            {
                try
                {
                    LogMessage($"[DataMappingParser] Processing array item {index}, type: {item.Type}", LogType.Log);
                    var modelData = MapJsonObjectToModel(item, model, nestedMapping, depth);
                    if (modelData.IsValid)
                    {
                        result.Add(modelData);
                        LogMessage($"[DataMappingParser] Successfully added item {index} to result", LogType.Log);
                    }
                    else
                    {
                        LogMessage($"[DataMappingParser] Failed to map array item at index {index} for model {model.ModelName}", LogType.Warning);
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"[DataMappingParser] Error mapping array item at index {index} for model {model.ModelName}: {ex.Message}", LogType.Error);
                }
                index++;
            }
            
            LogMessage($"[DataMappingParser] Successfully mapped {result.Count} items from array for model {model.ModelName}. Result type: {result.GetType().Name}", LogType.Log);
            return result;
        }

        /// <summary>
        /// Maps a JSON object to a model object
        /// </summary>
        private static ImmutableData MapJsonObjectToModel(JToken token, DataModel model, DataMapping nestedMapping, int depth)
        {
            if (token.Type != JTokenType.Object)
                return ImmutableData.Unknown;

            // Use nested mapping if available and depth is safe
            if (nestedMapping != null && depth < MAX_RECURSION_DEPTH)
            {
                try
                {
                    return ParseJsonWithMapping(token.ToString(), nestedMapping, depth);
                }
                catch (Exception ex)
                {
                    LogMessage($"[DataMappingParser] Failed to use nested mapping: {ex.Message}. Falling back to basic mapping.", LogType.Warning);
                }
            }
            
            // Fallback: create basic mapping from model fields (no recursion)
            var mappedData = new Dictionary<string, object>();
            foreach (var field in model.Fields)
            {
                try
                {
                    var fieldToken = token.SelectToken(field.key);
                    if (fieldToken != null)
                    {
                        // For Model types, handle them properly even in fallback
                        if (field.type == DataType.Model)
                        {
                            if (field.isArray)
                            {
                                // Handle array of models in fallback
                                var arrayResult = MapJsonArrayToModel(fieldToken, field.model, null, depth);
                                if (arrayResult != null && arrayResult.Count > 0)
                                {
                                    mappedData[field.key] = arrayResult;
                                }
                            }
                            else
                            {
                                // Handle single model in fallback
                                var modelResult = MapJsonObjectToModel(fieldToken, field.model, null, depth);
                                if (modelResult.IsValid)
                                {
                                    mappedData[field.key] = modelResult;
                                }
                            }
                        }
                        else
                        {
                            var value = MapJsonValue(fieldToken, field, null, depth);
                            if (value != null)
                            {
                                mappedData[field.key] = value;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"[DataMappingParser] Failed to map field '{field.key}': {ex.Message}", LogType.Warning);
                }
            }
            return new ImmutableData(model.ModelId, mappedData);
        }

        /// <summary>
        /// Gets a value from JSON using either simple key or JSON path (dot notation)
        /// </summary>
        /// <param name="jObject">The JSON object to search in</param>
        /// <param name="path">The key or path to find (e.g., "user.name" or "data.items[0].title")</param>
        /// <returns>The JToken at the specified path, or null if not found</returns>
        private static JToken GetValueByPath(JObject jObject, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                // First try using JObject's built-in path resolution
                var token = jObject.SelectToken(path);
                if (token != null)
                {
                    return token;
                }

                // Fallback: try simple key lookup for backward compatibility
                if (jObject.TryGetValue(path, out token))
                {
                    return token;
                }

                // Handle array indexing in paths like "items[0]"
                if (path.Contains("[") && path.Contains("]"))
                {
                    return ResolveArrayPath(jObject, path);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to resolve JSON path '{path}': {ex.Message}", LogType.Warning);
                return null;
            }
        }

        /// <summary>
        /// Resolves paths that contain array indexing like "items[0].name"
        /// </summary>
        private static JToken ResolveArrayPath(JObject jObject, string path)
        {
            try
            {
                // Split path into segments
                string[] segments = path.Split(new[] { '.', '[' }, StringSplitOptions.RemoveEmptyEntries);

                JToken current = jObject;

                foreach (string segment in segments)
                {
                    if (segment.Contains("]"))
                    {
                        // Handle array indexing: "items]0" becomes "items" and index 0
                        string arrayName = segment.Replace("]", "");
                        string indexPart = segment.Substring(segment.IndexOf("]") + 1);

                        if (int.TryParse(indexPart, out int index))
                        {
                            // Navigate to array
                            if (current[arrayName] is JArray array && index >= 0 && index < array.Count)
                            {
                                current = array[index];
                            }
                            else
                            {
                                return null; // Invalid array access
                            }
                        }
                    }
                    else
                    {
                        // Regular property access
                        if (current[segment] != null)
                        {
                            current = current[segment];
                        }
                        else
                        {
                            return null; // Property not found
                        }
                    }
                }

                return current;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a DataField by its key from a DataModel
        /// </summary>
        private static DataField GetDataFieldByKey(DataModel model, string key)
        {
            if (model?.Fields == null) return null;

            foreach (var field in model.Fields)
            {
                if (field.key == key)
                    return field;
            }
            return null;
        }
    }
}
