using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Networking.Utils;

namespace Molca.Networking.Auth
{
    /// <summary>
    /// Defines the contract for managing user authentication.
    /// </summary>
    public interface IAuthManager
    {
        /// <summary>
        /// Gets whether a user is currently authenticated.
        /// </summary>
        bool IsAuthenticated { get; }

        /// <summary>
        /// Gets whether there is a cached authentication token.
        /// </summary>
        bool HasCachedToken { get; }

        /// <summary>
        /// Gets the current authentication token.
        /// </summary>
        string AuthToken { get; }

        /// <summary>
        /// Attempts to validate a cached authentication token.
        /// </summary>
        /// <returns>True if token is valid, false otherwise.</returns>
        Awaitable<bool> TryValidateCachedToken();

        /// <summary>
        /// Attempts to authenticate a user with the provided credentials.
        /// </summary>
        /// <param name="username">The username for login.</param>
        /// <param name="password">The password for login.</param>
        /// <returns>True if login was successful, false otherwise.</returns>
        Awaitable<bool> LoginAsync(string username, string password);

        /// <summary>
        /// Attempts to authenticate a user with the provided credentials, honoring a
        /// cancellation token. Cancellation is non-error (rethrown as
        /// <see cref="System.OperationCanceledException"/>).
        /// </summary>
        Awaitable<bool> LoginAsync(string username, string password, CancellationToken cancellationToken);


        /// <summary>
        /// Logs out the current user.
        /// </summary>
        /// <returns>True if logout was successful, false otherwise.</returns>
        Awaitable<bool> LogoutAsync();

        /// <summary>Logs out the current user, honoring a cancellation token.</summary>
        Awaitable<bool> LogoutAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Renews the access token using the stored refresh token. Concurrent callers
        /// coalesce onto a single in-flight refresh. Returns <c>false</c> when no refresh
        /// token is available or the refresh endpoint rejects it.
        /// </summary>
        Awaitable<bool> RefreshAsync(CancellationToken cancellationToken = default);


        /// <summary>
        /// Attempts to apply the current authentication token to an HTTP request.
        /// </summary>
        /// <param name="request">The HTTP request to modify.</param>
        /// <returns>True if token was applied, false otherwise.</returns>
        [Obsolete("Token injection now happens automatically via AuthTokenInterceptor on requests that declare the token header. Do not mutate shared/asset-backed requests.")]
        bool TryApplyToken(HttpRequest request);
    }

    /// <summary>
    /// Manages user authentication and token handling.
    /// </summary>
    public class AuthManager : RuntimeSubsystem, IAuthManager
    {
        public static AuthManager Instance => RuntimeManager.GetSubsystem<AuthManager>();

        [SerializeField, FormerlySerializedAs("validateTokenRequest")] private HttpRequestAsset _validateTokenRequest;
        [SerializeField, FormerlySerializedAs("loginRequest")] private HttpRequestAsset _loginRequest;
        [SerializeField, FormerlySerializedAs("logoutRequest")] private HttpRequestAsset _logoutRequest;
        [Tooltip("Endpoint that exchanges a refresh token for a new access token. Must return the same payload shape as the login request.")]
        [SerializeField] private HttpRequestAsset _refreshRequest;
        [SerializeField, FormerlySerializedAs("user")] private AuthUser _user;

        public string authUserPath;
        public string authTokenKey;

        private const string USER_DATA_KEY = "USER_DATA";

        public string AuthToken => _user.Token;
        public AuthUser User => _user;
        public bool IsAuthenticated => _user.Data != null;
        public bool HasCachedToken => SecureStorage.HasKey(USER_DATA_KEY);

        /// <summary>
        /// Gets the user data cast to the specified type.
        /// </summary>
        /// <typeparam name="T">The type of user data to retrieve.</typeparam>
        /// <returns>The user data cast to type T.</returns>
        public T GetUserData<T>() where T : IAuthUserData
        {
            return (T)_user.Data;
        }

        // Timeout applied to per-call request clones; the SO assets stay untouched.
        private const int REQUEST_TIMEOUT_SECONDS = 30;

        private AuthTokenInterceptor _tokenInterceptor;

        // Serializes LoginAsync so concurrent callers can't both pass the
        // non-atomic IsAuthenticated check and clobber _user.Token/_user.Data.
        private readonly SemaphoreSlim _loginGate = new SemaphoreSlim(1, 1);

        // Coalesces concurrent RefreshAsync callers (e.g. N simultaneous 401s) onto a
        // single in-flight refresh: the gate guards inspection/creation of the shared
        // task, then every caller awaits that same task.
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private Task<bool> _refreshInFlight;

