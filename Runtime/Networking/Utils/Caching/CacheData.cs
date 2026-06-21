using System;
using System.IO;
using UnityEngine;

namespace Molca.Networking.Utils
{
    [Serializable]
    public class CacheData
    {
        public string path;
        public int version;
        public bool isEncrypted;

        private string _decryptedFilePath { get; set; }

        public CacheData(string cp, int v, bool ie)
        {
            path = cp;
            version = v;
            isEncrypted = ie;
        }

        public async Awaitable<string> GetPath()
        {
            if (path == null)
                return null;

            if (!isEncrypted)
                return path;

            if (!string.IsNullOrEmpty(_decryptedFilePath) && File.Exists(_decryptedFilePath))
                return _decryptedFilePath;

            _decryptedFilePath = Path.ChangeExtension(CacheManager.GetTempPathInternal(), Path.GetExtension(path));
            byte[] data = Encryption.DecryptData(await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(_decryptedFilePath, data);
            return _decryptedFilePath;
        }

        public void Clear()
        {
            if (string.IsNullOrEmpty(_decryptedFilePath))
                return;

            File.Delete(_decryptedFilePath);
            _decryptedFilePath = null;
        }
    }
} 