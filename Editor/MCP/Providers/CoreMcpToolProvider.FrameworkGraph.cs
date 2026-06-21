using Molca.Editor.FrameworkGraph;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_framework_graph</c> tool (Sprint 22.7): a read-only snapshot of how the loaded
        /// project is wired — subsystems, services, scene references, and sequences — as compact JSON.
        /// The exact same <see cref="FrameworkGraphSnapshot"/> that backs the Framework Graph window, so
        /// an assistant and a developer see one consistent topology.
        /// </summary>
        /// <remarks>
        /// Strictly read-only: no <see cref="McpToolKind.Action"/>, no confirmation token, no serialized
        /// mutation. Mode-aware — the subsystem/service layers only populate in Play mode and otherwise
        /// report their reason in <c>unavailable</c>, so the caller learns *why* a section is empty rather
        /// than guessing. Works in both Edit and Play mode (<see cref="McpToolMode.Any"/>).
        /// </remarks>
        private static McpToolDefinition CreateFrameworkGraphTool() => new McpToolDefinition(
            name: "molca_framework_graph",
            description: "Returns a read-only map of how the loaded project is wired: RuntimeSubsystems "
                       + "(+[DependsOn] and resolved init order), DI service registrations, scene "
                       + "ReferenceableComponent Ref Ids and SceneObjectReference edges (flagging "
                       + "empty/duplicate/unresolved), and SequenceControllers with their step-flow tree. "
                       + "Nodes carry a category, severity, and properties; edges carry a kind. The "
                       + "subsystem and service layers require Play mode; when unavailable the reason is "
                       + "listed under 'unavailable'. Read-only — never mutates the project.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteFrameworkGraph,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteFrameworkGraph(string argumentsJson)
        {
            var snapshot = FrameworkGraphBuilder.Build();

            var nodes = new JArray();
            foreach (var n in snapshot.Nodes)
            {
                if (n == null) continue;
                var props = new JObject();
                foreach (var kv in n.Properties)
                    if (kv.Value != null) props[kv.Key] = kv.Value;

                nodes.Add(new JObject
                {
                    ["id"] = n.Id,
                    ["label"] = n.Label,
                    ["subtitle"] = n.Subtitle,
                    ["category"] = n.Category.ToString(),
                    ["severity"] = n.Severity.ToString(),
                    ["runtimeOnly"] = n.RuntimeOnly,
                    ["properties"] = props
                });
            }

            var edges = new JArray();
            foreach (var e in snapshot.Edges)
            {
                if (e == null) continue;
                edges.Add(new JObject
                {
                    ["source"] = e.SourceId,
                    ["target"] = e.TargetId,
                    ["kind"] = e.Kind.ToString(),
                    ["label"] = e.Label
                });
            }

            var unavailable = new JArray();
            foreach (var reason in snapshot.UnavailableReasons)
                unavailable.Add(reason);

            var result = new JObject
            {
                ["isPlayMode"] = snapshot.IsPlayMode,
                ["nodeCount"] = nodes.Count,
                ["edgeCount"] = edges.Count,
                ["nodes"] = nodes,
                ["edges"] = edges,
                ["unavailable"] = unavailable
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
