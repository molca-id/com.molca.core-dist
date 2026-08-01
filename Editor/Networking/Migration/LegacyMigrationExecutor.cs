using System;
using System.Collections.Generic;
using UnityEditor;
using Molca.Networking.Configuration;
using Molca.Networking.Http;
using Molca.Editor.Networking.Authoring;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>What applying a migration plan actually did.</summary>
    /// <remarks>
    /// Structured rather than exception-based, matching <see cref="NetworkAuthoringResult"/>: the Hub, an
    /// MCP tool, and a test all report the same outcome the same way.
    /// </remarks>
    public sealed class LegacyMigrationResult
    {
        private readonly List<string> _applied;
        private readonly List<string> _failures;

        /// <summary>Whether every planned step succeeded.</summary>
        public bool Success => _failures.Count == 0;

        /// <summary>Whether the run stopped before finishing because the caller cancelled it.</summary>
        public bool Cancelled { get; }

        /// <summary>The catalog that was written to.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>One message per step that was applied, in order.</summary>
        public IReadOnlyList<string> Applied => _applied;

        /// <summary>One message per step that failed. Empty on success.</summary>
        public IReadOnlyList<string> Failures => _failures;

        /// <summary>Creates a result.</summary>
        /// <param name="catalog">The catalog written to.</param>
        /// <param name="applied">Messages for applied steps.</param>
        /// <param name="failures">Messages for failed steps.</param>
        /// <param name="cancelled">Whether the run was cancelled before finishing.</param>
        public LegacyMigrationResult(
            NetworkCatalog catalog,
            IEnumerable<string> applied,
            IEnumerable<string> failures,
            bool cancelled)
        {
            Catalog = catalog;
            _applied = applied == null ? new List<string>() : new List<string>(applied);
            _failures = failures == null ? new List<string>() : new List<string>(failures);
            Cancelled = cancelled;
        }

        /// <summary>A one-line summary suitable for a log line or a Hub toast.</summary>
        public string Summarize()
        {
            string state = Cancelled ? "cancelled" : Success ? "completed" : "completed with failures";
            return $"Legacy migration {state}: {_applied.Count} step(s) applied, {_failures.Count} failed.";
        }

        /// <inheritdoc />
        public override string ToString() => Summarize();
    }

    /// <summary>
    /// Applies a <see cref="LegacyMigrationPlan"/> to a catalog.
    /// </summary>
    /// <remarks>
    /// Every write goes through <see cref="NetworkCatalogEditingService"/> — the one write path — so Undo,
    /// dirty tracking, and validation behave identically whether a change came from migration or from the
    /// Hub. The whole run collapses into a single Undo step, so a half-applied migration is not a state
    /// the project can be left in by accident.
    /// <para>
    /// Safe to cancel and safe to re-run. Cancelling stops after the current step and keeps what already
    /// landed; re-running recomputes the plan from a fresh scan, which yields only the steps that remain.
    /// Endpoint provenance is what makes that work: an endpoint recording a request asset's GUID as its
    /// source is never migrated a second time.
    /// </para>
    /// <para>
    /// Nothing legacy is deleted or edited. Request assets, providers, and <c>HttpModule</c> are read
    /// only.
    /// </para>
    /// </remarks>
    public static class LegacyMigrationExecutor
    {
        /// <summary>
        /// Scans, plans, and previews without writing anything.
        /// </summary>
        /// <returns>The plan; call <see cref="Apply"/> to act on it.</returns>
        public static LegacyMigrationPlan DryRun() => LegacyMigrationPlan.Compute(LegacyNetworkScanner.Scan());

        /// <summary>
        /// Applies a plan, creating the catalog if the project has none.
        /// </summary>
        /// <param name="plan">The plan to apply.</param>
        /// <param name="shouldCancel">
        /// Polled before each step; returning <c>true</c> stops the run and keeps what already landed.
        /// May be <c>null</c>.
        /// </param>
        /// <returns>The result. Never <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <c>null</c>.</exception>
        public static LegacyMigrationResult Apply(LegacyMigrationPlan plan, Func<bool> shouldCancel = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var catalog = plan.Report.ExistingCatalog ?? NetworkCatalogLocator.GetOrCreateCatalog();
            return ApplyTo(catalog, plan, shouldCancel);
        }

        /// <summary>
        /// Applies a plan to a specific catalog.
        /// </summary>
        /// <param name="catalog">The catalog to write to.</param>
        /// <param name="plan">The plan to apply.</param>
        /// <param name="shouldCancel">Polled before each step; <c>true</c> stops the run.</param>
        /// <returns>The result. Never <c>null</c>.</returns>
        /// <remarks>
        /// The catalog is a parameter rather than always being located, so tests can migrate into an
        /// in-memory instance and the Hub can migrate into a catalog the author picked.
        /// </remarks>
        public static LegacyMigrationResult ApplyTo(
            NetworkCatalog catalog,
            LegacyMigrationPlan plan,
            Func<bool> shouldCancel = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var editing = new NetworkCatalogEditingService(catalog);
            var applied = new List<string>();
            var failures = new List<string>();
            bool cancelled = false;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Migrate Legacy Networking");

            // Collections are created mid-run, so endpoint steps resolve theirs by ID from the catalog as
            // it stands rather than from a reference captured before the run started.
            NetworkEndpointCollection collection = FindCollection(catalog, LegacyMigrationPlan.LegacyCollectionId);

            foreach (var step in plan.Steps)
            {
                if (shouldCancel != null && shouldCancel())
                {
                    cancelled = true;
                    break;
                }

                var outcome = ApplyStep(editing, catalog, plan, step, ref collection);

                if (outcome.Success)
                    applied.Add(outcome.Message);
                else
                    failures.Add($"{step.Description} — {outcome.Message}");
            }

            if (!cancelled && !string.IsNullOrEmpty(plan.Report.HttpModuleGuid))
                editing.RecordLegacySource(plan.Report.HttpModuleGuid);

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            return new LegacyMigrationResult(catalog, applied, failures, cancelled);
        }

        private static NetworkAuthoringResult ApplyStep(
            NetworkCatalogEditingService editing,
            NetworkCatalog catalog,
            LegacyMigrationPlan plan,
            LegacyMigrationStep step,
            ref NetworkEndpointCollection collection)
        {
            switch (step.Kind)
            {
                case LegacyMigrationStepKind.CreateEnvironment:
                    return editing.CreateEnvironment(
                        step.TargetId,
                        step.TargetId,
                        NetworkEnvironmentClassification.Development,
                        makeDefault: true);

                case LegacyMigrationStepKind.CreatePolicyProfile:
                    return ApplyPolicyProfile(editing, plan, step);

                case LegacyMigrationStepKind.CreateService:
                    return editing.CreateService(step.TargetId, step.TargetId, step.Protocols);

                case LegacyMigrationStepKind.BindService:
                    return editing.SetHttpBinding(step.ServiceId, step.EnvironmentId, step.Origin);

                case LegacyMigrationStepKind.CreateCredentialProfile:
                    // Left unscoped on purpose: an unscoped profile denies every host, so a migrated
                    // catalog cannot send a credential anywhere until the author says where it may go.
                    return editing.CreateCredentialProfile(
                        step.TargetId, step.TargetId, NetworkCredentialProviderKind.AuthManagerToken);

                case LegacyMigrationStepKind.CreateEndpointCollection:
                {
                    var result = editing.CreateEndpointCollection(
                        step.TargetId, step.TargetId, step.ServiceId);

                    if (result.Success)
                        collection = FindCollection(catalog, result.ResultId);

                    return result;
                }

                case LegacyMigrationStepKind.CreateEndpoint:
                {
                    if (collection == null)
                    {
                        return NetworkAuthoringResult.Fail(
                            $"No endpoint collection '{LegacyMigrationPlan.LegacyCollectionId}' exists to " +
                            "add the endpoint to.");
                    }

                    return editing.CreateHttpEndpoint(
                        collection,
                        step.TargetId,
                        step.ServiceId,
                        step.Method,
                        step.RelativePath,
                        NetworkEndpointSource.LegacyMigration,
                        step.SourceGuid);
                }

                default:
                    return NetworkAuthoringResult.Fail($"Unhandled migration step '{step.Kind}'.");
            }
        }

        /// <summary>
        /// Creates the legacy policy profile and copies the module's timeout, retry, and concurrency onto
        /// it.
        /// </summary>
        /// <remarks>
        /// Copying these matters even though the routed pipeline is opt-in: the moment a project switches
        /// legacy sends onto it, a profile carrying the library defaults instead of the authored ones
        /// would silently change every timeout and retry count.
        /// </remarks>
        private static NetworkAuthoringResult ApplyPolicyProfile(
            NetworkCatalogEditingService editing,
            LegacyMigrationPlan plan,
            LegacyMigrationStep step)
        {
            var created = editing.CreatePolicyProfile(step.TargetId, step.TargetId);
            if (!created.Success)
                return created;

            string profileId = created.ResultId;
            var module = ReadHttpModule(plan);

            if (module == null)
            {
                // Nothing authored to copy; the profile keeps the library defaults, which is correct.
                return created;
            }

            int timeout = Math.Max(1, module.DefaultTimeout);

            // The legacy timeout governed a single transport attempt. The routed overall budget also has
            // to cover queueing and retry backoff, so it is the attempt budget times the worst-case
            // attempt count — anything less would time out sends the legacy client completed.
            int attempts = module.EnableRetry ? module.MaxRetries + 1 : 1;
            float overall = timeout * attempts + module.RetryBaseDelaySeconds * attempts;

            editing.SetPolicyTimeouts(profileId, overall, timeout);
            editing.SetPolicyRetry(
                profileId, module.EnableRetry, module.MaxRetries, module.RetryBaseDelaySeconds);
            editing.SetPolicyConcurrency(profileId, module.MaxConcurrentRequests);
            editing.SetDefaultPolicyProfile(profileId);

            return NetworkAuthoringResult.Ok(
                $"Created policy profile '{profileId}' with {timeout}s per attempt, {overall}s overall, " +
                $"retry {(module.EnableRetry ? $"up to {module.MaxRetries}x" : "off")}, " +
                $"{module.MaxConcurrentRequests} concurrent per route, and made it the catalog default.",
                profileId);
        }

        /// <summary>
        /// The <c>HttpModule</c> the scan read, resolved from the report's base-URL item.
        /// </summary>
        /// <returns>The module, or <c>null</c> when the project has none.</returns>
        private static HttpModule ReadHttpModule(LegacyMigrationPlan plan)
        {
            foreach (var item in plan.Report.Items)
            {
                if (item.Kind == LegacyNetworkItemKind.GlobalBaseUrl && item.Asset is HttpModule module)
                    return module;
            }
            return null;
        }

        private static NetworkEndpointCollection FindCollection(NetworkCatalog catalog, string collectionId)
        {
            if (catalog?.EndpointCollections == null || string.IsNullOrEmpty(collectionId))
                return null;

            foreach (var collection in catalog.EndpointCollections)
            {
                if (collection != null &&
                    string.Equals(collection.CollectionId, collectionId, StringComparison.Ordinal))
                {
                    return collection;
                }
            }
            return null;
        }
    }
}
