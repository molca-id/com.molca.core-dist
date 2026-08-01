using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Authors endpoint templates, with collections as the master level.
    /// </summary>
    /// <remarks>
    /// Collections are the ownership and merge-conflict boundary (plan §5.2), so they are the grouping
    /// here rather than a flat endpoint list. An endpoint carries a service and a relative path but never
    /// an origin — the origin arrives from the service's binding for whichever environment the call
    /// targets, which is what makes one template usable everywhere.
    /// </remarks>
    internal sealed class NetworkEndpointsView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkEndpointsView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            _split = new NetworkHubUi.Split();
            Add(_split);

            Rebuild();
        }

        private string Selected
        {
            get => _session.SelectionFor(NetworkHubViews.Endpoints);
            set
            {
                _session.SetSelection(NetworkHubViews.Endpoints, value);
                Rebuild();
            }
        }

        private void Rebuild()
        {
            BuildMaster();
            BuildDetail();
        }

        private void BuildMaster()
        {
            _split.Master.Clear();

            var collections = _session.Catalog.EndpointCollections;

            var header = new VisualElement();
            header.AddToClassList("molca-network__master-header");
            header.Add(new Label($"{collections.Count} collection(s)"));
            header.Add(MolcaButtons.Mini("Add", AddCollection));
            _split.Master.Add(header);

            var list = new ScrollView();
            list.style.flexGrow = 1;
            _split.Master.Add(list);

            if (collections.Count == 0)
            {
                list.Add(NetworkHubUi.Note(
                    "No endpoint collections. A collection groups endpoints for one service or API " +
                    "surface, and is the asset two people can edit without colliding."));
                return;
            }

            foreach (var collection in collections)
            {
                if (collection == null)
                {
                    list.Add(NetworkHubUi.Note(
                        "A collection reference is missing — its asset was deleted or moved out of the project."));
                    continue;
                }

                var group = new VisualElement();
                group.AddToClassList("molca-network__group");

                var title = new Label($"{collection.DisplayName}  ·  {collection.Endpoints.Count}");
                title.AddToClassList("molca-network__group-title");
                title.tooltip = string.IsNullOrEmpty(collection.ServiceId)
                    ? "This collection names no default service; each endpoint must name its own."
                    : $"Default service: {collection.ServiceId}";
                group.Add(title);

                if (collection.Endpoints.Count == 0)
                    group.Add(NetworkHubUi.Note("No endpoints in this collection."));

                foreach (var endpoint in collection.Endpoints)
                {
                    if (endpoint == null) continue;

                    var badges = new List<VisualElement>
                    {
                        NetworkHubUi.Badge(endpoint.Method.ToString()),
                    };

                    if (endpoint.MutationClass != NetworkMutationClass.Safe)
                    {
                        badges.Add(NetworkHubUi.Badge(
                            endpoint.MutationClass.ToString().ToLowerInvariant(),
                            endpoint.MutationClass == NetworkMutationClass.Destructive
                                ? MolcaStatusKind.Error
                                : MolcaStatusKind.Warning));
                    }

                    if (endpoint.Source != NetworkEndpointSource.Authored)
                        badges.Add(NetworkHubUi.Badge(endpoint.Source == NetworkEndpointSource.LegacyMigration
                            ? "migrated"
                            : "imported"));

                    group.Add(NetworkHubUi.ListRow(
                        endpoint.Id,
                        string.IsNullOrEmpty(endpoint.RelativePath) ? "/" : endpoint.RelativePath,
                        NetworkHubUi.StatusOf(
                            _session.Validation, NetworkValidationEntityKind.Endpoint, endpoint.Id),
                        "Validation status for this endpoint.",
                        string.Equals(endpoint.Id, Selected, StringComparison.Ordinal),
                        () => Selected = endpoint.Id,
                        badges.ToArray()));
                }

                var target = collection;
                group.Add(NetworkHubUi.Actions(
                    MolcaButtons.Mini("Add endpoint", () => AddEndpoint(target)),
                    MolcaButtons.Mini("Import OpenAPI…", () =>
                        NetworkOpenApiImportWindow.Open(_session.Catalog, target, _session.Reload)),
                    MolcaButtons.Mini("Locate asset", () =>
                    {
                        Selection.activeObject = target;
                        EditorGUIUtility.PingObject(target);
                    })));

                list.Add(group);
            }
        }

        private void BuildDetail()
        {
            _split.Detail.Clear();

            if (!TryFindEndpoint(Selected, out var endpoint, out var collection))
            {
                _split.Detail.Add(NetworkHubUi.Note("Select an endpoint."));
                return;
            }

            _split.Detail.Add(BuildIdentity(endpoint, collection));
            _split.Detail.Add(BuildParameters(endpoint));
            _split.Detail.Add(BuildBodyAndResponse(endpoint));
            _split.Detail.Add(BuildResolvedPreview(endpoint, collection));
            _split.Detail.Add(BuildSource(endpoint));
            _split.Detail.Add(BuildFindings(endpoint));
        }

        private bool TryFindEndpoint(
            string endpointId,
            out NetworkEndpointDefinition endpoint,
            out NetworkEndpointCollection collection)
        {
            endpoint = null;
            collection = null;

            if (string.IsNullOrEmpty(endpointId))
                return false;

            foreach (var candidate in _session.Catalog.EndpointCollections)
            {
                var found = candidate?.FindEndpoint(endpointId);
                if (found == null) continue;

                endpoint = found;
                collection = candidate;
                return true;
            }
            return false;
        }

        private VisualElement BuildIdentity(
            NetworkEndpointDefinition endpoint,
            NetworkEndpointCollection collection)
        {
            var card = NetworkHubUi.Card(endpoint.DisplayName, endpoint.Id);

            card.Body.Add(NetworkHubUi.Field("Collection", collection.DisplayName));
            card.Body.Add(NetworkHubUi.Field("Service", collection.ResolveServiceId(endpoint),
                "Falls back to the collection's default service when the endpoint names none."));
            card.Body.Add(NetworkHubUi.Field("Kind", endpoint.Kind.ToString()));
            card.Body.Add(NetworkHubUi.Field("Method", endpoint.Method.ToString()));
            card.Body.Add(NetworkHubUi.Field("Relative path",
                string.IsNullOrEmpty(endpoint.RelativePath) ? "(origin itself)" : endpoint.RelativePath,
                "Relative to the service's origin for the target environment. Never absolute."));
            card.Body.Add(NetworkHubUi.Field("Policy override",
                string.IsNullOrEmpty(endpoint.PolicyProfileId) ? null : endpoint.PolicyProfileId));

            card.Body.Add(NetworkHubUi.Field("Mutation class", endpoint.MutationClass.ToString(),
                "Drives retry eligibility and the request console's production confirmation."));
            card.Body.Add(NetworkHubUi.Field("Idempotency key required",
                endpoint.RequiresIdempotencyKey ? "Yes" : "No"));
            card.Body.Add(NetworkHubUi.Field("Safe to repeat", endpoint.IsIdempotent ? "Yes" : "No",
                "A mutating call is not retried merely because it failed."));

            if (endpoint.Tags.Count > 0)
                card.Body.Add(NetworkHubUi.Field("Tags", string.Join(", ", endpoint.Tags)));

            if (!string.IsNullOrEmpty(endpoint.Description))
                card.Body.Add(NetworkHubUi.Field("Description", endpoint.Description));

            return card;
        }

        private VisualElement BuildParameters(NetworkEndpointDefinition endpoint)
        {
            int total = endpoint.PathParameters.Count +
                        endpoint.QueryParameters.Count +
                        endpoint.HeaderParameters.Count;

            var card = NetworkHubUi.Card("Parameters", total == 0 ? "None declared" : null);

            if (total == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "A caller can still supply a path and query directly; declared parameters are what the " +
                    "request console and validation use."));
                return card;
            }

            AppendParameters(card.Body, "Path", endpoint.PathParameters);
            AppendParameters(card.Body, "Query", endpoint.QueryParameters);
            AppendParameters(card.Body, "Header", endpoint.HeaderParameters);

            return card;
        }

        private static void AppendParameters(
            VisualElement body,
            string heading,
            IReadOnlyList<NetworkParameterDefinition> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return;

            body.Add(NetworkHubUi.Heading(heading));

            foreach (var parameter in parameters)
            {
                if (parameter == null) continue;

                var flags = new List<string>();
                if (parameter.Required) flags.Add("required");
                // A sensitive parameter is redacted in diagnostics and history; saying so here is why an
                // author would mark one.
                if (parameter.Sensitive) flags.Add("redacted");
                if (!string.IsNullOrEmpty(parameter.DefaultValue)) flags.Add($"default '{parameter.DefaultValue}'");

                body.Add(NetworkHubUi.Field(
                    parameter.Name,
                    flags.Count == 0 ? "optional" : string.Join(" · ", flags),
                    parameter.Description));
            }
        }

        private VisualElement BuildBodyAndResponse(NetworkEndpointDefinition endpoint)
        {
            var card = NetworkHubUi.Card("Body and response");

            card.Body.Add(NetworkHubUi.Field("Request body", endpoint.BodyType.ToString()));
            card.Body.Add(NetworkHubUi.Field("Expected response", endpoint.ExpectedResponseType.ToString()));

            if (!string.IsNullOrEmpty(endpoint.ResponseTypeName))
                card.Body.Add(NetworkHubUi.Field("Response type", endpoint.ResponseTypeName));

            if (!string.IsNullOrEmpty(endpoint.RequestBodyExample))
            {
                card.Body.Add(NetworkHubUi.Heading("Example body"));
                var example = new TextField { multiline = true, value = endpoint.RequestBodyExample };
                example.AddToClassList("molca-network__report");
                example.isReadOnly = true;
                card.Body.Add(example);
                card.Body.Add(NetworkHubUi.Note(
                    "An authoring example. Never real credentials or customer data — this asset is committed."));
            }

            return card;
        }

        private VisualElement BuildResolvedPreview(
            NetworkEndpointDefinition endpoint,
            NetworkEndpointCollection collection)
        {
            string environmentId = _session.PreviewEnvironmentId;
            string serviceId = collection.ResolveServiceId(endpoint);

            var card = NetworkHubUi.Card(
                "Resolved preview",
                string.IsNullOrEmpty(environmentId) ? null : $"Under '{environmentId}'");

            if (string.IsNullOrEmpty(environmentId) || _session.Effective == null)
            {
                card.Body.Add(NetworkHubUi.Note("Choose a preview environment in the toolbar."));
                return card;
            }

            var route = _session.Effective.Resolve(
                new Molca.Networking.Routing.NetworkRouteKey(environmentId, serviceId),
                endpoint.RequiredProtocol,
                endpoint.Id);

            card.SetStatus(
                route.Resolves ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                route.Resolves ? "Resolves" : route.FailureCategory.ToString());

            if (!route.Resolves)
            {
                card.Body.Add(NetworkHubUi.Note(route.FailureReason));
                return card;
            }

            card.Body.Add(NetworkHubUi.Field("URI", route.ResolvedUri));
            card.Body.Add(NetworkHubUi.Actions(MolcaButtons.Mini(
                "Open service",
                () => _session.Navigate(NetworkHubNavigationTarget.Service(serviceId, environmentId)))));

            return card;
        }

        private VisualElement BuildSource(NetworkEndpointDefinition endpoint)
        {
            if (endpoint.Source == NetworkEndpointSource.Authored)
                return new VisualElement();

            var card = NetworkHubUi.Card(
                "Source",
                endpoint.Source == NetworkEndpointSource.LegacyMigration
                    ? "Produced by legacy migration"
                    : "Imported");

            card.Body.Add(NetworkHubUi.Field("Origin", endpoint.SourceReference,
                endpoint.Source == NetworkEndpointSource.LegacyMigration
                    ? "The GUID of the request asset this came from. Migration reads it to know this asset " +
                      "is already migrated, so a re-run skips it."
                    : "The operation this was imported from."));

            if (!string.IsNullOrEmpty(endpoint.SourceHash))
            {
                card.Body.Add(NetworkHubUi.Field("Content hash", endpoint.SourceHash,
                    "Lets a re-import show a diff instead of overwriting local edits."));
            }

            return card;
        }

        private VisualElement BuildFindings(NetworkEndpointDefinition endpoint)
        {
            var findings = NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.Endpoint, endpoint.Id);

            var card = NetworkHubUi.Card(
                "Validation",
                null,
                findings.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(findings[0].Severity),
                findings.Count == 0 ? "Clear" : $"{findings.Count} finding(s)");

            if (findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("Nothing reported for this endpoint."));
                return card;
            }

            foreach (var finding in findings)
                card.Body.Add(NetworkHubUi.FindingRow(finding));

            return card;
        }

        // ---- Authoring actions

        private void AddCollection()
        {
            var index = new NetworkCatalogIndex(_session.Catalog);
            string id = NetworkIds.MakeUnique(
                "endpoints", candidate => index.Collections.ContainsKey(candidate));

            Report(_session.Editing.CreateEndpointCollection(id, id));
            _session.Reload();
        }

        private void AddEndpoint(NetworkEndpointCollection collection)
        {
            var index = new NetworkCatalogIndex(_session.Catalog);
            string id = NetworkIds.MakeUnique(
                "endpoint", candidate => index.Endpoints.ContainsKey(candidate));

            var result = _session.Editing.CreateHttpEndpoint(
                collection, id, collection.ServiceId, HttpMethod.GET, string.Empty);

            Report(result);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Endpoints, result.ResultId);

            _session.Reload();
        }

        private static void Report(NetworkAuthoringResult result)
        {
            if (result.Success)
                UnityEngine.Debug.Log($"[Network] {result.Message}");
            else
                UnityEngine.Debug.LogWarning($"[Network] {result.Message}");
        }
    }
}
