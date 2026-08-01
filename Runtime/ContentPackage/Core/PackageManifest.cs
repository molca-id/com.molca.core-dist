using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Durable store for package install state.
    ///
    /// Every write is atomic and keeps a last-known-good backup, and every write reports whether it
    /// succeeded. Both matter more here than they look: this file is the only record that installed
    /// content exists. Losing it does not lose the bytes on disk, it loses the knowledge that they
    /// are there — the player sees every package as uninstalled while the cache still holds them,
    /// and re-downloads content they already have.
    ///
    /// The previous implementation swallowed save failures and returned void, so a full disk or a
    /// permissions error was indistinguishable from success right up until the next launch.
    /// </summary>
    public class PackageManifest : IPackageStateStore
    {
        /// <summary>Legacy PlayerPrefs key — read during migration, deleted only after a verified file commit.</summary>
        private const string LegacyPlayerPrefsKey = "Molca.ContentPackage.Manifest";

        /// <summary>Current on-disk schema. 1 predates the orthogonal install/operation/update records.</summary>
        public const int CurrentSchemaVersion = 2;

        private static string Directory_ => Path.Combine(Application.persistentDataPath, "Molca");
        private static string ManifestPath => Path.Combine(Directory_, "packages_manifest.json");
        private static string BackupPath => ManifestPath + ".bak";

        /// <summary>How <see cref="PackageManifest"/> came to hold the state it holds.</summary>
        public enum LoadOutcome
        {
            /// <summary>No manifest and no legacy data: a first run.</summary>
            Fresh,

            /// <summary>The primary file loaded cleanly.</summary>
            Loaded,

            /// <summary>The primary was missing or unreadable and the backup was used.</summary>
            RestoredFromBackup,

            /// <summary>State was migrated from the legacy PlayerPrefs store.</summary>
            MigratedFromPlayerPrefs,

            /// <summary>Schema was upgraded from an older version.</summary>
            MigratedSchema,

            /// <summary>Primary and backup were both unusable. State was lost, not merely unread.</summary>
            Lost
        }

        [Serializable]
        private class ManifestData
        {
            public List<PackageState> packages = new List<PackageState>();

            /// <summary>Schema version of this document. Absent (0) in schema 1 files.</summary>
            public int schemaVersion;

            /// <summary>Retained for diagnostics; superseded by <see cref="schemaVersion"/>.</summary>
            public string version = "1.0.0";

            /// <summary>ISO 8601; string because JsonUtility cannot serialize DateTime.</summary>
            public string lastSaved;

            /// <summary>The content release version installed on this device. Empty if none.</summary>
            public string installedContentVersion;

            /// <summary>The content release ID installed on this device. Empty if none.</summary>
            public string installedReleaseId;

            /// <summary>Active and staged release records. Absent in documents written before schema 2.</summary>
            public ReleaseActivationRecord activation = new ReleaseActivationRecord();
        }

        private ManifestData _data;

        /// <summary>How the current state was obtained. Surfaced so callers can report a real data loss.</summary>
        public LoadOutcome Outcome { get; private set; }

        /// <summary>Diagnostic detail for <see cref="Outcome"/>, empty when unremarkable.</summary>
        public string OutcomeDetail { get; private set; } = string.Empty;

        /// <summary>True when the last <see cref="Save"/> committed to disk.</summary>
        public bool LastSaveSucceeded { get; private set; } = true;

        /// <summary>Initializes the manifest and loads existing state.</summary>
        public PackageManifest()
        {
            Load();
        }

        /// <summary>
        /// Loads state, preferring the primary file, falling back to the backup, and only then to
        /// empty. Falling straight to empty on a corrupt primary — as the previous implementation
        /// did — silently discards every install record the backup still holds.
        /// </summary>
        public void Load()
        {
            OutcomeDetail = string.Empty;

            var primary = TryLoadFile(ManifestPath, out string primaryError);
            if (primary != null)
            {
                Adopt(primary, LoadOutcome.Loaded);
                return;
            }

            bool primaryExisted = File.Exists(ManifestPath);

            var backup = TryLoadFile(BackupPath, out string backupError);
            if (backup != null)
            {
                Adopt(backup, LoadOutcome.RestoredFromBackup);
                OutcomeDetail = primaryExisted
                    ? $"primary unreadable ({primaryError}); restored {backup.packages.Count} package(s) from backup"
                    : "primary missing; restored from backup";
                Debug.LogWarning($"[PackageManifest] {OutcomeDetail}");
                // Re-establish the primary so the next launch is not also a restore.
                Save();
                return;
            }

            if (primaryExisted || File.Exists(BackupPath))
            {
                _data = new ManifestData { schemaVersion = CurrentSchemaVersion };
                Outcome = LoadOutcome.Lost;
                OutcomeDetail = $"primary ({primaryError}) and backup ({backupError}) both unusable";
                // Loud, because this is real data loss: installed content will be re-downloaded.
                Debug.LogError($"[PackageManifest] {OutcomeDetail}. Install state has been lost.");
                return;
            }

            if (PlayerPrefs.HasKey(LegacyPlayerPrefsKey))
            {
                var legacy = TryDeserialize(PlayerPrefs.GetString(LegacyPlayerPrefsKey, ""));
                if (legacy != null)
                {
                    Adopt(legacy, LoadOutcome.MigratedFromPlayerPrefs);
                    // Commit to the file BEFORE dropping the only other copy. The previous code
                    // deleted the key first and saved after, so a failed save lost everything.
                    if (Save() && File.Exists(ManifestPath))
                    {
                        PlayerPrefs.DeleteKey(LegacyPlayerPrefsKey);
                        PlayerPrefs.Save();
                        OutcomeDetail = $"migrated {_data.packages.Count} package(s) from PlayerPrefs";
                    }
                    else
                    {
                        OutcomeDetail = "migrated from PlayerPrefs in memory; file commit failed, legacy key retained";
                        Debug.LogWarning($"[PackageManifest] {OutcomeDetail}");
                    }
                    return;
                }
            }

            _data = new ManifestData { schemaVersion = CurrentSchemaVersion };
            Outcome = LoadOutcome.Fresh;
        }

        private void Adopt(ManifestData data, LoadOutcome outcome)
        {
            _data = data;
            Outcome = outcome;

            if (_data.schemaVersion < CurrentSchemaVersion)
            {
                foreach (var state in _data.packages)
                    state?.MigrateFromLegacyStatus();
                _data.schemaVersion = CurrentSchemaVersion;
                if (outcome == LoadOutcome.Loaded)
                {
                    Outcome = LoadOutcome.MigratedSchema;
                    OutcomeDetail = $"upgraded {_data.packages.Count} package(s) to schema {CurrentSchemaVersion}";
                }
                Save();
            }
        }

        private static ManifestData TryLoadFile(string path, out string error)
        {
            error = string.Empty;
            if (!File.Exists(path)) { error = "missing"; return null; }
            try
            {
                var data = TryDeserialize(File.ReadAllText(path));
                if (data == null) { error = "unparseable"; return null; }
                return data;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Writes the manifest atomically, rotating the previous good copy to the backup.
        /// </summary>
        /// <returns>True when the write reached disk. Callers must not treat an install as complete on false.</returns>
        public bool Save()
        {
            _data.lastSaved = DateTime.UtcNow.ToString("O");
            _data.schemaVersion = CurrentSchemaVersion;

            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                string path = ManifestPath;
                string tempPath = path + ".tmp";

                // Serialize and verify before touching anything durable: writing a document that
                // cannot be read back would poison both copies on the next rotation.
                string json = JsonUtility.ToJson(_data, prettyPrint: false);
                if (TryDeserialize(json) == null)
                {
                    LastSaveSucceeded = false;
                    Debug.LogError("[PackageManifest] Refusing to save: serialized manifest does not round-trip.");
                    return false;
                }

                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                {
                    try
                    {
                        // File.Replace does the rotation and the swap in one step.
                        File.Replace(tempPath, path, BackupPath);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // Android and some other platforms lack atomic replace. Copy the current
                        // good file to the backup first, so a failure between the delete and the
                        // move still leaves a recoverable copy.
                        try { File.Copy(path, BackupPath, overwrite: true); } catch { /* backup is best-effort */ }
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }

                LastSaveSucceeded = true;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveSucceeded = false;
                Debug.LogError($"[PackageManifest] Failed to save manifest: {ex.Message}");
                return false;
            }
        }

        /// <summary>Gets the state of a package, or null when it is not tracked.</summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        public PackageState GetState(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            return _data.packages.Find(p => p.packageId == packageId);
        }

        /// <summary>Sets or updates a package state and persists immediately.</summary>
        /// <param name="state">The state to store.</param>
        /// <returns>True when the change reached disk.</returns>
        public bool SetState(PackageState state)
        {
            if (state == null)
            {
                Debug.LogWarning("[PackageManifest] Attempted to set null package state");
                return false;
            }
            if (string.IsNullOrEmpty(state.packageId))
            {
                Debug.LogWarning("[PackageManifest] Attempted to set package state with null or empty packageId");
                return false;
            }

            // Migrate rather than merely recompute: a state built by hand or loaded from an older
            // document may carry only the legacy `status`, and recomputing would overwrite it from
            // empty records -- silently discarding what the caller asked to store. Migration is a
            // no-op once the records hold the truth.
            state.MigrateFromLegacyStatus();
            int index = _data.packages.FindIndex(p => p.packageId == state.packageId);
            if (index >= 0) _data.packages[index] = state;
            else _data.packages.Add(state);

            return Save();
        }

        /// <summary>Gets a shallow copy of the tracked states.</summary>
        public List<PackageState> GetAllStates()
        {
            return new List<PackageState>(_data.packages);
        }

        /// <summary>Sets many states and saves once, rather than once per state.</summary>
        /// <param name="states">The states to store. Null entries are skipped.</param>
        /// <returns>True when the change reached disk.</returns>
        public bool SetStatesBatch(IEnumerable<PackageState> states)
        {
            if (states == null) return true;
            foreach (var state in states)
            {
                if (state == null || string.IsNullOrEmpty(state.packageId)) continue;
                state.MigrateFromLegacyStatus();
                int index = _data.packages.FindIndex(p => p.packageId == state.packageId);
                if (index >= 0) _data.packages[index] = state;
                else _data.packages.Add(state);
            }
            return Save();
        }

        /// <summary>Removes every tracked state. Cannot be undone.</summary>
        /// <returns>True when the change reached disk.</returns>
        public bool Clear()
        {
            _data.packages.Clear();
            return Save();
        }

        /// <summary>The content release version installed on this device.</summary>
        public string InstalledContentVersion
        {
            get => _data.installedContentVersion;
            set { _data.installedContentVersion = value; Save(); }
        }

        /// <summary>The content release ID installed on this device, recorded alongside its version.</summary>
        public string InstalledReleaseId
        {
            get => _data.installedReleaseId;
            set { _data.installedReleaseId = value; Save(); }
        }

        /// <inheritdoc/>
        public ReleaseActivationRecord Activation => _data.activation ??= new ReleaseActivationRecord();

        /// <inheritdoc/>
        public bool SetActivation(ReleaseActivationRecord record)
        {
            if (record == null) return false;
            _data.activation = record;
            return Save();
        }

        private static ManifestData TryDeserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var data = JsonUtility.FromJson<ManifestData>(json);
                if (data == null) return null;
                data.packages ??= new List<PackageState>();
                // A schema-1 document has no activation record. Absent reads as "nothing has
                // activated", which is exactly right for a device that predates release activation.
                data.activation ??= new ReleaseActivationRecord();
                foreach (var state in data.packages)
                {
                    if (state == null) continue;
                    state.install ??= new PackageState.InstallRecord();
                    state.operation ??= new PackageState.OperationRecord();
                    state.update ??= new PackageState.UpdateRecord();
                }
                return data;
            }
            catch
            {
                return null;
            }
        }
    }
}
