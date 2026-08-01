using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>One schema upgrade step: takes a catalog from <see cref="FromVersion"/> to that plus one.</summary>
    /// <remarks>
    /// Steps are pure functions of the serialized asset. They never read secrets, never touch the
    /// network, and must be safe to run twice — a rerun of an already-applied step is a no-op, not a
    /// second application.
    /// </remarks>
    internal interface INetworkCatalogSchemaStep
    {
        /// <summary>The schema version this step upgrades from.</summary>
        int FromVersion { get; }

        /// <summary>What the step changes, for the migration preview.</summary>
        string Describe(NetworkCatalog catalog);

        /// <summary>
        /// Applies the upgrade to a serialized catalog. The caller stamps the new schema version and
        /// applies the <see cref="SerializedObject"/>.
        /// </summary>
        /// <param name="serialized">The catalog's serialized object, already open.</param>
        /// <returns>Human-readable notes about what changed.</returns>
        IReadOnlyList<string> Apply(SerializedObject serialized);
    }

    /// <summary>The outcome of a schema migration, whether previewed or applied.</summary>
    public sealed class NetworkSchemaMigrationReport
    {
        /// <summary>Schema version the catalog was at before migration.</summary>
        public int FromVersion { get; internal set; }

        /// <summary>Schema version the catalog is at after migration.</summary>
        public int ToVersion { get; internal set; }

        /// <summary>Whether any change was needed.</summary>
        public bool ChangesRequired => FromVersion < ToVersion;

        /// <summary>Whether the changes were written to the asset, as opposed to previewed.</summary>
        public bool Applied { get; internal set; }

        /// <summary>Per-step notes describing what changed or would change.</summary>
        public IReadOnlyList<string> Notes { get; internal set; } = Array.Empty<string>();

        /// <summary>Why migration could not proceed, or empty on success.</summary>
        public string BlockedReason { get; internal set; } = string.Empty;

        /// <summary>Whether migration was refused.</summary>
        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);

        /// <summary>A one-line summary for logs and the Hub.</summary>
        public string Summarize()
        {
            if (IsBlocked) return $"Blocked: {BlockedReason}";
            if (!ChangesRequired) return $"Already at schema version {ToVersion}.";

            string verb = Applied ? "Migrated" : "Would migrate";
            return $"{verb} schema {FromVersion} → {ToVersion} ({Notes.Count} change(s)).";
        }
    }

    /// <summary>
    /// Upgrades a <see cref="NetworkCatalog"/> asset from an older serialized schema to
    /// <see cref="NetworkCatalog.CurrentSchemaVersion"/>.
    /// </summary>
    /// <remarks>
    /// Deterministic, previewable, and rerunnable. Runs as an editor transaction under a single Undo
    /// group and records provenance on the asset, so an interrupted migration leaves a state that can
    /// be re-entered rather than a half-upgraded catalog.
    /// <para>
    /// Refuses to write to assets in packages or other read-only locations: a consumer project must
    /// not have its immutable package contents rewritten by an upgrade.
    /// </para>
    /// <para>
    /// Version 1 is the initial schema, so no steps are registered yet. The infrastructure ships now
    /// so that the first real schema change is a step registration and a fixture, not a scramble.
    /// </para>
    /// </remarks>
    public static class NetworkCatalogSchemaMigrator
    {
        private const string FieldSchemaVersion = "_schemaVersion";

        /// <summary>
        /// Upgrade steps in ascending order of <see cref="INetworkCatalogSchemaStep.FromVersion"/>.
        /// </summary>
        private static readonly INetworkCatalogSchemaStep[] Steps = Array.Empty<INetworkCatalogSchemaStep>();

        /// <summary>
        /// Reports what migrating <paramref name="catalog"/> would do, without modifying anything.
        /// </summary>
        /// <param name="catalog">The catalog to inspect.</param>
        /// <returns>The preview report.</returns>
        public static NetworkSchemaMigrationReport Preview(NetworkCatalog catalog)
        {
            var report = NewReport(catalog);
            if (report.IsBlocked || !report.ChangesRequired)
                return report;

            var notes = new List<string>();
            foreach (var step in StepsFrom(catalog.SchemaVersion))
                notes.Add($"v{step.FromVersion} → v{step.FromVersion + 1}: {step.Describe(catalog)}");

            report.Notes = notes;
            return report;
        }

        /// <summary>
        /// Migrates <paramref name="catalog"/> to <see cref="NetworkCatalog.CurrentSchemaVersion"/>.
        /// </summary>
        /// <param name="catalog">The catalog to upgrade.</param>
        /// <returns>
        /// The report. When <see cref="NetworkSchemaMigrationReport.ChangesRequired"/> is <c>false</c>
        /// the asset is untouched, which makes a rerun safe.
        /// </returns>
        public static NetworkSchemaMigrationReport Migrate(NetworkCatalog catalog)
        {
            var report = NewReport(catalog);
            if (report.IsBlocked || !report.ChangesRequired)
                return report;

            var notes = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Migrate Network Catalog Schema");

            var serialized = new SerializedObject(catalog);
            int version = catalog.SchemaVersion;

            foreach (var step in StepsFrom(version))
            {
                notes.AddRange(step.Apply(serialized));
                version = step.FromVersion + 1;
                serialized.FindProperty(FieldSchemaVersion).intValue = version;
            }

            serialized.ApplyModifiedProperties();

            // Record where the asset came from so a later upgrade — or a support question — can tell
            // an authored catalog from a migrated one.
            catalog.SetMigrationProvenance(report.FromVersion, AssetPathGuid(catalog));
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssetIfDirty(catalog);

            Undo.CollapseUndoOperations(undoGroup);

            report.Applied = true;
            report.Notes = notes;
            report.ToVersion = version;
            return report;
        }

        /// <summary>
        /// Migrates every catalog in the project that needs it.
        /// </summary>
        /// <returns>One report per catalog that required changes.</returns>
        public static List<NetworkSchemaMigrationReport> MigrateAll()
        {
            var reports = new List<NetworkSchemaMigrationReport>();

            foreach (var catalog in Authoring.NetworkCatalogLocator.FindAllCatalogs())
            {
                if (!catalog.RequiresSchemaMigration)
                    continue;

                reports.Add(Migrate(catalog));
            }
            return reports;
        }

        private static NetworkSchemaMigrationReport NewReport(NetworkCatalog catalog)
        {
            var report = new NetworkSchemaMigrationReport
            {
                FromVersion = catalog?.SchemaVersion ?? 0,
                ToVersion = NetworkCatalog.CurrentSchemaVersion
            };

            if (catalog == null)
            {
                report.BlockedReason = "No catalog was supplied.";
                return report;
            }

            if (catalog.SchemaVersion > NetworkCatalog.CurrentSchemaVersion)
            {
                // A newer asset than this framework understands. Downgrading would silently drop
                // fields, so refuse rather than corrupt.
                report.BlockedReason =
                    $"Catalog schema version {catalog.SchemaVersion} is newer than this framework " +
                    $"supports ({NetworkCatalog.CurrentSchemaVersion}). Update the Molca package.";
                report.ToVersion = catalog.SchemaVersion;
                return report;
            }

            string path = AssetDatabase.GetAssetPath(catalog);
            if (!string.IsNullOrEmpty(path) && IsReadOnlyLocation(path))
            {
                report.BlockedReason =
                    $"'{path}' is in a read-only location. Copy the catalog into the project before migrating.";
            }
            return report;
        }

        private static IEnumerable<INetworkCatalogSchemaStep> StepsFrom(int version)
        {
            for (int v = version; v < NetworkCatalog.CurrentSchemaVersion; v++)
            {
                var step = FindStep(v);
                if (step == null)
                {
                    Debug.LogError(
                        $"[Network] No schema migration step registered for version {v}. " +
                        "Catalogs at that version cannot be upgraded.");
                    yield break;
                }
                yield return step;
            }
        }

        private static INetworkCatalogSchemaStep FindStep(int fromVersion)
        {
            foreach (var step in Steps)
            {
                if (step.FromVersion == fromVersion)
                    return step;
            }
            return null;
        }

        /// <summary>
        /// Whether an asset path is somewhere migration must not write — a package, or Unity's own
        /// immutable folders.
        /// </summary>
        /// <param name="assetPath">The asset path to test.</param>
        private static bool IsReadOnlyLocation(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                // An embedded package under Packages/ on disk is writable; a resolved one in the
                // package cache is not. PackageInfo knows the difference.
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                return package == null || package.source != UnityEditor.PackageManager.PackageSource.Embedded;
            }

            return assetPath.StartsWith("Library/", StringComparison.Ordinal);
        }

        private static string AssetPathGuid(NetworkCatalog catalog)
        {
            string path = AssetDatabase.GetAssetPath(catalog);
            return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }
    }
}
