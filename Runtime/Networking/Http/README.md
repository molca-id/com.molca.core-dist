# Molca Networking Framework

A robust, Postman-like HTTP client framework for Unity that provides a modern, type-safe, and easy-to-use API for making HTTP requests.

## 🚀 Features

- **Postman-like Interface**: Custom editor with tabs for Request, Headers, Body, Settings, and Response
- **Fluent API**: Chain methods for building requests
- **Type Safety**: Strongly typed request/response models
- **Request Validation**: Built-in validation with detailed error messages
- **Request History**: Automatic tracking of request history
- **Multiple Response Types**: Support for Text, JSON, Binary, Texture, AudioClip, and AssetBundle
- **Progress Tracking**: Real-time progress callbacks
- **Error Handling**: Comprehensive error handling and reporting
- **Request Queuing**: Automatic request queuing and concurrency control
- **Default Headers**: Global default headers support
- **Request Cancellation**: Ability to cancel requests
- **JSON Support**: Built-in JSON serialization/deserialization

## 📁 Architecture

```
Assets/_Molca/_Core/Networking/
├── Models/
│   ├── HttpRequest.cs          # Request model with validation
│   └── HttpResponse.cs         # Response model with metadata
├── HttpClient.cs               # Main HTTP client with queuing
├── HttpRequestAsset.cs         # ScriptableObject wrapper
├── Editor/
│   ├── HttpRequestDrawer.cs    # Custom editor interface
│   └── NetworkRequestInfoDrawer.cs # Legacy editor (for compatibility)
└── README.md                   # This documentation

Assets/_Molca/_Core/Settings/Modules/
└── HttpModule.cs               # Global HTTP settings module

Assets/_Molca/_Core/Editor/
└── HttpModuleDrawer.cs         # Custom editor for HttpModule
```

## 🛠️ Setup

1. **Add HttpClient to Runtime Manager**: Add the `HttpClient` component to your Runtime Manager prefab
2. **Create HttpModule**: Right-click in Project → Create → Molca → Settings → HTTP
3. **Add HttpModule to Global Settings**: Add the HttpModule to your GlobalSettings modules array
4. **Configure HTTP Settings**: Use the custom editor to configure base URL, headers, and other settings
5. **Create Request Assets**: Right-click in Project → Create → Molca → HTTP Request
6. **Update AuthManager**: Configure AuthManager to use HttpRequestAsset instead of NetworkRequestInfo

## 📖 Usage Examples

### 1. Using HttpRequestAsset (Recommended)

```csharp
// Create an HTTP Request asset in the editor
[SerializeField] private HttpRequestAsset loginRequest;

// Send the request
async void LoginUser(string username, string password)
{
    // Set JSON body
    loginRequest.SetJsonBody(new { username, password });
    
    try
    {
        var response = await loginRequest.SendAsync();
        if (response.isSuccess)
        {
            var userData = response.GetJsonData<UserData>();
            Debug.Log($"Logged in user: {userData.name}");
        }
    }
    catch (Exception e)
    {
        Debug.LogError($"Login failed: {e.Message}");
    }
}
```

### 2. Using Fluent API

```csharp
// Simple GET request
var response = await HttpClient.CreateRequest()
    .Method(HttpMethod.GET)
    .Url("api/users")
    .Header("Authorization", "Bearer " + token)
    .SendAsync();

// POST with JSON body
var response = await HttpClient.CreateRequest()
    .Method(HttpMethod.POST)
    .Url("api/users")
    .Header("Content-Type", "application/json")
    .JsonBody(new { name = "John", email = "john@example.com" })
    .SendAsync();

// Form data POST
var response = await HttpClient.CreateRequest()
    .Method(HttpMethod.POST)
    .Url("api/upload")
    .FormField("name", "file.txt")
    .BinaryField("file", fileBytes, "file.txt")
    .SendAsync();
```

### 3. Using Callbacks

```csharp
HttpClient.Send(
    request: myRequest,
    onSuccess: (response) => {
        Debug.Log($"Success: {response.statusCode}");
        var data = response.GetJsonData<MyData>();
    },
    onError: (error) => {
        Debug.LogError($"Error: {error}");
    },
    onProgress: (progress) => {
        Debug.Log($"Progress: {progress:P1}");
    }
);
```

### 4. Direct HttpRequest Usage

