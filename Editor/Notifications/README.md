# Molca Notification System

The Molca Notification System is an extensible framework for sending notifications from the Unity Editor. It's designed with modularity in mind, allowing you to easily create custom notification providers for various services and events.

## Architecture

The system follows a modular design pattern similar to the GlobalSettings/SettingModule architecture:

- **NotificationProvider**: Abstract base class for all notification providers
- **NotificationSettings**: ScriptableObject manager that holds and manages all notification providers
- **DiscordNotificationProvider**: Base implementation for Discord webhook notifications
- **Specific Providers**: Concrete implementations like BuildNotificationProvider and LightBakingNotificationProvider

## Built-in Notification Providers

### 1. Build Notification Provider
Sends notifications when Unity builds start, complete, or fail.

**Features:**
- Build start notifications with version, platform, and configuration info
- Build completion notifications with duration, size, and error count
- Build failure detection and notifications
- Color-coded embeds (blue for start, green for success, red for failure)

### 2. Light Baking Notification Provider
Sends notifications during Unity's light baking process.

**Features:**
- Light baking start notifications
- Light baking completion notifications with duration
- Automatic event registration

### 3. Addressables Build Notification Provider
Sends notifications when Addressables content is built.

**Features:**
- Build start notifications with profile and group count
- Build completion notifications with duration and status
- Error reporting for failed builds
- Manual notification triggers via `NotifyBuildStarted()` and `NotifyBuildCompleted()`
- Test notification via menu: `Molca/Notifications/Test Addressables Build Notification`

**Usage:**
```csharp
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using Molca.Settings.Notification;

// Example: Custom build script with notifications
public static void BuildAddressablesWithNotifications()
{
    // Notify build started
    AddressablesBuildNotificationProvider.NotifyBuildStarted();
    
    try
    {
        // Build addressables
        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
        
        // Notify build completed
        bool success = string.IsNullOrEmpty(result.Error);
        AddressablesBuildNotificationProvider.NotifyBuildCompleted(success, result.Error);
    }
    catch (System.Exception ex)
    {
        // Notify build failed
        AddressablesBuildNotificationProvider.NotifyBuildCompleted(false, ex.Message);
    }
}
```

**Note:** Unlike Build and Light Baking providers, Addressables notifications require manual triggers because Unity's Addressables API doesn't provide automatic build callbacks.

## Creating Custom Notification Providers

### Option 1: Extend NotificationProvider (Generic)

For completely custom notification systems (email, SMS, custom webhooks, etc.):

```csharp
using UnityEngine;
using Molca.Settings;

[CreateAssetMenu(fileName = "My Custom Notifier", menuName = "Molca/Editor/Notifications/Custom Notifier")]
public class CustomNotificationProvider : NotificationProvider
{
    [SerializeField] private string apiKey;
    [SerializeField] private string endpoint;
    
    public override string DisplayName => "My Custom Notifications";
    
    public override bool IsEnabled => enabled && !string.IsNullOrEmpty(apiKey);
    
    public override void SendNotification(NotificationData notification, System.Action<bool> onCompleted = null)
    {
        // Implement your custom notification logic here
        Debug.Log($"Sending notification: {notification.title}");
        
        // Example: Send HTTP request, call API, etc.
        // ...
        
        onCompleted?.Invoke(true);
    }
}
```

### Option 2: Extend DiscordNotificationProvider (Discord)

For Discord webhook notifications:

```csharp
using UnityEngine;
using UnityEditor;
using Molca.Settings;

[CreateAssetMenu(fileName = "Custom Build Event Notifier", menuName = "Molca/Editor/Notifications/Custom Build Event Notifier")]
public class CustomBuildEventNotifier : DiscordNotificationProvider
{
    public override string DisplayName => "Custom Build Events";
    
    // Hook into Unity's build events
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }
    
    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        var settings = NotificationSettings.GetOrCreateSettings();
        var provider = settings.GetProvider<CustomBuildEventNotifier>();
        
        if (provider != null && provider.IsEnabled)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                provider.SendPlayModeNotification("Entered Play Mode");
            }
        }
    }
    
    private void SendPlayModeNotification(string message)
    {
        var embed = CreateEmbed("Play Mode", message, 0x00ff00);
        SendEmbedNotification(embed, (success) => {
            if (success) Debug.Log("Play mode notification sent!");
        });
    }
}
```

