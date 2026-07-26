using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Molca.Editor.Licensing;
using UnityEditor.PackageManager;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Distribution-fixed add-on trust policy. Private keys and entitlement rules remain server-side;
    /// this client contains only pinned HTTPS hosts, public verification keys, and resource limits.
    /// </summary>
    internal static class AddonDistributionConfig
    {
        internal const string ManifestKind = "molca.addon.manifest";
        internal const string CurrentSigningKeyId = "molca-rsa-2026-01";

        /// <summary>
        /// Wire schema this client speaks. Version 3 makes signed dependency metadata and project
        /// add-on policy mandatory; older clients must not install only the root of a dependency graph.
        /// </summary>
        internal const int CatalogSchemaVersion = 3;

        /// <summary>Schema of the signed manifest payload. Moves with the catalog schema.</summary>
        internal const int ManifestSchemaVersion = 3;
        internal const long MaxDownloadBytes = 512L * 1024 * 1024;
        internal const long MaxExpandedBytes = 1024L * 1024 * 1024;
        internal const int MaxExtractedFiles = 20_000;

        // Add exact CDN hosts here during a Core release before the server emits URLs for them.
        private static readonly string[] AdditionalTrustedDownloadHosts = Array.Empty<string>();

        internal static string CatalogUrl(string coreVersion, string runtime, string channel) =>
            $"{DevLicenseConfig.ServerBaseUrl.TrimEnd('/')}/addons/catalog" +
            $"?schema=3&coreVersion={Uri.EscapeDataString(coreVersion)}" +
            $"&runtime={Uri.EscapeDataString(runtime)}&channel={Uri.EscapeDataString(channel)}" +
            $"&projectBinding={Uri.EscapeDataString(MolcaProjectSettings.Instance?.ProjectBinding ?? string.Empty)}";

        internal static string ManifestUrl(string id, string version) =>
            $"{DevLicenseConfig.ServerBaseUrl.TrimEnd('/')}/addons/{Uri.EscapeDataString(id)}/manifest" +
            $"?schema=3&version={Uri.EscapeDataString(version)}" +
            $"&projectBinding={Uri.EscapeDataString(MolcaProjectSettings.Instance?.ProjectBinding ?? string.Empty)}";

        internal static string ApprovalUrl(string projectId) =>
            $"{DevLicenseConfig.ServerBaseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/addons/approve";

        internal static bool IsTrustedDownloadHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (Uri.TryCreate(DevLicenseConfig.ServerBaseUrl, UriKind.Absolute, out var server) &&
                string.Equals(server.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (string trusted in AdditionalTrustedDownloadHosts)
            {
                if (string.Equals(trusted, host, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static bool TryGetPublicKey(string keyId, out RSAParameters publicKey)
        {
            if (string.Equals(keyId, CurrentSigningKeyId, StringComparison.Ordinal))
            {
                publicKey = new RSAParameters
                {
                    Modulus = Convert.FromBase64String(DevLicenseConfig.PublicKeyModulusBase64),
                    Exponent = Convert.FromBase64String(DevLicenseConfig.PublicKeyExponentBase64),
                };
                return true;
            }
            publicKey = default;
            return false;
        }

        internal static string CoreVersion()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(AddonDistributionConfig).Assembly);
                return info != null ? info.version : string.Empty;
            }
            catch { return string.Empty; }
        }

        internal static string EditorRuntime() =>
            Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader", false) != null
                ? "coreclr"
                : "mono";

        internal static IReadOnlyList<string> TrustedDownloadHosts
        {
            get
            {
                var result = new List<string>();
                if (Uri.TryCreate(DevLicenseConfig.ServerBaseUrl, UriKind.Absolute, out var server))
                    result.Add(server.Host);
                result.AddRange(AdditionalTrustedDownloadHosts);
                return result;
            }
        }
    }
}
