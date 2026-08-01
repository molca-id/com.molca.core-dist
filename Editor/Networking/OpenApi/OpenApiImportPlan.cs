using System;
using System.Collections.Generic;
using System.Text;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.OpenApi
{
    /// <summary>What importing one operation would do to the target collection.</summary>
    internal enum OpenApiImportAction
    {
        /// <summary>No endpoint matches this operation; import would create one.</summary>
        Add = 0,

        /// <summary>An imported endpoint matches and the spec changed; import would rewrite it.</summary>
        Update,

        /// <summary>An imported endpoint matches and nothing changed; import would leave it alone.</summary>
        Unchanged,

        /// <summary>
        /// An endpoint occupies this operation's identity but was not created by import. Skipped.
        /// </summary>
        Conflict,
    }

    /// <summary>One operation's entry in the import diff.</summary>
    internal sealed class OpenApiImportEntry
    {
        /// <summary>The operation this entry describes.</summary>
        public OpenApiOperation Operation { get; }

        /// <summary>What import would do.</summary>
        public OpenApiImportAction Action { get; }

        /// <summary>The endpoint ID that would be written, or the existing one that matched.</summary>
        public string EndpointId { get; }

        /// <summary>Why this entry is a conflict or an update, in a sentence. Empty for a plain add.</summary>
        public string Reason { get; }

        /// <summary>Field-level changes an update would make. Empty otherwise.</summary>
        public IReadOnlyList<string> Changes { get; }

        /// <summary>Creates an entry.</summary>
        public OpenApiImportEntry(
            OpenApiOperation operation,
            OpenApiImportAction action,
            string endpointId,
            string reason = null,
            IReadOnlyList<string> changes = null)
        {
            Operation = operation;
            Action = action;
            EndpointId = endpointId ?? string.Empty;
            Reason = reason ?? string.Empty;
            Changes = changes ?? Array.Empty<string>();
        }

        /// <summary>Renders the entry as one diff line.</summary>
        public override string ToString()
        {
            string marker = Action switch
            {
                OpenApiImportAction.Add => "+",
                OpenApiImportAction.Update => "~",
                OpenApiImportAction.Conflict => "!",
                _ => " ",
            };

            string detail = Changes.Count > 0
                ? "  (" + string.Join(", ", Changes) + ")"
                : string.IsNullOrEmpty(Reason) ? string.Empty : "  — " + Reason;

            return $"{marker} {Operation.Method,-6} {Operation.Path}  →  {EndpointId}{detail}";
        }
    }

    /// <summary>
    /// The reviewable diff between a spec and an endpoint collection.
    /// </summary>
    /// <remarks>
    /// Computed before anything is written, and computed <b>purely</b> — a plan is a function of the
    /// document plus the collection, with no <c>AssetDatabase</c> writes and no Undo group. That is what
    /// makes the preview trustworthy: the thing shown is the thing that will happen, and re-running the
    /// plan after an apply produces all-<see cref="OpenApiImportAction.Unchanged"/>.
    /// </remarks>
    internal sealed class OpenApiImportPlan
    {
        /// <summary>The spec being imported.</summary>
        public OpenApiDocument Document { get; }

        /// <summary>The collection being imported into.</summary>
        public NetworkEndpointCollection Collection { get; }

        /// <summary>The service the imported endpoints will belong to.</summary>
        public string ServiceId { get; }

        /// <summary>One entry per operation, in spec order.</summary>
        public IReadOnlyList<OpenApiImportEntry> Entries { get; }

        /// <summary>Endpoints previously imported from this spec whose operation is gone from it.</summary>
        /// <remarks>
        /// Reported, never deleted. An operation missing from a newer spec might be genuinely retired, or
        /// the spec might have been trimmed for an unrelated reason — and a stale endpoint template costs
        /// nothing while a deleted one loses whatever policy an author attached to it.
        /// </remarks>
        public IReadOnlyList<string> Orphans { get; }

        /// <summary>Operations that would be created.</summary>
        public int AddCount => Count(OpenApiImportAction.Add);

        /// <summary>Operations whose endpoint would be rewritten.</summary>
        public int UpdateCount => Count(OpenApiImportAction.Update);

        /// <summary>Operations already up to date.</summary>
        public int UnchangedCount => Count(OpenApiImportAction.Unchanged);

        /// <summary>Operations blocked by a hand-authored endpoint.</summary>
        public int ConflictCount => Count(OpenApiImportAction.Conflict);

        /// <summary>Whether applying this plan would change anything.</summary>
        public bool HasWork => AddCount > 0 || UpdateCount > 0;

        internal OpenApiImportPlan(
            OpenApiDocument document,
            NetworkEndpointCollection collection,
            string serviceId,
            IReadOnlyList<OpenApiImportEntry> entries,
            IReadOnlyList<string> orphans)
        {
            Document = document;
            Collection = collection;
            ServiceId = serviceId ?? string.Empty;
            Entries = entries ?? Array.Empty<OpenApiImportEntry>();
            Orphans = orphans ?? Array.Empty<string>();
        }

        private int Count(OpenApiImportAction action)
        {
            int count = 0;
            foreach (var entry in Entries)
            {
                if (entry.Action == action) count++;
            }
            return count;
        }

        /// <summary>A one-line summary.</summary>
        public string Summarize() =>
            $"{AddCount} to add, {UpdateCount} to update, {UnchangedCount} unchanged, " +
            $"{ConflictCount} conflict(s), {Orphans.Count} orphan(s)";

        /// <summary>The full reviewable diff.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine(Document.Summarize());
            text.Append("Into collection '").Append(Collection != null ? Collection.DisplayName : "<none>")
                .Append("' for service '").Append(ServiceId).AppendLine("'");
            text.AppendLine(Summarize());
            text.AppendLine();

            foreach (var entry in Entries)
            {
                if (entry.Action == OpenApiImportAction.Unchanged) continue;
                text.AppendLine(entry.ToString());
            }

            if (Orphans.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Previously imported, no longer in the spec (left in place):");
                foreach (string orphan in Orphans)
                    text.Append("  ").AppendLine(orphan);
            }

            if (Document.Servers.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Servers the spec declares (not applied — bind a service deliberately):");
                foreach (string server in Document.Servers)
                    text.Append("  ").AppendLine(server);
            }

            if (Document.Warnings.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Parse warnings:");
                foreach (string warning in Document.Warnings)
                    text.Append("  ").AppendLine(warning);
            }

            return text.ToString();
        }
    }
}
