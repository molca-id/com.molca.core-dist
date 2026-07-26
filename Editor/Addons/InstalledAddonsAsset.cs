using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Addons
{
    /// <summary>One add-on installation owned by the Molca Add-on Manager.</summary>
    [Serializable]
    internal sealed class InstalledAddonRecord
    {
        public string id;
        public string name;
        public string version;
        public string sha256;
        public string publisher;
        public string sourceHost;
        public string signingKeyId;
        public string installedAtUtc;
        public bool hasRuntime;
        public bool requested = true;
        public string transactionId;
        public List<string> requiredBy = new List<string>();
        public List<InstalledAddonDependency> dependencies = new List<InstalledAddonDependency>();
        public List<InstalledExternalPrerequisite> externalPrerequisites =
            new List<InstalledExternalPrerequisite>();

        /// <summary>
        /// SHA-256 over the package's sorted <c>.cs</c> files at install time, for detecting post-install
        /// source drift. Empty on records written before this field existed (drift check skipped for those).
        /// </summary>
        public string contentHash;
    }

    [Serializable]
    internal sealed class InstalledAddonDependency
    {
        public string id;
        public string resolvedVersion;
        public string minimumVersion;
        public string maximumMajorExclusive;
    }

    [Serializable]
    internal sealed class InstalledExternalPrerequisite
    {
        public string packageId;
        public string resolvedSpec;
    }

    /// <summary>
    /// Project-local ownership ledger for manager-installed add-ons. This editor-only ScriptableObject lives
    /// under <c>Assets/_Molca/Editor/</c>; runtime code never reads or mutates it.
    /// </summary>
    internal sealed class InstalledAddonsAsset : ScriptableObject
    {
        [SerializeField] private List<InstalledAddonRecord> addons = new List<InstalledAddonRecord>();

        internal IReadOnlyList<InstalledAddonRecord> Addons => addons;

        internal InstalledAddonRecord Find(string id) =>
            addons.Find(item => string.Equals(item.id, id, StringComparison.Ordinal));

        internal void Upsert(InstalledAddonRecord record)
        {
            int index = addons.FindIndex(item => string.Equals(item.id, record.id, StringComparison.Ordinal));
            if (index >= 0) addons[index] = record;
            else addons.Add(record);
            Save();
        }

        internal void UpsertMany(IEnumerable<InstalledAddonRecord> records)
        {
            foreach (InstalledAddonRecord record in records)
            {
                int index = addons.FindIndex(item =>
                    string.Equals(item.id, record.id, StringComparison.Ordinal));
                if (index >= 0) addons[index] = record;
                else addons.Add(record);
            }
            Save();
        }

        internal List<InstalledAddonRecord> Snapshot() =>
            addons.Select(record => JsonUtility.FromJson<InstalledAddonRecord>(
                JsonUtility.ToJson(record))).ToList();

        internal void Restore(IEnumerable<InstalledAddonRecord> records)
        {
            addons = records?.ToList() ?? new List<InstalledAddonRecord>();
            Save();
        }

        internal IReadOnlyList<InstalledAddonRecord> DependentsOf(string id) =>
            addons.Where(record => record.dependencies != null &&
                record.dependencies.Exists(dependency =>
                    string.Equals(dependency.id, id, StringComparison.Ordinal))).ToList();

        internal bool Remove(string id)
        {
            int removed = addons.RemoveAll(item => string.Equals(item.id, id, StringComparison.Ordinal));
            if (removed > 0) Save();
            return removed > 0;
        }

        internal static InstalledAddonsAsset GetOrCreate() =>
            MolcaEditorSettingsAsset.GetOrCreate<InstalledAddonsAsset>("Installed Add-ons.asset");

        internal static InstalledAddonsAsset FindExisting()
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(InstalledAddonsAsset)}"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<InstalledAddonsAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) return asset;
            }
            return null;
        }

        private void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
        }
    }
}