### Option 3: Custom Events

You can create notification providers for any custom Unity editor events:

```csharp
using UnityEngine;
using UnityEditor;
using Molca.Settings;

[CreateAssetMenu(fileName = "Asset Import Notifier", menuName = "Molca/Editor/Notifications/Asset Import Notifier")]
public class AssetImportNotifier : DiscordNotificationProvider
{
    public override string DisplayName => "Asset Import Notifications";
    
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        // Subscribe to asset import events
        AssetDatabase.importPackageCompleted += OnPackageImported;
        AssetDatabase.importPackageFailed += OnPackageImportFailed;
    }
    
    private static void OnPackageImported(string packageName)
    {
        var settings = NotificationSettings.GetOrCreateSettings();
        var provider = settings.GetProvider<AssetImportNotifier>();
        
        if (provider != null && provider.IsEnabled)
        {
            var embed = provider.CreateEmbed(
                "Package Imported",
                $"Successfully imported: {packageName}",
                0x00ff00 // Green
            );
            
            provider.SendEmbedNotification(embed);
        }
    }
    
    private static void OnPackageImportFailed(string packageName, string errorMessage)
    {
        var settings = NotificationSettings.GetOrCreateSettings();
        var provider = settings.GetProvider<AssetImportNotifier>();
        
        if (provider != null && provider.IsEnabled)
        {
            var embed = provider.CreateEmbed(
                "Package Import Failed",
                $"Failed to import {packageName}: {errorMessage}",
                0xff0000 // Red
            );
            
            provider.SendEmbedNotification(embed);
        }
    }
}
```

## Setup Instructions

### 1. Create NotificationSettings Asset

1. Navigate to your MolcaProjectSettings asset
2. In the inspector, find the "Notification Settings" field
3. Click "Create" or assign an existing NotificationSettings asset

Or create manually:
- Right-click in Project window
- Create > Molca > Editor > Notification Settings

### 2. Create Notification Providers

The system **automatically discovers** all available notification provider types!

#### Easy Way (Recommended):
1. Open your NotificationSettings asset (or go to Edit → Project Settings → Molca → Notifications)
2. Use the **"Create Notification Provider"** dropdown
3. Select the provider type you want (Build, Light Baking, Addressables Build, or any custom provider)
4. Click **"Create"** button
5. Configure webhook URL and settings
6. The provider is automatically added to the providers array

#### Manual Way:
1. Right-click in Project window
2. Create > Molca > Editor > Notifications > [Choose Provider Type]
3. Configure webhook URL, mentions, and other settings
4. Manually add to NotificationSettings providers array

**Note:** Any custom providers you create will automatically appear in the dropdown!

### 3. Configure Discord Webhook (if using webhook providers)

1. In Discord, go to Server Settings > Integrations > Webhooks
2. Create a new webhook or select existing one
3. Copy the webhook URL
4. Paste into your notification provider's "Webhook Url" field

**Optional Configuration:**
- **Thread ID**: To post in a specific thread, add the thread ID
- **Mention Type**: Select User or Role to mention someone
- **Mention ID**: Discord user or role ID to mention

**Note:** Project name is automatically pulled from MolcaProjectSettings, so you don't need to configure it separately.

### 4. Initialize the System (Optional)

The notification system works automatically with Unity's callback system. However, if you want to initialize it manually:

```csharp
var notificationSettings = MolcaProjectSettings.Instance.NotificationSettings;
if (notificationSettings != null)
{
    notificationSettings.Initialize();
}
```

## Usage Examples

### Sending Notifications Programmatically

```csharp
using Molca.Settings;

// Get notification settings
var settings = NotificationSettings.GetOrCreateSettings();

// Create notification data
var notification = new NotificationData(
    "Custom Event",
    "Something important happened!",
    NotificationType.Success
);

// Add metadata (optional)
notification.AddMetadata("Time", System.DateTime.Now.ToString());
notification.AddMetadata("User", System.Environment.UserName);

// Send through a specific provider
settings.SendNotification<BuildNotificationProvider>(notification, (success) => {
    if (success) Debug.Log("Notification sent!");
});

// Or broadcast to all enabled providers
settings.BroadcastNotification(notification);
```

### Using DiscordNotificationProvider Directly

