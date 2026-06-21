# DataModel Subscription System

## Overview

The DataModel Subscription System allows external scripts to subscribe to specific DataModels and receive real-time notifications when new data is added to the cache from any provider. This system provides a reactive way to handle data updates without constantly polling the DataManager.

## Key Features

- **Real-time notifications** when new data arrives
- **Data pooling and batching** for efficient processing of multiple data entries
- **Subscribe by DataModel reference** for type safety and guaranteed uniqueness
- **Automatic cleanup** when providers are unregistered
- **Error handling** with try-catch in callbacks
- **Subscription statistics** for monitoring and debugging
- **Configurable flush intervals** to balance responsiveness vs. performance

## How It Works

1. **Subscription**: External scripts subscribe to specific DataModels using their reference
2. **Data Flow**: When a DataProvider adds new data to its cache, the `OnDataAdded` event is triggered
3. **Data Pooling**: DataManager pools incoming data for each DataModel to batch process multiple entries
4. **Batch Notification**: DataManager notifies all subscribers with an array of `ImmutableData` entries
5. **Processing**: External scripts receive batches of data and can process them efficiently

## Data Pooling System

The DataManager implements a **data pooling system** to efficiently handle scenarios where providers return multiple data entries in quick succession:

### How Pooling Works

1. **Data Collection**: When new data arrives, it's added to a pool specific to that DataModel
2. **Batch Processing**: Data is held in the pool until the flush interval is reached (default: 100ms)
3. **Efficient Notification**: Subscribers receive arrays of data instead of individual callbacks
4. **Performance Benefits**: Reduces callback overhead when processing multiple data entries
5. **Safety Mechanism**: Periodic background checking ensures no data is left behind, even if no new data arrives

### Pool Configuration

- **Flush Interval**: Configurable timing for when pools are flushed (default: 0.1 seconds)
- **Per-Model Pools**: Each DataModel has its own independent pool
- **Automatic Flushing**: Pools are automatically flushed based on time intervals
- **Manual Flushing**: Pools can be manually flushed for immediate processing

### Safety Mechanism

The system includes a **background safety mechanism** to prevent data loss:

- **Periodic Checking**: A background coroutine checks for stale pools every 50ms (half the flush interval)
- **Stale Pool Detection**: Pools with data that have exceeded the flush interval are automatically flushed
- **Guaranteed Delivery**: This ensures that even the last batch of data is always processed
- **No Data Loss**: No data can be left behind in pools, even if no new data arrives

### Pool Management Methods

```csharp
// Manually flush all data pools
DataManager.Instance.FlushAllDataPools();

// Manually flush a specific data pool
DataManager.Instance.FlushDataPoolManually("ModelId");

// Get pool status for monitoring
var poolStatus = DataManager.Instance.GetDataPoolStatus();
```

## Data Access Philosophy

The DataManager and DataCache operate at the **ImmutableData level**, not at the field level. This means:

- **No direct field access**: Neither DataManager nor DataCache extract individual fields from data
- **ImmutableData objects**: All data access methods return complete `ImmutableData` objects
- **Field extraction responsibility**: External scripts use `ImmutableData.TryGet<T>()` to access specific fields
- **Data integrity**: The complete data structure is preserved and accessible
- **Simplified architecture**: Since DataCache is based on specific DataModels, all data has consistent structure

This approach ensures:
- **Separation of concerns**: DataManager handles data management, DataCache handles caching, ImmutableData handles field access
- **Type safety**: Field access is handled by the strongly-typed ImmutableData methods
- **Flexibility**: External scripts can access any field without the data management layer knowing the data structure
- **Consistency**: All data access follows the same pattern through ImmutableData
- **Efficiency**: No unnecessary field indexing since all data in a cache has the same structure

## Usage Examples

### Data Access Methods

The DataManager provides methods to retrieve `ImmutableData` objects, which you then use to access specific fields:

```csharp
// Get all data from a specific provider
var allUserData = DataManager.Instance.GetAllData("UserProvider");
foreach (var userData in allUserData)
{
    if (userData.IsValid)
    {
        // Access fields using ImmutableData methods
        if (userData.TryGet<string>("userName", out string userName))
        {
            Debug.Log($"User: {userName}");
        }
    }
}
```

### DataCache Methods

The DataCache provides efficient data storage and retrieval for a specific DataModel:

```csharp
// Get all data in the cache
var allData = dataCache.Data;
foreach (var data in allData)
{
    if (data.IsValid)
    {
        Debug.Log($"Data: {data.modelId}");
    }
}

// Get the most recent data
var recentData = dataCache.GetRecentData(5);
foreach (var data in recentData)
{
    Debug.Log($"Recent data: {data.modelId}");
}

// Get data within a time range
var timeRangeData = dataCache.GetDataInTimeRange(startTime, endTime);
foreach (var data in timeRangeData)
{
    Debug.Log($"Time range data: {data.modelId}");
}
```

### Basic Subscription

