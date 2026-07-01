# PackageManifest Tests

This directory contains unit tests for the `PackageManifest` class, which handles JSON serialization of package states to PlayerPrefs for persistence across application sessions.

## Test Coverage

The tests validate the following functionality as specified in **Task 2.2**:

### 1. Save/Load Cycle Testing
- **Test**: `TestSaveLoadCycle()`
- **Validates**: Complete persistence cycle with valid data
- **Covers**: 
  - Creating PackageState with various properties
  - Saving state via `SetState()`
  - Loading state in new PackageManifest instance
  - Verifying all properties match after reload

### 2. Corrupted JSON Handling
- **Test**: `TestCorruptedJsonHandling()`
- **Validates**: Graceful handling of corrupted PlayerPrefs data
- **Covers**:
  - Invalid JSON in PlayerPrefs
  - Recovery to empty manifest state
  - Ability to save new data after corruption
  - No crashes or exceptions during corruption handling

### 3. Empty Manifest Creation
- **Test**: `TestEmptyManifestCreation()`
- **Validates**: Proper initialization when no data exists
- **Covers**:
  - Clean PlayerPrefs state
  - Empty package list initialization
  - Null returns for non-existent packages
  - Proper default state

### 4. PlayerPrefs Integration
- **Test**: `TestPlayerPrefsIntegration()`
- **Validates**: Direct PlayerPrefs storage and retrieval
- **Covers**:
  - JSON serialization to PlayerPrefs
  - Correct PlayerPrefs key usage
  - Data persistence verification
  - Clear() functionality

### 5. Additional Test Coverage

#### State Management
- **Test**: `TestStateManagement()`
- **Validates**: Core state operations
- **Covers**:
  - Adding new package states
  - Updating existing package states
  - LastModified timestamp updates
  - State retrieval accuracy

#### Multiple Package States
- **Test**: `TestMultiplePackageStates()`
- **Validates**: Handling multiple packages simultaneously
- **Covers**:
  - Multiple package storage
  - Individual package retrieval
  - Cross-instance persistence
  - Different package statuses

#### Input Validation
- **Test**: `TestNullAndEmptyInputHandling()`
- **Validates**: Robust input handling
- **Covers**:
  - Null state handling
  - Empty/null package ID handling
  - Graceful error handling
  - No data corruption from invalid inputs

## Running the Tests

### Method 1: Unity Editor Menu
1. Open Unity Editor
2. Go to **Molca > Content Package > Run PackageManifest Tests**
3. Check the Console window for test results

### Method 2: GameObject Component
1. Create an empty GameObject in a scene
2. Add the `PackageManifestTests` component
3. Check "Run Tests On Start" in the inspector
4. Enter Play mode to run tests automatically

### Method 3: Manual Execution
1. Add `PackageManifestTests` component to any GameObject
2. Use the "Run All Tests" context menu option in the inspector
3. Or call `RunAllTests()` method programmatically

## Test Results Interpretation

### Success Output
```
[PackageManifestTests] ✅ Empty Manifest Creation - PASSED
[PackageManifestTests] ✅ Save/Load Cycle - PASSED
[PackageManifestTests] ✅ Corrupted JSON Handling - PASSED
[PackageManifestTests] ✅ PlayerPrefs Integration - PASSED
[PackageManifestTests] ✅ State Management - PASSED
[PackageManifestTests] ✅ Multiple Package States - PASSED
[PackageManifestTests] ✅ Null and Empty Input Handling - PASSED
[PackageManifestTests] 🎉 ALL TESTS PASSED! PackageManifest persistence is working correctly.
```

### Failure Output
```
[PackageManifestTests] ❌ Test Name - FAILED: Specific failure reason
[PackageManifestTests] ⚠️ X test(s) failed. Please review the implementation.
```

## Test Data Management

### Cleanup
- Tests automatically clean up PlayerPrefs data before and after execution
- Manual cleanup available via **Molca > Content Package > Clean Test Data**
- PlayerPrefs key used: `"Molca.ContentPackage.Manifest"`

### Isolation
- Each test method starts with a clean PlayerPrefs state
- Tests do not interfere with each other
- Production data is not affected (uses same key but tests clean up)

## Integration with Task Requirements

These tests fulfill **Task 2.2** requirements:

✅ **Create unit test for save/load cycle**
- `TestSaveLoadCycle()` validates complete persistence workflow

✅ **Test corrupted JSON handling**  
- `TestCorruptedJsonHandling()` ensures graceful error recovery

✅ **Test empty manifest creation**
- `TestEmptyManifestCreation()` validates clean initialization

✅ **Verify PlayerPrefs integration**
- `TestPlayerPrefsIntegration()` confirms proper storage mechanism

## Dependencies

The tests depend on:
- `Molca.ContentPackage.Core.PackageManifest`
- `Molca.ContentPackage.Core.PackageState`  
- `Molca.ContentPackage.Core.PackageStatus`
- Unity's `PlayerPrefs` system
- Unity's `JsonUtility` for serialization

## Notes

- Tests use Unity's built-in logging system for output
- No external test framework dependencies required
- Compatible with Unity 2022.3 LTS and newer
- Tests can be adapted to NUnit or other frameworks if needed
- All tests are deterministic and repeatable