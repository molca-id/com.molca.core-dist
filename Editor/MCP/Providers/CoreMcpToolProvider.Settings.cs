using System.Collections.Generic;
using System.Linq;
using Molca;
using Molca.Editor;
using Molca.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Project settings <i>authoring</i> tools (Sprint 32): list the registered <see cref="SettingModule"/>
    /// assets on <see cref="GlobalSettings"/>, read/write a module's serialized fields, and read the
    /// bootstrap <see cref="MolcaProjectSettings"/>. Field writes are routed through
    /// <see cref="SettingsFieldEditingService"/> so each write is one Unity Undo group; Edit-mode only,
    /// allowlist+confirmation gated, revertible via Ctrl+Z.
    /// </summary>
    /// <remarks>
    /// These tools edit a module asset's authored SerializeFields on disk (Inspector-equivalent
    /// authoring), not the runtime <c>SettingState</c> the settings cardinal rule protects. Editor-only
    /// singleton settings (MCP/Assistant/Notification/Integration) are deliberately out of scope — they
    /// are a different category and may hold secrets that must never be exposed over MCP.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_settings_project_info (read) ───────────────────────────────────────────────

        private static McpToolDefinition CreateSettingsProjectInfoTool() => new McpToolDefinition(
            name: "molca_settings_project_info",
            description: "Reads the bootstrap MolcaProjectSettings: company/project name, project id, and the "
                       + "referenced GlobalSettings and RuntimeManager prefab. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteSettingsProjectInfo,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSettingsProjectInfo(string argumentsJson)
        {
            var project = MolcaProjectSettings.Instance;
            if (project == null) return Error("MolcaProjectSettings could not be located.");

            var global = project.GlobalSettings;
            return new JObject
            {
                ["companyName"] = project.CompanyName,
                ["projectName"] = project.ProjectName,
                ["projectId"] = project.ProjectId,
                ["globalSettings"] = global != null ? AssetDatabase.GetAssetPath(global) : null,
                ["runtimeManager"] = project.RuntimeManager != null
                    ? AssetDatabase.GetAssetPath(project.RuntimeManager)
                    : null,
                ["moduleCount"] = global?.modules?.Length ?? 0
            }.ToString(Formatting.None);
        }

        // ── molca_settings_list_modules (read) ───────────────────────────────────────────────

        private static McpToolDefinition CreateSettingsListModulesTool() => new McpToolDefinition(
            name: "molca_settings_list_modules",
            description: "Lists the SettingModule assets registered on GlobalSettings (type name, full type, "
                       + "asset path). Flags null entries and duplicate types, mirroring the bootstrap "
                       + "validator. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteSettingsListModules,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSettingsListModules(string argumentsJson)
        {
            var global = ResolveGlobalSettings(out var error);
            if (global == null) return Error(error);

            var arr = new JArray();
            int nullCount = 0;
            var seenTypes = new HashSet<string>();
            var duplicates = new HashSet<string>();

            foreach (var module in global.modules ?? System.Array.Empty<SettingModule>())
            {
                if (module == null)
                {
                    nullCount++;
                    continue;
                }
                var type = module.GetType();
                if (!seenTypes.Add(type.FullName)) duplicates.Add(type.Name);

                arr.Add(new JObject
                {
                    ["type"] = type.Name,
                    ["fullType"] = type.FullName,
                    ["assetPath"] = AssetDatabase.GetAssetPath(module)
                });
            }

            return new JObject
            {
                ["count"] = arr.Count,
                ["modules"] = arr,
                ["nullEntries"] = nullCount,
                ["duplicateTypes"] = new JArray(duplicates)
            }.ToString(Formatting.None);
        }

        // ── molca_settings_get_fields (read) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateSettingsGetFieldsTool() => new McpToolDefinition(
            name: "molca_settings_get_fields",
            description: "Reads the current values of a registered SettingModule's serialized fields (by "
                       + "module type name or full name). Excludes the script reference. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"module\":{\"type\":\"string\",\"description\":\"SettingModule type name (or full name).\"}}," +
                "\"required\":[\"module\"],\"additionalProperties\":false}",
            execute: ExecuteSettingsGetFields,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSettingsGetFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var module = ResolveModule(args.Value<string>("module"), out var error);
            if (module == null) return Error(error);

            var fields = new JArray();
            foreach (var field in SettingsFieldEditingService.GetFields(module))
            {
                fields.Add(new JObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type,
                    ["value"] = field.Value
                });
            }

            return new JObject
            {
                ["module"] = module.GetType().Name,
                ["assetPath"] = AssetDatabase.GetAssetPath(module),
                ["fields"] = fields
            }.ToString(Formatting.None);
        }

        // ── molca_settings_set_fields (action) ───────────────────────────────────────────────

        private static McpToolDefinition CreateSettingsSetFieldsTool() => new McpToolDefinition(
            name: "molca_settings_set_fields",
            description: "Sets serialized fields on a registered SettingModule asset (by module type name or "
                       + "full name). 'fields' is an object of fieldName -> value; values are coerced by "
                       + "field type (string/number/bool, enum name, Object by instance id or asset path, "
                       + "Vector2/3/4/Quaternion/Rect/Bounds and Color as a JSON number array or '#RRGGBB', "
                       + "and array/list fields as a JSON array). This authors the asset on disk "
                       + "(Inspector-equivalent), not runtime SettingState. Unknown or read-only fields are "
                       + "reported back as rejected. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"module\":{\"type\":\"string\",\"description\":\"SettingModule type name (or full name).\"}," +
                "\"fields\":{\"type\":\"object\",\"description\":\"fieldName -> value map.\"}}," +
                "\"required\":[\"module\",\"fields\"],\"additionalProperties\":false}",
            execute: ExecuteSettingsSetFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSettingsSetFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var module = ResolveModule(args.Value<string>("module"), out var error);
            if (module == null) return Error(error);

            if (!(args["fields"] is JObject fieldsObj) || !fieldsObj.HasValues)
                return Error("'fields' must be a non-empty object of fieldName -> value.");

            var result = SettingsFieldEditingService.SetFields(module, ToFieldNodeMap(fieldsObj));

            return new JObject
            {
                ["module"] = module.GetType().Name,
                ["assetPath"] = AssetDatabase.GetAssetPath(module),
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedToJson(result.Rejected),
                ["writableFields"] = result.Rejected.Count > 0
                    ? new JArray(SettingsFieldEditingService.GetWritableFields(module))
                    : new JArray()
            }.ToString(Formatting.None);
        }

        // ── Resolution helpers ───────────────────────────────────────────────────────────────

        /// <summary>Resolves the project's <see cref="GlobalSettings"/> asset, or returns a reason.</summary>
        private static GlobalSettings ResolveGlobalSettings(out string error)
        {
            error = null;
            var project = MolcaProjectSettings.Instance;
            if (project == null)
            {
                error = "MolcaProjectSettings could not be located.";
                return null;
            }
            if (project.GlobalSettings == null)
            {
                error = "MolcaProjectSettings has no GlobalSettings assigned.";
                return null;
            }
            return project.GlobalSettings;
        }

        /// <summary>
        /// Resolves a registered <see cref="SettingModule"/> by type name or full name, or returns a
        /// reason listing the available module types.
        /// </summary>
        private static SettingModule ResolveModule(string moduleName, out string error)
        {
            error = null;
            var global = ResolveGlobalSettings(out error);
            if (global == null) return null;

            if (string.IsNullOrWhiteSpace(moduleName))
            {
                error = "'module' is required (SettingModule type name or full name).";
                return null;
            }

            var modules = (global.modules ?? System.Array.Empty<SettingModule>())
                .Where(m => m != null).ToList();

            var match = modules.FirstOrDefault(m =>
                m.GetType().Name == moduleName || m.GetType().FullName == moduleName);

            if (match == null)
            {
                var available = string.Join(", ", modules.Select(m => m.GetType().Name).Distinct());
                error = $"No registered SettingModule named '{moduleName}'. Available: {available}";
                return null;
            }
            return match;
        }
    }
}
