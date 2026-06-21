using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Molca.Settings;

namespace Molca.Networking.Http.Models
{
    [Serializable]
    public class HttpRequest
    {
        [Header("Request Configuration")]
        public string name = "New Request";
        public HttpMethod method = HttpMethod.GET;
        public string url = "";
        public bool useFullUrl = false;
        
        [Header("Headers")]
        public List<HttpHeader> headers = new List<HttpHeader>();
        
        [Header("Query Parameters")]
        public List<HttpParam> queryParams = new List<HttpParam>();
        
        public BodyType bodyType = BodyType.None;
        public string jsonBody = "";
        public List<FormField> formFields = new List<FormField>();
        public List<BinaryField> binaryFields = new List<BinaryField>();
        
        public int timeout = 30;
        public bool followRedirects = true;
        public bool validateSSL = true;
        
        [Header("Response")]
        public ResponseType expectedResponseType = ResponseType.Text;
        
        // Computed properties
        public string FullUrl
        {
            get
            {
                string baseUrl = useFullUrl ? url : $"{GetBaseUrl()?.TrimEnd('/')}/{url?.TrimStart('/')}";
                
                if (queryParams == null || !queryParams.Any(p => p.isEnabled))
                    return baseUrl;

                var enabledParams = queryParams.Where(p => p.isEnabled).ToList();
                var queryString = string.Join("&", enabledParams.Select(p => 
                    $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value)}"));

                char separator = baseUrl.Contains("?") ? '&' : '?';
                return $"{baseUrl}{separator}{queryString}";
            }
        }
        
        private string GetBaseUrl()
        {
            // Try to get from HttpModule first, then fallback to HttpClient
            var httpModule = GlobalSettings.GetModule<HttpModule>();
            return httpModule?.BaseUrl ?? HttpClient.Current?.BaseUrl ?? "";
        }
        public bool HasBody => bodyType != BodyType.None && 
            (bodyType == BodyType.Json ? !string.IsNullOrEmpty(jsonBody) : 
             bodyType == BodyType.Form ? formFields.Any() : 
             bodyType == BodyType.Binary ? binaryFields.Any() : false);
        
        public HttpRequest()
        {
            // Add common headers by default
            AddDefaultHeaders();
        }
        
        private void AddDefaultHeaders()
        {
            headers.Add(new HttpHeader("Accept", "*/*"));
            headers.Add(new HttpHeader("User-Agent", "Unity/1.0"));
        }
        
        public void AddHeader(string key, string value)
        {
            var existing = headers.FirstOrDefault(h => h.key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.value = value;
            }
            else
            {
                headers.Add(new HttpHeader(key, value));
            }
        }
        
        public void RemoveHeader(string key)
        {
            headers.RemoveAll(h => h.key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        
        public string GetHeaderValue(string key)
        {
            return headers.FirstOrDefault(h => h.key.Equals(key, StringComparison.OrdinalIgnoreCase))?.value;
        }
        
        public void AddParam(string key, string value)
        {
            var existing = queryParams.FirstOrDefault(p => p.key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.value = value;
            }
            else
            {
                queryParams.Add(new HttpParam(key, value));
            }
        }
        
        public void RemoveParam(string key)
        {
            queryParams.RemoveAll(p => p.key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        
        public string GetParamValue(string key)
        {
            return queryParams.FirstOrDefault(p => p.key.Equals(key, StringComparison.OrdinalIgnoreCase))?.value;
        }
        
        public void AddFormField(string key, string value)
        {
            formFields.Add(new FormField(key, value));
        }
        
        public void AddBinaryField(string key, byte[] data, string filename = "")
        {
            binaryFields.Add(new BinaryField(key, data, filename));
        }
        
        public void SetJsonBody(string json)
        {
            bodyType = BodyType.Json;
            jsonBody = json;
        }
        
        public void ClearBody()
        {
            bodyType = BodyType.None;
            jsonBody = "";
            formFields.Clear();
            binaryFields.Clear();
        }
        
        public HttpRequest Clone()
        {
            var clone = new HttpRequest
            {
                name = this.name,
                method = this.method,
                url = this.url,
                useFullUrl = this.useFullUrl,
                bodyType = this.bodyType,
                jsonBody = this.jsonBody,
                timeout = this.timeout,
                followRedirects = this.followRedirects,
                validateSSL = this.validateSSL,
                expectedResponseType = this.expectedResponseType
            };
            
            clone.headers.AddRange(this.headers.Select(h => h.Clone()));
            clone.queryParams.AddRange(this.queryParams.Select(p => p.Clone()));
            clone.formFields.AddRange(this.formFields.Select(f => f.Clone()));
            clone.binaryFields.AddRange(this.binaryFields.Select(b => b.Clone()));
            
            return clone;
        }
        
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();
            
            if (string.IsNullOrWhiteSpace(name))
                errors.Add("Request name is required");
                
            if (string.IsNullOrWhiteSpace(url))
                errors.Add("URL is required");
            else if (!useFullUrl && !url.StartsWith("http") && !url.StartsWith("/"))
                errors.Add("URL must start with 'http' or '/'");
                
            if (timeout <= 0)
                errors.Add("Timeout must be greater than 0");
                
            if (bodyType == BodyType.Json && !string.IsNullOrEmpty(jsonBody))
            {
                try
                {
                    Newtonsoft.Json.JsonConvert.DeserializeObject(jsonBody);
                }
                catch
                {
                    errors.Add("Invalid JSON format");
                }
            }
            
            return errors.Count == 0;
        }
    }
    
    [Serializable]
    public class HttpHeader
    {
        public string key = "";
        public string value = "";
        public bool isEnabled = true;
        
        public HttpHeader() { }
        
        public HttpHeader(string key, string value)
        {
            this.key = key;
            this.value = value;
        }
        
        public HttpHeader Clone() => new HttpHeader(key, value) { isEnabled = isEnabled };
    }
    
    [Serializable]
    public class HttpParam
    {
        public string key = "";
        public string value = "";
        public bool isEnabled = true;
        
        public HttpParam() { }
        
        public HttpParam(string key, string value)
        {
            this.key = key;
            this.value = value;
        }
        
        public HttpParam Clone() => new HttpParam(key, value) { isEnabled = isEnabled };
    }
    
    [Serializable]
    public class FormField
    {
        public string key = "";
        public string value = "";
        public bool isEnabled = true;
        
        public FormField() { }
        
        public FormField(string key, string value)
        {
            this.key = key;
            this.value = value;
        }
        
        public FormField Clone() => new FormField(key, value) { isEnabled = isEnabled };
    }
    
    [Serializable]
    public class BinaryField
    {
        public string key = "";
        public byte[] data;
        public string filename = "";
        public bool isEnabled = true;
        
        public BinaryField() { }
        
        public BinaryField(string key, byte[] data, string filename = "")
        {
            this.key = key;
            this.data = data;
            this.filename = filename;
        }
        
        public BinaryField Clone() => new BinaryField(key, data, filename) { isEnabled = isEnabled };
    }
    
    public enum HttpMethod
    {
        GET,
        POST,
        PUT,
        DELETE,
        PATCH,
        HEAD,
        OPTIONS
    }
    
    public enum BodyType
    {
        None,
        Json,
        Form,
        Binary
    }
    
    public enum ResponseType
    {
        Text,
        Json,
        Binary,
        Texture,
        AudioClip,
        AssetBundle
    }
} 