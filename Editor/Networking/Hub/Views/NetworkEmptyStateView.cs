using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Migration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// What the workspace shows when the project has no catalog: three actions and a read-only account of
    /// what already exists.
    /// </summary>
    /// <remarks>
    /// The scan is read-only and produces a report before anything changes (plan §7.14). Nothing here
    /// creates an asset as a side effect of looking — opening the workspace on an unconfigured project
    /// must leave it unconfigured.
    /// </remarks>
    internal sealed class NetworkEmptyStateView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly VisualElement _scanResult;

        /// <summary>Builds the empty state.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkEmptyStateView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__empty");
            style.flexGrow = 1;

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            Add(scroll);

            var card = NetworkHubUi.Card(
                "No network catalog",
                "Requests still work. A catalog is what lets them target more than one backend safely.",
                MolcaStatusKind.Idle,
                "Not configured");

            card.Body.Add(NetworkHubUi.Note(
                "A catalog replaces the single global base URL with routes — an environment and a service " +
                "per request — so one session can reach several backends, and a credential is scoped to " +
                "the hosts it belongs to instead of travelling with every request."));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Primary("Create Network Catalog", CreateCatalog),
                MolcaButtons.Mini("Scan Legacy Networking", RunScan),
                MolcaButtons.Mini("Open Networking Guide",
                    () => NetworkHubUi.OpenDoc("NETWORKING_CATALOG"))));

            scroll.Add(card);

            _scanResult = new VisualElement();
            scroll.Add(_scanResult);
        }

        /// <summary>
        /// Creates and registers a catalog, then reloads the workspace onto it.
        /// </summary>
        /// <remarks>
        /// Registered on <c>GlobalSettings</c> as part of the same action: an unregistered catalog is
        /// authored-but-inert, and creating one from an explicit button is a request to use it, not to
        /// have an asset sitting in the project doing nothing.
        /// </remarks>
        private void CreateCatalog()
        {
            var catalog = NetworkCatalogLocator.GetOrCreateCatalog();
            _session.Reload();

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>Runs the read-only legacy scan and shows what it found and what migration would do.</summary>
        private void RunScan()
        {
            _scanResult.Clear();

            var plan = _session.LegacyPlan();
            var report = plan.Report;

            var card = NetworkHubUi.Card(
                "Legacy networking scan",
                report.Summarize(),
                report.HasWork ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                report.HasWork ? "Migration available" : "Nothing found");

            if (!report.HasWork)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No HttpModule base URL, request assets, or data providers were found, so there is " +
                    "nothing to migrate. Create a catalog and author routes directly."));
                _scanResult.Add(card);
                return;
            }

            var found = new TextField { multiline = true, value = report.Describe() };
            found.AddToClassList("molca-network__report");
            found.isReadOnly = true;
            card.Body.Add(NetworkHubUi.Heading("What exists today"));
            card.Body.Add(found);

            var proposed = new TextField { multiline = true, value = plan.Describe() };
            proposed.AddToClassList("molca-network__report");
            proposed.isReadOnly = true;
            card.Body.Add(NetworkHubUi.Heading("What migration would do"));
            card.Body.Add(proposed);

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Primary("Apply migration", ApplyMigration),
                MolcaButtons.Mini("Open migration guide",
                    () => NetworkHubUi.OpenDoc("NETWORKING_MIGRATION"))));

            _scanResult.Add(card);
        }

        private void ApplyMigration()
        {
            var plan = _session.LegacyPlan();

            if (!EditorUtility.DisplayDialog(
                    "Apply legacy networking migration?",
                    $"{plan.Steps.Count} step(s) will create catalog entities alongside your existing " +
                    "assets.\n\nNo request asset, data provider, or HttpModule is modified or deleted, and " +
                    "the whole run is a single Undo step.",
                    "Apply", "Cancel"))
            {
                return;
            }

            var result = LegacyMigrationExecutor.Apply(plan);
            Debug.Log($"[Network] {result.Summarize()}");

            foreach (string failure in result.Failures)
                Debug.LogWarning($"[Network] Migration step failed: {failure}");

            _session.Reload();
        }
    }
}
