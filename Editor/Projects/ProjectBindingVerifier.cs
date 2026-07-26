using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Molca.Editor.Addons;
using UnityEngine;

namespace Molca.Editor.Projects
{
    /// <summary>Verifies the signed, commit-safe receipt connecting this repository to a backend project.</summary>
    internal static class ProjectBindingVerifier
    {
        internal const int SchemaVersion = 1;
        internal const string Kind = "molca.project-binding";

        internal static bool TryVerify(string token, string expectedProjectId, string expectedProjectCode,
            string expectedLicenseeId, out ProjectBindingPayload payload, out string error)
        {
            payload = null;
            error = null;
            string[] parts = (token ?? string.Empty).Split('.');
            if (parts.Length != 3 || !AddonDistributionConfig.TryGetPublicKey(parts[0], out var key))
            {
                error = "Project binding uses a malformed or untrusted signing key.";
                return false;
            }
            return TryVerify(token, parts[0], key, expectedProjectId, expectedProjectCode,
                expectedLicenseeId, out payload, out error);
        }

        internal static bool TryVerify(string token, string trustedKeyId, RSAParameters publicKey,
            string expectedProjectId, string expectedProjectCode, string expectedLicenseeId,
            out ProjectBindingPayload payload, out string error)
        {
            payload = null;
            error = null;
            string[] parts = (token ?? string.Empty).Split('.');
            if (parts.Length != 3 || parts[0] != trustedKeyId || Array.Exists(parts, string.IsNullOrEmpty))
            {
                error = "Project binding token is malformed.";
                return false;
            }

            byte[] signature;
            try { signature = Base64UrlDecode(parts[2]); }
            catch { error = "Project binding signature encoding is invalid."; return false; }

            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    error = "Project binding signature verification failed.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = $"Project binding verification error: {exception.Message}";
                return false;
            }

            try
            {
                payload = JsonUtility.FromJson<ProjectBindingPayload>(
                    Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            }
            catch (Exception exception)
            {
                error = $"Project binding payload is invalid: {exception.Message}";
                return false;
            }

            if (payload == null || payload.schemaVersion != SchemaVersion || payload.kind != Kind)
            { error = "Project binding schema or kind is unsupported."; return false; }
            if (!Guid.TryParse(payload.bindingId, out _) || !Guid.TryParse(payload.projectId, out _))
            { error = "Project binding identity is malformed."; return false; }
            if (!Regex.IsMatch(payload.projectCode ?? string.Empty, "^MOLCA-[A-Z0-9]{6}$") ||
                string.IsNullOrWhiteSpace(payload.licenseeId) ||
                !DateTimeOffset.TryParse(payload.issuedAt, out var issuedAt) ||
                issuedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            { error = "Project binding metadata is malformed."; return false; }
            if (!string.IsNullOrEmpty(expectedProjectId) &&
                !string.Equals(payload.projectId, expectedProjectId, StringComparison.OrdinalIgnoreCase))
            { error = "Project binding does not match this project's id."; return false; }
            if (!string.IsNullOrEmpty(expectedProjectCode) &&
                !string.Equals(payload.projectCode, expectedProjectCode, StringComparison.Ordinal))
            { error = "Project binding does not match this project's code."; return false; }
            if (!string.IsNullOrEmpty(expectedLicenseeId) &&
                !string.Equals(payload.licenseeId, expectedLicenseeId, StringComparison.Ordinal))
            { error = "Project binding belongs to another licensee."; return false; }
            return true;
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string text = value.Replace('-', '+').Replace('_', '/');
            if (text.Length % 4 == 2) text += "==";
            else if (text.Length % 4 == 3) text += "=";
            else if (text.Length % 4 == 1) throw new FormatException("Invalid base64url length.");
            return Convert.FromBase64String(text);
        }
    }
}
