using System;
using System.Collections.Generic;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// The semantic groups a <see cref="MolcaHubWorkspaceItem"/> can declare, the order those groups render
    /// in, and their human labels. Grouping replaces guessing a global <see cref="MolcaHubWorkspaceItem.Order"/>
    /// integer against a namespace a provider cannot observe: <c>Order</c> keeps its meaning *within* a group,
    /// which is the only scope a provider can reason about honestly.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. A group is a plain string so a fork can invent
    /// its own; unknown groups sort after every declared one, ordinally by id. Editor-only; main thread.
    /// </remarks>
    public static class MolcaHubWorkspaceGroups
    {
        /// <summary>Default group for a tab that declares none.</summary>
        public const string General = "general";

        /// <summary>Diagnostics and validation surfaces (Core: Doctor).</summary>
        public const string Quality = "quality";

        /// <summary>Content-authoring surfaces (Core: Sequence).</summary>
        public const string Authoring = "authoring";

        /// <summary>Assistive/agentic surfaces (Core: Assistant).</summary>
        public const string Assistance = "assistance";

        /// <summary>Consumer integrations with external services.</summary>
        public const string Integrations = "integrations";

        /// <summary>Read-only reference material (Core: Docs).</summary>
        public const string Reference = "reference";

        // Ordering rationale: Quality/Assistance/Authoring first, in that sequence, so Core's own
        // Doctor → Assistant → Sequence strip renders exactly as it did before groups existed. `General`
        // sits late on purpose — an ungrouped third-party tab used to land after Core's tabs (Core uses low
        // Order values), and it still should. `Reference` is last: it is auxiliary by definition and its
        // members are usually right-anchored anyway.
        private static readonly string[] Declared =
        {
            Quality, Assistance, Authoring, Integrations, General, Reference
        };

        /// <summary>Declared render order; unknown groups sort after these, ordinally by id.</summary>
        public static IReadOnlyList<string> DeclaredOrder => Declared;

        /// <summary>
        /// Maps a raw group value to the group actually used for sorting and labelling: <c>null</c>,
        /// empty, or whitespace becomes <see cref="General"/>; anything else is trimmed and returned.
        /// </summary>
        /// <param name="group">The raw group value from a workspace item.</param>
        /// <returns>The normalized group id.</returns>
        public static string Normalize(string group) =>
            string.IsNullOrWhiteSpace(group) ? General : group.Trim();

        /// <summary>
        /// The render rank of a group. Declared groups return their index in <see cref="DeclaredOrder"/>;
        /// every unknown group returns <see cref="DeclaredOrder"/>'s count, so unknown groups sort last as a
        /// block and are ordered among themselves by group id (see
        /// <see cref="MolcaHubWorkspaceRegistry.CompareItems"/>).
        /// </summary>
        /// <param name="group">The group id (normalized internally).</param>
        /// <returns>The zero-based rank; higher sorts later.</returns>
        public static int Rank(string group)
        {
            var normalized = Normalize(group);
            for (int i = 0; i < Declared.Length; i++)
                if (string.Equals(Declared[i], normalized, StringComparison.Ordinal))
                    return i;
            return Declared.Length;
        }

        /// <summary>
        /// Human label used for overflow-menu submenus and group separators. Declared groups have curated
        /// labels; an unknown group is title-cased from its id so a fork's own group is still readable.
        /// </summary>
        /// <param name="group">The group id (normalized internally).</param>
        /// <returns>A display label, never <c>null</c> or empty.</returns>
        public static string Label(string group)
        {
            var normalized = Normalize(group);
            switch (normalized)
            {
                case General: return "General";
                case Quality: return "Quality";
                case Authoring: return "Authoring";
                case Assistance: return "Assistance";
                case Integrations: return "Integrations";
                case Reference: return "Reference";
                default:
                    return char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);
            }
        }
    }
}
