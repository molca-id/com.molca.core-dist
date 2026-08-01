using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Editor.ReferenceSystem;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Pure, read-only builder that assembles a <see cref="FrameworkGraphSnapshot"/> from the framework's
    /// existing introspection sources — RuntimeManager (subsystems/services) and the ReferenceSystem —
    /// plus whatever <see cref="IFrameworkGraphContributor"/>s are installed (e.g. <c>com.molca.sequence</c>
    /// contributes the Sequence layer). GUI-free so the same snapshot serves the editor window and the
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

        /// <summary>
        /// Projects the shared reference audit into graph nodes and edges.
        /// </summary>
        /// <remarks>
        /// Reads <see cref="ReferenceAuditService"/> rather than scanning, which fixes three gaps at once.
        /// Every provider kind now appears — <c>Step</c>, <c>SequenceController</c> and custom
        /// <see cref="IReferenceable"/> implementers used to be invisible because the layer looked only for
        /// <see cref="ReferenceableComponent"/>. Every reference site now appears, including ones nested in
        /// serializable structs and <c>[SerializeReference]</c> graphs that reflection over top-level fields
        /// missed, and ones owned by objects that are not themselves providers, whose edges were dropped
        /// entirely. And duplicates are keyed on the exact <c>(RefType, RefId)</c> pair, so two legal
        /// same-id/different-type providers are no longer flagged as an error.
        /// </remarks>
        private static void BuildReferenceLayer(FrameworkGraphSnapshot snapshot)
        {
            var audit = ReferenceAuditService.GetOrRun(ReferenceAuditScope.OpenScenes());

            // Duplicates are keyed on the exact pair the runtime registry uses.
            var duplicatedKeys = new HashSet<string>(
                audit.Providers
                    .Where(p => p.IsRuntimeResolvable && !string.IsNullOrEmpty(p.RefId))
                    .GroupBy(p => p.RefType + "|" + p.RefId, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.Ordinal);

            foreach (var provider in audit.Providers)
            {
                if (string.IsNullOrEmpty(provider.RefId))
                {
                    snapshot.AddNode(new FrameworkGraphNode(
                        "ref:empty:" + provider.Locator.Key, provider.DisplayName, FrameworkNodeCategory.Reference)
                    {
                        Subtitle = provider.Locator.ObjectPath,
                        Severity = FrameworkGraphSeverity.Error,
                    }.With("issue", "empty Ref Id").With("runtimeType", provider.RuntimeTypeName));
                    continue;
                }

                var node = snapshot.AddNode(new FrameworkGraphNode(
                    ReferenceId(provider.RefId), provider.RefId, FrameworkNodeCategory.Reference)
                {
                    Subtitle = provider.DisplayName,
                }
                .With("refType", provider.RefType)
                .With("gameObject", provider.DisplayName)
                .With("runtimeType", provider.RuntimeTypeName)
                .With("providerKind", provider.Kind.ToString()));

                if (!provider.IsRuntimeResolvable)
                {
                    // Present as authored data, but not resolvable at runtime — worth showing, not an error.
                    node.With("issue", "not runtime-resolvable (" + provider.Kind + ")");
                }
                else if (duplicatedKeys.Contains(provider.RefType + "|" + provider.RefId))
                {
                    node.Severity = FrameworkGraphSeverity.Error;
                    node.With("issue", "duplicate Ref Id");
                }
            }

            // Locator -> owning provider, so a site declared by a provider attaches its edge to that
            // provider's node rather than to a second node for the same object.
            var providerByLocator = new Dictionary<string, ReferenceProviderRecord>(StringComparer.Ordinal);
            foreach (var provider in audit.Providers.Where(p => !string.IsNullOrEmpty(p.RefId)))
                providerByLocator[provider.Locator.Key] = provider;

            foreach (var resolution in audit.Resolutions)
            {
                var site = resolution.Site;
                if (!site.IsAssigned)
                    continue;

                var targetId = ReferenceId(site.StoredRefId);
                if (!snapshot.HasNode(targetId))
                {
                    snapshot.AddNode(new FrameworkGraphNode(
                        targetId, site.StoredRefId, FrameworkNodeCategory.Reference)
                    {
                        Subtitle = "unresolved",
                        Severity = FrameworkGraphSeverity.Error,
                    }.With("refType", site.StoredRefType).With("issue", resolution.Outcome.ToString()));
                }

                snapshot.AddEdge(new FrameworkGraphEdge(
                    OwnerNodeId(snapshot, site, providerByLocator), targetId, FrameworkEdgeKind.SceneReference));
            }

            if (!audit.Coverage.IsComplete)
            {
                snapshot.AddUnavailable(
                    $"References: partial coverage ({audit.Coverage.DescribeGaps()}). "
                    + "Some references may be missing from this graph.");
            }

            // The graph reuses the cached snapshot rather than paying for a project-wide audit on every
            // rebuild. Saying when that snapshot is behind the project beats presenting it as current.
            if (ReferenceAuditService.IsStale)
            {
                snapshot.AddUnavailable(
                    $"References: the audit snapshot is stale ({ReferenceAuditService.StaleReason}). "
                    + "Re-run the reference audit for an up-to-date graph.");
            }
        }

        /// <summary>
        /// Node id for the object declaring <paramref name="site"/>: its own reference node when it is a
        /// provider, otherwise a source node created for it.
        /// </summary>
        /// <remarks>
        /// A non-provider owner — a plain MonoBehaviour, or a ScriptableObject holding an outbound
        /// reference — used to have its edges silently dropped, which made the graph look like nothing
        /// referenced the target.
        /// </remarks>
        private static string OwnerNodeId(
            FrameworkGraphSnapshot snapshot,
            ReferenceSiteRecord site,
            Dictionary<string, ReferenceProviderRecord> providerByLocator)
        {
            if (providerByLocator.TryGetValue(site.OwnerLocator.Key, out var owner)
                && snapshot.HasNode(ReferenceId(owner.RefId)))
            {
                return ReferenceId(owner.RefId);
            }

            var sourceId = SourceNodeId(site);
            if (!snapshot.HasNode(sourceId))
            {
                snapshot.AddNode(new FrameworkGraphNode(
                    sourceId, site.OwnerLocator.ObjectPath, FrameworkNodeCategory.Reference)
                {
                    Subtitle = site.OwnerLocator.TypeName,
                }
                .With("sourceKind", site.SourceKind.ToString())
                .With("asset", site.OwnerLocator.AssetPath));
            }

            return sourceId;
        }

        private static string SourceNodeId(ReferenceSiteRecord site) => "ref:source:" + site.OwnerLocator.Key;
    }
}