```csharp
using Molca.Settings;

var settings = NotificationSettings.GetOrCreateSettings();
var provider = settings.GetProvider<BuildNotificationProvider>();

if (provider != null && provider.IsEnabled)
{
    // Create a Discord embed
    var embed = provider.CreateEmbed(
        "Custom Title",
        "Custom description",
        0x0099ff // Blue color
    );
    
    // Add fields
    embed.AddField("Field 1", "Value 1", true);
    embed.AddField("Field 2", "Value 2", true);
    
    // Send
    provider.SendEmbedNotification(embed);
}
```

## Best Practices

1. **Keep Providers Focused**: Each provider should handle one type of event or service
2. **Use Descriptive Names**: Make it clear what each provider does
3. **Handle Failures Gracefully**: Always provide callbacks and log errors
4. **Respect User Settings**: Check IsEnabled before sending notifications
5. **Add Context**: Include relevant metadata in notifications
6. **Test Thoroughly**: Verify notifications work in different scenarios

## Extending Outside Molca Framework

The notification system is designed to be extended from anywhere in your project:

1. Create a new script anywhere in your project
2. Extend `NotificationProvider` (for custom services) or `DiscordNotificationProvider` (for Discord)
3. Add `[CreateAssetMenu]` attribute for easy asset creation (optional)
4. Implement required methods
5. Your provider **automatically appears** in the "Create Notification Provider" dropdown!

No modifications to the Molca framework are required!

### Example Custom Provider

```csharp
using UnityEngine;
using Molca.Settings;

[CreateAssetMenu(fileName = "My Custom Discord Notifier", menuName = "Custom/My Discord Notifier")]
public class MyCustomDiscordNotifier : DiscordNotificationProvider
{
    public override string DisplayName => "My Custom Discord Notifications";
    
    // Implement your custom Discord notification logic
}
```

After creating this script, **it will automatically appear** in the provider dropdown - no registration needed!

**Note:** `DiscordNotificationProvider` is specifically for Discord webhooks. For other services like Slack, Teams, or custom webhooks, extend `NotificationProvider` directly or create your own base class.

## Troubleshooting

### Notifications Not Sending

1. Check if provider is enabled
2. Verify webhook URL is correct
3. Check Unity Console for error messages
4. Verify network connectivity
5. Check Discord server permissions (if using Discord)

### Provider Not Showing in Settings

1. Ensure provider extends NotificationProvider
2. Verify provider is a ScriptableObject
3. Check if provider asset is created
4. Ensure it's added to NotificationSettings providers array

### Build Notifications Not Working

1. Verify BuildNotificationProvider is in NotificationSettings
2. Check if provider IsEnabled is true
3. Ensure webhook configuration is correct
4. Check if builds are actually completing (not just starting)

## API Reference

### NotificationProvider

- `bool IsEnabled`: Check if provider is enabled and configured
- `string DisplayName`: Display name for UI
- `void SendNotification(NotificationData, Action<bool>)`: Send a notification
- `bool ValidateConfiguration()`: Validate provider configuration
- `string GetStatusMessage()`: Get current status message

### DiscordNotificationProvider

Inherits from NotificationProvider, adds Discord-specific features:
- `string webhookUrl`: Discord webhook endpoint URL
- `string threadId`: Optional Discord thread ID
- `MentionType mentionType`: None, User, or Role (Discord-specific)
- `string mentionId`: Discord user/role ID to mention
- `string ProjectName`: Automatically pulled from MolcaProjectSettings

### NotificationSettings

- `void Initialize()`: Initialize all providers
- `T GetProvider<T>()`: Get specific provider by type
- `List<NotificationProvider> GetEnabledProviders()`: Get all enabled providers
- `void SendNotification<T>(NotificationData, Action<bool>)`: Send via specific provider
- `void BroadcastNotification(NotificationData, Action<bool>)`: Send via all providers

## Discord Embed Colors

Common color codes for Discord embeds:

```csharp
0x00ff00 // Green - Success
0xff0000 // Red - Error
0xffff00 // Yellow - Warning
0x0099ff // Blue - Info
0x808080 // Gray - Neutral/Unknown
0xff9900 // Orange - In Progress
```

## Support

For issues, questions, or contributions related to the notification system, please refer to the main Molca framework documentation or create an issue in the repository.

