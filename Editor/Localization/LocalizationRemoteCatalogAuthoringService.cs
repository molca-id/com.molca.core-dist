using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Molca.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Builds bounded remote-catalog publication bundles and repairs the shipped identity allowlist.
    /// </summary>
    public static class LocalizationRemoteCatalogAuthoringService
    {
        /// <summary>Maximum UTF-8 bundle size accepted by protocol v1.</summary>
        public const int MaximumBytes = 4 * 1024 * 1024;
        /// <summary>Maximum localized values accepted by protocol v1.</summary>
        public const int MaximumEntries = 50_000;
        /// <summary>Maximum locales accepted by protocol v1.</summary>
        public const int MaximumLocales = 32;
        /// <summary>Maximum UTF-16 characters accepted for one value.</summary>
        public const int MaximumValueCharacters = 16_384;

        /// <summary>Creates a publication-ready v1 bundle from the current Unity StringTables.</summary>
        public static LocalizationRemoteCatalogExport BuildBundle(
            LocalizationRemoteCatalogSettings settings,
            string version,
            string baseCatalogVersion = "",
            string minAppVersion = "",
            string maxAppVersion = "")
        {
            if (settings == null)
                return LocalizationRemoteCatalogExport.Failure("Remote catalog settings are required.");
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
                return LocalizationRemoteCatalogExport.Failure("Remote catalog project id is required.");
            if (settings.ProjectId.Length > 128)
                return LocalizationRemoteCatalogExport.Failure(
                    "Remote catalog project id exceeds 128 characters.");
            if (settings.Channel is not ("stable" or "beta" or "internal"))
                return LocalizationRemoteCatalogExport.Failure(
                    $"Remote catalog channel '{settings.Channel}' is unsupported.");
            version = version?.Trim() ?? string.Empty;
            if (version.Length == 0 ||
                version.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
                return LocalizationRemoteCatalogExport.Failure(
                    "Version must contain only letters, digits, dot, underscore, or dash.");

            var snapshot = LocalizationCatalogAuthoringService.Capture();
            var populated = snapshot.Cells
                .Where(cell => !cell.IsMissing)
                .OrderBy(cell => cell.CollectionId, StringComparer.Ordinal)
                .ThenBy(cell => cell.EntryId)
                .ThenBy(cell => cell.LocaleCode, StringComparer.Ordinal)
                .ToArray();
            var locales = populated
                .Select(cell => LocalizationModule.CanonicalizeLocaleCode(cell.LocaleCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var defaultLocale = LocalizationCatalogAuthoringService.GetDefaultLocaleCode();
            var placeholderContracts = populated
                .GroupBy(
                    cell => $"{NormalizeCollectionId(cell.CollectionId)}:{cell.EntryId}",
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var source = group.FirstOrDefault(cell =>
                                         string.Equals(
                                             LocalizationModule.CanonicalizeLocaleCode(cell.LocaleCode),
                                             defaultLocale,
                                             StringComparison.OrdinalIgnoreCase))
                                     ?? group.First();
                        return LocalizationPlaceholderUtility.Extract(source.Value);
                    },
                    StringComparer.Ordinal);
            if (populated.Length > MaximumEntries)
                return LocalizationRemoteCatalogExport.Failure(
                    $"Catalog has {populated.Length} values; the remote limit is {MaximumEntries}.");
            if (locales.Length > MaximumLocales)
                return LocalizationRemoteCatalogExport.Failure(
                    $"Catalog has {locales.Length} locales; the remote limit is {MaximumLocales}.");

            var entries = new JArray();
            foreach (var cell in populated)
            {
                var normalizedCollectionId = NormalizeCollectionId(cell.CollectionId);
                if (normalizedCollectionId.Length != 32 ||
                    normalizedCollectionId.Any(character =>
                        !Uri.IsHexDigit(character)))
                    return LocalizationRemoteCatalogExport.Failure(
                        $"'{cell.CollectionName}' does not have a valid stable collection id.");
                if (cell.Value.Length > MaximumValueCharacters)
                    return LocalizationRemoteCatalogExport.Failure(
                        $"'{cell.Key}' [{cell.LocaleCode}] exceeds {MaximumValueCharacters} characters.");
                var placeholders = LocalizationPlaceholderUtility.Extract(cell.Value);
                if (!placeholderContracts[
                        $"{normalizedCollectionId}:{cell.EntryId}"].SetEquals(placeholders))
                    return LocalizationRemoteCatalogExport.Failure(
                        $"'{cell.Key}' [{cell.LocaleCode}] changes its source placeholder contract.");
                entries.Add(new JObject
                {
                    ["collectionId"] = normalizedCollectionId,
                    ["entryId"] = cell.EntryId.ToString(),
                    ["locale"] = LocalizationModule.CanonicalizeLocaleCode(cell.LocaleCode),
                    ["placeholders"] = new JArray(placeholders),
                    ["value"] = cell.Value,
                });
            }

            var bundle = new JObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "molca.localization.bundle",
                ["projectId"] = settings.ProjectId,
                ["channel"] = settings.Channel.ToLowerInvariant(),
                ["version"] = version,
                ["baseCatalogVersion"] = baseCatalogVersion?.Trim() ?? string.Empty,
                ["createdAt"] = DateTime.UtcNow.ToString("O"),
                ["minAppVersion"] = minAppVersion?.Trim() ?? string.Empty,
                ["maxAppVersion"] = maxAppVersion?.Trim() ?? string.Empty,
                ["entries"] = entries,
            };
            var json = bundle.ToString(Formatting.None);
            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaximumBytes)
                return LocalizationRemoteCatalogExport.Failure(
                    $"Bundle is {byteCount} bytes; the remote limit is {MaximumBytes}.");
            return LocalizationRemoteCatalogExport.Success(
                json,
                snapshot.SourceFingerprint,
                populated.Length,
                locales.Length,
                byteCount);
        }

        /// <summary>
        /// Replaces the settings allowlist from current stable identities and source-locale placeholders.
        /// </summary>
        public static int SyncAllowlist(LocalizationRemoteCatalogSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            var snapshot = LocalizationCatalogAuthoringService.Capture();
            var defaultLocale = LocalizationCatalogAuthoringService.GetDefaultLocaleCode();
            var identities = snapshot.Cells
                .GroupBy(
                    cell => $"{NormalizeCollectionId(cell.CollectionId)}:{cell.EntryId}",
                    StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var source = group.FirstOrDefault(cell =>
                                     string.Equals(
                                         LocalizationModule.CanonicalizeLocaleCode(cell.LocaleCode),
                                         defaultLocale,
                                         StringComparison.OrdinalIgnoreCase) &&
                                     !cell.IsMissing)
                                 ?? group.FirstOrDefault(cell => !cell.IsMissing)
                                 ?? group.First();
                    return new
                    {
                        source.CollectionId,
                        source.EntryId,
                        Placeholders = LocalizationPlaceholderUtility.Extract(source.Value).ToArray(),
                    };
                })
                .ToArray();

            Undo.RecordObject(settings, "Sync Localization Remote Allowlist");
            var serialized = new SerializedObject(settings);
            var allowedEntries = serialized.FindProperty("allowedEntries");
            allowedEntries.arraySize = identities.Length;
            for (var index = 0; index < identities.Length; index++)
            {
                var target = allowedEntries.GetArrayElementAtIndex(index);
                target.FindPropertyRelative("CollectionId").stringValue =
                    NormalizeCollectionId(identities[index].CollectionId);
                target.FindPropertyRelative("EntryId").longValue = identities[index].EntryId;
                var placeholders = target.FindPropertyRelative("Placeholders");
                placeholders.arraySize = identities[index].Placeholders.Length;
                for (var placeholderIndex = 0;
                     placeholderIndex < identities[index].Placeholders.Length;
                     placeholderIndex++)
                    placeholders.GetArrayElementAtIndex(placeholderIndex).stringValue =
                        identities[index].Placeholders[placeholderIndex];
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            return identities.Length;
        }

        private static string NormalizeCollectionId(string value) =>
            (value ?? string.Empty).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>Result of building one remote-catalog publication bundle.</summary>
    public sealed class LocalizationRemoteCatalogExport
    {
        /// <summary>Whether bundle construction and bounds validation succeeded.</summary>
        public bool Succeeded { get; private set; }
        /// <summary>Validation failure detail, or empty on success.</summary>
        public string Error { get; private set; }
        /// <summary>Publication-ready UTF-8 JSON.</summary>
        public string Json { get; private set; }
        /// <summary>Fingerprint of the source Unity catalog.</summary>
        public string SourceFingerprint { get; private set; }
        /// <summary>Number of localized values exported.</summary>
        public int EntryCount { get; private set; }
        /// <summary>Number of distinct locales exported.</summary>
        public int LocaleCount { get; private set; }
        /// <summary>UTF-8 byte size before server canonicalization.</summary>
        public int SizeBytes { get; private set; }

        internal static LocalizationRemoteCatalogExport Success(
            string json,
            string sourceFingerprint,
            int entryCount,
            int localeCount,
            int sizeBytes) =>
            new()
            {
                Succeeded = true,
                Json = json,
                SourceFingerprint = sourceFingerprint,
                EntryCount = entryCount,
                LocaleCount = localeCount,
                SizeBytes = sizeBytes,
            };

        internal static LocalizationRemoteCatalogExport Failure(string error) =>
            new() { Error = error ?? "Remote catalog export failed." };
    }
}
