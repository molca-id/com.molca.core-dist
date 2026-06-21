# ContentPackage System - Refactored

A comprehensive runtime dynamic asset management system for Unity using Addressables, designed for DLC, content packages, and intelligent asset management.

## Overview

The ContentPackage system provides a robust framework for managing downloadable content, DLC packages, and runtime asset management with features like:

- **Package Lifecycle Management**: Install, uninstall, update packages with dependency resolution
- **Intelligent Storage Management**: Cache limits, automatic cleanup, storage optimization
- **Asset Tracking**: Comprehensive tracking of loaded assets and their package relationships
- **Content Validation**: Integrity checking and validation of downloaded content
- **User Interface**: Runtime UI for package management
- **Error Handling**: Enhanced error tracking and recovery

## Architecture

### Core Components

#### AddressablesManager
The main entry point for package operations. Enhanced with:
- Package state management
- Asset tracking integration
- Storage management
- Content validation
- Enhanced error handling

#### PackageStateManager
Manages package lifecycle states:
- `NotDownloaded` → `Downloading` → `Installed` → `UpdateAvailable`
- Tracks installation dates, usage statistics, and package metadata

#### AssetTracker
Tracks all loaded assets and their relationships:
- Asset-to-package mapping
- Instance tracking
- Memory usage statistics
- Automatic cleanup of invalid references

#### StorageManager
Intelligent storage management:
- Cache size limits and monitoring
- Automatic cleanup suggestions
- Storage optimization based on usage patterns

#### PackageDependencyResolver
Handles package dependencies:
- Recursive dependency resolution
- Version compatibility checking
- Circular dependency detection

#### ContentValidator
Validates package integrity:
- Asset accessibility verification
- Content validation
- System integrity checks

## Package Configuration

Packages are configured through `ContentPackageSettings`:

```csharp
[CreateAssetMenu(fileName = "ContentPackageSettings", menuName = "Molca/Settings/Content Package Settings")]
public class ContentPackageSettings : SettingModule
{
    [Serializable]
    public class PackageConfig
    {
        public string packageId;
        public string displayName;
        public PackageMetadata metadata;
        public bool isEnabled = true;
        public bool isLocal = false;
        public bool allowUninstall = true;
        public PackageDependency[] dependencies;
        public string[] assetGroups;
        public string[] sceneReferences;
        public string[] prefabReferences;
    }
}
```

## Usage Examples

### Basic Package Installation

```csharp
private async void InstallPackage()
{
    var result = await _addressablesManager.InstallPackageAsync("my_package",
        new Progress<PackageProgress>(p =>
        {
            Debug.Log($"Progress: {p.Message} ({p.Progress:P1})");
        }));

    if (result.Success)
    {
        Debug.Log("Package installed successfully!");
    }
    else
    {
        Debug.LogError($"Installation failed: {result.Message}");
    }
}
```

### Loading Assets from Packages

```csharp
private async void LoadAssetFromPackage()
{
    // Load with package tracking
    var prefab = await _addressablesManager.LoadAssetAsync<GameObject>(
        assetReference, "my_package");

    if (prefab != null)
    {
        // Instantiate with package tracking
        var instance = await _addressablesManager.InstantiateAsync(
            assetReference, transform, "my_package");

        // Asset is automatically tracked and will be cleaned up with package
    }
}
```

### Storage Management

```csharp
private async void ManageStorage()
{
    // Get storage statistics
    var stats = await _addressablesManager.GetStorageStatsAsync();
    Debug.Log($"Used: {FormatSize(stats.TotalCacheSize)} / {FormatSize(stats.MaxCacheSize)}");

    // Perform cleanup if needed
    if (stats.AvailableSpace < 100 * 1024 * 1024) // Less than 100MB free
    {
        var freedSpace = await _addressablesManager.CleanupCacheAsync(
            200 * 1024 * 1024, // Free 200MB
            new Progress<StorageCleanupProgress>(p =>
            {
                Debug.Log($"Cleanup: {p.Message}");
            }));

        Debug.Log($"Freed {FormatSize(freedSpace)} of storage");
    }
}
```

### Content Validation

```csharp
private async void ValidateContent()
{
    // Validate single package
    var result = await _addressablesManager.ValidatePackageAsync("my_package",
        new Progress<ValidationProgress>(p =>
        {
            Debug.Log($"Validation: {p.Message} ({p.Progress:P1})");
        }));

    if (result.IsValid)
    {
        Debug.Log("Package validation passed!");
    }
    else
    {
        Debug.LogError($"Validation failed: {string.Join(", ", result.Errors)}");
    }

    // System integrity check
    var integrityResult = await _addressablesManager.PerformIntegrityCheckAsync(
        new Progress<IntegrityCheckProgress>(p =>
        {
            Debug.Log($"Integrity check: {p.Message}");
        }));

    Debug.Log($"System health: {integrityResult.SystemHealthy}");
}
```

## Error Handling

The system includes comprehensive error handling:

```csharp
// Subscribe to error events
PackageErrorHandler.OnErrorOccurred += HandlePackageError;

// Log errors with context
PackageErrorHandler.LogError(packageId, "Install", "Download failed", exception);

// Get error statistics
var stats = PackageErrorHandler.GetErrorStats(packageId);
Debug.Log($"Package errors: {stats.TotalErrors}");

// Get system health
var health = PackageErrorHandler.GetSystemHealthSummary();
Debug.Log($"System health: {health.GetHealthStatus()} ({health.HealthScore}%)");
```

