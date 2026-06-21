using System.Collections.Generic;
using System.Linq;
using Molca.Attributes;
using UnityEngine;
using UnityEngine.Serialization;
using Molca.ReferenceSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Molca.Networking.Data
{
    /// <summary>
    /// Defines the structure and schema of a data model.
    /// This class contains a collection of DataField definitions that describe
    /// the structure of data that can be processed by the networking system.
    /// </summary>
    /// <remarks>
    /// SOs-OUT BOUNDARY (locked): a <see cref="DataModel"/> is a ScriptableObject and is
    /// <b>not runtime-resolvable</b> through the reference system. Nothing registers it
    /// into the runtime <see cref="ReferenceManager"/>. Its <see cref="RefId"/> is a
    /// <i>data-identity</i> value (used by editor validation tooling and asset wiring),
    /// not a reference-system handle. The <see cref="IReferenceable{T}"/> implementation
    /// is inert and is scheduled for removal in the next major version — do not call
    /// <c>ReferenceManager.Register</c>/<c>Resolve</c> with a DataModel.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "DataModel", menuName = "Molca/Networking/DataModel", order = 20)]
    public class DataModel : ScriptableObject, IReferenceable<DataModel>
    {
        [SerializeField, FormerlySerializedAs("modelId"), RefId] private string _modelId;
        [SerializeField, FormerlySerializedAs("modelName")] private string _modelName;
        [SerializeField, FormerlySerializedAs("fields")] private List<DataField> _fields;

        /// <summary>
        /// Unique identifier for this data model.
        /// </summary>
        public string ModelId 
        { 
            get => _modelId; 
            set 
            { 
                _modelId = value; 
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    UnityEditor.EditorUtility.SetDirty(this);
                }
                #endif
            }
        }
        public string ModelName { get => _modelName; set => _modelName = value; }

        // IReferenceable implementation — data-identity only; NOT a runtime reference
        // handle. See the type remarks (SOs-out boundary). Removed next major.
        public string RefId { get => _modelId; set => _modelId = value; }
        public string RefType => "DataModel";
        public string DisplayName => string.IsNullOrEmpty(_modelName) ? name : _modelName;



        // Backward compatibility properties
        public string Id
        {
            get => _modelId;
            set => _modelId = value;
        }

        public string TypeId => "DataModel";
        
        /// <summary>
        /// Collection of field definitions that make up this data model.
        /// </summary>
        public IReadOnlyList<DataField> Fields => _fields?.AsReadOnly();
        public void SetFields(List<DataField> newFields)
        {
            _fields = newFields ?? new List<DataField>();
        }

        private void OnValidate()
        {
            // Ensure modelName is not empty
            if (string.IsNullOrEmpty(_modelName))
            {
                _modelName = name;
            }

            // Ensure fields list is never null
            if (_fields == null)
            {
                _fields = new List<DataField>();
            }

            // Note: Reference ID validation is now manual via ReferenceManagerSettings
        }
        
        /// <summary>
        /// Generates a new unique ID for this DataModel
        /// </summary>
        public void GenerateUniqueId()
        {
            ModelId = ReferenceGenerator.GenerateUniqueId(RefType);
        }
        
        /// <summary>
        /// Validates that all field keys are unique.
        /// </summary>
        /// <returns>True if all keys are unique, false otherwise.</returns>
        public bool ValidateFieldKeys()
        {
            if (_fields == null || _fields.Count == 0) return true;

            var keys = _fields.Select(f => f.key).ToList();
            var uniqueKeys = keys.Distinct().ToList();
            
            return keys.Count == uniqueKeys.Count;
        }

        /// <summary>
        /// Gets a list of duplicate field keys if any exist.
        /// </summary>
        /// <returns>List of duplicate keys, empty if none found.</returns>
        public List<string> GetDuplicateKeys()
        {
            var duplicates = new List<string>();
            
            if (_fields == null || _fields.Count == 0) return duplicates;

            var keys = _fields.Select(f => f.key).ToList();
            var duplicateGroups = keys.GroupBy(k => k).Where(g => g.Count() > 1);
            
            foreach (var group in duplicateGroups)
            {
                duplicates.Add(group.Key);
            }

            return duplicates;
        }
        
        /// <summary>
        /// Called when the asset is created in the editor
        /// </summary>
        private void Awake()
        {
            // Ensure we have a unique ID when the asset is created
            if (string.IsNullOrEmpty(_modelId))
            {
                GenerateUniqueId();
            }
        }

        #region Static Data Access Methods
        
        /// <summary>
        /// Gets data from any provider that uses this model by model name
        /// </summary>
        /// <param name="modelName">The name of the model to search for</param>
        /// <returns>The first ImmutableData found, or null if not found</returns>
        public static ImmutableData GetDataByModelName(string _modelName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataModel] DataManager is not initialized");
                return ImmutableData.Unknown;
            }
            
            // Get all providers and find ones that use this model
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var model = DataManager.Instance.GetProviderModel(providerId);
                if (model != null && model.ModelName == _modelName)
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
        /// Gets all data from providers that use this model by model name
        /// </summary>
        /// <param name="modelName">The name of the model to search for</param>
        /// <returns>List of all data from providers using this model</returns>
        public static List<ImmutableData> GetAllDataByModelName(string _modelName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataModel] DataManager is not initialized");
                return new List<ImmutableData>();
            }
            
            var allData = new List<ImmutableData>();
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var model = DataManager.Instance.GetProviderModel(providerId);
                if (model != null && model.ModelName == _modelName)
                {
                    var providerData = DataManager.Instance.GetAllData(providerId);
                    allData.AddRange(providerData);
                }
            }
            
            return allData;
        }
        
        /// <summary>
        /// Gets all providers that use this model by model name
        /// </summary>
        /// <param name="modelName">The name of the model to search for</param>
        /// <returns>List of provider IDs that use this model</returns>
        public static List<string> GetProvidersByModelName(string _modelName)
        {
            if (DataManager.Instance == null)
            {
                Debug.LogWarning("[DataModel] DataManager is not initialized");
                return new List<string>();
            }
            
            var providers = new List<string>();
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var model = DataManager.Instance.GetProviderModel(providerId);
                if (model != null && model.ModelName == _modelName)
                {
                    providers.Add(providerId);
                }
            }
            
            return providers;
        }
        
        /// <summary>
        /// Checks if any provider is using this model by model name
        /// </summary>
        /// <param name="modelName">The name of the model to check</param>
        /// <returns>True if any provider uses this model, false otherwise</returns>
        public static bool IsModelInUse(string _modelName)
        {
            if (DataManager.Instance == null)
            {
                return false;
            }
            
            var providerIds = DataManager.Instance.GetProviderIds();
            
            foreach (string providerId in providerIds)
            {
                var model = DataManager.Instance.GetProviderModel(providerId);
                if (model != null && model.ModelName == _modelName)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        #endregion
    }
}