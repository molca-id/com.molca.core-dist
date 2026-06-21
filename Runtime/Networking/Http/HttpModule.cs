using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Molca.Settings;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// HTTP client configuration. SerializeFields are authored defaults; mutable
    /// runtime values (and persisted default headers) live on the paired
    /// <see cref="HttpState"/>.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "HTTP Settings", menuName = "Molca/Settings/HTTP", order = 10)]
    public class HttpModule : SettingModule
    {
        [Header("HTTP Configuration")]
        [SerializeField, FormerlySerializedAs("baseUrl")] private string _baseUrl = "";
        [SerializeField, FormerlySerializedAs("maxConcurrentRequests")] private int _maxConcurrentRequests = 4;
        [SerializeField, FormerlySerializedAs("defaultTimeout")] private int _defaultTimeout = 30;
        [SerializeField, FormerlySerializedAs("enableRequestHistory")] private bool _enableRequestHistory = true;
        [SerializeField, FormerlySerializedAs("maxHistorySize")] private int _maxHistorySize = 100;

        [Header("Default Headers")]
        [SerializeField, FormerlySerializedAs("defaultHeaderKeys")] private string[] _defaultHeaderKeys = Array.Empty<string>();
        [SerializeField, FormerlySerializedAs("defaultHeaderValues")] private string[] _defaultHeaderValues = Array.Empty<string>();

        [Header("Retry")]
        [Tooltip("Retry idempotent requests (GET/HEAD/OPTIONS/PUT/DELETE) on transient failures.")]
        [SerializeField, FormerlySerializedAs("enableRetry")] private bool _enableRetry = false;
        [SerializeField, FormerlySerializedAs("maxRetries")] private int _maxRetries = 2;
        [Tooltip("First retry delay in seconds; doubles per attempt (exponential backoff).")]
        [SerializeField, FormerlySerializedAs("retryBaseDelaySeconds")] private float _retryBaseDelaySeconds = 0.5f;

        [Header("Advanced Settings")]
        [SerializeField, FormerlySerializedAs("followRedirects")] private bool _followRedirects = true;
        [SerializeField, FormerlySerializedAs("validateSSL")] private bool _validateSSL = true;
        [SerializeField, FormerlySerializedAs("enableLogging")] private bool _enableLogging = true;

        // Internal accessors so HttpState can copy authored defaults at construction time.
        internal string DefaultBaseUrl => _baseUrl;
        internal int DefaultMaxConcurrentRequests => _maxConcurrentRequests;
        internal int DefaultDefaultTimeout => _defaultTimeout;
        internal bool DefaultEnableRequestHistory => _enableRequestHistory;
        internal int DefaultMaxHistorySize => _maxHistorySize;
        internal string[] DefaultHeaderKeysSeed => _defaultHeaderKeys;
        internal string[] DefaultHeaderValuesSeed => _defaultHeaderValues;
        internal bool DefaultEnableRetry => _enableRetry;
        internal int DefaultMaxRetries => _maxRetries;
        internal float DefaultRetryBaseDelaySeconds => _retryBaseDelaySeconds;
        internal bool DefaultFollowRedirects => _followRedirects;
        internal bool DefaultValidateSSL => _validateSSL;
        internal bool DefaultEnableLogging => _enableLogging;

        private HttpState TypedState => (HttpState)State;

        // When State is null (e.g., the module is being read from an editor tool before
        // RuntimeManager has booted), getters fall back to the authored default; setters
        // log and no-op rather than silently writing to the SerializeField (which would
        // re-introduce the runtime-mutates-SO violation we're fixing).
        private bool TryRequireState(string action)
        {
            if (State != null) return true;
            Debug.LogError($"[HttpModule] Cannot {action} before GlobalSettings has initialized. Wait for RuntimeManager.WaitForInitialization().", this);
            return false;
        }

        public string BaseUrl
        {
            get => State != null ? TypedState.BaseUrl : _baseUrl;
            set { if (!TryRequireState("set BaseUrl")) return; TypedState.BaseUrl = value; SaveSettings(); }
        }

        public int MaxConcurrentRequests
        {
            get => State != null ? TypedState.MaxConcurrentRequests : _maxConcurrentRequests;
            set { if (!TryRequireState("set MaxConcurrentRequests")) return; TypedState.MaxConcurrentRequests = Mathf.Clamp(value, 1, 20); SaveSettings(); }
        }

        public int DefaultTimeout
        {
            get => State != null ? TypedState.DefaultTimeout : _defaultTimeout;
            set { if (!TryRequireState("set DefaultTimeout")) return; TypedState.DefaultTimeout = Mathf.Clamp(value, 1, 300); SaveSettings(); }
        }

        public bool EnableRequestHistory
        {
            get => State != null ? TypedState.EnableRequestHistory : _enableRequestHistory;
            set { if (!TryRequireState("set EnableRequestHistory")) return; TypedState.EnableRequestHistory = value; SaveSettings(); }
        }

        public int MaxHistorySize
        {
            get => State != null ? TypedState.MaxHistorySize : _maxHistorySize;
            set { if (!TryRequireState("set MaxHistorySize")) return; TypedState.MaxHistorySize = Mathf.Clamp(value, 10, 1000); SaveSettings(); }
        }

        /// <summary>Whether idempotent requests are retried on transient failures.</summary>
        public bool EnableRetry
        {
            get => State != null ? TypedState.EnableRetry : _enableRetry;
            set { if (!TryRequireState("set EnableRetry")) return; TypedState.EnableRetry = value; SaveSettings(); }
        }

        /// <summary>Maximum retry attempts after the initial send.</summary>
        public int MaxRetries
        {
            get => State != null ? TypedState.MaxRetries : _maxRetries;
            set { if (!TryRequireState("set MaxRetries")) return; TypedState.MaxRetries = Mathf.Clamp(value, 0, 10); SaveSettings(); }
        }

        /// <summary>First retry delay in seconds; doubled for each subsequent attempt.</summary>
        public float RetryBaseDelaySeconds
        {
            get => State != null ? TypedState.RetryBaseDelaySeconds : _retryBaseDelaySeconds;
            set { if (!TryRequireState("set RetryBaseDelaySeconds")) return; TypedState.RetryBaseDelaySeconds = Mathf.Max(0f, value); SaveSettings(); }
        }

        public bool FollowRedirects
        {
            get => State != null ? TypedState.FollowRedirects : _followRedirects;
            set { if (!TryRequireState("set FollowRedirects")) return; TypedState.FollowRedirects = value; SaveSettings(); }
        }

        public bool ValidateSSL
        {
            get => State != null ? TypedState.ValidateSSL : _validateSSL;
            set { if (!TryRequireState("set ValidateSSL")) return; TypedState.ValidateSSL = value; SaveSettings(); }
        }

        public bool EnableLogging
        {
            get => State != null ? TypedState.EnableLogging : _enableLogging;
            set { if (!TryRequireState("set EnableLogging")) return; TypedState.EnableLogging = value; SaveSettings(); }
        }

        /// <summary>Gets a default header value by key, or <c>null</c> if absent.</summary>
        public string GetDefaultHeader(string key)
        {
            if (State != null) return TypedState.GetHeader(key);
            // Edit-time fallback: read from authored defaults arrays.
            if (_defaultHeaderKeys == null || _defaultHeaderValues == null) return null;
            int n = Mathf.Min(_defaultHeaderKeys.Length, _defaultHeaderValues.Length);
            for (int i = 0; i < n; i++) if (_defaultHeaderKeys[i] == key) return _defaultHeaderValues[i];
            return null;
        }

        /// <summary>Adds or replaces a default header. No-op for null/empty key.</summary>
        public void SetDefaultHeader(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!TryRequireState("set header")) return;
            TypedState.SetHeader(key, value);
            SaveSettings();
        }

        /// <summary>Removes a default header by key. No-op if absent.</summary>
        public void RemoveDefaultHeader(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!TryRequireState("remove header")) return;
            if (TypedState.RemoveHeader(key)) SaveSettings();
        }

        /// <summary>Clears all default headers.</summary>
        public void ClearDefaultHeaders()
        {
            if (!TryRequireState("clear headers")) return;
            TypedState.ClearHeaders();
            SaveSettings();
        }

        /// <summary>Returns a snapshot of all default headers as a dictionary.</summary>
        public Dictionary<string, string> GetDefaultHeaders()
        {
            if (State != null) return TypedState.SnapshotHeaders();
            // Edit-time fallback.
            var dict = new Dictionary<string, string>();
            if (_defaultHeaderKeys != null && _defaultHeaderValues != null)
            {
                int n = Mathf.Min(_defaultHeaderKeys.Length, _defaultHeaderValues.Length);
                for (int i = 0; i < n; i++)
                {
                    if (!string.IsNullOrEmpty(_defaultHeaderKeys[i]))
                        dict[_defaultHeaderKeys[i]] = _defaultHeaderValues[i];
                }
            }
            return dict;
        }

        public override SettingState CreateState() => new HttpState(this);

        public override void SaveSettings() => TypedState.Save(this);

        public override void LoadSettings() => TypedState.Load(this);
    }

    /// <summary>
    /// Retry configuration applied by <c>HttpClient</c> to idempotent requests that
    /// fail transiently. Normally sourced from <see cref="HttpModule"/>; tests or
    /// pre-settings code can override via <c>HttpClient.SetRetryPolicy</c>.
    /// </summary>
    public class HttpRetryPolicy
    {
        /// <summary>Whether retries are performed at all.</summary>
        public bool Enabled;

        /// <summary>Maximum retry attempts after the initial send.</summary>
        public int MaxRetries;

        /// <summary>First retry delay in seconds; doubled per attempt.</summary>
        public float BaseDelaySeconds;

        /// <summary>Builds a policy from the module's current values; disabled when the module is absent.</summary>
        public static HttpRetryPolicy FromModule(HttpModule module) => module == null
            ? new HttpRetryPolicy()
            : new HttpRetryPolicy
            {
                Enabled = module.EnableRetry,
                MaxRetries = module.MaxRetries,
                BaseDelaySeconds = module.RetryBaseDelaySeconds
            };

        /// <summary>
        /// Whether a failed response should be retried, decided from its
        /// <see cref="HttpErrorKind"/> rather than a hard-coded status list:
        /// transient transport failures (<see cref="HttpErrorKind.Network"/>,
        /// <see cref="HttpErrorKind.Timeout"/>) and <see cref="HttpErrorKind.Http5xx"/>
        /// are retryable, as are the transient 4xx codes 408 (timeout) and 429
        /// (rate-limit); all other 4xx, auth, serialization, cancel, and success are not.
        /// </summary>
        public bool IsRetryable(HttpError error)
        {
            switch (error.Kind)
            {
                case HttpErrorKind.Network:
                case HttpErrorKind.Timeout:
                case HttpErrorKind.Http5xx:
                    return true;
                case HttpErrorKind.Http4xx:
                    return error.StatusCode == 408 || error.StatusCode == 429;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Full-jitter exponential backoff: a random point in <c>[0, base · 2^(attempt-1)]</c>.
        /// Spreading the delay across the whole window prevents many clients that failed
        /// together from retrying in a synchronized burst ("thundering herd").
        /// </summary>
        /// <param name="attempt">1-based attempt number that just failed.</param>
        /// <param name="rng">Randomness source; a <c>null</c> source yields the full (un-jittered) cap.</param>
        public float ComputeBackoffDelay(int attempt, System.Random rng)
        {
            float cap = BaseDelaySeconds * (1 << (attempt - 1));
            if (cap <= 0f)
                return 0f;
            double fraction = rng?.NextDouble() ?? 1.0;
            return (float)(fraction * cap);
        }
    }

    /// <summary>Mutable runtime state for <see cref="HttpModule"/>. Owned by <see cref="GlobalSettings"/>.</summary>
    public class HttpState : SettingState
    {
        // PlayerPrefs key names — preserved verbatim from the legacy implementation
        // for backward compatibility with existing user installs.
        private const string HeaderCountKey = "defaultHeaderCount";
        private const string HeaderKeyPrefix = "defaultHeaderKey_";
        private const string HeaderValuePrefix = "defaultHeaderValue_";

        public string BaseUrl;
        public int MaxConcurrentRequests;
        public int DefaultTimeout;
        public bool EnableRequestHistory;
        public int MaxHistorySize;
        public bool EnableRetry;
        public int MaxRetries;
        public float RetryBaseDelaySeconds;
        public bool FollowRedirects;
        public bool ValidateSSL;
        public bool EnableLogging;

        private readonly List<string> _headerKeys = new List<string>();
        private readonly List<string> _headerValues = new List<string>();

        /// <summary>Constructs state seeded from the module's authored SerializeField defaults.</summary>
        public HttpState(HttpModule module)
        {
            BaseUrl = module.DefaultBaseUrl;
            MaxConcurrentRequests = module.DefaultMaxConcurrentRequests;
            DefaultTimeout = module.DefaultDefaultTimeout;
            EnableRequestHistory = module.DefaultEnableRequestHistory;
            MaxHistorySize = module.DefaultMaxHistorySize;
            EnableRetry = module.DefaultEnableRetry;
            MaxRetries = module.DefaultMaxRetries;
            RetryBaseDelaySeconds = module.DefaultRetryBaseDelaySeconds;
            FollowRedirects = module.DefaultFollowRedirects;
            ValidateSSL = module.DefaultValidateSSL;
            EnableLogging = module.DefaultEnableLogging;

            var seedKeys = module.DefaultHeaderKeysSeed;
            var seedValues = module.DefaultHeaderValuesSeed;
            if (seedKeys != null && seedValues != null)
            {
                int n = Mathf.Min(seedKeys.Length, seedValues.Length);
                for (int i = 0; i < n; i++)
                {
                    if (string.IsNullOrEmpty(seedKeys[i])) continue;
                    _headerKeys.Add(seedKeys[i]);
                    _headerValues.Add(seedValues[i]);
                }
            }
        }

        public string GetHeader(string key)
        {
            int idx = _headerKeys.IndexOf(key);
            return idx < 0 ? null : _headerValues[idx];
        }

        public void SetHeader(string key, string value)
        {
            int idx = _headerKeys.IndexOf(key);
            if (idx >= 0)
            {
                _headerValues[idx] = value;
            }
            else
            {
                _headerKeys.Add(key);
                _headerValues.Add(value);
            }
        }

        public bool RemoveHeader(string key)
        {
            int idx = _headerKeys.IndexOf(key);
            if (idx < 0) return false;
            _headerKeys.RemoveAt(idx);
            _headerValues.RemoveAt(idx);
            return true;
        }

        public void ClearHeaders()
        {
            _headerKeys.Clear();
            _headerValues.Clear();
        }

        public Dictionary<string, string> SnapshotHeaders()
        {
            var dict = new Dictionary<string, string>(_headerKeys.Count);
            for (int i = 0; i < _headerKeys.Count; i++)
            {
                if (!string.IsNullOrEmpty(_headerKeys[i]))
                    dict[_headerKeys[i]] = _headerValues[i];
            }
            return dict;
        }

        public override void Load(SettingModule owner)
        {
            int count = owner.LoadInt(HeaderCountKey, -1);
            if (count < 0)
            {
                // Nothing persisted yet — keep authored defaults seeded by the constructor.
                return;
            }

            _headerKeys.Clear();
            _headerValues.Clear();
            for (int i = 0; i < count; i++)
            {
                _headerKeys.Add(owner.LoadString(HeaderKeyPrefix + i));
                _headerValues.Add(owner.LoadString(HeaderValuePrefix + i));
            }
        }

        public override void Save(SettingModule owner)
        {
            owner.SaveInt(HeaderCountKey, _headerKeys.Count);
            for (int i = 0; i < _headerKeys.Count; i++)
            {
                owner.SaveString(HeaderKeyPrefix + i, _headerKeys[i]);
                owner.SaveString(HeaderValuePrefix + i, _headerValues[i]);
            }
        }
    }
}
