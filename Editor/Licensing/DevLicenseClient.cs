using System;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Molca.Settings.Integration.OAuth;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Drives interactive developer activation: captures a Google authorization code, sends it to
    /// the license server for exchange, and stores the signed entitlement on success. The Google
    /// credential exchange and allow-list decision are made entirely server-side.
    /// </summary>
    internal static class DevLicenseClient
    {
        private const int RequestTimeoutSeconds = 30;

        /// <summary>The outcome of an activation attempt.</summary>
        public readonly struct ActivationResult
        {
            private ActivationResult(bool success, bool canceled, string error, string licenseeId, string expiresAt)
            {
                Success = success;
                Canceled = canceled;
                Error = error;
                LicenseeId = licenseeId;
                ExpiresAt = expiresAt;
            }

            /// <summary>True when a valid entitlement was obtained and stored.</summary>
            public bool Success { get; }

            /// <summary>True when the developer/caller canceled (not an error).</summary>
            public bool Canceled { get; }

            /// <summary>The failure reason when <see cref="Success"/> is false and not canceled.</summary>
            public string Error { get; }

            /// <summary>The granted licensee id on success.</summary>
            public string LicenseeId { get; }

            /// <summary>The entitlement expiry (ISO-8601) on success.</summary>
            public string ExpiresAt { get; }

            internal static ActivationResult Ok(string licenseeId, string expiresAt) =>
                new ActivationResult(true, false, null, licenseeId, expiresAt);
            internal static ActivationResult Fail(string error) =>
                new ActivationResult(false, false, error, null, null);
            internal static ActivationResult Cancel() =>
                new ActivationResult(false, true, "Canceled.", null, null);
        }

        /// <summary>Serializable body for the <c>/activate-dev</c> request.</summary>
        [Serializable]
        private class ActivateRequest
        {
            public string authorizationCode;
            public string codeVerifier;
            public string redirectUri;
            public string machineId;
            public string coreVersion;
        }

        /// <summary>Serializable body of an error response from <c>/activate-dev</c>.</summary>
        [Serializable]
        private class ErrorResponse
        {
            public string reason;
        }

        /// <summary>Serializable body of the <c>/activate-dev</c> success response.</summary>
        [Serializable]
        private class ActivateResponse
        {
            public string entitlementToken;
            public string licenseeId;
            public string expiresAt;
        }

        /// <summary>
        /// Runs the full activation flow. Signs in with Google (loopback + PKCE), posts the one-time
        /// code to the server, and — if the identity is on the allow-list — stores the returned
        /// entitlement.
        /// </summary>
        /// <param name="cancellationToken">Cancels the sign-in / request; surfaces as a canceled result.</param>
        /// <returns>The activation result.</returns>
        public static async Awaitable<ActivationResult> ActivateAsync(CancellationToken cancellationToken = default)
        {
            if (!DevLicenseConfig.IsConfigured)
                return ActivationResult.Fail("Licensing is not configured (see DevLicenseConfig).");

            // 1) Capture the code and PKCE verifier. The trusted control plane performs the token exchange.
            var descriptor = GoogleOAuthDescriptor.Create();
            var oauth = new OAuthAuthorizationCodeClient();
            OAuthCodeResult auth;
            try
            {
                auth = await oauth.AuthorizeForCodeAsync(descriptor, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ActivationResult.Cancel();
            }

            if (auth.Canceled) return ActivationResult.Cancel();
            if (!auth.Success) return ActivationResult.Fail($"Google sign-in failed: {auth.Error}");
            if (string.IsNullOrEmpty(auth.Code) || string.IsNullOrEmpty(auth.CodeVerifier)
                || string.IsNullOrEmpty(auth.RedirectUri))
                return ActivationResult.Fail("Google sign-in returned an incomplete authorization result.");

            // 2) Let the server exchange the code, resolve identity, and issue the signed entitlement.
            var body = new ActivateRequest
            {
                authorizationCode = auth.Code,
                codeVerifier = auth.CodeVerifier,
                redirectUri = auth.RedirectUri,
                machineId = SystemInfo.deviceUniqueIdentifier,
                coreVersion = CoreVersion(),
            };

            try
            {
                return await PostActivateAsync(JsonUtility.ToJson(body), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ActivationResult.Cancel();
            }
            catch (Exception e)
            {
                return ActivationResult.Fail($"Activation request failed: {e.Message}");
            }
        }

        /// <summary>POSTs the activation body and interprets the response.</summary>
        private static async Awaitable<ActivationResult> PostActivateAsync(string json, CancellationToken cancellationToken)
        {
            using var request = new UnityWebRequest(DevLicenseConfig.ActivateUrl, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = RequestTimeoutSeconds;

            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            long code = request.responseCode;
            string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

            if (request.result != UnityWebRequest.Result.Success)
            {
                string reason = null;
                try { reason = JsonUtility.FromJson<ErrorResponse>(text)?.reason; }
                catch { /* Fall back to the HTTP status below. */ }

                if (reason == "google_code_exchange_failed")
                    return ActivationResult.Fail("Google sign-in expired. Please try again.");
                if (reason == "invalid_redirect_uri")
                    return ActivationResult.Fail("Google sign-in callback was rejected by the server. Please update Molca Core.");
                if (reason == "missing_fields")
                    return ActivationResult.Fail("The activation request was incomplete. Please update Molca Core.");
                if (code == 403)
                    return ActivationResult.Fail("This Google account is not on the license allow-list.");
                if (code == 401)
                    return ActivationResult.Fail("Google verification failed. Please try signing in again.");
                return ActivationResult.Fail($"Server returned {code}: {request.error}");
            }

            ActivateResponse response;
            try { response = JsonUtility.FromJson<ActivateResponse>(text); }
            catch (Exception e) { return ActivationResult.Fail($"Could not parse server response: {e.Message}"); }

            if (response == null || string.IsNullOrEmpty(response.entitlementToken))
                return ActivationResult.Fail("Server response contained no entitlement.");

            // Verify what we were handed before trusting/storing it.
            if (!DevEntitlementVerifier.TryVerify(response.entitlementToken, out _, out string verifyError))
                return ActivationResult.Fail($"Received an invalid entitlement: {verifyError}");

            DevEntitlementStore.Save(response.entitlementToken);
            return ActivationResult.Ok(response.licenseeId, response.expiresAt);
        }

        /// <summary>The com.molca.core package version, for activation telemetry/attribution.</summary>
        private static string CoreVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DevLicenseClient).Assembly);
                return info != null ? info.version : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
