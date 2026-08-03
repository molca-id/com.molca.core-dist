using Molca.Networking.Utils;
using Molca.App;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using Molca.Attributes;
#if VUPLEX_STANDALONE
using Vuplex.WebView;
#endif

/// <summary>
/// Handles document viewing functionality using Vuplex WebView.
/// This component requires Vuplex WebView package to be installed.
/// 
/// Setup Instructions:
/// 1. Add Vuplex WebView package to your project
/// 2. Create a prefab with this component and required references:
///    - WebView Reference (AssetReference to Vuplex WebView prefab)
///    - Background GameObject
///    - Title TextMeshProUGUI
///    - Close Button
/// 3. Add the prefab to your scene or instantiate it at runtime
/// 
/// Usage Example:
/// <code>
/// // Load a document using MediaInfo
/// await DocumentHandler.Load(mediaInfo);
/// 
/// // Or load directly with parameters
/// await DocumentHandler.Load("file://path/to/document.pdf", "Document Title", 1, "cache-id");
/// </code>
/// </summary>
namespace Molca.App.Media
{
    public class DocumentHandler : MonoBehaviour
    {
        // Instance API of the network cache (Sprint 5.1 de-static).
        private static ICacheService Cache => Molca.RuntimeManager.GetService<ICacheService>();
        private static DocumentHandler _instance;

#if VUPLEX_STANDALONE
        private CanvasWebViewPrefab _webView;
#else
        [InfoBox("Vuplex WebView is not available. Please import the Vuplex WebView package to enable document viewing.", InfoBoxType.Warning)]
#endif

        [SerializeField, Tooltip("Reference to the Vuplex WebView prefab in Addressables")]
        private AssetReference _webViewRef;

        [SerializeField, Tooltip("Background panel that covers the screen when document is open")]
        private GameObject _background;

        [SerializeField, Tooltip("Text component to display the document title")]
        private TextMeshProUGUI _title;

        [SerializeField, Tooltip("Button to close the document viewer")]
        private Button _closeButton;

        private ScreenOrientation _cachedOrientation;

        private void Awake()
        {
            if (_instance != null)
            {
                Debug.Log($"{typeof(DocumentHandler)} already exist: {_instance}");
                Destroy(gameObject);
                return;
            }

            _instance = this;
    #if VUPLEX_STANDALONE
            _webView = GetComponentInChildren<CanvasWebViewPrefab>(true);
            if (_webView == null)
            {
                Debug.LogWarning("CanvasWebViewPrefab component not found in children. Make sure the WebView prefab is properly set up.");
            }
    #endif
            _closeButton.onClick.AddListener(CloseViewer);
            ToggleVisibility(false);
        }

        /// <summary>
        /// Loads a document using MediaInfo object.
        /// </summary>
        /// <param name="info">MediaInfo containing document details (url, name, version, id)</param>
        /// <returns>True if document was loaded successfully, false otherwise</returns>
        public static async Awaitable<bool> Load(MediaInfo info)
        {
            return await Load(info.url, info.name, info.version, $"document-{info.id}");
        }

        /// <summary>
        /// Loads a document with specified parameters.
        /// </summary>
        /// <param name="url">URL or file path of the document</param>
        /// <param name="title">Title to display in the viewer (optional)</param>
        /// <param name="version">Document version (optional, defaults to 1)</param>
        /// <param name="cacheId">Unique identifier for caching (optional)</param>
        /// <returns>True if document was loaded successfully, false otherwise</returns>
        /// <remarks>
        /// The document will be loaded from cache if available. If not cached, it will attempt to load from the provided URL.
        /// When Vuplex is not available, this method will return false and log a warning.
        /// </remarks>
        public static async Awaitable<bool> Load(string url, string title = null, int version = 1, string cacheId = null)
        {
    #if !VUPLEX_STANDALONE
            Debug.LogWarning("DocumentHandler: Vuplex WebView is not available. Please import the Vuplex WebView package to enable document viewing.");
            await Awaitable.NextFrameAsync();
            return false;
    #else
            string filePath = null;
            if(!string.IsNullOrEmpty(cacheId) && Cache != null && Cache.IsCached(cacheId))
                filePath = await Cache.GetCachePath(cacheId);

            if (string.IsNullOrEmpty(filePath))
            {
                _instance.CloseViewer();
                return false;
            }
            else
            {
                _instance.ToggleVisibility(true);
                _instance._title.text = string.IsNullOrWhiteSpace(title) ? "Document Viewer" : title;
                _instance._cachedOrientation = Screen.orientation;
                Screen.orientation = ScreenOrientation.AutoRotation;

                await CreateWebView();
                _instance._webView.WebView.LoadUrl("file://" + filePath.Replace(" ", "%20"));
                return true;
            }
    #endif
        }

    #if VUPLEX_STANDALONE
        /// <summary>
        /// Creates and initializes the WebView component.
        /// </summary>
        private static async Awaitable CreateWebView()
        {
            if (_instance._webView != null)
                return;

            var async = _instance._webViewRef.InstantiateAsync(_instance.transform);
            while (!async.IsDone)
                await Awaitable.NextFrameAsync();
            _instance._webView = async.Result.GetComponent<CanvasWebViewPrefab>();
            await _instance._webView.WaitUntilInitialized();
        }

        /// <summary>
        /// Cleans up the WebView component and releases its resources.
        /// </summary>
        private static async Awaitable ClearWebView()
        {
            if (_instance._webView == null)
                return;
            await _instance._webView.WaitUntilInitialized();
            _instance._webView.WebView.LoadUrl("about:blank");
    #if UNITY_ANDROID && !UNITY_EDITOR
            (_instance._webView.WebView as AndroidWebView).ClearHistory();
    #endif

            _instance._webViewRef.ReleaseInstance(_instance._webView.gameObject);
        }
    #endif

        /// <summary>
        /// Closes the document viewer and restores the previous screen orientation.
        /// </summary>
        public async void CloseViewer()
        {
            try
            {
                ToggleVisibility(false);
    #if VUPLEX_STANDALONE
                await ClearWebView();
    #endif
                await Awaitable.NextFrameAsync();
                Screen.orientation = _cachedOrientation;
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Toggles the visibility of the document viewer UI elements.
        /// </summary>
        /// <param name="visible">Whether the viewer should be visible</param>
        public void ToggleVisibility(bool visible)
        {
            _background.gameObject.SetActive(visible);
            _title.gameObject.SetActive(visible);
        }
    }
}