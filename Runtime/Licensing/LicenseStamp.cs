using UnityEngine;

namespace Molca.Licensing
{
    /// <summary>
    /// Reads the license stamp baked into a player build. Returns null in the editor and in any build
    /// produced without an authorized license, which is what makes every stamp-driven feature
    /// (runtime heartbeat, control-plane usage reporting) inert by default.
    /// </summary>
    /// <remarks>
    /// The stamp is written to <c>Assets/Resources/MolcaLicenseStamp.json</c> during build pre-process
    /// and deleted during post-process, so <see cref="Resources.Load"/> only finds it inside a player.
    /// Loaded once per run and cached: several subsystems consult it during bootstrap.
    /// </remarks>
    public static class LicenseStamp
    {
        private const string ResourceName = "MolcaLicenseStamp";

        private static bool _loaded;
        private static LicenseStampData _stamp;

        /// <summary>The build's license stamp, or <c>null</c> when this is not a stamped player build.</summary>
        public static LicenseStampData Current
        {
            get
            {
                if (_loaded) return _stamp;
                _loaded = true;
                var asset = Resources.Load<TextAsset>(ResourceName);
                if (asset == null) return _stamp = null;
                try { _stamp = JsonUtility.FromJson<LicenseStampData>(asset.text); }
                catch { _stamp = null; }
                if (_stamp != null && string.IsNullOrEmpty(_stamp.licenseeId)) _stamp = null;
                return _stamp;
            }
        }
    }
}
