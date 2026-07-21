using System;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Resolves a check's <b>category</b> — the group it appears under in the Doctor window and the
    /// unit by which a run can be scoped to a related subset of checks. Categories are derived from a
    /// check's kebab-case <see cref="IDoctorCheck.Id"/> via an ordered prefix table, so the built-in
    /// checks group correctly with zero per-check wiring; a check that disagrees overrides
    /// <see cref="IDoctorCheck.Category"/> directly.
    /// </summary>
    /// <remarks>
    /// This is intentionally id-driven rather than a per-check field: ids are the stable key throughout
    /// Doctor (see <see cref="DoctorCheckRegistry.BuiltInOrder"/>), so grouping stays derivable without
    /// touching the 30-plus existing check types. Adding a new built-in check whose id does not match a
    /// prefix here lands it in <see cref="General"/> — <c>DoctorCategoriesTests</c> guards against that so
    /// a new group is a deliberate one-line addition, not an accident.
    /// </remarks>
    public static class DoctorCategories
    {
        /// <summary>Fallback category for a check whose id matches no known prefix.</summary>
        public const string General = "General";

        /// <summary>
        /// Ordered id-prefix → category rules. The first rule whose prefix matches the id (ordinal
        /// <see cref="string.StartsWith(string, StringComparison)"/>) wins, so more specific prefixes
        /// must precede any shorter prefix they extend.
        /// </summary>
        private static readonly (string Prefix, string Category)[] Rules =
        {
            ("scene-", "Scene"),
            ("http-", "Networking"),
            ("dataprovider-", "Networking"),
            ("color-id-", "Theming"),
            ("design-language", "Theming"),
            ("dynamic-localization-", "Localization"),
            ("build-", "Build & Config"),
            ("version-settings", "Build & Config"),
            ("content-package", "Build & Config"),
            ("sequence-", "Sequence"),
            ("docs-coverage", "Docs"),
            ("doc-links", "Docs"),
            ("unity-lifecycle-", "Async & Lifecycle"),
            ("async-void-", "Async & Lifecycle"),
            ("awaitable-", "Async & Lifecycle"),
            ("task-returning-", "Async & Lifecycle"),
            ("static-singleton", "Architecture"),
            ("runtime-so", "Architecture"),
            ("missing-finish-callback", "Architecture"),
            ("inject-", "Architecture"),
            ("unresolvable-scene-reference", "Architecture"),
        };

        /// <summary>
        /// Maps a check id to its category using the prefix rules, or <see cref="General"/> when no rule
        /// matches (including for a null/empty id).
        /// </summary>
        /// <param name="id">A check's kebab-case <see cref="IDoctorCheck.Id"/>.</param>
        /// <returns>The resolved category label; never null or empty.</returns>
        public static string Derive(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return General;

            foreach (var (prefix, category) in Rules)
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    return category;

            return General;
        }
    }
}
