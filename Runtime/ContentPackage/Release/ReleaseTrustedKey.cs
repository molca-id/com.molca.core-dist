using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>One trusted release-signing public key, addressed by its key id.</summary>
    /// <remarks>
    /// Modulus and exponent rather than a PEM: Unity's Mono runtime has
    /// <c>RSA.ImportParameters</c> everywhere, while <c>ImportSubjectPublicKeyInfo</c> is not
    /// available on every player scripting backend this SDK ships to. Same encoding the
    /// localization overlay already trusts, so key rotation is one procedure rather than two.
    /// </remarks>
    [Serializable]
    public class ReleaseTrustedKey
    {
        /// <summary>The <c>kid</c> segment of a compact token, compared ordinally.</summary>
        public string KeyId;

        /// <summary>RSA modulus, standard base64 (not base64url).</summary>
        public string ModulusBase64;

        /// <summary>RSA public exponent, standard base64.</summary>
        public string ExponentBase64;

        /// <summary>True when every field needed to import the key is present.</summary>
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(KeyId) &&
            !string.IsNullOrWhiteSpace(ModulusBase64) &&
            !string.IsNullOrWhiteSpace(ExponentBase64);
    }

    /// <summary>Resolves a signing key id to the public key that must have signed with it.</summary>
    /// <remarks>
    /// An interface so a test can supply a throwaway key and so key material can later come from
    /// somewhere other than a serialized asset without touching verification. Fail-closed is the
    /// contract: an unresolvable key id is <see cref="ContentReleaseReason.ManifestUntrusted"/>,
    /// never a reason to skip the check.
    /// </remarks>
    public interface IReleaseKeyring
    {
        /// <summary>Resolves a key id, or returns false when it is not trusted.</summary>
        /// <param name="keyId">The <c>kid</c> from the compact token.</param>
        /// <param name="key">The trusted key, when this returns true.</param>
        bool TryGetKey(string keyId, out ReleaseTrustedKey key);
    }

    /// <summary>An <see cref="IReleaseKeyring"/> over a fixed list of keys.</summary>
    public sealed class ReleaseKeyring : IReleaseKeyring
    {
        private readonly Dictionary<string, ReleaseTrustedKey> _keys =
            new Dictionary<string, ReleaseTrustedKey>(StringComparer.Ordinal);

        /// <summary>Builds a keyring, ignoring null and incomplete entries.</summary>
        /// <param name="keys">The trusted keys, typically from serialized settings.</param>
        public ReleaseKeyring(IEnumerable<ReleaseTrustedKey> keys)
        {
            foreach (var key in keys ?? Array.Empty<ReleaseTrustedKey>())
            {
                if (key == null || !key.IsComplete)
                {
                    // Loud, because a half-filled key entry means the release this build was meant
                    // to trust will be refused at runtime with a signature error that looks like
                    // tampering rather than like misconfiguration.
                    if (key != null)
                        Debug.LogWarning($"[ContentRelease] Trusted key '{key.KeyId}' is incomplete and was ignored.");
                    continue;
                }
                _keys[key.KeyId] = key;
            }
        }

        /// <summary>True when the keyring holds no usable key, so nothing can ever verify.</summary>
        public bool IsEmpty => _keys.Count == 0;

        /// <inheritdoc/>
        public bool TryGetKey(string keyId, out ReleaseTrustedKey key)
        {
            key = null;
            return !string.IsNullOrEmpty(keyId) && _keys.TryGetValue(keyId, out key);
        }
    }
}
