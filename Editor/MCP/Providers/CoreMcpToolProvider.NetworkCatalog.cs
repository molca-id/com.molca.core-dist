using System;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Migration;
using Molca.Editor.Networking.OpenApi;
using Molca.Editor.Networking.RequestConsole;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Structured network-catalog tools: inspect, edit, validate, migrate, send, and read diagnostics.
    /// </summary>
    /// <remarks>
    /// <b>Every one of these delegates.</b> Reads project the catalog and
    /// <c>NetworkCatalogValidator</c>; writes go through <c>NetworkCatalogEditingService</c>; migration
    /// goes through <c>LegacyMigrationExecutor</c>; a send goes through <c>NetworkConsolePreflight</c> and
    /// <c>NetworkConsoleRunner</c>. There is no rule, no ID normalization, and no origin parsing in this
    /// file, because a second copy of that logic is how automation and the Hub start disagreeing about
    /// what a valid catalog is (plan §8.2).
    /// <para>
    /// The legacy <c>molca_network_*_request</c> tools in
    /// <c>CoreMcpToolProvider.Networking.cs</c> are unchanged and stay as adapters for projects still
    /// authoring <c>HttpRequestAsset</c>s.
    /// </para>
    /// <para>
    /// Automation gets no privileged path. It cannot weaken a security rule, cannot bypass Undo, and
    /// cannot send a production mutation the catalog forbids — a forbidden send is <em>refused</em>, never
    /// prompted, because there is no user at an MCP call to answer a dialog.
    /// </para>
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_network_catalog (read) ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkCatalogTool() => new McpToolDefinition(
            name: "molca_network_catalog",
            description: "Inspects the NetworkCatalog: environments, services with their per-environment "
                       + "bindings, policy profiles, credential profile metadata (never a value), endpoint "
                       + "collections, and a validation summary. Pass 'verbose' for full endpoint listings. "
                       + "Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"verbose\":{\"type\":\"boolean\",\"description\":\"Include every endpoint template.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteNetworkCatalog,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkCatalog(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            bool verbose = args.Value<bool?>("verbose") ?? false;

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
            {
                return new JObject
                {
                    ["hasCatalog"] = false,
                    ["message"] = "This project has no NetworkCatalog. Requests resolve against " +
                                  "HttpModule.BaseUrl. Create one from the Hub's Network workspace.",
                }.ToString(Formatting.None);
            }

            var report = NetworkCatalogValidator.Validate(catalog);

            var environments = new JArray();
            foreach (var environment in catalog.Environments)
            {
                if (environment == null) continue;
                environments.Add(new JObject
                {
                    ["id"] = environment.Id,
                    ["classification"] = environment.Classification.ToString(),
                    ["isDefault"] = string.Equals(environment.Id, catalog.DefaultEnvironmentId, StringComparison.Ordinal),
                    ["productionSafetyEnforced"] = environment.IsProductionSafetyEnforced,
                });
            }

            var services = new JArray();
            foreach (var service in catalog.Services)
            {
                if (service == null) continue;

                var bindings = new JArray();
                foreach (var binding in service.Bindings)
                {
                    if (binding == null) continue;
                    bindings.Add(new JObject
                    {
                        ["environmentId"] = binding.EnvironmentId,
                        ["httpOrigin"] = binding.HttpOrigin,
                        ["sseOrigin"] = binding.SseOrigin,
                        ["webSocketOrigin"] = binding.WebSocketOrigin,
                        ["socketIoOrigin"] = binding.SocketIoOrigin,
                        ["enabled"] = binding.Enabled,
                    });
                }

                services.Add(new JObject
                {
                    ["id"] = service.Id,
                    ["protocols"] = service.Protocols.ToString(),
                    ["credentialProfileId"] = service.CredentialProfileId,
                    ["policyProfileId"] = service.PolicyProfileId,
                    ["allowedHostPatterns"] = new JArray(service.AllowedHostPatterns),
                    ["bindings"] = bindings,
                });
            }

            var credentials = new JArray();
            foreach (var credential in catalog.CredentialProfiles)
            {
                if (credential == null) continue;

                // Metadata only, by contract. A credential value never crosses MCP, and neither does
                // anything that would let a caller derive one.
                credentials.Add(new JObject
                {
                    ["id"] = credential.Id,
                    ["providerKind"] = credential.ProviderKind.ToString(),
                    ["headerName"] = credential.HeaderName,
                    ["allowedServiceIds"] = new JArray(credential.AllowedServiceIds),
                    ["allowedHostPatterns"] = new JArray(credential.AllowedHostPatterns),
                    ["usableFromRequestConsole"] = credential.UsableFromRequestConsole,
                });
            }

            var collections = new JArray();
            foreach (var collection in catalog.EndpointCollections)
            {
                if (collection == null) continue;

                var entry = new JObject
                {
                    ["id"] = collection.CollectionId,
                    ["defaultServiceId"] = collection.ServiceId,
                    ["endpointCount"] = collection.Endpoints.Count,
                };

                if (verbose)
                {
                    var endpoints = new JArray();
                    foreach (var endpoint in collection.Endpoints)
                    {
                        if (endpoint == null) continue;
                        endpoints.Add(new JObject
                        {
                            ["id"] = endpoint.Id,
                            ["serviceId"] = endpoint.ServiceId,
                            ["method"] = endpoint.Method.ToString(),
                            ["relativePath"] = endpoint.RelativePath,
                            ["mutationClass"] = endpoint.MutationClass.ToString(),
                            ["requiresIdempotencyKey"] = endpoint.RequiresIdempotencyKey,
                            ["source"] = endpoint.Source.ToString(),
                        });
                    }
                    entry["endpoints"] = endpoints;
                }

                collections.Add(entry);
            }

            var policies = new JArray();
            foreach (var policy in catalog.PolicyProfiles)
            {
                if (policy == null) continue;
                policies.Add(new JObject
                {
                    ["id"] = policy.Id,
                    ["isDefault"] = string.Equals(policy.Id, catalog.DefaultPolicyProfileId, StringComparison.Ordinal),
                });
            }

            return new JObject
            {
                ["hasCatalog"] = true,
                ["assetPath"] = UnityEditor.AssetDatabase.GetAssetPath(catalog),
                ["registeredOnGlobalSettings"] = NetworkCatalogLocator.IsRegistered(catalog),
                ["schemaVersion"] = catalog.SchemaVersion,
                ["requiresSchemaMigration"] = catalog.RequiresSchemaMigration,
                ["defaultEnvironmentId"] = catalog.DefaultEnvironmentId,
                ["defaultPolicyProfileId"] = catalog.DefaultPolicyProfileId,
                ["failBuildOnValidationError"] = catalog.FailBuildOnValidationError,
                ["allowProductionConsoleMutations"] = catalog.AllowProductionConsoleMutations,
                ["environments"] = environments,
                ["services"] = services,
                ["policyProfiles"] = policies,
                ["credentialProfiles"] = credentials,
                ["endpointCollections"] = collections,
                ["validation"] = new JObject
                {
                    ["isValid"] = report.IsValid,
                    ["summary"] = report.Summarize(),
                    ["errorCount"] = report.ErrorCount,
                    ["warningCount"] = report.WarningCount,
                },
            }.ToString(Formatting.None);
        }

        // ── molca_network_validate (read) ────────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkValidateTool() => new McpToolDefinition(
            name: "molca_network_validate",
            description: "Validates the NetworkCatalog, or resolves one route when 'service' is supplied. "
                       + "Returns the same findings — same stable codes — that Doctor and the build gate use. "
                       + "Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"service\":{\"type\":\"string\",\"description\":\"Resolve this service instead of validating the whole catalog.\"}," +
                "\"environment\":{\"type\":\"string\",\"description\":\"Environment to resolve under; defaults to the catalog default.\"}," +
                "\"endpoint\":{\"type\":\"string\",\"description\":\"Endpoint template to apply.\"}," +
                "\"protocol\":{\"type\":\"string\",\"description\":\"Http, ServerSentEvents, WebSocket, or SocketIO.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteNetworkValidate,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkValidate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
                return Error("This project has no NetworkCatalog to validate.");

            string serviceId = args.Value<string>("service");
            if (!string.IsNullOrWhiteSpace(serviceId))
                return ResolveOneRoute(catalog, args, serviceId);

            var report = NetworkCatalogValidator.Validate(catalog);
            var findings = new JArray();

            foreach (var finding in report.Findings)
            {
                findings.Add(new JObject
                {
                    ["code"] = finding.Code,
                    ["severity"] = finding.Severity.ToString(),
                    ["entityKind"] = finding.EntityKind.ToString(),
                    ["entityId"] = finding.EntityId,
                    ["message"] = finding.Message,
                    ["remedy"] = finding.Remedy,
                    // The deep link a caller can hand back to a human.
                    ["link"] = Networking.Hub.NetworkHubDeepLinks.For(finding).ToString(),
                });
            }

            return new JObject
            {
                ["isValid"] = report.IsValid,
                ["summary"] = report.Summarize(),
                ["errorCount"] = report.ErrorCount,
                ["warningCount"] = report.WarningCount,
                ["findings"] = findings,
            }.ToString(Formatting.None);
        }

        private static string ResolveOneRoute(NetworkCatalog catalog, JObject args, string serviceId)
        {
            string environmentId = args.Value<string>("environment");
            if (string.IsNullOrWhiteSpace(environmentId))
                environmentId = catalog.DefaultEnvironmentId;

            if (string.IsNullOrWhiteSpace(environmentId))
                return Error("No environment was supplied and the catalog names no default environment.");

            if (!Enum.TryParse(args.Value<string>("protocol") ?? "Http", true, out NetworkProtocols protocol))
                protocol = NetworkProtocols.Http;

            var effective = new NetworkEffectiveConfigurationService(catalog);
            var route = effective.Resolve(
                new NetworkRouteKey(environmentId, serviceId), protocol, args.Value<string>("endpoint"));

            var policy = route.Policy;

            return new JObject
            {
                ["route"] = $"{environmentId}/{serviceId}",
                ["protocol"] = protocol.ToString(),
                ["resolves"] = route.Resolves,
                ["failureCategory"] = route.FailureCategory.ToString(),
                ["failureReason"] = route.FailureReason,
                ["origin"] = route.Origin,
                ["resolvedUri"] = route.ResolvedUri,
                ["isProduction"] = route.IsProduction,
                // Name only. The resolver knows the profile; it never holds a value.
                ["credentialProfileId"] = route.CredentialProfileId,
                ["credentialAppliesToHost"] = route.CredentialAppliesToHost,
                ["policy"] = policy == null ? null : new JObject
                {
                    ["overallTimeoutSeconds"] = policy.OverallTimeoutSeconds.Value,
                    ["attemptTimeoutSeconds"] = policy.AttemptTimeoutSeconds.Value,
                    ["retryEnabled"] = policy.RetryEnabled.Value,
                    ["maxRetries"] = policy.MaxRetries.Value,
                    ["redirectMode"] = policy.RedirectMode.Value.ToString(),
                    ["requireSecureTransport"] = policy.RequireSecureTransport.Value,
                    ["validateTlsCertificate"] = policy.ValidateTlsCertificate.Value,
                    ["securityClamps"] = new JArray(policy.SecurityClamps),
                },
            }.ToString(Formatting.None);
        }

        // ── molca_network_edit (action) ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkEditTool() => new McpToolDefinition(
            name: "molca_network_edit",
            description: "Authors the NetworkCatalog through the shared editing service. 'operation' is one of: "
                       + "create_environment, create_service, bind_service, create_policy, create_credential, "
                       + "create_collection, create_endpoint, set_default_environment, set_default_policy, "
                       + "rename_environment, rename_service, delete_environment, delete_service. IDs are "
                       + "normalized and de-duplicated by the same rules the Hub uses; every write is one "
                       + "Undo group. Creates the catalog if the project has none.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"operation\":{\"type\":\"string\"}," +
                "\"id\":{\"type\":\"string\"},\"newId\":{\"type\":\"string\"}," +
                "\"displayName\":{\"type\":\"string\"}," +
                "\"environmentId\":{\"type\":\"string\"},\"serviceId\":{\"type\":\"string\"}," +
                "\"collectionId\":{\"type\":\"string\"}," +
                "\"classification\":{\"type\":\"string\"},\"protocols\":{\"type\":\"string\"}," +
                "\"providerKind\":{\"type\":\"string\"}," +
                "\"httpOrigin\":{\"type\":\"string\"}," +
                "\"method\":{\"type\":\"string\"},\"relativePath\":{\"type\":\"string\"}," +
                "\"folder\":{\"type\":\"string\"}}," +
                "\"required\":[\"operation\"],\"additionalProperties\":false}",
            execute: ExecuteNetworkEdit,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteNetworkEdit(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string operation = (args.Value<string>("operation") ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(operation))
                return Error("Supply an 'operation'.");

            // A catalog asset is project-owned by construction — the locator creates it under Assets —
            // so the protected-zone rule is satisfied without a path check here.
            var catalog = NetworkCatalogLocator.GetOrCreateCatalog();
            if (catalog == null)
                return Error("Could not locate or create a NetworkCatalog.");

            var editing = new NetworkCatalogEditingService(catalog);

            switch (operation)
            {
                case "create_environment":
                    return Report(editing.CreateEnvironment(
                        Require(args, "id"),
                        args.Value<string>("displayName"),
                        ParseEnum(args.Value<string>("classification"),
                            NetworkEnvironmentClassification.Development)));

                case "create_service":
                    return Report(editing.CreateService(
                        Require(args, "id"),
                        args.Value<string>("displayName"),
                        ParseEnum(args.Value<string>("protocols"), NetworkProtocols.Http)));

                case "bind_service":
                    return Report(editing.SetHttpBinding(
                        Require(args, "serviceId"),
                        Require(args, "environmentId"),
                        args.Value<string>("httpOrigin")));

                case "create_policy":
                    return Report(editing.CreatePolicyProfile(
                        Require(args, "id"), args.Value<string>("displayName")));

                case "create_credential":
                    return Report(editing.CreateCredentialProfile(
                        Require(args, "id"),
                        args.Value<string>("displayName"),
                        ParseEnum(args.Value<string>("providerKind"),
                            NetworkCredentialProviderKind.None)));

                case "create_collection":
                    return Report(editing.CreateEndpointCollection(
                        Require(args, "id"),
                        args.Value<string>("serviceId"),
                        args.Value<string>("displayName"),
                        args.Value<string>("folder")));

                case "create_endpoint":
                    return CreateEndpoint(catalog, editing, args);

                case "set_default_environment":
                    return Report(editing.SetDefaultEnvironment(Require(args, "id")));

                case "set_default_policy":
                    return Report(editing.SetDefaultPolicyProfile(Require(args, "id")));

                case "rename_environment":
                    return Report(editing.RenameEnvironmentId(Require(args, "id"), Require(args, "newId")));

                case "rename_service":
                    return Report(editing.RenameServiceId(Require(args, "id"), Require(args, "newId")));

                case "delete_environment":
                    return Report(editing.DeleteEnvironment(Require(args, "id")));

                case "delete_service":
                    return Report(editing.DeleteService(Require(args, "id")));

                default:
                    return Error(
                        $"Unknown operation '{operation}'. See the tool description for the supported set.");
            }
        }

        private static string CreateEndpoint(
            NetworkCatalog catalog, NetworkCatalogEditingService editing, JObject args)
        {
            string collectionId = Require(args, "collectionId");
            NetworkEndpointCollection collection = null;

            foreach (var candidate in catalog.EndpointCollections)
            {
                if (candidate != null && string.Equals(candidate.CollectionId, collectionId, StringComparison.Ordinal))
                {
                    collection = candidate;
                    break;
                }
            }

            if (collection == null)
                return Error($"No endpoint collection '{collectionId}' is registered on this catalog.");

            return Report(editing.CreateHttpEndpoint(
                collection,
                Require(args, "id"),
                args.Value<string>("serviceId"),
                ParseEnum(args.Value<string>("method"), HttpMethod.GET),
                args.Value<string>("relativePath")));
        }

        // ── molca_network_migrate (read + action) ────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkMigrateTool() => new McpToolDefinition(
            name: "molca_network_migrate",
            description: "Scans the project's legacy networking (HttpModule base URL, HttpRequestAssets, HTTP "
                       + "and streaming providers) and reports what migration would create. Pass "
                       + "'apply': true to execute it. The scan is read-only; applying creates project-owned "
                       + "catalog and collection assets in one Undo group and never deletes a legacy asset. "
                       + "The migrated credential profile is created unscoped, so it can reach nothing until "
                       + "an author scopes it.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"apply\":{\"type\":\"boolean\",\"description\":\"Execute the plan instead of previewing it.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteNetworkMigrate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteNetworkMigrate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            bool apply = args.Value<bool?>("apply") ?? false;

            var plan = LegacyMigrationExecutor.DryRun();

            var steps = new JArray();
            foreach (var step in plan.Steps)
                steps.Add(new JObject { ["kind"] = step.Kind.ToString(), ["description"] = step.Description });

            var skips = new JArray();
            foreach (var skip in plan.Skipped)
            {
                skips.Add(new JObject
                {
                    ["reason"] = skip.Reason,
                    ["subject"] = skip.Item?.DisplayName ?? string.Empty,
                    ["alreadyMigrated"] = skip.AlreadyMigrated,
                });
            }

            var result = new JObject
            {
                ["applied"] = false,
                ["scan"] = plan.Report.Summarize(),
                ["stepCount"] = plan.Steps.Count,
                ["steps"] = steps,
                ["skips"] = skips,
            };

            if (!apply)
                return result.ToString(Formatting.None);

            var outcome = LegacyMigrationExecutor.Apply(plan);
            result["applied"] = outcome.Success;
            result["cancelled"] = outcome.Cancelled;
            result["appliedSteps"] = new JArray(outcome.Applied);
            result["failures"] = new JArray(outcome.Failures);
            return result.ToString(Formatting.None);
        }

        // ── molca_network_import_openapi (read + action) ────────────────────────────

        private static McpToolDefinition CreateNetworkImportOpenApiTool() => new McpToolDefinition(
            name: "molca_network_import_openapi",
            description: "Imports an OpenAPI 3.x or Swagger 2.0 JSON document into an endpoint collection. "
                       + "Previews a reviewable diff by default (add / update / unchanged / conflict, plus "
                       + "orphans and the spec's server URLs); pass 'apply': true to write it in one Undo "
                       + "group. It never overwrites a hand-authored endpoint, never binds a service to a "
                       + "server URL from the spec, and never creates a credential profile.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"spec\":{\"type\":\"string\",\"description\":\"Path to a JSON OpenAPI document.\"}," +
                "\"collection\":{\"type\":\"string\",\"description\":\"Endpoint collection ID to import into.\"}," +
                "\"service\":{\"type\":\"string\",\"description\":\"Service the endpoints belong to; defaults to the collection's.\"}," +
                "\"idPrefix\":{\"type\":\"string\",\"description\":\"Prefix for generated endpoint IDs.\"}," +
                "\"apply\":{\"type\":\"boolean\",\"description\":\"Write the plan instead of previewing it.\"}}," +
                "\"required\":[\"spec\",\"collection\"],\"additionalProperties\":false}",
            execute: ExecuteNetworkImportOpenApi,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteNetworkImportOpenApi(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
                return Error("This project has no NetworkCatalog to import into.");

            string collectionId = Require(args, "collection");
            NetworkEndpointCollection collection = null;

            foreach (var candidate in catalog.EndpointCollections)
            {
                if (candidate != null &&
                    string.Equals(candidate.CollectionId, collectionId, StringComparison.Ordinal))
                {
                    collection = candidate;
                    break;
                }
            }

            if (collection == null)
            {
                return Error(
                    $"No endpoint collection '{collectionId}' is registered on this catalog. Create one with " +
                    "molca_network_edit operation=create_collection.");
            }

            if (!NetworkOpenApiImportService.TryLoad(Require(args, "spec"), out var document, out string error))
                return Error(error);

            var plan = NetworkOpenApiImportService.Plan(
                document, collection, args.Value<string>("service"), args.Value<string>("idPrefix"));

            var entries = new JArray();
            foreach (var entry in plan.Entries)
            {
                entries.Add(new JObject
                {
                    ["action"] = entry.Action.ToString(),
                    ["operationId"] = entry.Operation.OperationId,
                    ["method"] = entry.Operation.Method.ToString(),
                    ["path"] = entry.Operation.Path,
                    ["endpointId"] = entry.EndpointId,
                    ["reason"] = entry.Reason,
                    ["changes"] = new JArray(entry.Changes),
                });
            }

            var payload = new JObject
            {
                ["applied"] = false,
                ["spec"] = document.Summarize(),
                ["summary"] = plan.Summarize(),
                ["addCount"] = plan.AddCount,
                ["updateCount"] = plan.UpdateCount,
                ["unchangedCount"] = plan.UnchangedCount,
                ["conflictCount"] = plan.ConflictCount,
                ["entries"] = entries,
                ["orphans"] = new JArray(plan.Orphans),
                // Reported, never applied. Binding a service to a URL from a document is a decision the
                // catalog makes explicit.
                ["declaredServers"] = new JArray(document.Servers),
                ["parseWarnings"] = new JArray(document.Warnings),
            };

            if (!(args.Value<bool?>("apply") ?? false))
                return payload.ToString(Formatting.None);

            var result = NetworkOpenApiImportService.Apply(plan, catalog);

            payload["applied"] = result.Success;
            payload["added"] = new JArray(result.Added);
            payload["updated"] = new JArray(result.Updated);
            payload["failures"] = new JArray(result.Failures);
            return payload.ToString(Formatting.None);
        }

        // ── molca_network_send (action) ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkSendTool() => new McpToolDefinition(
            name: "molca_network_send",
            description: "Sends one request through the routed pipeline from the editor, against a catalog "
                       + "route — never a raw URL. Returns status, error category, timings, attempts, and a "
                       + "redacted body preview. Refuses anything preflight blocks, and refuses a production "
                       + "mutation outright rather than prompting: there is no user at an MCP call to confirm "
                       + "one. Credentials come only from profiles marked usable from the request console.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"service\":{\"type\":\"string\"},\"environment\":{\"type\":\"string\"}," +
                "\"endpoint\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}," +
                "\"method\":{\"type\":\"string\"},\"body\":{\"type\":\"string\"}," +
                "\"idempotencyKey\":{\"type\":\"string\"}," +
                "\"captureBody\":{\"type\":\"boolean\",\"description\":\"Include a redacted response body preview.\"}," +
                "\"preflightOnly\":{\"type\":\"boolean\",\"description\":\"Report the preflight without sending.\"}}," +
                "\"required\":[\"service\"],\"additionalProperties\":false}",
            execute: ExecuteNetworkSend,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteNetworkSend(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
                return Error("This project has no NetworkCatalog, so there is no route to send to.");

            string environmentId = args.Value<string>("environment");
            if (string.IsNullOrWhiteSpace(environmentId))
                environmentId = catalog.DefaultEnvironmentId;

            var draft = new NetworkConsoleRequest
            {
                EnvironmentId = environmentId ?? string.Empty,
                ServiceId = args.Value<string>("service") ?? string.Empty,
                EndpointId = args.Value<string>("endpoint") ?? string.Empty,
                RelativePath = args.Value<string>("path") ?? string.Empty,
                Method = ParseEnum(args.Value<string>("method"), HttpMethod.GET),
                Body = args.Value<string>("body") ?? string.Empty,
                IdempotencyKey = args.Value<string>("idempotencyKey") ?? string.Empty,
                CaptureBody = args.Value<bool?>("captureBody") ?? false,
            };

            if (!string.IsNullOrEmpty(draft.Body))
                draft.BodyType = BodyType.Json;

            var preflight = NetworkConsolePreflight.Evaluate(catalog, draft);

            var notes = new JArray();
            foreach (var note in preflight.Notes)
                notes.Add(new JObject { ["code"] = note.Code, ["level"] = note.Level.ToString(), ["message"] = note.Message });

            var payload = new JObject
            {
                ["sent"] = false,
                ["canSend"] = preflight.CanSend,
                ["requiresConfirmation"] = preflight.RequiresConfirmation,
                ["destination"] = preflight.RedactedUri,
                ["credentialProfileId"] = preflight.CredentialProfileId,
                ["credentialWillBeSent"] = preflight.CredentialWillBeSent,
                ["isMutation"] = preflight.IsMutation,
                ["notes"] = notes,
            };

            if (args.Value<bool?>("preflightOnly") ?? false)
                return payload.ToString(Formatting.None);

            if (!preflight.CanSend)
            {
                payload["error"] = "Preflight blocked this send. See 'notes'.";
                return payload.ToString(Formatting.None);
            }

            if (preflight.RequiresConfirmation)
            {
                // Automation must not bypass a production confirmation, and it cannot answer one either.
                // Refusing is the only honest outcome: a human opens the Hub's console for this.
                payload["error"] =
                    "This send is a production mutation and needs a per-send human confirmation, which an " +
                    "MCP call cannot give. Send it from the Hub's Network ▸ Console instead.";
                return payload.ToString(Formatting.None);
            }

            var runner = new NetworkConsoleRunner();
            try
            {
                runner.Rebuild(catalog);

                // The bridge calls tools from a background thread; the routed pipeline is Awaitable-based
                // and main-thread only. This is the same dispatcher every other async tool uses, so a send
                // cannot deadlock the bridge and cannot outlive its timeout.
                var outcome = McpMainThreadDispatcher.InvokeAsync(
                    () => runner.SendAsync(draft), MaxMcpSendMilliseconds);

                if (outcome == null)
                {
                    payload["error"] = "The send did not complete.";
                    return payload.ToString(Formatting.None);
                }

                payload["sent"] = true;
                payload["statusCode"] = outcome.StatusCode;
                payload["isSuccess"] = outcome.IsSuccess;
                payload["category"] = outcome.Category.ToString();
                payload["message"] = outcome.Message;
                payload["correlationId"] = outcome.CorrelationId;
                payload["attemptCount"] = outcome.AttemptCount;
                payload["totalMs"] = outcome.Timings.Total.TotalMilliseconds;
                payload["servedFromCache"] = outcome.ServedFromCache;
                payload["redirectCount"] = outcome.RedirectCount;
                payload["securityClamps"] = new JArray(outcome.SecurityClamps);

                if (draft.CaptureBody)
                    payload["bodyPreview"] = runner.LastBodyPreview;

                return payload.ToString(Formatting.None);
            }
            finally
            {
                runner.Dispose();
            }
        }

        /// <summary>Ceiling on one MCP-initiated send, so a hung server cannot block the bridge.</summary>
        private const int MaxMcpSendMilliseconds = 60000;

        // ── molca_network_diagnostics (read) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkDiagnosticsTool() => new McpToolDefinition(
            name: "molca_network_diagnostics",
            description: "Returns the running game's redacted network diagnostics: completed/failed counts, "
                       + "per-route queue and circuit state, live streaming sessions, and the retained request "
                       + "records. Request headers are never retained and query values are masked. Available "
                       + "only in Play mode. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteNetworkDiagnostics,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkDiagnostics(string argumentsJson)
        {
            if (!Application.isPlaying || !RuntimeManager.IsReady)
            {
                return new JObject
                {
                    ["available"] = false,
                    ["message"] = "Network diagnostics exist while the game is running. Enter Play mode.",
                }.ToString(Formatting.None);
            }

            var diagnostics = RuntimeManager.GetService<Molca.Networking.Diagnostics.INetworkDiagnostics>();
            if (diagnostics == null)
            {
                return new JObject
                {
                    ["available"] = false,
                    ["message"] = "INetworkDiagnostics is not registered. Add a NetworkRuntimeSubsystem.",
                }.ToString(Formatting.None);
            }

            var routes = new JArray();
            foreach (var state in diagnostics.RouteStates())
            {
                routes.Add(new JObject
                {
                    ["route"] = state.Route.ToString(),
                    ["active"] = state.ActiveCount,
                    ["waiting"] = state.WaitingCount,
                    ["consecutiveFailures"] = state.ConsecutiveFailures,
                });
            }

            var streams = new JArray();
            foreach (var session in diagnostics.StreamSessions())
            {
                streams.Add(new JObject
                {
                    ["id"] = session.Id,
                    ["protocol"] = session.Protocol.ToString(),
                    ["route"] = session.Route.ToString(),
                    ["state"] = session.State.ToString(),
                    ["attempts"] = session.AttemptCount,
                    ["received"] = session.ReceivedCount,
                    ["authenticated"] = session.IsAuthenticated,
                    ["lastError"] = session.LastError,
                });
            }

            var records = new JArray();
            foreach (var record in diagnostics.Snapshot())
            {
                records.Add(new JObject
                {
                    ["completedUtc"] = record.CompletedUtc.ToString("O"),
                    ["route"] = record.Route.ToString(),
                    ["method"] = record.Method,
                    // Already masked at capture; no further redaction step to forget.
                    ["uri"] = record.Uri,
                    ["statusCode"] = record.StatusCode,
                    ["category"] = record.Category.ToString(),
                    ["isSuccess"] = record.IsSuccess,
                    ["totalMs"] = record.Timings.Total.TotalMilliseconds,
                    ["attempts"] = record.Attempts.Count,
                    ["credentialProfileId"] = record.CredentialProfileId,
                    ["correlationId"] = record.CorrelationId,
                });
            }

            return new JObject
            {
                ["available"] = true,
                ["totalCompleted"] = diagnostics.TotalCompleted,
                ["totalFailed"] = diagnostics.TotalFailed,
                ["retained"] = diagnostics.Count,
                ["capacity"] = diagnostics.Capacity,
                ["observerFailures"] = diagnostics.ObserverFailureCount,
                ["paused"] = diagnostics.IsPaused,
                ["routeStates"] = routes,
                ["streamSessions"] = streams,
                ["records"] = records,
            }.ToString(Formatting.None);
        }

        // ── Shared helpers ──────────────────────────────────────────────────────────────────

        /// <summary>Renders an authoring result, including the references a rename or delete touched.</summary>
        private static string Report(NetworkAuthoringResult result)
        {
            var payload = new JObject
            {
                ["success"] = result.Success,
                ["message"] = result.Message,
            };

            if (!string.IsNullOrEmpty(result.ResultId))
                payload["id"] = result.ResultId;

            if (result.AffectedReferences != null && result.AffectedReferences.Count > 0)
                payload["affected"] = new JArray(result.AffectedReferences);

            return payload.ToString(Formatting.None);
        }

        private static string Require(JObject args, string key) => args.Value<string>(key) ?? string.Empty;

        /// <summary>
        /// Parses an enum argument, falling back rather than failing.
        /// </summary>
        /// <remarks>
        /// A fallback is safe here only because every enum this is used with has a conservative zero or
        /// named default — <c>Development</c>, <c>Http</c>, <c>None</c>, <c>GET</c>. None of them widens a
        /// permission when a caller misspells a value.
        /// </remarks>
        private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
            !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out T parsed) ? parsed : fallback;
    }
}
