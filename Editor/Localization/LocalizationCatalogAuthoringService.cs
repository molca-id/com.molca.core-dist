using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Molca.Localization;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Tables;

namespace Molca.Editor
{
    /// <summary>
    /// Shared stable-identity StringTable authoring and CSV round-trip service.
    /// Hub and MCP remain thin consumers of these previewed transactions.
    /// </summary>
    public static class LocalizationCatalogAuthoringService
    {
        public const string CsvSchema = "molca.localization.catalog.v1";

        private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
        private const int MaxRememberedPlans = 32;
        private const int MaxCsvCharacters = 10 * 1024 * 1024;
        private const int MaxCsvRows = 250_000;
        private static readonly Dictionary<string, LocalizationCatalogEditPlan> EditPlans = new();
        private static readonly Queue<string> EditPlanOrder = new();
        private static readonly Dictionary<string, LocalizationCatalogImportPlan> ImportPlans = new();
        private static readonly Queue<string> ImportPlanOrder = new();

        /// <summary>Captures a deterministic matrix of stable collection, entry, and locale identities.</summary>
        public static LocalizationCatalogSnapshot Capture()
        {
            var audit = LocalizationAuditEngine.Audit(LocalizationAuditRequest.CreateDoctorRequest());
            var cells = new List<LocalizationCatalogCell>();
            var warnings = new List<string>();
            foreach (var collection in GetCollections())
            {
                var collectionId = GetCollectionId(collection);
                var sharedPath = AssetDatabase.GetAssetPath(collection.SharedData);
                foreach (var table in collection.StringTables
                             .Where(table => table != null)
                             .OrderBy(table => table.LocaleIdentifier.Code, StringComparer.Ordinal))
                {
                    var tablePath = AssetDatabase.GetAssetPath(table);
                    var isReadOnly = !IsWritableAssetPath(tablePath) ||
                                     !IsWritableAssetPath(sharedPath);
                    foreach (var sharedEntry in collection.SharedData.Entries
                                 .Where(entry => entry != null)
                                 .OrderBy(entry => entry.Id))
                    {
                        var entry = table.GetEntry(sharedEntry.Id);
                        cells.Add(new LocalizationCatalogCell(
                            collectionId,
                            collection.TableCollectionName,
                            sharedEntry.Id,
                            sharedEntry.Key,
                            table.LocaleIdentifier.Code,
                            entry?.Value,
                            entry?.IsSmart == true,
                            tablePath,
                            isReadOnly));
                    }
                }

                if (!IsWritableAssetPath(sharedPath))
                    warnings.Add(
                        $"Collection '{collection.TableCollectionName}' is package-owned/read-only.");
            }

            return new LocalizationCatalogSnapshot(
                audit.CatalogFingerprint,
                cells,
                warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }

        /// <summary>Previews one catalog-cell add or update without changing project assets.</summary>
        public static LocalizationCatalogEditPlan PreviewEdit(
            string collectionId,
            long entryId,
            string key,
            string localeCode,
            string value)
        {
            var audit = LocalizationAuditEngine.Audit(LocalizationAuditRequest.CreateDoctorRequest());
            var collection = FindCollection(collectionId);
            var canonicalCollectionId = collection == null
                ? collectionId?.Trim() ?? string.Empty
                : GetCollectionId(collection);
            var plan = new LocalizationCatalogEditPlan(
                audit.CatalogFingerprint,
                canonicalCollectionId,
                entryId,
                key?.Trim(),
                CanonicalizeLocale(localeCode),
                value);

            if (collection == null)
            {
                plan.AddError(
                    $"StringTable collection '{collectionId}' was not found. Use its stable collection id.");
                Remember(plan);
                return plan;
            }

            plan.Collection = collection;
            var sharedPath = AssetDatabase.GetAssetPath(collection.SharedData);
            var table = collection.GetTable(plan.LocaleCode) as StringTable;
            if (table == null)
            {
                plan.AddError(
                    $"Collection '{collection.TableCollectionName}' has no table for locale '{plan.LocaleCode}'. " +
                    "Repair the locale transaction before editing values.");
                Remember(plan);
                return plan;
            }

            plan.Table = table;
            var tablePath = AssetDatabase.GetAssetPath(table);
            if (!IsWritableAssetPath(sharedPath) || !IsWritableAssetPath(tablePath))
                plan.AddError(
                    $"The target is package-owned/read-only ({tablePath}). Create an Assets-owned override.");

            SharedTableData.SharedTableEntry sharedEntry = null;
            if (entryId > 0)
            {
                sharedEntry = collection.SharedData.GetEntry(entryId);
                if (sharedEntry == null)
                    plan.AddError(
                        $"Entry id '{entryId}' does not exist in collection '{collection.TableCollectionName}'.");
                else if (!string.IsNullOrEmpty(plan.Key) &&
                         !string.Equals(sharedEntry.Key, plan.Key, StringComparison.Ordinal))
                    plan.AddError(
                        $"Entry id '{entryId}' is key '{sharedEntry.Key}', not '{plan.Key}'. " +
                        "Stable identity and key disagree.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(plan.Key))
                    plan.AddError("A non-blank key is required when creating a catalog entry.");
                else
                    sharedEntry = collection.SharedData.GetEntry(plan.Key);

                if (sharedEntry == null)
                {
                    plan.CreatesEntry = true;
                    plan.AddChange(
                        $"Create key '{plan.Key}' in '{collection.TableCollectionName}' with a generated stable id.");
                }
                else
                {
                    plan.EntryId = sharedEntry.Id;
                }
            }

            if (sharedEntry != null)
            {
                plan.EntryId = sharedEntry.Id;
                var current = table.GetEntry(sharedEntry.Id);
                plan.PreviousValue = current?.Value ?? string.Empty;
                plan.CreatesLocaleCell = current == null;
                if (string.Equals(plan.PreviousValue, plan.Value, StringComparison.Ordinal))
                    plan.AddWarning("The requested value already matches the catalog; no mutation is needed.");
                else
                    plan.AddChange(
                        $"{(current == null ? "Add" : "Update")} '{sharedEntry.Key}' [{plan.LocaleCode}] " +
                        $"in '{collection.TableCollectionName}'.");

                ValidatePlaceholders(
                    plan,
                    collection,
                    sharedEntry.Id,
                    plan.Value,
                    GetDefaultLocaleCode());
            }
            else if (plan.CreatesEntry)
            {
                plan.CreatesLocaleCell = true;
            }

            Remember(plan);
            return plan;
        }

        /// <summary>Executes a fresh catalog-cell preview as one verified Unity Undo group.</summary>
        public static LocalizationCatalogEditResult ExecuteEdit(LocalizationCatalogEditPlan plan)
        {
            var result = new LocalizationCatalogEditResult(plan);
            if (plan == null || !plan.IsExecutable)
            {
                result.Error = "The catalog edit plan is missing or not executable.";
                return result;
            }

            var current = LocalizationAuditEngine.Audit(LocalizationAuditRequest.CreateDoctorRequest());
            if (!string.Equals(
                    current.CatalogFingerprint,
                    plan.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                result.WasStale = true;
                result.Error =
                    "The localization catalog changed after this preview. Preview the edit again.";
                return result;
            }

            var collection = FindCollection(plan.CollectionId);
            var table = collection?.GetTable(plan.LocaleCode) as StringTable;
            if (collection == null || table == null ||
                collection != plan.Collection || table != plan.Table)
            {
                result.WasStale = true;
                result.Error = "The target collection or locale table changed after this preview.";
                return result;
            }

            var undoGroup = BeginUndoGroup($"Edit localization {plan.Key} [{plan.LocaleCode}]");
            try
            {
                Undo.RegisterCompleteObjectUndo(
                    new UnityEngine.Object[] { collection.SharedData, table },
                    $"Edit localization {plan.Key} [{plan.LocaleCode}]");

                var entryId = plan.EntryId;
                if (plan.CreatesEntry)
                {
                    var sharedEntry = collection.SharedData.AddKey(plan.Key);
                    if (sharedEntry == null)
                        throw new InvalidOperationException($"Could not create key '{plan.Key}'.");
                    entryId = sharedEntry.Id;
                    plan.EntryId = entryId;
                }

                var entry = table.GetEntry(entryId) ?? table.AddEntry(entryId, string.Empty);
                if (entry == null)
                    throw new InvalidOperationException(
                        $"Could not create locale cell '{entryId}' [{plan.LocaleCode}].");
                entry.Value = plan.Value;
                EditorUtility.SetDirty(collection.SharedData);
                EditorUtility.SetDirty(table);
                AssetDatabase.SaveAssetIfDirty(collection.SharedData);
                AssetDatabase.SaveAssetIfDirty(table);

                var verified = collection.SharedData.GetEntry(entryId);
                var verifiedCell = table.GetEntry(entryId);
                if (verified == null ||
                    !string.Equals(verified.Key, plan.Key, StringComparison.Ordinal) ||
                    verifiedCell == null ||
                    !string.Equals(verifiedCell.Value, plan.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Catalog edit postcondition failed; the transaction was rolled back.");

                result.PostAudit = LocalizationAuditEngine.Audit(
                    LocalizationAuditRequest.CreateDoctorRequest());
                result.Succeeded = true;
                Undo.CollapseUndoOperations(undoGroup);
                return result;
            }
            catch (Exception exception)
            {
                TryRollback(undoGroup);
                result.Error = exception.Message;
                return result;
            }
        }

        /// <summary>Gets a remembered cell-edit plan when it is still fresh.</summary>
        public static bool TryGetEditPlan(string planId, out LocalizationCatalogEditPlan plan) =>
            TryGetPlan(EditPlans, planId, out plan);

        /// <summary>Exports stable StringTable identities and values as deterministic RFC 4180 CSV.</summary>
        public static string ExportCsv(string collectionId = null)
        {
            var snapshot = Capture();
            var cells = snapshot.Cells
                .Where(cell => string.IsNullOrWhiteSpace(collectionId) ||
                               string.Equals(
                                   cell.CollectionId,
                                   collectionId.Trim(),
                                   StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(
                                   cell.CollectionName,
                                   collectionId.Trim(),
                                   StringComparison.Ordinal))
                .OrderBy(cell => cell.CollectionId, StringComparer.Ordinal)
                .ThenBy(cell => cell.EntryId)
                .ThenBy(cell => cell.LocaleCode, StringComparer.Ordinal)
                .ToArray();
            var builder = new StringBuilder();
            builder.AppendLine(
                "schema,collection_id,collection_name,entry_id,key,locale,value,is_smart");
            foreach (var cell in cells)
            {
                AppendCsvRow(builder, new[]
                {
                    CsvSchema,
                    cell.CollectionId,
                    cell.CollectionName,
                    cell.EntryId.ToString(CultureInfo.InvariantCulture),
                    cell.Key,
                    cell.LocaleCode,
                    cell.Value,
                    cell.IsSmart ? "true" : "false"
                });
            }
            return builder.ToString();
        }

        /// <summary>Previews a stable-identity CSV import. No row is applied when any row is invalid.</summary>
        public static LocalizationCatalogImportPlan PreviewCsvImport(string csv)
        {
            var audit = LocalizationAuditEngine.Audit(LocalizationAuditRequest.CreateDoctorRequest());
            var plan = new LocalizationCatalogImportPlan(audit.CatalogFingerprint);
            if (csv == null)
            {
                plan.AddError("CSV is missing.");
                Remember(plan);
                return plan;
            }
            if (csv.Length > MaxCsvCharacters)
            {
                plan.AddError(
                    $"CSV exceeds the {MaxCsvCharacters / (1024 * 1024)} MB authoring limit. " +
                    "Split the export by collection.");
                Remember(plan);
                return plan;
            }

            List<string[]> rows;
            try
            {
                rows = ParseCsv(csv);
            }
            catch (Exception exception)
            {
                plan.AddError($"CSV could not be parsed: {exception.Message}");
                Remember(plan);
                return plan;
            }

            if (rows.Count == 0)
            {
                plan.AddError("CSV is empty.");
                Remember(plan);
                return plan;
            }
            if (rows.Count > MaxCsvRows)
            {
                plan.AddError(
                    $"CSV has {rows.Count} rows; the limit is {MaxCsvRows}. " +
                    "Split the export by collection.");
                Remember(plan);
                return plan;
            }

            var expectedHeader = new[]
            {
                "schema", "collection_id", "collection_name", "entry_id",
                "key", "locale", "value", "is_smart"
            };
            if (!rows[0].SequenceEqual(expectedHeader, StringComparer.Ordinal))
            {
                plan.AddError(
                    "CSV header does not match the Molca catalog v1 schema. Export a fresh template.");
                Remember(plan);
                return plan;
            }

            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceLocaleCode = GetDefaultLocaleCode();
            for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
                    row[columnIndex] = UnprotectSpreadsheetCell(row[columnIndex]);
                if (row.All(string.IsNullOrEmpty))
                    continue;
                if (row.Length != expectedHeader.Length)
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} has {row.Length} columns; expected {expectedHeader.Length}.");
                    continue;
                }
                if (!string.Equals(row[0], CsvSchema, StringComparison.Ordinal))
                {
                    plan.AddError($"Row {rowIndex + 1} has unsupported schema '{row[0]}'.");
                    continue;
                }
                if (!long.TryParse(
                        row[3],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var entryId) ||
                    entryId <= 0)
                {
                    plan.AddError($"Row {rowIndex + 1} has invalid stable entry id '{row[3]}'.");
                    continue;
                }

                var collection = FindCollection(row[1]);
                if (collection == null)
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} references unknown collection id '{row[1]}'.");
                    continue;
                }
                if (!string.Equals(
                        collection.TableCollectionName,
                        row[2],
                        StringComparison.Ordinal))
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} collection name '{row[2]}' does not match stable id " +
                        $"'{row[1]}' ('{collection.TableCollectionName}').");
                    continue;
                }