## UI Components

### PackageManagerUI
A complete UI system for runtime package management:

```csharp
public class PackageManagerUI : MonoBehaviour
{
    [SerializeField] private Transform packageListContainer;
    [SerializeField] private GameObject packageItemPrefab;
    [SerializeField] private Button refreshButton;
    [SerializeField] private TextMeshProUGUI storageInfoText;

    private void Start()
    {
        var addressablesManager = RuntimeManager.GetSubsystem<AddressablesManager>();
        // UI automatically populates with available packages
    }
}
```

## Storage Optimization

### Automatic Cleanup
The system automatically suggests cleanup based on:
- Last used time
- Package size
- Storage pressure
- Usage patterns

### Manual Cleanup
```csharp
// Get cleanup suggestions
var suggestions = await _addressablesManager.GetCleanupSuggestionsAsync(targetFreeSpace);

// Perform cleanup
var freedSpace = await _addressablesManager.CleanupCacheAsync(targetFreeSpace);
```

## Package Dependencies

### Dependency Resolution
```csharp
// Check dependencies before installation
bool valid = await _addressablesManager.ValidatePackageDependenciesAsync(packageId);

// Install with automatic dependency resolution
var result = await _addressablesManager.InstallPackageAsync(packageId); // Dependencies installed automatically
```

### Dependency Configuration
```csharp
public class PackageDependency
{
    public string packageId;
    public string minVersion = "1.0.0";
    public bool isOptional = false;
}
```

## Performance Considerations

### Memory Management
- Assets are tracked and automatically released when packages are uninstalled
- Memory usage statistics available via `GetAssetMemoryStats()`
- Automatic cleanup of invalid asset references

### Storage Management
- Configurable cache size limits
- LRU-style cleanup based on usage patterns
- Background cleanup operations

### Error Recovery
- Comprehensive error logging and statistics
- Automatic retry mechanisms for transient failures
- Graceful degradation when services are unavailable

## Integration with Existing Systems

### RuntimeManager Integration
The system integrates seamlessly with the existing RuntimeManager:

```csharp
private void Start()
{
    var addressablesManager = RuntimeManager.GetSubsystem<AddressablesManager>();
    // Ready to use
}
```

### Modal System Integration
Built-in integration with the modal system for user feedback:

```csharp
// Automatic progress display
var result = await addressablesManager.InstallPackageAsync(packageId);

// Modal notifications
modalManager.AddMessage(result.Message,
    result.Success ? ModalManager.MessageType.Default : ModalManager.MessageType.Error);
```

## Best Practices

### Package Design
1. **Modular Content**: Design packages as independent, feature-complete units
2. **Dependency Management**: Keep dependency chains shallow and explicit
3. **Version Control**: Use semantic versioning for packages
4. **Size Optimization**: Consider download sizes and target platforms

### Error Handling
1. **Always check operation results**: All operations return detailed results
2. **Handle exceptions gracefully**: Use the built-in error handling system
3. **Monitor system health**: Regular integrity checks prevent issues
4. **Log important operations**: Use the error handler for debugging

### Performance
1. **Batch operations**: Install multiple packages together when possible
2. **Monitor storage**: Regular cleanup prevents storage issues
3. **Validate content**: Regular integrity checks catch corruption early
4. **Track usage**: The system learns from usage patterns for optimization

## Configuration

### Settings Structure
```csharp
[Header("Remote Configuration")]
public string remoteCatalogUrl = "";
public bool checkForCatalogUpdates = true;

[Header("Package Configuration")]
public List<PackageConfig> packageConfigs = new List<PackageConfig>();

[Header("Cache Settings")]
public bool clearCacheOnStart = false;
public long maxCacheSize = 1024 * 1024 * 1024; // 1GB default
```

### Runtime Configuration
Settings can be modified at runtime through the SettingModule interface:

```csharp
var settings = GlobalSettings.GetModule<ContentPackageSettings>();
settings.maxCacheSize = 2L * 1024 * 1024 * 1024; // 2GB
settings.SaveSettings();
```

## Testing

The system includes comprehensive testing support:

```csharp
// Use the ContentPackageExample script for testing
public class ContentPackageTest : MonoBehaviour
{
    [SerializeField] private string testPackageId = "test_package";
    [SerializeField] private AssetReference testAsset;

    // Test all major functionality
    private async void Start()
    {
        await TestPackageOperations();
        await TestStorageManagement();
        await TestContentValidation();
    }
}
```

## Migration from Legacy System

If migrating from the old ContentPackage system:

1. **Update package configuration**: Convert to new PackageConfig structure
2. **Update asset references**: Ensure all assets are properly referenced
3. **Update code**: Replace old API calls with new enhanced methods
4. **Test thoroughly**: Verify all package operations work correctly
5. **Update UI**: Use new PackageManagerUI or integrate with existing UI

## Future Enhancements

Potential areas for future development:
- **P2P Distribution**: Peer-to-peer package distribution
- **Delta Updates**: Incremental updates for large packages
- **Cloud Sync**: Cloud synchronization of package states
- **Analytics**: Detailed usage analytics and reporting
- **Cross-Platform**: Enhanced cross-platform package management

---

This refactored ContentPackage system provides a solid foundation for dynamic content management in Unity applications, with room for future enhancements and customization based on specific project needs.
