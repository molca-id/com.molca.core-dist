using System;
using System.Collections.Generic;
using System.Text;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>What one planned migration step will create.</summary>
    public enum LegacyMigrationStepKind
    {
        /// <summary>Create the environment legacy traffic is attributed to.</summary>
        CreateEnvironment = 0,

        /// <summary>Create the policy profile carrying the legacy module's timeouts, retry, and concurrency.</summary>
        CreatePolicyProfile,

        /// <summary>Create a service.</summary>
        CreateService,

        /// <summary>Bind a service's HTTP origin for the environment.</summary>
        BindService,

        /// <summary>Create the credential profile the author then scopes by hand.</summary>
        CreateCredentialProfile,

        /// <summary>Create the endpoint collection migrated endpoints live in.</summary>
        CreateEndpointCollection,

        /// <summary>Create one endpoint from a legacy request asset.</summary>
        CreateEndpoint
    }

    /// <summary>
    /// One step in a migration plan: everything the executor needs, and a sentence for the preview.
    /// </summary>
    /// <remarks>
    /// Immutable and self-describing. The preview text and the applied change come from the same object,
    /// so a dry run cannot describe something other than what apply does.
    /// </remarks>
    public sealed class LegacyMigrationStep
    {
        /// <summary>What this step creates.</summary>
        public LegacyMigrationStepKind Kind { get; }

        /// <summary>ID of the entity being created, or the service being bound.</summary>
        public string TargetId { get; }

        /// <summary>Service the step attaches to, for bindings and endpoints. Otherwise empty.</summary>
        public string ServiceId { get; }

        /// <summary>Environment the step applies to, for bindings. Otherwise empty.</summary>
        public string EnvironmentId { get; }

        /// <summary>Absolute origin to bind. Empty unless <see cref="Kind"/> is a binding.</summary>
        public string Origin { get; }

        /// <summary>HTTP method, for an endpoint step.</summary>
        public HttpMethod Method { get; }

        /// <summary>Path relative to the service origin, for an endpoint step.</summary>
        public string RelativePath { get; }

        /// <summary>Protocols to declare, for a service step.</summary>
        public NetworkProtocols Protocols { get; }

        /// <summary>
        /// GUID of the legacy asset this step derives from, recorded as the created entity's source so a
        /// re-run recognizes it as already migrated. Empty for scaffolding steps.
        /// </summary>
        public string SourceGuid { get; }

        /// <summary>One sentence describing the step, for the dry-run preview.</summary>
        public string Description { get; }

        private LegacyMigrationStep(
            LegacyMigrationStepKind kind,
            string targetId,
            string description,
            string serviceId = null,
            string environmentId = null,
            string origin = null,
            HttpMethod method = HttpMethod.GET,
            string relativePath = null,
            NetworkProtocols protocols = NetworkProtocols.None,
            string sourceGuid = null)
        {
            Kind = kind;
            TargetId = targetId ?? string.Empty;
            Description = description ?? string.Empty;
            ServiceId = serviceId ?? string.Empty;
            EnvironmentId = environmentId ?? string.Empty;
            Origin = origin ?? string.Empty;
            Method = method;
            RelativePath = relativePath ?? string.Empty;
            Protocols = protocols;
            SourceGuid = sourceGuid ?? string.Empty;
        }

        /// <summary>Plans an environment.</summary>
        /// <param name="id">Environment ID to create.</param>
        public static LegacyMigrationStep Environment(string id) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreateEnvironment, id,
                $"Create environment '{id}' and make it the catalog default.");

        /// <summary>Plans the legacy policy profile.</summary>
        /// <param name="id">Profile ID to create.</param>
        /// <param name="summary">What legacy values it carries.</param>
        public static LegacyMigrationStep PolicyProfile(string id, string summary) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreatePolicyProfile, id,
                $"Create policy profile '{id}' carrying {summary}.");

        /// <summary>Plans a service.</summary>
        /// <param name="id">Service ID to create.</param>
        /// <param name="protocols">Protocols it declares.</param>
        /// <param name="derivedFrom">What the service was derived from, for the preview.</param>
        public static LegacyMigrationStep Service(string id, NetworkProtocols protocols, string derivedFrom) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreateService, id,
                $"Create service '{id}' ({protocols}) from {derivedFrom}.",
                protocols: protocols);

        /// <summary>Plans a service binding.</summary>
        /// <param name="serviceId">Service to bind.</param>
        /// <param name="environmentId">Environment to bind it in.</param>
        /// <param name="origin">Absolute HTTP origin.</param>
        public static LegacyMigrationStep Binding(string serviceId, string environmentId, string origin) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.BindService, serviceId,
                $"Bind '{serviceId}' in '{environmentId}' to {origin}.",
                serviceId: serviceId, environmentId: environmentId, origin: origin);

        /// <summary>Plans the credential profile.</summary>
        /// <param name="id">Profile ID to create.</param>
        public static LegacyMigrationStep CredentialProfile(string id) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreateCredentialProfile, id,
                $"Create credential profile '{id}', unscoped. You then choose which services and hosts " +
                "may use it — migration deliberately does not decide that.");

        /// <summary>Plans the endpoint collection.</summary>
        /// <param name="id">Collection ID to create.</param>
        /// <param name="serviceId">Default service for its endpoints.</param>
        public static LegacyMigrationStep EndpointCollection(string id, string serviceId) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreateEndpointCollection, id,
                $"Create endpoint collection '{id}'.",
                serviceId: serviceId);

        /// <summary>Plans an endpoint migrated from a request asset.</summary>
        /// <param name="id">Endpoint ID to create.</param>
        /// <param name="serviceId">Owning service.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">Path relative to the service origin.</param>
        /// <param name="sourceGuid">GUID of the request asset it came from.</param>
        /// <param name="sourceName">Name of the request asset, for the preview.</param>
        public static LegacyMigrationStep Endpoint(
            string id,
            string serviceId,
            HttpMethod method,
            string relativePath,
            string sourceGuid,
            string sourceName) =>
            new LegacyMigrationStep(
                LegacyMigrationStepKind.CreateEndpoint, id,
                $"Create endpoint '{id}' ({method} {(string.IsNullOrEmpty(relativePath) ? "/" : relativePath)}) " +
                $"on '{serviceId}' from request asset '{sourceName}'.",
                serviceId: serviceId, method: method, relativePath: relativePath, sourceGuid: sourceGuid);

        /// <inheritdoc />
        public override string ToString() => Description;
    }

    /// <summary>A legacy artifact the plan will not touch, and why.</summary>
    public sealed class LegacyMigrationSkip
    {
        /// <summary>The artifact being skipped.</summary>
        public LegacyNetworkItem Item { get; }

        /// <summary>Why it is skipped.</summary>
        public string Reason { get; }

        /// <summary>Whether it is skipped because it is already migrated, rather than because it cannot be.</summary>
        public bool AlreadyMigrated { get; }

        /// <summary>Creates a skip record.</summary>
        /// <param name="item">The artifact.</param>
        /// <param name="reason">Why it is skipped.</param>
        /// <param name="alreadyMigrated">Whether the reason is prior migration.</param>
        public LegacyMigrationSkip(LegacyNetworkItem item, string reason, bool alreadyMigrated = false)
        {
            Item = item;
            Reason = reason ?? string.Empty;
            AlreadyMigrated = alreadyMigrated;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Item?.DisplayName}: {Reason}";
    }

    /// <summary>
    /// The deterministic set of catalog changes that would bring a legacy project onto the routed model.
    /// </summary>
    /// <remarks>
    /// A pure function of a <see cref="LegacyNetworkScanReport"/> — computing it changes nothing, so the
    /// dry run and the apply step share one code path. Recomputing after a partial apply yields only the
    /// steps that remain, which is what makes migration safe to cancel and re-run (plan §10.2).
    /// <para>
    /// The plan never deletes or edits a legacy asset. Request assets, providers, and the
    /// <c>HttpModule</c> stay exactly as they are and keep working; the catalog is authored alongside
    /// them.
    /// </para>
    /// <para>
    /// It also never assigns a credential profile to a service. Which hosts may see a token is a security
    /// decision, and auto-scoping a credential to every host that happened to declare an
    /// <c>Authorization</c> header would rebuild the very leak the routed model exists to close.
    /// </para>
    /// </remarks>
    public sealed class LegacyMigrationPlan
    {
        /// <summary>ID reserved for the environment migration creates.</summary>
        public const string DefaultEnvironmentId = "development";

        /// <summary>ID of the policy profile carrying the legacy module's settings.</summary>
        public const string LegacyPolicyProfileId = "molca-legacy-policy";

        /// <summary>ID of the credential profile migration creates when anything opts into auth.</summary>
        public const string LegacyCredentialProfileId = "molca-legacy-credential";

        /// <summary>ID of the collection migrated endpoints are placed in.</summary>
        public const string LegacyCollectionId = "molca-legacy-endpoints";

        private readonly List<LegacyMigrationStep> _steps;
        private readonly List<LegacyMigrationSkip> _skips;

        /// <summary>The report this plan was computed from.</summary>
        public LegacyNetworkScanReport Report { get; }

        /// <summary>The environment migrated traffic is attributed to.</summary>
        public string EnvironmentId { get; }

        /// <summary>The steps, in the order they must be applied.</summary>
        public IReadOnlyList<LegacyMigrationStep> Steps => _steps;

        /// <summary>Artifacts the plan will not touch, with reasons.</summary>
        public IReadOnlyList<LegacyMigrationSkip> Skipped => _skips;

        /// <summary>Whether applying this plan would change anything.</summary>
        public bool HasWork => _steps.Count > 0;

        private LegacyMigrationPlan(
            LegacyNetworkScanReport report,
            string environmentId,
            List<LegacyMigrationStep> steps,
            List<LegacyMigrationSkip> skips)
        {
            Report = report;
            EnvironmentId = environmentId;
            _steps = steps;
            _skips = skips;
        }

        /// <summary>
        /// Computes the plan for a scan report.
        /// </summary>
        /// <param name="report">The scan to plan from.</param>
        /// <returns>The plan; may have no steps when the project is already migrated.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="report"/> is <c>null</c>.</exception>
        public static LegacyMigrationPlan Compute(LegacyNetworkScanReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            var steps = new List<LegacyMigrationStep>();
            var skips = new List<LegacyMigrationSkip>();
            var catalog = report.ExistingCatalog;

            // A fresh project with no legacy module values or artifacts has nothing to migrate. Do not
            // manufacture an environment/catalog merely because the planner normally needs an environment
            // for later legacy steps.
            if (!report.HasWork)
            {
                return new LegacyMigrationPlan(
                    report,
                    catalog?.DefaultEnvironmentId ?? string.Empty,
                    steps,
                    skips);
            }

            // Entities this plan will have created by the time later steps run, so a step never plans
            // something an earlier step in the same plan already covers.
            var plannedServices = new HashSet<string>(StringComparer.Ordinal);
            var plannedEndpointIds = new HashSet<string>(StringComparer.Ordinal);

            string environmentId = ResolveEnvironment(catalog, steps);
            PlanPolicyProfile(report, catalog, steps);

            // Which service each host's traffic lands on, so endpoints can be attributed without
            // recomputing the ID derivation.
            var serviceByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            PlanLegacyDefaultService(report, catalog, environmentId, steps, plannedServices, serviceByHost);
            PlanForeignHostServices(report, catalog, environmentId, steps, plannedServices, serviceByHost);
            PlanCredentialProfile(report, catalog, steps);
            PlanEndpoints(report, catalog, steps, skips, plannedServices, plannedEndpointIds, serviceByHost);
            NoteUnroutableProviders(report, skips);

            return new LegacyMigrationPlan(report, environmentId, steps, skips);
        }

        /// <summary>
        /// Reuses the catalog's default environment, or plans one.
        /// </summary>
        /// <remarks>
        /// Reuse matters: a project that already authored <c>staging</c> as its default must not have
        /// migrated traffic attributed to a second, freshly invented environment.
        /// </remarks>
        private static string ResolveEnvironment(NetworkCatalog catalog, List<LegacyMigrationStep> steps)
        {
            if (catalog != null && !string.IsNullOrEmpty(catalog.DefaultEnvironmentId) &&
                catalog.FindEnvironment(catalog.DefaultEnvironmentId) != null)
            {
                return catalog.DefaultEnvironmentId;
            }

            steps.Add(LegacyMigrationStep.Environment(DefaultEnvironmentId));
            return DefaultEnvironmentId;
        }

        private static void PlanPolicyProfile(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<LegacyMigrationStep> steps)
        {
            if (!report.HasHttpModule)
                return;

            if (catalog != null && catalog.FindPolicyProfile(LegacyPolicyProfileId) != null)
                return;

            steps.Add(LegacyMigrationStep.PolicyProfile(
                LegacyPolicyProfileId, "the HttpModule's timeout, retry, and concurrency settings"));
        }

        private static void PlanLegacyDefaultService(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            string environmentId,
            List<LegacyMigrationStep> steps,
            HashSet<string> plannedServices,
            Dictionary<string, string> serviceByHost)
        {
            if (string.IsNullOrWhiteSpace(report.BaseUrl))
                return;

            if (!NetworkOrigin.TryNormalize(report.BaseUrl, false, out string origin, out _))
                return;

            string host = NetworkHostRule.HostOf(origin);
            if (host != null)
                serviceByHost[host] = NetworkIds.LegacyDefaultServiceId;

            var existing = catalog?.FindService(NetworkIds.LegacyDefaultServiceId);
            if (existing == null)
            {
                steps.Add(LegacyMigrationStep.Service(
                    NetworkIds.LegacyDefaultServiceId, NetworkProtocols.Http, "HttpModule.BaseUrl"));
                plannedServices.Add(NetworkIds.LegacyDefaultServiceId);
            }

            // Bind unconditionally when the binding is absent — a service that exists without a binding
            // for this environment is exactly the "half-migrated" state a re-run must finish.
            if (existing?.FindBinding(environmentId) == null)
            {
                steps.Add(LegacyMigrationStep.Binding(
                    NetworkIds.LegacyDefaultServiceId, environmentId, origin));
            }
        }

        private static void PlanForeignHostServices(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            string environmentId,
            List<LegacyMigrationStep> steps,
            HashSet<string> plannedServices,
            Dictionary<string, string> serviceByHost)
        {
            foreach (string host in report.ForeignHosts)
            {
                var protocols = NetworkProtocols.None;
                string origin = null;

                foreach (var item in report.Items)
                {
                    if (!string.Equals(item.Host, host, StringComparison.Ordinal)) continue;

                    protocols |= item.Protocol;
                    origin ??= OriginOf(item.EffectiveUrl);
                }

                if (origin == null)
                    continue;

                string existingId = FindServiceBoundTo(catalog, host);
                if (existingId != null)
                {
                    serviceByHost[host] = existingId;
                    continue;
                }

                string id = NetworkIds.MakeUnique(
                    NetworkIds.Suggest(host, "external-service"),
                    candidate => catalog?.FindService(candidate) != null || plannedServices.Contains(candidate));

                serviceByHost[host] = id;
                plannedServices.Add(id);

                steps.Add(LegacyMigrationStep.Service(id, protocols, $"host '{host}'"));
                steps.Add(LegacyMigrationStep.Binding(id, environmentId, origin));
            }
        }

        private static void PlanCredentialProfile(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<LegacyMigrationStep> steps)
        {
            bool anyCredential = false;
            foreach (var item in report.Items)
            {
                if (item.DeclaresCredential) { anyCredential = true; break; }
            }

            if (!anyCredential)
                return;

            if (catalog?.FindCredentialProfile(LegacyCredentialProfileId) != null)
                return;

            steps.Add(LegacyMigrationStep.CredentialProfile(LegacyCredentialProfileId));
        }

        private static void PlanEndpoints(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<LegacyMigrationStep> steps,
            List<LegacyMigrationSkip> skips,
            HashSet<string> plannedServices,
            HashSet<string> plannedEndpointIds,
            Dictionary<string, string> serviceByHost)
        {
            var requests = report.OfKind(LegacyNetworkItemKind.RequestAsset);
            if (requests.Count == 0)
                return;

            var index = catalog != null ? new NetworkCatalogIndex(catalog) : null;
            var migratedGuids = CollectMigratedSourceGuids(catalog);
            bool collectionPlanned = false;

            foreach (var item in requests)
            {
                if (!string.IsNullOrEmpty(item.AssetGuid) && migratedGuids.Contains(item.AssetGuid))
                {
                    skips.Add(new LegacyMigrationSkip(
                        item, "Already migrated — an endpoint records this asset as its source.", true));
                    continue;
                }

                if (!TryAttributeRequest(item, serviceByHost, plannedServices, catalog,
                        out string serviceId, out string relativePath, out string reason))
                {
                    skips.Add(new LegacyMigrationSkip(item, reason));
                    continue;
                }

                if (!collectionPlanned && (index == null || !index.Collections.ContainsKey(LegacyCollectionId)))
                {
                    steps.Add(LegacyMigrationStep.EndpointCollection(
                        LegacyCollectionId, NetworkIds.LegacyDefaultServiceId));
                    collectionPlanned = true;
                }

                string endpointId = NetworkIds.MakeUnique(
                    NetworkIds.Suggest(item.DisplayName, "legacy-endpoint"),
                    candidate => (index != null && index.Endpoints.ContainsKey(candidate)) ||
                                 plannedEndpointIds.Contains(candidate));

                plannedEndpointIds.Add(endpointId);

                steps.Add(LegacyMigrationStep.Endpoint(
                    endpointId, serviceId, item.Method, relativePath, item.AssetGuid, item.DisplayName));
            }
        }

        /// <summary>
        /// Decides which service and relative path a legacy request asset migrates onto.
        /// </summary>
        /// <returns><c>false</c> with a reason when the request cannot be attributed.</returns>
        private static bool TryAttributeRequest(
            LegacyNetworkItem item,
            Dictionary<string, string> serviceByHost,
            HashSet<string> plannedServices,
            NetworkCatalog catalog,
            out string serviceId,
            out string relativePath,
            out string reason)
        {
            serviceId = null;
            relativePath = null;
            reason = null;

            if (!item.IsAbsolute)
            {
                // A relative URL belongs to whatever the global base URL pointed at.
                bool legacyServiceAvailable =
                    plannedServices.Contains(NetworkIds.LegacyDefaultServiceId) ||
                    catalog?.FindService(NetworkIds.LegacyDefaultServiceId) != null;

                if (!legacyServiceAvailable)
                {
                    reason =
                        "The URL is relative but no base URL is set, so there is no origin to attribute " +
                        "it to. Set HttpModule.BaseUrl, or author the service by hand.";
                    return false;
                }

                serviceId = NetworkIds.LegacyDefaultServiceId;
                relativePath = (item.AuthoredUrl ?? string.Empty).TrimStart('/');
                return true;
            }

            if (!serviceByHost.TryGetValue(item.Host, out serviceId))
            {
                reason = $"No service covers host '{item.Host}'.";
                return false;
            }

            if (!Uri.TryCreate(item.EffectiveUrl, UriKind.Absolute, out Uri uri))
            {
                reason = $"'{item.EffectiveUrl}' is not a usable absolute URL.";
                return false;
            }

            relativePath = (uri.PathAndQuery ?? string.Empty).TrimStart('/');
            return true;
        }

        /// <summary>
        /// GUIDs already recorded as an endpoint's migration source.
        /// </summary>
        /// <remarks>
        /// The idempotence key. Provenance lives on the created endpoint rather than in a side-car list,
        /// so deleting a migrated endpoint correctly makes its source eligible again.
        /// </remarks>
        private static HashSet<string> CollectMigratedSourceGuids(NetworkCatalog catalog)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            if (catalog?.EndpointCollections == null)
                return guids;

            foreach (var collection in catalog.EndpointCollections)
            {
                if (collection?.Endpoints == null) continue;

                foreach (var endpoint in collection.Endpoints)
                {
                    if (endpoint == null) continue;
                    if (endpoint.Source != NetworkEndpointSource.LegacyMigration) continue;
                    if (!string.IsNullOrEmpty(endpoint.SourceReference))
                        guids.Add(endpoint.SourceReference);
                }
            }
            return guids;
        }

        private static string FindServiceBoundTo(NetworkCatalog catalog, string host)
        {
            if (catalog?.Services == null)
                return null;

            foreach (var service in catalog.Services)
            {
                if (service?.Bindings == null) continue;

                foreach (var binding in service.Bindings)
                {
                    if (binding == null) continue;

                    string boundHost = NetworkHostRule.HostOf(
                        NetworkOrigin.TryNormalize(binding.HttpOrigin, false, out string origin, out _)
                            ? origin
                            : null);

                    if (string.Equals(boundHost, host, StringComparison.OrdinalIgnoreCase))
                        return service.Id;
                }
            }
            return null;
        }

        /// <summary>The scheme, host, and port of an absolute URL, without its path.</summary>
        private static string OriginOf(string absoluteUrl) =>
            Uri.TryCreate(absoluteUrl, UriKind.Absolute, out Uri uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;

        /// <summary>
        /// Records providers that cannot be migrated without an authoring decision.
        /// </summary>
        /// <remarks>
        /// A provider is not migrated into an endpoint the way a request asset is: it keeps its own URL
        /// and keeps working. What the plan does is make sure a service exists for its host, so the author
        /// can point it at a route in Phase 6's streaming convergence.
        /// </remarks>
        private static void NoteUnroutableProviders(
            LegacyNetworkScanReport report,
            List<LegacyMigrationSkip> skips)
        {
            foreach (var item in report.Items)
            {
                switch (item.Kind)
                {
                    case LegacyNetworkItemKind.HttpProvider:
                    case LegacyNetworkItemKind.SseProvider:
                    case LegacyNetworkItemKind.WebSocketProvider:
                    case LegacyNetworkItemKind.SocketIoProvider:
                        break;
                    default:
                        continue;
                }

                skips.Add(new LegacyMigrationSkip(
                    item,
                    item.IsAbsolute
                        ? $"Left as authored. A service now covers '{item.Host}', so this provider can be " +
                          "pointed at a route without changing its URL."
                        : "Left as authored. It has no absolute URL of its own to attribute to a service."));
            }
        }

        /// <summary>
        /// The human-readable dry run: what will change, what will not, and why.
        /// </summary>
        /// <returns>A multi-line description. Deterministic for a given report.</returns>
        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine("Legacy networking migration — dry run");
            text.AppendLine("=====================================");
            text.AppendLine(Report.Summarize());
            text.AppendLine();

            if (_steps.Count == 0)
            {
                text.AppendLine("Nothing to do: the catalog already covers everything this scan found.");
            }
            else
            {
                text.AppendLine($"Will apply {_steps.Count} step(s) to environment '{EnvironmentId}':");
                for (int i = 0; i < _steps.Count; i++)
                    text.AppendLine($"  {i + 1}. {_steps[i].Description}");
            }

            if (_skips.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"Left untouched ({_skips.Count}):");
                foreach (var skip in _skips)
                    text.AppendLine($"  - {skip.Item.DisplayName}: {skip.Reason}");
            }

            text.AppendLine();
            text.AppendLine(
                "No legacy asset is modified or deleted. Request assets, data providers, and HttpModule " +
                "keep working exactly as they do now.");

            return text.ToString();
        }
    }
}
