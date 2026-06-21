using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_services</c> tool (Sprint 15.4): the DI container registrations — eager
        /// singletons, lazy bindings, and factories. Requires Play mode (the container is populated at
        /// bootstrap).
        /// </summary>
        private static McpToolDefinition CreateServicesTool() => new McpToolDefinition(
            name: "molca_services",
            description: "Lists the RuntimeManager service-container registrations: service type, "
                       + "implementation type, lifetime (Singleton/Transient), whether it is a factory, "
                       + "and whether an instance has been created. Requires Play mode.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteServices,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteServices(string argumentsJson)
        {
            var arr = new JArray();
            foreach (var s in RuntimeManager.GetServiceRegistrations())
            {
                arr.Add(new JObject
                {
                    ["serviceType"] = s.ServiceType?.Name,
                    ["implementationType"] = s.ImplementationType?.Name,
                    ["lifetime"] = s.Lifetime.ToString(),
                    ["isFactory"] = s.IsFactory,
                    ["hasInstance"] = s.HasInstance
                });
            }

            var result = new JObject
            {
                ["count"] = arr.Count,
                ["services"] = arr
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
