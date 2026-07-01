using System.IO;
using UnityEngine;
using UnityEditor;
using Molca.ContentPackage.Tests;

namespace Molca.ContentPackage.Tests.Editor
{
    /// <summary>
    /// Editor utility to run PackageManifest tests from the Unity Editor menu.
    /// </summary>
    public static class PackageManifestTestRunner
    {
        /// <summary>
        /// Menu item to run PackageManifest tests from the Unity Editor.
        /// </summary>
        [MenuItem("Molca/Content Package/Run PackageManifest Tests")]
        public static void RunPackageManifestTests()
        {
            Debug.Log("[PackageManifestTestRunner] Starting PackageManifest tests from Editor...");
            
            // Create a temporary GameObject with the test component
            GameObject testObject = new GameObject("PackageManifestTests_Temp");
            PackageManifestTests testComponent = testObject.AddComponent<PackageManifestTests>();
            
            try
            {
                // Run the tests
                testComponent.RunAllTests();
                
                Debug.Log("[PackageManifestTestRunner] Tests completed. Check console for results.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PackageManifestTestRunner] Error running tests: {ex.Message}");
            }
            finally
            {
                // Clean up the temporary GameObject
                Object.DestroyImmediate(testObject);
            }
        }

        /// <summary>
        /// Menu item to clean up the manifest file.
        /// </summary>
        [MenuItem("Molca/Content Package/Clean Test Data")]
        public static void CleanTestData()
        {
            var path = Path.Combine(Application.persistentDataPath, "Molca", "packages_manifest.json");
            if (File.Exists(path))
            {
                File.Delete(path);
                Debug.Log($"[PackageManifestTestRunner] Manifest file deleted: {path}");
            }
            else
            {
                Debug.Log("[PackageManifestTestRunner] No manifest file to delete.");
            }
        }

        /// <summary>
        /// Menu item to validate PackageManifest implementation.
        /// </summary>
        [MenuItem("Molca/Content Package/Validate PackageManifest")]
        public static void ValidatePackageManifest()
        {
            Debug.Log("[PackageManifestTestRunner] Validating PackageManifest implementation...");
            
            try
            {
                // Basic validation - create instance and verify it doesn't crash
                var manifest = new Molca.ContentPackage.Core.PackageManifest();
                var states = manifest.GetAllStates();
                
                Debug.Log($"[PackageManifestTestRunner] ✅ PackageManifest validation passed. Current state count: {states.Count}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PackageManifestTestRunner] ❌ PackageManifest validation failed: {ex.Message}");
            }
        }
    }
}