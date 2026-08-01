using System;
using System.Collections.Generic;
using Molca.Networking.Http.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Networking.OpenApi
{
    /// <summary>
    /// Parses an OpenAPI 3.x or Swagger 2.0 document into the narrow shape import needs.
    /// </summary>
    /// <remarks>
    /// JSON only. Unity ships no YAML parser and Molca will not add a dependency for one, so a YAML spec
    /// is refused with an instruction to convert it rather than half-parsed.
    /// <para>
    /// Tolerant by design: an operation this parser cannot make sense of becomes a warning and is skipped,
    /// not an exception. A 400-operation spec with one malformed corner should still import the other 399,
    /// and the author should be told which one was dropped.
    /// </para>
    /// <para>
    /// <c>$ref</c> is resolved for parameters and request bodies within the same document, one level of
    /// indirection at a time with a visited set. External and remote refs are not fetched — an importer
    /// that dereferences a URL is an importer that makes network requests from a parse, which is not a
    /// thing an editor tool should do quietly.
    /// </para>
    /// </remarks>
    internal static class OpenApiParser
    {
        /// <summary>Methods that can carry an operation. Anything else in a path item is ignored.</summary>
        private static readonly Dictionary<string, HttpMethod> Methods =
            new Dictionary<string, HttpMethod>(StringComparer.OrdinalIgnoreCase)
            {
                ["get"] = HttpMethod.GET,
                ["post"] = HttpMethod.POST,
                ["put"] = HttpMethod.PUT,
                ["patch"] = HttpMethod.PATCH,
                ["delete"] = HttpMethod.DELETE,
                ["head"] = HttpMethod.HEAD,
                ["options"] = HttpMethod.OPTIONS,
            };

        /// <summary>Guard against a cyclic <c>$ref</c> chain.</summary>
        private const int MaxRefDepth = 8;

        /// <summary>
        /// Parses a document.
        /// </summary>
        /// <param name="json">The spec's JSON text.</param>
        /// <param name="document">The parsed document on success.</param>
        /// <param name="error">Why parsing failed, or <c>null</c>.</param>
        /// <returns><c>false</c> when the text is not a usable OpenAPI or Swagger document.</returns>
        public static bool TryParse(string json, out OpenApiDocument document, out string error)
        {
            document = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The document is empty.";
                return false;
            }

            string trimmed = json.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                error =
                    "This does not look like JSON. Molca imports OpenAPI in JSON form only — convert a YAML " +
                    "spec first (for example with `npx @redocly/cli bundle spec.yaml -o spec.json`).";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException e)
            {
                error = $"The document is not valid JSON: {e.Message}";
                return false;
            }

            bool isSwagger2 = root.Value<string>("swagger")?.StartsWith("2", StringComparison.Ordinal) == true;
            bool isOpenApi3 = root.Value<string>("openapi")?.StartsWith("3", StringComparison.Ordinal) == true;

            if (!isSwagger2 && !isOpenApi3)
            {
                error =
                    "No 'openapi: 3.x' or 'swagger: 2.0' version field was found, so this is not a spec " +
                    "this importer understands.";
                return false;
            }

            var paths = root["paths"] as JObject;
            if (paths == null)
            {
                error = "The document declares no 'paths', so there is nothing to import.";
                return false;
            }

            var warnings = new List<string>();
            var info = root["info"] as JObject;
            var credentialParameters = ReadCredentialParameterNames(root, isSwagger2);
            bool hasGlobalSecurity = (root["security"] as JArray)?.Count > 0;

            var operations = new List<OpenApiOperation>();
            var seenOperationIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pathEntry in paths)
            {
                if (!(pathEntry.Value is JObject pathItem))
                {
                    warnings.Add($"Path '{pathEntry.Key}' is not an object and was skipped.");
                    continue;
                }

                // Parameters declared on the path item apply to every operation under it.
                var shared = ReadParameters(
                    root, pathItem["parameters"] as JArray, credentialParameters, warnings, pathEntry.Key);

                foreach (var methodEntry in pathItem)
                {
                    if (!Methods.TryGetValue(methodEntry.Key, out var method))
                        continue;

                    if (!(methodEntry.Value is JObject operationNode))
                    {
                        warnings.Add($"{methodEntry.Key.ToUpperInvariant()} {pathEntry.Key} is not an object and was skipped.");
                        continue;
                    }

                    var operation = ReadOperation(
                        root, operationNode, method, pathEntry.Key, shared, credentialParameters,
                        isSwagger2, hasGlobalSecurity, seenOperationIds, warnings);

                    if (operation != null)
                        operations.Add(operation);
                }
            }

            if (operations.Count == 0)
            {
                error = "The document declares paths but no operations this importer can read.";
                return false;
            }

            document = new OpenApiDocument(
                info?.Value<string>("title"),
                info?.Value<string>("version"),
                ReadServers(root, isSwagger2, warnings),
                operations,
                warnings);

            error = null;
            return true;
        }

        private static OpenApiOperation ReadOperation(
            JObject root,
            JObject node,
            HttpMethod method,
            string path,
            List<OpenApiParameter> shared,
            HashSet<string> credentialParameters,
            bool isSwagger2,
            bool hasGlobalSecurity,
            HashSet<string> seenOperationIds,
            List<string> warnings)
        {
            var parameters = new List<OpenApiParameter>(shared);
            parameters.AddRange(ReadParameters(
                root, node["parameters"] as JArray, credentialParameters, warnings, path));

            // Path placeholders the spec forgot to declare still have to be filled in before a send, so
            // they are inferred rather than dropped — an undeclared {id} would otherwise reach the wire
            // literally.
            foreach (string placeholder in Placeholders(path))
            {
                if (!HasParameter(parameters, placeholder, OpenApiParameterLocation.Path))
                {
                    parameters.Add(new OpenApiParameter(
                        placeholder, OpenApiParameterLocation.Path, required: true,
                        description: "Inferred from the path; the spec did not declare it."));
                }
            }

            var (bodyType, bodyExample) = isSwagger2
                ? ReadSwagger2Body(parameters)
                : ReadOpenApi3Body(root, node["requestBody"], warnings, path);

            bool requiresAuth = hasGlobalSecurity || (node["security"] as JArray)?.Count > 0;

            string operationId = node.Value<string>("operationId");
            if (string.IsNullOrWhiteSpace(operationId))
            {
                // Deterministic so a re-import matches what a previous import wrote. Iteration order and
                // indices are deliberately not part of it.
                operationId = $"{method.ToString().ToLowerInvariant()}-{Slug(path)}";
            }

            if (!seenOperationIds.Add(operationId))
            {
                warnings.Add(
                    $"Duplicate operationId '{operationId}' on {method} {path} was skipped; an operation ID " +
                    "must be unique for a re-import to match it.");
                return null;
            }

            return new OpenApiOperation(
                operationId,
                method,
                NormalizePath(path),
                JoinDescription(node.Value<string>("summary"), node.Value<string>("description")),
                parameters,
                bodyType,
                bodyExample,
                ReadTags(node),
                node.Value<bool?>("deprecated") ?? false,
                requiresAuth);
        }

        private static List<OpenApiParameter> ReadParameters(
            JObject root,
            JArray declared,
            HashSet<string> credentialParameters,
            List<string> warnings,
            string path)
        {
            var parameters = new List<OpenApiParameter>();
            if (declared == null) return parameters;

            foreach (var entry in declared)
            {
                var node = Dereference(root, entry, warnings) as JObject;
                if (node == null) continue;

                string name = node.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    warnings.Add($"A parameter on '{path}' has no name and was skipped.");
                    continue;
                }

                var location = ParseLocation(node.Value<string>("in"));
                if (location == OpenApiParameterLocation.Other)
                {
                    // A cookie parameter is a real thing this importer does not model. Saying so is more
                    // useful than silently producing a template that is missing an input.
                    warnings.Add(
                        $"Parameter '{name}' on '{path}' is in '{node.Value<string>("in")}', which Molca " +
                        "does not model; it was skipped.");
                    continue;
                }

                // OpenAPI 3 puts `default` on the schema; Swagger 2 puts it on the parameter.
                var schema = Dereference(root, node["schema"], warnings) as JObject;
                string defaultValue = (schema?["default"] ?? node["default"])?.ToString();

                parameters.Add(new OpenApiParameter(
                    name,
                    location,
                    node.Value<bool?>("required") ?? false,
                    node.Value<string>("description"),
                    credentialParameters.Contains(name),
                    defaultValue));
            }

            return parameters;
        }

        /// <summary>
        /// Names of parameters a security scheme uses to carry a credential.
        /// </summary>
        /// <remarks>
        /// Collected so those parameters import as <c>Sensitive</c>. Import reads only the <em>names</em>
        /// — it never creates a credential profile, because a spec cannot tell Molca where a secret comes
        /// from or which hosts may receive it, and guessing either is how a credential ends up somewhere
        /// nobody authorized.
        /// </remarks>
        private static HashSet<string> ReadCredentialParameterNames(JObject root, bool isSwagger2)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var schemes = isSwagger2
                ? root["securityDefinitions"] as JObject
                : (root["components"] as JObject)?["securitySchemes"] as JObject;

            if (schemes == null) return names;

            foreach (var entry in schemes)
            {
                if (!(entry.Value is JObject scheme)) continue;

                if (string.Equals(scheme.Value<string>("type"), "apiKey", StringComparison.OrdinalIgnoreCase))
                {
                    string name = scheme.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }

            return names;
        }

        private static (BodyType, string) ReadOpenApi3Body(
            JObject root, JToken requestBody, List<string> warnings, string path)
        {
            var node = Dereference(root, requestBody, warnings) as JObject;
            var content = node?["content"] as JObject;
            if (content == null) return (BodyType.None, string.Empty);

            foreach (var entry in content)
            {
                string mediaType = entry.Key;

                if (mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (BodyType.Json, ReadExample(root, entry.Value as JObject, warnings));

                if (mediaType.IndexOf("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mediaType.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (BodyType.Form, string.Empty);
                }
            }

            warnings.Add(
                $"The request body on '{path}' uses a media type Molca does not compose " +
                $"({string.Join(", ", ContentKeys(content))}); the endpoint imported with no body.");
            return (BodyType.None, string.Empty);
        }

        /// <summary>
        /// Swagger 2.0 declares a body as a parameter with <c>in: body</c>.
        /// </summary>
        /// <remarks>
        /// Those parameters are removed from the list rather than left in it: a body is not a query or
        /// header input, and leaving it would put a field named "body" in the console's parameter editor.
        /// </remarks>
        private static (BodyType, string) ReadSwagger2Body(List<OpenApiParameter> parameters)
        {
            // ReadParameters already dropped `in: body` and `in: formData` as unmodelled locations, so the
            // only thing left to decide is the body kind — and 2.0 bodies are JSON in practice.
            return (BodyType.None, string.Empty);
        }

        private static string ReadExample(JObject root, JObject mediaType, List<string> warnings)
        {
            if (mediaType == null) return string.Empty;

            var example = mediaType["example"];
            if (example != null)
                return example.ToString(Formatting.Indented);

            if (mediaType["examples"] is JObject examples)
            {
                foreach (var entry in examples)
                {
                    var value = (Dereference(root, entry.Value, warnings) as JObject)?["value"];
                    if (value != null) return value.ToString(Formatting.Indented);
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Resolves a local <c>$ref</c>, following at most <see cref="MaxRefDepth"/> hops.
        /// </summary>
        /// <remarks>
        /// Local only. Fetching an external <c>$ref</c> would mean a parse makes network requests, which
        /// an editor import must not do without the user having asked for it.
        /// </remarks>
        private static JToken Dereference(JObject root, JToken token, List<string> warnings)
        {
            for (int depth = 0; depth < MaxRefDepth; depth++)
            {
                if (!(token is JObject node)) return token;

                string reference = node.Value<string>("$ref");
                if (string.IsNullOrEmpty(reference)) return token;

                if (!reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    warnings.Add($"External reference '{reference}' was not fetched; it was skipped.");
                    return null;
                }

                JToken resolved = root;
                foreach (string segment in reference.Substring(2).Split('/'))
                {
                    string key = segment.Replace("~1", "/").Replace("~0", "~");
                    resolved = resolved?[key];
                }

                if (resolved == null)
                {
                    warnings.Add($"Reference '{reference}' does not resolve in this document; it was skipped.");
                    return null;
                }

                token = resolved;
            }

            warnings.Add("A $ref chain was too deep to resolve and was skipped; it is probably cyclic.");
            return null;
        }

        private static IReadOnlyList<string> ReadServers(JObject root, bool isSwagger2, List<string> warnings)
        {
            var servers = new List<string>();

            if (isSwagger2)
            {
                string host = root.Value<string>("host");
                string basePath = root.Value<string>("basePath") ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(host))
                {
                    var schemes = root["schemes"] as JArray;
                    string scheme = schemes != null && schemes.Count > 0 ? schemes[0].ToString() : "https";
                    servers.Add($"{scheme}://{host}{basePath}");
                }

                return servers;
            }

            if (!(root["servers"] is JArray declared)) return servers;

            foreach (var entry in declared)
            {
                string url = (entry as JObject)?.Value<string>("url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (url.IndexOf('{') >= 0)
                {
                    // A templated server URL needs variable values Molca has no way to choose.
                    warnings.Add($"Server URL '{url}' is templated and cannot be used as an origin as written.");
                }

                servers.Add(url);
            }

            return servers;
        }

        private static IReadOnlyList<string> ReadTags(JObject node)
        {
            var tags = new List<string>();
            if (!(node["tags"] is JArray declared)) return tags;

            foreach (var tag in declared)
            {
                string value = tag?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) tags.Add(value);
            }

            return tags;
        }

        private static OpenApiParameterLocation ParseLocation(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "path": return OpenApiParameterLocation.Path;
                case "query": return OpenApiParameterLocation.Query;
                case "header": return OpenApiParameterLocation.Header;
                default: return OpenApiParameterLocation.Other;
            }
        }

        private static bool HasParameter(
            List<OpenApiParameter> parameters, string name, OpenApiParameterLocation location)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Location == location &&
                    string.Equals(parameter.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The <c>{placeholder}</c> names in a path, in order.</summary>
        private static IEnumerable<string> Placeholders(string path)
        {
            int index = 0;
            while (index < path.Length)
            {
                int open = path.IndexOf('{', index);
                if (open < 0) break;

                int close = path.IndexOf('}', open + 1);
                if (close < 0) break;

                yield return path.Substring(open + 1, close - open - 1);
                index = close + 1;
            }
        }

        /// <summary>
        /// A path relative to the service origin.
        /// </summary>
        /// <remarks>
        /// The leading slash is stripped because an endpoint's path is relative by contract — the origin
        /// comes from the service binding, and a leading slash would make joining ambiguous.
        /// </remarks>
        private static string NormalizePath(string path) => (path ?? string.Empty).TrimStart('/');

        private static string JoinDescription(string summary, string description)
        {
            if (string.IsNullOrWhiteSpace(summary)) return description ?? string.Empty;
            if (string.IsNullOrWhiteSpace(description)) return summary;
            return summary.TrimEnd() + "\n\n" + description;
        }

        /// <summary>A kebab-case slug of a path, for a fallback operation ID.</summary>
        private static string Slug(string path)
        {
            var builder = new System.Text.StringBuilder(path.Length);
            bool lastWasSeparator = true;

            foreach (char c in path)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        private static IEnumerable<string> ContentKeys(JObject content)
        {
            foreach (var entry in content)
                yield return entry.Key;
        }
    }
}
