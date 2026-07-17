using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Molca.Networking.Utils;

namespace Molca.Networking.Http.Models
{
    /// <summary>
    /// Branchable classification of an HTTP failure. Lets callers (and the retry
    /// policy) decide on <i>why</i> a request failed rather than parsing an error
    /// string. <see cref="None"/> denotes success.
    /// </summary>
    public enum HttpErrorKind
    {
        /// <summary>No error — the request succeeded.</summary>
        None,
        /// <summary>Connection-level failure with no HTTP status (DNS/socket/TLS).</summary>
        Network,
        /// <summary>The request exceeded its timeout.</summary>
        Timeout,
        /// <summary>The request was cancelled (not an error condition).</summary>
        Canceled,
        /// <summary>An HTTP 4xx client error.</summary>
        Http4xx,
        /// <summary>An HTTP 5xx server error.</summary>
        Http5xx,
        /// <summary>The response body could not be (de)serialized.</summary>
        Serialization,
        /// <summary>An authentication/authorization failure (assigned by the auth layer).</summary>
        Auth
    }

    /// <summary>
    /// Structured, additive description of an HTTP failure. Mirrors the legacy
    /// <see cref="HttpResponse.errorMessage"/>/<see cref="HttpResponse.exception"/> so callers
    /// can branch on <see cref="Kind"/> without those fields being removed.
    /// </summary>
    public readonly struct HttpError
    {
        /// <summary>The failure classification.</summary>
        public readonly HttpErrorKind Kind;
        /// <summary>The HTTP status code, or <c>0</c> when there was no HTTP exchange.</summary>
        public readonly int StatusCode;
        /// <summary>Human-readable message (mirrors <see cref="HttpResponse.errorMessage"/>).</summary>
        public readonly string Message;
        /// <summary>The underlying transport exception, if any.</summary>
        public readonly Exception Cause;

        public HttpError(HttpErrorKind kind, int statusCode, string message, Exception cause)
        {
            Kind = kind;
            StatusCode = statusCode;
            Message = message;
            Cause = cause;
        }

        /// <summary>A non-error sentinel (<see cref="HttpErrorKind.None"/>).</summary>
        public static readonly HttpError None = new HttpError(HttpErrorKind.None, 0, null, null);

        /// <summary>Whether this represents a real failure (anything but <see cref="HttpErrorKind.None"/>).</summary>
        public bool IsError => Kind != HttpErrorKind.None;

        public override string ToString() => IsError ? $"{Kind}({StatusCode}): {Message}" : "None";
    }

    [Serializable]
    public class HttpResponse
    {
        public bool isSuccess;
        public int statusCode;
        public string statusMessage;
        public Dictionary<string, string> headers = new Dictionary<string, string>();
        public byte[] rawData;
        public string text;
        public Texture2D texture;
        public AudioClip audioClip;
        public AssetBundle assetBundle;
        public float responseTime;
        public long contentLength;
        public string contentType;
        public string errorMessage;
        public Exception exception;
        
        /// <summary>
        /// Whether an HTTP status code denotes success: <c>200 ≤ code &lt; 300</c>.
        /// Used by the transport to set <see cref="isSuccess"/> from the status code
        /// rather than the transport error string (a 4xx/5xx with a body is a failure).
        /// </summary>
        /// <param name="statusCode">The HTTP status code; <c>0</c> (no HTTP exchange) is not success.</param>
        public static bool IsSuccessStatusCode(int statusCode) => statusCode is >= 200 and < 300;

        /// <summary>
        /// Structured classification of this response's failure, derived from the
        /// status code and transport exception. Returns <see cref="HttpError.None"/>
        /// for a successful response. Additive — the legacy <see cref="isSuccess"/>,
        /// <see cref="errorMessage"/>, and <see cref="exception"/> fields are unchanged.
        /// </summary>
        public HttpError Error
        {
            get
            {
                if (isSuccess)
                    return HttpError.None;

                string message = errorMessage ?? statusMessage;
                var kind = ClassifyErrorKind();
                return new HttpError(kind, statusCode, message, exception);
            }
        }

        private HttpErrorKind ClassifyErrorKind()
        {
            if (exception is OperationCanceledException)
                return HttpErrorKind.Canceled;

            // The transport tags a timeout with a typed TimeoutException (measured
            // against the request's configured timeout), so classification does not
            // depend on error-string sniffing.
            if (exception is TimeoutException)
                return HttpErrorKind.Timeout;

            if (statusCode is >= 400 and < 500)
                return HttpErrorKind.Http4xx;
            if (statusCode is >= 500 and < 600)
                return HttpErrorKind.Http5xx;

            // No HTTP status and no typed exception (a transport other than the
            // default may not tag timeouts) — fall back to the error text.
            string signal = (errorMessage ?? statusMessage)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(signal) && (signal.Contains("timeout") || signal.Contains("timed out")))
                return HttpErrorKind.Timeout;