```csharp
public class UserDataHandler : MonoBehaviour
{
    [SerializeField] private DataModel userDataModel;
    
    private void Start()
    {
        // Subscribe to notifications when new user data arrives
        // Note: Callback now receives an array of ImmutableData for batch processing
        DataManager.Instance.SubscribeToDataModel(userDataModel, OnUserDataUpdated);
    }
    
    private void OnDestroy()
    {
        // Always unsubscribe to prevent memory leaks
        DataManager.Instance.UnsubscribeFromDataModel(userDataModel, OnUserDataUpdated);
    }
    
    private void OnUserDataUpdated(ImmutableData newData)
    {
        Debug.Log($"New user data received: {newData.modelId}");
        
        // Access the new data
        if (newData.TryGet<string>("userName", out string userName))
        {
            UpdateUserName(userName);
        }
        
        if (newData.TryGet<int>("userLevel", out int userLevel))
        {
            UpdateUserLevel(userLevel);
        }
    }
    
    private void UpdateUserName(string name) { /* Update UI */ }
    private void UpdateUserLevel(int level) { /* Update UI */ }
}
```

### Multiple Subscriptions

```csharp
public class MultiModelHandler : MonoBehaviour
{
    [SerializeField] private DataModel userModel;
    [SerializeField] private DataModel gameModel;
    [SerializeField] private DataModel settingsModel;
    
    private void Start()
    {
        // Subscribe to multiple models
        DataManager.Instance.SubscribeToDataModel(userModel, OnUserDataUpdated);
        DataManager.Instance.SubscribeToDataModel(gameModel, OnGameDataUpdated);
        DataManager.Instance.SubscribeToDataModel(settingsModel, OnSettingsDataUpdated);
    }
    
    private void OnDestroy()
    {
        // Unsubscribe from all models
        DataManager.Instance.UnsubscribeFromDataModel(userModel, OnUserDataUpdated);
        DataManager.Instance.UnsubscribeFromDataModel(gameModel, OnGameDataUpdated);
        DataManager.Instance.UnsubscribeFromDataModel(settingsModel, OnSettingsDataUpdated);
    }
    
    private void OnUserDataUpdated(ImmutableData[] dataArray) { /* Handle user data batch */ }
    private void OnGameDataUpdated(ImmutableData[] dataArray) { /* Handle game data batch */ }
    private void OnSettingsDataUpdated(ImmutableData[] dataArray) { /* Handle settings data batch */ }
}
```

## API Reference

### Data Access Methods

#### `GetAllData(string providerId)`
- **Parameters**: `providerId`: The ID of the provider to get data from
- **Description**: Gets all data from a specific provider
- **Returns**: `IReadOnlyList<ImmutableData>` - List of all data entries from the provider

### DataCache Methods

#### `Data` (Property)
- **Description**: Gets all data in the cache
- **Returns**: `IReadOnlyList<ImmutableData>` - List of all data entries

#### `GetRecentData(int count)`
- **Parameters**: `count`: Number of most recent data entries to retrieve
- **Description**: Gets the most recent data entries, ordered by creation time
- **Returns**: `List<ImmutableData>` - List of recent data entries

#### `GetDataInTimeRange(DateTime startTime, DateTime endTime)`
- **Parameters**: 
  - `startTime`: Start of the time range
  - `endTime`: End of the time range
- **Description**: Gets data within a specific time range
- **Returns**: `List<ImmutableData>` - List of data entries within the time range

#### `SearchData(string searchTerm, bool caseSensitive = false)`
- **Parameters**: 
  - `searchTerm`: The term to search for
  - `caseSensitive`: Whether the search should be case sensitive (default: false)
- **Description**: Searches for data containing a specific term in any field
- **Returns**: `List<ImmutableData>` - List of all data entries that match the search term

### Subscription Methods

#### `SubscribeToDataModel(DataModel dataModel, Action<ImmutableData[]> callback)`
- **Parameters**: 
  - `dataModel`: The DataModel to subscribe to (must not be null)
  - `callback`: Action to invoke when new data is added (receives array of ImmutableData for batch processing)
- **Description**: Subscribe to notifications for a specific DataModel using its unique ID
- **Returns**: void
- **Note**: Uses the DataModel's unique ID for subscription management. Callbacks receive batched data for efficient processing.

### Unsubscription Methods

#### `UnsubscribeFromDataModel(DataModel dataModel, Action<ImmutableData[]> callback)`
- **Parameters**:
  - `dataModel`: The DataModel to unsubscribe from
  - `callback`: The callback to remove (must match the signature used in subscription)
- **Description**: Unsubscribe from a specific DataModel
- **Returns**: void

### Pool Management Methods

#### `FlushAllDataPools()`
- **Description**: Manually flushes all data pools, immediately processing any pending data
- **Returns**: void
- **Use Case**: Useful for testing or when immediate processing is needed

#### `FlushDataPoolManually(string modelId)`
- **Parameters**: `modelId`: The ID of the DataModel whose pool should be flushed
- **Description**: Manually flushes a specific data pool
- **Returns**: void
- **Use Case**: Useful for testing or when immediate processing is needed for a specific model

#### `GetDataPoolStatus()`
- **Description**: Gets the current status of all data pools for monitoring
- **Returns**: `Dictionary<string, object>` - Status information for each pool including count, time since last flush, and flush status

