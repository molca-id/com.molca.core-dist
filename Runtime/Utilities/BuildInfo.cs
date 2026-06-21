using UnityEngine;

namespace Molca
{
    /// <summary>
    /// Serializable build provenance written at build time and read back at runtime. Authored by the
    /// editor build pipeline and deserialized by <see cref="BuildInfo"/>.
    /// </summary>
    [System.Serializable]
    public class MolcaBuildInfoData
    {
        /// <summary>Version string in Major.Minor.Patch form.</summary>
        public string version;
        /// <summary>Build number string.</summary>
        public string buildNumber;
        /// <summary>Short git commit hash at build time.</summary>
        public string commit;
        /// <summary>Git branch name at build time.</summary>
        public string branch;
        /// <summary>UTC build timestamp (ISO 8601).</summary>
        public string timestampUtc;
    }

    /// <summary>
    /// Runtime access to the build provenance captured when the player was built: version, build
    /// number, git commit/branch, and timestamp.
    /// </summary>
    /// <remarks>
    /// Backed by a generated <c>Resources/MolcaBuildInfo</c> TextAsset that the build pipeline writes
    /// before the build and removes afterwards, so it normally exists only inside player builds. In
    /// the editor (or any build where it was not generated) <see cref="IsAvailable"/> is <c>false</c>
    /// and the string accessors fall back to <see cref="Application.version"/> where sensible.
    /// </remarks>
    public static class BuildInfo
    {
        /// <summary>The Resources path (without extension) of the generated build-info asset.</summary>
        public const string ResourceName = "MolcaBuildInfo";

        private static bool _loaded;
        private static MolcaBuildInfoData _data;

        private static MolcaBuildInfoData Data
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    var asset = Resources.Load<TextAsset>(ResourceName);
                    if (asset != null && !string.IsNullOrEmpty(asset.text))
                    {
                        try { _data = JsonUtility.FromJson<MolcaBuildInfoData>(asset.text); }
                        catch { _data = null; }
                    }
                }
                return _data;
            }
        }

        /// <summary>True when build provenance was embedded in this build.</summary>
        public static bool IsAvailable => Data != null;

        /// <summary>Version string (e.g. "1.4.0"), falling back to <see cref="Application.version"/>.</summary>
        public static string Version => Data != null ? Data.version : Application.version;

        /// <summary>Build number string, or empty when unavailable.</summary>
        public static string BuildNumber => Data != null ? Data.buildNumber : string.Empty;

        /// <summary>Short git commit hash, or empty when unavailable.</summary>
        public static string Commit => Data != null ? Data.commit : string.Empty;

        /// <summary>Git branch name, or empty when unavailable.</summary>
        public static string Branch => Data != null ? Data.branch : string.Empty;

        /// <summary>UTC build timestamp (ISO 8601), or empty when unavailable.</summary>
        public static string TimestampUtc => Data != null ? Data.timestampUtc : string.Empty;

        /// <summary>Clears the cached data so the next access reloads. Intended for editor/test use.</summary>
        public static void ClearCache()
        {
            _loaded = false;
            _data = null;
        }
    }
}
