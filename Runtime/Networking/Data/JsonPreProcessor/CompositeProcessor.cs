using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Composite processor that chains multiple pre-processors together
    /// Processes data through each processor in sequence until one can handle it
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "Composite Processor", menuName = "Molca/Networking/JsonPreProcessor/Composite Processor", order = 20)]
    public class CompositeProcessor : JsonPreProcessor
    {
        [Header("Processor Chain")]
        [SerializeField, FormerlySerializedAs("processors")] private List<JsonPreProcessor> _processors = new List<JsonPreProcessor>();
        [SerializeField, FormerlySerializedAs("stopOnFirstMatch")] private bool _stopOnFirstMatch = true;
        [SerializeField, FormerlySerializedAs("logProcessorChain")] private bool _logProcessorChain = true;

        // Cycle/runaway guard: a composite can reference another composite (or itself),
        // so ProcessData can recurse. The thread-static set tracks composites currently
        // on the call stack (re-entry = a cycle); the depth cap is a belt-and-suspenders
        // bound. Thread-static because each processing call runs on one thread.
        private const int MaxChainDepth = 16;
        [System.ThreadStatic] private static HashSet<JsonPreProcessor> _activeChain;
        [System.ThreadStatic] private static int _chainDepth;
        // Separate guard for CanHandle, which also recurses through nested composites
        // (and runs before ProcessData), so it needs its own cycle protection.
        [System.ThreadStatic] private static HashSet<JsonPreProcessor> _canHandleGuard;
        
        public override string GetDescription()
        {
            return $"Composite processor with {_processors.Count} sub-_processors in chain";
        }
        
        public override bool CanHandle(string rawData)
        {
            if (string.IsNullOrEmpty(rawData)) return false;

            // Cycle guard: a composite referencing itself (directly or via another
            // composite) would otherwise recurse forever here.
            bool owner = _canHandleGuard == null;
            if (owner)
                _canHandleGuard = new HashSet<JsonPreProcessor>();
            if (!_canHandleGuard.Add(this))
            {
                if (owner) _canHandleGuard = null;
                return false;
            }

            try
            {
                foreach (var processor in _processors)
                {
                    if (processor != null && processor.CanHandle(rawData))
                        return true;
                }
                return false;
            }
            finally
            {
                _canHandleGuard.Remove(this);
                if (owner) _canHandleGuard = null;
            }
        }
        
        public override string ProcessData(string rawData)
        {
            if (string.IsNullOrEmpty(rawData))
            {
                LogWarning("Raw data is null or empty");
                return "{}";
            }

            // Cycle/depth guard. The outermost composite owns the per-call state.
            bool owner = _activeChain == null;
            if (owner)
            {
                _activeChain = new HashSet<JsonPreProcessor>();
                _chainDepth = 0;
            }

            if (_chainDepth >= MaxChainDepth)
            {
                LogWarning($"Processor chain exceeded max depth ({MaxChainDepth}); returning data unchanged to avoid runaway.");
                return rawData;
            }
            if (!_activeChain.Add(this))
            {
                LogWarning("Cycle detected in processor chain (this composite is already processing); returning data unchanged.");
                return rawData;
            }

            _chainDepth++;
            try
            {
                return ProcessChain(rawData);
            }
            finally
            {
                _chainDepth--;
                _activeChain.Remove(this);
                if (owner)
                    _activeChain = null;
            }
        }

        private string ProcessChain(string rawData)
        {
            LogMessage($"Composite processing: {rawData.Substring(0, Mathf.Min(100, rawData.Length))}...");

            string processedData = rawData;
            JsonPreProcessor lastProcessor = null;
            
            // Process through each processor in the chain
            foreach (var processor in _processors)
            {
                if (processor == null) continue;
                
                if (_logProcessorChain)
                {
                    LogMessage($"Trying processor: {processor.GetType().Name} - {processor.GetDescription()}");
                }
                
                if (processor.CanHandle(processedData))
                {
                    if (_logProcessorChain)
                    {
                        LogMessage($"Processor {processor.GetType().Name} can handle the data");
                    }
                    
                    string beforeProcessing = processedData;
                    processedData = processor.ProcessData(processedData);
                    lastProcessor = processor;
                    
                    if (_logProcessorChain)
                    {
                        LogMessage($"Processor {processor.GetType().Name} result: {processedData.Substring(0, Mathf.Min(100, processedData.Length))}...");
                    }
                    
                    // Stop processing if configured to do so
                    if (_stopOnFirstMatch)
                    {
                        LogMessage($"Stopping at first match: {processor.GetType().Name}");
                        break;
                    }
                }
                else
                {
                    if (_logProcessorChain)
                    {
                        LogMessage($"Processor {processor.GetType().Name} cannot handle the data, skipping");
                    }
                }
            }
            
            if (lastProcessor != null)
            {
                LogMessage($"Final result from {lastProcessor.GetType().Name}: {processedData.Substring(0, Mathf.Min(100, processedData.Length))}...");
            }
            else
            {
                LogWarning("No processor in the chain could handle the data");
            }
            
            return processedData;
        }
        
        /// <summary>
        /// Adds a processor to the chain
        /// </summary>
        public void AddProcessor(JsonPreProcessor processor)
        {
            if (processor != null && !_processors.Contains(processor))
            {
                _processors.Add(processor);
                LogMessage($"Added processor: {processor.GetType().Name}");
            }
        }
        
        /// <summary>
        /// Removes a processor from the chain
        /// </summary>
        public void RemoveProcessor(JsonPreProcessor processor)
        {
            if (_processors.Remove(processor))
            {
                LogMessage($"Removed processor: {processor.GetType().Name}");
            }
        }
        
        /// <summary>
        /// Clears all processors from the chain
        /// </summary>
        public void ClearProcessors()
        {
            _processors.Clear();
            LogMessage("Cleared all processors from chain");
        }
        
        /// <summary>
        /// Gets the number of processors in the chain
        /// </summary>
        public int ProcessorCount => _processors.Count;
        
        /// <summary>
        /// Test method to verify the composite processor is working correctly
        /// </summary>
        [ContextMenu("Test Composite Processor")]
        public void TestCompositeProcessor()
        {
            Debug.Log("=== Composite Processor Test ===");
            
            if (_processors.Count == 0)
            {
                Debug.LogWarning("No processors in the chain to test!");
                return;
            }
            
            // Test 1: SSE data
            string sseData = @"event: line-detail-oee
data: {""working_hour_start"":""2025-08-17 07:00:00"",""oee"":""124.20%""}";
            Debug.Log($"Test 1 - SSE data:");
            Debug.Log($"Input: {sseData}");
            Debug.Log($"Output: {ProcessData(sseData)}");
            
            // Test 2: Complex JSON
            string complexData = @"""CLUSTER LOW VOLUME"": {""historical_performance"": {""target"": 84884}}";
            Debug.Log($"Test 2 - Complex JSON:");
            Debug.Log($"Input: {complexData}");
            Debug.Log($"Output: {ProcessData(complexData)}");
            
            // Test 3: Valid JSON
            string validJson = @"{""name"": ""test"", ""value"": 42}";
            Debug.Log($"Test 3 - Valid JSON:");
            Debug.Log($"Input: {validJson}");
            Debug.Log($"Output: {ProcessData(validJson)}");
            
            Debug.Log("=== End Test ===");
        }
        
        /// <summary>
        /// Test method to show processor chain information
        /// </summary>
        [ContextMenu("Show Processor Chain")]
        public void ShowProcessorChain()
        {
            Debug.Log("=== Processor Chain Information ===");
            Debug.Log($"Total _processors: {_processors.Count}");
            Debug.Log($"Stop on first match: {_stopOnFirstMatch}");
            
            for (int i = 0; i < _processors.Count; i++)
            {
                var processor = _processors[i];
                if (processor != null)
                {
                    Debug.Log($"Processor {i + 1}: {processor.GetType().Name} - {processor.GetDescription()}");
                }
                else
                {
                    Debug.LogWarning($"Processor {i + 1}: NULL (missing reference)");
                }
            }
            
            Debug.Log("=== End Chain Info ===");
        }
    }
}
