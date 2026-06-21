using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Molca.Networking.Utils
{
    /// <summary>
    /// Encrypted-at-rest string storage on top of <see cref="PlayerPrefs"/>, used for
    /// persisted credentials (auth tokens, cached user data). Values are AES-256-CBC
    /// encrypted with a key derived from <see cref="SystemInfo.deviceUniqueIdentifier"/>,
    /// so persisted data is unreadable in plaintext and not portable across devices.
    /// </summary>
    /// <remarks>
    /// <b>Security scope (honest limitation):</b> the key is derived from device-unique
    /// but non-secret material — this protects against casual inspection of
    /// PlayerPrefs/registry and against copying values between devices. It is NOT a
    /// hardware keystore; an attacker with code execution on the device can recover the
    /// key. Platform keystore backends (Android Keystore / iOS Keychain) are the
    /// planned upgrade path via the SaveSubsystem roadmap item (Sprint 6.1).
    /// Corrupt or undecryptable entries are treated as absent and deleted.
    /// </remarks>
    public static class SecureStorage
    {
        private const string KEY_SALT = "Molca.SecureStorage.v1";
        private const int IV_SIZE = 16;

        private static byte[] _key;

        private static byte[] Key
        {
            get
            {
                if (_key == null)
                {
                    using var sha256 = SHA256.Create();
                    _key = sha256.ComputeHash(Encoding.UTF8.GetBytes(SystemInfo.deviceUniqueIdentifier + KEY_SALT));
                }
                return _key;
            }
        }

        /// <summary>Encrypts and persists <paramref name="value"/> under <paramref name="key"/>.</summary>
        public static void SaveString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required", nameof(key));
            if (value == null)
            {
                Delete(key);
                return;
            }

            byte[] cipher = Encrypt(Encoding.UTF8.GetBytes(value));
            PlayerPrefs.SetString(key, Convert.ToBase64String(cipher));
        }

        /// <summary>
        /// Loads and decrypts the value stored under <paramref name="key"/>.
        /// </summary>
        /// <returns><c>false</c> when absent, corrupt, or written on another device (the entry is then removed).</returns>
        public static bool TryLoadString(string key, out string value)
        {
            value = null;
            if (!PlayerPrefs.HasKey(key))
                return false;

            try
            {
                byte[] cipher = Convert.FromBase64String(PlayerPrefs.GetString(key));
                value = Encoding.UTF8.GetString(Decrypt(cipher));
                return true;
            }
            catch (Exception)
            {
                // Corrupt/foreign data is unrecoverable — drop it so callers re-authenticate.
                PlayerPrefs.DeleteKey(key);
                return false;
            }
        }

        /// <summary>Whether an entry exists (does not validate decryptability).</summary>
        public static bool HasKey(string key) => PlayerPrefs.HasKey(key);

        /// <summary>Removes the entry, if present.</summary>
        public static void Delete(string key) => PlayerPrefs.DeleteKey(key);

        private static byte[] Encrypt(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            using var output = new MemoryStream();
            output.Write(aes.IV, 0, aes.IV.Length);
            using (var cryptoStream = new CryptoStream(output, encryptor, CryptoStreamMode.Write))
            {
                cryptoStream.Write(data, 0, data.Length);
                cryptoStream.FlushFinalBlock();
            }
            return output.ToArray();
        }

        private static byte[] Decrypt(byte[] cipher)
        {
            if (cipher.Length <= IV_SIZE)
                throw new CryptographicException("Cipher too short");

            byte[] iv = new byte[IV_SIZE];
            Array.Copy(cipher, iv, IV_SIZE);

            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            using var input = new MemoryStream(cipher, IV_SIZE, cipher.Length - IV_SIZE);
            using var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
            using var output = new MemoryStream();
            cryptoStream.CopyTo(output);
            return output.ToArray();
        }
    }
}
