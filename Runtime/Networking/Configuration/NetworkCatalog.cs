using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.Settings;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// The project's network catalog: environments, services and their per-environment bindings,
    /// policy profiles, credential metadata, and endpoint collection references.
    /// </summary>
    /// <remarks>
    /// A <see cref="SettingModule"/>, so it is discovered and loaded through
    /// <see cref="GlobalSettings"/> like every other project setting. It has **no**
    /// <see cref="SettingState"/>: the catalog is read-only configuration, and
    /// <see cref="CreateState"/> deliberately returns <c>null</c>. Anything mutable at
    /// runtime — active environment selection, session state, queues — belongs to the network
    /// subsystem, not here.
    /// <para>
    /// One catalog is the project default. Additional catalogs may exist for tests and tools but are
    /// never auto-discovered as the default (ADR decision 1).
    /// </para>
    /// <para>
    /// No field on this asset or anything it serializes holds a credential value. See
    /// <see cref="NetworkCredentialProfile"/>.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(
        fileName = "Network Catalog",
        menuName = "Molca/Networking/Network Catalog",
        order = 19)]
    public class NetworkCatalog : SettingModule
    {
        /// <summary>
        /// Schema version this build of the framework authors and understands. Bump when the
        /// serialized shape changes in a way that needs a migration step.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        [Tooltip("Serialized schema version. Managed by migration; do not edit by hand.")]
        [SerializeField] private int _schemaVersion = CurrentSchemaVersion;

        [Header("Defaults")]
        [Tooltip("Environment used when a call site does not name one. Must match an environment below.")]
        [SerializeField] private string _defaultEnvironmentId = "";

        [Tooltip("Policy profile applied when nothing more specific is authored. Empty uses the library defaults.")]
        [SerializeField] private string _defaultPolicyProfileId = "";

        [Header("Environments")]
        [SerializeField] private List<NetworkEnvironmentProfile> _environments = new List<NetworkEnvironmentProfile>();

        [Header("Services")]
        [SerializeField] private List<NetworkServiceDefinition> _services = new List<NetworkServiceDefinition>();

        [Header("Policies")]
        [SerializeField] private List<NetworkPolicyProfile> _policyProfiles = new List<NetworkPolicyProfile>();

        [Header("Credentials (metadata only — never secret values)")]
        [SerializeField] private List<NetworkCredentialProfile> _credentialProfiles = new List<NetworkCredentialProfile>();

        [Header("Endpoints")]
        [Tooltip("Every endpoint collection in this catalog. Services reference a subset of these.")]
        [SerializeField] private List<NetworkEndpointCollection> _endpointCollections = new List<NetworkEndpointCollection>();

        [Header("Validation and build gates")]
        [Tooltip("Fail the build when catalog validation reports an error. Warning-only while the routed pipeline rolls out.")]
        [SerializeField] private bool _failBuildOnValidationError = false;

        [Tooltip("Require every production service with a credential profile to have a reachable credential source before building.")]
        [SerializeField] private bool _requireProductionCredentialSource = true;

        [Tooltip("Allow the Hub request console to send mutating requests to production environments, subject to a per-send confirmation.")]
        [SerializeField] private bool _allowProductionConsoleMutations = false;

        [Header("Legacy compatibility")]
        [Tooltip("Transition flag: keep applying global HttpModule auth to unrelated full URLs. Produces a warning; never enabled for a new catalog.")]
        [SerializeField] private bool _allowLegacyGlobalAuthOnExternalUrls = false;

        [Tooltip("Execute legacy HttpClient sends through the routed pipeline when they map to a route. Off until a project has verified parity.")]
        [SerializeField] private bool _routeLegacyHttpThroughPipeline = false;

        [Tooltip("Schema version this catalog was last migrated by. 0 means it was authored, not migrated.")]
        [SerializeField] private int _migratedFromSchemaVersion = 0;

        [Tooltip("GUID of the asset legacy migration read to produce this catalog, for audit.")]
        [SerializeField] private string _legacySourceGuid = "";

        /// <summary>Serialized schema version of this asset.</summary>
        public int SchemaVersion => _schemaVersion;

        /// <summary>Whether this asset predates <see cref="CurrentSchemaVersion"/> and needs migration.</summary>
        public bool RequiresSchemaMigration => _schemaVersion < CurrentSchemaVersion;

        /// <summary>ID of the environment used when a call site does not name one.</summary>
        public string DefaultEnvironmentId => _defaultEnvironmentId;

        /// <summary>Catalog-wide policy profile ID, or empty to use the library defaults.</summary>
        public string DefaultPolicyProfileId => _defaultPolicyProfileId;

        /// <summary>Authored environment profiles.</summary>
        public IReadOnlyList<NetworkEnvironmentProfile> Environments => _environments;

        /// <summary>Authored service definitions.</summary>
        public IReadOnlyList<NetworkServiceDefinition> Services => _services;

        /// <summary>Authored policy profiles.</summary>
        public IReadOnlyList<NetworkPolicyProfile> PolicyProfiles => _policyProfiles;

        /// <summary>Authored credential profiles. Metadata only.</summary>
        public IReadOnlyList<NetworkCredentialProfile> CredentialProfiles => _credentialProfiles;

        /// <summary>Every endpoint collection in this catalog. Entries may be <c>null</c> if an asset was deleted.</summary>
        public IReadOnlyList<NetworkEndpointCollection> EndpointCollections => _endpointCollections;

        /// <summary>Whether validation errors fail the build.</summary>
        public bool FailBuildOnValidationError => _failBuildOnValidationError;

        /// <summary>Whether production services must have a reachable credential source before building.</summary>
        public bool RequireProductionCredentialSource => _requireProductionCredentialSource;

        /// <summary>
        /// Whether the request console may send mutating requests to production, still subject to a
        /// per-send confirmation (ADR decision 2).
        /// </summary>
        public bool AllowProductionConsoleMutations => _allowProductionConsoleMutations;

        /// <summary>
        /// Transition flag permitting legacy global auth on unrelated full URLs. Produces a warning
        /// wherever it is honoured, and is never set for a newly created catalog (ADR decision 4).
        /// </summary>
        public bool AllowLegacyGlobalAuthOnExternalUrls => _allowLegacyGlobalAuthOnExternalUrls;

        /// <summary>
        /// Whether <c>HttpClient</c> executes a legacy send through the routed pipeline when the request
        /// maps to a route. Off by default.
        /// </summary>
        /// <remarks>
        /// Rollout step 4 (plan §10.3): the adapter ships before it becomes the default, so a project can
        /// migrate its catalog, compare routed and legacy behaviour, and only then switch execution over.
        /// Turning it on changes nothing about the <c>IHttpClient</c> API, the events it raises, or the
        /// request history — only which pipeline runs the transport attempts.
        /// <para>
        /// Credential scoping on external full URLs is <b>not</b> gated by this flag; that correction
        /// applies as soon as a catalog exists. See <see cref="AllowLegacyGlobalAuthOnExternalUrls"/>.
        /// </para>
        /// </remarks>
        public bool RouteLegacyHttpThroughPipeline => _routeLegacyHttpThroughPipeline;

        /// <summary>Schema version this catalog was migrated from, or 0 when authored directly.</summary>
        public int MigratedFromSchemaVersion => _migratedFromSchemaVersion;

        /// <summary>GUID of the asset legacy migration read to produce this catalog.</summary>
        public string LegacySourceGuid => _legacySourceGuid;

        /// <summary>
        /// Finds an environment profile by ID.
        /// </summary>
        /// <param name="environmentId">The environment ID to look up.</param>
        /// <returns>The profile, or <c>null</c> when absent.</returns>
        public NetworkEnvironmentProfile FindEnvironment(string environmentId)
        {
            if (_environments == null || string.IsNullOrEmpty(environmentId)) return null;
            for (int i = 0; i < _environments.Count; i++)
            {
                var environment = _environments[i];
                if (environment != null && string.Equals(environment.Id, environmentId, StringComparison.Ordinal))
                    return environment;
            }
            return null;
        }

        /// <summary>
        /// Finds a service definition by ID.
        /// </summary>
        /// <param name="serviceId">The service ID to look up.</param>
        /// <returns>The definition, or <c>null</c> when absent.</returns>
        public NetworkServiceDefinition FindService(string serviceId)
        {
            if (_services == null || string.IsNullOrEmpty(serviceId)) return null;
            for (int i = 0; i < _services.Count; i++)
            {
                var service = _services[i];
                if (service != null && string.Equals(service.Id, serviceId, StringComparison.Ordinal))
                    return service;
            }
            return null;
        }

        /// <summary>
        /// Finds a policy profile by ID.
        /// </summary>
        /// <param name="policyProfileId">The profile ID to look up.</param>
        /// <returns>The profile, or <c>null</c> when absent.</returns>
        public NetworkPolicyProfile FindPolicyProfile(string policyProfileId)
        {
            if (_policyProfiles == null || string.IsNullOrEmpty(policyProfileId)) return null;
            for (int i = 0; i < _policyProfiles.Count; i++)
            {
                var profile = _policyProfiles[i];
                if (profile != null && string.Equals(profile.Id, policyProfileId, StringComparison.Ordinal))
                    return profile;
            }
            return null;
        }

        /// <summary>
        /// Finds a credential profile by ID.
        /// </summary>
        /// <param name="credentialProfileId">The profile ID to look up.</param>
        /// <returns>The profile, or <c>null</c> when absent.</returns>
        public NetworkCredentialProfile FindCredentialProfile(string credentialProfileId)
        {
            if (_credentialProfiles == null || string.IsNullOrEmpty(credentialProfileId)) return null;
            for (int i = 0; i < _credentialProfiles.Count; i++)
            {
                var profile = _credentialProfiles[i];
                if (profile != null && string.Equals(profile.Id, credentialProfileId, StringComparison.Ordinal))
                    return profile;
            }
            return null;
        }

        /// <summary>
        /// The catalog has no runtime-mutable state — it is read-only configuration.
        /// </summary>
        /// <returns>Always <c>null</c>.</returns>
        public override SettingState CreateState() => null;

        /// <summary>No-op: the catalog is not persisted to <see cref="PlayerPrefs"/>.</summary>
        public override void SaveSettings() { }

        /// <summary>No-op: the catalog is authored in the asset, not loaded from preferences.</summary>
        public override void LoadSettings() { }

        // ---- Editing entry points (migration, import, tests). Hub authoring goes through
        // SerializedObject/SerializedProperty in NetworkCatalogEditingService instead, so it gets
        // Undo and dirty tracking for free.

        /// <summary>Appends an environment profile.</summary>
        /// <param name="environment">The profile to add.</param>
        internal void AddEnvironment(NetworkEnvironmentProfile environment)
        {
            _environments ??= new List<NetworkEnvironmentProfile>();
            _environments.Add(environment);
        }

        /// <summary>Appends a service definition.</summary>
        /// <param name="service">The definition to add.</param>
        internal void AddService(NetworkServiceDefinition service)
        {
            _services ??= new List<NetworkServiceDefinition>();
            _services.Add(service);
        }

        /// <summary>Appends a policy profile.</summary>
        /// <param name="profile">The profile to add.</param>
        internal void AddPolicyProfile(NetworkPolicyProfile profile)
        {
            _policyProfiles ??= new List<NetworkPolicyProfile>();
            _policyProfiles.Add(profile);
        }

        /// <summary>Appends a credential profile.</summary>
        /// <param name="profile">The profile to add.</param>
        internal void AddCredentialProfile(NetworkCredentialProfile profile)
        {
            _credentialProfiles ??= new List<NetworkCredentialProfile>();
            _credentialProfiles.Add(profile);
        }

        /// <summary>Appends an endpoint collection reference.</summary>
        /// <param name="collection">The collection asset to reference.</param>
        internal void AddEndpointCollection(NetworkEndpointCollection collection)
        {
            _endpointCollections ??= new List<NetworkEndpointCollection>();
            _endpointCollections.Add(collection);
        }

        /// <summary>Sets the default environment ID.</summary>
        /// <param name="environmentId">The environment to default to.</param>
        internal void SetDefaultEnvironmentId(string environmentId) => _defaultEnvironmentId = environmentId;

        /// <summary>Records that migration produced this catalog.</summary>
        /// <param name="fromSchemaVersion">Schema version migrated from.</param>
        /// <param name="legacySourceGuid">GUID of the asset that was read.</param>
        internal void SetMigrationProvenance(int fromSchemaVersion, string legacySourceGuid)
        {
            _migratedFromSchemaVersion = fromSchemaVersion;
            _legacySourceGuid = legacySourceGuid ?? string.Empty;
        }

        /// <summary>Stamps the schema version. Called only by the schema migrator.</summary>
        /// <param name="schemaVersion">The version this asset now conforms to.</param>
        internal void SetSchemaVersion(int schemaVersion) => _schemaVersion = schemaVersion;
    }
}
