using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Molca.ContentPackage.Core;

namespace Molca.ContentPackage.Tests
{
    /// <summary>
    /// Unit tests for PackageManifest persistence functionality.
    /// Tests save/load cycle, corrupted JSON handling, empty manifest creation, and PlayerPrefs integration.
    /// </summary>
    public class PackageManifestTests : MonoBehaviour
    {
        [Header("Test Configuration")]
        [SerializeField] private bool runTestsOnStart = true;
        [SerializeField] private bool logDetailedResults = true;
        
        private int _testsPassed = 0;
        private int _testsFailed = 0;
        private List<string> _failureMessages = new List<string>();

        private void Start()
        {
            if (runTestsOnStart)
            {
                RunAllTests();
            }
        }

        /// <summary>
        /// Runs all PackageManifest tests and reports results.
        /// </summary>
        [ContextMenu("Run All Tests")]
        public void RunAllTests()
        {
            Debug.Log("[PackageManifestTests] Starting PackageManifest persistence tests...");
            
            _testsPassed = 0;
            _testsFailed = 0;
            _failureMessages.Clear();

            // Clean up any existing test data
            CleanupTestData();

            // Run all test methods
            TestEmptyManifestCreation();
            TestSaveLoadCycle();
            TestCorruptedJsonHandling();
            TestPlayerPrefsIntegration();
            TestStateManagement();
            TestMultiplePackageStates();
            TestNullAndEmptyInputHandling();

            // Report final results
            ReportTestResults();
        }

