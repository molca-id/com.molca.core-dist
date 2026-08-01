using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// The full set of endpoint fields an importer authors, as one value.
    /// </summary>
    /// <remarks>
    /// Exists so <see cref="NetworkCatalogEditingService"/> can write a whole endpoint transactionally
    /// rather than exposing a setter per field. An import writes a template as a unit — a half-applied
    /// endpoint with the new path but the old parameters is worse than no endpoint at all.
    /// <para>
    /// It carries only what import owns. Policy profile, idempotency requirement, and expected response
    /// type are deliberately absent: those are decisions an author makes about <em>their</em> project, and
    /// re-importing a spec must not reset them.
    /// </para>
    /// </remarks>
    public sealed class NetworkEndpointImport
    {
        /// <summary>One imported parameter.</summary>
        public sealed class Parameter
        {
            /// <summary>Parameter name as it appears in the path, query, or header.</summary>
            public string Name;

            /// <summary>Whether a value must be supplied.</summary>
            public bool Required;

            /// <summary>The source document's description.</summary>
            public string Description;

            /// <summary>Whether this parameter carries a credential and must be redacted.</summary>
            public bool Sensitive;

            /// <summary>The source document's default value, or empty. Never set for a sensitive parameter.</summary>
            public string DefaultValue;
        }

        /// <summary>Service the endpoint belongs to; empty inherits the collection's default.</summary>
        public string ServiceId;

        /// <summary>HTTP method.</summary>
        public HttpMethod Method = HttpMethod.GET;

        /// <summary>Path relative to the service origin, including <c>{placeholders}</c>.</summary>
        public string RelativePath;

        /// <summary>Description carried over from the source document.</summary>
        public string Description;

        /// <summary>Body kind the endpoint accepts.</summary>
        public BodyType BodyType = BodyType.None;

        /// <summary>An example request body, or empty. Never a real credential or customer record.</summary>
        public string RequestBodyExample;

        /// <summary>Tags carried over from the source document.</summary>
        public List<string> Tags = new List<string>();

        /// <summary>Path parameters.</summary>
        public List<Parameter> PathParameters = new List<Parameter>();

        /// <summary>Query parameters.</summary>
        public List<Parameter> QueryParameters = new List<Parameter>();

        /// <summary>Header parameters.</summary>
        public List<Parameter> HeaderParameters = new List<Parameter>();

        /// <summary>How dangerous the operation is, as the importer inferred it.</summary>
        public NetworkMutationClass MutationClass = NetworkMutationClass.Safe;

        /// <summary>The source document's operation ID. The identity a re-import matches on.</summary>
        public string SourceReference;

        /// <summary>Content hash of the source operation, so a re-import can diff rather than overwrite.</summary>
        public string SourceHash;
    }
}
