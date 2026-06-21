# Progress Tracking Guide

## Overview

The HttpClient system provides multiple ways to track progress for HTTP requests. This guide covers all the available methods and their use cases.

## 🔄 Progress Tracking Methods

### 1. HttpClient.Send() with Progress Callback (Recommended)

The most straightforward way to track progress is using the `HttpClient.Send()` method with a progress callback.

```csharp
// Basic progress tracking
HttpClient.Send(
    request: myRequest,
    onSuccess: (response) => {
        Debug.Log($"Download completed: {response.contentLength} bytes");
    },
    onError: (error) => {
        Debug.LogError($"Download failed: {error}");
    },
    onProgress: (progress) => {
        Debug.Log($"Download progress: {progress:P1}");
        // Update UI progress bar
        progressBar.value = progress;
    }
);
```

### 2. HttpRequestAsset with Progress Callback

For reusable request assets, use the `HttpRequestAsset.Send()` method.

```csharp
[SerializeField] private HttpRequestAsset downloadRequest;

void DownloadFile()
{
    downloadRequest.Send(
        onSuccess: (response) => {
            Debug.Log($"Download completed: {response.contentLength} bytes");
            // Handle successful download
        },
        onError: (error) => {
            Debug.LogError($"Download failed: {error}");
            // Handle error
        },
        onProgress: (progress) => {
            Debug.Log($"Download progress: {progress:P1}");
            // Update UI
            UpdateProgressUI(progress);
        }
    );
}

void UpdateProgressUI(float progress)
{
    // Update progress bar
    progressBar.value = progress;
    progressText.text = $"{progress:P1}";
}
```

### 3. Fluent API with Progress

Use the HttpRequestBuilder for chained method calls with progress tracking.

```csharp
HttpClient.CreateRequest()
    .Method(HttpMethod.GET)
    .Url("api/files/large-file.zip")
    .ResponseType(ResponseType.Binary)
    .Send(
        onSuccess: (response) => {
            Debug.Log("Download completed!");
        },
        onError: (error) => {
            Debug.LogError($"Download failed: {error}");
        },
        onProgress: (progress) => {
            Debug.Log($"Progress: {progress:P1}");
        }
    );
```

### 4. Global Event Tracking

Subscribe to global events for monitoring all requests.

```csharp
void Start()
{
    HttpClient.OnRequestStarted += OnRequestStarted;
    HttpClient.OnRequestCompleted += OnRequestCompleted;
    HttpClient.OnRequestFailed += OnRequestFailed;
}

void OnRequestStarted(HttpRequestContext context)
{
    Debug.Log($"Request started: {context.request.name}");
    // Show global loading indicator
    ShowGlobalLoading();
}

void OnRequestCompleted(HttpRequestContext context)
{
    Debug.Log($"Request completed: {context.request.name} in {context.Duration.TotalSeconds:F2}s");
    // Hide global loading indicator
    HideGlobalLoading();
}

void OnRequestFailed(HttpRequestContext context)
{
    Debug.LogError($"Request failed: {context.request.name} - {context.response?.errorMessage}");
    // Show error notification
    ShowErrorNotification(context.response?.errorMessage);
}

void OnDestroy()
{
    HttpClient.OnRequestStarted -= OnRequestStarted;
    HttpClient.OnRequestCompleted -= OnRequestCompleted;
    HttpClient.OnRequestFailed -= OnRequestFailed;
}
```

### 5. MediaLoader Style Progress Tracking

For media downloads with loading modals, use the MediaLoader pattern.

```csharp
public async Awaitable<Texture2D> DownloadTexture(string url)
{
    var loading = _modalManager.AddLoading(url);
    
    try
    {
        var tcs = new AwaitableCompletionSource<HttpResponse>();
        
        textureRequest.Send(
            onSuccess: (response) => {
                if (response.texture != null)
                {
                    // Handle successful download
                    tcs.SetResult(response);
                }
                else
                {
                    tcs.SetException(new Exception("No texture data received"));
                }
            },
            onError: (error) => {
                tcs.SetException(new Exception(error));
            },
            onProgress: (progress) => {
                loading.Refresh($"Downloading texture {progress:P1}", progress);
            }
        );
        
        var response = await tcs.Awaitable;
        return response.texture;
    }
    finally
    {
        _modalManager.RemoveLoading(url);
    }
}
```

## 📊 Progress Information

The progress callback provides a `float` value between 0.0 and 1.0:

- `0.0` = 0% complete
- `0.5` = 50% complete  
- `1.0` = 100% complete

### Additional Progress Data

You can also access detailed progress information from the HttpResponse:

```csharp
void OnSuccess(HttpResponse response)
{
    Debug.Log($"Download completed:");
    Debug.Log($"  - Size: {response.contentLength} bytes");
    Debug.Log($"  - Time: {response.responseTime:F2} seconds");
    Debug.Log($"  - Type: {response.contentType}");
}
```

## 🎨 UI Integration Examples

### Progress Bar Integration