            return HttpErrorKind.Network;
        }

        // Computed properties
        public bool IsJson => contentType?.Contains("application/json") == true;
        public bool IsText => contentType?.Contains("text/") == true || contentType?.Contains("application/json") == true;
        public bool IsImage => contentType?.StartsWith("image/") == true;
        public bool IsAudio => contentType?.StartsWith("audio/") == true;
        
        public T GetJsonData<T>() where T : class
        {
            if (!IsJson || string.IsNullOrEmpty(text))
                return null;
                
            try
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(text);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to deserialize JSON: {e.Message}");
                return null;
            }
        }
        
        public string GetHeaderValue(string key)
        {
            return headers.TryGetValue(key, out string value) ? value : null;
        }
        
        public bool HasHeader(string key)
        {
            return headers.ContainsKey(key);
        }
        
        public void AddHeader(string key, string value)
        {
            headers[key] = value;
        }
        
        public string GetContentAsString()
        {
            if (!string.IsNullOrEmpty(text))
                return text;
                
            if (rawData != null)
                return System.Text.Encoding.UTF8.GetString(rawData);
                
            return null;
        }
        
        public void SetContent(string content)
        {
            text = content;
            rawData = System.Text.Encoding.UTF8.GetBytes(content);
        }
        
        /// <summary>
        /// Sets the raw byte content of the response and decodes it as a UTF-8 string.
        /// </summary>
        /// <param name="data">
        /// The response body bytes. A <c>null</c> value (e.g. an empty body on a 204/401/5xx
        /// response) is treated as an empty payload so the real HTTP status still propagates
        /// to callers instead of throwing.
        /// </param>
        public void SetContent(byte[] data)
        {
            rawData = data ?? System.Array.Empty<byte>();
            text = System.Text.Encoding.UTF8.GetString(rawData);
        }
        
        public void SetContent(Texture2D tex)
        {
            texture = tex;
            if (tex != null)
            {
                rawData = tex.EncodeToPNG();
                text = null;
            }
        }
        
        public void SetContent(AudioClip clip)
        {
            audioClip = clip;
            // Note: AudioClip to byte[] conversion would require additional processing
        }
        
        public void SetContent(AssetBundle bundle)
        {
            assetBundle = bundle;
            // Note: AssetBundle to byte[] conversion would require additional processing
        }
        
        public HttpResponse Clone()
        {
            return new HttpResponse
            {
                isSuccess = this.isSuccess,
                statusCode = this.statusCode,
                statusMessage = this.statusMessage,
                headers = new Dictionary<string, string>(this.headers),
                rawData = this.rawData?.Clone() as byte[],
                text = this.text,
                texture = this.texture,
                audioClip = this.audioClip,
                assetBundle = this.assetBundle,
                responseTime = this.responseTime,
                contentLength = this.contentLength,
                contentType = this.contentType,
                errorMessage = this.errorMessage,
                exception = this.exception
            };
        }
        
        public override string ToString()
        {
            return $"HttpResponse {{ Status: {statusCode} {statusMessage}, Success: {isSuccess}, Time: {responseTime:F2}s, Size: {contentLength} bytes }}";
        }
    }
    
    [Serializable]
    public class HttpRequestContext
    {
        public HttpRequest request;
        public DateTime startTime;
        public DateTime endTime;
        public HttpResponse response;
        public bool wasCancelled;
        public string cancellationReason;
        
        // Events
        public event Action<HttpResponse> OnCompleted;
        public event Action<HttpResponse> OnSuccess;
        public event Action<string> OnError;
        public event Action<string> OnFailed;
        public event Action<float> OnProgress;
        
        public TimeSpan Duration => endTime - startTime;
        public bool IsCompleted => response != null || wasCancelled;

        /// <summary>
        /// Token supplied by the caller of <c>SendAsync</c>/<c>Send</c>; cancelling it
        /// aborts this request. Defaults to <see cref="System.Threading.CancellationToken.None"/>.
        /// </summary>
        public System.Threading.CancellationToken CallerToken { get; set; }

        /// <summary>
        /// Set once an auth response-interceptor has triggered a refresh+retry for this
        /// request, so a second 401 can't loop. Internal plumbing for
        /// <see cref="Molca.Networking.Auth.AuthTokenInterceptor"/>.
        /// </summary>
        public bool AuthRetryConsumed { get; set; }

        /// <summary>
        /// The auth token value actually injected into this request's prepared clone,
        /// recorded at prepare time. Lets the 401 handler distinguish "this request
        /// carried a token that has since been refreshed — just retry" from "the
        /// current token itself was rejected — refresh". Internal plumbing for
        /// <see cref="Molca.Networking.Auth.AuthTokenInterceptor"/>; <c>null</c> when
        /// no token was injected.
        /// </summary>
        public string AuthTokenSent { get; set; }

        public HttpRequestContext(HttpRequest request)
        {
            this.request = request;
            this.startTime = DateTime.Now;
        }

        /// <summary>
        /// The request URL with every query-parameter value masked
        /// (see <see cref="LogRedaction.RedactUrl"/>). Use this — never
        /// <c>request.FullUrl</c> — for any log line or diagnostics surface so
        /// credentials carried in the query string never reach a log or the Hub.
        /// </summary>
        public string RedactedUrl => LogRedaction.RedactUrl(request?.FullUrl);

        /// <summary>
        /// A log/diagnostics-safe one-line summary of the request: method, redacted
        /// URL, and headers with sensitive values masked. Safe to write to
        /// <c>Debug.Log</c> or store/display from request history.
        /// </summary>
        public string ToRedactedString()
        {
            if (request == null)
                return "(null request)";

            var builder = new StringBuilder();
            builder.Append(request.method).Append(' ').Append(RedactedUrl);
            if (request.headers != null && request.headers.Count > 0)
            {
                builder.Append(" {");
                bool first = true;
                foreach (var header in request.headers)
                {
                    if (header == null) continue;
                    if (!first) builder.Append(", ");
                    first = false;
                    builder.Append(header.key).Append(": ")
                        .Append(LogRedaction.RedactHeaderValue(header.key, header.value));
                }
                builder.Append('}');
            }
            return builder.ToString();
        }
        
        public void Complete(HttpResponse response)
        {
            if (IsCompleted)
            {
                Debug.LogWarning($"[HttpRequestContext] Attempted to complete already completed request: {request.name}");
                return;
            }
            
            this.response = response;
            this.endTime = DateTime.Now;
            
            if (response.isSuccess)
            {
                OnSuccess?.Invoke(response);
            }
            else
            {
                OnError?.Invoke(response.errorMessage);
                OnFailed?.Invoke(response.errorMessage);
            }
            
            OnCompleted?.Invoke(response);
        }
        
        public void Cancel(string reason = "User cancelled")
        {
            if (IsCompleted)
            {
                Debug.LogWarning($"[HttpRequestContext] Attempted to cancel already completed request: {request.name}");
                return;
            }
            
            this.wasCancelled = true;
            this.cancellationReason = reason;
            this.endTime = DateTime.Now;
            
            OnError?.Invoke(reason);
            OnFailed?.Invoke(reason);
        }
        
        public void UpdateProgress(float progress)
        {
            OnProgress?.Invoke(progress);
        }

        /// <summary>
        /// Builds a diagnostics-safe copy of this context for request history:
        /// sensitive header/query/form values are masked, credential-shaped JSON body
        /// fields are masked, binary payloads are dropped, and the response keeps its
        /// metadata but has its body redacted and heavy Unity object references
        /// (texture/audio/bundle) released. The live context (with the raw login body,
        /// bearer tokens, etc.) is never retained by history.
        /// </summary>
        /// <returns>An event-free snapshot safe to store and display indefinitely.</returns>
        public HttpRequestContext CreateRedactedSnapshot()
        {
            HttpRequest redactedRequest = null;
            if (request != null)
            {
                redactedRequest = request.Clone();

                foreach (var header in redactedRequest.headers)
                    header.value = LogRedaction.RedactHeaderValue(header.key, header.value);

                foreach (var param in redactedRequest.queryParams)
                {
                    if (LogRedaction.IsSensitiveField(param.key))
                        param.value = "***";
                }

                foreach (var field in redactedRequest.formFields)
                {
                    if (LogRedaction.IsSensitiveField(field.key))
                        field.value = "***";
                }

                // Binary payloads are dropped outright: they can be large and cannot
                // be meaningfully redacted.
                foreach (var field in redactedRequest.binaryFields)
                    field.data = Array.Empty<byte>();

                redactedRequest.jsonBody = LogRedaction.RedactJsonBody(redactedRequest.jsonBody);
            }

            var snapshot = new HttpRequestContext(redactedRequest)
            {
                startTime = startTime,
                endTime = endTime,
                wasCancelled = wasCancelled,
                cancellationReason = cancellationReason,
                response = CreateRedactedResponse()
            };
            return snapshot;
        }

        private HttpResponse CreateRedactedResponse()
        {
            if (response == null)
                return null;

            var redacted = response.Clone();
            // The body may carry credentials (a login response holds the tokens);
            // keep only the redacted text form and drop the raw bytes + Unity object
            // references so history can't pin bundles/textures or retain secrets.
            redacted.text = LogRedaction.RedactJsonBody(redacted.text);
            redacted.rawData = null;
            redacted.texture = null;
            redacted.audioClip = null;
            redacted.assetBundle = null;
            foreach (var key in new List<string>(redacted.headers.Keys))
                redacted.headers[key] = LogRedaction.RedactHeaderValue(key, redacted.headers[key]);
            return redacted;
        }
    }
} 