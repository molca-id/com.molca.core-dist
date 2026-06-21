using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Molca.Attributes;
using Molca.ReferenceSystem;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Molca.Networking.Data
{
    /// <summary>
    /// Defines a mapping between source data fields and target field names.
    /// This is typically used to transform data from one format to another,
    /// such as mapping API response fields to internal data model fields.
    /// </summary>
    /// <remarks>
    /// SOs-OUT BOUNDARY (locked): a <see cref="DataMapping"/> is a ScriptableObject and is
    /// <b>not runtime-resolvable</b> through the reference system. Nothing registers it into
    /// the runtime <see cref="ReferenceManager"/>. Its <see cref="RefId"/> is a
    /// <i>data-identity</i> value (used by editor validation tooling and asset wiring), not a
    /// reference-system handle. The <see cref="IReferenceable{T}"/> implementation is inert and
    /// is scheduled for removal in the next major version — do not call
    /// <c>ReferenceManager.Register</c>/<c>Resolve</c> with a DataMapping.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "DataMapping", menuName = "Molca/Networking/DataMapping", order = 20)]
    public class DataMapping : ScriptableObject, IReferenceable<DataMapping>
    {
        [SerializeField, FormerlySerializedAs("mappingId"), RefId] private string _mappingId;
        [SerializeField, FormerlySerializedAs("mappingName")] private string _mappingName;
        [SerializeField] private DataModel _model;
        [SerializeField] private List<MappingField> _fields;

        /// <summary>
        /// Unique identifier for this data mapping.
        /// Automatically generated and managed by ReferenceGenerator.
        /// </summary>
        public string MappingId => _mappingId;
        public string MappingName { get => _mappingName; set => _mappingName = value; }

        // IReferenceable implementation — data-identity only; NOT a runtime reference
        // handle. See the type remarks (SOs-out boundary). Removed next major.
        public string RefId { get => _mappingId; set => _mappingId = value; }
        public string RefType => "DataMapping";
        public string DisplayName => string.IsNullOrEmpty(_mappingName) ? name : _mappingName;



        // Backward compatibility properties
        public string Id
        {
            get => _mappingId;
            set => _mappingId = value;
        }

        public string TypeId => "DataMapping";
        
        /// <summary>
        /// The target DataModel that defines the structure of the output data.
        /// </summary>
        public DataModel Model { get => _model; set => _model = value; }
        
        /// <summary>
        /// The list of field mappings from raw backend fields to DataModel field keys.
        /// This list is automatically populated based on the assigned DataModel.
        /// </summary>
        public IReadOnlyList<MappingField> Fields => _fields?.AsReadOnly();
        
        /// <summary>
        /// Internal method for the editor to update fields.
        /// This method is used by the custom editor to automatically populate
        /// mapping fields based on the assigned DataModel.
        /// </summary>
        /// <param name="newFields">The new list of mapping fields</param>
        public void SetFields(List<MappingField> newFields)
        {
            _fields = newFields ?? new List<MappingField>();
            
            // Mark the object as dirty to ensure Unity saves the changes
            #if UNITY_EDITOR
            if (Application.isPlaying == false)
            {
                EditorUtility.SetDirty(this);
            }
            #endif
        }

        /// <summary>
        /// Parses a JSON string and creates ImmutableData using this mapping configuration.
        /// </summary>
        /// <param name="json">The JSON string to parse</param>
        /// <returns>ImmutableData object or null if parsing fails</returns>
        public ImmutableData ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json) || _model == null)
            {
                Debug.LogWarning("Cannot parse JSON: JSON is empty or DataModel is not assigned");
                return ImmutableData.Unknown;
            }

            // Use the DataMappingParser to parse the JSON with this mapping
            return DataMappingParser.ParseJsonWithMapping(json, this);
        }

        /// <summary>
        /// Extracts all available JSON paths from a sample JSON string.
        /// This helps users see what fields are available for mapping.
        /// </summary>
        /// <param name="json">Sample JSON string</param>
        /// <returns>List of all available JSON paths</returns>
        public static List<string> ExtractJsonPaths(string json)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(json)) return paths;

            try
            {
                var jObject = Newtonsoft.Json.Linq.JObject.Parse(json);
                ExtractPathsRecursive(jObject, "", paths);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Failed to parse JSON for path extraction: {e.Message}");
            }

            return paths;
        }

        private static void ExtractPathsRecursive(Newtonsoft.Json.Linq.JToken token, string currentPath, List<string> paths)
        {
            if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
            {
                foreach (var property in token.Children<Newtonsoft.Json.Linq.JProperty>())
                {
                    string newPath = string.IsNullOrEmpty(currentPath) ? property.Name : $"{currentPath}.{property.Name}";
                    paths.Add(newPath);
                    ExtractPathsRecursive(property.Value, newPath, paths);
                }
            }
            else if (token.Type == Newtonsoft.Json.Linq.JTokenType.Array)
            {
                // For arrays, add paths for array access and process first element if it exists
                if (token.HasValues)
                {
                    var firstItem = token.First;
                    if (firstItem.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        // Add array item access path
                        string arrayItemPath = $"{currentPath}[0]";
                        paths.Add(arrayItemPath);
                        ExtractPathsRecursive(firstItem, arrayItemPath, paths);
                    }
                }
            }
        }

        /// <summary>
        /// Validates if a JSON path exists in the given JSON string.
        /// </summary>
        /// <param name="json">JSON string to validate against</param>
        /// <param name="jsonPath">JSON path to validate</param>
        /// <returns>True if the path exists and has a value</returns>
        public static bool ValidateJsonPath(string json, string jsonPath)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(jsonPath))
                return false;

            try
            {
                var jObject = Newtonsoft.Json.Linq.JObject.Parse(json);
                var token = jObject.SelectToken(jsonPath);
                return token != null && !string.IsNullOrEmpty(token.ToString());
            }
            catch
            {
                return false;
            }
        }

        private void OnValidate()
        {
            // Ensure fields list is never null
            if (_fields == null)
            {
                _fields = new List<MappingField>();
            }

            // Note: Reference ID validation is now manual via ReferenceManagerSettings
        }
        
        /// <summary>
        /// Generates a new unique ID for this DataMapping
        /// </summary>
        public void GenerateUniqueId()
        {
            _mappingId = ReferenceGenerator.GenerateUniqueId(RefType);

            #if UNITY_EDITOR
            if (Application.isPlaying == false)
            {
                EditorUtility.SetDirty(this);
            }
            #endif
        }
        
        /// <summary>
        /// Called when the asset is created in the editor
        /// </summary>
        private void Awake()
        {
            // Ensure we have a unique ID when the asset is created
            if (string.IsNullOrEmpty(_mappingId))
            {
                GenerateUniqueId();
            }
        }
        
        #region Static Data Access Methods
        
        /// <summary>
        /// Gets data from any provider that uses this mapping by mapping name
        /// </summary>
        /// <param name="mappingName">The name of the mapping to search for</param>
        /// <returns>The first ImmutableData found, or null if not found</returns>
        public static ImmutableData GetDataByMappingName(string _mappingName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataMapping] DataManager is not initialized");
                return ImmutableData.Unknown;
            }
            
            // Get all providers and find ones that use this mapping
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var provider = DataManager.Instance.GetProvider(providerId);
                if (provider != null && provider.Mapping != null && provider.Mapping.MappingName == _mappingName)
                {
                    var allData = DataManager.Instance.GetAllData(providerId);
                    if (allData.Count > 0)
                    {
                        return allData[0]; // Return first data entry
                    }
                }
            }
            
            return ImmutableData.Unknown;
        }
        
        /// <summary>
        /// Gets all data from providers that use this mapping by mapping name
        /// </summary>
        /// <param name="mappingName">The name of the mapping to search for</param>
        /// <returns>List of all data from providers using this mapping</returns>
        public static List<ImmutableData> GetAllDataByMappingName(string _mappingName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataMapping] DataManager is not initialized");
                return new List<ImmutableData>();
            }
            
            var allData = new List<ImmutableData>();
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var provider = DataManager.Instance.GetProvider(providerId);
                if (provider != null && provider.Mapping != null && provider.Mapping.MappingName == _mappingName)
                {
                    var providerData = DataManager.Instance.GetAllData(providerId);
                    allData.AddRange(providerData);
                }
            }
            
            return allData;
        }
        
        /// <summary>
        /// Gets all providers that use this mapping by mapping name
        /// </summary>
        /// <param name="mappingName">The name of the mapping to search for</param>
        /// <returns>List of provider IDs that use this mapping</returns>
        public static List<string> GetProvidersByMappingName(string _mappingName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataMapping] DataManager is not initialized");
                return new List<string>();
            }
            
            var providers = new List<string>();
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var provider = DataManager.Instance.GetProvider(providerId);
                if (provider != null && provider.Mapping != null && provider.Mapping.MappingName == _mappingName)
                {
                    providers.Add(providerId);
                }
            }
            
            return providers;
        }
        
        /// <summary>
        /// Checks if any provider is using this mapping by mapping name
        /// </summary>
        /// <param name="mappingName">The name of the mapping to check</param>
        /// <returns>True if any provider uses this mapping, false otherwise</returns>
        public static bool IsMappingInUse(string _mappingName)
        {
            if (DataManager.Instance == null)
            {
                return false;
            }
            
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var provider = DataManager.Instance.GetProvider(providerId);
                if (provider != null && provider.Mapping != null && provider.Mapping.MappingName == _mappingName)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the model name used by a mapping by mapping name
        /// </summary>
        /// <param name="mappingName">The name of the mapping to check</param>
        /// <returns>The model name used by this mapping, or null if not found</returns>
        public static string GetModelNameByMappingName(string _mappingName)
        {
            if (DataManager.Instance == null)
            {
                return null;
            }
            
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var provider = DataManager.Instance.GetProvider(providerId);
                if (provider != null && provider.Mapping != null && provider.Mapping.MappingName == _mappingName)
                {
                    return provider.Mapping.Model?.ModelName;
                }
            }
            
            return null;
        }
        
        #endregion
    }
}