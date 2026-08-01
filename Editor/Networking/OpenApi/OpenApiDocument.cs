using System;
using System.Collections.Generic;
using Molca.Networking.Http.Models;

namespace Molca.Editor.Networking.OpenApi
{
    /// <summary>Where a declared parameter travels.</summary>
    internal enum OpenApiParameterLocation
    {
        /// <summary>A <c>{placeholder}</c> in the path.</summary>
        Path = 0,

        /// <summary>A query-string parameter.</summary>
        Query,

        /// <summary>A request header.</summary>
        Header,

        /// <summary>Somewhere this importer does not model — a cookie, or a vendor extension.</summary>
        Other,
    }

    /// <summary>One parameter an operation declares.</summary>
    internal sealed class OpenApiParameter
    {
        /// <summary>Parameter name as it appears in the path, query, or header.</summary>
        public string Name { get; }

        /// <summary>Where it travels.</summary>
        public OpenApiParameterLocation Location { get; }

        /// <summary>Whether the spec marks it required.</summary>
        public bool Required { get; }

        /// <summary>The spec's description, or empty.</summary>
        public string Description { get; }

        /// <summary>
        /// The schema's default value, or empty. Never populated for a sensitive parameter.
        /// </summary>
        /// <remarks>
        /// Withheld when <see cref="Sensitive"/> so a spec's illustrative API key never becomes a
        /// pre-filled value in the request console — that is the one example that turns out to be a real
        /// key often enough to matter.
        /// </remarks>
        public string DefaultValue { get; }

        /// <summary>
        /// Whether the spec names this parameter as carrying a credential.
        /// </summary>
        /// <remarks>
        /// True when a security scheme declares it — an <c>apiKey</c> in a header or query. It maps to
        /// <c>NetworkParameterDefinition.Sensitive</c>, so its value is redacted in diagnostics and
        /// history rather than being logged like an ordinary parameter.
        /// </remarks>
        public bool Sensitive { get; }

        /// <summary>Creates a parameter.</summary>
        /// <param name="name">Parameter name.</param>
        /// <param name="location">Where it travels.</param>
        /// <param name="required">Whether the spec marks it required.</param>
        /// <param name="description">The spec's description.</param>
        /// <param name="sensitive">Whether it carries a credential.</param>
        /// <param name="defaultValue">The schema's default; ignored when <paramref name="sensitive"/>.</param>
        public OpenApiParameter(
            string name,
            OpenApiParameterLocation location,
            bool required = false,
            string description = null,
            bool sensitive = false,
            string defaultValue = null)
        {
            Name = name ?? string.Empty;
            Location = location;
            Required = required;
            Description = description ?? string.Empty;
            Sensitive = sensitive;
            DefaultValue = sensitive ? string.Empty : defaultValue ?? string.Empty;
        }
    }

    /// <summary>One operation the spec declares — the unit an endpoint template is imported from.</summary>
    internal sealed class OpenApiOperation
    {
        // Field separator for ContentHash. A unit separator cannot appear in an identifier, a path,
        // or a description, so two different field combinations cannot run together into the same
        // hash input.
        private const char Separator = (char)0x1f;

        /// <summary>
        /// The spec's <c>operationId</c>, or a deterministic fallback derived from method and path.
        /// </summary>
        /// <remarks>
        /// This becomes the endpoint's <c>SourceReference</c>, which is what makes a re-import able to
        /// recognize an operation it already imported. A fallback must therefore be stable across runs —
        /// never an index or a hash of iteration order.
        /// </remarks>
        public string OperationId { get; }

        /// <summary>HTTP method.</summary>
        public HttpMethod Method { get; }

        /// <summary>Path as written in the spec, including <c>{placeholders}</c>, without a leading slash.</summary>
        public string Path { get; }

        /// <summary>Summary and description, joined; may be empty.</summary>
        public string Description { get; }

        /// <summary>Declared parameters, in spec order.</summary>
        public IReadOnlyList<OpenApiParameter> Parameters { get; }

        /// <summary>The request body kind this operation accepts.</summary>
        public BodyType BodyType { get; }

        /// <summary>An example request body, or empty.</summary>
        public string RequestBodyExample { get; }

        /// <summary>The spec's tags.</summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>Whether the spec marks the operation deprecated.</summary>
        public bool Deprecated { get; }

        /// <summary>Whether the operation declares a security requirement.</summary>
        public bool RequiresAuthentication { get; }

