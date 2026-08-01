using System;
using System.Collections.Generic;
using System.IO;
using Molca.Editor.Networking.Authoring;
using Molca.Networking.Configuration;
using UnityEditor;

namespace Molca.Editor.Networking.OpenApi
{
    /// <summary>What one apply run did.</summary>
    internal sealed class OpenApiImportResult
    {
        /// <summary>Whether every planned write succeeded.</summary>
        public bool Success => Failures.Count == 0;

        /// <summary>Endpoint IDs created.</summary>
        public IReadOnlyList<string> Added { get; }

        /// <summary>Endpoint IDs rewritten.</summary>
        public IReadOnlyList<string> Updated { get; }

        /// <summary>One message per write that failed.</summary>
        public IReadOnlyList<string> Failures { get; }

        internal OpenApiImportResult(
            IReadOnlyList<string> added, IReadOnlyList<string> updated, IReadOnlyList<string> failures)
        {
            Added = added ?? Array.Empty<string>();
            Updated = updated ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }

        /// <summary>A one-line summary.</summary>
        public string Summarize() =>
            $"{Added.Count} added, {Updated.Count} updated" +
            (Failures.Count > 0 ? $", {Failures.Count} failed" : string.Empty);
    }

    /// <summary>
    /// Imports OpenAPI operations into an endpoint collection, preview first.
    /// </summary>
    /// <remarks>
    /// The optional <c>NetworkOpenApiImportService</c> of plan §8.1. Non-visual and instance-free, so the
    /// Hub, MCP, and tests all drive the same code path: <see cref="TryLoad"/> to parse,
    /// <see cref="Plan"/> to produce a reviewable diff, <see cref="Apply"/> to write it.
    /// <para>
    /// Three rules decide what import is allowed to touch, and all three exist because a spec is written
    /// by someone who does not know how this project is configured:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>It never overwrites a hand-authored endpoint.</b> An endpoint whose <c>Source</c> is not
    /// <c>OpenApi</c> is a conflict — reported and skipped. Silently replacing one would discard whatever
    /// policy, mutation class, or idempotency requirement an author had attached to it.
    /// </description></item>
    /// <item><description>
    /// <b>It never binds a service to a spec's server URL.</b> The servers are reported; binding is a
    /// separate, deliberate act, for the same reason the catalog never falls back between environments.
    /// </description></item>
    /// <item><description>
    /// <b>It never creates a credential profile.</b> A spec says an operation needs authentication; it
    /// cannot say where the secret comes from or which hosts may receive it. Import marks the affected
    /// parameters sensitive and stops there.
    /// </description></item>
    /// </list>
    /// <para>
    /// Every write goes through <see cref="NetworkCatalogEditingService"/> under one Undo group, so an
    /// import is a single Ctrl+Z.
    /// </para>
    /// </remarks>
    internal static class NetworkOpenApiImportService
    {
        /// <summary>
        /// Loads and parses a spec from disk.
        /// </summary>
        /// <param name="path">Absolute or project-relative path to a JSON spec.</param>
        /// <param name="document">The parsed document on success.</param>
        /// <param name="error">Why loading failed, or <c>null</c>.</param>
        /// <returns><c>false</c> when the file is missing or is not a spec this importer understands.</returns>
        public static bool TryLoad(string path, out OpenApiDocument document, out string error)
        {
            document = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No spec path was supplied.";
                return false;
            }

