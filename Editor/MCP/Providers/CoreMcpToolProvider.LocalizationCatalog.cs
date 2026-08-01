using System;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>Stable catalog browsing, previewed cell edits, and CSV round trips.</summary>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationCatalogTool() => new(
            name: "molca_localization_catalog",
            description: "Lists stable StringTable collection/entry/locale cells from the same catalog "
                       + "used by localization audit. Reports missing values and package ownership. "
                       + "Use collectionId to narrow large catalogs.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"collectionId\":{\"type\":\"string\",\"description\":\"Optional stable collection id or exact collection name.\"}," +
                "\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000,\"default\":200}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationCatalog);

        private static McpToolDefinition CreateLocalizationPlanCatalogEditTool() => new(
            name: "molca_localization_plan_catalog_edit",
            description: "Previews one stable StringTable cell edit or new key without changing assets. "
                       + "Validates collection/entry identity, locale availability, ownership, stale source "
                       + "fingerprints, and placeholders.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"collectionId\":{\"type\":\"string\",\"description\":\"Stable collection id from molca_localization_catalog.\"}," +
                "\"entryId\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Stable entry id. Use 0 with key to create a new entry.\"}," +
                "\"key\":{\"type\":\"string\",\"description\":\"Exact developer key; required for new entries and checked for existing ids.\"}," +
                "\"locale\":{\"type\":\"string\",\"description\":\"BCP-47 locale code.\"}," +
                "\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"collectionId\",\"entryId\",\"key\",\"locale\",\"value\"]," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationPlanCatalogEdit);

        private static McpToolDefinition CreateLocalizationCatalogEditTool() => new(
            name: "molca_localization_catalog_edit",
            description: "Executes one fresh catalog-cell edit plan as a verified Unity Undo transaction. "
                       + "Use molca_localization_plan_catalog_edit first.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"planId\":{\"type\":\"string\",\"description\":\"Opaque plan id from molca_localization_plan_catalog_edit.\"}}," +
                "\"required\":[\"planId\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationCatalogEdit,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLocalizationExportCsvTool() => new(
            name: "molca_localization_export_csv",
            description: "Exports deterministic RFC 4180 CSV with schema, stable collection id, stable "
                       + "entry id, key, locale, value, and smart-string state. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"collectionId\":{\"type\":\"string\",\"description\":\"Optional stable collection id or exact name.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationExportCsv);

        private static McpToolDefinition CreateLocalizationPlanImportCsvTool() => new(
            name: "molca_localization_plan_import_csv",
            description: "Previews a Molca catalog v1 CSV import without changing assets. The entire file "
                       + "is rejected on unknown/stale identities, locale or key mismatch, placeholder "
                       + "mismatch, smart-metadata changes, conflicting duplicates, or read-only targets.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"csv\":{\"type\":\"string\",\"description\":\"CSV returned by molca_localization_export_csv after translation edits.\"}}," +
                "\"required\":[\"csv\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationPlanImportCsv);

        private static McpToolDefinition CreateLocalizationImportCsvTool() => new(
            name: "molca_localization_import_csv",
            description: "Executes a fresh catalog CSV import plan atomically as one Unity Undo group and "
                       + "verifies every postcondition. Use molca_localization_plan_import_csv first.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"planId\":{\"type\":\"string\",\"description\":\"Opaque plan id from molca_localization_plan_import_csv.\"}}," +
                "\"required\":[\"planId\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationImportCsv,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteLocalizationCatalog(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var filter = args.Value<string>("collectionId");
            var maxResults = Math.Clamp(args.Value<int?>("maxResults") ?? 200, 1, 1000);
            var snapshot = LocalizationCatalogAuthoringService.Capture();
            var filtered = snapshot.Cells.Where(cell =>
                    string.IsNullOrWhiteSpace(filter) ||
                    string.Equals(cell.CollectionId, filter, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cell.CollectionName, filter, StringComparison.Ordinal))
                .ToArray();
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["snapshotId"] = snapshot.SnapshotId,
                ["sourceFingerprint"] = snapshot.SourceFingerprint,
                ["total"] = filtered.Length,
                ["truncated"] = filtered.Length > maxResults,
                ["warnings"] = new JArray(snapshot.Warnings),
                ["cells"] = new JArray(filtered.Take(maxResults).Select(cell => new JObject
                {
                    ["collectionId"] = cell.CollectionId,
                    ["collection"] = cell.CollectionName,
                    ["entryId"] = cell.EntryId,
                    ["key"] = cell.Key,
                    ["locale"] = cell.LocaleCode,
                    ["value"] = cell.Value,
                    ["missing"] = cell.IsMissing,
                    ["smart"] = cell.IsSmart,
                    ["assetPath"] = cell.TableAssetPath,
                    ["readOnly"] = cell.IsReadOnly
                }))
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPlanCatalogEdit(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var plan = LocalizationCatalogAuthoringService.PreviewEdit(
                args.Value<string>("collectionId"),
                args.Value<long>("entryId"),
                args.Value<string>("key"),
                args.Value<string>("locale"),
                args.Value<string>("value"));
            return SerializeCatalogEditPlan(plan).ToString(Formatting.None);
        }

        private static string ExecuteLocalizationCatalogEdit(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!LocalizationCatalogAuthoringService.TryGetEditPlan(
                    args.Value<string>("planId"),
                    out var plan))
                return Error(
                    "The catalog edit plan is missing or expired. " +
                    "Run molca_localization_plan_catalog_edit again.");
            var result = LocalizationCatalogAuthoringService.ExecuteEdit(plan);
            if (!result.Succeeded)
                return Error(result.Error);
            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["collectionId"] = plan.CollectionId,
                ["entryId"] = plan.EntryId,
                ["key"] = plan.Key,
                ["locale"] = plan.LocaleCode,
                ["value"] = plan.Value,
                ["postAuditSnapshotId"] = result.PostAudit?.SnapshotId,
                ["postAuditFingerprint"] = result.PostAudit?.CatalogFingerprint
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationExportCsv(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var csv = LocalizationCatalogAuthoringService.ExportCsv(
                args.Value<string>("collectionId"));
            return new JObject
            {
                ["schema"] = LocalizationCatalogAuthoringService.CsvSchema,
                ["csv"] = csv
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPlanImportCsv(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var plan = LocalizationCatalogAuthoringService.PreviewCsvImport(
                args.Value<string>("csv"));
            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["createdAtUtc"] = plan.CreatedAtUtc,
                ["sourceFingerprint"] = plan.SourceFingerprint,
                ["executable"] = plan.IsExecutable,
                ["changeCount"] = plan.Changes.Count,
                ["changes"] = new JArray(plan.Changes.Select(change => new JObject
                {
                    ["collectionId"] = change.CollectionId,
                    ["entryId"] = change.EntryId,
                    ["key"] = change.Key,
                    ["locale"] = change.LocaleCode,
                    ["previousValue"] = change.PreviousValue,
                    ["value"] = change.Value
                })),
                ["warnings"] = new JArray(plan.Warnings),
                ["errors"] = new JArray(plan.Errors)
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationImportCsv(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!LocalizationCatalogAuthoringService.TryGetImportPlan(
                    args.Value<string>("planId"),
                    out var plan))
                return Error(
                    "The catalog import plan is missing or expired. " +
                    "Run molca_localization_plan_import_csv again.");
            var result = LocalizationCatalogAuthoringService.ExecuteCsvImport(plan);
            if (!result.Succeeded)
                return Error(result.Error);
            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["applied"] = plan.Changes.Count,
                ["postAuditSnapshotId"] = result.PostAudit?.SnapshotId,
                ["postAuditFingerprint"] = result.PostAudit?.CatalogFingerprint
            }.ToString(Formatting.None);
        }

        private static JObject SerializeCatalogEditPlan(LocalizationCatalogEditPlan plan) =>
            new()
            {
                ["planId"] = plan.PlanId,
                ["createdAtUtc"] = plan.CreatedAtUtc,
                ["sourceFingerprint"] = plan.SourceFingerprint,
                ["collectionId"] = plan.CollectionId,
                ["entryId"] = plan.EntryId,
                ["key"] = plan.Key,
                ["locale"] = plan.LocaleCode,
                ["previousValue"] = plan.PreviousValue,
                ["value"] = plan.Value,
                ["createsEntry"] = plan.CreatesEntry,
                ["createsLocaleCell"] = plan.CreatesLocaleCell,
                ["executable"] = plan.IsExecutable,
                ["changes"] = new JArray(plan.Changes),
                ["warnings"] = new JArray(plan.Warnings),
                ["errors"] = new JArray(plan.Errors)
            };
    }
}
