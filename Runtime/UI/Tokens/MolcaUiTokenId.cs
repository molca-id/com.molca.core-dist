using System;

namespace Molca.UI.Tokens
{
    /// <summary>
    /// Validation + parsing for token ids in <c>category/name</c> form (e.g. <c>color/primary</c>,
    /// <c>surface/panel-bg</c>, <c>control/button</c>). Tokens are stored as plain strings everywhere
    /// (catalog assets, the <see cref="MolcaStyleApplier"/>, the Figma intent spec) so this is the one
    /// place that knows the grammar.
    /// </summary>
    public static class MolcaUiTokenId
    {
        /// <summary>The lower-case category prefix for <paramref name="category"/> (e.g. <c>color</c>).</summary>
        public static string Prefix(MolcaUiTokenCategory category) =>
            category.ToString().ToLowerInvariant();

        /// <summary>
        /// Parses <paramref name="id"/> into its <paramref name="category"/> and <paramref name="name"/>.
        /// Returns false (with defaults) for null/empty, a missing or unknown category prefix, an empty
        /// name, or more than one separator.
        /// </summary>
        public static bool TryParse(string id, out MolcaUiTokenCategory category, out string name)
        {
            category = default;
            name = null;
            if (string.IsNullOrWhiteSpace(id)) return false;

            var slash = id.IndexOf('/');
            if (slash <= 0 || slash != id.LastIndexOf('/')) return false;

            var prefix = id.Substring(0, slash);
            var rest = id.Substring(slash + 1);
            if (string.IsNullOrWhiteSpace(rest)) return false;

            foreach (MolcaUiTokenCategory c in Enum.GetValues(typeof(MolcaUiTokenCategory)))
            {
                if (string.Equals(prefix, Prefix(c), StringComparison.Ordinal))
                {
                    category = c;
                    name = rest;
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if <paramref name="id"/> is a well-formed <c>category/name</c> token id.</summary>
        public static bool IsValid(string id) => TryParse(id, out _, out _);

        /// <summary>The category of <paramref name="id"/>, or null if it is not a valid token id.</summary>
        public static MolcaUiTokenCategory? CategoryOf(string id) =>
            TryParse(id, out var c, out _) ? c : (MolcaUiTokenCategory?)null;
    }
}
