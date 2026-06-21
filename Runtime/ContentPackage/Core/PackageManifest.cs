using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Handles JSON serialization of package states to a file in <see cref="Application.persistentDataPath"/>
    /// for persistence across application sessions.
    /// Provides methods to load, save, and manage package state data with graceful error handling for corrupted data.
    /// </summary>
    public class PackageManifest
    {
        /// <summary>Legacy PlayerPrefs key — read once during migration, then deleted.</summary>
        private const string LegacyPlayerPrefsKey = "Molca.ContentPackage.Manifest";

        private static string ManifestPath =>
            Path.Combine(Application.persistentDataPath, "Molca", "packages_manifest.json");

        /// <summary>
        /// Internal data structure for JSON serialization of the manifest.
        /// Contains the list of package states and metadata about the manifest itself.
        /// </summary>
        [Serializable]
        private class ManifestData
        {
            /// <summary>
            /// List of all package states tracked by the manifest.
            /// </summary>
            public List<PackageState> packages = new List<PackageState>();

            /// <summary>
            /// Version of the manifest format for future compatibility.
            /// </summary>
            public string version = "1.0.0";

            /// <summary>ISO 8601 timestamp; string because JsonUtility cannot serialize DateTime.</summary>
            public string lastSaved;

            /// <summary>
            /// The content version string currently installed on this device (e.g. <c>"1.1.0"</c>).
            /// Null or empty if no versioned content has been installed yet.
            /// </summary>
            public string installedContentVersion;
        }

        /// <summary>
        /// The internal manifest data containing all package states.
        /// </summary>
        private ManifestData _data;

        /// <summary>
        /// Initializes a new instance of the PackageManifest class and loads existing data from disk.
        /// Migrates legacy PlayerPrefs data on first run if no file exists yet.
        /// </summary>
        public PackageManifest()
        {
            Load();
        }

        /// <summary>
        /// Loads the package manifest from disk. If no file exists, attempts to migrate from
        /// the legacy PlayerPrefs store. Falls back to an empty manifest on any failure.
        /// </summary>
        public void Load()
        {
            string path = ManifestPath;

            if (!File.Exists(path))
            {
                // Attempt one-time migration from PlayerPrefs
                if (PlayerPrefs.HasKey(LegacyPlayerPrefsKey))
                {
                    Debug.Log("[PackageManifest] Migrating manifest from PlayerPrefs to file storage");
                    string legacyJson = PlayerPrefs.GetString(LegacyPlayerPrefsKey, "");
                    _data = TryDeserialize(legacyJson) ?? new ManifestData();
                    PlayerPrefs.DeleteKey(LegacyPlayerPrefsKey);
                    PlayerPrefs.Save();
                    Save(); // Write to file immediately
                }
                else
                {
                    _data = new ManifestData();
                }
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                _data = TryDeserialize(json) ?? new ManifestData();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageManifest] Failed to load manifest from '{path}': {ex.Message}. Creating empty manifest.");
                _data = new ManifestData();
            }
        }

        /// <summary>
        /// Saves the current manifest data to disk as JSON.
        /// Updates the lastSaved timestamp before serialization.
        /// </summary>
        public void Save()
        {
            _data.lastSaved = DateTime.UtcNow.ToString("O");

            try
            {
                string path = ManifestPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(_data, prettyPrint: false));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageManifest] Failed to save manifest: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the state of a specific package by its identifier.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        /// <returns>The PackageState if found, null otherwise.</returns>
        public PackageState GetState(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
                return null;

            return _data.packages.Find(p => p.packageId == packageId);
        }

        /// <summary>
        /// Sets or updates the state of a package. If the package already exists in the manifest,
        /// its state is updated. If it doesn't exist, it's added to the manifest.
        /// The manifest is automatically saved after the state is updated.
        /// </summary>
        /// <param name="state">The package state to set or update.</param>
        public void SetState(PackageState state)
        {
            if (state == null)
            {
                Debug.LogWarning("[PackageManifest] Attempted to set null package state");
                return;
            }

            if (string.IsNullOrEmpty(state.packageId))
            {
                Debug.LogWarning("[PackageManifest] Attempted to set package state with null or empty packageId");
                return;
            }

            int index = _data.packages.FindIndex(p => p.packageId == state.packageId);
            if (index >= 0)
                _data.packages[index] = state;
            else
                _data.packages.Add(state);

            state.lastModified = DateTime.UtcNow.ToString("O");
            Save();
        }

        /// <summary>
        /// Gets a copy of all package states currently tracked by the manifest.
        /// </summary>
        /// <returns>A new list containing copies of all package states.</returns>
        public List<PackageState> GetAllStates()
        {
            return new List<PackageState>(_data.packages);
        }

        /// <summary>
        /// Updates or inserts multiple package states in a single operation and saves once.
        /// Prefer this over calling <see cref="SetState"/> in a loop to avoid N disk writes.
        /// </summary>
        /// <param name="states">The states to set. Null entries are silently skipped.</param>
        public void SetStatesBatch(IEnumerable<PackageState> states)
        {
            if (states == null) return;
            var now = DateTime.UtcNow.ToString("O");
            foreach (var state in states)
            {
                if (state == null || string.IsNullOrEmpty(state.packageId)) continue;
                state.lastModified = now;
                int index = _data.packages.FindIndex(p => p.packageId == state.packageId);
                if (index >= 0)
                    _data.packages[index] = state;
                else
                    _data.packages.Add(state);
            }
            Save();
        }

        /// <summary>
        /// Clears all package states from the manifest and saves the empty manifest to disk.
        /// This operation cannot be undone.
        /// </summary>
        public void Clear()
        {
            _data.packages.Clear();
            Save();
        }

        /// <summary>
        /// Gets or sets the content version string currently installed on this device.
        /// Setting this value immediately persists the manifest to disk.
        /// </summary>
        public string InstalledContentVersion
        {
            get => _data.installedContentVersion;
            set
            {
                _data.installedContentVersion = value;
                Save();
            }
        }

        private static ManifestData TryDeserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var data = JsonUtility.FromJson<ManifestData>(json);
                if (data != null && data.packages == null)
                    data.packages = new List<PackageState>();
                return data;
            }
            catch
            {
                return null;
            }
        }
    }
}
