using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationPseudoPreviewTool() => new(
            name: "molca_localization_pseudo_preview",
            description: "Transforms text with a non-mutating localization stress profile: AccentExpansion, MissingKeyVisibility, or RightToLeftStress.",
            inputSchemaJson:
                "{\"type\":\"object\",\"required\":[\"text\"],\"properties\":{" +
                "\"text\":{\"type\":\"string\"}," +
                "\"profile\":{\"type\":\"string\",\"enum\":[\"AccentExpansion\",\"MissingKeyVisibility\",\"RightToLeftStress\"]}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationPseudoPreview,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLocalizationPseudoCatalogTool() => new(
            name: "molca_localization_pseudo_catalog",
            description: "Returns a non-mutating pseudo-localized preview of catalog cells.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"profile\":{\"type\":\"string\",\"enum\":[\"AccentExpansion\",\"MissingKeyVisibility\",\"RightToLeftStress\"]}," +
                "\"collectionId\":{\"type\":\"string\"}," +
                "\"maximum\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationPseudoCatalog,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLocalizationPseudoOverflowTool() => new(
            name: "molca_localization_pseudo_overflow",
            description: "Stress-tests loaded LocalizedText UI without mutation and reports text whose preferred size exceeds its RectTransform.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"profile\":{\"type\":\"string\",\"enum\":[\"AccentExpansion\",\"MissingKeyVisibility\",\"RightToLeftStress\"]}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationPseudoOverflow,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static LocalizationPseudoProfile ParsePseudoProfile(JObject args)
        {
            var value = args.Value<string>("profile");
            return Enum.TryParse(value, true, out LocalizationPseudoProfile profile)
                ? profile
                : LocalizationPseudoProfile.AccentExpansion;
        }

        private static string ExecuteLocalizationPseudoPreview(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var profile = ParsePseudoProfile(args);
            var source = args.Value<string>("text") ?? string.Empty;
            return new JObject
            {
                ["profile"] = profile.ToString(),
                ["source"] = source,
                ["pseudo"] = LocalizationPseudoPreviewService.Transform(source, profile),
                ["mutated"] = false,
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPseudoCatalog(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var profile = ParsePseudoProfile(args);
            var rows = LocalizationPseudoPreviewService.PreviewCatalog(
                profile,
                args.Value<string>("collectionId"),
                args.Value<int?>("maximum") ?? 100);
            return new JObject
            {
                ["profile"] = profile.ToString(),
                ["count"] = rows.Count,
                ["mutated"] = false,
                ["rows"] = new JArray(rows.Select(row => new JObject
                {
                    ["collectionId"] = row.CollectionId,
                    ["key"] = row.Key,
                    ["localeCode"] = row.LocaleCode,
                    ["source"] = row.Source,
                    ["pseudo"] = row.Pseudo,
                })),
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPseudoOverflow(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var profile = ParsePseudoProfile(args);
            var rows = LocalizationPseudoPreviewService.ScanLoadedUi(profile);
            return new JObject
            {
                ["profile"] = profile.ToString(),
                ["count"] = rows.Count,
                ["mutated"] = false,
                ["overflows"] = new JArray(rows.Select(row => new JObject
                {
                    ["path"] = row.Path,
                    ["source"] = row.Source,
                    ["pseudo"] = row.Pseudo,
                    ["availableWidth"] = row.Available.x,
                    ["availableHeight"] = row.Available.y,
                    ["preferredWidth"] = row.Preferred.x,
                    ["preferredHeight"] = row.Preferred.y,
                })),
            }.ToString(Formatting.None);
        }
    }
}
