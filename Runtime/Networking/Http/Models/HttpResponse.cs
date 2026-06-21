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

            if (statusCode is >= 400 and < 500)
                return HttpErrorKind.Http4xx;
            if (statusCode is >= 500 and < 600)
                return HttpErrorKind.Http5xx;

            // No HTTP status (connection/timeout/cancel). UnityWebRequest surfaces
            // timeouts as a connection error string, not a typed exception, so the
            // text is the only signal available to distinguish a timeout from a
            // generic network failure.
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
    }
} 