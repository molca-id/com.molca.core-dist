using System.Collections.Generic;
using Molca.Editor.Telemetry;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Records add-on lifecycle events into the framework-wide usage reporter.
    /// </summary>
    /// <remarks>
    /// Add-on signals used to have their own queue, endpoint, and table. They are now ordinary events in
    /// the <c>addon.*</c> namespace handled by <see cref="MolcaEditorTelemetry"/>; this type exists only
    /// to keep the pack id and version in a consistent property shape across the four call sites.
    /// </remarks>
    internal static class AddonTelemetry
    {
        /// <param name="action">One of <c>install</c>, <c>update</c>, <c>remove</c>, <c>verify_fail</c>.</param>
        /// <param name="packId">The add-on package id.</param>
        /// <param name="version">The version the action applied to.</param>
        /// <param name="detail">Optional short failure reason; never a URL, token, or path.</param>
        internal static void Record(string action, string packId, string version, string detail = null)
        {
            var properties = new Dictionary<string, object>
            {
                { "packId", packId ?? string.Empty },
                { "version", version ?? string.Empty },
            };
            if (!string.IsNullOrEmpty(detail))
                properties["detail"] = detail.Length > 300 ? detail.Substring(0, 300) : detail;
            MolcaEditorTelemetry.Track("addon." + action, properties);
        }
    }
}