                var sharedEntry = collection.SharedData.GetEntry(entryId);
                if (sharedEntry == null ||
                    !string.Equals(sharedEntry.Key, row[4], StringComparison.Ordinal))
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} entry identity/key does not match the current catalog.");
                    continue;
                }

                var localeCode = CanonicalizeLocale(row[5]);
                var table = collection.GetTable(localeCode) as StringTable;
                if (table == null)
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} locale '{row[5]}' is not present in collection " +
                        $"'{collection.TableCollectionName}'.");
                    continue;
                }
                var tablePath = AssetDatabase.GetAssetPath(table);
                if (!IsWritableAssetPath(tablePath) ||
                    !IsWritableAssetPath(AssetDatabase.GetAssetPath(collection.SharedData)))
                {
                    plan.AddError($"Row {rowIndex + 1} targets package-owned/read-only asset '{tablePath}'.");
                    continue;
                }

                var requestedSmart = string.Equals(row[7], "true", StringComparison.OrdinalIgnoreCase);
                var existing = table.GetEntry(entryId);
                if (existing != null && existing.IsSmart != requestedSmart)
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} changes is_smart metadata. Catalog v1 imports values only.");
                    continue;
                }
                if (existing == null && requestedSmart)
                {
                    plan.AddError(
                        $"Row {rowIndex + 1} would create a smart cell. Set smart metadata in Unity first.");
                    continue;
                }

                var identity = $"{row[1]}:{entryId}:{localeCode}";
                if (identities.TryGetValue(identity, out var priorValue))
                {
                    if (!string.Equals(priorValue, row[6], StringComparison.Ordinal))
                        plan.AddError(
                            $"Rows contain conflicting values for '{row[4]}' [{localeCode}].");
                    continue;
                }
                identities.Add(identity, row[6]);

                var previous = existing?.Value ?? string.Empty;
                if (string.Equals(previous, row[6], StringComparison.Ordinal))
                    continue;

                var placeholderError = GetPlaceholderError(
                    collection,
                    entryId,
                    localeCode,
                    row[6],
                    sourceLocaleCode);
                if (!string.IsNullOrEmpty(placeholderError))
                {
                    plan.AddError($"Row {rowIndex + 1}: {placeholderError}");
                    continue;
                }

                plan.AddChange(new LocalizationCatalogImportChange(
                    row[1],
                    entryId,
                    row[4],
                    localeCode,
                    previous,
                    row[6])
                {
                    Table = table
                });
            }

            if (plan.Errors.Count == 0 && plan.Changes.Count == 0)
                plan.AddWarning("The import matches the current catalog; there are no changes to apply.");
            Remember(plan);
            return plan;
        }

        /// <summary>Applies every previewed CSV cell or rolls all of them back.</summary>
        public static LocalizationCatalogImportResult ExecuteCsvImport(
            LocalizationCatalogImportPlan plan)
        {
            var result = new LocalizationCatalogImportResult(plan);
            if (plan == null || !plan.IsExecutable)
            {
                result.Error = "The catalog import plan is missing or not executable.";
                return result;
            }

            var current = LocalizationAuditEngine.Audit(LocalizationAuditRequest.CreateDoctorRequest());
            if (!string.Equals(
                    current.CatalogFingerprint,
                    plan.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                result.WasStale = true;
                result.Error =
                    "The localization catalog changed after this import preview. Preview the CSV again.";
                return result;
            }

            var tables = plan.Changes.Select(change => change.Table).Distinct().ToArray();
            if (tables.Any(table => table == null))
            {
                result.WasStale = true;
                result.Error = "An import target disappeared after preview.";
                return result;
            }

            var undoGroup = BeginUndoGroup($"Import {plan.Changes.Count} localization value(s)");
            try
            {
                Undo.RegisterCompleteObjectUndo(
                    tables.Cast<UnityEngine.Object>().ToArray(),
                    $"Import {plan.Changes.Count} localization value(s)");
                foreach (var change in plan.Changes)
                {
                    var entry = change.Table.GetEntry(change.EntryId) ??
                                change.Table.AddEntry(change.EntryId, string.Empty);
                    if (entry == null)
                        throw new InvalidOperationException(
                            $"Could not create '{change.Key}' [{change.LocaleCode}].");
                    entry.Value = change.Value;
                    EditorUtility.SetDirty(change.Table);
                }
                foreach (var table in tables)
                    AssetDatabase.SaveAssetIfDirty(table);

                foreach (var change in plan.Changes)
                {
                    var verified = change.Table.GetEntry(change.EntryId);
                    if (verified == null ||
                        !string.Equals(verified.Value, change.Value, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Import postcondition failed for '{change.Key}' [{change.LocaleCode}].");
                }

                result.PostAudit = LocalizationAuditEngine.Audit(
                    LocalizationAuditRequest.CreateDoctorRequest());
                result.Succeeded = true;
                Undo.CollapseUndoOperations(undoGroup);
                return result;
            }
            catch (Exception exception)
            {
                TryRollback(undoGroup);
                result.Error = exception.Message;
                return result;
            }
        }

        /// <summary>Gets a remembered import plan when it is still fresh.</summary>
        public static bool TryGetImportPlan(
            string planId,
            out LocalizationCatalogImportPlan plan) =>
            TryGetPlan(ImportPlans, planId, out plan);

        private static StringTableCollection[] GetCollections() =>
            LocalizationEditorSettings.GetStringTableCollections()
                .Where(collection => collection != null)
                .OrderBy(GetCollectionId, StringComparer.Ordinal)
                .ToArray();

        private static StringTableCollection FindCollection(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
                return null;
            var candidate = identity.Trim();
            return GetCollections().FirstOrDefault(collection =>
                string.Equals(GetCollectionId(collection), candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(collection.TableCollectionName, candidate, StringComparison.Ordinal));
        }

        private static string GetCollectionId(StringTableCollection collection) =>
            collection.SharedData.TableCollectionNameGuid.ToString("N");

        private static string CanonicalizeLocale(string localeCode)
        {
            if (string.IsNullOrWhiteSpace(localeCode))
                return string.Empty;
            try
            {
                return CultureInfo.GetCultureInfo(localeCode.Trim()).Name;
            }
            catch (CultureNotFoundException)
            {
                return localeCode.Trim();
            }
        }

        private static bool IsWritableAssetPath(string path) =>
            !string.IsNullOrEmpty(path) &&
            path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);

        private static void ValidatePlaceholders(
            LocalizationCatalogEditPlan plan,
            StringTableCollection collection,
            long entryId,
            string value,
            string sourceLocaleCode)
        {
            var error = GetPlaceholderError(
                collection,
                entryId,
                plan.LocaleCode,
                value,
                sourceLocaleCode);
            if (!string.IsNullOrEmpty(error))
                plan.AddError(error);
        }

        private static string GetPlaceholderError(
            StringTableCollection collection,
            long entryId,
            string localeCode,
            string value,
            string sourceCode)
        {
            if (string.IsNullOrEmpty(sourceCode) ||
                string.Equals(sourceCode, localeCode, StringComparison.OrdinalIgnoreCase))
                return null;
            var sourceTable = collection.GetTable(sourceCode) as StringTable;
            var sourceValue = sourceTable?.GetEntry(entryId)?.Value;
            if (string.IsNullOrEmpty(sourceValue))
                return null;

            var expected = LocalizationPlaceholderUtility.Extract(sourceValue);
            var actual = LocalizationPlaceholderUtility.Extract(value);
            if (expected.SetEquals(actual))
                return null;
            return
                $"Placeholder mismatch for '{collection.SharedData.GetEntry(entryId)?.Key}' " +
                $"[{localeCode}]. Expected {{{string.Join(", ", expected)}}}; " +
                $"found {{{string.Join(", ", actual)}}}.";
        }

        internal static string GetDefaultLocaleCode()
        {
            return AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => AssetDatabase.LoadAssetAtPath<LocalizationModule>(path))
                .Where(module => module != null)
                .SelectMany(module => module.LanguageCode)
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        }

        private static void AppendCsvRow(StringBuilder builder, IEnumerable<string> values)
        {
            builder.AppendLine(string.Join(
                ",",
                values.Select(ProtectSpreadsheetCell).Select(EscapeCsv)));
        }

        private static string ProtectSpreadsheetCell(string value)
        {
            value ??= string.Empty;
            if (value.Length == 0)
                return value;
            if (value[0] == '\t' ||
                value[0] == '=' ||
                value[0] == '+' ||
                value[0] == '-' ||
                value[0] == '@')
                return "\t" + value;
            return value;
        }

        private static string UnprotectSpreadsheetCell(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '\t' || value.Length < 2)
                return value ?? string.Empty;
            if (value[1] == '\t' ||
                value[1] == '=' ||
                value[1] == '+' ||
                value[1] == '-' ||
                value[1] == '@')
                return value.Substring(1);
            return value;
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static List<string[]> ParseCsv(string csv)
        {
            if (csv == null)
                throw new ArgumentNullException(nameof(csv));
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var quoteClosed = false;
            for (var index = 0; index < csv.Length; index++)
            {
                var character = csv[index];
                if (quoted)
                {
                    if (character == '"' &&
                        index + 1 < csv.Length &&
                        csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else if (character == '"')
                    {
                        quoted = false;
                        quoteClosed = true;
                    }
                    else
                    {
                        field.Append(character);
                    }
                    continue;
                }

                if (quoteClosed &&
                    character != ',' &&
                    character != '\r' &&
                    character != '\n')
                    throw new FormatException(
                        $"unexpected character after a quoted field at offset {index}");

                if (character == '"' && field.Length == 0 && !quoteClosed)
                {
                    quoted = true;
                }
                else if (character == '"')
                {
                    throw new FormatException($"unexpected quote at offset {index}");
                }
                else if (character == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    quoteClosed = false;
                }
                else if (character == '\r' || character == '\n')
                {
                    if (character == '\r' &&
                        index + 1 < csv.Length &&
                        csv[index + 1] == '\n')
                        index++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    quoteClosed = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            if (quoted)
                throw new FormatException("unterminated quoted field");
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row.ToArray());
            }
            return rows;
        }

        private static int BeginUndoGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return group;
        }

        private static void TryRollback(int undoGroup)
        {
            try
            {
                Undo.RevertAllDownToGroup(undoGroup);
                AssetDatabase.SaveAssets();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Localization catalog rollback failed: {exception.Message}");
            }
        }

        private static void Remember(LocalizationCatalogEditPlan plan) =>
            Remember(EditPlans, EditPlanOrder, plan.PlanId, plan);

        private static void Remember(LocalizationCatalogImportPlan plan) =>
            Remember(ImportPlans, ImportPlanOrder, plan.PlanId, plan);

        private static void Remember<T>(
            IDictionary<string, T> plans,
            Queue<string> order,
            string planId,
            T plan)
        {
            plans[planId] = plan;
            order.Enqueue(planId);
            while (order.Count > MaxRememberedPlans)
                plans.Remove(order.Dequeue());
        }

        private static bool TryGetPlan<T>(
            IDictionary<string, T> plans,
            string planId,
            out T plan)
            where T : class
        {
            plan = null;
            if (string.IsNullOrEmpty(planId) || !plans.TryGetValue(planId, out plan))
                return false;
            var createdAt = plan switch
            {
                LocalizationCatalogEditPlan edit => edit.CreatedAtUtc,
                LocalizationCatalogImportPlan import => import.CreatedAtUtc,
                _ => DateTime.MinValue
            };
            if (DateTime.UtcNow - createdAt <= PlanLifetime)
                return true;
            plans.Remove(planId);
            plan = null;
            return false;
        }
    }

    /// <summary>One placeholder interpretation shared by Doctor and every write preview.</summary>
    internal static class LocalizationPlaceholderUtility
    {
        internal static SortedSet<string> Extract(string value)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
                return result;

            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '{')
                    continue;
                if (index + 1 < value.Length && value[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var tokenStart = index + 1;
                while (tokenStart < value.Length && char.IsWhiteSpace(value[tokenStart]))
                    tokenStart++;
                var tokenEnd = tokenStart;
                while (tokenEnd < value.Length)
                {
                    var character = value[tokenEnd];
                    if (!(char.IsLetterOrDigit(character) ||
                          character == '_' ||
                          character == '.'))
                        break;
                    tokenEnd++;
                }
                if (tokenEnd > tokenStart)
                    result.Add(value.Substring(tokenStart, tokenEnd - tokenStart));
            }
            return result;
        }
    }
}
