using UnityEngine;
using Molca.ContentPackage.Core;

namespace Molca.ContentPackage.Tests
{
    /// <summary>
    /// Demonstration script showing PackageManifest usage and integration with the test suite.
    /// This can be used to manually verify the PackageManifest functionality in a real scenario.
    /// </summary>
    public class PackageManifestIntegrationDemo : MonoBehaviour
    {
        [Header("Demo Configuration")]
        [SerializeField] private bool runDemoOnStart = false;
        [SerializeField] private string demoPackageId = "demo-package";
        
        [Header("Demo Controls")]
        [SerializeField] private PackageStatus targetStatus = PackageStatus.Available;
        [SerializeField] private float downloadProgress = 0.0f;
        [SerializeField] private string installedVersion = "1.0.0";
        [SerializeField] private string errorMessage = "";

        private PackageManifest _manifest;

        private void Start()
        {
            if (runDemoOnStart)
            {
                RunDemo();
            }
        }

        /// <summary>
        /// Demonstrates basic PackageManifest operations.
        /// </summary>
        [ContextMenu("Run Demo")]
        public void RunDemo()
        {
            Debug.Log("[PackageManifestDemo] Starting PackageManifest integration demo...");
            
            // Initialize manifest
            _manifest = new PackageManifest();
            
            // Show current state
            ShowCurrentState();
            
            // Create demo package state
            CreateDemoPackageState();
            
            // Demonstrate state updates
            DemonstrateStateUpdates();
            
            // Show persistence across instances
            DemonstratePersistence();
            
            Debug.Log("[PackageManifestDemo] Demo completed successfully!");
        }

        /// <summary>
        /// Shows the current state of all packages in the manifest.
        /// </summary>
        [ContextMenu("Show Current State")]
        public void ShowCurrentState()
        {
            if (_manifest == null)
                _manifest = new PackageManifest();
                
            var allStates = _manifest.GetAllStates();
            
            Debug.Log($"[PackageManifestDemo] Current manifest contains {allStates.Count} package(s):");
            
            foreach (var state in allStates)
            {
                Debug.Log($"[PackageManifestDemo] - {state.packageId}: {state.status} " +
                         $"(Progress: {state.downloadProgress:P1}, Version: {state.installedVersion ?? "N/A"})");
            }
            
            if (allStates.Count == 0)
            {
                Debug.Log("[PackageManifestDemo] Manifest is empty.");
            }
        }

        /// <summary>
        /// Creates a demo package state for testing.
        /// </summary>
        [ContextMenu("Create Demo Package")]
        public void CreateDemoPackageState()
        {
            if (_manifest == null)
                _manifest = new PackageManifest();
                
            var demoState = new PackageState(demoPackageId)
            {
                status = targetStatus,
                downloadProgress = downloadProgress,
                installedVersion = string.IsNullOrEmpty(installedVersion) ? null : installedVersion,
                errorMessage = string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
                downloadedBytes = (long)(downloadProgress * 1024 * 1024), // Simulate 1MB total
                totalBytes = 1024 * 1024
            };
            
            _manifest.SetState(demoState);
            
            Debug.Log($"[PackageManifestDemo] Created demo package '{demoPackageId}' with status: {targetStatus}");
        }

        /// <summary>
        /// Demonstrates various state update operations.
        /// </summary>
        private void DemonstrateStateUpdates()
        {
            Debug.Log("[PackageManifestDemo] Demonstrating state updates...");
            
            // Simulate download progress
            var state = _manifest.GetState(demoPackageId);
            if (state != null)
            {
                // Update to downloading
                state.status = PackageStatus.Downloading;
                state.downloadProgress = 0.25f;
                _manifest.SetState(state);
                Debug.Log($"[PackageManifestDemo] Updated to Downloading (25%)");
                
                // Update progress
                state.downloadProgress = 0.75f;
                _manifest.SetState(state);
                Debug.Log($"[PackageManifestDemo] Updated progress to 75%");
                
                // Complete installation
                state.status = PackageStatus.Installed;
                state.downloadProgress = 1.0f;
                state.installedVersion = "1.0.0";
                _manifest.SetState(state);
                Debug.Log($"[PackageManifestDemo] Completed installation");
            }
        }

        /// <summary>
        /// Demonstrates persistence across PackageManifest instances.
        /// </summary>
        private void DemonstratePersistence()
        {
            Debug.Log("[PackageManifestDemo] Demonstrating persistence...");
            
            // Create new manifest instance (should load from file)
            var newManifest = new PackageManifest();
            var persistedState = newManifest.GetState(demoPackageId);
            
            if (persistedState != null)
            {
                Debug.Log($"[PackageManifestDemo] ✅ Persistence verified! Loaded state: {persistedState.status}");
                Debug.Log($"[PackageManifestDemo] - Package ID: {persistedState.packageId}");
                Debug.Log($"[PackageManifestDemo] - Status: {persistedState.status}");
                Debug.Log($"[PackageManifestDemo] - Progress: {persistedState.downloadProgress:P1}");
                Debug.Log($"[PackageManifestDemo] - Version: {persistedState.installedVersion ?? "N/A"}");
                Debug.Log($"[PackageManifestDemo] - Last Modified: {persistedState.lastModified}");
            }
            else
            {
                Debug.LogError("[PackageManifestDemo] ❌ Persistence failed! Could not load state from new instance.");
            }
        }

        /// <summary>
        /// Clears all demo data.
        /// </summary>
        [ContextMenu("Clear Demo Data")]
        public void ClearDemoData()
        {
            if (_manifest == null)
                _manifest = new PackageManifest();
                
            _manifest.Clear();
            Debug.Log("[PackageManifestDemo] Demo data cleared.");
        }

        /// <summary>
        /// Runs the full test suite from this demo.
        /// </summary>
        [ContextMenu("Run Full Tests")]
        public void RunFullTests()
        {
            var testObject = new GameObject("PackageManifestTests_FromDemo");
            var testComponent = testObject.AddComponent<PackageManifestTests>();
            
            try
            {
                testComponent.RunAllTests();
            }
            finally
            {
                DestroyImmediate(testObject);
            }
        }
    }
}