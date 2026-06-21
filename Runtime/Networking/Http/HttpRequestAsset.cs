using UnityEngine;
using Molca.Networking.Http.Models;
using System;
using System.Collections.Generic;

namespace Molca.Networking.Http
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "New HTTP Request", menuName = "Molca/Networking/HTTP Request", order = 20)]
    public class HttpRequestAsset : ScriptableObject
    {
        [Header("HTTP Request Configuration")]
        public HttpRequest request = new HttpRequest();

        // Instance API of the HTTP subsystem; the convenience senders below are
        // thin wrappers over it.
        private static IHttpClient Http =>
            HttpClient.Current ?? throw new InvalidOperationException("HttpClient is not initialized");


        /// <summary>
        /// Creates a mutable copy of this asset's request. This is the supported way
        /// to customize a request at runtime (URL segments, headers, body) — the asset
        /// itself is read-only configuration and must never be mutated in play mode.
        /// </summary>
        /// <returns>An independent <see cref="HttpRequest"/> clone safe to modify and send.</returns>
        public HttpRequest CreateRequest()
        {
            return request.Clone();
        }

        /// <summary>
        /// Sends this HTTP request asynchronously
        /// </summary>
        public async Awaitable<HttpResponse> SendAsync()
        {
            return await Http.SendAsync(request);
        }

        /// <summary>
        /// Sends this HTTP request asynchronously; cancelling the token aborts it.
        /// </summary>
        public async Awaitable<HttpResponse> SendAsync(System.Threading.CancellationToken cancellationToken)
        {
            return await Http.SendAsync(request, cancellationToken);
        }
        
        /// <summary>
        /// Sends this HTTP request with callbacks
        /// </summary>
        public void Send(Action<HttpResponse> onSuccess = null, Action<string> onError = null, Action<float> onProgress = null)
        {
            Http.Send(request, onSuccess: onSuccess, onError: onError, onProgress: onProgress);
        }
        
        /// <summary>
        /// Creates a builder for this request
        /// </summary>
        public HttpRequestBuilder CreateBuilder()
        {
            var builder = new HttpRequestBuilder()
                .Method(request.method)
                .Url(request.url)
                .ResponseType(request.expectedResponseType)
                .Timeout(request.timeout);

            foreach (var param in request.queryParams)
            {
                if (param.isEnabled)
                {
                    builder.Param(param.key, param.value);
                }
            }

            foreach (var header in request.headers)
            {
                if (header.isEnabled)
                {
                    builder.Header(header.key, header.value);
                }
            }

            return builder;
        }
        
        /// <summary>
        /// Validates the request configuration
        /// </summary>
        public bool Validate(out string[] errors)
        {
            bool isValid = request.Validate(out List<string> errorList);
            errors = errorList.ToArray();
            return isValid;
        }
        
        /// <summary>
        /// Gets the full URL for this request
        /// </summary>
        public string GetFullUrl()
        {
            return request.FullUrl;
        }
        
        /// <summary>
        /// Adds a header to this request
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void AddHeader(string key, string value)
        {
            request.AddHeader(key, value);
        }

        /// <summary>
        /// Adds a query parameter to this request
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void AddParam(string key, string value)
        {
            request.AddParam(key, value);
        }

        /// <summary>
        /// Sets the JSON body for this request
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void SetJsonBody(string json)
        {
            request.SetJsonBody(json);
        }

        /// <summary>
        /// Sets the JSON body for this request from an object
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void SetJsonBody<T>(T obj) where T : class
        {
            request.SetJsonBody(Newtonsoft.Json.JsonConvert.SerializeObject(obj));
        }

        /// <summary>
        /// Adds a form field to this request
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void AddFormField(string key, string value)
        {
            request.AddFormField(key, value);
        }

        /// <summary>
        /// Clears the body of this request
        /// </summary>
        [Obsolete("Mutates the ScriptableObject asset at runtime. Use CreateRequest() and modify the returned clone instead.")]
        public void ClearBody()
        {
            request.ClearBody();
        }
    }
} 