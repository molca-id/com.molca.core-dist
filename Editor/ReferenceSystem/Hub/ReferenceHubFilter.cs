using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>Whether a row's reference is set, unset, or either.</summary>
    public enum ReferenceHubAssignmentFilter
    {
        /// <summary>No requiredness constraint.</summary>
        Any = 0,

        /// <summary>Only rows whose reference stores an id — someone asked for a target.</summary>
        Assigned = 1,

        /// <summary>Only rows whose reference is unset.</summary>
        Unassigned = 2,
    }

    /// <summary>
    /// The filter applied to a <see cref="ReferenceHubRow"/> table. Pure and mutable: the view edits it and
    /// re-runs <see cref="Apply"/>; the session owns it so filters survive a tab switch.
    /// </summary>
    /// <remarks>
    /// Filtering is a pure function of the row set so it can be tested without a panel, and so the
    /// 10,000-row budget in the plan is a matter of how often <see cref="Apply"/> runs rather than of what
    /// the UI does while it runs.
    /// </remarks>
    public sealed class ReferenceHubFilter
    {
        /// <summary>Show error-severity rows.</summary>
        public bool ShowErrors { get; set; } = true;

        /// <summary>Show warning-severity rows.</summary>
        public bool ShowWarnings { get; set; } = true;

        /// <summary>Show info-severity rows.</summary>
        public bool ShowInfo { get; set; } = true;

        /// <summary>
        /// Free-text query, matched case-insensitively against the row's code, title, asset path, owner,
        /// property path, stored target, expected type and resolution state.
        /// </summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>Restrict to one source category (<c>Scene</c>, <c>PrefabAsset</c>, …). Empty means any.</summary>
        public string SourceKind { get; set; } = string.Empty;

        /// <summary>
        /// Restrict to rows whose asset path starts with this folder. Empty means any. This is also how the
        /// package/asset-folder and per-scene filters are expressed.
        /// </summary>
        public string FolderPrefix { get; set; } = string.Empty;

        /// <summary>Restrict to one stored Ref Type. Empty means any.</summary>
        public string RefType { get; set; } = string.Empty;

        /// <summary>Requiredness constraint.</summary>
        public ReferenceHubAssignmentFilter Assignment { get; set; } = ReferenceHubAssignmentFilter.Any;

        /// <summary>Restrict to references that store no Ref Type and rely on the ID-only fallback.</summary>
        public bool LegacyOnly { get; set; }

        /// <summary>Restrict to rows in read-only assets.</summary>
        public bool ReadOnlyOnly { get; set; }

        /// <summary>Restrict by repair availability. Null means any.</summary>
        public ReferenceHubRepairAvailability? Repair { get; set; }

        /// <summary>True when nothing is filtered out, so the view can say "showing everything".</summary>
        public bool IsDefault =>
            ShowErrors && ShowWarnings && ShowInfo
            && string.IsNullOrEmpty(Query)
            && string.IsNullOrEmpty(SourceKind)
            && string.IsNullOrEmpty(FolderPrefix)
            && string.IsNullOrEmpty(RefType)
            && Assignment == ReferenceHubAssignmentFilter.Any
            && !LegacyOnly
            && !ReadOnlyOnly
            && Repair == null;

        /// <summary>Resets every constraint.</summary>
        public void Reset()
        {
            ShowErrors = ShowWarnings = ShowInfo = true;
            Query = string.Empty;
            SourceKind = string.Empty;
            FolderPrefix = string.Empty;
            RefType = string.Empty;
            Assignment = ReferenceHubAssignmentFilter.Any;
            LegacyOnly = false;
            ReadOnlyOnly = false;
            Repair = null;
        }

        /// <summary>True when <paramref name="row"/> survives every active constraint.</summary>
        /// <param name="row">The row to test. Null never matches.</param>
        public bool Matches(ReferenceHubRow row)
        {
            if (row == null)
                return false;

            if (!SeverityVisible(row.Severity))
                return false;

            if (!string.IsNullOrEmpty(SourceKind)
                && !string.Equals(row.SourceKind, SourceKind, StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrEmpty(FolderPrefix)
                && !row.AssetPath.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(RefType) && !MatchesRefType(row))
                return false;

            if (Assignment == ReferenceHubAssignmentFilter.Assigned && !row.IsAssigned)
                return false;
            if (Assignment == ReferenceHubAssignmentFilter.Unassigned && row.IsAssigned)
                return false;

            if (LegacyOnly && !row.IsLegacyFallback)
                return false;

            if (ReadOnlyOnly && !row.IsReadOnly)
                return false;

            if (Repair.HasValue && row.Repair != Repair.Value)
                return false;

            if (!string.IsNullOrEmpty(Query)
                && (row.SearchText == null
                    || row.SearchText.IndexOf(Query, StringComparison.OrdinalIgnoreCase) < 0))
                return false;

            return true;
        }

        /// <summary>Applies this filter to a table, preserving row order.</summary>
        /// <param name="rows">The rows to filter. Null yields an empty list.</param>
        public IReadOnlyList<ReferenceHubRow> Apply(IReadOnlyList<ReferenceHubRow> rows)
        {
            if (rows == null || rows.Count == 0)
                return Array.Empty<ReferenceHubRow>();

            var result = new List<ReferenceHubRow>(rows.Count);
            foreach (var row in rows)
                if (Matches(row))
                    result.Add(row);
            return result;
        }

        private bool SeverityVisible(ReferenceFindingSeverity severity) => severity switch
        {
            ReferenceFindingSeverity.Error => ShowErrors,
            ReferenceFindingSeverity.Warning => ShowWarnings,
            _ => ShowInfo,
        };

        // The stored target is "type:id", so the ref-type constraint tests the part before the separator
        // rather than the whole string: filtering on type "Step" must not match a reference whose *id*
        // happens to contain "Step".
        private bool MatchesRefType(ReferenceHubRow row)
        {
            var stored = row.StoredTarget;
            var separator = stored.IndexOf(':');
            if (separator <= 0)
                return false;
            return string.Equals(stored.Substring(0, separator), RefType, StringComparison.Ordinal);
        }

        /// <summary>
        /// The distinct source kinds present in a table, for populating the source-kind dropdown from the
        /// data rather than from an enum the project may not use all of.
        /// </summary>
        /// <param name="rows">The unfiltered table.</param>
        public static IReadOnlyList<string> SourceKindsIn(IReadOnlyList<ReferenceHubRow> rows) =>
            rows == null
                ? Array.Empty<string>()
                : rows.Select(r => r.SourceKind)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();

        /// <summary>The distinct stored Ref Types present in a table, for the ref-type dropdown.</summary>
        /// <param name="rows">The unfiltered table.</param>
        public static IReadOnlyList<string> RefTypesIn(IReadOnlyList<ReferenceHubRow> rows)
        {
            if (rows == null)
                return Array.Empty<string>();

            var types = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var separator = row.StoredTarget.IndexOf(':');
                if (separator > 0)
                    types.Add(row.StoredTarget.Substring(0, separator));
            }
            return types.ToList();
        }

        /// <summary>
        /// Human-readable description of the active constraints, so a filtered table can say what it is
        /// hiding instead of just showing fewer rows.
        /// </summary>
        public string Describe()
        {
            if (IsDefault)
                return "no filter";

            var parts = new List<string>();
            if (!ShowErrors || !ShowWarnings || !ShowInfo)
            {
                var shown = new List<string>();
                if (ShowErrors) shown.Add("errors");
                if (ShowWarnings) shown.Add("warnings");
                if (ShowInfo) shown.Add("info");
                parts.Add(shown.Count == 0 ? "no severities" : string.Join("/", shown) + " only");
            }

            if (!string.IsNullOrEmpty(Query)) parts.Add($"matching \"{Query}\"");
            if (!string.IsNullOrEmpty(SourceKind)) parts.Add($"source {SourceKind}");
            if (!string.IsNullOrEmpty(FolderPrefix)) parts.Add($"under {FolderPrefix}");
            if (!string.IsNullOrEmpty(RefType)) parts.Add($"type {RefType}");
            if (Assignment != ReferenceHubAssignmentFilter.Any) parts.Add(Assignment.ToString().ToLowerInvariant());
            if (LegacyOnly) parts.Add("legacy fallback only");
            if (ReadOnlyOnly) parts.Add("read-only assets only");
            if (Repair.HasValue) parts.Add($"repair {Repair.Value}");

            return string.Join(", ", parts);
        }
    }
}