            string resolved = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));

            if (!File.Exists(resolved))
            {
                error = $"No file at '{resolved}'.";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(resolved);
            }
            catch (IOException e)
            {
                error = $"Could not read '{resolved}': {e.Message}";
                return false;
            }

            return OpenApiParser.TryParse(json, out document, out error);
        }

        /// <summary>
        /// Computes the diff between a document and a collection.
        /// </summary>
        /// <param name="document">The parsed spec.</param>
        /// <param name="collection">The collection to import into.</param>
        /// <param name="serviceId">The service imported endpoints belong to; empty inherits the collection's.</param>
        /// <param name="idPrefix">Prefix for generated endpoint IDs, or <c>null</c> for none.</param>
        /// <returns>The plan; never <c>null</c>.</returns>
        /// <remarks>
        /// Pure. It reads the collection and writes nothing, so the preview a user approves is exactly what
        /// <see cref="Apply"/> performs.
        /// </remarks>
        public static OpenApiImportPlan Plan(
            OpenApiDocument document,
            NetworkEndpointCollection collection,
            string serviceId = null,
            string idPrefix = null)
        {
            if (document == null || collection == null)
            {
                return new OpenApiImportPlan(
                    document, collection, serviceId, Array.Empty<OpenApiImportEntry>(), Array.Empty<string>());
            }

            string targetService = string.IsNullOrWhiteSpace(serviceId) ? collection.ServiceId : serviceId;

            var byOperationId = new Dictionary<string, NetworkEndpointDefinition>(StringComparer.Ordinal);
            var byId = new Dictionary<string, NetworkEndpointDefinition>(StringComparer.Ordinal);

            foreach (var endpoint in collection.Endpoints)
            {
                if (endpoint == null) continue;

                byId[endpoint.Id] = endpoint;

                if (endpoint.Source == NetworkEndpointSource.OpenApi &&
                    !string.IsNullOrEmpty(endpoint.SourceReference))
                {
                    byOperationId[endpoint.SourceReference] = endpoint;
                }
            }

            var entries = new List<OpenApiImportEntry>();
            var matchedOperationIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var operation in document.Operations)
            {
                matchedOperationIds.Add(operation.OperationId);
                entries.Add(Classify(operation, byOperationId, byId, idPrefix));
            }

            var orphans = new List<string>();
            foreach (var pair in byOperationId)
            {
                if (!matchedOperationIds.Contains(pair.Key))
                    orphans.Add($"{pair.Value.Id} (operation '{pair.Key}')");
            }
            orphans.Sort(StringComparer.Ordinal);

            return new OpenApiImportPlan(document, collection, targetService, entries, orphans);
        }

        private static OpenApiImportEntry Classify(
            OpenApiOperation operation,
            Dictionary<string, NetworkEndpointDefinition> byOperationId,
            Dictionary<string, NetworkEndpointDefinition> byId,
            string idPrefix)
        {
            // Identity is the operation ID recorded on a previous import, not the endpoint ID. An author is
            // free to rename an imported endpoint; a re-import must still recognize it.
            if (byOperationId.TryGetValue(operation.OperationId, out var existing))
            {
                string hash = operation.ContentHash();

                if (string.Equals(existing.SourceHash, hash, StringComparison.Ordinal))
                {
                    return new OpenApiImportEntry(
                        operation, OpenApiImportAction.Unchanged, existing.Id);
                }

                return new OpenApiImportEntry(
                    operation, OpenApiImportAction.Update, existing.Id,
                    "The spec changed since this endpoint was imported.",
                    DescribeChanges(operation, existing));
            }

            string candidateId = EndpointIdFor(operation, idPrefix);

            // An endpoint already holding this ID that import did not create is somebody's work.
            if (byId.TryGetValue(candidateId, out var occupant) &&
                occupant.Source != NetworkEndpointSource.OpenApi)
            {
                return new OpenApiImportEntry(
                    operation, OpenApiImportAction.Conflict, candidateId,
                    $"Endpoint '{candidateId}' already exists and was authored by hand " +
                    $"({occupant.Source}). Import will not overwrite it — rename one of the two.");
            }

            return new OpenApiImportEntry(operation, OpenApiImportAction.Add, candidateId);
        }

        /// <summary>Field-level changes an update would make, for the diff.</summary>
        private static List<string> DescribeChanges(
            OpenApiOperation operation, NetworkEndpointDefinition existing)
        {
            var changes = new List<string>();

            if (existing.Method != operation.Method)
                changes.Add($"method {existing.Method} → {operation.Method}");

            if (!string.Equals(existing.RelativePath, operation.Path, StringComparison.Ordinal))
                changes.Add($"path '{existing.RelativePath}' → '{operation.Path}'");

            int specParameters = operation.At(OpenApiParameterLocation.Path).Count +
                                 operation.At(OpenApiParameterLocation.Query).Count +
                                 operation.At(OpenApiParameterLocation.Header).Count;
            int localParameters = existing.PathParameters.Count +
                                  existing.QueryParameters.Count +
                                  existing.HeaderParameters.Count;

            if (specParameters != localParameters)
                changes.Add($"parameters {localParameters} → {specParameters}");

            if (existing.BodyType != operation.BodyType)
                changes.Add($"body {existing.BodyType} → {operation.BodyType}");

            if (changes.Count == 0)
                changes.Add("description, tags, or example");

            return changes;
        }

        /// <summary>
        /// The endpoint ID an operation imports as.
        /// </summary>
        /// <remarks>
        /// Derived from the operation ID so it is stable across re-imports and readable in a deep link.
        /// The prefix exists because endpoint IDs are unique catalog-wide: two specs can both declare
        /// <c>getUser</c>, and prefixing by service keeps them apart without a numeric suffix nobody can
        /// interpret.
        /// </remarks>
        internal static string EndpointIdFor(OpenApiOperation operation, string idPrefix)
        {
            // NetworkIds.Suggest is the same generator migration uses, so an ID minted here reads like
            // every other ID in the catalog and is already length- and charset-legal.
            string fallback = NetworkIds.Suggest($"{operation.Method}-{operation.Path}", "endpoint");
            string slug = NetworkIds.Suggest(operation.OperationId, fallback);

            return string.IsNullOrWhiteSpace(idPrefix)
                ? slug
                : NetworkIds.Suggest(idPrefix + "-" + slug, slug);
        }

        /// <summary>
        /// Applies a plan.
        /// </summary>
        /// <param name="plan">A plan from <see cref="Plan"/>.</param>
        /// <param name="catalog">The catalog owning the collection.</param>
        /// <param name="shouldCancel">Polled between operations; return <c>true</c> to stop.</param>
        /// <returns>What was written.</returns>
        /// <remarks>
        /// One Undo group for the whole import. Conflicts and unchanged entries are skipped without
        /// touching the asset — a plan that reports 40 unchanged and 1 conflict performs no writes at all.
        /// </remarks>
        public static OpenApiImportResult Apply(
            OpenApiImportPlan plan, NetworkCatalog catalog, Func<bool> shouldCancel = null)
        {
            var added = new List<string>();
            var updated = new List<string>();
            var failures = new List<string>();

            if (plan == null || catalog == null || plan.Collection == null)
            {
                failures.Add("No plan, catalog, or collection was supplied.");
                return new OpenApiImportResult(added, updated, failures);
            }

            var editing = new NetworkCatalogEditingService(catalog);

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Import OpenAPI into '{plan.Collection.DisplayName}'");

            try
            {
                foreach (var entry in plan.Entries)
                {
                    if (shouldCancel != null && shouldCancel())
                        break;

                    if (entry.Action == OpenApiImportAction.Unchanged ||
                        entry.Action == OpenApiImportAction.Conflict)
                    {
                        continue;
                    }

                    var definition = ToDefinition(entry, plan.ServiceId);

                    var result = entry.Action == OpenApiImportAction.Add
                        ? editing.CreateImportedEndpoint(plan.Collection, entry.EndpointId, definition)
                        : editing.UpdateImportedEndpoint(plan.Collection, entry.EndpointId, definition);

                    if (!result.Success)
                    {
                        failures.Add($"{entry.Operation.Method} {entry.Operation.Path}: {result.Message}");
                        continue;
                    }

                    if (entry.Action == OpenApiImportAction.Add)
                        added.Add(result.ResultId);
                    else
                        updated.Add(result.ResultId);
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            return new OpenApiImportResult(added, updated, failures);
        }

        /// <summary>Projects an operation onto the endpoint fields import owns.</summary>
        private static NetworkEndpointImport ToDefinition(OpenApiImportEntry entry, string serviceId)
        {
            var operation = entry.Operation;

            string description = operation.Deprecated
                ? "DEPRECATED in the source spec.\n\n" + operation.Description
                : operation.Description;

            return new NetworkEndpointImport
            {
                ServiceId = serviceId,
                Method = operation.Method,
                RelativePath = operation.Path,
                Description = description,
                BodyType = operation.BodyType,
                RequestBodyExample = operation.RequestBodyExample,
                Tags = new List<string>(operation.Tags),
                PathParameters = Project(operation.At(OpenApiParameterLocation.Path)),
                QueryParameters = Project(operation.At(OpenApiParameterLocation.Query)),
                HeaderParameters = Project(operation.At(OpenApiParameterLocation.Header)),
                MutationClass = MutationClassFor(operation),
                SourceReference = operation.OperationId,
                SourceHash = operation.ContentHash(),
            };
        }

        /// <summary>
        /// The mutation class an operation imports as.
        /// </summary>
        /// <remarks>
        /// From the method, and erring toward "this changes something". A spec has no field for how
        /// dangerous an operation is, and the console's production confirmation keys off this value — so a
        /// wrong guess in the safe direction only costs an extra confirmation, while a wrong guess in the
        /// other direction skips one.
        /// </remarks>
        internal static NetworkMutationClass MutationClassFor(OpenApiOperation operation)
        {
            switch (operation.Method)
            {
                case Molca.Networking.Http.Models.HttpMethod.DELETE:
                    return NetworkMutationClass.Destructive;

                case Molca.Networking.Http.Models.HttpMethod.POST:
                case Molca.Networking.Http.Models.HttpMethod.PUT:
                case Molca.Networking.Http.Models.HttpMethod.PATCH:
                    return NetworkMutationClass.Mutating;

                default:
                    return NetworkMutationClass.Safe;
            }
        }

        private static List<NetworkEndpointImport.Parameter> Project(List<OpenApiParameter> parameters)
        {
            var result = new List<NetworkEndpointImport.Parameter>(parameters.Count);

            foreach (var parameter in parameters)
            {
                result.Add(new NetworkEndpointImport.Parameter
                {
                    Name = parameter.Name,
                    Required = parameter.Required,
                    Description = parameter.Description,
                    // A credential-bearing parameter is redacted in diagnostics and history, and
                    // OpenApiParameter already refuses to carry a default for one.
                    Sensitive = parameter.Sensitive,
                    DefaultValue = parameter.DefaultValue,
                });
            }

            return result;
        }
    }
}
