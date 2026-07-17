using System;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// Default <see cref="IHttpTransport"/> backed by <see cref="UnityWebRequest"/>.
    /// Aborts the underlying request when the cancellation token fires.
    /// </summary>
    public class UnityWebRequestTransport : IHttpTransport
    {
        /// <inheritdoc />
        public async Awaitable<HttpResponse> SendAsync(HttpRequest request, Action<float> onProgress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var webRequest = CreateWebRequest(request);
            // Abort() forces the in-flight operation to complete immediately with an
            // error; the isDone loop below then observes the cancelled token and throws.
            using var abortRegistration = cancellationToken.Register(() => webRequest.Abort());

            // Monotonic elapsed time — used to tag timeouts with a typed exception.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                onProgress?.Invoke(webRequest.downloadProgress);
                await Awaitable.NextFrameAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke(1f);

            return CreateHttpResponse(webRequest, request, stopwatch.Elapsed);
        }

        private static UnityWebRequest CreateWebRequest(HttpRequest request)
        {
            var uri = new Uri(request.FullUrl);
            var webRequest = new UnityWebRequest(uri)
            {
                method = request.method.ToString(),
                timeout = request.timeout,
                // Wire the request's redirect config (previously dead): 0 disables
                // redirect following entirely; otherwise keep Unity's default cap.
                redirectLimit = request.followRedirects ? 32 : 0
            };

            // Wire the request's SSL-validation config (previously dead). Disabling
            // validation accepts ANY certificate — development/self-signed endpoints
            // only, never production.
            if (!request.validateSSL)
                webRequest.certificateHandler = new AcceptAllCertificatesHandler();

            foreach (var header in request.headers.Where(h => h.isEnabled))
            {
                webRequest.SetRequestHeader(header.key, header.value);
            }

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

        /// <summary>
        /// Accepts every certificate. Installed only when a request explicitly sets
        /// <see cref="HttpRequest.validateSSL"/> to <c>false</c> — a development
        /// escape hatch for self-signed endpoints.
        /// </summary>
        private sealed class AcceptAllCertificatesHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

        private static HttpResponse CreateHttpResponse(UnityWebRequest webRequest, HttpRequest request, TimeSpan elapsed)
        {
            int statusCode = (int)webRequest.responseCode;
            // Success is driven by the HTTP status code, not by the transport error
            // string: a 4xx/5xx with a body completes without a UnityWebRequest.error,
            // so inferring success from the error alone reported failures as successes.
            // statusCode == 0 means the exchange never produced an HTTP status
            // (connection/protocol failure), which is never a success.
            bool isSuccess = HttpResponse.IsSuccessStatusCode(statusCode);

            var response = new HttpResponse
            {
                isSuccess = isSuccess,
                statusCode = statusCode,
                statusMessage = webRequest.error ?? "OK",
                contentLength = (long)webRequest.downloadedBytes,
                contentType = webRequest.GetResponseHeader("Content-Type")
            };

            foreach (var header in webRequest.GetResponseHeaders() ?? new System.Collections.Generic.Dictionary<string, string>())
            {
                response.AddHeader(header.Key, header.Value);
            }

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
                // Network/timeout/protocol failures carry webRequest.error; an HTTP
                // error status (4xx/5xx) completes with no transport error, so fall
                // back to the status line so callers always get a non-null message.
                response.errorMessage = string.IsNullOrEmpty(webRequest.error)
                    ? $"HTTP {statusCode}"
                    : webRequest.error;

                // Typed timeout classification: UnityWebRequest reports a timeout as a
                // generic connection error, so tag it here — where both the configured
                // timeout and the measured elapsed time are known — instead of leaving
                // downstream code to sniff the error string. (Result.ConnectionError
                // with the full timeout elapsed and no HTTP status is a timeout.)
                bool noHttpExchange = statusCode == 0;
                bool elapsedFullTimeout = request.timeout > 0 && elapsed.TotalSeconds >= request.timeout;
                if (noHttpExchange
                    && webRequest.result == UnityWebRequest.Result.ConnectionError
                    && elapsedFullTimeout)
                {
                    response.exception = new TimeoutException(
                        $"Request exceeded its {request.timeout}s timeout ({elapsed.TotalSeconds:F1}s elapsed).");
                }
            }

            return response;
        }
    }
}