        /// <summary>Creates an operation.</summary>
        public OpenApiOperation(
            string operationId,
            HttpMethod method,
            string path,
            string description,
            IReadOnlyList<OpenApiParameter> parameters,
            BodyType bodyType,
            string requestBodyExample,
            IReadOnlyList<string> tags,
            bool deprecated,
            bool requiresAuthentication)
        {
            OperationId = operationId ?? string.Empty;
            Method = method;
            Path = path ?? string.Empty;
            Description = description ?? string.Empty;
            Parameters = parameters ?? Array.Empty<OpenApiParameter>();
            BodyType = bodyType;
            RequestBodyExample = requestBodyExample ?? string.Empty;
            Tags = tags ?? Array.Empty<string>();
            Deprecated = deprecated;
            RequiresAuthentication = requiresAuthentication;
        }

        /// <summary>Parameters at one location.</summary>
        /// <param name="location">The location to filter to.</param>
        public List<OpenApiParameter> At(OpenApiParameterLocation location)
        {
            var result = new List<OpenApiParameter>();
            foreach (var parameter in Parameters)
            {
                if (parameter.Location == location) result.Add(parameter);
            }
            return result;
        }

        /// <summary>
        /// A content hash of everything import writes, so a re-import can tell "unchanged" from "changed".
        /// </summary>
        /// <remarks>
        /// Covers exactly the fields import authors and nothing else. Including something import does not
        /// write would make every re-import report a spurious change; excluding something it does write
        /// would make a real change read as unchanged, which is worse — a stale endpoint would look
        /// current.
        /// </remarks>
        public string ContentHash()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(Method).Append(Separator).Append(Path).Append(Separator)
                   .Append(Description).Append(Separator).Append(BodyType).Append(Separator)
                   .Append(RequestBodyExample).Append(Separator).Append(Deprecated).Append(Separator);

            foreach (var parameter in Parameters)
            {
                builder.Append(parameter.Location).Append(':').Append(parameter.Name).Append(':')
                       .Append(parameter.Required).Append(':').Append(parameter.Sensitive).Append(':')
                       .Append(parameter.DefaultValue).Append(Separator);
            }

            foreach (string tag in Tags)
                builder.Append(tag).Append(Separator);

            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
            // Twelve bytes of SHA-256 in base64: short enough to read in a diff, and far beyond any
            // chance of two real specs colliding. A change detector, not a security primitive.
            return Convert.ToBase64String(hash, 0, 12);
        }
    }

    /// <summary>
    /// The parts of an OpenAPI or Swagger document Molca imports.
    /// </summary>
    /// <remarks>
    /// Deliberately a narrow projection, not a general OpenAPI object model. Import produces endpoint
    /// <em>templates</em> — method, path, parameter shape, body kind — and Molca has no use for schema
    /// composition, discriminators, or response models. Parsing only what is used keeps the importer
    /// small enough to reason about and keeps a malformed corner of a spec from failing an import that
    /// did not need that corner.
    /// </remarks>
    internal sealed class OpenApiDocument
    {
        /// <summary>The spec's title, or empty.</summary>
        public string Title { get; }

        /// <summary>The spec's API version, or empty.</summary>
        public string Version { get; }

        /// <summary>
        /// Server URLs the spec declares.
        /// </summary>
        /// <remarks>
        /// Reported, never applied. A <c>servers</c> entry is a URL somebody wrote in a document; binding
        /// a service to it automatically would point the project's traffic wherever the spec author
        /// pointed, which is exactly the decision the catalog exists to make explicit.
        /// </remarks>
        public IReadOnlyList<string> Servers { get; }

        /// <summary>Every operation, in spec order.</summary>
        public IReadOnlyList<OpenApiOperation> Operations { get; }

        /// <summary>Non-fatal problems found while parsing — a skipped path, an unsupported body type.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Creates a document.</summary>
        public OpenApiDocument(
            string title,
            string version,
            IReadOnlyList<string> servers,
            IReadOnlyList<OpenApiOperation> operations,
            IReadOnlyList<string> warnings)
        {
            Title = title ?? string.Empty;
            Version = version ?? string.Empty;
            Servers = servers ?? Array.Empty<string>();
            Operations = operations ?? Array.Empty<OpenApiOperation>();
            Warnings = warnings ?? Array.Empty<string>();
        }

        /// <summary>A one-line summary for the Hub and MCP.</summary>
        public string Summarize()
        {
            string name = string.IsNullOrEmpty(Title) ? "Untitled API" : Title;
            string version = string.IsNullOrEmpty(Version) ? string.Empty : $" v{Version}";
            return $"{name}{version} — {Operations.Count} operation(s), {Servers.Count} server(s)";
        }
    }
}
