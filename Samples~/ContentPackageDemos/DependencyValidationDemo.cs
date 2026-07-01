using System.Collections.Generic;
using UnityEngine;
using Molca.ContentPackage.Services;
using Molca.ContentPackage.Core;
using Molca.ContentPackage;

namespace Molca.ContentPackage.Tests
{
    /// <summary>
    /// Demonstration script showing how dependency validation works in the PackageService.
    /// This script can be attached to a GameObject to test the functionality in the Unity Editor.
    /// </summary>
    public class DependencyValidationDemo : MonoBehaviour
    {
        [Header("Demo Configuration")]
        [SerializeField] private bool runDemoOnStart = false;
        
        private PackageService _packageService;
        private ContentPackageSettings _settings;

        void Start()
        {
            if (runDemoOnStart)
            {
                RunDemo();
            }
        }

        [ContextMenu("Run Dependency Validation Demo")]
        public void RunDemo()
        {
            Debug.Log("=== Dependency Validation Demo ===");
            
            SetupTestEnvironment();
            DemoBasicValidation();
            DemoComplexDependencyChain();
            DemoUninstallationValidation();
            
            Debug.Log("=== Demo Complete ===");
        }

        private void SetupTestEnvironment()
        {
            Debug.Log("Setting up test environment...");
            
            // Create test settings with a dependency chain
            _settings = ScriptableObject.CreateInstance<ContentPackageSettings>();
            _settings.packageConfigs = new List<ContentPackageSettings.PackageConfig>
            {
                CreatePackageConfig("base-game", "Base Game", new string[0]),
                CreatePackageConfig("dlc-weapons", "Weapons DLC", new string[] { "base-game" }),
                CreatePackageConfig("dlc-maps", "Maps DLC", new string[] { "base-game" }),
                CreatePackageConfig("dlc-premium", "Premium DLC", new string[] { "base-game", "dlc-weapons" }),
                CreatePackageConfig("standalone-mod", "Standalone Mod", new string[0])
            };

            _packageService = new PackageService(_settings);
            
            Debug.Log("Test environment created with 5 packages");
        }

        private void DemoBasicValidation()
        {
            Debug.Log("\n--- Basic Validation Demo ---");
            
            // Test 1: Validate uninstalling a package with no dependents
            Debug.Log("Test 1: Validating standalone package uninstallation");
            SimulatePackageInstalled("standalone-mod");
            
            var result = _packageService.ValidatePackageUninstallation("standalone-mod");
            Debug.Log($"Result: {(result.Success ? "SUCCESS" : "FAILED")} - {result.ErrorMessage ?? "No error"}");
            
            // Test 2: Check dependents for standalone package
            var dependents = _packageService.GetInstalledDependents("standalone-mod");
            Debug.Log($"Dependents for standalone-mod: {dependents.Count}");
        }

        private void DemoComplexDependencyChain()
        {
            Debug.Log("\n--- Complex Dependency Chain Demo ---");
            
            // Install a complex dependency chain
            SimulatePackageInstalled("base-game");
            SimulatePackageInstalled("dlc-weapons");
            SimulatePackageInstalled("dlc-maps");
            SimulatePackageInstalled("dlc-premium");
            
            Debug.Log("Installed packages: base-game, dlc-weapons, dlc-maps, dlc-premium");
            Debug.Log("Dependency chain: dlc-premium → [base-game, dlc-weapons], dlc-weapons → [base-game], dlc-maps → [base-game]");
            
            // Check dependents for base-game (should have 3)
            var baseGameDependents = _packageService.GetInstalledDependents("base-game");
            Debug.Log($"Dependents for base-game: {baseGameDependents.Count}");
            foreach (var dependent in baseGameDependents)
            {
                Debug.Log($"  - {dependent.displayName} ({dependent.packageId})");
            }
            
            // Check dependents for dlc-weapons (should have 1)
            var weaponsDependents = _packageService.GetInstalledDependents("dlc-weapons");
            Debug.Log($"Dependents for dlc-weapons: {weaponsDependents.Count}");
            foreach (var dependent in weaponsDependents)
            {
                Debug.Log($"  - {dependent.displayName} ({dependent.packageId})");
            }
        }

        private void DemoUninstallationValidation()
        {
            Debug.Log("\n--- Uninstallation Validation Demo ---");
            
            // Try to uninstall base-game (should fail - has dependents)
            Debug.Log("Attempting to validate uninstallation of base-game (has dependents):");
            var result1 = _packageService.ValidatePackageUninstallation("base-game");
            Debug.Log($"Result: {(result1.Success ? "SUCCESS" : "FAILED")}");
            if (!result1.Success)
            {
                Debug.Log($"Error: {result1.ErrorMessage}");
            }
            
            // Try to uninstall dlc-premium (should succeed - no dependents)
            Debug.Log("\nAttempting to validate uninstallation of dlc-premium (no dependents):");
            var result2 = _packageService.ValidatePackageUninstallation("dlc-premium");
            Debug.Log($"Result: {(result2.Success ? "SUCCESS" : "FAILED")} - {result2.ErrorMessage ?? "No error"}");
            
            // Try to uninstall dlc-weapons (should fail - dlc-premium depends on it)
            Debug.Log("\nAttempting to validate uninstallation of dlc-weapons (dlc-premium depends on it):");
            var result3 = _packageService.ValidatePackageUninstallation("dlc-weapons");
            Debug.Log($"Result: {(result3.Success ? "SUCCESS" : "FAILED")}");
            if (!result3.Success)
            {
                Debug.Log($"Error: {result3.ErrorMessage}");
            }
        }

        private ContentPackageSettings.PackageConfig CreatePackageConfig(string packageId, string displayName, string[] dependencies)
        {
            var config = new ContentPackageSettings.PackageConfig
            {
                packageId = packageId,
                displayName = displayName,
                isVisible = true,
                addressableLabels = new string[] { packageId + "-label" },
                metadata = new ContentPackageSettings.PackageMetadata
                {
                    version = "1.0.0",
                    description = $"Demo package: {displayName}",
                }
            };

            if (dependencies != null && dependencies.Length > 0)
            {
                config.dependencies = new ContentPackageSettings.PackageDependency[dependencies.Length];
                for (int i = 0; i < dependencies.Length; i++)
                {
                    config.dependencies[i] = new ContentPackageSettings.PackageDependency { packageId = dependencies[i] };
                }
            }

            return config;
        }

        private void SimulatePackageInstalled(string packageId)
        {
            var state = _packageService.GetPackageState(packageId);
            if (state != null)
            {
                // Use reflection to set the status since UpdateState is private
                var statusField = typeof(PackageState).GetField("status");
                statusField.SetValue(state, PackageStatus.Installed);
                
                var versionField = typeof(PackageState).GetField("installedVersion");
                versionField.SetValue(state, "1.0.0");
                
                Debug.Log($"Simulated installation of package: {packageId}");
            }
        }

        void OnDestroy()
        {
            if (_settings != null)
            {
                DestroyImmediate(_settings);
            }
        }
    }
}