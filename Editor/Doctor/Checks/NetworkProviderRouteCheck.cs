using System.Collections.Generic;
using System.Threading;
using Molca.Editor.Networking.Authoring;
using Molca.Networking.Data;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags streaming provider assets that still author a URL directly while the project has a catalog
    /// they could route through.
    /// </summary>
    /// <remarks>
    /// A direct URL on a provider is not broken — it is supported and unchanged — but it sits outside
    /// every rule the catalog enforces: allowed hosts, the production encrypted-scheme requirement, and
    /// credential scope. A project that has adopted the catalog for HTTP and left its streams on raw URLs
    /// has a gap it probably does not know about, which is exactly what Doctor is for.
    /// <para>
    /// Silent when the project has no catalog: there would be nothing to migrate to, and the advice would
    /// be noise. Read by <em>serialized property name</em>, not by provider type — the WebSocket and
    /// Socket.IO providers compile only under their own define symbols, and a check that named those types
    /// would not compile in a project that has neither.
    /// </para>
    /// </remarks>
    public class NetworkProviderRouteCheck : IDoctorCheck
    {
        /// <summary>The serialized field a routed provider stores its route in.</summary>
        private const string RouteProperty = "_route";

        /// <inheritdoc/>
        public string Id => "network-provider-route";

        /// <inheritdoc/>
        public string Description => "Streaming providers authoring a raw URL while a NetworkCatalog exists";

        /// <inheritdoc/>
        public string Category => "Networking";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(
            DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.NextFrameAsync(cancellationToken);

            var issues = new List<DoctorIssue>();

            if (NetworkCatalogLocator.FindCatalog() == null)
                return issues;

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(DataProvider)}"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = AssetDatabase.GUIDToAssetPath(guid);

                // Package-owned assets are not the project's to change.
                if (path.StartsWith("Packages/", System.StringComparison.Ordinal))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<DataProvider>(path);
                if (asset == null) continue;

                var serialized = new SerializedObject(asset);
                var route = serialized.FindProperty(RouteProperty);

                // No route field means this provider type does not support routing — HTTP data providers,
                // and anything a project subclassed itself.
                if (route == null) continue;

                string serviceId = route.FindPropertyRelative("_serviceId")?.stringValue;
                if (!string.IsNullOrEmpty(serviceId)) continue;

                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    $"'{asset.name}' connects to a URL authored on the asset while this project has a " +
                    "NetworkCatalog. A direct URL is outside the catalog's allowed-host, production-scheme, " +
                    "and credential-scope rules. Set a service on its Route in Hub ▸ Network ▸ Providers.",
                    path));
            }

            return issues;
        }
    }
}
