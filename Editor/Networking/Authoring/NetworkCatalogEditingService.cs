using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// The one write path into a <see cref="NetworkCatalog"/>. Hub views, MCP tools, migration, and
    /// tests all edit through this service; nothing writes catalog fields directly.
    /// </summary>
    /// <remarks>
    /// Every mutation goes through <see cref="SerializedObject"/>/<see cref="SerializedProperty"/>, so
    /// Undo, dirty tracking, and multi-object edits behave the way the rest of the editor does.
    /// Operations that touch more than one asset — an ID refactor rewriting references across
    /// collections — are grouped into a single Undo step, so a partially applied rename is not a state
    /// the project can end up in.
    /// <para>
    /// Instance-based and cheap to construct; holds no static state and does not depend on a Hub
    /// window existing.
    /// </para>
    /// </remarks>
    public sealed class NetworkCatalogEditingService
    {
        // Serialized field names. Kept as constants so a field rename breaks compilation here rather
        // than silently no-op'ing a SerializedObject lookup at runtime.
        private const string FieldDefaultEnvironmentId = "_defaultEnvironmentId";
        private const string FieldDefaultPolicyProfileId = "_defaultPolicyProfileId";
        private const string FieldEnvironments = "_environments";
        private const string FieldServices = "_services";
        private const string FieldPolicyProfiles = "_policyProfiles";
        private const string FieldCredentialProfiles = "_credentialProfiles";
        private const string FieldEndpointCollections = "_endpointCollections";
        private const string FieldId = "_id";
        private const string FieldCollectionId = "_collectionId";
        private const string FieldDisplayName = "_displayName";
        private const string FieldClassification = "_classification";
        private const string FieldProtocols = "_protocols";
        private const string FieldServiceId = "_serviceId";
        private const string FieldPolicyProfileId = "_policyProfileId";
        private const string FieldCredentialProfileId = "_credentialProfileId";
        private const string FieldAllowedServiceIds = "_allowedServiceIds";
        private const string FieldBindings = "_bindings";
        private const string FieldEnvironmentId = "_environmentId";
        private const string FieldHttpOrigin = "_httpOrigin";
        private const string FieldEndpoints = "_endpoints";
        private const string FieldRelativePath = "_relativePath";
        private const string FieldMethod = "_method";
        private const string FieldProviderKind = "_providerKind";
        private const string FieldSource = "_source";
        private const string FieldSourceReference = "_sourceReference";
        private const string FieldSourceHash = "_sourceHash";
        private const string FieldKind = "_kind";
        private const string FieldDescription = "_description";
        private const string FieldTags = "_tags";
        private const string FieldBodyType = "_bodyType";
        private const string FieldRequestBodyExample = "_requestBodyExample";
        private const string FieldMutationClass = "_mutationClass";
        private const string FieldPathParameters = "_pathParameters";
        private const string FieldQueryParameters = "_queryParameters";
        private const string FieldHeaderParameters = "_headerParameters";
        private const string FieldName = "_name";
        private const string FieldRequired = "_required";
        private const string FieldSensitive = "_sensitive";
        private const string FieldDefaultValue = "_defaultValue";
        private const string FieldLegacySourceGuid = "_legacySourceGuid";
        private const string FieldOverallTimeoutSeconds = "_overallTimeoutSeconds";
        private const string FieldAttemptTimeoutSeconds = "_attemptTimeoutSeconds";
        private const string FieldRetryEnabled = "_retryEnabled";
        private const string FieldMaxRetries = "_maxRetries";
        private const string FieldRetryBaseDelaySeconds = "_retryBaseDelaySeconds";
        private const string FieldMaxConcurrentRequests = "_maxConcurrentRequests";

        private readonly NetworkCatalog _catalog;

        /// <summary>The catalog this service edits.</summary>
        public NetworkCatalog Catalog => _catalog;

        /// <summary>Creates a service bound to one catalog.</summary>
        /// <param name="catalog">The catalog to edit.</param>
        /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <c>null</c>.</exception>
        public NetworkCatalogEditingService(NetworkCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        // ---- Creation

        /// <summary>
        /// Adds an environment profile.
        /// </summary>
        /// <param name="requestedId">Preferred ID; made unique if it collides.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <param name="classification">Safety posture for the new environment.</param>
        /// <param name="makeDefault">
        /// When <c>true</c>, the new environment becomes the catalog default. Also applied
        /// automatically when the catalog has no default yet.
        /// </param>
        /// <returns>The outcome, carrying the ID actually used.</returns>
        public NetworkAuthoringResult CreateEnvironment(
            string requestedId,
            string displayName = null,
            NetworkEnvironmentClassification classification = NetworkEnvironmentClassification.Development,
            bool makeDefault = false)
        {
            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            var serialized = new SerializedObject(_catalog);
            var list = serialized.FindProperty(FieldEnvironments);

            string id = NetworkIds.MakeUnique(requestedId, candidate => _catalog.FindEnvironment(candidate) != null);

            var element = AppendElement(list);
            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = displayName ?? id;
            element.FindPropertyRelative(FieldClassification).enumValueIndex = (int)classification;

            bool becomesDefault = makeDefault || string.IsNullOrEmpty(_catalog.DefaultEnvironmentId);
            if (becomesDefault)
                serialized.FindProperty(FieldDefaultEnvironmentId).stringValue = id;

            Apply(serialized, "Create Network Environment");

            string suffix = becomesDefault ? " and set as the default" : "";
            return NetworkAuthoringResult.Ok($"Created environment '{id}'{suffix}.", id);
        }

        /// <summary>
        /// Adds a service definition.
        /// </summary>
        /// <param name="requestedId">Preferred ID; made unique if it collides.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <param name="protocols">Protocols the service declares.</param>
        /// <returns>The outcome, carrying the ID actually used.</returns>
        public NetworkAuthoringResult CreateService(
            string requestedId,
            string displayName = null,
            NetworkProtocols protocols = NetworkProtocols.Http)
        {
            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            if (protocols == NetworkProtocols.None)
                return NetworkAuthoringResult.Fail("A service must declare at least one protocol.");

            var serialized = new SerializedObject(_catalog);
            var list = serialized.FindProperty(FieldServices);

            string id = NetworkIds.MakeUnique(requestedId, candidate => _catalog.FindService(candidate) != null);

            var element = AppendElement(list);
            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = displayName ?? id;
            element.FindPropertyRelative(FieldProtocols).intValue = (int)protocols;

            Apply(serialized, "Create Network Service");
            return NetworkAuthoringResult.Ok($"Created service '{id}'.", id);
        }

        /// <summary>
        /// Adds a policy profile seeded with the library defaults.
        /// </summary>
        /// <param name="requestedId">Preferred ID; made unique if it collides.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <returns>The outcome, carrying the ID actually used.</returns>
        public NetworkAuthoringResult CreatePolicyProfile(string requestedId, string displayName = null)
        {
            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            var serialized = new SerializedObject(_catalog);
            var list = serialized.FindProperty(FieldPolicyProfiles);

            string id = NetworkIds.MakeUnique(requestedId, candidate => _catalog.FindPolicyProfile(candidate) != null);

            var element = AppendElement(list);
            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = displayName ?? id;

            Apply(serialized, "Create Network Policy Profile");
            return NetworkAuthoringResult.Ok($"Created policy profile '{id}'.", id);
        }

        /// <summary>
        /// Adds a credential profile. Metadata only — no secret value is written anywhere.
        /// </summary>
        /// <param name="requestedId">Preferred ID; made unique if it collides.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <param name="providerKind">Which provider will supply the secret at execution time.</param>
        /// <returns>The outcome, carrying the ID actually used.</returns>
        public NetworkAuthoringResult CreateCredentialProfile(
            string requestedId,
            string displayName = null,
            NetworkCredentialProviderKind providerKind = NetworkCredentialProviderKind.None)
        {
            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            var serialized = new SerializedObject(_catalog);
            var list = serialized.FindProperty(FieldCredentialProfiles);

            string id = NetworkIds.MakeUnique(requestedId, candidate => _catalog.FindCredentialProfile(candidate) != null);

            var element = AppendElement(list);
            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = displayName ?? id;
            element.FindPropertyRelative(FieldProviderKind).enumValueIndex = (int)providerKind;

            Apply(serialized, "Create Network Credential Profile");
            return NetworkAuthoringResult.Ok($"Created credential profile '{id}'.", id);
        }

        /// <summary>
        /// Creates an endpoint collection asset and registers it on the catalog.
        /// </summary>
        /// <param name="requestedId">Preferred collection ID; made unique if it collides.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <param name="serviceId">Default service for the collection's endpoints, or <c>null</c>.</param>
        /// <param name="folder">
        /// Folder for the new asset. Defaults to a <c>Networking</c> subfolder beside the catalog, so
        /// collections live next to the thing that references them.
        /// </param>
        /// <returns>The outcome, carrying the collection ID actually used.</returns>
        public NetworkAuthoringResult CreateEndpointCollection(
            string requestedId,
            string displayName = null,
            string serviceId = null,
            string folder = null)
        {
            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            var index = new NetworkCatalogIndex(_catalog);
            string id = NetworkIds.MakeUnique(requestedId, candidate => index.Collections.ContainsKey(candidate));

            string targetFolder = folder ?? DefaultCollectionFolder();
            Directory.CreateDirectory(targetFolder);
            AssetDatabase.Refresh();

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Network Endpoint Collection");

            var collection = ScriptableObject.CreateInstance<NetworkEndpointCollection>();
            collection.Initialize(id, displayName ?? id, serviceId);

            string path = AssetDatabase.GenerateUniqueAssetPath($"{targetFolder}/{id}.asset");
            AssetDatabase.CreateAsset(collection, path);
            Undo.RegisterCreatedObjectUndo(collection, "Create Network Endpoint Collection");

            var serialized = new SerializedObject(_catalog);
            var list = serialized.FindProperty(FieldEndpointCollections);
            AppendElement(list).objectReferenceValue = collection;
            Apply(serialized, "Create Network Endpoint Collection");

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();

            return NetworkAuthoringResult.Ok($"Created endpoint collection '{id}' at {path}.", id);
        }

        // ---- Bindings

        /// <summary>
        /// Adds or updates a service's binding for one environment.
        /// </summary>
        /// <param name="serviceId">The service to bind.</param>
        /// <param name="environmentId">The environment to bind it in.</param>
        /// <param name="httpOrigin">Absolute HTTP origin, or <c>null</c> to leave it unset.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Copying a binding from one environment to another is always this explicit call. There is no
        /// implicit fallback between environments (plan §7.6).
        /// </remarks>
        public NetworkAuthoringResult SetHttpBinding(string serviceId, string environmentId, string httpOrigin)
        {
            var service = _catalog.FindService(serviceId);
            if (service == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            if (_catalog.FindEnvironment(environmentId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{environmentId}' in this catalog.");

            if (!string.IsNullOrWhiteSpace(httpOrigin) &&
                !NetworkOrigin.TryNormalize(httpOrigin, false, out httpOrigin, out string originError))
            {
                return NetworkAuthoringResult.Fail(originError);
            }

            var serialized = new SerializedObject(_catalog);
            var serviceElement = FindElementById(serialized.FindProperty(FieldServices), FieldId, serviceId);
            if (serviceElement == null)
                return NetworkAuthoringResult.Fail($"Could not resolve service '{serviceId}' in the serialized catalog.");

            var bindings = serviceElement.FindPropertyRelative(FieldBindings);
            var binding = FindElementById(bindings, FieldEnvironmentId, environmentId);

            bool created = binding == null;
            if (created)
            {
                binding = AppendElement(bindings);
                binding.FindPropertyRelative(FieldEnvironmentId).stringValue = environmentId;
            }

            binding.FindPropertyRelative(FieldHttpOrigin).stringValue = httpOrigin ?? string.Empty;
            Apply(serialized, created ? "Add Network Service Binding" : "Edit Network Service Binding");

            string verb = created ? "Bound" : "Updated";
            return NetworkAuthoringResult.Ok($"{verb} service '{serviceId}' in '{environmentId}'.", environmentId);
        }

        // ---- Endpoints

        /// <summary>
        /// Adds an HTTP endpoint template to a collection.
        /// </summary>
        /// <param name="collection">The collection to add to.</param>
        /// <param name="requestedId">Preferred endpoint ID; made unique across the catalog if it collides.</param>
        /// <param name="serviceId">Owning service, or <c>null</c> to inherit the collection's default.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">Path relative to the service origin.</param>
        /// <param name="source">Where the template came from. Defaults to hand-authored.</param>
        /// <param name="sourceReference">
        /// Asset GUID or operation ID the template derives from, or <c>null</c>. Migration records the
        /// legacy asset's GUID here, which is what lets a re-run recognize it as already migrated.
        /// </param>
        /// <returns>The outcome, carrying the endpoint ID actually used.</returns>
        public NetworkAuthoringResult CreateHttpEndpoint(
            NetworkEndpointCollection collection,
            string requestedId,
            string serviceId,
            Molca.Networking.Http.Models.HttpMethod method,
            string relativePath,
            NetworkEndpointSource source = NetworkEndpointSource.Authored,
            string sourceReference = null)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            if (!NetworkOrigin.TryJoin("https://validation.invalid", relativePath, out _, out string pathError))
                return NetworkAuthoringResult.Fail(pathError);

            // Endpoint IDs are unique catalog-wide, not per collection, so the console and deep links
            // can address an endpoint by ID alone.
            var index = new NetworkCatalogIndex(_catalog);
            string id = NetworkIds.MakeUnique(requestedId, candidate => index.Endpoints.ContainsKey(candidate));

            var serialized = new SerializedObject(collection);
            var list = serialized.FindProperty(FieldEndpoints);

            var element = AppendElement(list);
            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = id;
            element.FindPropertyRelative(FieldServiceId).stringValue = serviceId ?? string.Empty;
            element.FindPropertyRelative(FieldMethod).enumValueIndex = (int)method;
            element.FindPropertyRelative(FieldRelativePath).stringValue = relativePath ?? string.Empty;
            element.FindPropertyRelative(FieldSource).enumValueIndex = (int)source;
            element.FindPropertyRelative(FieldSourceReference).stringValue = sourceReference ?? string.Empty;

            Apply(serialized, "Create Network Endpoint");
            return NetworkAuthoringResult.Ok($"Created endpoint '{id}' in '{collection.DisplayName}'.", id);
        }

        /// <summary>
        /// Creates an endpoint from an imported definition, writing every field it carries.
        /// </summary>
        /// <param name="collection">The collection to add to.</param>
        /// <param name="requestedId">Preferred endpoint ID; made unique across the catalog if it collides.</param>
        /// <param name="import">The imported definition.</param>
        /// <returns>The outcome, carrying the endpoint ID actually used.</returns>
        /// <remarks>
        /// Separate from <see cref="CreateHttpEndpoint"/> rather than an overload of it: hand authoring
        /// creates a stub the author then fills in, while import writes a complete template in one step.
        /// Collapsing the two would mean either import needing a dozen follow-up calls, or hand authoring
        /// taking a value object nobody has yet.
        /// </remarks>
        public NetworkAuthoringResult CreateImportedEndpoint(
            NetworkEndpointCollection collection,
            string requestedId,
            NetworkEndpointImport import)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (import == null)
                return NetworkAuthoringResult.Fail("No imported definition was supplied.");

            if (!NetworkIds.IsValid(requestedId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            if (!NetworkOrigin.TryJoin("https://validation.invalid", import.RelativePath, out _, out string pathError))
                return NetworkAuthoringResult.Fail(pathError);

            var index = new NetworkCatalogIndex(_catalog);
            string id = NetworkIds.MakeUnique(requestedId, candidate => index.Endpoints.ContainsKey(candidate));

            var serialized = new SerializedObject(collection);
            var element = AppendElement(serialized.FindProperty(FieldEndpoints));

            element.FindPropertyRelative(FieldId).stringValue = id;
            element.FindPropertyRelative(FieldDisplayName).stringValue = id;
            WriteImportedFields(element, import);

            Apply(serialized, "Import Network Endpoint");
            return NetworkAuthoringResult.Ok($"Imported endpoint '{id}'.", id);
        }

        /// <summary>
        /// Rewrites an existing imported endpoint from a newer definition.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">The endpoint to rewrite.</param>
        /// <param name="import">The newer definition.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Refuses an endpoint whose <c>Source</c> is not <see cref="NetworkEndpointSource.OpenApi"/>. The
        /// importer already classifies that case as a conflict, and this is the second gate: a caller that
        /// skipped the plan still cannot overwrite hand-authored work.
        /// <para>
        /// The endpoint's ID and display name are left alone. An author who renamed an imported endpoint
        /// keeps that name — identity is the recorded source reference, not the ID.
        /// </para>
        /// </remarks>
        public NetworkAuthoringResult UpdateImportedEndpoint(
            NetworkEndpointCollection collection,
            string endpointId,
            NetworkEndpointImport import)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (import == null)
                return NetworkAuthoringResult.Fail("No imported definition was supplied.");

            var existing = collection.FindEndpoint(endpointId);
            if (existing == null)
                return NetworkAuthoringResult.Fail($"No endpoint '{endpointId}' in '{collection.DisplayName}'.");

            if (existing.Source != NetworkEndpointSource.OpenApi)
            {
                return NetworkAuthoringResult.Fail(
                    $"Endpoint '{endpointId}' was authored as {existing.Source}, so import will not " +
                    "overwrite it.");
            }

            if (!NetworkOrigin.TryJoin("https://validation.invalid", import.RelativePath, out _, out string pathError))
                return NetworkAuthoringResult.Fail(pathError);

            var serialized = new SerializedObject(collection);
            var element = FindElementById(serialized.FindProperty(FieldEndpoints), FieldId, endpointId);

            if (element == null)
                return NetworkAuthoringResult.Fail($"Could not resolve endpoint '{endpointId}' in the serialized collection.");

            WriteImportedFields(element, import);

            Apply(serialized, "Update Imported Network Endpoint");
            return NetworkAuthoringResult.Ok($"Updated imported endpoint '{endpointId}'.", endpointId);
        }

        /// <summary>Writes every field an import owns onto a serialized endpoint element.</summary>
        private static void WriteImportedFields(SerializedProperty element, NetworkEndpointImport import)
        {
            element.FindPropertyRelative(FieldServiceId).stringValue = import.ServiceId ?? string.Empty;
            element.FindPropertyRelative(FieldKind).enumValueIndex = (int)NetworkEndpointKind.Http;
            element.FindPropertyRelative(FieldMethod).enumValueIndex = (int)import.Method;
            element.FindPropertyRelative(FieldRelativePath).stringValue = import.RelativePath ?? string.Empty;
            element.FindPropertyRelative(FieldDescription).stringValue = import.Description ?? string.Empty;
            element.FindPropertyRelative(FieldBodyType).enumValueIndex = (int)import.BodyType;
            element.FindPropertyRelative(FieldRequestBodyExample).stringValue =
                import.RequestBodyExample ?? string.Empty;
            element.FindPropertyRelative(FieldMutationClass).enumValueIndex = (int)import.MutationClass;
            element.FindPropertyRelative(FieldSource).enumValueIndex = (int)NetworkEndpointSource.OpenApi;
            element.FindPropertyRelative(FieldSourceReference).stringValue = import.SourceReference ?? string.Empty;
            element.FindPropertyRelative(FieldSourceHash).stringValue = import.SourceHash ?? string.Empty;

            WriteStrings(element.FindPropertyRelative(FieldTags), import.Tags);
            WriteParameters(element.FindPropertyRelative(FieldPathParameters), import.PathParameters);
            WriteParameters(element.FindPropertyRelative(FieldQueryParameters), import.QueryParameters);
            WriteParameters(element.FindPropertyRelative(FieldHeaderParameters), import.HeaderParameters);
        }

        private static void WriteStrings(SerializedProperty list, System.Collections.Generic.List<string> values)
        {
            list.ClearArray();
            if (values == null) return;

            for (int i = 0; i < values.Count; i++)
            {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).stringValue = values[i] ?? string.Empty;
            }
        }

        /// <summary>
        /// Replaces a parameter list wholesale.
        /// </summary>
        /// <remarks>
        /// Replaced rather than merged. A parameter the spec dropped must disappear from the console's
        /// editor, and a merge would leave it there forever looking like a required input.
        /// </remarks>
        private static void WriteParameters(
            SerializedProperty list, System.Collections.Generic.List<NetworkEndpointImport.Parameter> values)
        {
            list.ClearArray();
            if (values == null) return;

            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (value == null) continue;

                list.InsertArrayElementAtIndex(i);
                var element = list.GetArrayElementAtIndex(i);

                element.FindPropertyRelative(FieldName).stringValue = value.Name ?? string.Empty;
                element.FindPropertyRelative(FieldRequired).boolValue = value.Required;
                element.FindPropertyRelative(FieldDescription).stringValue = value.Description ?? string.Empty;
                element.FindPropertyRelative(FieldSensitive).boolValue = value.Sensitive;

                // A sensitive parameter never carries a default: a spec's example for an API-key parameter
                // is exactly the kind of thing that turns out to be a real key someone pasted in. The
                // importer enforces that upstream; this is the second gate.
                element.FindPropertyRelative(FieldDefaultValue).stringValue =
                    value.Sensitive ? string.Empty : value.DefaultValue ?? string.Empty;
            }
        }

        // ---- Policy profile values
        //
        // Focused setters rather than one "configure everything" call: each mirrors a group an author
        // reasons about together, and each is a separate Undo step, so reverting a timeout change does not
        // also revert a retry change.

        /// <summary>
        /// Sets a policy profile's overall and per-attempt timeouts.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="overallSeconds">Budget covering queueing, auth, retries, and wire time.</param>
        /// <param name="attemptSeconds">Budget for a single transport attempt.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyTimeouts(
            string policyProfileId,
            float overallSeconds,
            float attemptSeconds)
        {
            if (overallSeconds < 0f || attemptSeconds < 0f)
                return NetworkAuthoringResult.Fail("Timeouts cannot be negative.");

            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldOverallTimeoutSeconds).floatValue = overallSeconds;
            element.FindPropertyRelative(FieldAttemptTimeoutSeconds).floatValue = attemptSeconds;

            Apply(serialized, "Set Network Policy Timeouts");
            return NetworkAuthoringResult.Ok(
                $"Policy '{policyProfileId}' now allows {overallSeconds}s overall and {attemptSeconds}s per attempt.",
                policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's retry behaviour.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="enabled">Whether failed attempts are retried at all.</param>
        /// <param name="maxRetries">Attempts after the first. Clamped to the field's 0–10 range.</param>
        /// <param name="baseDelaySeconds">First backoff delay; doubles per attempt.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyRetry(
            string policyProfileId,
            bool enabled,
            int maxRetries,
            float baseDelaySeconds)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldRetryEnabled).boolValue = enabled;
            element.FindPropertyRelative(FieldMaxRetries).intValue = Mathf.Clamp(maxRetries, 0, 10);
            element.FindPropertyRelative(FieldRetryBaseDelaySeconds).floatValue = Mathf.Max(0f, baseDelaySeconds);

            Apply(serialized, "Set Network Policy Retry");
            return NetworkAuthoringResult.Ok(
                enabled
                    ? $"Policy '{policyProfileId}' retries up to {maxRetries} time(s) from {baseDelaySeconds}s."
                    : $"Policy '{policyProfileId}' does not retry.",
                policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's per-route concurrency limit.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="maxConcurrentRequests">
        /// Simultaneous requests allowed per route. Clamped to the field's 0–64 range; 0 means unbounded.
        /// </param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// The legacy limit was process-wide; this one is per route. Copying the number across is
        /// deliberately conservative — it can only permit fewer simultaneous requests to any one service
        /// than the project already tolerated in total.
        /// </remarks>
        public NetworkAuthoringResult SetPolicyConcurrency(string policyProfileId, int maxConcurrentRequests)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldMaxConcurrentRequests).intValue =
                Mathf.Clamp(maxConcurrentRequests, 0, 64);

            Apply(serialized, "Set Network Policy Concurrency");
            return NetworkAuthoringResult.Ok(
                $"Policy '{policyProfileId}' allows {maxConcurrentRequests} concurrent request(s) per route.",
                policyProfileId);
        }

        /// <summary>
        /// Records which legacy asset a migration read to produce this catalog.
        /// </summary>
        /// <param name="legacySourceGuid">GUID of the legacy <c>HttpModule</c> asset.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Audit provenance, so a catalog can always answer "was this authored, or migrated, and from
        /// what?" without inspecting its contents.
        /// </remarks>
        public NetworkAuthoringResult RecordLegacySource(string legacySourceGuid)
        {
            var serialized = new SerializedObject(_catalog);
            serialized.FindProperty(FieldLegacySourceGuid).stringValue = legacySourceGuid ?? string.Empty;
            Apply(serialized, "Record Network Catalog Legacy Source");

            return NetworkAuthoringResult.Ok(
                string.IsNullOrEmpty(legacySourceGuid)
                    ? "Cleared the legacy migration source."
                    : $"Recorded legacy migration source {legacySourceGuid}.",
                legacySourceGuid);
        }

        /// <summary>Resolves a policy profile's serialized element.</summary>
        /// <returns><c>false</c> with a reason when the profile is absent.</returns>
        private bool TryFindPolicyElement(
            string policyProfileId,
            out SerializedObject serialized,
            out SerializedProperty element,
            out string error)
        {
            serialized = null;
            element = null;

            if (_catalog.FindPolicyProfile(policyProfileId) == null)
            {
                error = $"No policy profile '{policyProfileId}' in this catalog.";
                return false;
            }

            serialized = new SerializedObject(_catalog);
            element = FindElementById(serialized.FindProperty(FieldPolicyProfiles), FieldId, policyProfileId);

            if (element == null)
            {
                error = $"Could not resolve policy profile '{policyProfileId}' in the serialized catalog.";
                return false;
            }

            error = null;
            return true;
        }

        // ---- Catalog-level settings

        /// <summary>
        /// Sets the catalog's default environment.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetDefaultEnvironment(string environmentId)
        {
            if (_catalog.FindEnvironment(environmentId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{environmentId}' in this catalog.");

            var serialized = new SerializedObject(_catalog);
            serialized.FindProperty(FieldDefaultEnvironmentId).stringValue = environmentId;
            Apply(serialized, "Set Default Network Environment");

            return NetworkAuthoringResult.Ok($"Default environment is now '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Sets the catalog's default policy profile.
        /// </summary>
        /// <param name="policyProfileId">
        /// An existing policy profile ID, or empty to fall back to the library defaults.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetDefaultPolicyProfile(string policyProfileId)
        {
            if (!string.IsNullOrEmpty(policyProfileId) && _catalog.FindPolicyProfile(policyProfileId) == null)
                return NetworkAuthoringResult.Fail($"No policy profile '{policyProfileId}' in this catalog.");

            var serialized = new SerializedObject(_catalog);
            serialized.FindProperty(FieldDefaultPolicyProfileId).stringValue = policyProfileId ?? string.Empty;
            Apply(serialized, "Set Default Network Policy");

            return string.IsNullOrEmpty(policyProfileId)
                ? NetworkAuthoringResult.Ok("Cleared the default policy profile; library defaults apply.")
                : NetworkAuthoringResult.Ok($"Default policy profile is now '{policyProfileId}'.", policyProfileId);
        }

        // ---- ID refactors

        /// <summary>
        /// Renames an environment's stable ID and rewrites every reference to it.
        /// </summary>
        /// <param name="oldId">The current environment ID.</param>
        /// <param name="newId">The replacement ID.</param>
        /// <returns>
        /// The outcome, listing each reference that was rewritten. Nothing is modified on failure.
        /// </returns>
        /// <remarks>
        /// Applied as one Undo step across the catalog. References rewritten: the catalog default and
        /// every service binding naming the environment.
        /// </remarks>
        public NetworkAuthoringResult RenameEnvironmentId(string oldId, string newId)
        {
            if (_catalog.FindEnvironment(oldId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{oldId}' in this catalog.");

            if (!NetworkIds.IsValid(newId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            if (_catalog.FindEnvironment(newId) != null)
                return NetworkAuthoringResult.Fail($"An environment '{newId}' already exists.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rename Network Environment ID");

            var serialized = new SerializedObject(_catalog);

            var environment = FindElementById(serialized.FindProperty(FieldEnvironments), FieldId, oldId);
            environment.FindPropertyRelative(FieldId).stringValue = newId;

            var defaultEnvironment = serialized.FindProperty(FieldDefaultEnvironmentId);
            if (defaultEnvironment.stringValue == oldId)
            {
                defaultEnvironment.stringValue = newId;
                affected.Add("catalog default environment");
            }

            var services = serialized.FindProperty(FieldServices);
            for (int s = 0; s < services.arraySize; s++)
            {
                var service = services.GetArrayElementAtIndex(s);
                var bindings = service.FindPropertyRelative(FieldBindings);

                for (int b = 0; b < bindings.arraySize; b++)
                {
                    var environmentIdProperty = bindings.GetArrayElementAtIndex(b)
                        .FindPropertyRelative(FieldEnvironmentId);

                    if (environmentIdProperty.stringValue != oldId) continue;

                    environmentIdProperty.stringValue = newId;
                    affected.Add($"binding on service '{service.FindPropertyRelative(FieldId).stringValue}'");
                }
            }

            Apply(serialized, "Rename Network Environment ID");
            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Renamed environment '{oldId}' to '{newId}' and updated {affected.Count} reference(s).",
                newId,
                affected);
        }

        /// <summary>
        /// Renames a service's stable ID and rewrites every reference to it.
        /// </summary>
        /// <param name="oldId">The current service ID.</param>
        /// <param name="newId">The replacement ID.</param>
        /// <returns>
        /// The outcome, listing each reference that was rewritten. Nothing is modified on failure.
        /// </returns>
        /// <remarks>
        /// Applied as one Undo step spanning the catalog and every endpoint collection. References
        /// rewritten: credential scope entries, each collection's default service, and each endpoint's
        /// service.
        /// </remarks>
        public NetworkAuthoringResult RenameServiceId(string oldId, string newId)
        {
            if (_catalog.FindService(oldId) == null)
                return NetworkAuthoringResult.Fail($"No service '{oldId}' in this catalog.");

            if (!NetworkIds.IsValid(newId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            if (_catalog.FindService(newId) != null)
                return NetworkAuthoringResult.Fail($"A service '{newId}' already exists.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rename Network Service ID");

            var serialized = new SerializedObject(_catalog);

            FindElementById(serialized.FindProperty(FieldServices), FieldId, oldId)
                .FindPropertyRelative(FieldId).stringValue = newId;

            var credentials = serialized.FindProperty(FieldCredentialProfiles);
            for (int c = 0; c < credentials.arraySize; c++)
            {
                var credential = credentials.GetArrayElementAtIndex(c);
                var allowedServices = credential.FindPropertyRelative(FieldAllowedServiceIds);

                for (int a = 0; a < allowedServices.arraySize; a++)
                {
                    var entry = allowedServices.GetArrayElementAtIndex(a);
                    if (entry.stringValue != oldId) continue;

                    entry.stringValue = newId;
                    affected.Add($"credential scope on '{credential.FindPropertyRelative(FieldId).stringValue}'");
                }
            }

            Apply(serialized, "Rename Network Service ID");

            // Endpoint collections are separate assets, so each needs its own SerializedObject. They
            // join the same Undo group, which is what keeps the rename all-or-nothing.
            foreach (var collection in _catalog.EndpointCollections)
            {
                if (collection == null) continue;

                var collectionSerialized = new SerializedObject(collection);
                bool changed = false;

                var defaultService = collectionSerialized.FindProperty(FieldServiceId);
                if (defaultService.stringValue == oldId)
                {
                    defaultService.stringValue = newId;
                    affected.Add($"default service on collection '{collection.DisplayName}'");
                    changed = true;
                }

                var endpoints = collectionSerialized.FindProperty(FieldEndpoints);
                for (int e = 0; e < endpoints.arraySize; e++)
                {
                    var endpoint = endpoints.GetArrayElementAtIndex(e);
                    var endpointService = endpoint.FindPropertyRelative(FieldServiceId);
                    if (endpointService.stringValue != oldId) continue;

                    endpointService.stringValue = newId;
                    affected.Add($"endpoint '{endpoint.FindPropertyRelative(FieldId).stringValue}'");
                    changed = true;
                }

                if (changed)
                    Apply(collectionSerialized, "Rename Network Service ID");
            }

            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Renamed service '{oldId}' to '{newId}' and updated {affected.Count} reference(s).",
                newId,
                affected);
        }

        // ---- Deletion

        /// <summary>
        /// Removes an environment and every service binding that named it.
        /// </summary>
        /// <param name="environmentId">The environment to delete.</param>
        /// <returns>
        /// The outcome, listing what was removed alongside it. Refuses when this is the last
        /// environment, since a catalog with no environment can resolve nothing.
        /// </returns>
        public NetworkAuthoringResult DeleteEnvironment(string environmentId)
        {
            if (_catalog.FindEnvironment(environmentId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{environmentId}' in this catalog.");

            if (_catalog.Environments.Count <= 1)
                return NetworkAuthoringResult.Fail(
                    "This is the only environment. A catalog needs at least one to resolve any route.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Network Environment");

            var serialized = new SerializedObject(_catalog);

            var services = serialized.FindProperty(FieldServices);
            for (int s = 0; s < services.arraySize; s++)
            {
                var service = services.GetArrayElementAtIndex(s);
                var bindings = service.FindPropertyRelative(FieldBindings);

                for (int b = bindings.arraySize - 1; b >= 0; b--)
                {
                    var binding = bindings.GetArrayElementAtIndex(b);
                    if (binding.FindPropertyRelative(FieldEnvironmentId).stringValue != environmentId)
                        continue;

                    bindings.DeleteArrayElementAtIndex(b);
                    affected.Add($"binding on service '{service.FindPropertyRelative(FieldId).stringValue}'");
                }
            }

            RemoveElementById(serialized.FindProperty(FieldEnvironments), FieldId, environmentId);

            // The default must keep pointing at something that exists.
            var defaultEnvironment = serialized.FindProperty(FieldDefaultEnvironmentId);
            if (defaultEnvironment.stringValue == environmentId)
            {
                var environments = serialized.FindProperty(FieldEnvironments);
                string replacement = environments.arraySize > 0
                    ? environments.GetArrayElementAtIndex(0).FindPropertyRelative(FieldId).stringValue
                    : string.Empty;

                defaultEnvironment.stringValue = replacement;
                affected.Add($"catalog default environment (now '{replacement}')");
            }

            Apply(serialized, "Delete Network Environment");
            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Deleted environment '{environmentId}' and {affected.Count} dependent reference(s).",
                environmentId,
                affected);
        }

        /// <summary>
        /// Removes a service definition.
        /// </summary>
        /// <param name="serviceId">The service to delete.</param>
        /// <returns>
        /// The outcome. Endpoints that referenced the service are left in place and become validation
        /// findings rather than being silently deleted — losing authored endpoints to a service
        /// deletion would be worse than a reported inconsistency.
        /// </returns>
        public NetworkAuthoringResult DeleteService(string serviceId)
        {
            if (_catalog.FindService(serviceId) == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            var index = new NetworkCatalogIndex(_catalog);
            var orphaned = index.FindEndpointsForService(serviceId);

            var serialized = new SerializedObject(_catalog);
            RemoveElementById(serialized.FindProperty(FieldServices), FieldId, serviceId);
            Apply(serialized, "Delete Network Service");

            string note = orphaned.Count == 0
                ? "."
                : $". {orphaned.Count} endpoint(s) now reference a missing service and will be reported by validation.";

            return NetworkAuthoringResult.Ok($"Deleted service '{serviceId}'{note}", serviceId);
        }

        // ---- SerializedProperty helpers

        /// <summary>Appends an element to a serialized list and returns it.</summary>
        /// <param name="list">The array property to grow.</param>
        /// <remarks>
        /// Unity copies the previous element's values into a newly inserted one. Every caller here
        /// overwrites the identity fields immediately, so a duplicated ID never survives an
        /// <see cref="Apply"/>.
        /// </remarks>
        private static SerializedProperty AppendElement(SerializedProperty list)
        {
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            return list.GetArrayElementAtIndex(index);
        }

        /// <summary>Finds a list element whose named string field equals a value.</summary>
        /// <param name="list">The array property to search.</param>
        /// <param name="idField">Name of the relative string field holding the identifier.</param>
        /// <param name="id">The value to match.</param>
        /// <returns>The element, or <c>null</c> when absent.</returns>
        private static SerializedProperty FindElementById(SerializedProperty list, string idField, string id)
        {
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative(idField).stringValue == id)
                    return element;
            }
            return null;
        }

        /// <summary>Removes the first list element whose named string field equals a value.</summary>
        /// <param name="list">The array property to search.</param>
        /// <param name="idField">Name of the relative string field holding the identifier.</param>
        /// <param name="id">The value to match.</param>
        /// <returns><c>true</c> when an element was removed.</returns>
        private static bool RemoveElementById(SerializedProperty list, string idField, string id)
        {
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative(idField).stringValue != id) continue;

                list.DeleteArrayElementAtIndex(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Applies pending changes with Undo recorded under a named step.
        /// </summary>
        /// <param name="serialized">The serialized object to flush.</param>
        /// <param name="undoName">Undo step name shown in the Edit menu.</param>
        private static void Apply(SerializedObject serialized, string undoName)
        {
            Undo.SetCurrentGroupName(undoName);
            serialized.ApplyModifiedProperties();
        }

        /// <summary>
        /// A <c>Networking</c> subfolder beside the catalog asset, or the locator's canonical folder
        /// when the catalog has no asset path (an in-memory test instance).
        /// </summary>
        private string DefaultCollectionFolder()
        {
            string catalogPath = AssetDatabase.GetAssetPath(_catalog);
            if (string.IsNullOrEmpty(catalogPath))
                return NetworkCatalogLocator.CanonicalFolder + "/Networking";

            return Path.GetDirectoryName(catalogPath)?.Replace('\\', '/') + "/Networking";
        }
    }
}
