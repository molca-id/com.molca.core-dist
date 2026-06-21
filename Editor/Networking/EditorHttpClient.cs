using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Networking.Utils;

namespace Molca.Editor
{
    /// <summary>
    /// Editor-specific HTTP client that works outside of play mode
    /// </summary>
    public static class EditorHttpClient
    {
        private static HttpModule _httpModule;
        
        static EditorHttpClient()
        {
            // Get HTTP module from global settings
            _httpModule = GlobalSettings.GetModule<HttpModule>();
        }
        
        /// <summary>
        /// Sends an HTTP request asynchronously in the editor
        /// </summary>
        public static async Awaitable<HttpResponse> SendAsync(HttpRequest request)
        {
#if !UNITY_EDITOR
            throw new InvalidOperationException("EditorHttpClient can only be used in the Unity Editor");
#endif
            
            // Validate request
            if (!request.Validate(out var errors))
            {
                throw new InvalidOperationException($"Validation failed: {string.Join(", ", errors)}");
            }
            
            var webRequest = CreateWebRequest(request);
            
            // Per-request send/success traces are gated by HttpModule.EnableLogging (consistent with the
            // runtime HttpClient); failures are always logged regardless. Toggle it off to quiet the console.
            bool verbose = _httpModule == null || _httpModule.EnableLogging;

            try
            {
                if (verbose)
                    Debug.Log($"[EditorHttpClient] Sending {request.method} request to {LogRedaction.RedactUrl(request.FullUrl)}");

                // Send the request
                var operation = webRequest.SendWebRequest();

                // Wait for completion
                while (!operation.isDone)
                {
                    await Awaitable.NextFrameAsync();
                }

                var response = CreateHttpResponse(webRequest);

                if (response.isSuccess)
                {
                    if (verbose)
                        Debug.Log($"[EditorHttpClient] Request completed successfully: {response.statusCode} {response.statusMessage}");
                }
                else
                {
                    Debug.LogError($"[EditorHttpClient] Request failed: {response.statusCode} {response.statusMessage} - {response.errorMessage}");
                }
                
                return response;
            }
            catch (Exception e)
            {
                Debug.LogError($"[EditorHttpClient] Request failed with exception: {e.Message}");
                throw;
            }
            finally
            {
                webRequest?.Dispose();
            }
        }
        
        private static UnityWebRequest CreateWebRequest(HttpRequest request)
        {
            var uri = new Uri(request.FullUrl);
            var webRequest = new UnityWebRequest(uri);
            
            // Set method
            webRequest.method = request.method.ToString();
            
            // Set timeout
            webRequest.timeout = request.timeout > 0 ? request.timeout : (_httpModule?.DefaultTimeout ?? 30);
            
            // Set headers
            if (_httpModule != null)
            {
                var defaultHeaders = _httpModule.GetDefaultHeaders();
                foreach (var header in defaultHeaders)
                {
                    webRequest.SetRequestHeader(header.Key, header.Value);
                }
            }
            
            foreach (var header in request.headers.Where(h => h.isEnabled))
            {
                webRequest.SetRequestHeader(header.key, header.value);
            }
            
            // Set body
            if (request.HasBody)
            {
                switch (request.bodyType)
                {
                    case BodyType.Json:
                        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(request.jsonBody);
                        webRequest.uploadHandler = new UploadHandlerRaw(jsonBytes);
                        webRequest.SetRequestHeader("Content-Type", "application/json");
                        break;
                        
                    case BodyType.Form:
                        var form = new WWWForm();
                        foreach (var field in request.formFields.Where(f => f.isEnabled))
                        {
                            form.AddField(field.key, field.value);
                        }
                        foreach (var field in request.binaryFields.Where(f => f.isEnabled))
                        {
                            form.AddBinaryData(field.key, field.data, field.filename);
                        }
                        webRequest.uploadHandler = new UploadHandlerRaw(form.data);
                        webRequest.SetRequestHeader("Content-Type", form.headers["Content-Type"]);
                        break;
                        
                    case BodyType.Binary:
                        // Handle binary upload
                        break;
                }
            }
            
            // Set download handler based on expected response type
            switch (request.expectedResponseType)
            {
                case ResponseType.Text:
                case ResponseType.Json:
                case ResponseType.Binary:
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    break;
                case ResponseType.Texture:
                    webRequest.downloadHandler = new DownloadHandlerTexture();
                    break;
                case ResponseType.AudioClip:
                    webRequest.downloadHandler = new DownloadHandlerAudioClip(uri, AudioType.WAV);
                    break;
                case ResponseType.AssetBundle:
                    webRequest.downloadHandler = new DownloadHandlerAssetBundle(uri.ToString(), 0);
                    break;
            }
            
            return webRequest;
        }
        
        private static HttpResponse CreateHttpResponse(UnityWebRequest webRequest)
        {
            int statusCode = (int)webRequest.responseCode;
            // Success is driven by the HTTP status code, not by the transport error string: a 4xx/5xx with a
            // body completes without a UnityWebRequest.error, so inferring success from the error alone reported
            // failures as successes. statusCode == 0 means no HTTP status was produced (connection/protocol
            // failure), which is never a success. Mirrors UnityWebRequestTransport (Sprint 36).
            var response = new HttpResponse
            {
                isSuccess = HttpResponse.IsSuccessStatusCode(statusCode),
                statusCode = statusCode,
                statusMessage = webRequest.error ?? "OK",
                responseTime = webRequest.downloadProgress,
                contentLength = (long)webRequest.downloadedBytes,
                contentType = webRequest.GetResponseHeader("Content-Type")
            };
            
            // Parse response headers
            foreach (var header in webRequest.GetResponseHeaders() ?? new Dictionary<string, string>())
            {
                response.AddHeader(header.Key, header.Value);
            }
            
            // Set content based on download handler
            if (webRequest.downloadHandler != null)
            {
                if (webRequest.downloadHandler is DownloadHandlerBuffer bufferHandler)
                {
                    response.SetContent(bufferHandler.data);
                }
                else if (webRequest.downloadHandler is DownloadHandlerTexture textureHandler)
                {
                    response.SetContent(textureHandler.texture);
                }
                else if (webRequest.downloadHandler is DownloadHandlerAudioClip audioHandler)
                {
                    response.SetContent(audioHandler.audioClip);
                }
                else if (webRequest.downloadHandler is DownloadHandlerAssetBundle bundleHandler)
                {
                    response.SetContent(bundleHandler.assetBundle);
                }
            }
            
            if (!response.isSuccess)
            {
                // Network/timeout/protocol failures carry webRequest.error; an HTTP error status (4xx/5xx)
                // completes with no transport error, so fall back to the status line for a non-null message.
                response.errorMessage = string.IsNullOrEmpty(webRequest.error)
                    ? $"HTTP {statusCode}"
                    : webRequest.error;
            }

            return response;
        }
    }
} 