using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Utils
{
    /// <summary>
    /// Local AES encryption helpers for data at rest (used by <see cref="CacheManager"/>).
    /// </summary>
    /// <remarks>
    /// <b>Scope (Sprint 4.6):</b> this subsystem previously advertised a server
    /// key-exchange flow (<c>EncryptString</c>/<c>DecryptString</c>) built on RSA
    /// decryption with a <i>public</i> key — which is cryptographically impossible and
    /// threw <see cref="CryptographicException"/> on every call. That flow has been
    /// scoped out rather than faked: a real key exchange needs a client-side keypair
    /// (server encrypts the symmetric key with the client's public key) and matching
    /// server support, which does not exist yet. The legacy entry points remain as
    /// <c>[Obsolete]</c> shims that throw <see cref="NotSupportedException"/>.
    /// <para/>
    /// What remains — <see cref="EncryptData"/>/<see cref="DecryptData"/> — is local
    /// AES-256-CBC for cache files. The key is derived from the configured public-key
    /// asset (compatible with previously written caches) or, when absent, from
    /// <see cref="SystemInfo.deviceUniqueIdentifier"/>. Both derivations use
    /// non-secret material: this is at-rest obfuscation, not strong secrecy.
    /// For credentials, use <see cref="SecureStorage"/>.
    /// </remarks>
    public class Encryption : RuntimeSubsystem
    {
        private const int IV_SIZE = 16; // 128 bits
        private static readonly Regex PublicKeyRegex = new(@"(?<=-----BEGIN PUBLIC KEY-----).*?(?=-----END PUBLIC KEY-----)", RegexOptions.Singleline);

        /// <summary>Result of the removed string-encryption flow. Kept for binary compatibility.</summary>
        public struct EncryptionResult
        {
            public string EncryptedData { get; }
            public byte[] SymmetricKey { get; }

            public EncryptionResult(string data, byte[] key)
            {
                EncryptedData = data;
                SymmetricKey = key;
            }
        }

        [SerializeField, FormerlySerializedAs("publicKey")] private TextAsset _publicKey;

        private static string _publicKeyString;
        private static byte[] _cachedAesKey;

        public override async Awaitable InitializeAsync(System.Threading.CancellationToken cancellationToken)
        {
            // Key-material seed only; no network key exchange happens anymore.
            if (_publicKey != null)
            {
                _publicKeyString = PublicKeyRegex.Match(_publicKey.text).Value;
                if (string.IsNullOrEmpty(_publicKeyString))
                    Debug.LogWarning("[Encryption] Could not extract public key from PEM; falling back to device-derived key.");
            }

            await RuntimeManager.FromResult(true);
        }

        public override void Teardown()
        {
            _publicKeyString = null;
            _cachedAesKey = null;
            base.Teardown();
        }

        #region Public Encryption Methods

        /// <summary>Removed: see class remarks. Always throws.</summary>
        /// <exception cref="NotSupportedException">Always.</exception>
        [Obsolete("The public-key string-encryption flow was removed in Sprint 4.6: it relied on RSA decryption with a public key, which is impossible and always threw at runtime. Use EncryptData/DecryptData for local at-rest encryption, or SecureStorage for credentials.", false)]
        public static Awaitable<EncryptionResult> EncryptString(string data)
        {
            throw new NotSupportedException("Encryption.EncryptString was removed (broken public-key DecryptRsa). See Encryption class remarks.");
        }

        /// <summary>Removed: see class remarks. Always throws.</summary>
        /// <exception cref="NotSupportedException">Always.</exception>
        [Obsolete("The public-key string-encryption flow was removed in Sprint 4.6: it relied on RSA decryption with a public key, which is impossible and always threw at runtime. Use EncryptData/DecryptData for local at-rest encryption, or SecureStorage for credentials.", false)]
        public static string DecryptString(string data, byte[] symKey)
        {
            throw new NotSupportedException("Encryption.DecryptString was removed (broken public-key DecryptRsa). See Encryption class remarks.");
        }

        /// <summary>Encrypts data with the local AES key (IV prepended to the output).</summary>
        public static byte[] EncryptData(byte[] data)
        {
            EnsureAesKey();
            return PerformAesEncryption(data, _cachedAesKey);
        }

        /// <summary>Decrypts data produced by <see cref="EncryptData"/> on this device/configuration.</summary>
        public static byte[] DecryptData(byte[] encryptedData)
        {
            EnsureAesKey();
            return PerformAesDecryption(encryptedData, _cachedAesKey);
        }

        #endregion

        #region Private Encryption Methods

        private static void EnsureAesKey()
        {
            if (_cachedAesKey != null)
                return;

            // Derivation from the public-key asset is kept for compatibility with
            // caches written by earlier versions; device id is the keyless fallback.
            string seed = !string.IsNullOrEmpty(_publicKeyString)
                ? _publicKeyString
                : SystemInfo.deviceUniqueIdentifier;

            using var sha256 = SHA256.Create();
            _cachedAesKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
        }

        private static byte[] PerformAesEncryption(byte[] data, byte[] key)
        {
            byte[] iv = GenerateIv();
            using var aes = CreateAesInstance(key, iv);
            using var encryptor = aes.CreateEncryptor();
            using var msEncrypt = new MemoryStream();

            msEncrypt.Write(iv, 0, iv.Length);
            using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
            {
                csEncrypt.Write(data, 0, data.Length);
                csEncrypt.FlushFinalBlock();
            }

            return msEncrypt.ToArray();
        }

        private static byte[] PerformAesDecryption(byte[] cipher, byte[] key)
        {
            byte[] iv = new byte[IV_SIZE];
            byte[] encryptedData = new byte[cipher.Length - IV_SIZE];

            Array.Copy(cipher, 0, iv, 0, IV_SIZE);
            Array.Copy(cipher, IV_SIZE, encryptedData, 0, encryptedData.Length);

            using var aes = CreateAesInstance(key, iv);
            using var decryptor = aes.CreateDecryptor();
            using var msDecrypt = new MemoryStream(encryptedData);
            using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
            using var msOutput = new MemoryStream();

            csDecrypt.CopyTo(msOutput);
            return msOutput.ToArray();
        }

        private static Aes CreateAesInstance(byte[] key, byte[] iv)
        {
            var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        private static byte[] GenerateIv()
        {
            byte[] iv = new byte[IV_SIZE];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(iv);
            return iv;
        }

        #endregion
    }
}
