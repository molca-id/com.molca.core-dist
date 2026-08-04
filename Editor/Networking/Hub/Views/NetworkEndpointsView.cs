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

            AddCard("Identity", () => BuildIdentity(endpoint, collection));
            AddCard("Parameters", () => BuildParameters(endpoint, collection));
            AddCard("Body and response", () => BuildBodyAndResponse(endpoint, collection));
            AddCard("Resolved preview", () => BuildResolvedPreview(endpoint, collection));
            AddCard("Collection", () => BuildCollection(collection));
            AddCard("Source", () => BuildSource(endpoint));
            AddCard("Findings", () => BuildFindings(endpoint));
        }

        /// <summary>
        /// Adds one detail card, replacing it with a visible failure if it cannot be built.
        /// </summary>
        /// <param name="title">Card title, used for the replacement when <paramref name="build"/> throws.</param>
        /// <param name="build">Builds the card.</param>
        /// <remarks>
        /// <para>The detail panel is a column of independent cards, and it used to be all-or-nothing. The
        /// resolved preview threw on an endpoint with no service, and the exception unwound the entire
        /// panel — including the Collection card directly beneath it, whose <i>Default service</i> field is
        /// one of the two ways to fix precisely that. A workspace must not refuse to open on the state it
        /// exists to repair.</para>
        ///
        /// <para>The failure is not swallowed: it is logged with its stack and rendered in place of the one
        /// card that produced it. What changes is the blast radius — one card instead of the workspace.</para>
        /// </remarks>
        private void AddCard(string title, Func<VisualElement> build)
        {
            try
            {
                _split.Detail.Add(build());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);

                var card = NetworkHubUi.Card(title, "could not be built", MolcaStatusKind.Error);
                card.Body.Add(NetworkHubUi.Note(
                    $"{e.GetType().Name}: {e.Message}\n\nEvery other card on this endpoint is still "
                    + "editable. The full stack is in the Console."));
                _split.Detail.Add(card);
            }
        }

        /// <summary>
        /// Rejects text that could not be a type name at all.
        /// </summary>
        /// <param name="candidate">The proposed name.</param>
        /// <returns>Null when plausible, otherwise why not.</returns>
        /// <remarks>
        /// Shape only. Whether the type exists is exactly what the author is asserting by using the create
        /// action, so checking for it here would refuse the one case the action is for.
        /// </remarks>
        private static string IsPlausibleTypeName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return "Enter a type name.";

            string trimmed = candidate.Trim();
            if (trimmed.IndexOf(' ') >= 0)
                return "A type name cannot contain spaces.";

            foreach (char c in trimmed)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '+')
                    return $"'{c}' cannot appear in a type name.";
            }

            if (trimmed[0] == '.' || trimmed[trimmed.Length - 1] == '.')
                return "A type name cannot start or end with a dot.";

            return null;
        }

        /// <summary>
        /// The owning collection's own metadata, editable from the endpoint that led here.
        /// </summary>
        /// <remarks>
        /// Shown in the endpoint detail rather than behind a separate selection because the collection is
        /// the master level: there is no "collection selected" state to hang it off, and its default
        /// service is the value that decides what the endpoint above inherits.
        /// </remarks>
        private VisualElement BuildCollection(NetworkEndpointCollection collection)
        {
            var card = NetworkHubUi.Card(
                "Collection",
                collection.CollectionId,
                MolcaStatusKind.None);

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                collection.DisplayName,
                value => _session.Apply(_session.Editing.SetCollectionMetadata(
                    collection, value, collection.ServiceId, collection.Description))));

            card.Body.Add(NetworkHubFields.EditReference(
                "Default service",
                collection.ServiceId,
                _session.ServiceIds(),
                value => _session.Apply(_session.Editing.SetCollectionMetadata(
                    collection, collection.DisplayName, value, collection.Description)),
                NetworkHubFields.NoneLabel,
                "Inherited by every endpoint here that names no service of its own."));

            card.Body.Add(NetworkHubFields.EditTextArea(
                "Description",
                collection.Description,
                value => _session.Apply(_session.Editing.SetCollectionMetadata(
                    collection, collection.DisplayName, collection.ServiceId, value))));

            card.Body.Add(NetworkHubUi.Actions(MolcaButtons.Mini("Locate asset", () =>
            {
                Selection.activeObject = collection;
                EditorGUIUtility.PingObject(collection);
            })));

            return card;
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
            string id = endpoint.Id;
            var editing = _session.Editing;
            var card = NetworkHubUi.Card(endpoint.DisplayName, id);

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                endpoint.DisplayName,
                value => _session.Apply(editing.SetEndpointDisplayName(collection, id, value))));

            card.Body.Add(NetworkHubUi.Field("Stable ID", id,
                "Unique across the catalog, so the console and deep links can address this endpoint by ID " +
                "alone. Changing it is a refactor — use Rename ID."));

            card.Body.Add(NetworkHubUi.Field("Collection", collection.DisplayName));

            card.Body.Add(NetworkHubFields.EditReference(
                "Service",
                endpoint.ServiceId,
                _session.ServiceIds(),
                value => _session.Apply(editing.SetEndpointRoute(
                    collection, id, value, endpoint.Method, endpoint.RelativePath)),
                NetworkHubFields.InheritLabel,
                $"Empty inherits the collection's default, currently " +
                $"'{collection.ResolveServiceId(endpoint)}'."));

            card.Body.Add(NetworkHubUi.Field("Kind", endpoint.Kind.ToString()));

            card.Body.Add(NetworkHubFields.EditEnum(
                "Method",
                endpoint.Method,
                value => _session.Apply(editing.SetEndpointRoute(
                    collection, id, endpoint.ServiceId, value, endpoint.RelativePath))));

            card.Body.Add(NetworkHubFields.EditText(
                "Relative path",
                endpoint.RelativePath,
                value => _session.Apply(editing.SetEndpointRoute(
                    collection, id, endpoint.ServiceId, endpoint.Method, value)),
                "Relative to the service's origin for the target environment. Never absolute — that is what " +
                "makes one template usable in every environment.",
                "(origin itself)"));

            card.Body.Add(NetworkHubFields.EditReference(
                "Policy override",
                endpoint.PolicyProfileId,
                _session.PolicyProfileIds(),
                value => _session.Apply(editing.SetEndpointPolicyProfile(collection, id, value)),
                NetworkHubFields.InheritLabel,
                "Empty inherits the service's policy."));

            card.Body.Add(NetworkHubFields.EditEnum(
                "Mutation class",
                endpoint.MutationClass,
                value => _session.Apply(editing.SetEndpointSafety(
                    collection, id, value, endpoint.RequiresIdempotencyKey)),
                "Drives retry eligibility and the request console's production confirmation. Not cosmetic."));

            card.Body.Add(NetworkHubFields.EditToggle(
                "Idempotency key required",
                endpoint.RequiresIdempotencyKey,
                value => _session.Apply(editing.SetEndpointSafety(
                    collection, id, endpoint.MutationClass, value))));

            card.Body.Add(NetworkHubUi.Field("Safe to repeat", endpoint.IsIdempotent ? "Yes" : "No",
                "Derived from the mutation class and the idempotency requirement. A mutating call is not " +
                "retried merely because it failed."));

            card.Body.Add(NetworkHubFields.EditTextArea(
                "Description",
                endpoint.Description,
                value => _session.Apply(editing.SetEndpointDocumentation(
                    collection, id, value, endpoint.Tags))));

            card.Body.Add(NetworkHubFields.EditStringList(
                "Tags",
                endpoint.Tags,
                values => _session.Apply(editing.SetEndpointDocumentation(
                    collection, id, endpoint.Description, values)),
                "tag",
                "No tags."));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Rename ID…", () => RenameId(collection, id)),
                MolcaButtons.Mini("Delete…", () => Delete(collection, id))));

            return card;
        }

        private void RenameId(NetworkEndpointCollection collection, string oldId)
        {
            string newId = NetworkHubPrompt.ForId(
                "Rename endpoint ID",
                $"'{oldId}' can be addressed by ID from the request console, a deep link, and a service's " +
                "health check. Renaming rewrites those references in one Undo step.",
                oldId);

            if (string.IsNullOrEmpty(newId) || string.Equals(newId, oldId, StringComparison.Ordinal))
                return;

            var result = _session.Editing.RenameEndpointId(collection, oldId, newId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Endpoints, newId);

            _session.Apply(result);
        }

        private void Delete(NetworkEndpointCollection collection, string endpointId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete endpoint?",
                    $"'{endpointId}' will be removed from '{collection.DisplayName}'. Any service that " +
                    "health-checks through it will have that reference cleared.\n\nThis is one Undo step.",
                    "Delete", "Cancel"))
            {
                return;
            }

            var result = _session.Editing.DeleteEndpoint(collection, endpointId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Endpoints, string.Empty);

            _session.Apply(result);
        }

        private VisualElement BuildParameters(
            NetworkEndpointDefinition endpoint,
            NetworkEndpointCollection collection)
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
            }

            AppendParameters(card.Body, endpoint, collection,
                NetworkCatalogEditingService.EndpointParameterKind.Path, endpoint.PathParameters);
            AppendParameters(card.Body, endpoint, collection,
                NetworkCatalogEditingService.EndpointParameterKind.Query, endpoint.QueryParameters);
            AppendParameters(card.Body, endpoint, collection,
                NetworkCatalogEditingService.EndpointParameterKind.Header, endpoint.HeaderParameters);

            return card;
        }

        /// <summary>
        /// One parameter list, editable: name, required, sensitive, default, and description per entry.
        /// </summary>
        /// <remarks>
        /// The default field is disabled for a sensitive parameter rather than hidden, because the reason
        /// matters: a default for an API-key parameter is exactly the kind of thing that turns out to be a
        /// real key, and this asset is committed. The editing service drops it regardless of what the
        /// control does.
        /// </remarks>
        private void AppendParameters(
            VisualElement body,
            NetworkEndpointDefinition endpoint,
            NetworkEndpointCollection collection,
            NetworkCatalogEditingService.EndpointParameterKind kind,
            IReadOnlyList<NetworkParameterDefinition> parameters)
        {
            body.Add(NetworkHubUi.Heading(kind.ToString()));

            string id = endpoint.Id;
            var current = Snapshot(parameters);

            for (int i = 0; i < current.Count; i++)
            {
                int index = i;
                var parameter = current[index];

                var group = new VisualElement();
                group.AddToClassList("molca-network__parameter");

                var header = new VisualElement();
                header.AddToClassList("molca-network__list-row");

                var name = new TextField { value = parameter.Name };
                name.AddToClassList("molca-network__list-entry");
                name.textEdition.placeholder = "name";
                name.RegisterCallback<BlurEvent>(_ =>
                {
                    string next = name.value?.Trim() ?? string.Empty;
                    if (string.Equals(next, parameter.Name, StringComparison.Ordinal)) return;

                    Commit(collection, id, kind, current, index, p => p.Name = next);
                });
                header.Add(name);

                header.Add(MolcaButtons.Mini("Remove", () =>
                {
                    var next = Snapshot(parameters);
                    next.RemoveAt(index);
                    _session.Apply(_session.Editing.SetEndpointParameters(collection, id, kind, next));
                }));

                group.Add(header);

                group.Add(NetworkHubFields.EditToggle(
                    "Required",
                    parameter.Required,
                    value => Commit(collection, id, kind, current, index, p => p.Required = value)));

                group.Add(NetworkHubFields.EditToggle(
                    "Sensitive",
                    parameter.Sensitive,
                    value => Commit(collection, id, kind, current, index, p => p.Sensitive = value),
                    "Redacted in diagnostics and request history, and never stored with a default value."));

                var defaultValue = NetworkHubFields.EditText(
                    "Default",
                    parameter.DefaultValue,
                    value => Commit(collection, id, kind, current, index, p => p.DefaultValue = value),
                    parameter.Sensitive
                        ? "A sensitive parameter never carries a default, so this is disabled."
                        : "Used by the request console when the caller supplies nothing.");

                defaultValue.SetEnabled(!parameter.Sensitive);
                group.Add(defaultValue);

                group.Add(NetworkHubFields.EditText(
                    "Description",
                    parameter.Description,
                    value => Commit(collection, id, kind, current, index, p => p.Description = value)));

                body.Add(group);
            }

            body.Add(NetworkHubUi.Actions(MolcaButtons.Mini($"Add {kind} parameter", () =>
            {
                var next = Snapshot(parameters);
                next.Add(new NetworkEndpointImport.Parameter
                {
                    Name = NetworkIds.MakeUnique(
                        "parameter", candidate => ContainsParameter(next, candidate)),
                    Description = string.Empty,
                    DefaultValue = string.Empty,
                });

                _session.Apply(_session.Editing.SetEndpointParameters(collection, id, kind, next));
            })));
        }

        /// <summary>Applies one change to one parameter and commits the whole list.</summary>
        private void Commit(
            NetworkEndpointCollection collection,
            string endpointId,
            NetworkCatalogEditingService.EndpointParameterKind kind,
            List<NetworkEndpointImport.Parameter> current,
            int index,
            Action<NetworkEndpointImport.Parameter> change)
        {
            var next = new List<NetworkEndpointImport.Parameter>();
            for (int i = 0; i < current.Count; i++)
            {
                var source = current[i];
                var copy = new NetworkEndpointImport.Parameter
                {
                    Name = source.Name,
                    Required = source.Required,
                    Description = source.Description,
                    Sensitive = source.Sensitive,
                    DefaultValue = source.DefaultValue,
                };

                if (i == index) change(copy);
                next.Add(copy);
            }

            _session.Apply(_session.Editing.SetEndpointParameters(collection, endpointId, kind, next));
        }

        /// <summary>Copies a serialized parameter list into the value objects the editing service takes.</summary>
        private static List<NetworkEndpointImport.Parameter> Snapshot(
            IReadOnlyList<NetworkParameterDefinition> parameters)
        {
            var copy = new List<NetworkEndpointImport.Parameter>();
            if (parameters == null) return copy;

            foreach (var parameter in parameters)
            {
                if (parameter == null) continue;

                copy.Add(new NetworkEndpointImport.Parameter
                {
                    Name = parameter.Name,
                    Required = parameter.Required,
                    Description = parameter.Description,
                    Sensitive = parameter.Sensitive,
                    DefaultValue = parameter.DefaultValue,
                });
            }
            return copy;
        }

        private static bool ContainsParameter(
            List<NetworkEndpointImport.Parameter> parameters,
            string name)
        {
            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private VisualElement BuildBodyAndResponse(
            NetworkEndpointDefinition endpoint,
            NetworkEndpointCollection collection)
        {
            string id = endpoint.Id;
            var editing = _session.Editing;
            var card = NetworkHubUi.Card("Body and response");

            card.Body.Add(NetworkHubFields.EditEnum(
                "Request body",
                endpoint.BodyType,
                value => _session.Apply(editing.SetEndpointBody(
                    collection, id, value, endpoint.RequestBodyExample, endpoint.ExpectedResponseType,
                    endpoint.ResponseTypeName))));

            card.Body.Add(NetworkHubFields.EditEnum(
                "Expected response",
                endpoint.ExpectedResponseType,
                value => _session.Apply(editing.SetEndpointBody(
                    collection, id, endpoint.BodyType, endpoint.RequestBodyExample, value,
                    endpoint.ResponseTypeName))));

            card.Body.Add(NetworkHubFields.EditChoice(
                "Response type",
                endpoint.ResponseTypeName,
                NetworkHubChoices.ResponseTypes(),
                value => _session.Apply(editing.SetEndpointBody(
                    collection, id, endpoint.BodyType, endpoint.RequestBodyExample,
                    endpoint.ExpectedResponseType, value)),
                NetworkHubFields.NoneLabel,
                new NetworkHubFields.ChoiceCreation(
                    "New type…",
                    "Name a response type",
                    "The type is not compiled yet, so nothing can confirm this name. It is stored exactly "
                    + "as written and shown as not found until a matching type exists.",
                    "Type name",
                    IsPlausibleTypeName),
                "The response model's type name, for generated call sites. Offered from the player "
                + "assemblies — an editor-only or test type could never be deserialized at runtime."));

            card.Body.Add(NetworkHubUi.Heading("Example body"));
            card.Body.Add(NetworkHubFields.EditTextArea(
                "Example",
                endpoint.RequestBodyExample,
                value => _session.Apply(editing.SetEndpointBody(
                    collection, id, endpoint.BodyType, value, endpoint.ExpectedResponseType,
                    endpoint.ResponseTypeName))));

            card.Body.Add(NetworkHubUi.Note(
                "An authoring example. Never real credentials or customer data — this asset is committed."));

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

            // A route needs a service, and this endpoint may not have one yet — its own Service is unset and
            // its collection declares no default. "Cannot resolve, here is what is missing" is the correct
            // preview for that state; building the key anyway throws, and the exception unwinds the whole
            // detail panel, taking the two fields that set a service down with it.
            if (string.IsNullOrEmpty(serviceId))
            {
                card.SetStatus(MolcaStatusKind.Warning, "No service");
                card.Body.Add(NetworkHubUi.Note(
                    "This endpoint names no service and its collection sets no default, so there is no "
                    + "origin to resolve against. Set either one below: the endpoint's own Service, or the "
                    + "collection's Default service that it inherits."));
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

            _session.Apply(_session.Editing.CreateEndpointCollection(id, id));
        }

        private void AddEndpoint(NetworkEndpointCollection collection)
        {
            var index = new NetworkCatalogIndex(_session.Catalog);
            string id = NetworkIds.MakeUnique(
                "endpoint", candidate => index.Endpoints.ContainsKey(candidate));

            var result = _session.Editing.CreateHttpEndpoint(
                collection, id, collection.ServiceId, HttpMethod.GET, string.Empty);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Endpoints, result.ResultId);

            _session.Apply(result);
        }
    }
}
