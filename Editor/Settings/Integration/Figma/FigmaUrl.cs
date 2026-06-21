using System;
using System.Text.RegularExpressions;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Parses Figma file keys and node ids out of full Figma URLs, so tools can accept a pasted link anywhere a
    /// bare key/node id is expected.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Registration: pure utility; not an asset. Network-free and editor-agnostic, so it is unit-testable.
    /// <para>
    /// Supported URL shapes (all of <c>figma.com/&lt;kind&gt;/&lt;key&gt;/&lt;slug&gt;?node-id=&lt;id&gt;</c>):
    /// <c>file</c>, <c>design</c>, <c>board</c>, and <c>proto</c>. Node ids appear in URLs in dash form
    /// (<c>8142-2730</c>); the Figma REST API uses colon form (<c>8142:2730</c>), so
    /// <see cref="ResolveNodeId"/> normalizes dashes to colons. Inputs that are not URLs are returned trimmed and
    /// unchanged, so a bare key or colon-form node id passes through untouched.
    /// </para>
    /// </remarks>
    public static class FigmaUrl
    {
        // figma.com/<kind>/<KEY>/... — key is the alphanumeric segment after the kind.
        private static readonly Regex FileKeyPattern = new Regex(
            @"figma\.com/(?:file|design|board|proto)/([A-Za-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ?node-id=<ID> or &node-id=<ID> — value runs to the next query delimiter.
        private static readonly Regex NodeIdPattern = new Regex(
            @"[?&]node-id=([^&#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Whether the input looks like a Figma URL (rather than a bare key/id).</summary>
        /// <param name="value">The candidate string.</param>
        public static bool LooksLikeUrl(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf("figma.com", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Returns the Figma file key for <paramref name="fileKeyOrUrl"/>: parsed from the URL when it is one,
        /// otherwise the input trimmed and unchanged.
        /// </summary>
        /// <param name="fileKeyOrUrl">A bare file key or a full Figma URL.</param>
        /// <returns>The file key, or the trimmed input if no key could be parsed.</returns>
        public static string ResolveFileKey(string fileKeyOrUrl)
        {
            if (string.IsNullOrWhiteSpace(fileKeyOrUrl)) return fileKeyOrUrl;

            var match = FileKeyPattern.Match(fileKeyOrUrl);
            return match.Success ? match.Groups[1].Value : fileKeyOrUrl.Trim();
        }

        /// <summary>
        /// Returns the API-form node id for <paramref name="nodeIdOrUrl"/>: parsed from a URL's
        /// <c>node-id</c> query and normalized to colon form, otherwise the input trimmed and unchanged.
        /// </summary>
        /// <param name="nodeIdOrUrl">A bare node id (either form) or a full Figma URL with a <c>node-id</c>.</param>
        /// <returns>The colon-form node id, or the trimmed input if none could be parsed.</returns>
        public static string ResolveNodeId(string nodeIdOrUrl)
        {
            if (string.IsNullOrWhiteSpace(nodeIdOrUrl)) return nodeIdOrUrl;

            string raw;
            var match = NodeIdPattern.Match(nodeIdOrUrl);
            if (match.Success)
                raw = Uri.UnescapeDataString(match.Groups[1].Value);
            else if (LooksLikeUrl(nodeIdOrUrl))
                return null; // A URL with no node-id carries no node to resolve.
            else
                raw = nodeIdOrUrl.Trim();

            // URLs encode the API's colon separator as a dash; normalize so the /nodes endpoint accepts it.
            return raw.Replace('-', ':');
        }
    }
}
