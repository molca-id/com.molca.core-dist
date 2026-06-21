using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.ReferenceSystem;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Pure, read-only builder that assembles a <see cref="FrameworkGraphSnapshot"/> from the framework's
    /// existing introspection sources — RuntimeManager (subsystems/services), the ReferenceSystem, and
    /// loaded SequenceControllers. GUI-free so the same snapshot serves the editor window and the
    /// <c>molca_framework_graph</c> MCP export.
    /// </summary>
    /// <remarks>
    /// The builder never mutates serialized data and never resolves ScriptableObjects as scene reference
    /// targets (preserves the SOs-out boundary). Subsystem and service layers require Play mode; in Edit
    /// mode they record an entry in <see cref="FrameworkGraphSnapshot.UnavailableReasons"/> rather than
    /// emitting nothing. Every per-object read is guarded so one faulting component cannot abort the scan.
    /// </remarks>
    public static class FrameworkGraphBuilder
    {
        /// <summary>Builds a full snapshot of the loaded project's framework topology.</summary>
        public static FrameworkGraphSnapshot Build()
        {
            var snapshot = new FrameworkGraphSnapshot { IsPlayMode = Application.isPlaying };

            BuildSubsystemLayer(snapshot);
            BuildServiceLayer(snapshot);
            BuildReferenceLayer(snapshot);
            BuildSequenceLayer(snapshot);
            InvokeContributors(snapshot);

            return snapshot;
        }

        /// <summary>
        /// Discovers every <see cref="IFrameworkGraphContributor"/> implementor via <c>TypeCache</c> and
        /// lets each add its read-only nodes/edges (Sprint 22.8 fork extension). Test assemblies are
        /// skipped so test fixtures can't pollute a real graph. Each contributor is wrapped in try/catch
        /// and only parameterless types are instantiated, so a faulty fork contributor can't break Core.
        /// </summary>
        private static void InvokeContributors(FrameworkGraphSnapshot snapshot)
            => RunContributors(snapshot, DiscoverContributors());

        /// <summary>
        /// Discovers fork graph contributors via <c>TypeCache</c>: concrete, parameterless
        /// <see cref="IFrameworkGraphContributor"/> implementors outside test assemblies (so test fixtures
        /// can't pollute a real graph). Each is instantiated defensively.
        /// </summary>
        public static IEnumerable<IFrameworkGraphContributor> DiscoverContributors()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<IFrameworkGraphContributor>())
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;
                var asm = type.Assembly.GetName().Name;
                if (asm != null && (asm.EndsWith(".Tests", StringComparison.Ordinal) || asm.EndsWith("Tests", StringComparison.Ordinal)))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                IFrameworkGraphContributor contributor = null;
                try { contributor = (IFrameworkGraphContributor)Activator.CreateInstance(type); }
                catch (Exception ex) { Debug.LogWarning($"[Molca FrameworkGraph] Could not create contributor '{type.Name}': {ex.Message}"); }
                if (contributor != null) yield return contributor;
            }
        }

        /// <summary>
        /// Lets each contributor add its read-only nodes/edges, isolating failures: a throwing contributor
        /// is recorded in <see cref="FrameworkGraphSnapshot.UnavailableReasons"/> and never aborts the graph.
        /// </summary>
        public static void RunContributors(FrameworkGraphSnapshot snapshot, IEnumerable<IFrameworkGraphContributor> contributors)
        {
            if (snapshot == null || contributors == null) return;
            foreach (var contributor in contributors)
            {
                if (contributor == null) continue;
                try
                {
                    contributor.Contribute(snapshot);
                }
                catch (Exception ex)
                {
                    snapshot.AddUnavailable($"Fork graph contributor '{contributor.GetType().Name}' failed: {ex.Message}");
                }
            }
        }

        // --- ids -------------------------------------------------------------------------------------

        private static string SubsystemId(System.Type t) => "subsystem:" + (t.FullName ?? t.Name);
        private static string ServiceId(System.Type t) => "service:" + (t?.FullName ?? t?.Name ?? "?");
        private static string ReferenceId(string refId) => "ref:" + refId;
        private static string SequenceId(string refId, string name) => "seq:" + (string.IsNullOrEmpty(refId) ? name : refId);
        private static string StepId(string refId, int fallback) => "step:" + (string.IsNullOrEmpty(refId) ? "#" + fallback : refId);

        // --- subsystem layer (Play only) -------------------------------------------------------------

        private static void BuildSubsystemLayer(FrameworkGraphSnapshot snapshot)
        {
            if (!snapshot.IsPlayMode)
            {
                snapshot.AddUnavailable("Subsystems: requires Play mode (the resolved init order only exists after bootstrap).");
                return;
            }

            var subsystems = RuntimeManager.GetSubsystems();
            if (subsystems == null) return;

            // One node per subsystem instance.
            foreach (var s in subsystems)
            {
                if (s == null) continue;
                var type = s.GetType();
                snapshot.AddNode(new FrameworkGraphNode(SubsystemId(type), type.Name, FrameworkNodeCategory.Subsystem)
                {
                    Subtitle = type.FullName,
                    RuntimeOnly = true,
                }
                .With("mode", s.Mode.ToString())
                .With("isActive", s.IsActive.ToString())
                .With("initializationPriority", s.InitializationPriority.ToString()));
            }

            // [DependsOn] edges: dependant → matching dependency subsystem (matched by assignability).
            foreach (var s in subsystems)
            {
                if (s == null) continue;
                var type = s.GetType();
                foreach (var attr in type.GetCustomAttributes<DependsOnAttribute>(inherit: true))
                {
                    foreach (var dep in attr.Dependencies)
                    {
                        if (dep == null) continue;
                        var match = subsystems.FirstOrDefault(o => o != null && dep.IsInstanceOfType(o));
                        if (match != null)
                            snapshot.AddEdge(new FrameworkGraphEdge(
                                SubsystemId(type), SubsystemId(match.GetType()), FrameworkEdgeKind.DependsOn));
                    }
                }
            }

            // Resolved init order: chain consecutive entries.
            var order = RuntimeManager.GetResolvedInitOrder();
            if (order != null)
            {
                FrameworkGraphNode prev = null;
                foreach (var s in order)
                {
                    if (s == null) continue;
                    var node = snapshot.FindNode(SubsystemId(s.GetType()));
                    if (node == null) continue;
                    if (prev != null)
                        snapshot.AddEdge(new FrameworkGraphEdge(prev.Id, node.Id, FrameworkEdgeKind.InitOrder));
                    prev = node;
                }
            }
        }

        // --- service layer (Play only) ---------------------------------------------------------------

        private static void BuildServiceLayer(FrameworkGraphSnapshot snapshot)
        {
            if (!snapshot.IsPlayMode)
            {
                snapshot.AddUnavailable("Services: requires Play mode (the DI container is populated at bootstrap).");
                return;
            }

            var registrations = RuntimeManager.GetServiceRegistrations();
            if (registrations == null) return;

            foreach (var s in registrations)
            {
                if (s.ServiceType == null) continue;
                var node = snapshot.AddNode(new FrameworkGraphNode(
                    ServiceId(s.ServiceType), s.ServiceType.Name, FrameworkNodeCategory.Service)
                {
                    Subtitle = s.ServiceType.FullName,
                    RuntimeOnly = true,
                }
                .With("implementation", s.ImplementationType?.Name)
                .With("lifetime", s.Lifetime.ToString())
                .With("isFactory", s.IsFactory.ToString())
                .With("hasInstance", s.HasInstance.ToString()));

                // If the implementation is itself a subsystem node, link the binding.
                if (s.ImplementationType != null)
                {
                    var implNode = snapshot.FindNode(SubsystemId(s.ImplementationType));
                    if (implNode != null)
                        snapshot.AddEdge(new FrameworkGraphEdge(
                            node.Id, implNode.Id, FrameworkEdgeKind.ServiceBinding));
                }
            }
        }

        // --- reference layer (Edit + Play) -----------------------------------------------------------

        private static void BuildReferenceLayer(FrameworkGraphSnapshot snapshot)
        {
            var referenceables = UnityEngine.Object.FindObjectsByType<ReferenceableComponent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var known = new HashSet<string>();
            foreach (var rc in referenceables)
            {
                if (rc == null) continue;
                try
                {
                    var id = rc.RefId;
                    if (string.IsNullOrEmpty(id))
                    {
                        // Empty Ref Id: surface as a problem node keyed on the GameObject.
                        snapshot.AddNode(new FrameworkGraphNode(
                            "ref:empty:" + rc.GetInstanceID(), rc.name, FrameworkNodeCategory.Reference)
                        {
                            Subtitle = rc.name,
                            Severity = FrameworkGraphSeverity.Error,
                        }.With("issue", "empty Ref Id"));
                        continue;
                    }

                    bool duplicate = !known.Add(id);
                    var node = snapshot.AddNode(new FrameworkGraphNode(
                        ReferenceId(id), id, FrameworkNodeCategory.Reference)
                    {
                        Subtitle = rc.name,
                    }.With("refType", rc.RefType).With("gameObject", rc.name));

                    if (duplicate)
                    {
                        node.Severity = FrameworkGraphSeverity.Error;
                        node.With("issue", "duplicate Ref Id");
                    }
                }
                catch
                {
                    // A faulting ReferenceableComponent must not abort the whole layer.
                }
            }

            // SceneObjectReference edges (and unresolved-target nodes). Edges are emitted only where the
            // owner is itself a ReferenceableComponent, so both endpoints are reference nodes.
            int scanErrors = 0;
            foreach (var rc in referenceables)
            {
                if (rc == null) continue;
                string ownerId;
                try { ownerId = string.IsNullOrEmpty(rc.RefId) ? null : ReferenceId(rc.RefId); }
                catch { continue; }
                if (ownerId == null || !snapshot.HasNode(ownerId)) continue;

                List<SceneObjectReference> refs;
                try { refs = EnumerateSceneRefs(rc); }
                catch { scanErrors++; continue; }

                foreach (var sor in refs)
                {
                    if (!sor.IsValid) continue;
                    var targetId = ReferenceId(sor.RefId);
                    if (!snapshot.HasNode(targetId))
                    {
                        // Unresolved: the referenced Ref Id is backed by no ReferenceableComponent.
                        snapshot.AddNode(new FrameworkGraphNode(targetId, sor.RefId, FrameworkNodeCategory.Reference)
                        {
                            Subtitle = "unresolved",
                            Severity = FrameworkGraphSeverity.Error,
                        }.With("refType", sor.RefType).With("issue", "unresolved reference"));
                    }
                    snapshot.AddEdge(new FrameworkGraphEdge(ownerId, targetId, FrameworkEdgeKind.SceneReference));
                }
            }

            if (scanErrors > 0)
                snapshot.AddUnavailable($"References: {scanErrors} component(s) could not be fully scanned (partial coverage).");
        }

        /// <summary>
        /// Collects every <see cref="SceneObjectReference"/> held by a component (including inside arrays
        /// and lists) via reflection, guarding each field read so one faulting getter skips only that
        /// field. Mirrors the resilient scan in <c>molca_refids</c>.
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

        // --- sequence layer (Edit + Play) ------------------------------------------------------------

        private static void BuildSequenceLayer(FrameworkGraphSnapshot snapshot)
        {
            var controllers = UnityEngine.Object.FindObjectsByType<SequenceController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var controller in controllers)
            {
                if (controller == null) continue;

                string controllerId;
                try { controllerId = SequenceId(controller.RefId, controller.name); }
                catch { continue; }

                snapshot.AddNode(new FrameworkGraphNode(controllerId, controller.name, FrameworkNodeCategory.Sequence)
                {
                    Subtitle = string.IsNullOrEmpty(controller.RefId) ? null : controller.RefId,
                }.With("refId", controller.RefId));

                // Steps: a node each, parent → child = StepFlow (matches the sequence graph editor's
                // edges-as-execution-flow semantics). The controller is the implicit root.
                Step[] steps;
                try { steps = controller.GetComponentsInChildren<Step>(true); }
                catch { continue; }

                int i = 0;
                var stepNodeByStep = new Dictionary<Step, string>();
                foreach (var step in steps)
                {
                    if (step == null) continue;
                    string sid;
                    try { sid = StepId(step.RefId, i++); }
                    catch { continue; }
                    stepNodeByStep[step] = sid;

                    snapshot.AddNode(new FrameworkGraphNode(sid, step.name, FrameworkNodeCategory.Step)
                    {
                        Subtitle = step.GetType().Name,
                    }.With("refId", step.RefId).With("type", step.GetType().Name));
                }

                foreach (var pair in stepNodeByStep)
                {
                    var step = pair.Key;
                    var sid = pair.Value;
                    Step parentStep = null;
                    try
                    {
                        var parentTransform = step.transform.parent;
                        if (parentTransform != null)
                            parentStep = parentTransform.GetComponentInParent<Step>(true);
                    }
                    catch { /* leave parentStep null → root */ }

                    var parentId = parentStep != null && stepNodeByStep.TryGetValue(parentStep, out var pid)
                        ? pid
                        : controllerId;
                    snapshot.AddEdge(new FrameworkGraphEdge(parentId, sid, FrameworkEdgeKind.StepFlow));
                }

                ApplyValidationFindings(snapshot, controller, controllerId, stepNodeByStep);
            }
        }

        /// <summary>
        /// Runs <see cref="SequenceValidator"/> over a controller (Sprint 22.6) and folds its findings
        /// into node badges: a step-scoped finding raises its step node's severity; a controller-scoped
        /// finding raises the controller node. Each affected node accumulates the finding messages under
        /// a <c>findings</c> property so the detail panel and MCP export can show the evidence.
        /// </summary>
        private static void ApplyValidationFindings(
            FrameworkGraphSnapshot snapshot, SequenceController controller, string controllerId,
            Dictionary<Step, string> stepNodeByStep)
        {
            List<SequenceFinding> findings;
            try { findings = SequenceValidator.Validate(controller); }
            catch { return; }
            if (findings == null) return;

            foreach (var f in findings)
            {
                if (f == null) continue;
                var nodeId = f.Step != null && stepNodeByStep.TryGetValue(f.Step, out var sid)
                    ? sid
                    : controllerId;
                var node = snapshot.FindNode(nodeId);
                if (node == null) continue;

                var severity = MapSeverity(f.Severity);
                if (severity > node.Severity) node.Severity = severity;

                // Accumulate messages (one node can carry several findings).
                node.Properties.TryGetValue("findings", out var existing);
                node.Properties["findings"] = string.IsNullOrEmpty(existing)
                    ? f.Message
                    : existing + " | " + f.Message;
            }
        }

        private static FrameworkGraphSeverity MapSeverity(SequenceFindingSeverity severity) => severity switch
        {
            SequenceFindingSeverity.Error => FrameworkGraphSeverity.Error,
            SequenceFindingSeverity.Warning => FrameworkGraphSeverity.Warning,
            _ => FrameworkGraphSeverity.Info,
        };
    }
}
