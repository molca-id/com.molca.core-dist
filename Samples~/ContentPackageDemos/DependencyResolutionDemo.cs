using System.Collections.Generic;
using UnityEngine;
using Molca.ContentPackage.Services;
using Molca.ContentPackage.Core;
using Molca.ContentPackage;

namespace Molca.ContentPackage.Tests
{
    /// <summary>
    /// Demo script showing how dependency resolution works in the PackageService.
    /// This can be attached to a GameObject to test dependency resolution in the editor.
    /// </summary>
    public class DependencyResolutionDemo : MonoBehaviour
    {
        [Header("Test Configuration")]
        [SerializeField] private string packageIdToResolve = "package-c";
        [SerializeField] private ContentPackageSettings testSettings;

        [Header("Results (Read Only)")]
        [SerializeField] private bool resolutionSucceeded;
        [SerializeField] private string[] resolvedDependencies;
        [SerializeField] private string errorMessage;

        private PackageService _packageService;

        [ContextMenu("Test Dependency Resolution")]
        public void TestDependencyResolution()
        {
            if (testSettings == null)
            {
                Debug.LogError("[DependencyResolutionDemo] Test settings not assigned!");
                return;
            }

            // Create package service
            _packageService = new PackageService(testSettings);

            // Test dependency resolution
            var result = _packageService.ResolveDependencies(packageIdToResolve);

            // Update UI fields
            resolutionSucceeded = result.Success;
            errorMessage = result.ErrorMessage;

            if (result.Success)
            {
                resolvedDependencies = result.Data.ToArray();
                Debug.Log($"[DependencyResolutionDemo] Successfully resolved dependencies for '{packageIdToResolve}':");
                for (int i = 0; i < resolvedDependencies.Length; i++)
                {
                    Debug.Log($"  {i + 1}. {resolvedDependencies[i]}");
                }
            }
            else
            {
                resolvedDependencies = new string[0];
                Debug.LogError($"[DependencyResolutionDemo] Failed to resolve dependencies for '{packageIdToResolve}': {errorMessage}");
            }
        }

        [ContextMenu("Test Circular Dependency")]
        public void TestCircularDependency()
        {
            if (testSettings == null)
            {
                Debug.LogError("[DependencyResolutionDemo] Test settings not assigned!");
                return;
            }

            // Create package service
            _packageService = new PackageService(testSettings);

            // Test circular dependency detection
            var result = _packageService.ResolveDependencies("package-circular-1");

            Debug.Log($"[DependencyResolutionDemo] Circular dependency test result:");
            Debug.Log($"  Success: {result.Success}");
            Debug.Log($"  Error: {result.ErrorMessage}");

            if (result.Success)
            {
                Debug.LogWarning("  WARNING: Circular dependency was not detected!");
            }
            else
            {
                Debug.Log("  ✓ Circular dependency correctly detected and prevented");
            }
        }

        [ContextMenu("Test All Scenarios")]
        public void TestAllScenarios()
        {
            if (testSettings == null)
            {
                Debug.LogError("[DependencyResolutionDemo] Test settings not assigned!");
                return;
            }

            _packageService = new PackageService(testSettings);

            Debug.Log("[DependencyResolutionDemo] Testing all dependency resolution scenarios:");

            // Test 1: No dependencies
            TestScenario("package-a", "No dependencies");

            // Test 2: Simple dependency
            TestScenario("package-b", "Simple dependency (A)");

            // Test 3: Chained dependencies
            TestScenario("package-c", "Chained dependencies (A → B → C)");

            // Test 4: Circular dependency
            TestScenario("package-circular-1", "Circular dependency (should fail)");

            // Test 5: Missing dependency
            TestScenario("package-missing-dep", "Missing dependency (should fail)");

            // Test 6: Non-existent package
            TestScenario("non-existent", "Non-existent package (should fail)");

            Debug.Log("[DependencyResolutionDemo] All scenarios tested!");
        }

        private void TestScenario(string packageId, string description)
        {
            var result = _packageService.ResolveDependencies(packageId);
            
            Debug.Log($"  {description}:");
            Debug.Log($"    Package: {packageId}");
            Debug.Log($"    Success: {result.Success}");
            
            if (result.Success)
            {
                Debug.Log($"    Dependencies: [{string.Join(", ", result.Data)}]");
            }
            else
            {
                Debug.Log($"    Error: {result.ErrorMessage}");
            }
        }
    }
}