        // Instance API of the HTTP subsystem; resolved lazily because AuthManager
        // may initialize before HttpClient in bootstrap order.
        private static IHttpClient Http =>
            HttpClient.Current ?? throw new InvalidOperationException("HttpClient is not initialized");

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            // Auth header injection point: fills the declared token header on every
            // outgoing request clone while a user is authenticated. The interceptor also
            // handles 401 recovery (IHttpResponseInterceptor) via the refresh delegate
            // and raises AuthExpired when refresh is impossible.
            _tokenInterceptor = new AuthTokenInterceptor(
                authTokenKey,
                () => IsAuthenticated ? _user.Token : null,
                RefreshAsync,
                () => AuthEvents.Expired.Dispatch(new AuthExpiredEventData(_user.GetUserId())));
            HttpClient.AddInterceptorCore(_tokenInterceptor);

            MigrateLegacyPlaintextUserData();

            finishCallback?.Invoke(this);
        }

        public override void Teardown()
        {
            HttpClient.RemoveInterceptorCore(_tokenInterceptor);
            _tokenInterceptor = null;
            // The gates are deliberately NOT disposed: an in-flight login/refresh may
            // still hold one, and its finally-block Release() would then throw
            // ObjectDisposedException. Those flows drain naturally — their HTTP calls
            // are linked to ShutdownToken (cancelled by base.Teardown) — and an
            // undisposed SemaphoreSlim without a wait handle has no unmanaged state.
            base.Teardown();
        }

        public Awaitable<bool> TryValidateCachedToken() => TryValidateCachedToken(default);

        /// <summary>
        /// Attempts to validate a cached authentication token, honoring a cancellation
        /// token. Cancellation is non-error: it rethrows
        /// <see cref="OperationCanceledException"/> and leaves the cached user intact
        /// (a cancelled probe proves nothing about token validity).
        /// </summary>
        /// <param name="cancellationToken">Aborts the validation request.</param>
        public async Awaitable<bool> TryValidateCachedToken(CancellationToken cancellationToken)
        {
            if (IsAuthenticated)
            {
                Debug.Log("User already authenticated.");
                return true;
            }

            if (!SecureStorage.TryLoadString(USER_DATA_KEY, out string cachedData) || string.IsNullOrEmpty(cachedData) || !_user.DeserializeFromJson(cachedData))
                return false;

            if (_validateTokenRequest == null)
            {
                Debug.LogError("Validate token request is not configured");
                return false;
            }

            // Configure a per-call clone — never the ScriptableObject asset itself.
            // The token travels only in the header — never in the URL path, where it
            // would leak into server access logs, proxies, and browser history. The
            // validate endpoint (asset-authored URL) must accept it via this header.
            var request = _validateTokenRequest.CreateRequest();
            request.AddHeader("Authorization-Token", _user.Token);
            request.timeout = REQUEST_TIMEOUT_SECONDS;

            try
            {
                var response = await Http.SendAsync(request, cancellationToken);
                bool valid = response.isSuccess && response.statusCode == 200;
                if (!valid)
                    ClearUser();
                return valid;
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is not a verdict on the token — keep the cached user
            }
            catch (Exception e)
            {
                Debug.LogError($"Token validation failed: {e.Message}");
                ClearUser();
                return false;
            }
        }

        public Awaitable<bool> LoginAsync(string username, string password)
            => LoginAsync(username, password, default);

