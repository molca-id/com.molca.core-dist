using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Simple, safe benchmark system for testing DataMappingParser performance
    /// </summary>
    public class DataMappingBenchmark : MonoBehaviour
    {
        [Header("Benchmark Settings")]
        [SerializeField, FormerlySerializedAs("objectCount")] private int _objectCount = 100;
        [SerializeField, FormerlySerializedAs("iterations")] private int _iterations = 3;
        [SerializeField, FormerlySerializedAs("runOnStart")] private bool _runOnStart = false;
        [SerializeField, FormerlySerializedAs("includeMemoryProfiling")] private bool _includeMemoryProfiling = true;
        [SerializeField, FormerlySerializedAs("useBlockingMode")] private bool _useBlockingMode = false; // Choose between blocking and non-blocking
        [SerializeField, FormerlySerializedAs("objectsPerFrame")] private int _objectsPerFrame = 10; // How many objects to process per frame in non-blocking mode
        
        [Header("Test Data")]
        [SerializeField, FormerlySerializedAs("testDataMapping")] private DataMapping _testDataMapping;
        [SerializeField, FormerlySerializedAs("testDataModel")] private DataModel _testDataModel;
        
        [Header("Results")]
        [SerializeField, FormerlySerializedAs("averageParseTimeMs")] private float _averageParseTimeMs;
        [SerializeField, FormerlySerializedAs("totalParseTimeMs")] private float _totalParseTimeMs;
        [SerializeField, FormerlySerializedAs("memoryUsageBytes")] private long _memoryUsageBytes;
        [SerializeField, FormerlySerializedAs("successfulParses")] private int _successfulParses;
        [SerializeField, FormerlySerializedAs("failedParses")] private int _failedParses;
        [SerializeField, FormerlySerializedAs("isRunning")] private bool _isRunning = false;
        [SerializeField, FormerlySerializedAs("progress")] private float _progress = 0f;

        private List<string> _testJsonData;
        private Stopwatch _stopwatch;
        private long _initialMemory;
        private Coroutine _benchmarkCoroutine;

        private void Start()
        {
            if (_runOnStart)
            {
                StartCoroutine(DelayedStart());
            }
        }

        private IEnumerator DelayedStart()
        {
            yield return new WaitForSeconds(0.1f);
            RunBenchmark();
        }

        private void OnGUI()
        {
            if (_isRunning)
            {
                var rect = new Rect(10, 10, 300, 20);
                GUI.Box(rect, $"Benchmark Progress: {_progress * 100:F1}%");
                
                var progressRect = new Rect(rect.x + 5, rect.y + 5, (rect.width - 10) * _progress, rect.height - 10);
                GUI.Box(progressRect, "");
                
                var stopRect = new Rect(rect.x + rect.width + 10, rect.y, 60, 20);
                if (GUI.Button(stopRect, "Stop"))
                {
                    StopBenchmark();
                }
            }
        }

        private void OnValidate()
        {
            if (_objectCount < 1) _objectCount = 1;
            if (_objectCount > 1000) _objectCount = 1000;
            if (_iterations < 1) _iterations = 1;
            if (_iterations > 20) _iterations = 20;
            if (_objectsPerFrame < 1) _objectsPerFrame = 1;
            if (_objectsPerFrame > 100) _objectsPerFrame = 100;
        }

        [ContextMenu("Run Benchmark")]
        public void RunBenchmark()
        {
            if (_testDataMapping == null || _testDataModel == null)
            {
                Debug.LogError("Please assign test DataMapping and DataModel first!");
                return;
            }

            if (_isRunning)
            {
                Debug.LogWarning("Benchmark is already running!");
                return;
            }

            Debug.Log("Starting Simple Benchmark");
            Debug.Log($"Objects: {_objectCount:N0}, Iterations: {_iterations}");
            Debug.Log($"Mode: {(_useBlockingMode ? "Blocking" : "Non-blocking")}");
            if (!_useBlockingMode)
            {
                Debug.Log($"Objects per frame: {_objectsPerFrame}");
            }
            
            if (_useBlockingMode)
            {
                RunBlockingBenchmark();
            }
            else
            {
                _benchmarkCoroutine = StartCoroutine(RunBenchmarkCoroutine());
            }
        }

        [ContextMenu("Run Blocking Benchmark")]
        public void RunBlockingBenchmarkDirect()
        {
            if (_testDataMapping == null || _testDataModel == null)
            {
                Debug.LogError("Please assign test DataMapping and DataModel first!");
                return;
            }

            if (_isRunning)
            {
                Debug.LogWarning("Benchmark is already running!");
                return;
            }

            Debug.Log("Starting Blocking Benchmark (Fast Mode)");
            Debug.Log($"Objects: {_objectCount:N0}, Iterations: {_iterations}");
            
            RunBlockingBenchmark();
        }

        [ContextMenu("Test High Performance (1 object/frame)")]
        public void TestHighPerformance()
        {
            _objectsPerFrame = 1;
            Debug.Log($"Set objects per frame to {_objectsPerFrame} for maximum responsiveness");
            RunBenchmark();
        }

        [ContextMenu("Test Balanced (10 objects/frame)")]
        public void TestBalanced()
        {
            _objectsPerFrame = 10;
            Debug.Log($"Set objects per frame to {_objectsPerFrame} for balanced performance");
            RunBenchmark();
        }

        [ContextMenu("Test Fast (50 objects/frame)")]
        public void TestFast()
        {
            _objectsPerFrame = 50;
            Debug.Log($"Set objects per frame to {_objectsPerFrame} for faster execution");
            RunBenchmark();
        }

        /// <summary>
        /// Dynamically adjust objects per frame during runtime
        /// </summary>
        /// <param name="newObjectsPerFrame">New objects per frame value (1-100)</param>
        public void SetObjectsPerFrame(int newObjectsPerFrame)
        {
            if (newObjectsPerFrame < 1 || newObjectsPerFrame > 100)
            {
                Debug.LogWarning($"Objects per frame must be between 1-100. Got: {newObjectsPerFrame}");
                return;
            }

            if (_isRunning)
            {
                Debug.LogWarning("Cannot change objects per frame while benchmark is running!");
                return;
            }

            _objectsPerFrame = newObjectsPerFrame;
            Debug.Log($"Objects per frame set to: {_objectsPerFrame}");
        }

        /// <summary>
        /// Fast blocking benchmark - runs synchronously without yielding
        /// </summary>
        private void RunBlockingBenchmark()
        {
            _isRunning = true;
            _progress = 0f;
            
            Debug.Log("Generating test data (blocking mode)...");
            
            // Generate simple test data
            _testJsonData = new List<string>();
            for (int i = 0; i < _objectCount; i++)
            {
                // Use very simple JSON to avoid parsing issues
                var json = $"{{\"id\":{i},\"name\":\"user_{i}\"}}";
                _testJsonData.Add(json);
                
                _progress = (float)i / _objectCount * 0.3f;
            }
            
            Debug.Log($"Generated {_testJsonData.Count:N0} test objects");
            
            Debug.Log("Running benchmark (blocking mode)...");
            
            // Initialize
            _stopwatch = new Stopwatch();
            _successfulParses = 0;
            _failedParses = 0;
            
            if (_includeMemoryProfiling)
            {
                _initialMemory = GC.GetTotalMemory(false);
            }
            
            var totalTime = 0f;
            var parseTimes = new List<float>();
            
            // Run iterations
            for (int iteration = 0; iteration < _iterations; iteration++)
            {
                Debug.Log($"Iteration {iteration + 1}/{_iterations}");
                
                _stopwatch.Restart();
                
                // Parse objects without yielding
                for (int i = 0; i < _testJsonData.Count; i++)
                {
                    try
                    {
                        var result = _testDataMapping.ParseJson(_testJsonData[i]);
                        if (result.data != null)
                        {
                            _successfulParses++;
                        }
                        else
                        {
                            _failedParses++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Failed to parse object {i}: {ex.Message}");
                        _failedParses++;
                    }
                    
                    _progress = 0.3f + ((float)(iteration * _objectCount + i) / (_objectCount * _iterations)) * 0.7f;
                }
                
                _stopwatch.Stop();
                var iterationTime = _stopwatch.ElapsedMilliseconds;
                totalTime += iterationTime;
                parseTimes.Add(iterationTime);
                
                Debug.Log($"Iteration {iteration + 1} completed in {iterationTime:F2}ms");
            }
            
            // Calculate results
            _totalParseTimeMs = totalTime;
            _averageParseTimeMs = totalTime / _iterations;
            
            if (_includeMemoryProfiling)
            {
                GC.Collect();
                var finalMemory = GC.GetTotalMemory(false);
                _memoryUsageBytes = finalMemory - _initialMemory;
            }
            
            _isRunning = false;
            _progress = 1f;
            
            DisplayResults();
        }

        [ContextMenu("Stop Benchmark")]
        public void StopBenchmark()
        {
            if (_benchmarkCoroutine != null)
            {
                StopCoroutine(_benchmarkCoroutine);
                _benchmarkCoroutine = null;
            }
            
            _isRunning = false;
            _progress = 0f;
            Debug.Log("Benchmark stopped");
        }

        /// <summary>
        /// Force stop any running benchmark (works for both modes)
        /// </summary>
        public void ForceStopBenchmark()
        {
            if (_benchmarkCoroutine != null)
            {
                StopCoroutine(_benchmarkCoroutine);
                _benchmarkCoroutine = null;
            }
            
            _isRunning = false;
            _progress = 0f;
            Debug.Log("Benchmark force stopped");
        }

        private IEnumerator RunBenchmarkCoroutine()
        {
            _isRunning = true;
            _progress = 0f;
            
            Debug.Log("Generating test data...");
            
            // Generate simple test data
            _testJsonData = new List<string>();
            for (int i = 0; i < _objectCount; i++)
            {
                if (!_isRunning) break;
                
                // Use very simple JSON to avoid parsing issues
                var json = $"{{\"id\":{i},\"name\":\"user_{i}\"}}";
                _testJsonData.Add(json);
                
                _progress = (float)i / _objectCount * 0.3f;
                
                // Yield every batch of objects for better responsiveness
                if ((i + 1) % _objectsPerFrame == 0)
                {
                    yield return null;
                }
            }
            
            Debug.Log($"Generated {_testJsonData.Count:N0} test objects");
            
            if (!_isRunning) yield break;
            
            Debug.Log("Running benchmark...");
            
            // Initialize
            _stopwatch = new Stopwatch();
            _successfulParses = 0;
            _failedParses = 0;
            
            if (_includeMemoryProfiling)
            {
                _initialMemory = GC.GetTotalMemory(false);
            }
            
            var totalTime = 0f;
            var parseTimes = new List<float>();
            
            // Run iterations
            for (int iteration = 0; iteration < _iterations; iteration++)
            {
                if (!_isRunning) break;
                
                Debug.Log($"Iteration {iteration + 1}/{_iterations}");
                
                _stopwatch.Restart();
                
                // Parse objects in batches based on objectsPerFrame
                for (int i = 0; i < _testJsonData.Count; i++)
                {
                    if (!_isRunning) break;
                    
                    try
                    {
                        var result = _testDataMapping.ParseJson(_testJsonData[i]);
                        if (result.data != null)
                        {
                            _successfulParses++;
                        }
                        else
                        {
                            _failedParses++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Failed to parse object {i}: {ex.Message}");
                        _failedParses++;
                    }
                    
                    _progress = 0.3f + ((float)(iteration * _objectCount + i) / (_objectCount * _iterations)) * 0.7f;
                    
                    // Yield after processing each batch of objects
                    if ((i + 1) % _objectsPerFrame == 0)
                    {
                        yield return null;
                    }
                }
                
                _stopwatch.Stop();
                var iterationTime = _stopwatch.ElapsedMilliseconds;
                totalTime += iterationTime;
                parseTimes.Add(iterationTime);
                
                Debug.Log($"Iteration {iteration + 1} completed in {iterationTime:F2}ms");
                
                // Yield between iterations
                yield return null;
            }
            
            // Calculate results
            _totalParseTimeMs = totalTime;
            _averageParseTimeMs = totalTime / _iterations;
            
            if (_includeMemoryProfiling)
            {
                GC.Collect();
                var finalMemory = GC.GetTotalMemory(false);
                _memoryUsageBytes = finalMemory - _initialMemory;
            }
            
            _isRunning = false;
            _progress = 1f;
            _benchmarkCoroutine = null;
            
            DisplayResults();
        }

        private void DisplayResults()
        {
            Debug.Log("Benchmark Results:");
            Debug.Log("=====================");
            Debug.Log($"Total objects: {_objectCount * _iterations:N0}");
            Debug.Log($"Successful: {_successfulParses:N0}");
            Debug.Log($"Failed: {_failedParses:N0}");
            Debug.Log($"Total time: {_totalParseTimeMs:F2}ms");
            Debug.Log($"Average per iteration: {_averageParseTimeMs:F2}ms");
            
            if (_totalParseTimeMs > 0)
            {
                var objectsPerSecond = (_objectCount * _iterations) / (_totalParseTimeMs / 1000);
                Debug.Log($"Objects per second: {objectsPerSecond:N0}");
                Debug.Log($"Average per object: {_averageParseTimeMs / _objectCount:F4}ms");
            }
            
            if (_includeMemoryProfiling)
            {
                Debug.Log($"Memory: {_memoryUsageBytes / (1024.0 * 1024.0):F2} MB");
            }
        }
    }
}
