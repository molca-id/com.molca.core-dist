using System;
using System.Collections.Generic;
using Molca.ContentPackage.Core;

namespace Molca.App.UI.ContentPackage
{
    /// <summary>Which packages the list shows.</summary>
    public enum PackageListFilter
    {
        /// <summary>Everything visible.</summary>
        All = 0,

        /// <summary>Only packages whose content is on this device.</summary>
        Installed = 1,

        /// <summary>Only packages with a newer version available.</summary>
        Updates = 2,

        /// <summary>Only packages that have never been downloaded.</summary>
        NotInstalled = 3,
    }

    /// <summary>How the list is ordered.</summary>
    public enum PackageListSort
    {
        /// <summary>Alphabetical by display name.</summary>
        Name = 0,

        /// <summary>Largest first — the order that matters when reclaiming space.</summary>
        Size = 1,

        /// <summary>By what needs attention first.</summary>
        Status = 2,
    }

    /// <summary>
    /// One row's worth of facts, enough to search, filter, and sort without touching the service.
    /// </summary>
    /// <remarks>
    /// Carries <see cref="IsInstalled"/> and <see cref="HasUpdate"/> alongside <see cref="Status"/>
    /// rather than deriving them from it. <c>Status</c> is a projection over the install, operation,
    /// and update records, and the projection is lossy in exactly the places that matter here: a
    /// package that is installed but whose last update failed projects one way while being two
    /// different things to a filter. Re-deriving from the projection is how "Installed" and
    /// "Updates" end up disagreeing with the buttons on the detail panel.
    /// </remarks>
    public readonly struct PackageListEntry
    {
        /// <summary>Stable package identifier.</summary>
        public string PackageId { get; }

        /// <summary>Name shown to the user; falls back to the id.</summary>
        public string DisplayName { get; }

        /// <summary>Searchable tags, possibly null.</summary>
        public string[] Tags { get; }

        /// <summary>The projected status, used for ordering and colour.</summary>
        public PackageStatus Status { get; }

        /// <summary>True when content is on this device.</summary>
        public bool IsInstalled { get; }

        /// <summary>True when a newer version is available.</summary>
        public bool HasUpdate { get; }

        /// <summary>Size in bytes, or 0 when unknown.</summary>
        public long SizeBytes { get; }

        /// <summary>Builds an entry.</summary>
        /// <param name="packageId">Stable identifier.</param>
        /// <param name="displayName">Name shown to the user.</param>
        /// <param name="tags">Searchable tags, may be null.</param>
        /// <param name="status">Projected status.</param>
        /// <param name="isInstalled">Whether content is present.</param>
        /// <param name="hasUpdate">Whether a newer version exists.</param>
        /// <param name="sizeBytes">Size in bytes.</param>
        public PackageListEntry(
            string packageId, string displayName, string[] tags,
            PackageStatus status, bool isInstalled, bool hasUpdate, long sizeBytes)
        {
            PackageId = packageId ?? "";
            DisplayName = string.IsNullOrEmpty(displayName) ? PackageId : displayName;
            Tags = tags;
            Status = status;
            IsInstalled = isInstalled;
            HasUpdate = hasUpdate;
            SizeBytes = sizeBytes;
        }
    }

    /// <summary>
    /// Searches, filters, and orders the package list.
    /// </summary>
    /// <remarks>
    /// Deliberately a pure static over a value type rather than methods on the UI component. A
    /// MonoBehaviour cannot be exercised without a scene, and the rules worth testing here — that a
    /// search never hides an installed package the user is looking at by name, that the order is the
    /// same every time — are ordinary data rules that should not need a play mode run to check.
    /// </remarks>
    public static class PackageListQuery
    {
        /// <summary>
        /// The package count above which the search/filter/sort toolbar earns its space.
        /// </summary>
        /// <remarks>
        /// Twelve, chosen against the observed shape of these projects rather than a round number:
        /// list rows here are tall enough that roughly eight to ten fit a panel, so twelve is the
        /// first count at which scrolling is guaranteed and a user can no longer see everything at
        /// once. Below that the toolbar costs a row of vertical space to solve a problem the user
        /// does not have; the plan (§12) asks for the control "if the package count exceeds the
        /// agreed threshold", and this is that threshold.
        /// </remarks>
        public const int ToolbarThreshold = 12;

