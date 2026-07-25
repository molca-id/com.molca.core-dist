using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>Verifies keyed RSA add-on manifests and all security-sensitive manifest claims.</summary>
    internal static class AddonManifestVerifier
    {
        internal static bool TryVerify(string token, string expectedId, string expectedVersion,
            out VerifiedAddonManifest verified, out string error)
        {
            verified = null;
            error = null;
            if (string.IsNullOrWhiteSpace(token)) { error = "Manifest token is empty."; return false; }
            string[] parts = token.Split('.');
            if (parts.Length != 3 || Array.Exists(parts, string.IsNullOrEmpty))
            {
                error = "Manifest token is malformed.";
                return false;
            }
            string keyId = parts[0];
            if (!AddonDistributionConfig.TryGetPublicKey(keyId, out var key))
            {
                error = $"Manifest uses untrusted signing key '{keyId}'.";
                return false;
            }
            return TryVerify(token, expectedId, expectedVersion, keyId, key, false, out verified, out error);
        }

        /// <summary>Verifies an administrator-exported manifest for manual air-gapped installation.</summary>
        internal static bool TryVerifyOffline(string token, out VerifiedAddonManifest verified, out string error)
        {
            verified = null;
            error = null;
            string[] parts = (token ?? string.Empty).Trim().Split('.');
            if (parts.Length != 3 || !AddonDistributionConfig.TryGetPublicKey(parts[0], out var key))
            { error = "Offline manifest uses a malformed or untrusted signing key."; return false; }
            return TryVerify(token.Trim(), null, null, parts[0], key, true, out verified, out error);
        }

        internal static bool TryVerifyOffline(string token, string trustedKeyId, RSAParameters publicKey,
            out VerifiedAddonManifest verified, out string error) =>
            TryVerify((token ?? string.Empty).Trim(), null, null, trustedKeyId, publicKey, true,
                out verified, out error);

        internal static bool TryVerify(string token, string expectedId, string expectedVersion,
            string trustedKeyId, RSAParameters publicKey, out VerifiedAddonManifest verified, out string error) =>
            TryVerify(token, expectedId, expectedVersion, trustedKeyId, publicKey, false, out verified, out error);

        private static bool TryVerify(string token, string expectedId, string expectedVersion,
            string trustedKeyId, RSAParameters publicKey, bool allowOffline,
            out VerifiedAddonManifest verified, out string error)
        {
            verified = null;
            error = null;
            string[] parts = (token ?? string.Empty).Split('.');
            if (parts.Length != 3 || parts[0] != trustedKeyId)
            {
                error = "Manifest token key id is malformed or untrusted.";
                return false;
            }

            byte[] signature;
            try { signature = Base64UrlDecode(parts[2]); }
            catch { error = "Manifest signature encoding is invalid."; return false; }

            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    error = "Manifest signature verification failed.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = $"Manifest verification error: {exception.Message}";
                return false;
            }

            AddonManifest manifest;
            try { manifest = JsonUtility.FromJson<AddonManifest>(Encoding.UTF8.GetString(Base64UrlDecode(parts[1]))); }
            catch (Exception exception) { error = $"Manifest payload is invalid: {exception.Message}"; return false; }

            if (manifest == null || manifest.schemaVersion != AddonDistributionConfig.ManifestSchemaVersion || manifest.kind != AddonDistributionConfig.ManifestKind)
            { error = "Manifest schema or kind is unsupported."; return false; }
            if ((expectedId != null && !string.Equals(manifest.id, expectedId, StringComparison.Ordinal)) ||
                (expectedVersion != null && !string.Equals(manifest.version, expectedVersion, StringComparison.Ordinal)))
            { error = "Manifest identity does not match the requested add-on."; return false; }
            if (string.IsNullOrWhiteSpace(manifest.publisher))
            { error = "Manifest publisher is missing."; return false; }
            if (manifest.sizeBytes < 0 || manifest.sizeBytes > AddonDistributionConfig.MaxDownloadBytes)
            { error = "Manifest artifact size exceeds the client policy."; return false; }
            if (!IsSha256(manifest.sha256))
            { error = "Manifest SHA-256 is invalid."; return false; }
            if (!AddonSemVer.Satisfies(AddonDistributionConfig.CoreVersion(), manifest.coreVersionRange))
            { error = $"Add-on requires Core {manifest.coreVersionRange}; this project uses {AddonDistributionConfig.CoreVersion()}."; return false; }
            string runtime = AddonDistributionConfig.EditorRuntime();
            // A pack shipping a precompiled assembly is built against one scripting runtime and must
            // name it. Runtime *source* is compiled by whichever editor installs it, so it stays 'any'
            // — the publisher derives this from the archive, it is not a publisher assertion.
            if (manifest.precompiled && manifest.runtime == "any")
            { error = "Add-ons shipping precompiled assemblies must target mono or coreclr."; return false; }
            if (manifest.runtime != "any" && manifest.runtime != runtime)
            { error = $"Add-on targets {manifest.runtime}; this editor uses {runtime}."; return false; }
            if (allowOffline)
            {
                if (!manifest.offline || !string.IsNullOrEmpty(manifest.downloadUrl))
                { error = "Manifest is not an offline distribution manifest."; return false; }
            }
            else
            {
                if (manifest.offline || !Uri.TryCreate(manifest.downloadUrl, UriKind.Absolute, out var downloadUri) ||
                    !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !AddonDistributionConfig.IsTrustedDownloadHost(downloadUri.Host))
                { error = "Manifest download URL is not on the pinned HTTPS host allowlist."; return false; }
                if (!DateTimeOffset.TryParse(manifest.downloadExpiresAt, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
                { error = "Manifest download grant is expired or malformed."; return false; }
            }

            verified = new VerifiedAddonManifest(manifest, trustedKeyId, token);
            return true;
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!Uri.IsHexDigit(c)) return false;
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