#### `TriggerPeriodicFlushCheck()`
- **Description**: Manually triggers the periodic flush check to find and flush any stale pools
- **Returns**: void
- **Use Case**: Useful for testing or when you want to ensure immediate processing of any pending data

### Utility Methods

#### `HasActiveSubscriptions(DataModel dataModel)`
- **Parameters**: `dataModel`: The DataModel to check
- **Description**: Check if there are active subscriptions for a specific DataModel
- **Returns**: `bool`

#### `GetDataModelSubscriptionStats()`
- **Description**: Get statistics about all active subscriptions
- **Returns**: `Dictionary<string, int>` with subscription counts (key format: "ID:{modelId}")

## Best Practices

### 1. Always Unsubscribe
```csharp
private void OnDestroy()
{
    // Always unsubscribe to prevent memory leaks
    if (DataManager.Instance != null)
    {
        DataManager.Instance.UnsubscribeFromDataModel(dataModel, callback);
    }
}
```

### 2. Null Checks
```csharp
private void Start()
{
    if (DataManager.Instance != null && userDataModel != null)
    {
        DataManager.Instance.SubscribeToDataModel(userDataModel, OnDataUpdated);
    }
}
```

### 3. Error Handling in Callbacks
```csharp
private void OnDataUpdated(ImmutableData newData)
{
    try
    {
        // Process the data
        ProcessNewData(newData);
    }
    catch (Exception e)
    {
        Debug.LogError($"Error processing data: {e.Message}");
    }
}
```

### 4. Use TryGet for Safe Field Access
```csharp
private void OnDataUpdated(ImmutableData newData)
{
    // Safe field access with TryGet
    if (newData.TryGet<string>("userName", out string userName))
    {
        UpdateUserName(userName);
    }
    else
    {
        Debug.LogWarning("userName field not found in new data");
    }
}
```

## Architecture Details

### Event Flow
```
DataProvider → DataCache.AddData() → OnDataAdded Event → DataManager.OnCacheDataAdded() → Subscriber Callbacks
```

### Subscription Storage
- **Model ID Subscriptions**: `Dictionary<string, List<Action<ImmutableData>>>`
- **Key**: DataModel.ModelId (guaranteed unique)
- **Value**: List of callback actions for that model

### Automatic Cleanup
- Subscriptions are automatically cleaned up when providers are unregistered
- All subscriptions are cleared when DataManager shuts down
- Event handlers are properly unsubscribed to prevent memory leaks

## Performance Considerations

- **Callback Execution**: All callbacks are executed synchronously on the main thread
- **Memory Usage**: Each subscription stores a reference to the callback method
- **Event Propagation**: Events are propagated to all subscribers for a given model ID
- **Indexing**: Field-based indexing in DataCache ensures fast data lookups

## Debugging and Monitoring

### Check Subscription Status
```csharp
// Check if a specific model has active subscriptions
bool hasSubscriptions = DataManager.Instance.HasActiveSubscriptions(userDataModel);

// Get global subscription statistics
var stats = DataManager.Instance.GetDataModelSubscriptionStats();
foreach (var kvp in stats)
{
    Debug.Log($"{kvp.Key}: {kvp.Value} subscribers");
}
```

### Logging
Enable `LogDataOperations` in DataConfig to see subscription/unsubscription logs:
```
[DataManager] Subscribed to DataModel: UserModel (ID: UserModel_001)
[DataManager] Unsubscribed from DataModel: UserModel (ID: UserModel_001)
```

## Integration with Existing Systems

The subscription system integrates seamlessly with:
- **DataProvider**: Automatically triggers events when data is added
- **DataCache**: Provides the `OnDataAdded` event
- **DataModel**: Used for subscription identification via unique ModelId
- **ImmutableData**: The data structure passed to callbacks

## Testing

Use the provided `DataSubscriptionTest` script to test the subscription system:
1. Attach to a GameObject in your scene
2. Assign a test DataModel
3. Run the scene and observe the subscription logs
4. Use the context menu options to test different scenarios

## Troubleshooting

### Common Issues

1. **No callbacks triggered**: Check if the DataModel is properly assigned and the provider is active
2. **Memory leaks**: Ensure you're unsubscribing in OnDestroy
3. **Null reference exceptions**: Add null checks for DataManager.Instance and DataModel
4. **Multiple callbacks**: Verify you're not subscribing the same callback multiple times

### Debug Steps

1. Check if DataManager is initialized
2. Verify the DataModel reference is not null
3. Ensure the DataProvider is registered and active
4. Check the console for subscription/unsubscription logs
5. Use the subscription statistics to verify active subscriptions

## Why DataModel References Only?

The subscription system only supports DataModel references (not names) because:

1. **Uniqueness Guarantee**: DataModel IDs are guaranteed to be unique, while names are not
2. **Type Safety**: Direct references provide compile-time type checking
3. **Performance**: No need to search through providers to find models by name
4. **Reliability**: Eliminates potential conflicts from duplicate model names
5. **Maintainability**: Clearer code intent and easier debugging