        /// <summary>
        /// Test 1: Verify empty manifest creation when no data exists.
        /// </summary>
        private void TestEmptyManifestCreation()
        {
            string testName = "Empty Manifest Creation";
            
            try
            {
                // Ensure no existing data
                CleanupTestData();

                // Create new manifest
                var manifest = new PackageManifest();
                
                // Verify empty state
                var allStates = manifest.GetAllStates();
                Assert(allStates != null, testName, "GetAllStates should return non-null list");
                Assert(allStates.Count == 0, testName, "New manifest should have empty package list");
                
                // Verify null package retrieval
                var nonExistentState = manifest.GetState("non-existent-package");
                Assert(nonExistentState == null, testName, "GetState should return null for non-existent package");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 2: Verify complete save/load cycle with valid data.
        /// </summary>
        private void TestSaveLoadCycle()
        {
            string testName = "Save/Load Cycle";
            
            try
            {
                // Clean slate
                CleanupTestData();

                // Create manifest and add test data
                var manifest1 = new PackageManifest();
                var testState = new PackageState("test-package-1")
                {
                    status = PackageStatus.Installed,
                    downloadProgress = 1.0f,
                    downloadedBytes = 1024,
                    totalBytes = 1024,
                    installedVersion = "1.0.0"
                };
                
                manifest1.SetState(testState);
                
                // Create new manifest instance (should load from file)
                var manifest2 = new PackageManifest();
                var loadedState = manifest2.GetState("test-package-1");
                
                // Verify loaded data matches saved data
                Assert(loadedState != null, testName, "Loaded state should not be null");
                Assert(loadedState.packageId == "test-package-1", testName, "Package ID should match");
                Assert(loadedState.status == PackageStatus.Installed, testName, "Status should match");
                Assert(Mathf.Approximately(loadedState.downloadProgress, 1.0f), testName, "Download progress should match");
                Assert(loadedState.downloadedBytes == 1024, testName, "Downloaded bytes should match");
                Assert(loadedState.totalBytes == 1024, testName, "Total bytes should match");
                Assert(loadedState.installedVersion == "1.0.0", testName, "Installed version should match");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 3: Verify graceful handling of corrupted JSON data.
        /// </summary>
        private void TestCorruptedJsonHandling()
        {
            string testName = "Corrupted JSON Handling";
            
            try
            {
                // Write corrupted JSON data directly to the manifest file
                var corruptPath = Path.Combine(Application.persistentDataPath, "Molca", "packages_manifest.json");
                Directory.CreateDirectory(Path.GetDirectoryName(corruptPath));
                File.WriteAllText(corruptPath, "{ invalid json data }");
                
                // Create manifest (should handle corruption gracefully)
                var manifest = new PackageManifest();
                
                // Verify it creates empty manifest instead of crashing
                var allStates = manifest.GetAllStates();
                Assert(allStates != null, testName, "Should create empty manifest on corruption");
                Assert(allStates.Count == 0, testName, "Corrupted data should result in empty manifest");
                
                // Verify it can still save new data
                var testState = new PackageState("recovery-test")
                {
                    status = PackageStatus.Available
                };
                manifest.SetState(testState);
                
                // Verify recovery worked
                var recoveredState = manifest.GetState("recovery-test");
                Assert(recoveredState != null, testName, "Should be able to save after corruption recovery");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 4: Verify file persistence integration works correctly.
        /// </summary>
        private void TestPlayerPrefsIntegration()
        {
            string testName = "File Persistence Integration";

            try
            {
                // Clean slate
                CleanupTestData();

                var manifestPath = Path.Combine(Application.persistentDataPath, "Molca", "packages_manifest.json");

                // Create and populate manifest
                var manifest = new PackageManifest();
                var testState = new PackageState("prefs-test")
                {
                    status = PackageStatus.Downloading,
                    downloadProgress = 0.5f,
                    errorMessage = "Test error message"
                };
                manifest.SetState(testState);

                // Verify data was written to the manifest file
                Assert(File.Exists(manifestPath), testName, "Manifest file should exist after save");
                string savedJson = File.ReadAllText(manifestPath);
                Assert(!string.IsNullOrEmpty(savedJson), testName, "JSON should be saved to file");
                Assert(savedJson.Contains("prefs-test"), testName, "Saved JSON should contain package ID");
                Assert(savedJson.Contains("Test error message"), testName, "Saved JSON should contain error message");

                // Test Clear functionality
                manifest.Clear();
                var clearedStates = manifest.GetAllStates();
                Assert(clearedStates.Count == 0, testName, "Clear should remove all states");

                // Verify file was updated
                string clearedJson = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : "";
                Assert(!clearedJson.Contains("prefs-test"), testName, "Cleared data should not contain package ID");

                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 5: Verify state management operations work correctly.
        /// </summary>
        private void TestStateManagement()
        {
            string testName = "State Management";
            
            try
            {
                CleanupTestData();

                var manifest = new PackageManifest();
                
                // Test adding new state
                var state1 = new PackageState("package-1") { status = PackageStatus.Available };
                manifest.SetState(state1);
                
                var retrieved1 = manifest.GetState("package-1");
                Assert(retrieved1 != null, testName, "Should retrieve added state");
                Assert(retrieved1.status == PackageStatus.Available, testName, "Retrieved state should match");
                
                // Test updating existing state
                state1.status = PackageStatus.Installed;
                state1.installedVersion = "2.0.0";
                manifest.SetState(state1);
                
                var updated1 = manifest.GetState("package-1");
                Assert(updated1.status == PackageStatus.Installed, testName, "State should be updated");
                Assert(updated1.installedVersion == "2.0.0", testName, "Version should be updated");
                
                // Verify lastModified is set
                Assert(!string.IsNullOrEmpty(updated1.lastModified), testName, "LastModified should be set");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 6: Verify handling of multiple package states.
        /// </summary>
        private void TestMultiplePackageStates()
        {
            string testName = "Multiple Package States";
            
            try
            {
                CleanupTestData();

                var manifest = new PackageManifest();
                
                // Add multiple packages
                var packages = new[]
                {
                    new PackageState("package-a") { status = PackageStatus.Available },
                    new PackageState("package-b") { status = PackageStatus.Installed, installedVersion = "1.0.0" },
                    new PackageState("package-c") { status = PackageStatus.Failed, errorMessage = "Network error" },
                    new PackageState("package-d") { status = PackageStatus.UpdateAvailable, installedVersion = "1.0.0" }
                };
                
                foreach (var pkg in packages)
                {
                    manifest.SetState(pkg);
                }
                
                // Verify all packages are stored
                var allStates = manifest.GetAllStates();
                Assert(allStates.Count == 4, testName, "Should store all 4 packages");
                
                // Verify each package can be retrieved correctly
                foreach (var expectedPkg in packages)
                {
                    var retrievedPkg = manifest.GetState(expectedPkg.packageId);
                    Assert(retrievedPkg != null, testName, $"Should retrieve {expectedPkg.packageId}");
                    Assert(retrievedPkg.status == expectedPkg.status, testName, $"Status should match for {expectedPkg.packageId}");
                }
                
                // Test persistence across instances
                var manifest2 = new PackageManifest();
                var reloadedStates = manifest2.GetAllStates();
                Assert(reloadedStates.Count == 4, testName, "Should persist all packages across instances");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 7: Verify proper handling of null and empty inputs.
        /// </summary>
        private void TestNullAndEmptyInputHandling()
        {
            string testName = "Null and Empty Input Handling";
            
            try
            {
                CleanupTestData();

                var manifest = new PackageManifest();
                
                // Test null state
                manifest.SetState(null);
                var allStates = manifest.GetAllStates();
                Assert(allStates.Count == 0, testName, "Null state should not be added");
                
                // Test state with null/empty packageId
                var invalidState1 = new PackageState(null);
                manifest.SetState(invalidState1);
                Assert(manifest.GetAllStates().Count == 0, testName, "State with null ID should not be added");
                
                var invalidState2 = new PackageState("");
                manifest.SetState(invalidState2);
                Assert(manifest.GetAllStates().Count == 0, testName, "State with empty ID should not be added");
                
                // Test GetState with null/empty input
                var nullResult = manifest.GetState(null);
                Assert(nullResult == null, testName, "GetState with null should return null");
                
                var emptyResult = manifest.GetState("");
                Assert(emptyResult == null, testName, "GetState with empty string should return null");
                
                PassTest(testName);
            }
            catch (Exception ex)
            {
                FailTest(testName, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Simple assertion helper.
        /// </summary>
        private void Assert(bool condition, string testName, string message)
        {
            if (!condition)
            {
                throw new Exception($"Assertion failed: {message}");
            }
        }

        /// <summary>
        /// Records a passed test.
        /// </summary>
        private void PassTest(string testName)
        {
            _testsPassed++;
            if (logDetailedResults)
            {
                Debug.Log($"[PackageManifestTests] ✅ {testName} - PASSED");
            }
        }

        /// <summary>
        /// Records a failed test.
        /// </summary>
        private void FailTest(string testName, string reason)
        {
            _testsFailed++;
            string failureMessage = $"❌ {testName} - FAILED: {reason}";
            _failureMessages.Add(failureMessage);
            Debug.LogError($"[PackageManifestTests] {failureMessage}");
        }

        /// <summary>
        /// Reports the final test results.
        /// </summary>
        private void ReportTestResults()
        {
            int totalTests = _testsPassed + _testsFailed;
            
            Debug.Log($"[PackageManifestTests] ==========================================");
            Debug.Log($"[PackageManifestTests] TEST RESULTS SUMMARY");
            Debug.Log($"[PackageManifestTests] Total Tests: {totalTests}");
            Debug.Log($"[PackageManifestTests] Passed: {_testsPassed}");
            Debug.Log($"[PackageManifestTests] Failed: {_testsFailed}");
            Debug.Log($"[PackageManifestTests] Success Rate: {(_testsPassed * 100.0f / totalTests):F1}%");
            
            if (_testsFailed > 0)
            {
                Debug.Log($"[PackageManifestTests] ==========================================");
                Debug.Log($"[PackageManifestTests] FAILURE DETAILS:");
                foreach (var failure in _failureMessages)
                {
                    Debug.LogError($"[PackageManifestTests] {failure}");
                }
            }
            
            Debug.Log($"[PackageManifestTests] ==========================================");
            
            if (_testsFailed == 0)
            {
                Debug.Log($"[PackageManifestTests] 🎉 ALL TESTS PASSED! PackageManifest persistence is working correctly.");
            }
            else
            {
                Debug.LogError($"[PackageManifestTests] ⚠️ {_testsFailed} test(s) failed. Please review the implementation.");
            }
        }

        /// <summary>
        /// Cleans up test data from the manifest file.
        /// </summary>
        private void CleanupTestData()
        {
            var path = Path.Combine(Application.persistentDataPath, "Molca", "packages_manifest.json");
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// Manual cleanup method for testing.
        /// </summary>
        [ContextMenu("Cleanup Test Data")]
        public void ManualCleanup()
        {
            CleanupTestData();
            Debug.Log("[PackageManifestTests] Test data cleaned up.");
        }
    }
}