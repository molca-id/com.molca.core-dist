using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>What a node in the workspace's row tree stands for.</summary>
    public enum ReferenceHubTreeNodeKind
    {
        /// <summary>A plain row with no children — every node in the flat views.</summary>
        Row = 0,

        /// <summary>A Ref Type heading in the Targets view.</summary>
        RefTypeGroup = 1,

        /// <summary>A target, with the references that name it nested underneath.</summary>
        Target = 2,

        /// <summary>A reference nested under the target it reaches.</summary>
        Inbound = 3,

        /// <summary>A heading for references that reach nothing, or that are unset.</summary>
        LooseGroup = 4,
    }

    /// <summary>
    /// One node of the workspace's row tree: a row, or a heading over rows.
    /// </summary>
    /// <remarks>
    /// Pure derived data holding no live <see cref="UnityEngine.Object"/> and no UIElements type, so the
    /// shape of the Targets view can be asserted without a panel — the same reason
    /// <see cref="ReferenceHubRow"/> is a projection rather than a bound control.
    /// </remarks>
    public sealed class ReferenceHubTreeNode
    {
        /// <summary>What this node stands for.</summary>
        public ReferenceHubTreeNodeKind Kind { get; }

        /// <summary>Stable identity, used to keep expansion and selection across a rescan.</summary>
        public string Key { get; }

        /// <summary>Primary column text.</summary>
        public string Label { get; }

        /// <summary>The <c>RefType:RefId</c> identity column, or a group's counts.</summary>
        public string Identity { get; }

        /// <summary>Where the node lives: asset and owning object.</summary>
        public string Source { get; }

        /// <summary>Resolution or registration state.</summary>
        public string State { get; }

        /// <summary>Trailing note: repair availability, or an inbound count.</summary>
        public string Note { get; }

        /// <summary>Worst severity in this node and everything under it.</summary>
        public ReferenceFindingSeverity Severity { get; }

        /// <summary>Full explanation, shown as the row tooltip.</summary>
        public string Tooltip { get; }

        /// <summary>The row this node carries, or null for a heading.</summary>
        public ReferenceHubRow Row { get; }

        /// <summary>Child nodes, never null.</summary>
        public IReadOnlyList<ReferenceHubTreeNode> Children { get; }

        internal ReferenceHubTreeNode(
            ReferenceHubTreeNodeKind kind,
            string key,
            string label,
            string identity = null,
            string source = null,
            string state = null,
            string note = null,
            ReferenceFindingSeverity severity = ReferenceFindingSeverity.Info,
            string tooltip = null,
            ReferenceHubRow row = null,
            IReadOnlyList<ReferenceHubTreeNode> children = null)
        {
            Kind = kind;
            Key = key ?? string.Empty;
            Label = label ?? string.Empty;
            Identity = identity ?? string.Empty;
            Source = source ?? string.Empty;
            State = state ?? string.Empty;
            Note = note ?? string.Empty;
            Severity = severity;
            Tooltip = tooltip ?? string.Empty;
            Row = row;
            Children = children ?? Array.Empty<ReferenceHubTreeNode>();
        }

        /// <summary>True when this node stands for a row the user can act on.</summary>
        public bool IsActionable => Row != null;

        /// <inheritdoc/>
        public override string ToString() => $"{Kind}: {Label}";
    }

    /// <summary>
    /// Projects the workspace's tables into the tree the table control renders.
    /// </summary>
    /// <remarks>
    /// <para>The flat views project one root per row and stop there. The Targets view is the reason this
    /// exists: it nests <c>Ref Type → target → the references that reach it</c>, because those three are one
    /// graph and the author's question — "what points at this, and is it the only thing claiming that
    /// name?" — is a join across all three. Answering it used to mean reading the Providers table, noting an
    /// id, and searching the References table for it.</para>
    ///
    /// <para>References that reach nothing are not hidden for lack of a parent. They collect under their own
    /// headings, which is also the honest place for them: an unresolved reference has no target to sit
    /// under, and that <i>is</i> the finding.</para>
    /// </remarks>
    public static class ReferenceHubTargetTree
    {
        /// <summary>Heading key for assigned references that resolve to nothing.</summary>
        public const string UnresolvedGroupKey = "group:unresolved";

        /// <summary>Heading key for references that are not assigned at all.</summary>
        public const string UnsetGroupKey = "group:unset";

        /// <summary>Displayed instead of an empty Ref Type, which is legal data and a bad heading.</summary>
        public const string NoRefTypeLabel = "(no Ref Type)";

        /// <summary>Projects a flat table: one root node per row, no children.</summary>
        /// <param name="rows">The already-filtered rows.</param>
        /// <returns>Root nodes in the order given.</returns>
        public static IReadOnlyList<ReferenceHubTreeNode> Flat(IReadOnlyList<ReferenceHubRow> rows) =>
            (rows ?? Array.Empty<ReferenceHubRow>()).Select(FromRow).ToList();

        /// <summary>
        /// Projects the Targets tree.
        /// </summary>
        /// <param name="tables">Every projected row; both providers and sites are needed to nest them.</param>
        /// <param name="filter">The active filter, applied as described below. Null means no filter.</param>
        /// <returns>Ref Type groups, then the loose-reference groups, all in a stable order.</returns>
        /// <remarks>
        /// A target survives the filter when it matches <i>or</i> any reference that names it matches, and
        /// then shows all of its references. Hiding the siblings of a search hit would answer "which
        /// references mention the door" while withholding the thing the author actually needs to see next —
        /// how many other references share that target.
        /// </remarks>
        public static IReadOnlyList<ReferenceHubTreeNode> Targets(
            ReferenceHubTables tables, ReferenceHubFilter filter)
        {
            if (tables == null)
                return Array.Empty<ReferenceHubTreeNode>();

            var inboundByProvider = tables.Sites
                .Where(s => !string.IsNullOrEmpty(s.ProviderKey))
                .GroupBy(s => s.ProviderKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceHubRow>)g.ToList(), StringComparer.Ordinal);

            bool Keep(ReferenceHubRow row) => filter == null || filter.Matches(row);

            var groups = new List<ReferenceHubTreeNode>();

            foreach (var byType in tables.Providers
                         .GroupBy(p => string.IsNullOrEmpty(p.StoredRefType) ? NoRefTypeLabel : p.StoredRefType,
                                  StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var targets = new List<ReferenceHubTreeNode>();

                foreach (var provider in byType)
                {
                    var inbound = inboundByProvider.TryGetValue(provider.ProviderKey, out var found)
                        ? found
                        : Array.Empty<ReferenceHubRow>();

                    if (!Keep(provider) && !inbound.Any(Keep))
                        continue;

                    targets.Add(Target(provider, inbound));
                }

                if (targets.Count == 0)
                    continue;

                var inboundTotal = targets.Sum(t => t.Children.Count);
                groups.Add(new ReferenceHubTreeNode(
                    ReferenceHubTreeNodeKind.RefTypeGroup,
                    key: "type:" + byType.Key,
                    label: byType.Key,
                    identity: $"{targets.Count} target{(targets.Count == 1 ? "" : "s")}",
                    note: $"{inboundTotal} inbound",
                    severity: Worst(targets.Select(t => t.Severity)),
                    tooltip: byType.Key == NoRefTypeLabel
                        ? "These targets declare no Ref Type. Their identity is the Ref Id alone, which is the "
                          + "compatibility path — it refuses to resolve as soon as two of them share an id."
                        : $"Every target whose Ref Type is \"{byType.Key}\".",
                    children: targets));
            }

            var unresolved = tables.Sites
                .Where(s => s.IsAssigned && string.IsNullOrEmpty(s.ProviderKey))
                .Where(Keep)
                .ToList();

            if (unresolved.Count > 0)
            {
                groups.Add(LooseGroup(
                    UnresolvedGroupKey,
                    "Reaches nothing",
                    unresolved,
                    "These references store an identity that no discovered target answers to. Either the "
                    + "target is outside the audited scope, or the identity is wrong."));
            }

            var unset = tables.Sites.Where(s => !s.IsAssigned).Where(Keep).ToList();
            if (unset.Count > 0)
            {
                groups.Add(LooseGroup(
                    UnsetGroupKey,
                    "Not assigned",
                    unset,
                    "These reference fields hold no identity at all. That is a legal authoring choice for an "
                    + "optional reference and a REF006 for a required one."));
            }

            return groups;
        }

        /// <summary>Every node in a tree, depth first — for index lookups and counting.</summary>
        /// <param name="nodes">The roots.</param>
        public static IEnumerable<ReferenceHubTreeNode> Flatten(IEnumerable<ReferenceHubTreeNode> nodes)
        {
            foreach (var node in nodes ?? Array.Empty<ReferenceHubTreeNode>())
            {
                yield return node;
                foreach (var child in Flatten(node.Children))
                    yield return child;
            }
        }

        private static ReferenceHubTreeNode Target(
            ReferenceHubRow provider, IReadOnlyList<ReferenceHubRow> inbound)
        {
            var children = inbound
                .Select(site => new ReferenceHubTreeNode(
                    ReferenceHubTreeNodeKind.Inbound,
                    key: site.Key,
                    label: site.PropertyPath,
                    identity: Identity(site),
                    source: Source(site),
                    state: site.ResolutionState,
                    note: site.IsLegacyFallback ? "legacy fallback" : string.Empty,
                    severity: EffectiveSeverity(site),
                    tooltip: site.Summary,
                    row: site))
                .ToList();

            // A target claimed by more references than reach it is the duplicate-id symptom, so the count
            // says both numbers rather than the flattering one.
            var note = provider.ClaimingCount > provider.InboundCount
                ? $"{provider.InboundCount} in · {provider.ClaimingCount} claim the id"
                : $"{provider.InboundCount} in";

            return new ReferenceHubTreeNode(
                ReferenceHubTreeNodeKind.Target,
                key: provider.Key,
                label: provider.Title,
                identity: Identity(provider),
                source: Source(provider),
                state: provider.ResolutionState,
                note: note,
                severity: Worst(children.Select(c => c.Severity).Append(EffectiveSeverity(provider))),
                tooltip: provider.Summary,
                row: provider,
                children: children);
        }

        private static ReferenceHubTreeNode LooseGroup(
            string key, string label, IReadOnlyList<ReferenceHubRow> rows, string tooltip)
        {
            var children = rows.Select(FromRow).ToList();
            return new ReferenceHubTreeNode(
                ReferenceHubTreeNodeKind.LooseGroup,
                key: key,
                label: label,
                identity: $"{rows.Count} reference{(rows.Count == 1 ? "" : "s")}",
                severity: Worst(children.Select(c => c.Severity)),
                tooltip: tooltip,
                children: children);
        }

        private static ReferenceHubTreeNode FromRow(ReferenceHubRow row) =>
            new ReferenceHubTreeNode(
                ReferenceHubTreeNodeKind.Row,
                key: row.Key,
                label: string.IsNullOrEmpty(row.Code) ? row.Title : $"{row.Code}  {row.Title}",
                identity: Identity(row),
                source: Source(row),
                state: row.ResolutionState,
                note: Note(row),
                severity: EffectiveSeverity(row),
                tooltip: string.IsNullOrEmpty(row.Summary) ? row.Title : row.Summary,
                row: row);

        /// <summary>
        /// The identity column: display name first, machine id second.
        /// </summary>
        /// <remarks>
        /// Ref Ids are <c>ref_&lt;guid&gt;</c> by default, so a column of raw ids is a column of noise. The
        /// type is what an author scans for and it goes first; the id follows so it can still be read and
        /// copied, which is what a duplicate report needs.
        /// </remarks>
        private static string Identity(ReferenceHubRow row)
        {
            if (!row.IsAssigned && row.Kind != ReferenceHubRowKind.Provider)
                return "<unset>";

            var type = string.IsNullOrEmpty(row.StoredRefType) ? "(no type)" : row.StoredRefType;
            return string.IsNullOrEmpty(row.StoredRefId) ? type : $"{type}:{row.StoredRefId}";
        }

        private static string Source(ReferenceHubRow row)
        {
            var asset = string.IsNullOrEmpty(row.AssetPath)
                ? string.Empty
                : System.IO.Path.GetFileNameWithoutExtension(row.AssetPath);

            if (string.IsNullOrEmpty(row.Owner))
                return asset;

            return string.IsNullOrEmpty(asset) ? row.Owner : $"{asset} :: {row.Owner}";
        }

        private static string Note(ReferenceHubRow row) => row.Kind switch
        {
            ReferenceHubRowKind.Provider => $"{row.InboundCount} in",
            ReferenceHubRowKind.Issue => row.Repair switch
            {
                ReferenceHubRepairAvailability.Automatic => "automatic",
                ReferenceHubRepairAvailability.RequiresChoice => "needs a decision",
                _ => string.Empty,
            },
            _ => row.IsLegacyFallback ? "legacy fallback" : string.Empty,
        };

        private static ReferenceFindingSeverity Worst(IEnumerable<ReferenceFindingSeverity> severities)
        {
            var worst = ReferenceFindingSeverity.Info;
            foreach (var severity in severities)
                if (severity > worst)
                    worst = severity;
            return worst;
        }

        /// <summary>
        /// The severity a node should render, which is not always the row's own.
        /// </summary>
        /// <remarks>
        /// A site row carries <c>Info</c> on purpose: the Issues table is where judgements live, and
        /// colouring every optional unset reference amber trains the reader to ignore the colour. The one
        /// exception is the fact that reads as a problem on sight — a reference that asked for a target and
        /// got nothing — because a tree of grey rows containing one broken wire hides the broken wire.
        /// </remarks>
        public static ReferenceFindingSeverity EffectiveSeverity(ReferenceHubRow row)
        {
            if (row == null)
                return ReferenceFindingSeverity.Info;

            if (row.Kind == ReferenceHubRowKind.Issue)
                return row.Severity;

            var unresolved = row.Kind == ReferenceHubRowKind.Site
                && row.IsAssigned
                && !row.ResolutionState.StartsWith("Resolved", StringComparison.Ordinal);

            return unresolved ? ReferenceFindingSeverity.Error : ReferenceFindingSeverity.Info;
        }
    }
}
