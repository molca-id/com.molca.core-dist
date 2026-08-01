using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.Localization
{
    /// <summary>One trusted keyed RSA public key used to verify catalog manifests.</summary>
    [Serializable]
    public sealed class LocalizationRemotePublicKey
    {
        /// <summary>Server signing-key identifier carried by the keyed manifest token.</summary>
        public string KeyId;
        /// <summary>Unsigned big-endian RSA modulus encoded as standard base64.</summary>
        [TextArea(2, 5)] public string ModulusBase64;
        /// <summary>Unsigned big-endian RSA exponent encoded as standard base64.</summary>
        public string ExponentBase64 = "AQAB";
    }

    /// <summary>One shipped stable entry identity and its immutable placeholder contract.</summary>
    [Serializable]
    public sealed class LocalizationRemoteAllowedEntry
    {
        /// <summary>Stable 32-hex Unity StringTable collection id.</summary>
        public string CollectionId;
        /// <summary>Stable positive Unity shared-table entry id.</summary>
        public long EntryId;
        /// <summary>Placeholder names permitted in every remote value for this identity.</summary>
        public string[] Placeholders;

        /// <summary>Normalized collection and entry identity.</summary>
        public string Identity => LocalizationOverlaySnapshot.Identity(CollectionId, EntryId);
    }

    /// <summary>Build-safe trust, compatibility, allowlist, and cache policy for remote catalogs.</summary>
    [CreateAssetMenu(
        fileName = "Localization Remote Catalog",
        menuName = "Molca/Localization/Remote Catalog Settings",
        order = 43)]
    public sealed class LocalizationRemoteCatalogSettings : ScriptableObject
    {
        [SerializeField] private bool enabled;
        [SerializeField] private string projectId;
        [SerializeField] private string manifestUrl;
        [SerializeField] private string channel = "stable";
        [SerializeField] private int retainedVersions = 2;
        [SerializeField] private List<string> allowedDownloadHosts = new();
        [SerializeField] private List<LocalizationRemotePublicKey> trustedKeys = new();
        [SerializeField] private List<LocalizationRemoteAllowedEntry> allowedEntries = new();

        /// <summary>Whether runtime remote localization is enabled.</summary>
        public bool Enabled => enabled;
        /// <summary>Project identity the signed manifest must match.</summary>
        public string ProjectId => projectId ?? string.Empty;
        /// <summary>Optional manifest endpoint; blank uses the licensed server endpoint.</summary>
        public string ManifestUrl => manifestUrl ?? string.Empty;
        /// <summary>Catalog channel the player accepts.</summary>
        public string Channel => string.IsNullOrWhiteSpace(channel) ? "stable" : channel;
        /// <summary>Maximum last-known-good versions retained on disk.</summary>
        public int RetainedVersions => Mathf.Clamp(retainedVersions, 1, 5);
        /// <summary>Explicit cross-origin download hosts allowed by transport policy.</summary>
        public IReadOnlyList<string> AllowedDownloadHosts =>
            (IReadOnlyList<string>)allowedDownloadHosts ?? Array.Empty<string>();
        /// <summary>Public verification keys trusted by this player build.</summary>
        public IReadOnlyList<LocalizationRemotePublicKey> TrustedKeys =>
            (IReadOnlyList<LocalizationRemotePublicKey>)trustedKeys ??
            Array.Empty<LocalizationRemotePublicKey>();
        /// <summary>Stable identities and placeholder contracts shipped with this build.</summary>
        public IReadOnlyList<LocalizationRemoteAllowedEntry> AllowedEntries =>
            (IReadOnlyList<LocalizationRemoteAllowedEntry>)allowedEntries ??
            Array.Empty<LocalizationRemoteAllowedEntry>();

        internal bool TryGetKey(string keyId, out LocalizationRemotePublicKey key)
        {
            key = trustedKeys?.FirstOrDefault(candidate =>
                string.Equals(candidate?.KeyId, keyId, StringComparison.Ordinal));
            return key != null;
        }

        internal Dictionary<string, LocalizationRemoteAllowedEntry> BuildAllowlist() =>
            (allowedEntries ?? new List<LocalizationRemoteAllowedEntry>())
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.CollectionId) &&
                            entry.EntryId > 0)
            .GroupBy(entry => entry.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }
}
