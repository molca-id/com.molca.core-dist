using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using Molca.Sequence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// The <c>molca_fix_refids</c> Action tool (Sprint 20.6): the write counterpart to read-only
    /// <c>molca_refids</c>. Repairs empty and duplicate Ref Ids on every live
    /// <see cref="IReferenceable"/> in the loaded scene(s) — <see cref="ReferenceableComponent"/>,
    /// <see cref="Step"/>, <c>SequenceController</c>, and custom implementers — by regenerating ids via
    /// <see cref="ReferenceGenerator"/>, as one Unity Undo group. Unresolved
    /// <see cref="SceneObjectReference"/> ids are reported but never auto-changed — the intended target
    /// is a human decision, so the tool surfaces them rather than guessing.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateFixRefIdsTool() => new McpToolDefinition(
            name: "molca_fix_refids",
            description: "Repairs empty and duplicate Ref Ids on every IReferenceable in the loaded "
                       + "scene(s) (ReferenceableComponents, Steps, SequenceControllers, custom implementers) "
                       + "by regenerating ids (duplicates keep the first occurrence). "
                       + "Unresolved SceneObjectReference ids are listed but not changed. One undo group; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteFixRefIds,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        /// <summary>A referenceable target with a settable Ref Id, abstracting over component types.</summary>
        private readonly struct RefHolder
        {
            public readonly UnityEngine.Object Owner;
            public readonly string Name;
            public readonly string RefType;
            public readonly Func<string> Get;
            public readonly Action<string> Set;

            public RefHolder(UnityEngine.Object owner, string name, string refType, Func<string> get, Action<string> set)
            {
                Owner = owner;
                Name = name;
                RefType = refType;
                Get = get;
                Set = set;
            }
        }

        private static string ExecuteFixRefIds(string argumentsJson)
        {
            var holders = CollectRefHolders();

            // First pass: empties. Second pass: duplicates (keep first seen per id).
            var changed = new JArray();
            int group = -1;

            void EnsureGroup()
            {
                if (group >= 0) return;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Fix Ref Ids");
                group = Undo.GetCurrentGroup();
            }

            var seen = new HashSet<string>();
            foreach (var h in holders)
            {
                var id = h.Get();
                if (string.IsNullOrWhiteSpace(id))
                {
                    EnsureGroup();
                    var newId = Regenerate(h);
                    changed.Add(Describe(h, "empty", null, newId));
                    continue;
                }
                if (!seen.Add(id))
                {
                    EnsureGroup();
                    var newId = Regenerate(h);
                    changed.Add(Describe(h, "duplicate", id, newId));
                }
            }

            if (group >= 0) Undo.CollapseUndoOperations(group);

            // Report (do not fix) SceneObjectReferences that resolve to nothing after the fixes.
            var knownIds = new HashSet<string>(CollectRefHolders().Select(h => h.Get()).Where(s => !string.IsNullOrEmpty(s)));
            var unresolved = new JArray();
            var seenUnresolved = new HashSet<string>();
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                foreach (var sor in EnumerateSceneRefs(mb))
                {
                    if (!sor.IsValid || knownIds.Contains(sor.RefId)) continue;
                    if (!seenUnresolved.Add($"{mb.GetType().Name}:{sor.RefId}")) continue;
                    unresolved.Add(new JObject
                    {
                        ["refId"] = sor.RefId,
                        ["referencedBy"] = mb.GetType().Name,
                        ["gameObject"] = mb.name
                    });
                }
            }

            return new JObject
            {
                ["fixedCount"] = changed.Count,
                ["changed"] = changed,
                ["unresolvedReferences"] = unresolved,
                ["message"] = unresolved.Count > 0
                    ? "Empty/duplicate ids regenerated. Unresolved references are listed but were not changed (their intended target is unknown)."
                    : "Empty/duplicate ids regenerated."
            }.ToString(Formatting.None);
        }

        private static List<RefHolder> CollectRefHolders()
        {
            var holders = new List<RefHolder>();

            // Every live IReferenceable (ReferenceableComponent, Step, SequenceController, custom
            // implementers) — the same set molca_refids reports against, so read and repair agree.
            foreach (var mb in FindLiveReferenceables())
            {
                var captured = (IReferenceable)mb;
                holders.Add(new RefHolder(mb, mb.name, captured.RefType,
                    () => captured.RefId, v => captured.RefId = v));
            }
            return holders;
        }

        private static string Regenerate(RefHolder holder)
        {
            Undo.RecordObject(holder.Owner, "Fix Ref Id");
            var newId = ReferenceGenerator.GenerateUniqueId(holder.RefType);
            holder.Set(newId);
            EditorUtility.SetDirty(holder.Owner);
            return newId;
        }

        private static JObject Describe(RefHolder holder, string reason, string oldId, string newId) => new JObject
        {
            ["gameObject"] = holder.Name,
            ["refType"] = holder.RefType,
            ["reason"] = reason,
            ["oldRefId"] = oldId,
            ["newRefId"] = newId
        };
    }
}