```csharp
[SerializeField] private Slider progressBar;
[SerializeField] private Text progressText;
[SerializeField] private Button downloadButton;

void StartDownload()
{
    downloadButton.interactable = false;
    progressBar.value = 0f;
    progressText.text = "0%";
    
    myRequest.Send(
        onSuccess: (response) => {
            progressBar.value = 1f;
            progressText.text = "100%";
            downloadButton.interactable = true;
            Debug.Log("Download completed!");
        },
        onError: (error) => {
            progressBar.value = 0f;
            progressText.text = "Failed";
            downloadButton.interactable = true;
            Debug.LogError($"Download failed: {error}");
        },
        onProgress: (progress) => {
            progressBar.value = progress;
            progressText.text = $"{progress:P1}";
        }
    );
}
```

### Loading Modal Integration

```csharp
public class LoadingModal : MonoBehaviour
{
    [SerializeField] private Slider progressBar;
    [SerializeField] private Text messageText;
    [SerializeField] private Text progressText;
    
    public void Show(string message)
    {
        gameObject.SetActive(true);
        messageText.text = message;
        progressBar.value = 0f;
        progressText.text = "0%";
    }
    
    public void UpdateProgress(string message, float progress)
    {
        messageText.text = message;
        progressBar.value = progress;
        progressText.text = $"{progress:P1}";
    }
    
    public void Hide()
    {
        gameObject.SetActive(false);
    }
}

// Usage
var loadingModal = FindObjectOfType<LoadingModal>();

loadingModal.Show("Downloading file...");

myRequest.Send(
    onSuccess: (response) => {
        loadingModal.Hide();
        Debug.Log("Download completed!");
    },
    onError: (error) => {
        loadingModal.Hide();
        Debug.LogError($"Download failed: {error}");
    },
    onProgress: (progress) => {
        loadingModal.UpdateProgress($"Downloading file... {progress:P1}", progress);
    }
);
```

## 🔧 Advanced Progress Tracking

### Multiple Concurrent Downloads

```csharp
public class DownloadManager : MonoBehaviour
{
    [SerializeField] private HttpRequestAsset[] downloadRequests;
    [SerializeField] private Slider[] progressBars;
    
    private int completedDownloads = 0;
    private int totalDownloads;
    
    void StartDownloads()
    {
        totalDownloads = downloadRequests.Length;
        completedDownloads = 0;
        
        for (int i = 0; i < downloadRequests.Length; i++)
        {
            int index = i; // Capture for closure
            StartDownload(index);
        }
    }
    
    void StartDownload(int index)
    {
        downloadRequests[index].Send(
            onSuccess: (response) => {
                completedDownloads++;
                Debug.Log($"Download {index + 1} completed. Total: {completedDownloads}/{totalDownloads}");
                
                if (completedDownloads == totalDownloads)
                {
                    Debug.Log("All downloads completed!");
                }
            },
            onError: (error) => {
                Debug.LogError($"Download {index + 1} failed: {error}");
            },
            onProgress: (progress) => {
                progressBars[index].value = progress;
            }
        );
    }
}
```

### Progress with Cancellation

```csharp
public class CancellableDownload
{
    private bool isCancelled = false;
    
    public async Awaitable<HttpResponse> DownloadWithCancellation(HttpRequestAsset request)
    {
        var tcs = new AwaitableCompletionSource<HttpResponse>();
        
        request.Send(
            onSuccess: (response) => {
                if (!isCancelled)
                    tcs.SetResult(response);
            },
            onError: (error) => {
                if (!isCancelled)
                    tcs.SetException(new Exception(error));
            },
            onProgress: (progress) => {
                if (!isCancelled)
                    Debug.Log($"Progress: {progress:P1}");
            }
        );
        
        return await tcs.Awaitable;
    }
    
    public void Cancel()
    {
        isCancelled = true;
    }
}
```

## 📝 Best Practices

1. **Always handle errors**: Provide error callbacks for all progress tracking
2. **Update UI on main thread**: Ensure UI updates happen on the main thread
3. **Clean up event subscriptions**: Unsubscribe from global events when done
4. **Use appropriate progress indicators**: Choose the right UI element for your use case
5. **Handle cancellation**: Provide ways to cancel long-running downloads
6. **Show meaningful progress**: Display both percentage and descriptive text

## 🚀 Performance Tips

1. **Throttle progress updates**: Don't update UI on every progress callback if it's too frequent
2. **Use object pooling**: Reuse progress UI elements for multiple downloads
3. **Batch UI updates**: Group multiple UI updates together
4. **Profile progress callbacks**: Ensure progress callbacks don't impact performance

## 🔍 Debugging Progress Issues

```csharp
// Enable detailed logging
HttpClient.OnRequestStarted += (context) => {
    Debug.Log($"[PROGRESS] Started: {context.request.name}");
};

HttpClient.OnRequestCompleted += (context) => {
    Debug.Log($"[PROGRESS] Completed: {context.request.name} - Duration: {context.Duration.TotalSeconds:F2}s");
};

// Add progress logging to your callbacks
onProgress: (progress) => {
    Debug.Log($"[PROGRESS] {progress:P1}");
    // Your progress handling code
}
``` 