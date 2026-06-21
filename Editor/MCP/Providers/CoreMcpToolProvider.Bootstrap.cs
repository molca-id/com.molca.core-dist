using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Utilities;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_describe_bootstrap</c> tool: a static, Edit-mode-capable description of the
        /// RuntimeManager bootstrap, derived purely from authored assets — the RuntimeManager prefab
        /// referenced by <see cref="MolcaProjectSettings"/>, its child <see cref="RuntimeSubsystem"/>
        /// components, their <see cref="DependsOnAttribute"/> declarations, and the configured
        /// <see cref="BootstrapExtension"/> list.
        /// </summary>
        /// <remarks>
        /// This is the authoring-time counterpart to <c>molca_subsystems</c> (which needs Play mode to
        /// read the live graph). It resolves the same predicted init order the runtime would compute by
        /// replicating <c>RuntimeManager</c>'s topological sort — <see cref="TopologicalSort"/> over the
        /// prefab-authored subsystems, with <see cref="RuntimeSubsystem.InitializationPriority"/>
        /// (descending) as the tiebreaker — and surfaces any <see cref="DependsOnAttribute"/> cycle.
        ///
        /// Strictly read-only and never mutates the prefab. It only sees subsystems authored as child
        /// components on the prefab; subsystems registered at runtime via code do not appear here and are
        /// noted in the description. Dependencies declared via <see cref="DependsOnAttribute"/> that have
        /// no matching authored subsystem are reported under <c>unresolvedDependencies</c> so missing
        /// wiring is visible before Play.
        /// </remarks>
        private static McpToolDefinition CreateDescribeBootstrapTool() => new McpToolDefinition(
            name: "molca_describe_bootstrap",
            description: "Statically describes the RuntimeManager bootstrap from authored assets (no Play "
                       + "mode): the RuntimeManager prefab from MolcaProjectSettings, its child "
                       + "RuntimeSubsystems with RuntimeMode/InitializationPriority/[DependsOn] edges, the "
                       + "predicted topological init order, any [DependsOn] cycle, [DependsOn] targets with "
                       + "no matching authored subsystem (unresolvedDependencies), and the configured "
                       + "BootstrapExtension list. Only sees prefab-authored subsystems; code-registered "
                       + "subsystems do not appear. Read-only — never mutates the prefab.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteDescribeBootstrap,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteDescribeBootstrap(string argumentsJson)
        {
            var settings = MolcaProjectSettings.Instance;
            if (settings == null)
            {
                return new JObject
                {
                    ["error"] = "MolcaProjectSettings could not be loaded.",
                    ["prefabAssigned"] = false
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            var runtimeManager = settings.RuntimeManager;
            if (runtimeManager == null)
            {
                return new JObject
                {
                    ["error"] = "MolcaProjectSettings has no RuntimeManager prefab assigned.",
                    ["prefabAssigned"] = false
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            // Read the authored prefab hierarchy directly. Inactive children are included so a disabled
            // subsystem still shows up — the runtime discovers it the same way (GetComponentsInChildren).
            var subsystems = runtimeManager.GetComponentsInChildren<RuntimeSubsystem>(includeInactive: true)
                                           .Where(s => s != null)
                                           .ToList();

            // Collapse duplicate types the way RuntimeManager does (first instance wins), so the predicted
            // order matches what the runtime would actually initialize.
            var seenTypes = new HashSet<System.Type>();
            var deduped = new List<RuntimeSubsystem>();
            foreach (var s in subsystems)
                if (seenTypes.Add(s.GetType()))
                    deduped.Add(s);

            var resolved = SortBootstrapSubsystems(deduped, out var cycleParticipants);

            var subsystemArr = new JArray();
            var unresolved = new JArray();
            foreach (var s in deduped)
            {
                var type = s.GetType();

                var dependsOn = new JArray();
                foreach (var attr in type.GetCustomAttributes<DependsOnAttribute>(inherit: true))
                {
                    if (attr.Dependencies == null) continue;
                    foreach (var depType in attr.Dependencies)
                    {
                        if (depType == null) continue;
                        dependsOn.Add(depType.Name);

                        // A declared dependency that matches no authored subsystem is a wiring gap.
                        bool matched = deduped.Any(c => depType.IsInstanceOfType(c));
                        if (!matched)
                            unresolved.Add(new JObject
                            {
                                ["subsystem"] = type.Name,
                                ["dependency"] = depType.Name
                            });
                    }
                }

                subsystemArr.Add(new JObject
                {
                    ["type"] = type.Name,
                    ["fullType"] = type.FullName,
                    ["runtimeMode"] = s.Mode.ToString(),
                    ["initializationPriority"] = s.InitializationPriority,
                    ["dependsOn"] = dependsOn
                });
            }

            var orderArr = new JArray();
            foreach (var s in resolved)
                orderArr.Add(s.GetType().Name);

            var cycleArr = new JArray();
            if (cycleParticipants != null)
                foreach (var s in cycleParticipants)
                    cycleArr.Add(s.GetType().Name);

            var extensionsArr = new JArray();
            foreach (var ext in settings.BootstrapExtensions)
                if (ext != null) extensionsArr.Add(ext.GetType().Name);

            var result = new JObject
            {
                ["prefabAssigned"] = true,
                ["runtimeManagerPrefab"] = runtimeManager.name,
                ["bootstrapExtensions"] = extensionsArr,
                ["subsystemCount"] = subsystemArr.Count,
                ["subsystems"] = subsystemArr,
                ["predictedInitOrder"] = orderArr,
                ["dependencyCycle"] = cycleArr,
                ["unresolvedDependencies"] = unresolved,
                ["note"] = "Static, authoring-time view. Only prefab-authored subsystems are listed; "
                         + "subsystems registered at runtime via code do not appear. Use molca_subsystems "
                         + "in Play mode for the live graph."
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Replicates <c>RuntimeManager.SortSubsystemsForInitialization</c> for the static authoring view:
        /// topological over <see cref="DependsOnAttribute"/> declarations restricted to the authored set,
        /// with <see cref="RuntimeSubsystem.InitializationPriority"/> descending as the tiebreaker.
        /// </summary>
        private static List<RuntimeSubsystem> SortBootstrapSubsystems(
            List<RuntimeSubsystem> candidates, out List<RuntimeSubsystem> cycleParticipants)
        {
            var tiebreaker = Comparer<RuntimeSubsystem>.Create(
                (a, b) => b.InitializationPriority.CompareTo(a.InitializationPriority));

            return TopologicalSort.Sort(
                candidates,
                s => GetAuthoredDependencies(s, candidates),
                tiebreaker,
                out cycleParticipants);
        }

        private static IEnumerable<RuntimeSubsystem> GetAuthoredDependencies(
            RuntimeSubsystem subsystem, IReadOnlyList<RuntimeSubsystem> candidates)
        {
            foreach (var attr in subsystem.GetType().GetCustomAttributes<DependsOnAttribute>(inherit: true))
            {
                if (attr.Dependencies == null) continue;
                foreach (var depType in attr.Dependencies)
                {
                    if (depType == null) continue;
                    for (int i = 0; i < candidates.Count; i++)
                        if (depType.IsInstanceOfType(candidates[i]))
                            yield return candidates[i];
                }
            }
        }
    }
}