        /// <summary>
        /// Applies the search text, filter, and sort, in that order.
        /// </summary>
        /// <param name="entries">Candidate rows. Null is treated as empty.</param>
        /// <param name="search">Free text; null or blank matches everything.</param>
        /// <param name="filter">Which subset to keep.</param>
        /// <param name="sort">Requested order.</param>
        /// <returns>A new list; never null.</returns>
        public static List<PackageListEntry> Apply(
            IEnumerable<PackageListEntry> entries,
            string search,
            PackageListFilter filter,
            PackageListSort sort)
        {
            var kept = new List<PackageListEntry>();
            if (entries == null) return kept;

            string needle = search?.Trim();
            bool searching = !string.IsNullOrEmpty(needle);

            foreach (var entry in entries)
            {
                if (!Matches(entry, filter)) continue;
                if (searching && !Matches(entry, needle)) continue;
                kept.Add(entry);
            }

            kept.Sort(Comparer(sort));
            return kept;
        }

        /// <summary>True when the entry belongs in the filtered subset.</summary>
        private static bool Matches(PackageListEntry entry, PackageListFilter filter) => filter switch
        {
            PackageListFilter.Installed => entry.IsInstalled,
            PackageListFilter.Updates => entry.HasUpdate,
            PackageListFilter.NotInstalled => !entry.IsInstalled,
            _ => true,
        };

        /// <summary>
        /// True when the entry matches the search text.
        /// </summary>
        /// <remarks>
        /// Substring, case-insensitive, across name, id, and tags. Case-insensitive comparison is
        /// done with <see cref="StringComparison.OrdinalIgnoreCase"/> rather than by lowercasing both
        /// sides: lowercasing allocates a string per row per keystroke, and a culture-aware
        /// <c>ToLower</c> maps a dotted capital I to a dotless i on a Turkish device, so a user there
        /// would find that typing the name of their own content matched nothing.
        /// </remarks>
        private static bool Matches(PackageListEntry entry, string needle)
        {
            if (Contains(entry.DisplayName, needle)) return true;
            if (Contains(entry.PackageId, needle)) return true;

            if (entry.Tags != null)
                foreach (string tag in entry.Tags)
                    if (Contains(tag, needle)) return true;

            return false;
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The comparison for a sort mode.
        /// </summary>
        /// <remarks>
        /// Every mode falls back to the display name, so the order is total and the same list always
        /// renders the same way. Without the tiebreak, two packages of equal size would swap places
        /// between refreshes for no reason the user can see — and a list that reorders itself under
        /// the finger is worse than one sorted the wrong way.
        /// </remarks>
        private static Comparison<PackageListEntry> Comparer(PackageListSort sort) => sort switch
        {
            // Largest first: sorting by size is what a user does when deciding what to delete.
            PackageListSort.Size => (left, right) =>
            {
                int bySize = right.SizeBytes.CompareTo(left.SizeBytes);
                return bySize != 0 ? bySize : ByName(left, right);
            },

            PackageListSort.Status => (left, right) =>
            {
                int byRank = Rank(left).CompareTo(Rank(right));
                return byRank != 0 ? byRank : ByName(left, right);
            },

            _ => ByName,
        };

        private static int ByName(PackageListEntry left, PackageListEntry right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Sort weight for <see cref="PackageListSort.Status"/> — what needs attention comes first.
        /// </summary>
        /// <remarks>
        /// Failed outranks Downloading: a transfer in progress needs nothing from the user, and a
        /// failure does. Installed sits below both and above Available, because content that is
        /// present and working is the state a user is least often looking for in a list.
        /// </remarks>
        private static int Rank(PackageListEntry entry) => entry.Status switch
        {
            PackageStatus.Failed => 0,
            PackageStatus.UpdateAvailable => 1,
            PackageStatus.Downloading => 2,
            PackageStatus.Installed => 3,
            PackageStatus.Available => 4,
            _ => 5,
        };

        /// <summary>A short caption for a filter, for a cycling button.</summary>
        /// <param name="filter">The filter.</param>
        /// <returns>The caption.</returns>
        public static string Caption(PackageListFilter filter) => filter switch
        {
            PackageListFilter.Installed => "Installed",
            PackageListFilter.Updates => "Updates",
            PackageListFilter.NotInstalled => "Not installed",
            _ => "All",
        };

        /// <summary>A short caption for a sort mode, for a cycling button.</summary>
        /// <param name="sort">The sort mode.</param>
        /// <returns>The caption.</returns>
        public static string Caption(PackageListSort sort) => sort switch
        {
            PackageListSort.Size => "Size",
            PackageListSort.Status => "Status",
            _ => "Name",
        };

        /// <summary>Advances to the next filter, wrapping.</summary>
        /// <param name="filter">Current filter.</param>
        /// <returns>The next filter.</returns>
        public static PackageListFilter Next(PackageListFilter filter) =>
            (PackageListFilter)(((int)filter + 1) % 4);

        /// <summary>Advances to the next sort mode, wrapping.</summary>
        /// <param name="sort">Current sort mode.</param>
        /// <returns>The next sort mode.</returns>
        public static PackageListSort Next(PackageListSort sort) =>
            (PackageListSort)(((int)sort + 1) % 3);
    }
}