```csharp
var request = new HttpRequest
{
    name = "Get Users",
    method = HttpMethod.GET,
    url = "api/users",
    expectedResponseType = ResponseType.Json
};

request.AddHeader("Authorization", "Bearer " + token);

var response = await HttpClient.SendAsync(request);
```

### 5. AuthManager Integration

```csharp
// Configure AuthManager with HttpRequestAsset
[SerializeField] private HttpRequestAsset loginRequest;
[SerializeField] private HttpRequestAsset logoutRequest;
[SerializeField] private HttpRequestAsset validateTokenRequest;

// AuthManager automatically uses the new HttpClient architecture
var authManager = GetComponent<AuthManager>();
bool success = await authManager.LoginAsync("username", "password");

// Apply authentication token to requests
var request = new HttpRequest { url = "api/protected" };
authManager.TryApplyToken(request);
```

## 🎨 Editor Interface

The custom editor provides a Postman-like interface with:

### Request Tab
- HTTP Method selection
- URL input with base URL preview
- Response type selection
- URL preview with copy button

### Headers Tab
- Add/remove headers
- Enable/disable headers
- Common headers presets
- Header validation

### Body Tab
- Body type selection (None, JSON, Form, Binary)
- JSON editor with formatting
- Form fields management
- Binary file uploads

### Settings Tab
- Timeout configuration
- Follow redirects
- SSL validation
- Advanced options

### Response Tab
- Response status and timing
- Response headers
- Formatted response body
- Copy response functionality

## 🔧 Configuration

### HttpModule - Global Settings

The `HttpModule` provides global access to HTTP configuration settings that can be accessed from both editor and runtime. It follows the same pattern as other setting modules like `LocalizationModule`.

#### Features:
- **Global Access**: Access HTTP settings from anywhere in your project
- **Persistent Storage**: Settings are automatically saved to PlayerPrefs
- **Editor Integration**: Custom editor with intuitive interface
- **Default Headers**: Manage global default headers
- **Runtime Configuration**: Change settings at runtime

#### Setup:
1. Create HttpModule asset: Right-click → Create → Molca → Settings → HTTP
2. Add to GlobalSettings: Add the HttpModule to your GlobalSettings modules array
3. Configure settings using the custom editor

#### Usage:
```csharp
// Get HTTP module
var httpModule = GlobalSettings.GetModule<HttpModule>();

// Configure basic settings
httpModule.BaseUrl = "https://api.example.com";
httpModule.MaxConcurrentRequests = 8;
httpModule.DefaultTimeout = 60;

// Manage default headers
httpModule.SetDefaultHeader("Authorization", "Bearer token");
httpModule.SetDefaultHeader("Content-Type", "application/json");
httpModule.RemoveDefaultHeader("User-Agent");

// Advanced settings
httpModule.EnableRequestHistory = true;
httpModule.MaxHistorySize = 200;
httpModule.FollowRedirects = true;
httpModule.ValidateSSL = true;
```

### HttpModule Settings

```csharp
// Access HTTP settings globally
var httpModule = GlobalSettings.GetModule<HttpModule>();

// Configure settings
httpModule.BaseUrl = "https://api.example.com";
httpModule.MaxConcurrentRequests = 4;
httpModule.DefaultTimeout = 30;

// Manage default headers
httpModule.SetDefaultHeader("Authorization", "Bearer token");
httpModule.SetDefaultHeader("Content-Type", "application/json");
httpModule.RemoveDefaultHeader("User-Agent");
```

### Global Default Headers

```csharp
// Set default headers for all requests (via HttpModule)
var httpModule = GlobalSettings.GetModule<HttpModule>();
httpModule.SetDefaultHeader("User-Agent", "MyApp/1.0");
httpModule.SetDefaultHeader("Accept", "application/json");

// Or use HttpClient directly (fallback)
HttpClient.AddDefaultHeader("User-Agent", "MyApp/1.0");
HttpClient.AddDefaultHeader("Accept", "application/json");

// Remove default header
httpModule.RemoveDefaultHeader("User-Agent");
// or
HttpClient.RemoveDefaultHeader("User-Agent");

// Clear all default headers
httpModule.ClearDefaultHeaders();
// or
HttpClient.ClearDefaultHeaders();
```

## 📊 Request History

```csharp
// Access request history
var history = HttpClient.RequestHistory;

foreach (var context in history)
{
    Debug.Log($"Request: {context.request.name}");
    Debug.Log($"Duration: {context.Duration}");
    Debug.Log($"Status: {context.response?.statusCode}");
}

// Clear history
HttpClient.ClearHistory();
```