        public async Awaitable<bool> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            // Single-flight: serialize the IsAuthenticated check + token write so two
            // concurrent logins can't both proceed and clobber _user.Token/_user.Data.
            await _loginGate.WaitAsync(cancellationToken);
            try
            {
                if (IsAuthenticated)
                {
                    Debug.LogWarning("A user is already authenticated. Please logout before logging in to another account.");
                    return true;
                }

                if (_loginRequest == null)
                {
                    Debug.LogError("Login request is not configured");
                    return false;
                }

                try
                {
                    // Configure a per-call clone — never the ScriptableObject asset itself.
                    var request = _loginRequest.CreateRequest();
                    request.SetJsonBody(_user.GetLoginJson(username, password));
                    request.timeout = REQUEST_TIMEOUT_SECONDS;

                    var response = await Http.SendAsync(request, cancellationToken);

                    if (response.isSuccess)
                    {
                        string responseText = response.GetContentAsString();
                        bool success = _user.DeserializeFromJson(responseText);
                        if (success)
                        {
                            AuthEvents.LoggedIn.Dispatch(new AuthLoggedInEventData(_user.GetUserId()));
                            // Encrypted at rest — never persist credentials as plaintext.
                            SecureStorage.SaveString(USER_DATA_KEY, responseText);
                            return true;
                        }
                    }

                    Debug.LogError($"Login failed: {response.statusCode} {response.statusMessage}");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is not an error — propagate it quietly.
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Login failed: {e.Message}");
                    return false;
                }
            }
            finally
            {
                _loginGate.Release();
            }
        }

        public Awaitable<bool> LogoutAsync() => LogoutAsync(default);

        public async Awaitable<bool> LogoutAsync(CancellationToken cancellationToken)
        {
            if (!IsAuthenticated || _user.IsGuest)
            {
                AuthEvents.LoggedOut.Dispatch(new AuthLoggedOutEventData(_user.GetUserId()));
                ClearUser();
                return true;
            }

            if (_logoutRequest == null)
            {
                Debug.LogError("Logout request is not configured");
                AuthEvents.LoggedOut.Dispatch(new AuthLoggedOutEventData(_user.GetUserId()));
                ClearUser();
                return true;
            }

            try
            {
                // Configure a per-call clone — never the ScriptableObject asset itself.
                var request = _logoutRequest.CreateRequest();
                request.url = $"{authUserPath}/{_user.GetUserId()}";
                request.AddHeader(authTokenKey, _user.Token);
                request.timeout = REQUEST_TIMEOUT_SECONDS;

                var response = await Http.SendAsync(request, cancellationToken);

                // Always dispatch logout event and clear user, regardless of server response
                AuthEvents.LoggedOut.Dispatch(new AuthLoggedOutEventData(_user.GetUserId()));
                ClearUser();
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is not an error; leave session state untouched
            }
            catch (Exception e)
            {
                Debug.LogError($"Logout failed: {e.Message}");
                // Still dispatch logout event and clear user even if request fails
                AuthEvents.LoggedOut.Dispatch(new AuthLoggedOutEventData(_user.GetUserId()));
                ClearUser();
                return true;
            }
        }

        /// <summary>
        /// Renews the access token using the stored refresh token. Concurrent callers
        /// (e.g. several requests that all hit 401 at once) coalesce onto a single
        /// in-flight refresh and all observe its result. Returns <c>false</c> when no
        /// refresh token is available or the refresh endpoint rejects it; the caller's
        /// cancellation token can abandon the await without cancelling the shared refresh.
        /// </summary>
        public async Awaitable<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<bool> inFlight;
            // Brief gate: only guards inspection/creation of the shared task.
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                if (_refreshInFlight == null || _refreshInFlight.IsCompleted)
                    _refreshInFlight = RefreshCoreAsync();
                inFlight = _refreshInFlight;
            }
            finally
            {
                _refreshGate.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool ok = await inFlight;
            cancellationToken.ThrowIfCancellationRequested();
            return ok;
        }

        /// <summary>
        /// The actual refresh exchange, run once and shared by all coalesced callers.
        /// Keyed on <see cref="RuntimeSubsystem.ShutdownToken"/> rather than any single
        /// caller's token so one caller cancelling can't abort the shared refresh.
        /// </summary>
        private async Task<bool> RefreshCoreAsync()
        {
            if (_refreshRequest == null)
            {
                Debug.LogError("Refresh request is not configured");
                return false;
            }

            string refreshToken = _user.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                Debug.LogWarning("[AuthManager] No refresh token available; cannot refresh.");
                return false;
            }

            try
            {
                // Configure a per-call clone — never the ScriptableObject asset itself.
                // The refresh token travels only in the header, never the URL.
                var request = _refreshRequest.CreateRequest();
                request.AddHeader("Authorization-Token", refreshToken);
                request.timeout = REQUEST_TIMEOUT_SECONDS;

                var response = await Http.SendAsync(request, ShutdownToken);
                if (response.isSuccess)
                {
                    string text = response.GetContentAsString();
                    if (_user.DeserializeFromJson(text))
                    {
                        // Re-persist the refreshed payload (encrypted at rest).
                        SecureStorage.SaveString(USER_DATA_KEY, text);
                        return true;
                    }
                }

                Debug.LogError($"Token refresh failed: {response.statusCode} {response.statusMessage}");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogError($"Token refresh failed: {e.Message}");
                return false;
            }
        }

        [Obsolete("Token injection now happens automatically via AuthTokenInterceptor on requests that declare the token header. Do not mutate shared/asset-backed requests.")]
        public bool TryApplyToken(HttpRequest request)
        {
            if (request == null) return false;
            
            var tokenHeader = request.headers.FirstOrDefault(h => h.key.Equals(authTokenKey, StringComparison.OrdinalIgnoreCase));
            if (tokenHeader != null)
            {
                tokenHeader.value = _user.Token;
                return true;
            }
            return false;
        }

        public void GuestLogin(IAuthUserData data)
        {
            _user.SetData(data);
            AuthEvents.LoggedIn.Dispatch(new AuthLoggedInEventData(_user.GetUserId()));
        }

        /// <summary>
        /// Pre-Sprint-4 installs persisted the auth payload as plaintext JSON under the
        /// same key. Re-encrypt it in place so existing sessions survive the upgrade.
        /// </summary>
        private void MigrateLegacyPlaintextUserData()
        {
            if (!PlayerPrefs.HasKey(USER_DATA_KEY))
                return;

            string raw = PlayerPrefs.GetString(USER_DATA_KEY, "");
            // Encrypted entries are base64; legacy plaintext is a JSON object.
            if (string.IsNullOrEmpty(raw) || !raw.TrimStart().StartsWith("{"))
                return;

            SecureStorage.SaveString(USER_DATA_KEY, raw);
            Debug.Log("[AuthManager] Migrated legacy plaintext auth data to encrypted storage.");
        }

        private void ClearUser()
        {
            _user.Clear();
            SecureStorage.Delete(USER_DATA_KEY);
        }
    }
}
