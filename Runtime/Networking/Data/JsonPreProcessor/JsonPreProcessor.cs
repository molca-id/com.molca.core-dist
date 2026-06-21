using System;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Abstract base class for JSON pre-processors
    /// Each specialized pre-processor handles a specific data format
    /// </summary>
    public abstract class JsonPreProcessor : ScriptableObject
    {
        [Header("Debug")]
        [SerializeField] protected bool logProcessingSteps = true;
        
        /// <summary>
        /// Processes raw data and converts it to valid JSON
        /// </summary>
        /// <param name="rawData">The raw data string to process</param>
        /// <returns>Processed JSON string ready for parsing</returns>
        public abstract string ProcessData(string rawData);
        
        /// <summary>
        /// Gets a human-readable description of what this pre-processor does
        /// </summary>
        public abstract string GetDescription();
        
        /// <summary>
        /// Checks if this pre-processor can handle the given data format
        /// </summary>
        /// <param name="rawData">The raw data to check</param>
        /// <returns>True if this pre-processor can handle the data</returns>
        public abstract bool CanHandle(string rawData);
        
        /// <summary>
        /// Logs a message if logging is enabled
        /// </summary>
        protected void LogMessage(string message)
        {
            if (logProcessingSteps)
            {
                Debug.Log($"[{GetType().Name}] {message}");
            }
        }
        
        /// <summary>
        /// Logs a warning if logging is enabled
        /// </summary>
        protected void LogWarning(string message)
        {
            if (logProcessingSteps)
            {
                Debug.LogWarning($"[{GetType().Name}] {message}");
            }
        }
        
        /// <summary>
        /// Logs an error if logging is enabled
        /// </summary>
        protected void LogError(string message)
        {
            if (logProcessingSteps)
            {
                Debug.LogError($"[{GetType().Name}] {message}");
            }
        }
    }
}
