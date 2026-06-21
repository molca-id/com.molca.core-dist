using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// Drives an OAuth 2.0 device-authorization-grant flow (RFC 8628) — the shippable choice for a
    /// provider that supports neither PKCE nor a public-client loopback (GitHub OAuth Apps).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Registration: instantiated by an <see cref="OAuthIntegrationProvider"/> (e.g. GitHub); not an asset.
    /// <para>
    /// Flow: request a device + user code with only the public <c>client_id</c> (no secret) → surface the
    /// <c>user_code</c> and <c>verification_uri</c> to the UI → poll the token endpoint honoring the
    /// server's <c>interval</c>, the <c>slow_down</c> back-off, and <c>expires_in</c>, while
    /// <c>authorization_pending</c> means "keep waiting". The poll-classification logic is a pure method
    /// (<see cref="ClassifyPoll"/>) so the state machine is unit-tested; the HTTP seam is injectable.
    /// </para>
    /// </remarks>
    public sealed class OAuthDeviceFlowClient
    {
        private readonly OAuthFormPoster _poster;
        private readonly Func<float, CancellationToken, Awaitable> _delay;
        private readonly Func<DateTime> _clock;

        /// <summary>Creates a client.</summary>
        /// <param name="poster">The endpoint POST seam; defaults to <see cref="OAuthHttp.DefaultPoster"/>.</param>
        /// <param name="delay">Waits the given seconds between polls; defaults to <c>Awaitable.WaitForSecondsAsync</c>.</param>
        /// <param name="clock">Supplies "now" for expiry math; defaults to <c>DateTime.UtcNow</c>.</param>
        public OAuthDeviceFlowClient(
            OAuthFormPoster poster = null,
            Func<float, CancellationToken, Awaitable> delay = null,
            Func<DateTime> clock = null)
        {
            _poster = poster ?? OAuthHttp.DefaultPoster;
            _delay = delay ?? ((seconds, ct) => Awaitable.WaitForSecondsAsync(seconds, ct));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Requests a device + user code from the device-authorization endpoint.
        /// </summary>
        /// <param name="descriptor">The provider endpoints/client-id/scope (no secret).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The device-code info, or a failure result.</returns>
        public async Awaitable<DeviceCodeResult> RequestDeviceCodeAsync(
            OAuthEndpointDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            descriptor.ValidateForDeviceFlow();

            var form = new Dictionary<string, string>
            {
                ["client_id"] = descriptor.ClientId,
                ["scope"] = descriptor.Scope ?? string.Empty
            };

            OAuthHttpResult response;
            try
            {
                response = await _poster(descriptor.DeviceCodeUrl, form, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (string.IsNullOrWhiteSpace(response.Body))
                return DeviceCodeResult.Fail($"Device-code endpoint returned no body (HTTP {response.StatusCode}).");

            if (TryParseDeviceCode(response.Body, out var info, out var error))
                return DeviceCodeResult.Ok(info);
            return DeviceCodeResult.Fail(error);
        }

        /// <summary>
        /// Polls the token endpoint until the user authorizes, the code expires, or the flow is canceled.
        /// </summary>
        /// <param name="descriptor">The provider endpoints/client-id (no secret).</param>
        /// <param name="info">The device-code info from <see cref="RequestDeviceCodeAsync"/>.</param>
        /// <param name="cancellationToken">Cancels polling; surfaces as <see cref="OAuthResult.Canceled"/>.</param>
        /// <returns>The token bundle, or a failure/cancel result.</returns>
        public async Awaitable<OAuthResult> PollForTokenAsync(
            OAuthEndpointDescriptor descriptor, DeviceCodeInfo info, CancellationToken cancellationToken = default)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = descriptor.ClientId,
                ["device_code"] = info.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            };

            // Start at the server-suggested interval (minimum 1s); slow_down bumps it.
            var interval = Mathf.Max(1, info.Interval);
            var deadline = _clock().AddSeconds(info.ExpiresIn > 0 ? info.ExpiresIn : 900);

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return OAuthResult.Cancel();
                if (_clock() >= deadline)
                    return OAuthResult.Fail("The device code expired before authorization completed.");

                try
                {
                    await _delay(interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return OAuthResult.Cancel();
                }

                OAuthHttpResult response;
                try
                {
                    response = await _poster(descriptor.TokenUrl, form, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return OAuthResult.Cancel();
                }

                var outcome = ClassifyPoll(response.Body, _clock());
                switch (outcome.State)
                {
                    case DevicePollState.Success:
                        return OAuthResult.Ok(outcome.Tokens);
                    case DevicePollState.Pending:
                        break; // keep polling at the current interval
                    case DevicePollState.SlowDown:
                        interval += 5; // RFC 8628 §3.5: increase the interval by 5s
                        break;
                    case DevicePollState.Failed:
                    default:
                        return OAuthResult.Fail(outcome.Error);
                }
            }
        }

        /// <summary>
        /// Classifies a single device-flow poll response into the next state-machine step. Pure and
        /// network-free so the state machine is directly unit-testable.
        /// </summary>
        /// <param name="body">The raw token-endpoint JSON body.</param>
        /// <param name="nowUtc">"Now" used when a success body carries <c>expires_in</c>.</param>
        /// <returns>The interpreted outcome.</returns>
        public static DevicePollOutcome ClassifyPoll(string body, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(body))
                return DevicePollOutcome.Failed("Empty poll response.");

            JObject json;
            try
            {
                json = JObject.Parse(body);
            }
            catch (Exception e)
            {
                return DevicePollOutcome.Failed($"Unparseable poll response: {e.Message}");
            }

            var error = json.Value<string>("error");
            if (!string.IsNullOrEmpty(error))
            {
                switch (error)
                {
                    case "authorization_pending":
                        return DevicePollOutcome.Pending();
                    case "slow_down":
                        return DevicePollOutcome.SlowDown();
                    case "expired_token":
                        return DevicePollOutcome.Failed("The device code expired before authorization completed.");
                    case "access_denied":
                        return DevicePollOutcome.Failed("Authorization was denied.");
                    default:
                        var description = json.Value<string>("error_description");
                        return DevicePollOutcome.Failed(string.IsNullOrEmpty(description) ? error : $"{error}: {description}");
                }
            }

            if (OAuthHttp.TryParseTokenResponse(body, nowUtc, out var bundle, out var parseError))
                return DevicePollOutcome.Succeeded(bundle);
            return DevicePollOutcome.Failed(parseError);
        }

        private static bool TryParseDeviceCode(string body, out DeviceCodeInfo info, out string error)
        {
            info = default;
            error = null;

            JObject json;
            try
            {
                json = JObject.Parse(body);
            }
            catch (Exception e)
            {
                error = $"Unparseable device-code response: {e.Message}";
                return false;
            }

            var providerError = json.Value<string>("error");
            if (!string.IsNullOrEmpty(providerError))
            {
                error = providerError;
                return false;
            }

            var deviceCode = json.Value<string>("device_code");
            var userCode = json.Value<string>("user_code");
            // GitHub returns verification_uri; the RFC also allows verification_url.
            var verificationUri = json.Value<string>("verification_uri") ?? json.Value<string>("verification_url");
            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(verificationUri))
            {
                error = "Device-code response was missing device_code/user_code/verification_uri.";
                return false;
            }

            info = new DeviceCodeInfo(
                deviceCode,
                userCode,
                verificationUri,
                json["expires_in"] != null ? json.Value<int>("expires_in") : 0,
                json["interval"] != null ? json.Value<int>("interval") : 5);
            return true;
        }
    }

    /// <summary>The user-facing device-code details surfaced to the UI while polling.</summary>
    public readonly struct DeviceCodeInfo
    {
        /// <summary>Creates device-code info.</summary>
        public DeviceCodeInfo(string deviceCode, string userCode, string verificationUri, int expiresIn, int interval)
        {
            DeviceCode = deviceCode;
            UserCode = userCode;
            VerificationUri = verificationUri;
            ExpiresIn = expiresIn;
            Interval = interval;
        }

        /// <summary>The opaque device code used when polling the token endpoint.</summary>
        public string DeviceCode { get; }

        /// <summary>The short human code the user types at the verification URL.</summary>
        public string UserCode { get; }

        /// <summary>The URL the user opens to enter the code.</summary>
        public string VerificationUri { get; }

        /// <summary>Lifetime of the codes in seconds.</summary>
        public int ExpiresIn { get; }

        /// <summary>The minimum polling interval in seconds.</summary>
        public int Interval { get; }
    }

    /// <summary>The result of requesting a device code: the info or a failure reason.</summary>
    public readonly struct DeviceCodeResult
    {
        private DeviceCodeResult(bool success, DeviceCodeInfo info, string error)
        {
            Success = success;
            Info = info;
            Error = error;
        }

        /// <summary>True when a device code was obtained.</summary>
        public bool Success { get; }

        /// <summary>The device-code info when <see cref="Success"/> is true.</summary>
        public DeviceCodeInfo Info { get; }

        /// <summary>The failure description when <see cref="Success"/> is false.</summary>
        public string Error { get; }

        /// <summary>Creates a success result.</summary>
        public static DeviceCodeResult Ok(DeviceCodeInfo info) => new DeviceCodeResult(true, info, null);

        /// <summary>Creates a failure result.</summary>
        public static DeviceCodeResult Fail(string error) => new DeviceCodeResult(false, default, error);
    }

    /// <summary>The next state-machine step for a device-flow poll.</summary>
    public enum DevicePollState
    {
        /// <summary>The user has not yet authorized; keep polling.</summary>
        Pending,
        /// <summary>The server asked to poll less frequently; increase the interval.</summary>
        SlowDown,
        /// <summary>Authorization completed; tokens are available.</summary>
        Success,
        /// <summary>A terminal error (denied, expired, unknown).</summary>
        Failed
    }

    /// <summary>The interpreted outcome of a single device-flow poll.</summary>
    public readonly struct DevicePollOutcome
    {
        private DevicePollOutcome(DevicePollState state, OAuthTokenBundle tokens, string error)
        {
            State = state;
            Tokens = tokens;
            Error = error;
        }

        /// <summary>The classified next step.</summary>
        public DevicePollState State { get; }

        /// <summary>The token bundle when <see cref="State"/> is <see cref="DevicePollState.Success"/>.</summary>
        public OAuthTokenBundle Tokens { get; }

        /// <summary>The error description when <see cref="State"/> is <see cref="DevicePollState.Failed"/>.</summary>
        public string Error { get; }

        /// <summary>Creates a pending outcome.</summary>
        public static DevicePollOutcome Pending() => new DevicePollOutcome(DevicePollState.Pending, null, null);

        /// <summary>Creates a slow-down outcome.</summary>
        public static DevicePollOutcome SlowDown() => new DevicePollOutcome(DevicePollState.SlowDown, null, null);

        /// <summary>Creates a success outcome.</summary>
        public static DevicePollOutcome Succeeded(OAuthTokenBundle tokens) => new DevicePollOutcome(DevicePollState.Success, tokens, null);

        /// <summary>Creates a failed outcome.</summary>
        public static DevicePollOutcome Failed(string error) => new DevicePollOutcome(DevicePollState.Failed, null, error);
    }
}
