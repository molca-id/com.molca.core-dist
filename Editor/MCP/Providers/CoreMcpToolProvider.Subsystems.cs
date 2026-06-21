using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_subsystems</c> tool (Sprint 15.3): the live subsystem graph. Requires Play mode
        /// because the resolved init order only exists once RuntimeManager has bootstrapped.
        /// </summary>
        private static McpToolDefinition CreateSubsystemsTool() => new McpToolDefinition(
            name: "molca_subsystems",
            description: "Lists the registered RuntimeSubsystems with their RuntimeMode, IsActive, "
                       + "InitializationPriority and [DependsOn] edges, plus the resolved initialization "
                       + "order. Requires Play mode.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteSubsystems,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSubsystems(string argumentsJson)
        {
            var subsystems = RuntimeManager.GetSubsystems();
            var initOrder = RuntimeManager.GetResolvedInitOrder();

            var arr = new JArray();
            foreach (var s in subsystems)
            {
                if (s == null) continue;
                var type = s.GetType();

                var dependsOn = new JArray();
                foreach (var attr in type.GetCustomAttributes<DependsOnAttribute>(inherit: true))
                    foreach (var dep in attr.Dependencies)
                        if (dep != null) dependsOn.Add(dep.Name);

                arr.Add(new JObject
                {
                    ["type"] = type.Name,
                    ["fullType"] = type.FullName,
                    ["mode"] = s.Mode.ToString(),
                    ["isActive"] = s.IsActive,
                    ["initializationPriority"] = s.InitializationPriority,
                    ["dependsOn"] = dependsOn
                });
            }

            var orderArr = new JArray();
            foreach (var s in initOrder)
                if (s != null) orderArr.Add(s.GetType().Name);

            var result = new JObject
            {
                ["count"] = arr.Count,
                ["subsystems"] = arr,
                ["resolvedInitOrder"] = orderArr
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
