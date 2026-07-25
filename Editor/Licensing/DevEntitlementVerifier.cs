using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Offline verification of a signed developer entitlement token. Verifies the RSA-2048 /
    /// SHA-256 (PKCS#1 v1.5) signature against the embedded public key, then evaluates expiry and
    /// machine binding. No network — a valid cached token survives an offline build.
    /// </summary>
    /// <remarks>
    /// The token is <c>base64url(payloadJson).base64url(signature)</c>; the signature covers the
    /// ASCII bytes of the first segment. RSA (not ECDSA) because Unity's Mono editor runtime does
    /// not implement <see cref="ECDsa"/> (it throws <see cref="NotImplementedException"/> at
    /// runtime); Mono verifies RSA natively. All methods are pure and allocate their own key handle.
    /// </remarks>
    internal static class DevEntitlementVerifier
    {
        /// <summary>
        /// Verifies a token's signature and, on success, returns its decoded payload.
        /// </summary>
        /// <param name="token">The compact entitlement token.</param>
        /// <param name="payload">The decoded claims when this returns <c>true</c>; otherwise <c>null</c>.</param>
        /// <param name="error">A human-readable failure reason when this returns <c>false</c>.</param>
        /// <returns><c>true</c> if the signature is valid and the payload parsed.</returns>
        public static bool TryVerify(string token, out DevEntitlementPayload payload, out string error)
            => TryVerify(token, ConfiguredPublicKey(), out payload, out error);

        /// <summary>
        /// Verification core against an explicitly-supplied public key. The public
        /// <see cref="TryVerify(string, out DevEntitlementPayload, out string)"/> passes the embedded
        /// distribution key; tests pass a generated key so the logic is exercisable without the
        /// production private key.
        /// </summary>
        /// <param name="token">The compact entitlement token.</param>
        /// <param name="publicKey">The RSA public key to verify against.</param>
        /// <param name="payload">The decoded claims when this returns <c>true</c>; otherwise <c>null</c>.</param>
        /// <param name="error">A human-readable failure reason when this returns <c>false</c>.</param>
        /// <returns><c>true</c> if the signature is valid and the payload parsed.</returns>
        internal static bool TryVerify(string token, RSAParameters publicKey, out DevEntitlementPayload payload, out string error)
        {
            payload = null;
            error = null;

            if (string.IsNullOrEmpty(token)) { error = "No token."; return false; }

            int dot = token.IndexOf('.');
            if (dot <= 0 || dot >= token.Length - 1) { error = "Malformed token."; return false; }

            string signingInput = token.Substring(0, dot);
            string signatureB64 = token.Substring(dot + 1);

            byte[] signature;
            try { signature = Base64UrlDecode(signatureB64); }
            catch { error = "Bad signature encoding."; return false; }

            byte[] message = Encoding.ASCII.GetBytes(signingInput);

            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    error = "Signature verification failed.";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = $"Verification error: {e.Message}";
                return false;
            }

            try
            {
                string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(signingInput));
                payload = JsonUtility.FromJson<DevEntitlementPayload>(payloadJson);
            }
            catch (Exception e)
            {
                error = $"Payload parse error: {e.Message}";
                return false;
            }

            if (payload == null) { error = "Empty payload."; return false; }
            return true;
        }

        /// <summary>
        /// Evaluates a token to a <see cref="DevLicenseStatus"/> against the given machine id and
        /// the current UTC clock (with <see cref="DevLicenseConfig.ExpirySkew"/> applied).
        /// </summary>
        /// <param name="token">The token to evaluate; may be null/empty.</param>
        /// <param name="currentMachineId">This machine's device unique identifier.</param>
        /// <param name="payload">The decoded payload when the signature was valid; otherwise <c>null</c>.</param>
        /// <returns>The evaluated status.</returns>
        public static DevLicenseStatus Evaluate(string token, string currentMachineId, out DevEntitlementPayload payload)
            => Evaluate(token, ConfiguredPublicKey(), currentMachineId, out payload);

        /// <summary>
        /// Evaluation core against an explicitly-supplied public key (test seam; see
        /// <see cref="TryVerify(string, RSAParameters, out DevEntitlementPayload, out string)"/>).
        /// </summary>
        /// <param name="token">The token to evaluate; may be null/empty.</param>
        /// <param name="publicKey">The RSA public key to verify against.</param>
        /// <param name="currentMachineId">This machine's device unique identifier.</param>
        /// <param name="payload">The decoded payload when the signature was valid; otherwise <c>null</c>.</param>
        /// <returns>The evaluated status.</returns>
        internal static DevLicenseStatus Evaluate(string token, RSAParameters publicKey, string currentMachineId, out DevEntitlementPayload payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(token))
                return DevLicenseStatus.Missing;

            if (!TryVerify(token, publicKey, out payload, out _))
                return DevLicenseStatus.Invalid;

            long nowWithSkew = DateTimeOffset.UtcNow.Add(DevLicenseConfig.ExpirySkew).ToUnixTimeSeconds();
            if (payload.exp <= nowWithSkew)
                return DevLicenseStatus.Expired;

            if (!string.IsNullOrEmpty(currentMachineId) &&
                !string.Equals(payload.machineId, currentMachineId, StringComparison.Ordinal))
                return DevLicenseStatus.WrongMachine;

            return DevLicenseStatus.Valid;
        }

        /// <summary>The embedded distribution public key as <see cref="RSAParameters"/>.</summary>
        private static RSAParameters ConfiguredPublicKey() => new RSAParameters
        {
            Modulus = Convert.FromBase64String(DevLicenseConfig.PublicKeyModulusBase64),
            Exponent = Convert.FromBase64String(DevLicenseConfig.PublicKeyExponentBase64),
        };

        /// <summary>Decodes a base64url string (no padding) to bytes.</summary>
        private static byte[] Base64UrlDecode(string value)
        {
            string s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