## 🚫 Request Cancellation

```csharp
// Cancel all active requests
HttpClient.CancelAllRequests();
```

## 🔍 Validation

```csharp
// Validate request before sending
if (!request.Validate(out var errors))
{
    Debug.LogError($"Validation failed: {string.Join(", ", errors)}");
    return;
}
```

## 📝 Response Handling

```csharp
var response = await HttpClient.SendAsync(request);

// Check success
if (response.isSuccess)
{
    // Handle success
    Debug.Log($"Status: {response.statusCode}");
    Debug.Log($"Response time: {response.responseTime:F2}s");
    Debug.Log($"Content length: {response.contentLength} bytes");
}

// Get response content
string text = response.GetContentAsString();
var jsonData = response.GetJsonData<MyData>();

// Check content type
if (response.IsJson)
{
    // Handle JSON response
}
else if (response.IsImage)
{
    // Handle image response
    Texture2D texture = response.texture;
}
```

## 🔄 Migration from Legacy NetworkManager

The old `NetworkManager` has been deprecated in favor of the new `HttpClient` architecture. See [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md) for detailed migration instructions.

### Quick Migration Steps:

1. **Replace NetworkManager with HttpClient** in your Runtime Manager
2. **Convert NetworkRequestInfo assets** to HttpRequestAsset assets
3. **Update AuthManager configuration** to use HttpRequestAsset
4. **Update event subscriptions** (e.g., `NetworkManager.onConnectionError` → `HttpClient.OnConnectionError`)

### Backward Compatibility

- `NetworkManager` is marked as obsolete but still functional
- `NetworkRequestInfo` assets continue to work
- Gradual migration is supported

### Migration Helper

```csharp
// Convert NetworkRequestInfo to HttpRequest
public static HttpRequest FromNetworkRequestInfo(NetworkRequestInfo nri)
{
    var request = new HttpRequest
    {
        name = nri.requestName,
        method = (HttpMethod)nri.method,
        url = nri.urlPath,
        useFullUrl = nri.usePathAsFullUrl,
        timeout = nri.timeout,
        expectedResponseType = (ResponseType)nri.returnType,
        jsonBody = nri.jsonBody
    };
    
    // Convert headers
    foreach (var header in nri.headers)
    {
        request.AddHeader(header.key, header.value);
    }
    
    // Convert form data
    foreach (var form in nri.forms)
    {
        if (!string.IsNullOrEmpty(form.stringValue))
            request.AddFormField(form.key, form.stringValue);
        else if (form.binaryValue != null)
            request.AddBinaryField(form.key, form.binaryValue);
    }
    
    return request;
}
```

## 🐛 Troubleshooting

### Common Issues

1. **"HttpClient is not initialized"**
   - Make sure HttpClient is added to Runtime Manager
   - Check that Runtime Manager is initialized

2. **"Validation failed"**
   - Check URL format
   - Ensure required fields are filled
   - Validate JSON format

3. **"Connection error"**
   - Check network connectivity
   - Verify URL is accessible
   - Check SSL certificates

4. **"Timeout"**
   - Increase timeout value
   - Check server response time
   - Verify network stability

### Debug Tips

```csharp
// Enable detailed logging
HttpClient.OnRequestStarted += (context) => {
    Debug.Log($"[HTTP] Started: {context.request.name}");
};

HttpClient.OnRequestCompleted += (context) => {
    Debug.Log($"[HTTP] Completed: {context.request.name} - {context.response.statusCode}");
};

HttpClient.OnRequestFailed += (context) => {
    Debug.LogError($"[HTTP] Failed: {context.request.name} - {context.response.errorMessage}");
};
```

## 📚 Best Practices

1. **Use HttpRequestAsset** for reusable requests
2. **Validate requests** before sending
3. **Handle errors** properly
4. **Use appropriate response types**
5. **Set reasonable timeouts**
6. **Use request history** for debugging
7. **Implement retry logic** for critical requests
8. **Cache responses** when appropriate
9. **Use progress callbacks** for large downloads
10. **Clean up resources** properly

## 🔮 Future Enhancements

- Request templates
- Environment variables
- Request collections
- Automated testing
- Performance monitoring
- Request caching
- Rate limiting
- Retry policies
- Circuit breaker pattern 