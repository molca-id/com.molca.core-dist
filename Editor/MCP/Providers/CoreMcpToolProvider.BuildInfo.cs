using System;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>The <c>molca_build_info</c> tool (Sprint 15.6): build profiles, version, changelog.</summary>
        private static McpToolDefinition CreateBuildInfoTool() => new McpToolDefinition(
            name: "molca_build_info",
            description: "Lists the configured build profiles, the current version (string, full, and "
                       + "semantic), and the most recent changelog/version-history entries.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"changelogLimit\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50," +
                "\"description\":\"Max recent version-history entries to return (default 10).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteBuildInfo,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteBuildInfo(string argumentsJson)
        {
            var limit = 10;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (args["changelogLimit"] != null)
                    limit = Math.Max(0, args.Value<int>("changelogLimit"));
            }
            catch { /* keep default on malformed args */ }

            var settings = MolcaEditorSettings.Instance;
            var result = new JObject();

            // Profiles.
            var profilesArr = new JArray();
            var buildSettings = settings.BuildSettings;
            if (buildSettings != null)
            {
                foreach (var p in buildSettings.Profiles)
                {
                    if (p == null) continue;
                    profilesArr.Add(new JObject
                    {
                        ["name"] = p.name,
                        ["target"] = p.target.ToString(),
                        ["outputPath"] = p.outputPath,
                        ["developmentBuild"] = p.developmentBuild,
                        ["il2cpp"] = p.il2cpp,
                        ["buildAppBundle"] = p.buildAppBundle,
                        ["buildAddressablesFirst"] = p.buildAddressablesFirst
                    });
                }
            }
            result["profiles"] = profilesArr;
            result["buildSettingsAssigned"] = buildSettings != null;

            // Version.
            var version = settings.VersionSettings;
            if (version != null)
            {
                result["version"] = new JObject
                {
                    ["version"] = version.GetVersionString(),
                    ["full"] = version.GetFullVersionString(),
                    ["semantic"] = version.GetSemanticVersion(),
                    ["buildNumber"] = version.GetBuildNumberString()
                };

                var historyArr = new JArray();
                var history = version.GetVersionHistory();
                if (history != null && limit > 0)
                {
                    // History is appended chronologically; return the most recent first.
                    var start = Math.Max(0, history.Length - limit);
                    for (var i = history.Length - 1; i >= start; i--)
                    {
                        var e = history[i];
                        if (e == null) continue;
                        historyArr.Add(new JObject
                        {
                            ["version"] = e.version,
                            ["timestamp"] = e.timestamp,
                            ["changeType"] = e.changeType,
                            ["notes"] = e.notes
                        });
                    }
                }
                result["recentChangelog"] = historyArr;
            }
            else
            {
                result["version"] = null;
            }

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
