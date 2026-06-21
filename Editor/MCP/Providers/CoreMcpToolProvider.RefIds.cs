using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.ReferenceSystem;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_refids</c> tool (Sprint 15.5): all Ref Ids exposed by live
        /// <see cref="IReferenceable"/> components in the loaded scene(s) — including
        /// <see cref="ReferenceableComponent"/>, <see cref="Molca.Sequence.Step"/>,
        /// <c>SequenceController</c>, and any custom implementer — plus any
        /// <see cref="SceneObjectReference"/> whose Ref Id resolves to no such component (unresolved).
        /// Works in both Edit and Play mode (scans loaded scene objects directly).
        /// </summary>
        /// <remarks>
        /// The "known" set spans every <see cref="IReferenceable"/> rather than a single concrete type,
        /// so it matches the runtime registry and the repair set used by <c>molca_fix_refids</c>.
        /// </remarks>
        private static McpToolDefinition CreateRefIdsTool() => new McpToolDefinition(
            name: "molca_refids",
            description: "Lists Ref Ids exposed by IReferenceable components in the loaded scene(s) "
                       + "(ReferenceableComponents, Steps, SequenceControllers, and custom implementers; "
                       + "flagging empty/duplicate ids), and SceneObjectReference fields whose Ref Id "
                       + "does not resolve to any of them (unresolved references).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteRefIds,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteRefIds(string argumentsJson)
        {
            var known = new HashSet<string>();
            var refArr = new JArray();
            var duplicates = new JArray();
            var empties = new JArray();

            foreach (var mb in FindLiveReferenceables())
            {
                var rc = (IReferenceable)mb;
                try
                {
                    var id = rc.RefId;
                    if (string.IsNullOrEmpty(id))
                    {
                        empties.Add(mb.name);
                        continue;
                    }
                    if (!known.Add(id))
                        duplicates.Add(id);

                    refArr.Add(new JObject
                    {
                        ["refId"] = id,
                        ["refType"] = rc.RefType,
                        ["gameObject"] = mb.name
                    });
                }
                catch
                {
                    // A faulting IReferenceable must not abort the whole listing.
                }
            }

            // Reflection scan for SceneObjectReference fields whose id isn't backed by a component.
            // A single component can throw while its fields are read (e.g. UnassignedReferenceException
            // from an unassigned Unity Object reference); skip that component rather than fail the whole
            // scan, and count skips so the caller knows coverage was partial.
            var unresolved = new JArray();
            var seenUnresolved = new HashSet<string>();
            int scanErrors = 0;
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                List<SceneObjectReference> refs;
                try { refs = EnumerateSceneRefs(mb); }
                catch { scanErrors++; continue; }

                foreach (var sor in refs)
                {
                    if (!sor.IsValid || known.Contains(sor.RefId)) continue;
                    var key = $"{mb.GetType().Name}:{sor.RefId}";
                    if (!seenUnresolved.Add(key)) continue;
                    unresolved.Add(new JObject
                    {
                        ["refId"] = sor.RefId,
                        ["refType"] = sor.RefType,
                        ["referencedBy"] = mb.GetType().Name,
                        ["gameObject"] = mb.name
                    });
                }
            }

            var result = new JObject
            {
                ["registeredCount"] = refArr.Count,
                ["refIds"] = refArr,
                ["duplicateRefIds"] = duplicates,
                ["componentsWithEmptyRefId"] = empties,
                ["unresolvedReferences"] = unresolved,
                ["scanErrors"] = scanErrors
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// All live <see cref="IReferenceable"/> components in the loaded scene(s) — the true runtime
        /// registry set. Spans <see cref="ReferenceableComponent"/>, <see cref="Molca.Sequence.Step"/>,
        /// <c>SequenceController</c>, and any custom implementer, rather than a single concrete type.
        /// Shared by <c>molca_refids</c> and <c>molca_fix_refids</c> so both agree on what is "known".
        /// </summary>
        internal static IEnumerable<MonoBehaviour> FindLiveReferenceables()
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb != null && mb is IReferenceable)
                    yield return mb;
            }
        }

        /// <summary>
        /// Collects every <see cref="SceneObjectReference"/> held by a component, including those in
        /// arrays and lists, via reflection over serializable fields. Each field read is guarded so a
        /// single throwing getter (e.g. an unassigned Unity reference) skips only that field, not the
        /// whole component.
        /// </summary>
        private static List<SceneObjectReference> EnumerateSceneRefs(MonoBehaviour mb)
        {
            var result = new List<SceneObjectReference>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in mb.GetType().GetFields(flags))
            {
                try
                {
                    if (field.FieldType == typeof(SceneObjectReference))
                    {
                        result.Add((SceneObjectReference)field.GetValue(mb));
                    }
                    else if (typeof(IEnumerable).IsAssignableFrom(field.FieldType)
                             && field.FieldType != typeof(string))
                    {
                        if (field.GetValue(mb) is IEnumerable seq)
                            foreach (var item in seq)
                                if (item is SceneObjectReference sor)
                                    result.Add(sor);
                    }
                }
                catch
                {
                    // Field unreadable (unassigned reference, faulting getter); skip it.
                }
            }
            return result;
        }
    }
}
