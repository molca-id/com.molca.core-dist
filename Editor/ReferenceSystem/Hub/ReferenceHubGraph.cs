using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// Builds the focused neighbourhood graph for the current selection, as Mermaid flowchart source.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately <b>not</b> a project-wide graph. A project graph of every reference is unreadable
    /// past a few dozen nodes and answers no question the tables do not answer better; what a graph is good
    /// for is "what does this one thing point at, and what points at it". So the graph is always rooted at the
    /// selected row and expands one hop, bounded by <see cref="MaxNeighbours"/>, and says out loud when it
    /// truncated rather than silently drawing a subset.</para>
    ///
    /// <para>Pure: takes a snapshot and a row, returns text. No Unity API, so the layout can be tested.</para>
    /// </remarks>
    internal static class ReferenceHubGraph
    {
        /// <summary>Maximum neighbours drawn on each side of the selection.</summary>
        internal const int MaxNeighbours = 12;

        /// <summary>
        /// Builds Mermaid flowchart source for the neighbourhood of <paramref name="row"/>.
        /// </summary>
        /// <param name="snapshot">The snapshot the row came from.</param>
        /// <param name="row">The selected row. Null yields a placeholder graph.</param>
        /// <returns>Mermaid <c>flowchart LR</c> source; never null or empty.</returns>
        internal static string BuildMermaid(ReferenceAuditSnapshot snapshot, ReferenceHubRow row)
        {
            if (snapshot == null || row == null)
                return "flowchart LR\n  none[Select a row to see its neighbourhood]";

            var text = new StringBuilder();
            text.AppendLine("flowchart LR");

            var ids = new NodeIds();

            if (row.Kind == ReferenceHubRowKind.Provider || !string.IsNullOrEmpty(row.ProviderKey))
            {
                var provider = snapshot.FindProvider(row.ProviderKey);
                if (provider != null)
                {
                    AppendProviderNeighbourhood(text, ids, snapshot, provider);
                    return text.ToString();
                }
            }

            var resolution = snapshot.FindResolution(row.SiteKey);
            if (resolution != null)
            {
                AppendSiteNeighbourhood(text, ids, resolution);
                return text.ToString();
            }

            // A finding with neither a resolvable site nor a provider (a coverage finding, say) has no
            // neighbourhood at all, and drawing a lone box labelled with the code is more honest than
            // inventing edges.
            text.AppendLine($"  only[\"{Escape(row.Title)}\"]");
            return text.ToString();
        }

        /// <summary>The graph rooted at one provider: every reference that points at, or claims, it.</summary>
        private static void AppendProviderNeighbourhood(
            StringBuilder text, NodeIds ids, ReferenceAuditSnapshot snapshot, ReferenceProviderRecord provider)
        {
            var target = ids.For(provider.ProviderKey);
            text.AppendLine($"  {target}([\"{Escape(Describe(provider))}\"])");

            var resolved = new List<ReferenceSiteResolution>();
            var claiming = new List<ReferenceSiteResolution>();

            foreach (var resolution in snapshot.Resolutions)
            {
                if (resolution.Resolved?.ProviderKey == provider.ProviderKey)
                    resolved.Add(resolution);
                else if (!string.IsNullOrEmpty(provider.RefId)
                         && string.Equals(resolution.Site.StoredRefId, provider.RefId, StringComparison.Ordinal))
                    claiming.Add(resolution);
            }

            AppendInbound(text, ids, resolved.Take(MaxNeighbours), target, resolvedEdge: true);
            AppendInbound(text, ids, claiming.Take(MaxNeighbours), target, resolvedEdge: false);

            AppendTruncation(text, ids, target, resolved.Count + claiming.Count, MaxNeighbours * 2);

            // A provider the runtime registry never holds is the difference between "referenced" and
            // "resolvable", so the graph states it rather than leaving a healthy-looking node.
            if (!provider.IsRuntimeResolvable)
            {
                var note = ids.For("note:inert");
                text.AppendLine($"  {note}{{\"not registered at runtime\"}}");
                text.AppendLine($"  {target} -.-> {note}");
            }
        }

        private static void AppendInbound(
            StringBuilder text, NodeIds ids, IEnumerable<ReferenceSiteResolution> sources,
            string target, bool resolvedEdge)
        {
            foreach (var resolution in sources)
            {
                var source = ids.For(resolution.Site.SiteKey);
                text.AppendLine($"  {source}[\"{Escape(Describe(resolution.Site))}\"]");
                text.AppendLine(resolvedEdge
                    ? $"  {source} --> {target}"
                    : $"  {source} -.->|\"{Escape(resolution.Outcome.ToString())}\"| {target}");
            }
        }

        /// <summary>The graph rooted at one reference site: the candidates it could resolve to.</summary>
        private static void AppendSiteNeighbourhood(
            StringBuilder text, NodeIds ids, ReferenceSiteResolution resolution)
        {
            var site = resolution.Site;
            var source = ids.For(site.SiteKey);
            text.AppendLine($"  {source}[\"{Escape(Describe(site))}\"]");

            if (resolution.Candidates.Count == 0)
            {
                var missing = ids.For("missing");
                text.AppendLine($"  {missing}{{\"no provider carries {Escape(site.StoredRefId)}\"}}");
                text.AppendLine($"  {source} -.->|\"{Escape(resolution.Outcome.ToString())}\"| {missing}");
                return;
            }

            var resolvedKey = resolution.Resolved?.ProviderKey;
            foreach (var candidate in resolution.Candidates.Take(MaxNeighbours))
            {
                var target = ids.For(candidate.ProviderKey);
                text.AppendLine($"  {target}([\"{Escape(Describe(candidate))}\"])");

                // A solid arrow means "this is what you get"; a dashed one means "this also matched and is
                // why you get nothing". With several candidates the distinction is the entire finding.
                text.AppendLine(candidate.ProviderKey == resolvedKey
                    ? $"  {source} --> {target}"
                    : $"  {source} -.->|\"also matches\"| {target}");
            }

            AppendTruncation(text, ids, source, resolution.Candidates.Count, MaxNeighbours);
        }

        private static void AppendTruncation(
            StringBuilder text, NodeIds ids, string anchor, int total, int shown)
        {
            if (total <= shown)
                return;

            var more = ids.For("more");
            text.AppendLine($"  {more}[\"+{total - shown} more (see the table)\"]");
            text.AppendLine($"  {anchor} -.-> {more}");
        }

        private static string Describe(ReferenceProviderRecord provider)
        {
            var name = string.IsNullOrEmpty(provider.DisplayName)
                ? provider.Locator.ObjectPath
                : provider.DisplayName;
            return $"{name}\n{provider.RefType}:{provider.RefId}";
        }

        private static string Describe(ReferenceSiteRecord site) =>
            $"{ShortOwner(site.OwnerLocator.ObjectPath)}\n{site.PropertyPath}";

        private static string ShortOwner(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath))
                return "(unnamed)";
            var slash = objectPath.LastIndexOf('/');
            return slash >= 0 && slash < objectPath.Length - 1 ? objectPath.Substring(slash + 1) : objectPath;
        }

        // Mermaid node labels are delimited by quotes and brackets, and a Molca display name or hierarchy
        // path can legitimately contain either. Escaping here keeps a stray bracket from turning one node
        // into an unparseable line — which would silently drop the rest of the graph.
        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text
                .Replace("\"", "'")
                .Replace("[", "(")
                .Replace("]", ")")
                .Replace("{", "(")
                .Replace("}", ")")
                .Replace("|", "/")
                .Replace("\r", string.Empty)
                .Replace("\n", "<br/>");
        }

        /// <summary>
        /// Maps snapshot keys to short, Mermaid-legal node identifiers, stably within one graph.
        /// </summary>
        private sealed class NodeIds
        {
            private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

            internal string For(string key)
            {
                if (_ids.TryGetValue(key ?? string.Empty, out var id))
                    return id;

                id = "n" + _ids.Count;
                _ids[key ?? string.Empty] = id;
                return id;
            }
        }
    }
}
