using System;
using Molca.Editor.Networking.OpenApi;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The OpenAPI import surface: pick a spec, review the diff, apply it.
    /// </summary>
    /// <remarks>
    /// A modal window rather than a pane in the Endpoints view, because import is a review step with a
    /// decision at the end. Inlining it would put a button next to a wall of diff text that a user could
    /// scroll past, and "apply 200 endpoint changes" is not a thing to click by accident.
    /// <para>
    /// The window holds no import logic. It parses through <see cref="NetworkOpenApiImportService.TryLoad"/>,
    /// diffs through <c>Plan</c>, and writes through <c>Apply</c> — the same three calls the MCP tool makes,
    /// so a diff reviewed here and a diff reviewed from automation cannot disagree.
    /// </para>
    /// </remarks>
    internal sealed class NetworkOpenApiImportWindow : EditorWindow
    {
        private const float WindowWidth = 720f;
        private const float WindowHeight = 560f;

        private NetworkCatalog _catalog;
        private NetworkEndpointCollection _collection;
        private Action _onApplied;

        private string _specPath = string.Empty;
        private string _serviceId = string.Empty;
        private string _idPrefix = string.Empty;

        private OpenApiDocument _document;
        private OpenApiImportPlan _plan;
        private string _error = string.Empty;

        private VisualElement _body;

        /// <summary>
        /// Opens the import window for one collection.
        /// </summary>
        /// <param name="catalog">The catalog owning the collection.</param>
        /// <param name="collection">The collection to import into.</param>
        /// <param name="onApplied">Invoked after a successful apply, so the workspace can reload.</param>
        internal static void Open(
            NetworkCatalog catalog, NetworkEndpointCollection collection, Action onApplied)
        {
            var window = CreateInstance<NetworkOpenApiImportWindow>();
            window.titleContent = new GUIContent("Import OpenAPI");
            window._catalog = catalog;
            window._collection = collection;
            window._serviceId = collection != null ? collection.ServiceId : string.Empty;
            window._onApplied = onApplied;

            window.minSize = new Vector2(WindowWidth, WindowHeight);
            window.ShowUtility();
        }

        private void CreateGUI()
        {
            MolcaEditorUi.Apply(rootVisualElement);
            rootVisualElement.style.paddingLeft = 8;
            rootVisualElement.style.paddingRight = 8;
            rootVisualElement.style.paddingTop = 8;

            _body = new ScrollView();
            _body.style.flexGrow = 1;
            rootVisualElement.Add(_body);

            Rebuild();
        }

        private void Rebuild()
        {
            _body.Clear();

            _body.Add(BuildSourceCard());

            if (!string.IsNullOrEmpty(_error))
                _body.Add(BuildErrorCard());

            if (_plan != null)
                _body.Add(BuildDiffCard());
        }

        private VisualElement BuildSourceCard()
        {
            var card = NetworkHubUi.Card(
                "Specification",
                _collection != null ? $"Into '{_collection.DisplayName}'" : "No collection");

            var path = new TextField("Spec file") { value = _specPath };
            path.tooltip = "A JSON OpenAPI 3.x or Swagger 2.0 document. Convert YAML first.";
            path.RegisterCallback<BlurEvent>(_ => _specPath = path.value);
            card.Body.Add(path);

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Browse…", Browse),
                MolcaButtons.Mini("Preview", Preview)));

            var service = new TextField("Service") { value = _serviceId };
            service.tooltip =
                "The catalog service imported endpoints belong to. It is not read from the spec — the " +
                "spec has no idea how this project's services are organized.";
            service.RegisterCallback<BlurEvent>(_ => _serviceId = service.value);
            card.Body.Add(service);

            var prefix = new TextField("ID prefix") { value = _idPrefix };
            prefix.tooltip =
                "Optional prefix for generated endpoint IDs. Endpoint IDs are unique catalog-wide, so a " +
                "prefix keeps two specs that both declare 'getUser' apart.";
            prefix.RegisterCallback<BlurEvent>(_ => _idPrefix = prefix.value);
            card.Body.Add(prefix);

            return card;
        }

        private VisualElement BuildErrorCard()
        {
            var card = NetworkHubUi.Card("Could not read the spec", null, MolcaStatusKind.Error, "Failed");
            card.Body.Add(NetworkHubUi.Note(_error));
            return card;
        }

        private VisualElement BuildDiffCard()
        {
            var status = _plan.ConflictCount > 0 ? MolcaStatusKind.Warning
                : _plan.HasWork ? MolcaStatusKind.Ok
                : MolcaStatusKind.Idle;

            var card = NetworkHubUi.Card(
                "Preview",
                _plan.Summarize(),
                status,
                _plan.HasWork ? "Ready to apply" : "Nothing to apply");

            card.Body.Add(NetworkHubUi.Field("Operations", _document.Operations.Count.ToString()));
            card.Body.Add(NetworkHubUi.Field("Add", _plan.AddCount.ToString()));
            card.Body.Add(NetworkHubUi.Field("Update", _plan.UpdateCount.ToString()));
            card.Body.Add(NetworkHubUi.Field("Unchanged", _plan.UnchangedCount.ToString()));
            card.Body.Add(NetworkHubUi.Field("Conflicts", _plan.ConflictCount.ToString(),
                "An endpoint authored by hand already holds the ID. Import never overwrites one."));

            if (_document.Servers.Count > 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "The spec declares server URLs. They are shown in the diff but not applied — binding a " +
                    "service to a URL from a document is a decision the catalog makes explicit."));
            }

            var diff = new TextField { value = _plan.Describe(), multiline = true, isReadOnly = true };
            diff.AddToClassList("molca-network__code");
            diff.style.minHeight = 220;
            card.Body.Add(diff);

            var apply = MolcaButtons.Primary(
                _plan.UpdateCount > 0 ? "Apply…" : "Apply", Apply);
            apply.SetEnabled(_plan.HasWork);

            card.Body.Add(NetworkHubUi.Actions(
                apply,
                MolcaButtons.Mini("Copy diff", () => EditorGUIUtility.systemCopyBuffer = _plan.Describe()),
                MolcaButtons.Mini("Close", Close)));

            return card;
        }

        private void Browse()
        {
            string picked = EditorUtility.OpenFilePanel("Select an OpenAPI document", string.Empty, "json");
            if (string.IsNullOrEmpty(picked)) return;

            _specPath = picked;
            Preview();
        }

        private void Preview()
        {
            _plan = null;
            _document = null;
            _error = string.Empty;

            if (_collection == null)
            {
                _error = "No endpoint collection was supplied.";
                Rebuild();
                return;
            }

            using var activity = NetworkActivityTracker.Begin(
                "openapi-import", "Network", "Parsing OpenAPI document");

            if (!NetworkOpenApiImportService.TryLoad(_specPath, out _document, out _error))
            {
                Rebuild();
                return;
            }

            _plan = NetworkOpenApiImportService.Plan(_document, _collection, _serviceId, _idPrefix);
            Rebuild();
        }

        private void Apply()
        {
            if (_plan == null || !_plan.HasWork) return;

            // Rewriting an endpoint an author may have adjusted is the part worth confirming; adding new
            // ones is not.
            if (_plan.UpdateCount > 0 &&
                !EditorUtility.DisplayDialog(
                    "Apply OpenAPI import?",
                    $"{_plan.Summarize()}\n\n" +
                    $"{_plan.UpdateCount} existing imported endpoint(s) will be rewritten from the spec. " +
                    "Their policy profile and idempotency settings are kept; method, path, parameters, " +
                    "body, and description are replaced.\n\nThis is one Undo step.",
                    "Apply", "Cancel"))
            {
                return;
            }

            using var activity = NetworkActivityTracker.Begin(
                "openapi-import", "Network", $"Importing {_plan.AddCount + _plan.UpdateCount} endpoint(s)");

            var result = NetworkOpenApiImportService.Apply(_plan, _catalog);

            if (!result.Success)
            {
                _error = string.Join("\n", result.Failures);
                Rebuild();
                return;
            }

            _onApplied?.Invoke();

            // Re-plan rather than close: the diff now reads all-unchanged, which is the proof that the
            // import landed and that a second apply would be a no-op.
            _plan = NetworkOpenApiImportService.Plan(_document, _collection, _serviceId, _idPrefix);
            Rebuild();
        }
    }
}
