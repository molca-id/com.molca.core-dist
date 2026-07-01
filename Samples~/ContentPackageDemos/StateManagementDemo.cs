using UnityEngine;
using Molca.ContentPackage.Services;
using Molca.ContentPackage.Core;
using Molca.ContentPackage;

namespace Molca.ContentPackage.Tests
{
    /// <summary>
    /// Demo script showing how the PackageService state management methods work.
    /// This demonstrates the GetOrCreateState and UpdateState functionality.
    /// </summary>
    public class StateManagementDemo : MonoBehaviour
    {
        [Header("Demo Configuration")]
        [SerializeField] private string testPackageId = "demo-package";
        [SerializeField] private ContentPackageSettings settings;

        private PackageService _packageService;

        void Start()
        {
            // Create settings if not assigned
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<ContentPackageSettings>();
                settings.packageConfigs = new System.Collections.Generic.List<ContentPackageSettings.PackageConfig>();
            }

            // Create package service
            _packageService = new PackageService(settings);

            // Subscribe to events
            _packageService.OnPackageStateChanged += OnPackageStateChanged;
            _packageService.OnPackageError += OnPackageError;

            Debug.Log("[StateManagementDemo] PackageService initialized");
            
            // Demonstrate state management
            DemonstrateStateManagement();
        }

        void OnDestroy()
        {
            // Unsubscribe from events
            if (_packageService != null)
            {
                _packageService.OnPackageStateChanged -= OnPackageStateChanged;
                _packageService.OnPackageError -= OnPackageError;
            }
        }

        private void DemonstrateStateManagement()
        {
            Debug.Log("[StateManagementDemo] Starting state management demonstration...");

            // Demonstrate state transitions
            StartCoroutine(DemoStateTransitions());
        }

        private System.Collections.IEnumerator DemoStateTransitions()
        {
            yield return new WaitForSeconds(1f);

            Debug.Log("[StateManagementDemo] 1. Simulating package download start...");
            SimulateUpdateState(testPackageId, PackageStatus.Downloading);

            yield return new WaitForSeconds(2f);

            Debug.Log("[StateManagementDemo] 2. Simulating download completion...");
            SimulateUpdateState(testPackageId, PackageStatus.Installed);

            yield return new WaitForSeconds(2f);

            Debug.Log("[StateManagementDemo] 3. Simulating download failure...");
            SimulateUpdateState(testPackageId, PackageStatus.Failed, "Network connection lost");

            yield return new WaitForSeconds(2f);

            Debug.Log("[StateManagementDemo] 4. Simulating retry (back to downloading)...");
            SimulateUpdateState(testPackageId, PackageStatus.Downloading);

            yield return new WaitForSeconds(2f);

            Debug.Log("[StateManagementDemo] 5. Simulating successful installation...");
            SimulateUpdateState(testPackageId, PackageStatus.Installed);

            yield return new WaitForSeconds(2f);

            Debug.Log("[StateManagementDemo] 6. Simulating update available...");
            SimulateUpdateState(testPackageId, PackageStatus.UpdateAvailable);

            Debug.Log("[StateManagementDemo] State management demonstration complete!");
        }

        /// <summary>
        /// Simulates calling the private UpdateState method using reflection.
        /// In real usage, this would be called internally by PackageService methods.
        /// </summary>
        private void SimulateUpdateState(string packageId, PackageStatus status, string errorMessage = null)
        {
            var method = typeof(PackageService).GetMethod("UpdateState", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method.Invoke(_packageService, new object[] { packageId, status, errorMessage });
        }

        /// <summary>
        /// Event handler for package state changes.
        /// </summary>
        private void OnPackageStateChanged(string packageId, PackageStatus newStatus)
        {
            Debug.Log($"[StateManagementDemo] Package '{packageId}' state changed to: {newStatus}");
        }

        /// <summary>
        /// Event handler for package errors.
        /// </summary>
        private void OnPackageError(string packageId, string errorMessage)
        {
            Debug.LogError($"[StateManagementDemo] Package '{packageId}' error: {errorMessage}");
        }

        [ContextMenu("Test State Persistence")]
        public void TestStatePersistence()
        {
            if (_packageService == null)
            {
                Debug.LogWarning("[StateManagementDemo] PackageService not initialized");
                return;
            }

            Debug.Log("[StateManagementDemo] Testing state persistence...");

            // Create a test state
            SimulateUpdateState("persistence-test", PackageStatus.Installed);

            // Create a new service instance to test loading
            var newService = new PackageService(settings);
            
            // Use reflection to get the state
            var method = typeof(PackageService).GetMethod("GetOrCreateState", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var loadedState = (PackageState)method.Invoke(newService, new object[] { "persistence-test" });

            if (loadedState != null && loadedState.status == PackageStatus.Installed)
            {
                Debug.Log("[StateManagementDemo] ✓ State persistence test PASSED - state was correctly loaded");
            }
            else
            {
                Debug.LogError("[StateManagementDemo] ✗ State persistence test FAILED - state was not loaded correctly");
            }
        }
    }
}