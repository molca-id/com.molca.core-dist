using System.Collections.Generic;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// An immutable view of one <see cref="NetworkCatalog"/>, captured once and then never re-read.
    /// </summary>
    /// <remarks>
    /// This is the boundary that makes an in-flight request immune to configuration changing
    /// underneath it (plan §6.2). The routed pipeline reads a snapshot; it never touches
    /// <see cref="GlobalSettings"/>, the catalog asset, or the Hub's current selection while a request
    /// is queued or executing.
    /// <para>
    /// Holds references rather than deep copies. That is sound because every catalog entity is
    /// read-only configuration — nothing mutates them at runtime, and the editor's authoring services
    /// only write in edit mode. What the snapshot buys is a stable <em>set</em> of entities and a
    /// pre-built index, so resolution costs a dictionary lookup instead of a linear scan.
    /// </para>
    /// </remarks>
    public sealed class NetworkCatalogSnapshot
    {
        /// <summary>A snapshot with no catalog. Every route resolution against it fails as unconfigured.</summary>
        public static readonly NetworkCatalogSnapshot Empty = new NetworkCatalogSnapshot(null);

        /// <summary>The catalog this snapshot was taken from, or <c>null</c>.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>The index built over <see cref="Catalog"/>. Never <c>null</c>.</summary>
        public NetworkCatalogIndex Index { get; }

        /// <summary>The default environment ID at capture time, or empty.</summary>
        public string DefaultEnvironmentId { get; }

        /// <summary>Whether the legacy global-auth transition flag was on at capture time.</summary>
        public bool AllowLegacyGlobalAuthOnExternalUrls { get; }

        /// <summary>Whether legacy sends were configured to execute on the routed pipeline at capture time.</summary>
        public bool RouteLegacyHttpThroughPipeline { get; }

        /// <summary>Whether this snapshot has a usable catalog.</summary>
        public bool HasCatalog => Catalog != null;

        private NetworkCatalogSnapshot(NetworkCatalog catalog)
        {
            Catalog = catalog;
            Index = new NetworkCatalogIndex(catalog);
            DefaultEnvironmentId = catalog?.DefaultEnvironmentId ?? string.Empty;
            AllowLegacyGlobalAuthOnExternalUrls = catalog != null && catalog.AllowLegacyGlobalAuthOnExternalUrls;
            RouteLegacyHttpThroughPipeline = catalog != null && catalog.RouteLegacyHttpThroughPipeline;
        }

        /// <summary>
        /// Captures a snapshot of <paramref name="catalog"/>.
        /// </summary>
        /// <param name="catalog">The catalog to capture, or <c>null</c> for <see cref="Empty"/>.</param>
        /// <returns>An immutable snapshot.</returns>
        public static NetworkCatalogSnapshot Capture(NetworkCatalog catalog) =>
            catalog == null ? Empty : new NetworkCatalogSnapshot(catalog);

        /// <summary>Environments in this snapshot, keyed by ID.</summary>
        public IReadOnlyDictionary<string, NetworkEnvironmentProfile> Environments => Index.Environments;

        /// <summary>Services in this snapshot, keyed by ID.</summary>
        public IReadOnlyDictionary<string, NetworkServiceDefinition> Services => Index.Services;
    }
}
