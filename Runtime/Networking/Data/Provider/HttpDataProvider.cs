using Molca.Networking.Http;
using Molca.Networking.Auth;
using Molca.Attributes;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "HttpDataProvider", menuName = "Molca/Networking/HttpDataProvider", order = 20)]
    public class HttpDataProvider : DataProvider
    {
        [Header("Http Settings")]
        [SerializeField, FormerlySerializedAs("request")] private HttpRequestAsset _request;
        [SerializeField, FormerlySerializedAs("autoFetchInterval")] private float _autoFetchInterval = 10f;
        [SerializeField, FormerlySerializedAs("requireAuthentication")] private bool _requireAuthentication = false;
        [SerializeField, FormerlySerializedAs("requestValidationErrors"), ReadOnly] private string[] _requestValidationErrors;

        private bool _isAuthenticated;
        private bool isRequestValid => _request.Validate(out _requestValidationErrors) || _isAuthenticated || !_requireAuthentication;

        public override void Activate()
        {
            if(!_request.Validate(out _requestValidationErrors))
            {
                Debug.LogError($"Request validation failed for {name}: {string.Join(", ", _requestValidationErrors)}");
                return;
            }

            base.Activate();

            if (_requireAuthentication)
            {
                AuthEvents.StateChanged.Register(OnAuthStateChanged);
            }
            else if (_autoFetchInterval > 0)
            {
                AutoFetch();
            }
            else
            {
                FetchData();
            }
        }

        private void OnAuthStateChanged(AuthChangedEventData data)
        {
            // Token injection happens per-send via AuthTokenInterceptor (requests
            // declaring the token header get it filled); no SO mutation here.
            _isAuthenticated = data.IsAuthenticated;
            if(_isAuthenticated && _autoFetchInterval > 0)
            {
                AutoFetch();
            }
        }

        public override void FetchData()
        {
            // Explicit fire-and-forget: the async method owns its exceptions.
            _ = FetchDataAsync();
        }

        private async Awaitable FetchDataAsync()
        {
            if (!IsActive || !isRequestValid) return;
            try
            {
                var response = await _request.SendAsync();
                if (LifetimeToken.IsCancellationRequested) return;
                OnDataFetched(response.text);
            }
            catch (System.OperationCanceledException)
            {
                // Provider deactivated mid-request — exit quietly.
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HttpDataProvider] {name}: Fetch failed: {e}");
            }
        }

        private void AutoFetch()
        {
            if (_autoFetchInterval <= 0) return;
            _ = AutoFetchLoopAsync();
        }

        private async Awaitable AutoFetchLoopAsync()
        {
            try
            {
                var token = LifetimeToken;
                while (IsActive && !token.IsCancellationRequested)
                {
                    await Awaitable.WaitForSecondsAsync(_autoFetchInterval, token);
                    await FetchDataAsync();
                }
            }
            catch (System.OperationCanceledException)
            {
                // Provider deactivated — loop unwinds.
            }
        }

        private void OnValidate()
        {
            if(_request != null)
            {
                _request.Validate(out _requestValidationErrors);
            }
        }
    }
}
