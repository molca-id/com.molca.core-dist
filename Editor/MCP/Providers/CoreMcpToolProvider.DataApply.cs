using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Tabular;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // Shared JSON-Schema fragment: both the plan and the apply tool take the same mapping spec.
        private const string DataApplySchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"rows\":{\"type\":\"array\",\"items\":{\"type\":\"object\"},\"description\":\"Row objects (column name → cell value). Typically the rows from molca_sheet_read, keyed by column name.\"}," +
            "\"keyColumn\":{\"type\":\"string\",\"description\":\"Column whose value selects the target entity for each row.\"}," +
            "\"targetSelector\":{\"type\":\"string\",\"enum\":[\"refId\",\"name\",\"goPath\",\"assetPath\"],\"description\":\"How keyColumn values are resolved: 'refId' (IReferenceable Ref Id), 'name'/'goPath' (scene GameObject), or 'assetPath' (project asset).\"}," +
            "\"bindings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"column\":{\"type\":\"string\"}," +
            "\"target\":{\"type\":\"string\",\"description\":\"'name' (GameObject name), 'ComponentType/fieldPath' (scene component field), or a serialized field path (asset). Set a SceneObjectReference by putting the destination Ref Id in the cell.\"}" +
            "},\"required\":[\"column\",\"target\"],\"additionalProperties\":false}}" +
            "},\"required\":[\"rows\",\"keyColumn\",\"targetSelector\",\"bindings\"],\"additionalProperties\":false}";

        /// <summary>
        /// The <c>molca_data_apply_plan</c> tool: resolves a sheet→entity mapping and returns the exact
        /// diff (old → new per field) it <em>would</em> write, mutating nothing. Read-only, so it runs
        /// without a confirmation prompt — the assistant calls this first to show the user what an apply
        /// would do, then calls <c>molca_data_apply</c> to commit.
        /// </summary>
        private static McpToolDefinition CreateDataApplyPlanTool() => new McpToolDefinition(
            name: "molca_data_apply_plan",
            description: "Previews applying tabular rows onto scene objects/components/assets: resolves each "
                       + "target, coerces each cell, and returns the diff (old → new) plus any rejections, "
                       + "WITHOUT changing anything. Call this before molca_data_apply to show the user the "
                       + "planned changes.",
            inputSchemaJson: DataApplySchema,
            execute: ExecuteDataApplyPlan,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>
        /// The <c>molca_data_apply</c> tool: applies a sheet→entity mapping as one Unity Undo group (a single
        /// Ctrl+Z reverts the whole batch). An Action tool, so the bridge gates it behind the allowlist and a
        /// confirmation prompt and records it in the action audit log.
        /// </summary>
        private static McpToolDefinition CreateDataApplyTool() => new McpToolDefinition(
            name: "molca_data_apply",
            description: "Applies tabular rows onto scene objects/components/assets (GameObject names, "
                       + "component fields, ScriptableObject fields, SceneObjectReference by Ref Id) as one "
                       + "undo-able batch. Per-row/per-cell failures are reported and skipped without aborting "
                       + "the batch. Preview with molca_data_apply_plan first.",
            inputSchemaJson: DataApplySchema,
            execute: ExecuteDataApply,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteDataApplyPlan(string argumentsJson)
        {
            var spec = BuildBindingSpec(ParseArgs(argumentsJson), out var error);
            if (spec == null) return Error(error);
            return BindingResultToJson(TabularBindingService.Plan(spec), dryRun: true);
        }

        private static string ExecuteDataApply(string argumentsJson)
        {
            var spec = BuildBindingSpec(ParseArgs(argumentsJson), out var error);
            if (spec == null) return Error(error);
            return BindingResultToJson(TabularBindingService.Apply(spec), dryRun: false);
        }

        /// <summary>
        /// Parses the shared mapping-spec arguments into a <see cref="TabularBindingSpec"/>. Structural
        /// problems (missing/empty rows, key column, selector, or bindings) set <paramref name="error"/> and
        /// return null; per-row/per-cell issues are left for the binder to report as rejections.
        /// </summary>
        private static TabularBindingSpec BuildBindingSpec(JObject args, out string error)
        {
            error = null;

            if (args["rows"] is not JArray rowsArr || rowsArr.Count == 0)
            {
                error = "'rows' is required and must be a non-empty array of row objects.";
                return null;
            }

            var keyColumn = (string)args["keyColumn"];
            if (string.IsNullOrWhiteSpace(keyColumn))
            {
                error = "'keyColumn' is required.";
                return null;
            }

            var selectorText = (string)args["targetSelector"];
            if (!TryParseSelector(selectorText, out var selector))
            {
                error = "'targetSelector' must be one of: refId, name, goPath, assetPath.";
                return null;
            }

            if (args["bindings"] is not JArray bindingsArr || bindingsArr.Count == 0)
            {
                error = "'bindings' is required and must be a non-empty array of {column, target}.";
                return null;
            }

            var bindings = new List<TabularBindingField>();
            foreach (var t in bindingsArr)
            {
                var column = (string)t["column"];
                var target = (string)t["target"];
                if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(target))
                {
                    error = "each binding must have a non-empty 'column' and 'target'.";
                    return null;
                }
                bindings.Add(new TabularBindingField(column, target));
            }

            var rows = new List<IReadOnlyDictionary<string, string>>(rowsArr.Count);
            foreach (var rowToken in rowsArr)
            {
                var dict = new Dictionary<string, string>();
                if (rowToken is JObject rowObj)
                    foreach (var prop in rowObj.Properties())
                        dict[prop.Name] = CellString(prop.Value);
                rows.Add(dict);
            }

            return new TabularBindingSpec(rows, keyColumn, selector, bindings);
        }

        private static bool TryParseSelector(string text, out TargetSelectorKind selector)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "refid": selector = TargetSelectorKind.RefId; return true;
                case "name":
                case "gopath":
                case "scene": selector = TargetSelectorKind.Scene; return true;
                case "assetpath": selector = TargetSelectorKind.AssetPath; return true;
                default: selector = default; return false;
            }
        }

        /// <summary>Flattens a JSON cell value to the string form the coercion path consumes.</summary>
        private static string CellString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token.Type == JTokenType.String) return (string)token;
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string BindingResultToJson(BindingResult result, bool dryRun)
        {
            var applied = new JArray();
            foreach (var c in result.Applied)
                applied.Add(new JObject
                {
                    ["rowKey"] = c.RowKey,
                    ["target"] = c.Target,
                    ["field"] = c.Field,
                    ["oldValue"] = c.OldValue,
                    ["newValue"] = c.NewValue
                });

            var rejected = new JArray();
            foreach (var r in result.Rejected)
                rejected.Add(new JObject
                {
                    ["rowKey"] = r.RowKey,
                    ["target"] = r.Target,
                    ["field"] = r.Field,
                    ["reason"] = r.Reason
                });

            return new JObject
            {
                ["dryRun"] = dryRun,
                ["appliedCount"] = result.Applied.Count,
                ["rejectedCount"] = result.Rejected.Count,
                ["applied"] = applied,
                ["rejected"] = rejected
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
