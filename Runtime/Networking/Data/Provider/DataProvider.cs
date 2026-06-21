using UnityEngine;
using UnityEngine.Serialization;
using Molca.Attributes;
using Molca.ReferenceSystem;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Base ScriptableObject for data providers (HTTP, etc.).
    /// </summary>
    /// <remarks>
    /// SOs-OUT BOUNDARY (locked): a <see cref="DataProvider"/> is a ScriptableObject and is
    /// <b>not runtime-resolvable</b> through the reference system. Nothing registers it into
    /// the runtime <see cref="ReferenceManager"/>. Its <see cref="RefId"/> is a
    /// <i>data-identity</i> value (used by editor validation tooling and asset wiring), not a
    /// reference-system handle. The <see cref="IReferenceable{T}"/> implementation is inert and
    /// is scheduled for removal in the next major version — do not call
    /// <c>ReferenceManager.Register</c>/<c>Resolve</c> with a DataProvider.
    /// </remarks>
    public abstract class DataProvider : ScriptableObject, IReferenceable<DataProvider>
    {
        [SerializeField, FormerlySerializedAs("providerId"), RefId] private string _providerId;
        [SerializeField, FormerlySerializedAs("providerName")] private string _providerName;
        [SerializeField, FormerlySerializedAs("isArrayResponse")] private bool _isArrayResponse = false;
        [SerializeField, FormerlySerializedAs("mapping")] private DataMapping _mapping;
        [SerializeField, FormerlySerializedAs("jsonPreProcessor")] private JsonPreProcessor _jsonPreProcessor;
        
        [Header("Chunking Settings")]
        [SerializeField, FormerlySerializedAs("enableChunking")] private bool _enableChunking = true;
        [SerializeField, FormerlySerializedAs("chunkSize")] private int _chunkSize = 100;
        [SerializeField, FormerlySerializedAs("chunkDelayMs")] private int _chunkDelayMs = 10; // Milliseconds between chunks
        
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Lifetime token for this provider. Live between <see cref="Activate"/> and
        /// <see cref="Deactivate"/>; already-cancelled outside that window. Key
        /// background loops (auto-fetch, reconnect, streaming reads) on this token.
        /// </summary>
        protected CancellationToken LifetimeToken =>
            _cancellationTokenSource?.Token ?? new CancellationToken(canceled: true);

        public bool IsActive => DataManager.Instance.IsProviderActive(ProviderId);
        public string ProviderId => _providerId;
        public string ProviderName => _providerName;
        public DataMapping Mapping => _mapping;
        public JsonPreProcessor JsonPreProcessor => _jsonPreProcessor;

        // IReferenceable implementation — data-identity only; NOT a runtime reference
        // handle. See the type remarks (SOs-out boundary). Removed next major.
        public string RefId { get => _providerId; set => _providerId = value; }
        public string RefType => "DataProvider";
        public string DisplayName => string.IsNullOrEmpty(_providerName) ? name : _providerName;



        // Backward compatibility properties
        public string Id
        {
            get => _providerId;
            set => _providerId = value;
        }

        public string TypeId => "DataProvider";
        
        public virtual void Activate()
        {
            if (IsActive) return;

            if (!ValidateConfiguration())
            {
                Debug.LogError($"[DataProvider] {name}: Configuration validation failed!");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            DataManager.Instance.RegisterDataProvider(this);
        }

        public virtual void Deactivate()
        {
            if (!IsActive) return;
            
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            
            DataManager.Instance.UnregisterDataProvider(this);
        }

        public abstract void FetchData();
        
        public virtual void OnDataFetched(string data)
        {
            // Explicit fire-and-forget: the async method owns its exceptions.
            _ = ProcessFetchedDataAsync(data);
        }

        private async Awaitable ProcessFetchedDataAsync(string data)
        {
            try
            {
                if (!ValidateDataProcessingPrerequisites(data)) return;

                string processedData = PreProcessData(data);
                var cache = DataManager.Instance.GetProviderCache(ProviderId);

                if (cache == null)
                {
                    Debug.LogWarning($"[DataProvider] {name}: Cache not found for provider key: {ProviderId}");
                    return;
                }

                if (_isArrayResponse)
                {
                    await ProcessArrayDataAsync(processedData, cache);
                }
                else
                {
                    ProcessSingleData(processedData, cache);
                }
            }
            catch (System.OperationCanceledException)
            {
                Debug.Log($"[DataProvider] {name}: Data processing was cancelled");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DataProvider] {name}: Error processing data: {e}");
            }
        }
        
        private bool ValidateDataProcessingPrerequisites(string data)
        {
            if (_mapping == null)
            {
                Debug.LogError($"[DataProvider] {name}: DataMapping is null!");
                return false;
            }
            
            if (DataManager.Instance == null)
            {
                Debug.LogError($"[DataProvider] {name}: DataManager.Instance is null!");
                return false;
            }
            
            if (string.IsNullOrEmpty(data))
            {
                Debug.LogWarning($"[DataProvider] {name}: Received null or empty data");
                return false;
            }
            
            return true;
        }
        
        private string PreProcessData(string data)
        {
            if (_jsonPreProcessor == null) return data;
            
            string processedData = _jsonPreProcessor.ProcessData(data);
            Debug.Log($"[DataProvider] {name}: Data pre-processed by {_jsonPreProcessor.GetType().Name}");
            return processedData;
        }
        
        private async Awaitable ProcessArrayDataAsync(string processedData, DataCache cache)
        {
            var arrayData = JsonConvert.DeserializeObject<JArray>(processedData);
            if (arrayData == null)
            {
                Debug.LogError($"[DataProvider] {name}: Failed to parse array data: {processedData}");
                return;
            }

            Debug.Log($"[DataProvider] {name}: Processing array with {arrayData.Count} elements");
            
            if (ShouldUseChunking(arrayData.Count))
            {
                await ProcessArrayInChunksAsync(arrayData, cache);
            }
            else
            {
                ProcessArrayNormally(arrayData, cache);
            }
        }
        
        private bool ShouldUseChunking(int arrayLength)
        {
            return _enableChunking && arrayLength > _chunkSize;
        }
        
        private async Awaitable ProcessArrayInChunksAsync(JArray arrayData, DataCache cache)
        {
            int totalElements = arrayData.Count;
            int processedElements = 0;
            
            Debug.Log($"[DataProvider] {name}: Starting chunked processing of {totalElements} elements (chunk size: {_chunkSize})");
            
            for (int i = 0; i < totalElements; i += _chunkSize)
            {
                if (_cancellationTokenSource?.Token.IsCancellationRequested == true)
                    return;
                
                int currentChunkSize = Mathf.Min(_chunkSize, totalElements - i);
                int endIndex = i + currentChunkSize;
                
                Debug.Log($"[DataProvider] {name}: Processing chunk {i / _chunkSize + 1} ({i + 1}-{endIndex} of {totalElements})");
                
                processedElements += ProcessChunk(arrayData, i, endIndex, cache);
                
                // Yield control back to Unity's main thread
                if (_chunkDelayMs > 0)
                {
                    await Awaitable.WaitForSecondsAsync(_chunkDelayMs / 1000f);
                    if (_cancellationTokenSource?.Token.IsCancellationRequested == true)
                        return;
                }
                else
                {
                    await Awaitable.NextFrameAsync();
                }
                
                // Log progress periodically
                if (processedElements % (_chunkSize * 5) == 0 || processedElements >= totalElements)
                {
                    Debug.Log($"[DataProvider] {name}: Progress: {processedElements}/{totalElements} elements processed");
                }
            }
            
            Debug.Log($"[DataProvider] {name}: Completed chunked processing of {totalElements} elements");
        }
        
        private int ProcessChunk(JArray arrayData, int startIndex, int endIndex, DataCache cache)
        {
            int processedCount = 0;
            
            for (int j = startIndex; j < endIndex; j++)
            {
                if (ProcessArrayElement(arrayData[j], j + 1, cache))
                {
                    processedCount++;
                }
            }
            
            return processedCount;
        }
        
        private void ProcessArrayNormally(JArray arrayData, DataCache cache)
        {
            Debug.Log($"[DataProvider] {name}: Processing array normally");
            
            for (int i = 0; i < arrayData.Count; i++)
            {
                ProcessArrayElement(arrayData[i], i + 1, cache);
            }
        }
        
        private bool ProcessArrayElement(JToken item, int elementNumber, DataCache cache)
        {
            try
            {
                string itemJson = item.ToString(Formatting.None);
                var parsedData = _mapping.ParseJson(itemJson);
                
                if (parsedData.IsValid)
                {
                    cache.AddData(parsedData);
                    DataManager.TriggerDataUpdated(ProviderId, parsedData);
                    return true;
                }
                else
                {
                    Debug.LogWarning($"[DataProvider] {name}: Invalid data in array element {elementNumber}: {itemJson}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DataProvider] {name}: Error processing array element {elementNumber}: {e.Message}");
            }
            
            return false;
        }
        
        private void ProcessSingleData(string processedData, DataCache cache)
        {
            var parsedData = _mapping.ParseJson(processedData);
            if (parsedData.IsValid)
            {
                Debug.Log($"[DataProvider] {name}: Adding single data to cache. ModelID: {parsedData.modelId}");
                cache.AddData(parsedData);
                DataManager.TriggerDataUpdated(ProviderId, parsedData);
            }
            else
            {
                Debug.LogWarning($"[DataProvider] {name}: Parsed data is not valid");
                Debug.LogWarning($"[DataProvider] {name}: Processed data: {processedData}");
            }
        }
        
        public virtual bool ValidateConfiguration()
        {
            if (_mapping == null)
            {
                Debug.LogError($"[DataProvider] {name}: DataMapping is not assigned!");
                return false;
            }
            
            if (_mapping.Model == null)
            {
                Debug.LogError($"[DataProvider] {name}: DataModel is not assigned!");
                return false;
            }
            
            return ValidateChunkingConfiguration();
        }
        
        private bool ValidateChunkingConfiguration()
        {
            if (!_enableChunking) return true;
            
            if (_chunkSize <= 0)
            {
                Debug.LogError($"[DataProvider] {name}: Chunk size must be greater than 0 (current: {_chunkSize})");
                return false;
            }
            
            if (_chunkDelayMs < 0)
            {
                Debug.LogError($"[DataProvider] {name}: Chunk delay cannot be negative (current: {_chunkDelayMs}ms)");
                return false;
            }
            
            if (_chunkDelayMs > 1000)
            {
                Debug.LogWarning($"[DataProvider] {name}: Chunk delay is very high ({_chunkDelayMs}ms), this may cause slow processing");
            }
            
            return true;
        }
        
        private void OnValidate()
        {
            // Note: Reference ID validation is now manual via ReferenceManagerSettings
        }
        
        public void GenerateUniqueId()
        {
            _providerId = ReferenceGenerator.GenerateUniqueId(RefType);

            #if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(this);
            }
            #endif
        }
        
        private void Awake()
        {
            if (string.IsNullOrEmpty(_providerId))
            {
                GenerateUniqueId();
            }
        }
        
        private void OnDestroy()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }
}
