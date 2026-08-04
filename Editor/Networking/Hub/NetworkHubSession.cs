using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Migration;
using Molca.Editor.Networking.RequestConsole;
using Molca.Editor.Networking.Validation;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The Network workspace's state: which catalog it is editing, the current validation report, the
    /// authoring preview environment, and the rail/selection state each view remembers.
    /// </summary>
    /// <remarks>
    /// Owned by <see cref="NetworkHubView"/> — an instance, not a static controller (plan §8.1). Only
    /// navigation and UI state is persisted, through project-scoped <see cref="MolcaEditorPrefs"/>;
    /// everything about the catalog is re-read from the asset on <see cref="Reload"/>.
    /// <para>
    /// The preview environment affects effective-value previews only. It is <em>not</em> the runtime
    /// environment selection and never writes to the catalog, so looking at how a route resolves in
    /// production cannot change what a build does.
    /// </para>
    /// </remarks>
    internal sealed class NetworkHubSession : IDisposable
    {
        private const string PrefViewPrefix = "network.hub.view";
        private const string PrefSelectionPrefix = "network.hub.selection.";
        private const string PrefPreviewEnvironment = "network.hub.previewEnvironment";

        private NetworkValidationReport _validation;
        private LegacyMigrationPlan _legacyPlan;
        private string _previewEnvironmentId;
        private NetworkConsoleRunner _console;
        private NetworkConsoleRequest _draft;

        /// <summary>Raised when the catalog, its validation, or the preview environment changed.</summary>
        public event Action Changed;

        /// <summary>Raised when something asks the workspace to navigate.</summary>
        public event Action<NetworkHubNavigationTarget> NavigationRequested;

        /// <summary>The catalog being authored, or <c>null</c> when the project has none.</summary>
        public NetworkCatalog Catalog { get; private set; }

        /// <summary>Whether a catalog exists to author.</summary>
        public bool HasCatalog => Catalog != null;

        /// <summary>Whether the catalog is registered on <c>GlobalSettings</c> and therefore loaded at runtime.</summary>
        public bool IsCatalogRegistered { get; private set; }

        /// <summary>The one write path into the catalog, or <c>null</c> when there is none.</summary>
        public NetworkCatalogEditingService Editing { get; private set; }

        /// <summary>Effective-configuration previews, or <c>null</c> when there is no catalog.</summary>
        public NetworkEffectiveConfigurationService Effective { get; private set; }

        /// <summary>The current validation report. Never <c>null</c> after <see cref="Reload"/>.</summary>
        public NetworkValidationReport Validation => _validation;

        /// <summary>
        /// The request console's executor, created on first use.
        /// </summary>
        /// <remarks>
        /// Owned by the session rather than by the console view, because the view is rebuilt on every
        /// session change — a runner on the view would drop an in-flight send the moment another pane
        /// caused a reload.
        /// </remarks>
        public NetworkConsoleRunner Console
        {
            get
            {
                if (_console == null)
                {
                    _console = new NetworkConsoleRunner();
                    _console.Rebuild(Catalog);
                }
                return _console;
            }
        }

        /// <summary>
        /// The draft the request console is editing. Persists for the life of the workspace.
        /// </summary>
        /// <remarks>
        /// Headers and body live here and nowhere else — never in preferences, never in an asset. A
        /// domain reload discards them, which is the correct trade for a field a developer may have
        /// pasted a bearer token into.
        /// </remarks>
        public NetworkConsoleRequest Draft => _draft ??= new NetworkConsoleRequest
        {
            EnvironmentId = PreviewEnvironmentId,
        };

        // When set, Reload keeps this catalog instead of re-locating one. Tests use it to author an
        // in-memory catalog without touching the AssetDatabase; production always locates by type.
        private readonly NetworkCatalog _pinnedCatalog;

        /// <summary>Creates a session and loads the project's catalog.</summary>
        public NetworkHubSession() => Reload();

        /// <summary>
        /// Creates a session over a specific catalog rather than the project's.
        /// </summary>
        /// <param name="catalog">The catalog to author.</param>
        /// <remarks>
        /// Internal: it exists so edit-mode tests can build the workspace's views over a constructed
        /// catalog. The Hub always uses the parameterless constructor, which locates the catalog by type.
        /// </remarks>
        internal NetworkHubSession(NetworkCatalog catalog)
        {
            _pinnedCatalog = catalog;
            Reload();
        }

        /// <summary>
        /// Re-locates the catalog, rebuilds the editing and preview services, and re-validates.
        /// </summary>
        /// <remarks>
        /// Called on attach, after every authoring action, and from the toolbar's Validate. Cheap enough
        /// to run eagerly: validation is a pure function of the catalog and touches no assets beyond the
        /// endpoint collections it already references.
        /// </remarks>
        public void Reload()
        {
            Catalog = _pinnedCatalog != null ? _pinnedCatalog : NetworkCatalogLocator.FindCatalog();
            IsCatalogRegistered = Catalog != null && NetworkCatalogLocator.IsRegistered(Catalog);

            Editing = Catalog != null ? new NetworkCatalogEditingService(Catalog) : null;
            Effective = Catalog != null ? new NetworkEffectiveConfigurationService(Catalog) : null;
            _validation = NetworkCatalogValidator.Validate(Catalog);

            // The legacy scan reads every asset in the project, so it is recomputed on demand rather than
            // on every reload. Dropping it here is what makes a re-scan reflect an applied migration.
            _legacyPlan = null;

            EnsurePreviewEnvironmentExists();

            // Re-snapshot the console's client so a send made now resolves against the catalog as it is,
            // not as it was when the workspace opened.
            _console?.Rebuild(Catalog);

            Changed?.Invoke();
        }

        /// <summary>
        /// Cancels and releases everything the session owns.
        /// </summary>
        /// <remarks>
        /// Called when the workspace view detaches — on cache eviction or a tab-set rebuild — so an
        /// in-flight console send does not outlive the surface that started it (plan §5 exit criteria).
        /// </remarks>
        public void Dispose()
        {
            _console?.Dispose();
            _console = null;
        }

        /// <summary>
        /// The legacy scan and migration plan, computed on first use and cached until <see cref="Reload"/>.
        /// </summary>
        /// <returns>The plan; never <c>null</c>.</returns>
        /// <remarks>
        /// Lazy because scanning walks the whole <c>AssetDatabase</c>, and most visits to this workspace
        /// never ask about legacy state. Overview and the empty state are the only callers.
        /// </remarks>
        public LegacyMigrationPlan LegacyPlan() => _legacyPlan ??= LegacyMigrationExecutor.DryRun();

        /// <summary>The rail view currently selected. Persisted per project.</summary>
        public string SelectedView
        {
            get
            {
                string stored = MolcaEditorPrefs.GetString(PrefViewPrefix, NetworkHubViews.Overview);
                return NetworkHubViews.IsKnown(stored) ? stored : NetworkHubViews.Overview;
            }
            set
            {
                if (NetworkHubViews.IsKnown(value))
                    MolcaEditorPrefs.SetString(PrefViewPrefix, value);
            }
        }

        /// <summary>The entity selected within one view. Persisted per project, per view.</summary>
        /// <param name="viewId">The view whose selection to read.</param>
        public string SelectionFor(string viewId) =>
            MolcaEditorPrefs.GetString(PrefSelectionPrefix + viewId, string.Empty);

        /// <summary>Records the entity selected within one view.</summary>
        /// <param name="viewId">The view whose selection to write.</param>
        /// <param name="entityId">The selected entity id, or <c>null</c> to clear.</param>
        public void SetSelection(string viewId, string entityId) =>
            MolcaEditorPrefs.SetString(PrefSelectionPrefix + viewId, entityId ?? string.Empty);

        /// <summary>
        /// The environment effective-value previews resolve under. Never the runtime selection.
        /// </summary>
        public string PreviewEnvironmentId
        {
            get => _previewEnvironmentId ?? string.Empty;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_previewEnvironmentId, next, StringComparison.Ordinal))
                    return;

                _previewEnvironmentId = next;
                MolcaEditorPrefs.SetString(PrefPreviewEnvironment, next);
                Changed?.Invoke();
            }
        }

        /// <summary>Asks the workspace to navigate somewhere.</summary>
        /// <param name="target">Where to go.</param>
        public void Navigate(NetworkHubNavigationTarget target) => NavigationRequested?.Invoke(target);

        /// <summary>
        /// Reports an authoring outcome and reloads so every view reflects the write.
        /// </summary>
        /// <param name="result">The outcome returned by the editing service.</param>
        /// <remarks>
        /// Reloads on failure too, which is the point: a refused edit leaves the control showing a value
        /// the catalog does not hold, and the reload snaps it back to the stored one. Without that, a
        /// rejected origin would sit in the field looking saved.
        /// </remarks>
        public void Apply(NetworkAuthoringResult result)
        {
            if (result.Success)
                UnityEngine.Debug.Log($"[Network] {result.Message}");
            else
                UnityEngine.Debug.LogWarning($"[Network] {result.Message}");

            Reload();
        }

        /// <summary>The catalog's environment IDs, for a reference dropdown.</summary>
        public List<string> EnvironmentIds() => IdsOf(Catalog?.Environments, e => e?.Id);

        /// <summary>The catalog's service IDs, for a reference dropdown.</summary>
        public List<string> ServiceIds() => IdsOf(Catalog?.Services, s => s?.Id);

        /// <summary>The catalog's policy profile IDs, for a reference dropdown.</summary>
        public List<string> PolicyProfileIds() => IdsOf(Catalog?.PolicyProfiles, p => p?.Id);

        /// <summary>The catalog's credential profile IDs, for a reference dropdown.</summary>
        public List<string> CredentialProfileIds() => IdsOf(Catalog?.CredentialProfiles, c => c?.Id);

        /// <summary>Every endpoint ID in the catalog, for a reference dropdown.</summary>
        /// <remarks>
        /// Flat across collections because endpoint IDs are unique catalog-wide, which is what lets a
        /// health-check reference or a deep link name one by ID alone.
        /// </remarks>
        public List<string> EndpointIds()
        {
            var ids = new List<string>();
            if (Catalog == null) return ids;

            foreach (var collection in Catalog.EndpointCollections)
            {
                if (collection == null) continue;

                foreach (var endpoint in collection.Endpoints)
                {
                    if (endpoint != null && !string.IsNullOrEmpty(endpoint.Id))
                        ids.Add(endpoint.Id);
                }
            }
            return ids;
        }

        private static List<string> IdsOf<T>(IReadOnlyList<T> source, Func<T, string> idOf)
        {
            var ids = new List<string>();
            if (source == null) return ids;

            foreach (var item in source)
            {
                string id = idOf(item);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Settles the preview environment on one that exists.
        /// </summary>
        /// <remarks>
        /// A stored preview naming a deleted environment would silently make every preview report
        /// "environment does not exist", which reads as a catalog problem rather than a stale preference.
        /// </remarks>
        private void EnsurePreviewEnvironmentExists()
        {
            _previewEnvironmentId ??= MolcaEditorPrefs.GetString(PrefPreviewEnvironment, string.Empty);

            if (Catalog == null)
                return;

            if (!string.IsNullOrEmpty(_previewEnvironmentId) &&
                Catalog.FindEnvironment(_previewEnvironmentId) != null)
            {
                return;
            }

            string fallback = !string.IsNullOrEmpty(Catalog.DefaultEnvironmentId) &&
                              Catalog.FindEnvironment(Catalog.DefaultEnvironmentId) != null
                ? Catalog.DefaultEnvironmentId
                : Catalog.Environments.Count > 0 ? Catalog.Environments[0]?.Id : null;

            _previewEnvironmentId = fallback ?? string.Empty;
            MolcaEditorPrefs.SetString(PrefPreviewEnvironment, _previewEnvironmentId);
        }
    }
}
