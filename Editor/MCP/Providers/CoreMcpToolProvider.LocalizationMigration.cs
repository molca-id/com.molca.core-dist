using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationMigrationInventoryTool() =>
            new(
                name: "molca_localization_migration_inventory",
                description: "Inventories legacy DynamicLocalization values without changing the project. " +
                             "Returns stable object/property locators, source kind, row counts, writability, " +
                             "and a fingerprint.",
                inputSchemaJson:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"pathFilter\":{\"type\":\"string\",\"description\":\"Optional case-insensitive asset/scene path substring.\"}}," +
                    "\"additionalProperties\":false}",
                execute: ExecuteLocalizationMigrationInventory);

        private static McpToolDefinition CreateLocalizationPlanMigrateValuesTool() =>
            new(
                name: "molca_localization_plan_migrate_values",
                description: "Previews schema-v1 to schema-v2 localization value migration. No writes. " +
                             "Returns an expiring fingerprint-bound plan id and every proposed change.",
                inputSchemaJson:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"pathFilter\":{\"type\":\"string\",\"description\":\"Optional case-insensitive asset/scene path substring.\"}}," +
                    "\"additionalProperties\":false}",
                execute: ExecuteLocalizationPlanMigrateValues,
                mode: McpToolMode.Edit,
                kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLocalizationMigrateValuesTool() =>
            new(
                name: "molca_localization_migrate_values",
                description: "Executes a fresh localization value migration preview as one Unity Undo " +
                             "transaction, refuses stale plans, preserves row order/text and catalog " +
                             "references, and runs a post-migration audit.",
                inputSchemaJson:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"planId\":{\"type\":\"string\",\"description\":\"Opaque plan id from molca_localization_plan_migrate_values.\"}}," +
                    "\"required\":[\"planId\"],\"additionalProperties\":false}",
                execute: ExecuteLocalizationMigrateValues,
                mode: McpToolMode.Edit,
                kind: McpToolKind.Action,
                reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteLocalizationMigrationInventory(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            return SerializeMigrationInventory(
                LocalizationValueMigrationService.Inventory(args.Value<string>("pathFilter")));
        }

        private static string ExecuteLocalizationPlanMigrateValues(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var plan = LocalizationValueMigrationService.Preview(args.Value<string>("pathFilter"));
            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["createdAtUtc"] = plan.CreatedAtUtc,
                ["sourceFingerprint"] = plan.SourceFingerprint,
                ["isExecutable"] = plan.IsExecutable,
                ["candidateCount"] = plan.Candidates.Count,
                ["changes"] = new JArray(plan.Changes),
                ["warnings"] = new JArray(plan.Warnings),
                ["errors"] = new JArray(plan.Errors),
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationMigrateValues(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var planId = args.Value<string>("planId");
            if (!LocalizationValueMigrationService.TryGetPlan(planId, out var plan))
                return Error(
                    "The migration plan is missing or expired. Run " +
                    "molca_localization_plan_migrate_values again.");
            var result = LocalizationValueMigrationService.Execute(plan);
            if (!result.Succeeded)
                return Error(result.Error);
            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["changedCount"] = result.ChangedCount,
                ["remainingLegacyCount"] = result.PostInventory.Candidates.Count,
                ["postAuditSnapshotId"] = result.PostAudit.SnapshotId,
                ["postAuditStatus"] = result.PostAudit.Status.ToString(),
                ["postAuditFingerprint"] = result.PostAudit.CatalogFingerprint,
            }.ToString(Formatting.None);
        }

        private static string SerializeMigrationInventory(
            LocalizationValueMigrationInventory inventory) =>
            new JObject
            {
                ["schemaVersion"] = 2,
                ["fingerprint"] = inventory.Fingerprint,
                ["candidateCount"] = inventory.Candidates.Count,
                ["writableCount"] = inventory.Candidates.Count(candidate => candidate.IsWritable),
                ["candidates"] = new JArray(inventory.Candidates.Select(candidate => new JObject
                {
                    ["stableId"] = candidate.StableId,
                    ["assetPath"] = candidate.AssetPath,
                    ["objectId"] = candidate.ObjectId,
                    ["objectType"] = candidate.ObjectType,
                    ["propertyPath"] = candidate.PropertyPath,
                    ["sourceKind"] = candidate.SourceKind.ToString(),
                    ["rowCount"] = candidate.RowCount,
                    ["writable"] = candidate.IsWritable,
                })),
            }.ToString(Formatting.None);
    }
}
